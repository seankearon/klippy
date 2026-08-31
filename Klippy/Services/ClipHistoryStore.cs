using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>
/// The captured clipboard history: a capped, newest-first list of <see cref="ClipEntry"/>.
///
/// Kept apart from <see cref="SnippetStore"/> because the two have opposite write
/// profiles. Snippets change when the user says so, so writing the whole file per
/// mutation is free. History changes on *every* copy anywhere on the system, so writes
/// are deferred: a mutation only marks the store dirty and the host flushes on a timer
/// and on shutdown. The cost is that a hard crash loses the last few seconds of
/// history, which is the right trade for data that is itself transient.
///
/// Session-only mode (<see cref="InMemory"/>) never touches the disk at all — the
/// answer for anyone who does not want a plaintext record of what they copied.
/// </summary>
public sealed class ClipHistoryStore
{
    public const int DefaultCapacity = 500;

    private readonly string? _filePath;
    private readonly List<ClipEntry> _entries = new();                     // newest first
    private readonly List<SnippetSearch.Entry<ClipEntry>> _index = new();  // parallel to _entries
    private int _capacity;
    private bool _dirty;

    /// <summary>
    /// Raised after any change to the history. Capture happens while the window is open,
    /// so the list has to learn about a new clip from somewhere other than a user action.
    /// </summary>
    public event Action? Changed;

