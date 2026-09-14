using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Klippy.Services;

/// <summary>What an unmatched search string turned out to be, if anything.</summary>
public enum LaunchKind
{
    /// <summary>Nothing runnable — the search simply found nothing, and that is the whole story.</summary>
    None,

    /// <summary>An https URL, or a www. host that becomes one.</summary>
    Url,

    /// <summary>A folder, opened in Explorer/Finder.</summary>
    Folder,

    /// <summary>A file: a program or script to run, or a document to open with its default app.</summary>
    File,

    /// <summary>A machine-level action: lock, sleep, hibernate, restart.</summary>
    System,
}

/// <summary>The machine-level actions a search string can name.</summary>
public enum SystemAction
{
    None,
    Lock,
    Sleep,
    Hibernate,
    Restart,
}

/// <summary>
/// Something an unmatched search can be turned into. <see cref="Target"/> is what the
/// platform is handed — a URL, or a path with its environment variables already expanded —
/// and is empty for a <see cref="LaunchKind.System"/> action, which names itself.
/// </summary>
public sealed record LaunchTarget(LaunchKind Kind, string Target, SystemAction Action = SystemAction.None)
{
    public static readonly LaunchTarget None = new(LaunchKind.None, "");

    /// <summary>Whether there is anything here to offer the user.</summary>
    public bool IsRunnable => Kind != LaunchKind.None;
}

/// <summary>
/// How <see cref="LaunchPolicy"/> asks whether a path exists. Injected so the classification
/// tests describe a filesystem rather than having to build one, and so they read the same on
/// Linux CI as on the Windows desktop they are actually about.
/// </summary>
public sealed record LaunchProbe(Func<string, bool> DirectoryExists, Func<string, bool> FileExists)
{
    public static readonly LaunchProbe Real = new(Directory.Exists, File.Exists);
}

/// <summary>
/// Decides what, if anything, a search string that matched no snippet and no clip should do.
///
/// Deliberately pure — no Process, no shell, nothing started. The head does the running
/// (<see cref="LaunchRunner"/>); this only says what the string is, which is what makes the
/// half of the feature that decides whether to execute something testable.
///
/// Nothing here is reached while the search still matches an item: a snippet called "lock"
/// always wins over locking the screen, because the list is what the user is looking at.
/// See <c>MainViewModel.UpdateLaunch</c>, which is where that rule lives.
/// </summary>
public static partial class LaunchPolicy
{
    /// <summary>
    /// Extensions treated as "run this" rather than "open this with whatever owns it".
    /// Only the verb shown on the offer differs — the head hands both to the shell, which
    /// does exactly what a double-click in Explorer would.
    /// </summary>
    private static readonly string[] ExecutableExtensions =
        [".exe", ".com", ".bat", ".cmd", ".ps1", ".msi", ".lnk", ".sh", ".command", ".app"];

    /// <summary>
    /// Classifies <paramref name="query"/>. Returns <see cref="LaunchTarget.None"/> for
    /// anything that is not plainly one of the four kinds — the offer has to be predictable,
    /// so a string that merely resembles a path is not one.
    /// </summary>
    /// <param name="verifyPaths">
    /// When true (the default) a path is only offered if it is actually there. Off, any
    /// rooted path is offered and the OS reports the failure — for a network share that is
    /// slow to answer, or a path that only exists once something else has run.
    /// </param>
    public static LaunchTarget Parse(string? query, bool verifyPaths = true, LaunchProbe? probe = null)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return LaunchTarget.None;

        var action = ParseSystemAction(text);
        if (action != SystemAction.None)
            return new LaunchTarget(LaunchKind.System, "", action);

        if (ParseUrl(text) is { } url)
            return new LaunchTarget(LaunchKind.Url, url);

