using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>
/// Keeps the snippets as a folder of JSON files, one per snippet — <c>&lt;id&gt;.json</c> in
/// <see cref="FolderPath"/> — and holds them all in memory (snippets are tiny).
///
/// One rule shapes everything here: <b>no two machines ever rewrite the same file as part of normal
/// use.</b> The folder is a thing people point at a synced drive, and the store used to be one file that
/// every change — every copy, even — rewrote whole from memory. Two machines running Klippy against it
/// then each wrote their own list over the other's; the sync client saw the edits overlap and kept one
/// side as a conflict copy nobody opened, and whatever only the other machine had went with it. So:
/// <list type="bullet">
/// <item>An edit writes only that snippet's file, so snippets added on two machines are two different
/// files and cannot collide. A delete deletes the file, which the sync client passes on.</item>
/// <item>Copying or running a snippet writes nothing here at all: when it was last used is a fact about
/// this machine, and lives in <see cref="SnippetUsage"/> beside settings.json.</item>
/// <item>Each save stamps <see cref="Snippet.ModifiedAt"/> and <see cref="Snippet.ModifiedBy"/>, so that
/// when the same snippet <em>was</em> edited on two machines before they synced, and the sync client kept
/// both, the later version is kept and the other moved to <see cref="ConflictsFolderPath"/> — see
/// <see cref="Rescan"/>.</item>
/// <item>A file that cannot be read is never taken for an empty or a deleted one. It is skipped, said so,
/// and read again on the next look; whatever was held for it stays. Nothing is ever saved as a result of
/// reading, so a failed read can cost nothing that is on disk.</item>
/// </list>
///
/// Each write goes to a temp file first and is swapped in (<see cref="AtomicFile"/>), so a crash cannot
/// leave half a snippet. Serialization is source-generated — no reflection, safe under NativeAOT.
///
/// The snippets are kept in creation order — <see cref="Snippet.CreatedAt"/>, then <see cref="Snippet.Id"/>
/// — which is the order a clash between two snippets' keys is settled in and the order a quick-code
/// answered by two snippets is looked up in. With one file per snippet there is no file order any more, so
/// the order has to be one every machine can work out for itself.
/// </summary>
public sealed class SnippetStore
{
    private string _folder;
    private readonly SnippetUsage _usage;
    private readonly string _machine = MachineName();

    /// <summary>Every snippet, in creation order.</summary>
    private readonly List<Snippet> _snippets = new();

    private readonly List<SnippetSearch.Entry<Snippet>> _entries = new();

    /// <summary>
    /// What each readable file in the folder held when it was last read, by file name. Usually one per
    /// snippet; more than one for an id is a sync conflict waiting to be settled. A file that has become
    /// unreadable keeps its entry, which is what keeps its snippet on the list while it cannot be read.
    /// </summary>
    private readonly Dictionary<string, LoadedFile> _files = new(StringComparer.Ordinal);

    /// <summary>
    /// Files that could not be read, and how each looked when that was said — so one that stays
    /// unreadable is reported once rather than on every look, and again only once it changes.
    /// </summary>
    private readonly Dictionary<string, FileStamp> _unreadable = new(StringComparer.Ordinal);

    /// <summary>What decides whether a file needs reading again: its time and its size.</summary>
    private readonly record struct FileStamp(DateTime LastWriteUtc, long Length);

    private sealed record LoadedFile(FileStamp Stamp, Snippet Snippet);

    public IReadOnlyList<SnippetSearch.Entry<Snippet>> Entries => _entries;
    public int Count => _snippets.Count;

    /// <summary>
    /// Every snippet, in creation order — <see cref="Snippet.CreatedAt"/>, then <see cref="Snippet.Id"/>:
    /// the order a clash between two shortcuts or two hotkeys is settled in, the first of them keeping it.
    /// </summary>
    public IReadOnlyList<Snippet> Snippets => _snippets;

    /// <summary>The snippet with <paramref name="id"/>, or null.</summary>
    public Snippet? Find(Guid id) => _snippets.Find(s => s.Id == id);

    /// <summary>The folder this store is currently reading and writing: one <c>&lt;id&gt;.json</c> per snippet.</summary>
    public string FolderPath => _folder;

    /// <summary>Where the losing side of a sync conflict is put: <c>conflicts</c>, beside <see cref="FolderPath"/>.</summary>
    public string ConflictsFolderPath => StorageLocations.ConflictsFolderFor(_folder);

