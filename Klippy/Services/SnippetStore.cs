using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>
/// Loads and saves snippets as a single JSON file in the platform's app-data
/// folder. Everything is held in memory (snippets are tiny); every mutation
/// rewrites the file atomically (temp file + move) so a crash can't corrupt it.
/// Serialization is source-generated — no reflection, safe under NativeAOT.
/// </summary>
public sealed class SnippetStore
{
    private string _filePath;
    private readonly List<Snippet> _snippets = new();
    private readonly List<SnippetSearch.Entry> _entries = new();

    public IReadOnlyList<SnippetSearch.Entry> Entries => _entries;
    public int Count => _snippets.Count;

    public static string DefaultFilePath => StorageLocations.ActivePath;

    /// <summary>Where this store is currently reading and writing.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// False when the store sits in the platform's backup-exempt directory, i.e. the
    /// snippets are wiped by an uninstall rather than restored from a backup.
    /// </summary>
    public bool IsIncludedInBackup =>
        StorageLocations.BackupExemptPath is not { } exempt ||
        !string.Equals(_filePath, exempt, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the store between the backed-up and backup-exempt directories. Writes the
    /// new copy before deleting the old one, so an interruption cannot lose data.
    /// No-op where the platform has no backup-exempt location.
    /// </summary>
    public void SetBackupParticipation(bool include)
    {
        var target = include ? StorageLocations.BackedUpPath : StorageLocations.BackupExemptPath;
        if (target is null || string.Equals(target, _filePath, StringComparison.OrdinalIgnoreCase))
            return;

        var previous = _filePath;
        _filePath = target;
        Save();

        try
        {
            if (File.Exists(previous)) File.Delete(previous);
        }
        catch (IOException)
        {
            // The new copy is already written, so data is safe either way.
        }
    }

    public SnippetStore(string? filePath = null, bool seedIfEmpty = true)
    {
        _filePath = filePath ?? DefaultFilePath;
        Load();
        if (_snippets.Count == 0 && seedIfEmpty && !File.Exists(_filePath))
        {
            _snippets.AddRange(SeedSnippets());
            Save();
        }
        RebuildEntries();
    }

    public void Add(Snippet snippet)
    {
        _snippets.Add(snippet);
        Save();
        RebuildEntries();
    }

    /// <summary>Call after mutating a snippet's fields to persist and re-index it.</summary>
    public void Update(Snippet snippet)
    {
        Save();
        int i = _entries.FindIndex(e => e.Snippet.Id == snippet.Id);
        if (i >= 0) _entries[i] = new SnippetSearch.Entry(snippet);
    }

    public void Delete(Guid id)
    {
        _snippets.RemoveAll(s => s.Id == id);
        Save();
        RebuildEntries();
    }

    /// <summary>Bumps recency so copied snippets rank first. Word index is unaffected.</summary>
    public void MarkUsed(Snippet snippet)
    {
        snippet.LastUsedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public List<SnippetSearch.Entry> Search(string? query) => SnippetSearch.Search(_entries, query);

    /// <summary>
    /// Writes snippets to <paramref name="destination"/> as JSON — the same shape as the
    /// store file, so an export is also a valid backup. Returns the number written.
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

            int i = _snippets.FindIndex(s => s.Id == snippet.Id);
            if (i >= 0)
            {
                _snippets[i] = snippet;
                updated++;
            }
            else
            {
                _snippets.Add(snippet);
                added++;
            }
        }

        if (added + updated > 0)
        {
            Save();
            RebuildEntries();
        }
        return (added, updated);
    }

    // Hand-edited or foreign JSON may carry nulls or a missing id.
    private static void Normalize(Snippet s)
    {
        s.Label = s.Label?.Trim() ?? "";
        s.Content = s.Content ?? "";
        s.Tag = s.Tag?.Trim().ToLowerInvariant() ?? "";
        s.QuickCode = s.QuickCode?.Trim().ToLowerInvariant() ?? "";
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

    private void RebuildEntries()
    {
        _entries.Clear();
        foreach (var s in _snippets)
            _entries.Add(new SnippetSearch.Entry(s));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize(stream, KlippyJsonContext.Default.ListSnippet);
            if (loaded is not null) _snippets.AddRange(loaded);
        }
        catch (Exception)
        {
            // Unreadable/corrupt file: start empty rather than crash; the file is
            // preserved until the next save.
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _filePath + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, _snippets, KlippyJsonContext.Default.ListSnippet);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static IEnumerable<Snippet> SeedSnippets()
    {
        // First-run examples so the app is immediately useful and demonstrable.
        var t = DateTimeOffset.UtcNow;
        Snippet Make(string label, string content, string tag, string quickCode = "", bool isMarkdown = false) =>
            new() { Label = label, Content = content, Tag = tag, QuickCode = quickCode, IsMarkdown = isMarkdown, CreatedAt = t, LastUsedAt = t = t.AddSeconds(-1) };

        yield return Make("Send log files",
            "Please send us the log files, as per the instructions here:\n\nhttps://www.shineforms.co.uk/docs/XXX",
            "work", "slf", isMarkdown: true);
        yield return Make("Work email", "sam.rivera@northwind.io", "work", "we");
        yield return Make("Home address", "Lindenstraße 24, 10969 Berlin", "personal");
        yield return Make("Meeting link — standup", "https://meet.example.com/j/882-441-veo", "work", "ms");
        yield return Make("Canned reply — out of office",
            "Hi,\nI'm out of office until Monday, Aug 31 with limited email access.\nFor urgent matters contact ops@klippy.app.",
            "work", "ooo", isMarkdown: true);
        yield return Make("SSH public key", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIF3k9examplekeydata sam@work", "dev");
        yield return Make("Docker prune", "docker system prune -af --volumes", "dev", "dp");
        yield return Make("IBAN — checking", "DE44 5001 0517 5407 3249 31", "banking");
        yield return Make("Support signature", "Best regards, Sam Rivera · Klippy Support", "work");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Snippet>))]
public sealed partial class KlippyJsonContext : JsonSerializerContext;
