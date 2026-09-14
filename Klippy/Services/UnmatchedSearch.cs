using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Klippy.Services;

/// <summary>
/// How <see cref="UnmatchedSearch"/> asks whether a path exists. Injected so the rules
/// stay pure and the tests can describe a filesystem rather than build one — and so a
/// test about Windows paths reads the same on the Linux machine CI runs it on.
/// </summary>
public sealed record PathProbe(Func<string, bool> DirectoryExists, Func<string, bool> FileExists)
{
    public static readonly PathProbe Real = new(Directory.Exists, File.Exists);
}

/// <summary>
/// What a search that matched nothing might mean, if anything: a link, a folder, a
/// script or application to run, or one of the machine's own controls.
///
/// It answers in an <see cref="ExecutionPlan"/>, the same thing an item marked Execute
/// produces, so both go to the one execution engine — this decides <em>what</em>, and
/// <see cref="ProcessLauncher"/> is still the only thing that starts anything. Pure, with
/// the platform a parameter and the file system reached through a
/// <see cref="PathProbe"/>: the rules deciding whether to execute typed text are the ones
/// most worth pinning down in tests.
///
/// Nothing here is reached while the search still matches an item — a snippet called
/// "lock" wins over locking the screen, because the list is what the user is looking at.
/// <c>MainViewModel.UpdateOffer</c> is where that rule lives.
///
/// Deliberately narrower than <see cref="ExecutionPolicy"/> is for a marked item, in two
/// ways. URLs here are <c>https</c> and a bare <c>www.</c> only, where a marked item may
/// also carry <c>http</c> and <c>mailto</c>: an item was written and marked on purpose,
/// while this is whatever happened to be typed into a filter box. And a path has to be
/// rooted, because a relative one would resolve against wherever Klippy was started from,
/// which is nobody's mental model.
/// </summary>
public static partial class UnmatchedSearch
{
    private static readonly ExecutionPlan Nothing = new();

    /// <param name="verifyPaths">
    /// When true (the default) a path is only offered if it is really there. Off, any
    /// rooted path is offered and the launcher reports the failure — for a network share
    /// that is slow to answer, or a path that only exists once something else has run.
    /// </param>
    public static ExecutionPlan Plan(
        string? query,
        bool verifyPaths = true,
        ExecutionPlatform? platform = null,
        PathProbe? probe = null)
    {
        var text = Unquote((query ?? "").Trim());
        if (text.Length == 0) return Nothing;

        var os = platform ?? ExecutionPolicy.CurrentPlatform;

        var action = SystemActionFor(text);
        if (action != SystemAction.None)
            return ExecutionPolicy.Supports(action, os)
                ? new ExecutionPlan { Kind = ExecutionKind.System, Action = action }
                : Nothing;

        if (AsUrl(text) is { } url)
            return new ExecutionPlan { Kind = ExecutionKind.Url, Target = url };

        return AsPath(text, verifyPaths, os, probe ?? PathProbe.Real);
    }