    /// <summary>
    /// The files in the folder that could not be read on the last look — locked, cloud-only and not yet
    /// downloaded, or not a snippet at all. Each is read again on the next look.
    /// </summary>
    public IReadOnlyCollection<string> UnreadableFiles => _unreadable.Keys;

    /// <summary>
    /// False when the store sits in the platform's backup-exempt directory, i.e. the
    /// snippets are wiped by an uninstall rather than restored from a backup.
    /// </summary>
    public bool IsIncludedInBackup =>
        StorageLocations.BackupExemptFolder is not { } exempt || !SamePath(_folder, exempt);

    /// <param name="folder">
    /// The snippets folder. Defaults to <see cref="StorageLocations.ActiveFolder"/>, which is what the app
    /// opens; a test hands in a folder of its own.
    /// </param>
    /// <param name="seedIfEmpty">Write the examples on a first run — when there is neither a folder nor an older file to carry forward.</param>
    /// <param name="usage">
    /// When each snippet was last used here. Defaults to the file in the app-data folder for the app's own
    /// store, and to one held in memory for a folder handed in — so a test never writes to the developer's
    /// own usage file by accident.
    /// </param>
    public SnippetStore(string? folder = null, bool seedIfEmpty = true, SnippetUsage? usage = null)
    {
        _folder = folder ?? StorageLocations.ActiveFolder;
        _usage = usage ?? (folder is null ? new SnippetUsage() : SnippetUsage.InMemory());

        var legacy = StorageLocations.LegacyFileFor(_folder);
        var folderExisted = Directory.Exists(_folder);
        var legacyExisted = File.Exists(legacy);

        if (legacyExisted) CarryForward(legacy);
        Rescan();
        // Built from what is held whatever the look found: what was just carried forward is already
        // known, so the look itself has nothing new to say about it.
        RebuildFromFiles();

        // A folder that is there but empty is a store whose snippets were all deleted, and one that is
        // there but unreadable is a store that cannot be read yet: neither is a first run.
        if (_snippets.Count == 0 && seedIfEmpty && !folderExisted && !legacyExisted)
        {
            foreach (var seed in SeedSnippets())
            {
                Stamp(seed);
                Write(seed);
                Upsert(seed);
            }
            Sort();
            RebuildEntries();
        }
    }

    /// <summary>
    /// Moves the store between the backed-up and backup-exempt directories. Writes the
    /// new copy before deleting the old one, so an interruption cannot lose data.
    /// No-op where the platform has no backup-exempt location.
    /// </summary>
    public void SetBackupParticipation(bool include)
    {
        var target = include ? StorageLocations.BackedUpFolder : StorageLocations.BackupExemptFolder;
        if (target is null || SamePath(target, _folder))
            return;

        var previous = _folder;
        var previousFiles = _files.Keys.ToList();

        _folder = target;
        _files.Clear();
        _unreadable.Clear();
        foreach (var snippet in _snippets) Write(snippet); // a move, not an edit: nothing is stamped

        // Then the old copy — only the files this store read, so nothing it could not read goes with it —
        // and the folder, if that leaves it empty.
        foreach (var name in previousFiles)
            TryDelete(Path.Combine(previous, name));
        try
        {
            Directory.Delete(previous);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not empty, or not ours to delete: the new copy is already written, so data is safe either way.
        }

        // A set carried forward from an older Klippy left its old file beside the folder. That is a full
        // copy of the snippets, so it goes where they go: a "wipe on uninstall" that left it in the
        // backed-up folder would wipe nothing.
        MoveCarriedForwardCopies(previous, target);
    }

    public void Add(Snippet snippet)
    {
        Stamp(snippet);
        Write(snippet);
        Upsert(snippet);
        Sort();
        RebuildEntries();
    }

    /// <summary>
    /// Call after mutating a snippet's fields to persist and re-index it. Writes that snippet's file and
    /// nothing else.
    ///
    /// Saves the snippet it is given even when the store has read another version of it since — the editor
    /// was open while the same snippet was edited on another machine — or none at all, because it was
    /// deleted there: an edit someone has just made and saved is the one version that must not be lost. Nor
    /// is the other one: the version being written over is copied to <see cref="ConflictsFolderPath"/>
    /// first, and the return value says so.
    /// </summary>
    /// <returns>Whether a version saved elsewhere was set aside to make way for this one.</returns>
    public bool Update(Snippet snippet)
    {
        var setAside = Find(snippet.Id) is { } held && !ReferenceEquals(held, snippet) &&
                       !SameContent(held, snippet) && TrySetAside(snippet.Id);

        Stamp(snippet);
        Write(snippet);
        Upsert(snippet);
        Sort();
        RebuildEntries();
        return setAside;
    }

