using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Klippy.Services;

/// <summary>
/// Local defines a snippet expands on copy, so one snippet can work on two machines:
/// <c>%ws% C:\src\shine</c> copies as the real WebStorm command line because this
/// machine's <c>klippy.vars</c> says what <c>ws</c> is.
///
/// The file is a plain list of <c>name=value</c> lines, hand-edited, and deliberately
/// *not* part of the snippet store: paths are the one thing that cannot be shared
/// between a Mac and a Windows box, so they belong beside the snippets rather than
/// inside them — and an export, which is a snippet file, never carries them.
///
/// <code>
/// # klippy.vars
/// ws=%localappdata%\Programs\WebStorm\bin\webstorm64.exe
/// src=D:\src
/// </code>
///
/// Only names the file defines are ever replaced. A percent sign in ordinary text —
/// "50% off", a SQL <c>LIKE '%foo%'</c> — is left exactly as it was written, which is
/// what makes turning the feature on safe for snippets that predate it.
/// </summary>
public sealed class KlippyVariables
{
    /// <summary>The file Klippy looks for beside the snippets unless settings name another.</summary>
    public const string DefaultFileName = "klippy.vars";

    private readonly Dictionary<string, string> _values;
    private readonly DateTime _stamp;
    private readonly long _length;

    private KlippyVariables(Dictionary<string, string> values, string filePath, bool exists, DateTime stamp, long length)
    {
        _values = values;
        FilePath = filePath;
        Exists = exists;
        _stamp = stamp;
        _length = length;
    }

    /// <summary>The file these came from, whether or not it is there.</summary>
    public string FilePath { get; }

    /// <summary>Whether that file exists. An empty file and a missing one both define nothing.</summary>
    public bool Exists { get; }

    public int Count => _values.Count;

    /// <summary>Names and their fully expanded values, for display and tests.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The value of <paramref name="name"/>, or null when it is not defined.</summary>
    public string? Get(string name) =>
        name.Length > 0 && _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// What a whole word stands for, or null when it stands for nothing: the lookup an
    /// argument typed after a quick-code gets, where the word <em>is</em> a name rather
    /// than merely containing one.
    ///
    /// <c>pir</c> and <c>%pir%</c> are the same request. With nothing either side of the
    /// name there is nothing to delimit it from, so the percent signs are optional here
    /// in the way both dialects are optional in a path — the cost of writing the one a
    /// reader did not expect should be nothing.
    ///
    /// Deliberately not <see cref="Expand(string?)"/>. A word with a name buried in the
    /// middle of it keeps its percent signs exactly as it always has, which is what lets
    /// <c>%TEMP%\build</c> reach a script meaning what it says. And the file only, never
    /// the environment: <c>%PATH%</c> has no business arriving as an argument because
    /// somebody typed <c>path</c>.
    /// </summary>
    /// <param name="qualifier">
    /// The flavour the <c>%P%</c> about to be filled asked for — <c>file</c> from a
    /// <c>%P:file%</c> — or null where it asked for none. A name may be defined once per
    /// flavour, as <c>klippy:folder</c> and <c>klippy:file</c>, and this is what lets the
    /// item say which of them it meant so the person invoking it need only type
    /// <c>klippy</c>.
    /// </param>
    public string? ValueOf(string? word, string? qualifier = null)
    {
        if (string.IsNullOrEmpty(word)) return null;

        // A pair around the whole word, and only there. "100%off%x" is a word with
        // percent signs in it, not a name, and Get would turn it down anyway — a name
        // that contains one could never have been written as %name%.
        if (word.Length >= 2 && word[0] == '%' && word[^1] == '%')
        {
            // %C% and %P% are the item's placeholders. A file that defines c or p does
            // not get to swallow them here any more than it does in Expand.
            if (Macros.IsPresent(word)) return null;
            word = word[1..^1];
        }

        // A word that names its own flavour is taken at it: "pir:file" is the whole name,
        // and what the item would have asked for does not overrule what somebody typed.
        // The same test keeps a Windows path out of this — "C:\temp" carries a colon
        // without naming a flavour, and looking it up whole is the right answer anyway.
        if (string.IsNullOrEmpty(qualifier) || word.Contains(':')) return Get(word);

        // The flavour asked for, then the bare name: putting a %P:file% on an item must
        // not stop it working with the defines that have no flavours at all.
        return Get(word + ':' + qualifier) ?? Get(word);
    }