    /// <summary>
    /// Takes the double quotes off a line that is wrapped in them, so a path pasted from
    /// Explorer's <b>Copy as path</b> — which always quotes, and quotes whether or not the
    /// path has a space in it — is a path rather than a string starting with a quote.
    ///
    /// Both quotes or neither: an unmatched one is a half-finished paste, and guessing at
    /// it would be worse than leaving it as the search it still is. Nothing is lost by
    /// taking them off, since a Windows path cannot contain a double quote at all.
    ///
    /// It happens before everything else rather than only for paths, because that is what
    /// the other route into execution already does — a marked item's line goes through
    /// <see cref="Macros.SplitArguments"/>, which unquotes its first word whatever that
    /// word turns out to be. Two routes to the same launcher should not disagree about
    /// what a pair of quotes means.
    /// </summary>
    public static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1].Trim()
            : text;

    /// <summary>
    /// The machine control a search names, matched as the whole line and nothing else.
    /// One word, so "restarting" and "please restart" stay ordinary searches.
    /// </summary>
    public static SystemAction SystemActionFor(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "lock" => SystemAction.Lock,
        "sleep" => SystemAction.Sleep,
        "hibernate" => SystemAction.Hibernate,
        "restart" => SystemAction.Restart,
        _ => SystemAction.None,
    };

    /// <summary>
    /// The URL a typed line stands for, or null. <c>https://</c> and a bare <c>www.</c>
    /// only: <c>http</c> would silently send the user over plaintext, and every other
    /// scheme — <c>file:</c>, <c>shell:</c>, <c>javascript:</c> — is a program waiting to
    /// be launched under another name. A marked item's own rules are wider on purpose;
    /// see the class summary.
    /// </summary>
    public static string? AsUrl(string text)
    {
        // A URL carries no spaces, and allowing them would let a sentence beginning
        // "www. …" through as a host.
        foreach (var c in text)
            if (char.IsWhiteSpace(c)) return null;

        if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return null;

        // Past the narrowing, the app's own rule decides — including putting the https in
        // front of a www., which is the one place both agree exactly.
        if (ExecutionPolicy.AsUrl(text) is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // A dot with something either side of it, which is what keeps "https://localhost"
        // and a half-typed "www." from counting as hosts.
        int dot = uri.Host.IndexOf('.');
        return dot > 0 && dot < uri.Host.Length - 1 ? url : null;
    }

    private static ExecutionPlan AsPath(string text, bool verifyPaths, ExecutionPlatform os, PathProbe probe)
    {
        var path = Expand(text);
        if (!IsRooted(path)) return Nothing;

        // The extension is asked before the disk is, because a macOS .app bundle *is* a
        // directory: probing first would open Safari in Finder rather than starting it.
        var application = ExecutionPolicy.ApplicationExtension(path);
        var extension = ExecutionPolicy.ScriptExtension(path) ?? application;
        if (extension is not null)
        {
            // .exe on a Mac, .bat on Linux: not runnable here, so not offered here.
            if (!ExecutionPolicy.Supports(extension, os)) return Nothing;

            var target = path.TrimEnd('/', '\\');
            bool there = application == ".app" ? probe.DirectoryExists(target) : probe.FileExists(target);
            if (!there && verifyPaths) return Nothing;

            return new ExecutionPlan
            {
                Kind = application is null ? ExecutionKind.Script : ExecutionKind.Application,
                Target = target,
            };
        }

        if (probe.DirectoryExists(path)) return Folder(path);

        // Nothing there to look at, so the shape decides — and a trailing separator is the
        // only thing left that still says "folder". Anything else is not offered at all:
        // the allow-list is the point, so a path Klippy cannot name is a path it declines.
        return !verifyPaths && EndsWithSeparator(path) ? Folder(path) : Nothing;
    }

    private static ExecutionPlan Folder(string path) =>
        new() { Kind = ExecutionKind.Folder, Target = path };

    /// <summary>
    /// Expands the environment-variable shorthands people actually type: <c>%APPDATA%</c>
    /// as on Windows, <c>$HOME</c>/<c>${HOME}</c> and a leading <c>~</c> as on macOS and
    /// Linux. Both dialects are honoured everywhere, since the cost of the wrong one is a
    /// name that does not resolve.
    ///
    /// An unknown name is left exactly as written, so it stays a string that matches
    /// nothing rather than quietly becoming a path with a hole in the middle of it.
    ///
    /// <c>%C%</c> and <c>%P%</c> are not touched here: they are an item's placeholders,
    /// filled from the clipboard and from typed arguments, and a search box is neither.
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
            // %C% would otherwise be read as an environment variable named C.
            Macros.IsPresent(match.Value)
                ? match.Value
                : Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? match.Value);
    }

    /// <summary>
    /// Whether a path names a place rather than somewhere relative to something else.
    ///
    /// Hand-rolled rather than <see cref="Path.IsPathRooted(string)"/>, which answers for
    /// the machine it is running on: <c>C:\work</c> is not a rooted path on Linux, so
    /// every test about the Windows desktop would pass for the wrong reason on CI. The
    /// same reasoning as <c>ExecutionPolicy.FileNameOf</c>, which avoids Path.GetFileName.
    /// </summary>
    public static bool IsRooted(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal)                // UNC share
        || path.StartsWith('/')                                           // unix absolute
        || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'  // a drive, with a
            && (path[2] == '\\' || path[2] == '/'));                      // separator: "C:" alone is drive-relative

    private static bool EndsWithSeparator(string path) =>
        path.Length > 0 && (path[^1] == '\\' || path[^1] == '/');

    // Source-generated rather than a runtime Regex, which NativeAOT cannot compile.
    [GeneratedRegex(@"%(?<name>[^%\s]+)%|\$\{(?<name>[^}\s]+)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvVar();
}