    /// <summary>Copies the snippet's own file into the conflicts folder. False when there was nothing to copy, or it would not copy.</summary>
    private bool TrySetAside(Guid id)
    {
        var name = OwnFileName(id);
        var path = Path.Combine(_folder, name);
        try
        {
            if (!File.Exists(path)) return false;
            File.Copy(path, FreePathIn(ConflictsFolderPath, name));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report($"Could not keep a copy of {path} before saving over it ({e.Message}).");
            return false;
        }
    }

    /// <summary>
    /// Deletes the snippet's file — and any other version of it the folder still holds, since those would
    /// otherwise bring it back on the next look.
    /// </summary>
    public void Delete(Guid id)
    {
        foreach (var name in FilesOf(id))
        {
            AtomicFile.Delete(Path.Combine(_folder, name));
            _files.Remove(name);
        }

        _snippets.RemoveAll(s => s.Id == id);
        _usage.Forget(id);
        RebuildEntries();
    }

    /// <summary>
    /// Bumps recency so copied snippets rank first. Written to <see cref="SnippetUsage"/> on this machine
    /// and never to the snippets folder: a copy is the commonest thing Klippy does, and the shared folder
    /// changing on every one of them is how two machines came to be writing over each other. The word
    /// index is unaffected.
    /// </summary>
    public void MarkUsed(Snippet snippet)
    {
        var now = DateTimeOffset.UtcNow;
        snippet.LastUsedAt = now;
        if (Find(snippet.Id) is { } held) held.LastUsedAt = now;
        _usage.Record(snippet.Id, now);
    }

    public List<SnippetSearch.Entry<Snippet>> Search(string? query) => SnippetSearch.Search(_entries, query);

