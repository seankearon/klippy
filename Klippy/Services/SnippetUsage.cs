using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klippy.Services;

/// <summary>
/// When each snippet was last copied or run on this machine — the recency the list is ranked by — kept in
/// <see cref="StorageLocations.UsagePath"/>, beside settings.json, as a JSON object of id to time.
///
/// Apart from the snippets because it is the one thing about them that changes on every copy. Stored with
/// them, in the folder a sync client shares, every copy on either machine would rewrite a shared file, and
/// two machines rewriting the same file is exactly how a sync client ends up keeping one side as a conflict
/// copy and losing track of the other. Kept here, copying a snippet writes nothing to the shared folder at
/// all — and the ranking reflects what is used on this machine, which is arguably what it should have
/// meant all along.
///
/// Writes go straight through and a failure is swallowed, as <see cref="CommandHistory"/>'s are: recording
/// is a side effect of a copy that has already happened, and what a lost write costs is a ranking.
/// </summary>
public sealed class SnippetUsage
{
    private readonly string? _filePath;
    private readonly Dictionary<Guid, DateTimeOffset> _lastUsed = new();

    /// <summary>Where this record persists, or null when it is memory-only.</summary>
    public string? FilePath => _filePath;

    public int Count => _lastUsed.Count;

    public SnippetUsage(string? filePath = null) : this(filePath ?? StorageLocations.UsagePath, persist: true) { }

    /// <summary>A record that is never read from or written to disk. For tests, and for a store opened on a folder of its own.</summary>
    public static SnippetUsage InMemory() => new(null, persist: false);

    private SnippetUsage(string? filePath, bool persist)
    {
        _filePath = persist ? filePath : null;
        Load();
    }

    /// <summary>When <paramref name="id"/> was last used here, or null when it never has been.</summary>
    public DateTimeOffset? LastUsed(Guid id) => _lastUsed.TryGetValue(id, out var when) ? when : null;

    /// <summary>Notes a use of <paramref name="id"/> at <paramref name="when"/>, and writes it through.</summary>
    public void Record(Guid id, DateTimeOffset when)
    {
        _lastUsed[id] = when;
        Save();
    }

    /// <summary>
    /// Notes <paramref name="when"/> for <paramref name="id"/> only if nothing is known for it yet — the
    /// recency a migrated or imported snippet arrives with, which must not overrule what this machine
    /// already knows. Not written through, so a batch costs one write: call <see cref="Save"/> after.
    /// Returns whether anything was noted.
    /// </summary>
    public bool Seed(Guid id, DateTimeOffset when) => _lastUsed.TryAdd(id, when);

    /// <summary>Forgets <paramref name="id"/>, a snippet deleted here. Written through.</summary>
    public void Forget(Guid id)
    {
        if (_lastUsed.Remove(id)) Save();
    }

    public void Save()
    {
        if (_filePath is null) return;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _lastUsed, SnippetUsageJsonContext.Default.DictionaryGuidDateTimeOffset);
            AtomicFile.Replace(tmp, _filePath);
        }
        catch (Exception)
        {
            // See the class remarks: the copy this came from has already happened.
        }
    }

    private void Load()
    {
        try
        {
            if (_filePath is null || !File.Exists(_filePath)) return;

            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize(stream, SnippetUsageJsonContext.Default.DictionaryGuidDateTimeOffset);
            if (loaded is null) return;
            foreach (var (id, when) in loaded) _lastUsed[id] = when;
        }
        catch (Exception)
        {
            // Unreadable usage costs a ranking, which is not worth refusing to start over. Each snippet
            // falls back to when it was made until it is next used.
            _lastUsed.Clear();
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<Guid, DateTimeOffset>))]
public sealed partial class SnippetUsageJsonContext : JsonSerializerContext;