    /// <summary>
    /// Replaces every <c>%name%</c> this file defines with its value. Everything else is
    /// left byte for byte as it was written — an undefined name, a lone percent sign, a
    /// pair with a space between them.
    /// </summary>
    public string Expand(string? text) => Expand(text, Get);

    /// <summary>
    /// These variables in front of <paramref name="machine"/>: a name is looked up here
    /// first and in the environment after.
    ///
    /// What the execute path resolves a path against, so <c>%ws%</c> names WebStorm there
    /// exactly as <c>%LOCALAPPDATA%</c> names a folder — one rule for what a percent pair
    /// in a path means, rather than two that disagree. Copying keeps the narrower rule:
    /// see <see cref="Resolve"/> for why a snippet's own text never reads the environment.
    /// </summary>
    public EnvironmentProbe Ahead(EnvironmentProbe machine) =>
        machine with { Value = name => Get(name) ?? machine.Value(name) };

    // ---- where the file is ----

    /// <summary>
    /// The file <paramref name="settings"/> names, resolved: a bare name lands in the
    /// storage folder, an absolute path is taken as given — which is how a shared snippet
    /// store still reads a machine-local file. A blank name is the default one, since
    /// there is no such thing as "no variables file": one that is not there defines
    /// nothing, which is the same answer.
    /// </summary>
    public static string PathFor(AppSettings settings) => StorageLocations.Resolve(
        settings.VariablesFile is { } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : DefaultFileName);

    /// <summary>What <see cref="Current"/> is reading. <see cref="PathFor"/> of the app's own settings.</summary>
    public static string CurrentPath => PathFor(AppSettings.Current);

    private static KlippyVariables? _current;

    /// <summary>
    /// The variables in force. Re-read whenever the file changes, so an edit applies to
    /// the next copy rather than the next launch: this is a file people open in a text
    /// editor, and "restart Klippy" is a poor answer to a typo. The check is one stat call
    /// per copy, against a gesture that already touches the clipboard and the store.
    /// </summary>
    public static KlippyVariables Current
    {
        get
        {
            var path = CurrentPath;
            if (_current is { } cached && cached.IsStillCurrent(path)) return cached;
            return _current = Load(path);
        }
    }

    private bool IsStillCurrent(string path) =>
        string.Equals(FilePath, path, StringComparison.Ordinal) && Fingerprint(path) == (_stamp, _length);

    /// <summary>Never throws: an unreadable file means no variables, not no copy.</summary>
    public static KlippyVariables Load(string? path = null)
    {
        path ??= CurrentPath;

        // Fingerprinted before the read, so a file edited mid-load is re-read next time
        // rather than cached as though it had already been seen.
        var (stamp, length) = Fingerprint(path);
        bool exists = File.Exists(path);
        try
        {
            if (exists)
                return new KlippyVariables(Resolve(ParseLines(File.ReadAllText(path))), path, true, stamp, length);
        }
        catch (Exception)
        {
            // Unreadable — locked, or not text. Still report it as there, since "no file
            // yet" would send someone off to create the one they already have.
        }
        return new KlippyVariables(NewMap(), path, exists, stamp, length);
    }

    /// <summary>Parses file contents directly, for tests and for callers holding the text.</summary>
    public static KlippyVariables Parse(string text, string filePath = "") =>
        new(Resolve(ParseLines(text)), filePath, true, default, 0);