    /// <summary>
    /// The snippet whose quick-code is exactly <paramref name="code"/>, or null — the first in creation
    /// order, should two answer to it. Exact rather than prefix, because this is what turns "code
    /// argument…" into an invocation: a prefix would hijack every ordinary two-word search whose first word
    /// happens to start with somebody's quick-code.
    /// </summary>
    public Snippet? FindByQuickCode(string? code) =>
        string.IsNullOrEmpty(code)
            ? null
            : _snippets.Find(s => s.QuickCode.Length > 0 &&
                                  string.Equals(s.QuickCode, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes snippets to <paramref name="destination"/> as one JSON list, in creation order — the format
    /// the store itself was until it became a folder, so an export is also a valid backup, and one an older
    /// Klippy can still import. Returns the number written.
    /// </summary>
    public int Export(Stream destination, string? tag = null)
    {
        var list = tag is null
            ? _snippets
            : _snippets.FindAll(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
        JsonSerializer.Serialize(destination, list, KlippyJsonContext.Default.ListSnippet);
        return list.Count;
    }

    /// <summary>Parses an exported file; null if it isn't valid Klippy JSON.</summary>
    public static List<Snippet>? TryParseSnippets(Stream source)
    {
        try
        {
            return JsonSerializer.Deserialize(source, KlippyJsonContext.Default.ListSnippet);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges snippets into the store: same id replaces the existing snippet, new ids are
    /// added — existing data is never wiped. Optionally only takes snippets carrying
    /// <paramref name="tag"/>. Returns how many were added and updated.
    ///
    /// Each one taken is saved as an edit made here and now, since that is what importing it is: it is
    /// what the person asked to have, and it has to win over whatever older version another machine still
    /// holds. When it was last used is kept only where this machine has no use of its own to go by.
    /// </summary>
    public (int Added, int Updated) Merge(IEnumerable<Snippet> incoming, string? tag = null)
    {
        int added = 0, updated = 0;
        foreach (var snippet in incoming)
        {
            Normalize(snippet);
            if (tag is not null && !string.Equals(snippet.Tag, tag, StringComparison.OrdinalIgnoreCase))
                continue;
            if (snippet.Label.Length == 0 && snippet.Content.Length == 0)
                continue;

            var existing = Find(snippet.Id);
            if (existing is null && FindByExternalIdentity(snippet) is { } same)
            {
                // Matched on external identity, so keep Klippy's own id stable rather
                // than letting it churn on every re-import of the same external record.
                snippet.Id = same.Id;
                existing = same;
            }

            snippet.LastUsedAt = _usage.LastUsed(snippet.Id) ?? snippet.LastUsedAt;
            _usage.Seed(snippet.Id, snippet.LastUsedAt);

            Stamp(snippet);
            Write(snippet);
            Upsert(snippet);
            if (existing is not null) updated++;
            else added++;
        }

        if (added + updated > 0)
        {
            _usage.Save();
            Sort();
            RebuildEntries();
        }
        return (added, updated);
    }

    /// <summary>
    /// Locates an existing snippet carrying the same (Source, ExternalId) pair, so a
    /// second import of the same external export updates rather than duplicates.
    /// Null when the incoming snippet has no external identity.
    /// </summary>
    private Snippet? FindByExternalIdentity(Snippet incoming)
    {
        if (incoming.Source.Length == 0 || incoming.ExternalId.Length == 0) return null;

        return _snippets.Find(s =>
            s.ExternalId.Length > 0 &&
            string.Equals(s.ExternalId, incoming.ExternalId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.Source, incoming.Source, StringComparison.OrdinalIgnoreCase));
    }

    // Hand-edited or foreign JSON may carry nulls or a missing id.
    private static void Normalize(Snippet s)
    {
        s.Label = s.Label?.Trim() ?? "";
        s.Content = s.Content ?? "";
        s.Tag = s.Tag?.Trim().ToLowerInvariant() ?? "";
        s.QuickCode = s.QuickCode?.Trim().ToLowerInvariant() ?? "";
        s.Source = s.Source?.Trim() ?? "";
        s.ExternalId = s.ExternalId?.Trim() ?? "";
        s.Shortcut = s.Shortcut?.Trim() ?? "";
        s.Hotkey = s.Hotkey?.Trim() ?? "";
        s.ModifiedBy = s.ModifiedBy?.Trim() ?? "";
        if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
    }

    public IEnumerable<string> Tags()
    {
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _snippets)
            if (s.Tag.Length > 0)
                seen.Add(s.Tag);
        return seen;
    }

    // ---- reading the folder ----

    /// <summary>
    /// Looks at the folder again and brings the store up to date with it: files that are new or have
    /// changed since they were last read are read, files that have gone take their snippet with them, and
    /// two versions of one snippet are settled. Cheap when nothing has changed — one listing, and a time
    /// and a size per file — so it is fine to call whenever the window is summoned, and is what a folder
    /// watcher calls once the folder has settled. Says what changed, so the list can refresh only what it
    /// has to.
    ///
    /// What it never does is take a failure for an answer. A folder that cannot be listed — gone, or on a
    /// drive that is not there right now — leaves everything as it was. A file that cannot be read is
    /// skipped and tried again next time, and whatever was held for it stays: a cloud-only file that will
    /// not open is not a deleted one.
    ///
    /// A sync client that sees the same file edited on two machines before they synced keeps both — this
    /// one as <c>&lt;id&gt; (# Edit conflict … #).json</c>, that one as <c>&lt;id&gt; (conflicted copy).json</c>,
    /// a third as <c>&lt;id&gt;-MACHINE.json</c>. They are recognised by what they hold rather than by what
    /// they are called, which is the one thing every client agrees on: any file whose snippet has the same
    /// id as another's is a version of it. The one with the later <see cref="Snippet.ModifiedAt"/> becomes
    /// <c>&lt;id&gt;.json</c> and the others go to <see cref="ConflictsFolderPath"/> — or simply go, when
    /// they say nothing the one kept does not, which is what two machines carrying the same snippets forward
    /// at once leave behind. A version is only ever moved over the snippet's own file once that file has
    /// been read, so nothing unread is written over.
    /// </summary>
    public SnippetChanges Rescan()
    {
        if (!TryListFolder(out var present)) return SnippetChanges.None;

        var before = _snippets.ToDictionary(s => s.Id);
        var looked = false;

        foreach (var name in _files.Keys.ToList())
            if (!present.ContainsKey(name))
            {
                _files.Remove(name);
                looked = true;
            }
        foreach (var name in _unreadable.Keys.ToList())
            if (!present.ContainsKey(name))
                _unreadable.Remove(name);

        foreach (var (name, stamp) in present)
        {
            _files.TryGetValue(name, out var known);
            if (known is not null && known.Stamp == stamp) continue;

            if (!TryRead(name, out var snippet, out var why))
            {
                // Kept as it was, if it was anything, and read again next time — the stamp it is held
                // under is the old one, so it still looks changed.
                if (!_unreadable.TryGetValue(name, out var said) || said != stamp)
                    Report($"Could not read {Path.Combine(_folder, name)} ({why}). " +
                           "Keeping what was there, and trying again on the next look.");
                _unreadable[name] = stamp;
                continue;
            }

            _unreadable.Remove(name);
            // The same snippet as already held keeps the instance the list is showing, and its recency.
            if (known is not null && SameContent(known.Snippet, snippet)) snippet = known.Snippet;
            _files[name] = new LoadedFile(stamp, snippet);
            looked = true;
        }

        var resolved = ResolveConflicts(present);
        if (!looked && resolved == 0) return SnippetChanges.None;

        RebuildFromFiles();

        var changed = new HashSet<Guid>();
        foreach (var snippet in _snippets)
            if (!before.Remove(snippet.Id, out var was) || !ReferenceEquals(was, snippet))
                changed.Add(snippet.Id);
        changed.UnionWith(before.Keys); // gone

        return changed.Count == 0 && resolved == 0
            ? SnippetChanges.None
            : new SnippetChanges(changed, resolved);
    }

    /// <summary>
    /// The snippet files in the folder, with their stamps. False when the folder is not there or cannot be
    /// listed, which <see cref="Rescan"/> takes as "nothing known" rather than as "nothing there".
    /// </summary>
    private bool TryListFolder(out Dictionary<string, FileStamp> present)
    {
        present = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        try
        {
            var folder = new DirectoryInfo(_folder);
            if (!folder.Exists) return false;

            foreach (var file in folder.EnumerateFiles())
                if (IsSnippetFileName(file.Name))
                    present[file.Name] = new FileStamp(file.LastWriteTimeUtc, file.Length);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Report($"Could not look in {_folder} ({e.Message}). Keeping the snippets already loaded.");
            return false;
        }
    }

    /// <summary>
    /// Whether a name in the folder could be a snippet: a <c>.json</c>, and not one of the hidden or
    /// half-written files editors and sync clients leave about — macOS's <c>._name</c>, Syncthing's
    /// <c>.syncthing.name.tmp</c>, Office's <c>~$name</c>. Klippy's own half-written files end in
    /// <c>.tmp</c>, so they never qualify either.
    /// </summary>
    private static bool IsSnippetFileName(string name) =>
        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith('.') && !name.StartsWith('~');

    private bool TryRead(string name, out Snippet snippet, out string why)
    {
        snippet = null!;
        why = "";
        try
        {
            // Shared for writing and deleting too: a sync client part way through writing the file is a
            // reason to read it again later, not a reason to fail to open it — what it half-holds will not
            // parse, and is retried.
            using var stream = new FileStream(
                Path.Combine(_folder, name), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (JsonSerializer.Deserialize(stream, SnippetFile) is not { } read)
            {
                why = "it holds no snippet";
                return false;
            }

            // A hand-written file may leave the id out; its name is the next best thing, but a file that
            // has neither cannot be told apart from the snippet it would duplicate on every look.
            if (read.Id == Guid.Empty)
            {
                if (!TryParseOwnName(name, out var id))
                {
                    why = "it has no id";
                    return false;
                }
                read.Id = id;
            }

            Normalize(read);
            read.LastUsedAt = _usage.LastUsed(read.Id) ?? read.CreatedAt;
            snippet = read;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException
                                    or JsonException or NotSupportedException)
        {
            why = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Settles every snippet the folder holds more than one version of, as <see cref="Rescan"/> describes.
    /// Returns how many were settled. One that cannot be — a file will not move, or the snippet's own file
    /// is there but cannot be read yet — is left for the next look.
    /// </summary>
    private int ResolveConflicts(Dictionary<string, FileStamp> present)
    {
        var resolved = 0;
        foreach (var versions in _files.GroupBy(f => f.Value.Snippet.Id).ToList())
        {
            var ownName = OwnFileName(versions.Key);
            var copies = versions.Where(f => !IsOwnName(f.Key, versions.Key)).ToList();
            if (copies.Count == 0) continue;

            var own = versions.FirstOrDefault(f => IsOwnName(f.Key, versions.Key));
            var ownIsUnread = own.Key is null && present.Keys.Any(n => IsOwnName(n, versions.Key));
            if (ownIsUnread) continue;

            // Later edit first; on a tie, the file that is already the snippet's own, so as little as
            // possible moves; then by name, so every machine looking at the same files picks the same one.
            var winner = versions
                .OrderByDescending(f => f.Value.Snippet.ModifiedAtOrCreated)
                .ThenBy(f => IsOwnName(f.Key, versions.Key) ? 0 : 1)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .First();

            try
            {
                var ownPath = Path.Combine(_folder, own.Key ?? ownName);
                if (!IsOwnName(winner.Key, versions.Key))
                {
                    // The version being replaced is kept first, so there is no moment at which it is nowhere.
                    if (own.Key is not null) File.Copy(ownPath, FreePathIn(ConflictsFolderPath, own.Key));
                    File.Move(Path.Combine(_folder, winner.Key), ownPath, overwrite: true);
                    _files.Remove(winner.Key);
                    Remember(Path.GetFileName(ownPath), winner.Value.Snippet);
                }

                foreach (var (name, file) in copies)
                {
                    if (name == winner.Key) continue;

                    var path = Path.Combine(_folder, name);
                    if (SameContent(file.Snippet, winner.Value.Snippet)) AtomicFile.Delete(path);
                    else File.Move(path, FreePathIn(ConflictsFolderPath, name));
                    _files.Remove(name);
                }
                resolved++;

                // Said here as well as by the list: one settled while the store is opening is settled before
                // there is a window to say so in.
                var kept = winner.Value.Snippet;
                Report($"“{kept.Label}” was edited on two machines. Kept the version saved " +
                       $"{kept.ModifiedAtOrCreated:u}{(kept.ModifiedBy.Length > 0 ? " on " + kept.ModifiedBy : "")}; " +
                       $"any other is in {ConflictsFolderPath}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report($"Could not settle the two versions of “{winner.Value.Snippet.Label}” in " +
                       $"{_folder} ({e.Message}). Trying again on the next look.");
            }
        }
        return resolved;
    }

    /// <summary>
    /// <paramref name="name"/> in <paramref name="folder"/>, or <c>name (2).json</c> and on when that is
    /// taken: nothing that goes into the conflicts folder ever goes over something already there.
    /// </summary>
    private static string FreePathIn(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }

    /// <summary>
    /// The snippets as the files say: one per id — its own file where that was read, the latest version
    /// otherwise — in creation order.
    /// </summary>
    private void RebuildFromFiles()
    {
        var byId = new Dictionary<Guid, (bool Own, Snippet Snippet)>();
        foreach (var (name, file) in _files)
        {
            var id = file.Snippet.Id;
            var own = IsOwnName(name, id);
            if (!byId.TryGetValue(id, out var held) ||
                (own && !held.Own) ||
                (own == held.Own && file.Snippet.ModifiedAtOrCreated > held.Snippet.ModifiedAtOrCreated))
                byId[id] = (own, file.Snippet);
        }

        _snippets.Clear();
        foreach (var (_, snippet) in byId.Values) _snippets.Add(snippet);
        Sort();
        RebuildEntries();
    }

    // ---- writing ----

    /// <summary>Writes the snippet's own file, and notes what it now looks like so the next look does not read it back.</summary>
    private void Write(Snippet snippet)
    {
        Directory.CreateDirectory(_folder);

        var name = OwnFileName(snippet.Id);
        var path = Path.Combine(_folder, name);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, snippet, SnippetFile);
        AtomicFile.Replace(tmp, path);

        Remember(name, snippet);
    }

    /// <summary>
    /// Notes that <paramref name="name"/> holds <paramref name="snippet"/> as of now. A file that will not
    /// even stat is noted all the same, under a stamp it cannot have, so the next look reads it back.
    /// </summary>
    private void Remember(string name, Snippet snippet)
    {
        FileStamp stamp = default;
        try
        {
            var info = new FileInfo(Path.Combine(_folder, name));
            stamp = new FileStamp(info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        _files[name] = new LoadedFile(stamp, snippet);
        _unreadable.Remove(name);
    }

    private void Stamp(Snippet snippet)
    {
        snippet.ModifiedAt = DateTimeOffset.UtcNow;
        snippet.ModifiedBy = _machine;
    }

    /// <summary>Puts <paramref name="snippet"/> in the list in place of whatever had its id. Leaves the order to <see cref="Sort"/>.</summary>
    private void Upsert(Snippet snippet)
    {
        var i = _snippets.FindIndex(s => s.Id == snippet.Id);
        if (i >= 0) _snippets[i] = snippet;
        else _snippets.Add(snippet);
    }

    private void Sort() => _snippets.Sort(static (a, b) =>
    {
        var byCreation = a.CreatedAt.CompareTo(b.CreatedAt);
        return byCreation != 0 ? byCreation : a.Id.CompareTo(b.Id);
    });

    private void RebuildEntries()
    {
        _entries.Clear();
        foreach (var s in _snippets)
            _entries.Add(new SnippetSearch.Entry<Snippet>(s));
    }

    /// <summary>Every file this store has read that holds a version of <paramref name="id"/>.</summary>
    private List<string> FilesOf(Guid id) =>
        _files.Where(f => f.Value.Snippet.Id == id).Select(f => f.Key).ToList();

    // ---- the single file this used to be ----

    /// <summary>
    /// Carries the snippets in <paramref name="legacy"/> — the one file the store was up to 1.0.33 — into
    /// the folder, each as its own file, and then renames it to <c>snippets.migrated.json</c>. Nothing is
    /// deleted. Their recency goes into this machine's <see cref="SnippetUsage"/>, since it no longer
    /// lives with them.
    ///
    /// Run whenever that file is there, not only when the folder is not: a Klippy that has not been
    /// upgraded yet, on another machine sharing the folder, goes on writing it, and the snippets it adds
    /// there should not be stranded in a file nothing reads. So a snippet whose file is already in the
    /// folder is never written over — that file is the newer one, and may hold an edit made since — which
    /// also makes carrying the same file forward twice harmless. Each snippet is written exactly as it was,
    /// its <see cref="Snippet.ModifiedAt"/> taken from when it was made where it has none: two machines
    /// carrying the same file forward at once write the same bytes, and the sync client has nothing to
    /// keep two of — or keeps two the same, which <see cref="Rescan"/> sees are the same.
    ///
    /// A file that cannot be read is left exactly where it is and tried again on the next start; until
    /// then nothing is seeded and nothing is written in its place.
    /// </summary>
    private void CarryForward(string legacy)
    {
        List<Snippet>? old;
        try
        {
            using var stream = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            old = JsonSerializer.Deserialize(stream, KlippyJsonContext.Default.ListSnippet);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException
                                    or JsonException or NotSupportedException)
        {
            Report($"Could not read {legacy} to carry its snippets into {_folder} ({e.Message}). " +
                   "It is left as it is, and tried again on the next start.");
            return;
        }

        var complete = true;
        var seeded = false;
        foreach (var snippet in old ?? [])
        {
            if (snippet is null) continue;
            Normalize(snippet);
            if (snippet.ModifiedAt == default) snippet.ModifiedAt = snippet.CreatedAt;

            try
            {
                var path = Path.Combine(_folder, OwnFileName(snippet.Id));
                if (!File.Exists(path)) Write(snippet);
                seeded |= _usage.Seed(snippet.Id, snippet.LastUsedAt);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                complete = false;
                Report($"Could not write “{snippet.Label}” into {_folder} ({e.Message}). " +
                       $"{legacy} is left where it is, and tried again on the next start.");
            }
        }
        if (seeded) _usage.Save();

        // Renamed only once every snippet in it has a file: one that was missed is still in the file to be
        // carried forward next time.
        if (!complete) return;
        try
        {
            File.Move(legacy, FreeCarriedForwardName(legacy));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report($"Carried the snippets in {legacy} into {_folder}, but could not rename it ({e.Message}).");
        }
    }

    /// <summary><c>snippets.migrated.json</c> beside <paramref name="legacy"/>, or <c>snippets.migrated.2.json</c> and on when that is taken.</summary>
    private static string FreeCarriedForwardName(string legacy)
    {
        var stem = Path.Combine(Path.GetDirectoryName(legacy) ?? "", Path.GetFileNameWithoutExtension(legacy));
        var path = stem + ".migrated.json";
        for (var n = 2; File.Exists(path); n++)
            path = $"{stem}.migrated.{n}.json";
        return path;
    }

    /// <summary>The <c>.migrated</c> copies beside <paramref name="from"/>, moved beside <paramref name="to"/>.</summary>
    private static void MoveCarriedForwardCopies(string from, string to)
    {
        try
        {
            var fromParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(from));
            var toParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(to));
            if (string.IsNullOrEmpty(fromParent) || string.IsNullOrEmpty(toParent) || !Directory.Exists(fromParent))
                return;

            var pattern = Path.GetFileName(Path.TrimEndingDirectorySeparator(from)) + ".migrated*.json";
            foreach (var copy in Directory.GetFiles(fromParent, pattern))
            {
                var destination = Path.Combine(toParent, Path.GetFileName(copy));
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(toParent);
                File.Move(copy, destination);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The snippets themselves have already moved; a leftover copy is one to move by hand.
        }
    }

    // ---- small things ----

    /// <summary>What a snippet's own file is called: its id, as every machine spells it.</summary>
    private static string OwnFileName(Guid id) => id.ToString("D") + ".json";

    private static bool IsOwnName(string name, Guid id) =>
        string.Equals(name, OwnFileName(id), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseOwnName(string name, out Guid id) =>
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "D", out id);

    /// <summary>Whether two versions say the same thing, as their files would — recency aside, which no file holds.</summary>
    private static bool SameContent(Snippet a, Snippet b) =>
        string.Equals(JsonSerializer.Serialize(a, SnippetFile), JsonSerializer.Serialize(b, SnippetFile),
            StringComparison.Ordinal);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string MachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// Where a file the store could not read, or a folder it could not list, is said. Standard error,
    /// as the desktop head's own startup problems are; there is nowhere better that every head has.
    /// </summary>
    private static void Report(string message) => Console.Error.WriteLine($"Klippy: {message}");

    /// <summary>
    /// How a snippet's own file is written: the snippet as exported, less <see cref="Snippet.LastUsedAt"/>.
    /// Recency changes on every copy and is about one machine, so it lives in <see cref="SnippetUsage"/>
    /// instead — a file that changed on every copy is a file two machines are forever writing over each
    /// other. Left out of reading too, so a hand-edited one cannot bring it back.
    /// </summary>
    private static readonly JsonTypeInfo<Snippet> SnippetFile = (JsonTypeInfo<Snippet>)new JsonSerializerOptions
    {
        WriteIndented = true,
        TypeInfoResolver = KlippyJsonContext.Default.WithAddedModifier(static info =>
        {
            if (info.Type != typeof(Snippet)) return;
            for (var i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name == nameof(Snippet.LastUsedAt))
                    info.Properties.RemoveAt(i);
        }),
    }.GetTypeInfo(typeof(Snippet));

    private static IEnumerable<Snippet> SeedSnippets()
    {
        // First-run examples so the app is immediately useful and demonstrable.
        var t = DateTimeOffset.UtcNow;
        Snippet Make(string label, string content, string tag, string quickCode = "",
            bool isMarkdown = false, bool isExecutable = false) =>
            new()
            {
                Label = label, Content = content, Tag = tag, QuickCode = quickCode,
                IsMarkdown = isMarkdown, IsExecutable = isExecutable,
                CreatedAt = t, LastUsedAt = t = t.AddSeconds(-1),
            };

        yield return Make("Send log files",
            "Please send us the log files, as per the instructions here:\n\nhttps://www.example.com/docs/XXX",
            "work", "slf", isMarkdown: true);
        yield return Make("Work email", "sam.rivera@northwind.io", "work", "we");
        yield return Make("Home address", "Lindenstraße 24, 10969 Berlin", "personal");
        yield return Make("Meeting link — standup", "https://meet.example.com/j/882-441-veo", "work", "ms");
        // Demonstrates both halves of the Execute marker: a snippet that runs rather than
        // copies, and a %P% for the words typed after its quick-code — "? some words".
        yield return Make("Google search", "https://www.google.com/search?q=%P%", "web", "?",
            isExecutable: true);
        yield return Make("Canned reply — out of office",
            "Hi,\nI'm out of office until Monday, Aug 31 with limited email access.\nFor urgent matters contact ops@klippy.app.",
            "work", "ooo", isMarkdown: true);
        yield return Make("SSH public key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIF3k9examplekeydata sam@work", "dev");
        yield return Make("Docker prune", "docker system prune -af --volumes", "dev", "dp");
        yield return Make("IBAN — checking", "DE44 5001 0517 5407 3249 31", "banking");
        yield return Make("Support signature", "Best regards, Sam Rivera · Klippy Support", "work");
    }
}

/// <summary>
/// What a <see cref="SnippetStore.Rescan"/> found: the snippets that were added, changed or removed, by id,
/// and how many sync conflicts it settled.
/// </summary>
public sealed record SnippetChanges(IReadOnlyCollection<Guid> Changed, int ConflictsResolved)
{
    public static readonly SnippetChanges None = new(Array.Empty<Guid>(), 0);

    /// <summary>Whether there is anything for the list to show.</summary>
    public bool Any => Changed.Count > 0 || ConflictsResolved > 0;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Snippet>))]
[JsonSerializable(typeof(Snippet))]
public sealed partial class KlippyJsonContext : JsonSerializerContext;