        return ParsePath(text, verifyPaths, probe ?? LaunchProbe.Real);
    }

    /// <summary>
    /// The four OS controls, matched as the whole search and nothing else. One word, so a
    /// search that happens to contain "restart" is still just a search.
    /// </summary>
    public static SystemAction ParseSystemAction(string text) => text.Trim().ToLowerInvariant() switch
    {
        "lock" => SystemAction.Lock,
        "sleep" => SystemAction.Sleep,
        "hibernate" => SystemAction.Hibernate,
        "restart" => SystemAction.Restart,
        _ => SystemAction.None,
    };

    /// <summary>
    /// The URL to open, or null. Deliberately narrow: <c>https://</c> and a bare
    /// <c>www.</c> host, nothing else.
    ///
    /// <c>http://</c> is left out because a launcher that silently sends you over plaintext
    /// is not doing you a favour, and every other scheme because this is the one place in
    /// Klippy where typed text is handed to the shell — <c>file:</c>, <c>shell:</c> and
    /// friends would make "it only opens web pages" untrue.
    /// </summary>
    public static string? ParseUrl(string text)
    {
        // A URL has no spaces in it, and allowing them would let a sentence that begins
        // "www. .." through as a host.
        foreach (var c in text)
            if (char.IsWhiteSpace(c)) return null;

        var candidate =
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? text
            : text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text
            : null;

        if (candidate is null) return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        // A dot with something either side of it, which is what keeps "https://localhost"
        // and a half-typed "www." from counting as hosts. Neither is what someone typing
        // into a snippet filter meant, and both are cheap to get wrong.
        int dot = uri.Host.IndexOf('.');
        return dot > 0 && dot < uri.Host.Length - 1 ? candidate : null;
    }

    private static LaunchTarget ParsePath(string text, bool verifyPaths, LaunchProbe probe)
    {
        var path = Expand(text);

        // Rooted only. A relative path would be relative to wherever the launcher happens
        // to have been started from, which is not a place the user is thinking about — and
        // without this every unmatched word with a dot in it would look like a file.
        if (!IsRooted(path)) return LaunchTarget.None;

        // Asked even when verification is off: the probe is one stat call, and it is what
        // tells a folder from a file, so the offer can say "Open folder" rather than guess.
        if (probe.DirectoryExists(path)) return new LaunchTarget(LaunchKind.Folder, path);
        if (probe.FileExists(path)) return new LaunchTarget(LaunchKind.File, path);

        if (verifyPaths) return LaunchTarget.None;

        // Nothing there to look at, so the shape decides: a trailing separator is the only
        // thing that still says "folder".
        return new LaunchTarget(EndsWithSeparator(path) ? LaunchKind.Folder : LaunchKind.File, path);
    }

    /// <summary>Whether a file path names something to run rather than something to open.</summary>
    public static bool IsExecutable(string path)
    {
        foreach (var extension in ExecutableExtensions)
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Expands the environment-variable shorthands people actually type: <c>%APPDATA%</c> on
    /// Windows, <c>$HOME</c>/<c>${HOME}</c> and a leading <c>~</c> on macOS and Linux. Both
    /// forms are honoured everywhere, since the cost of the wrong one is a name that does
    /// not resolve.
    ///
    /// An unknown name is left exactly as written, so it stays a string that matches nothing
    /// rather than quietly becoming a path with a hole in the middle of it.
    /// </summary>
    public static string Expand(string text)
    {
        if (text.Length == 0) return text;

        if (text[0] == '~' && (text.Length == 1 || text[1] == '/' || text[1] == '\\'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) text = home + text[1..];
        }

        return EnvVar().Replace(text, match =>
            Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? match.Value);
    }

    /// <summary>
    /// Whether the path names a place rather than a location relative to something.
    ///
    /// Hand-rolled rather than <see cref="Path.IsPathRooted(string)"/>, which answers for the
    /// OS it is running on: <c>C:\work</c> is not a rooted path on Linux, so every test about
    /// the Windows desktop this feature is mostly for would pass for the wrong reason on CI.
    /// </summary>
    public static bool IsRooted(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal)               // UNC share
        || path.StartsWith('/')                                          // unix absolute
        || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' // drive, with a
            && (path[2] == '\\' || path[2] == '/'));                     // separator: "C:" alone is drive-relative

    private static bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == '\\' || path[^1] == '/');

    // Source-generated rather than a runtime Regex, which NativeAOT cannot compile.
    [GeneratedRegex(@"%(?<name>[^%\s]+)%|\$\{(?<name>[^}\s]+)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvVar();
}