    private static (DateTime Stamp, long Length) Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, 0L);
        }
        catch (Exception)
        {
            return (default, 0L);
        }
    }

    // ---- parsing ----

    private static Dictionary<string, string> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>name=value</c> per line; <c>#</c> and <c>;</c> start a whole-line comment. The
    /// value is taken literally after trimming — quotes included, because a Windows path
    /// with spaces needs its quotes to survive into the shell you paste it at.
    /// A line that isn't a definition is skipped rather than rejected: one typo must not
    /// cost the rest of the file.
    /// </summary>
    private static Dictionary<string, string> ParseLines(string text)
    {
        var raw = NewMap();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var name = line[..eq].TrimEnd();
            if (!IsUsableName(name)) continue;

            raw[name] = line[(eq + 1)..].TrimStart();
        }
        return raw;
    }

    /// <summary>
    /// A name has to be something <see cref="Expand(string?)"/> could actually find:
    /// <c>%my var%</c> never matches, because the scan stops at whitespace, and a name
    /// containing a percent sign could never be delimited by one.
    /// </summary>
    private static bool IsUsableName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (c == '%' || char.IsWhiteSpace(c)) return false;
        return true;
    }

    // ---- expansion ----

    /// <summary>
    /// Expands each value once, at load, so a copy costs a dictionary lookup.
    ///
    /// A value may name other variables, and anything the file does not define falls
    /// through to the process environment — which is what makes the obvious line work:
    /// <c>ws=%localappdata%\Programs\WebStorm\bin\webstorm64.exe</c> resolves to a real
    /// path. That fallback is deliberately confined to values: a variables file is
    /// written for this, whereas a snippet may have contained <c>%TEMP%</c> since long
    /// before Klippy had variables and must keep meaning what it says.
    /// </summary>
    private static Dictionary<string, string> Resolve(Dictionary<string, string> raw)
    {
        var resolved = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in raw)
        {
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            resolved[name] = ResolveValue(value, raw, open);
        }
        return resolved;
    }

    private static string ResolveValue(string value, Dictionary<string, string> raw, HashSet<string> open) =>
        Expand(value, name =>
        {
            // A name already being expanded is a cycle, so it does not resolve against the
            // file a second time — it falls through to the environment instead. That is
            // what lets `localappdata=%localappdata%` mean "whatever the OS calls it",
            // which is the one way to make an environment variable visible to snippets.
            if (!open.Contains(name) && raw.TryGetValue(name, out var inner))
            {
                open.Add(name);
                var expanded = ResolveValue(inner, raw, open);
                open.Remove(name);
                return expanded;
            }
            return Environment.GetEnvironmentVariable(name);
        });

    /// <summary>
    /// Scans for <c>%name%</c> and replaces what <paramref name="lookup"/> knows. Hand
    /// written rather than a regex because the rule is one character wide: a name runs
    /// from a percent sign to the next one and cannot contain whitespace, so "50% off,
    /// 100% focus" holds no candidate at all.
    /// </summary>
    private static string Expand(string? text, Func<string, string?> lookup)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('%') < 0) return text ?? "";

        StringBuilder? built = null;
        int copied = 0, i = 0;

        while (i < text.Length)
        {
            int open = text.IndexOf('%', i);
            if (open < 0) break;

            int close = FindClose(text, open + 1);
            if (close < 0)
            {
                // No closing percent before whitespace or end of text. Keep scanning
                // rather than stopping: "50% off, use %ws%" still has a variable in it.
                i = open + 1;
                continue;
            }

            var name = text[(open + 1)..close];

            // %C% and %P% are an item's placeholders, filled from the clipboard and from
            // typed arguments. Nobody writing one meant a variable named C, so a file that
            // happens to define one does not get to swallow them — the same exemption
            // EnvironmentProbe makes.
            if (name.Length > 0 && !Macros.IsPresent(text[open..(close + 1)]) &&
                lookup(name) is { } value)
            {
                built ??= new StringBuilder(text.Length);
                built.Append(text, copied, open - copied).Append(value);
                copied = close + 1;

                // Past the pair we just consumed. Resuming at the closing percent instead
                // would let it open the *next* pair, and that pair would then start behind
                // what has already been copied out.
                i = copied;
            }
            else
            {
                // Nothing was consumed, so the closing percent is free to open the next
                // pair, as in "%undefined%defined%".
                i = close;
            }
        }

        if (built is null) return text;
        return built.Append(text, copied, text.Length - copied).ToString();
    }

    private static int FindClose(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] == '%') return i;
            if (char.IsWhiteSpace(text[i])) return -1;
        }
        return -1;
    }
}
