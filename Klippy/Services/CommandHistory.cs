using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klippy.Services;

/// <summary>
/// The lines typed into the search box that actually did something — copied a snippet or
/// ran one — newest first, capped and kept between runs. The search box doubles as a
/// command line (a quick-code and the arguments after it), and a command line without a
/// history is one you retype.
///
/// Stored as a plain JSON array of strings, so the file is readable and editable, and a
/// line is kept exactly as it was typed: "slf " is an invocation of <c>slf</c> with
/// nothing after it yet, and trimming the space would recall something subtly different
/// from what was run.
///
/// Writes go straight through, like <see cref="SnippetStore"/> and unlike
/// <see cref="ClipHistoryStore"/>: a command is recorded when the user presses Enter,
/// not on every copy made anywhere on the system, so there is nothing to batch. A write
/// that fails is swallowed — recording is a side effect of a copy, and a copy that has
/// already happened must not report a failure because the MRU could not be saved.
/// </summary>
public sealed class CommandHistory
{
    public const int DefaultCapacity = 100;

    private readonly string? _filePath;
    private readonly List<string> _commands = new(); // newest first
    private int _capacity;

    /// <summary>Remembered command lines, most recently used first.</summary>
    public IReadOnlyList<string> Commands => _commands;

    public int Count => _commands.Count;

    /// <summary>Where this store persists, or null when it is memory-only.</summary>
    public string? FilePath => _filePath;

    /// <summary>
    /// Whether anything is recorded at all. A capacity of zero turns the MRU off, which
    /// is the way to decline a written record of what you have been typing.
    /// </summary>
    public bool IsEnabled => _capacity > 0;

    /// <summary>
    /// How many commands to keep. Lowering it drops the oldest immediately — and writes
    /// that through — so the cap is never merely aspirational.
    /// </summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            _capacity = Math.Max(0, value);
            if (Evict()) Save();
        }
    }

    public CommandHistory(string? filePath = null, int capacity = DefaultCapacity)
        : this(filePath ?? StorageLocations.CommandsPath, capacity, persist: true) { }

    /// <summary>A history that is never read from or written to disk. For tests.</summary>
    public static CommandHistory InMemory(int capacity = DefaultCapacity) =>
        new(null, capacity, persist: false);

    private CommandHistory(string? filePath, int capacity, bool persist)
    {
        _filePath = persist ? filePath : null;
        _capacity = Math.Max(0, capacity);
        Load();
        // A capacity lowered between runs — to zero, which means "forget these" — has to
        // bite on startup rather than waiting for the next command.
        if (Evict()) Save();
    }

    /// <summary>
    /// Remembers <paramref name="line"/> as the newest command. Returns whether anything
    /// was recorded.
    ///
    /// A line already in the list moves back to the top rather than being duplicated:
    /// the same command run twice is one command used twice, which is what "most
    /// recently used" means. The comparison is exact — <c>? Cats</c> and <c>? cats</c>
    /// search for different things, so they are different commands.
    /// </summary>
    public bool Record(string? line)
    {
        if (_capacity <= 0 || string.IsNullOrWhiteSpace(line)) return false;

        int existing = _commands.FindIndex(c => string.Equals(c, line, StringComparison.Ordinal));
        if (existing >= 0) _commands.RemoveAt(existing);

        _commands.Insert(0, line);
        Evict();
        Save();
        return true;
    }

    /// <summary>
    /// The commands <paramref name="typed"/> could still become, newest first. An empty
    /// string matches everything, which is what opening the MRU on an empty search box
    /// asks for.
    ///
    /// Matching is a case-insensitive match on the start of the line, not the
    /// word-prefix search the snippet list uses: this completes a command you are part
    /// way through typing, and anything looser would leave the list open — holding on to
    /// the arrow keys — over ordinary searches that were never commands. A line already
    /// typed in full is left out, since there is nothing left to offer.
    /// </summary>
    public List<string> Match(string? typed)
    {
        var prefix = typed ?? "";
        var matches = new List<string>();

        foreach (var command in _commands)
        {
            if (prefix.Length > 0 &&
                (command.Length == prefix.Length ||
                 !command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            matches.Add(command);
        }
        return matches;
    }

    /// <summary>Drops the oldest commands until the cap is met. Returns whether anything went.</summary>
    private bool Evict()
    {
        if (_commands.Count <= _capacity) return false;
        _commands.RemoveRange(_capacity, _commands.Count - _capacity);
        return true;
    }

    private void Load()
    {
        try
        {
            if (_filePath is null || !File.Exists(_filePath)) return;

            using var stream = File.OpenRead(_filePath);
            var loaded = JsonSerializer.Deserialize(stream, CommandHistoryJsonContext.Default.ListString);
            if (loaded is null) return;

            foreach (var line in loaded)
            {
                // A hand-edited file may carry nulls, blanks or repeats; none of them is
                // a command anyone typed.
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (_commands.Exists(c => string.Equals(c, line, StringComparison.Ordinal))) continue;
                _commands.Add(line);
            }
        }
        catch (Exception)
        {
            // Unreadable history is not worth crashing over, and what it costs is a
            // convenience. The file stands until the next command replaces it.
            _commands.Clear();
        }
    }

    private void Save()
    {
        if (_filePath is null) return;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _filePath + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, _commands, CommandHistoryJsonContext.Default.ListString);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // See the class remarks: the copy this came from has already happened.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
public sealed partial class CommandHistoryJsonContext : JsonSerializerContext;