    /// <summary>Clips newest first.</summary>
    public IReadOnlyList<ClipEntry> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>True when there are unflushed changes. The host polls this to avoid pointless writes.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Where this store persists, or null in session-only mode.</summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// How many clips to keep. Lowering it evicts immediately, so the cap is never
    /// merely aspirational.
    /// </summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(1, value);
            if (Evict()) MarkChanged();
        }
    }

    public ClipHistoryStore(string? filePath = null, int capacity = DefaultCapacity)
        : this(filePath ?? StorageLocations.HistoryPath, capacity, persist: true) { }

    /// <summary>Session-only history: nothing is read from or written to disk.</summary>
    public static ClipHistoryStore InMemory(int capacity = DefaultCapacity) =>
        new(null, capacity, persist: false);

    private ClipHistoryStore(string? filePath, int capacity, bool persist)
    {
        _filePath = persist ? filePath : null;
        _capacity = Math.Max(1, capacity);
        Load();
    }

    /// <summary>
    /// Records a clip and returns the stored entry.
    ///
    /// An identical clip already in the history is moved back to the top rather than
    /// duplicated — apps routinely set the clipboard twice for one user copy, and
    /// re-copying something from an hour ago should promote it, not clone it. Pinned
    /// state survives the move.
    /// </summary>
    public ClipEntry Add(ClipEntry entry)
    {
        int existing = _entries.FindIndex(e =>
            string.Equals(e.Text, entry.Text, StringComparison.Ordinal) &&
            string.Equals(e.Html, entry.Html, StringComparison.Ordinal));

        if (existing >= 0)
        {
            var found = _entries[existing];
            found.CapturedAt = entry.CapturedAt;
            if (entry.SourceApp.Length > 0) found.SourceApp = entry.SourceApp;
            MoveToHead(existing);
            MarkChanged();
            return found;
        }

        _entries.Insert(0, entry);
        _index.Insert(0, new SnippetSearch.Entry<ClipEntry>(entry));
        Evict();
        MarkChanged();
        return entry;
    }

    /// <summary>
    /// Bumps an existing clip to the top, as a fresh capture of the same text would.
    ///
    /// Copying a clip out of the history is not a capture — Klippy's own writes are
    /// deliberately ignored — so re-ranking has to be asked for explicitly. Passing the
    /// clip back through <see cref="Add"/> would not do it: the caller holds the very
    /// instance the store holds, so refreshing the timestamp there assigns to itself.
    /// </summary>
    public bool MarkUsed(Guid id)
    {
        int i = _entries.FindIndex(e => e.Id == id);
        if (i < 0) return false;

        _entries[i].CapturedAt = DateTimeOffset.UtcNow;
        MoveToHead(i);
        MarkChanged();
        return true;
    }

    public bool SetPinned(Guid id, bool pinned)
    {
        int i = _entries.FindIndex(e => e.Id == id);
        if (i < 0 || _entries[i].IsPinned == pinned) return false;

        _entries[i].IsPinned = pinned;
        // Unpinning can push the store back over capacity, since pinned clips were
        // exempt from the cap while they held that state.
        if (!pinned) Evict();
        MarkChanged();
        return true;
    }

    public bool Remove(Guid id)
    {
        int i = _entries.FindIndex(e => e.Id == id);
        if (i < 0) return false;

        RemoveAt(i);
        MarkChanged();
        return true;
    }

    /// <summary>Empties the history. Pinned clips are kept unless <paramref name="includePinned"/>.</summary>
    public void Clear(bool includePinned = false)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (includePinned || !_entries[i].IsPinned)
                RemoveAt(i);
        MarkChanged();
    }

    public List<SnippetSearch.Entry<ClipEntry>> Search(string? query) => SnippetSearch.Search(_index, query);

    private void MarkChanged()
    {
        _dirty = true;
        Changed?.Invoke();
    }

    /// <summary>Writes pending changes to disk. No-op when clean or session-only.</summary>
    public void Flush()
    {
        if (!_dirty || _filePath is null) return;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _filePath + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, _entries, ClipHistoryJsonContext.Default.ListClipEntry);
        File.Move(tmp, _filePath, overwrite: true);
        _dirty = false;
    }

    /// <summary>
    /// Drops the oldest unpinned clips until the cap is met. Returns whether anything went.
    ///
    /// Two things are never evicted. Pinned clips, obviously — the user said to keep them.
    /// And the newest clip, index 0, which is subtler: a history full of pinned clips
    /// would otherwise evict each new capture the instant it arrived, so copying would
    /// appear to do nothing at all. In both cases the store sits over capacity, which is
    /// the honest outcome when everything in it has been asked to stay.
    /// </summary>
    private bool Evict()
    {
        bool removed = false;
        while (_entries.Count > _capacity)
        {
            int oldest = _entries.FindLastIndex(e => !e.IsPinned);
            if (oldest <= 0) break;
            RemoveAt(oldest);
            removed = true;
        }
        return removed;
    }

    // _index is kept in lockstep with _entries so a search never re-tokenizes the whole
    // history — which, at a keystroke's notice and 500 clips, would be felt.
    private void RemoveAt(int i)
    {
        _entries.RemoveAt(i);
        _index.RemoveAt(i);
    }

    private void MoveToHead(int i)
    {
        if (i == 0) return;
        var entry = _entries[i];
        var indexed = _index[i];
        _entries.RemoveAt(i);
        _index.RemoveAt(i);
        _entries.Insert(0, entry);
        _index.Insert(0, indexed);
    }

    private void Load()
    {
        try
        {
            if (_filePath is null || !File.Exists(_filePath)) return;
            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize(stream, ClipHistoryJsonContext.Default.ListClipEntry);
            if (loaded is null) return;

            foreach (var entry in loaded)
            {
                if (entry.Text.Length == 0) continue; // hand-edited or truncated file
                if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
                _entries.Add(entry);
                _index.Add(new SnippetSearch.Entry<ClipEntry>(entry));
            }

            SortNewestFirst();

            // A capacity lowered between runs has to bite on load, not just on the next copy.
            if (Evict()) _dirty = true;
        }
        catch (Exception)
        {
            // Unreadable history is not worth crashing over, and losing it costs nothing
            // that was not already transient. The file stands until the next flush.
            _entries.Clear();
            _index.Clear();
        }
    }

    // A hand-edited file may be in any order, and _index has to stay aligned with
    // _entries, so both are reordered by the same comparison rather than sorted apart.
    private void SortNewestFirst()
    {
        var order = new List<(ClipEntry Entry, SnippetSearch.Entry<ClipEntry> Indexed)>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
            order.Add((_entries[i], _index[i]));

        order.Sort(static (a, b) => b.Entry.CapturedAt.CompareTo(a.Entry.CapturedAt));

        _entries.Clear();
        _index.Clear();
        foreach (var (entry, indexed) in order)
        {
            _entries.Add(entry);
            _index.Add(indexed);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ClipEntry>))]
public sealed partial class ClipHistoryJsonContext : JsonSerializerContext;
