using System;
using System.Collections.Generic;
using System.IO;

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
/// What a search that matched nothing might mean, if anything: a link, a folder or a file
/// to show, a script or application to run, or one of the machine's own controls.
///
/// It answers in an <see cref="ExecutionPlan"/>, the same thing an item marked Execute
/// produces, so both go to the one execution engine — this decides <em>what</em>, and
/// <see cref="ProcessLauncher"/> is still the only thing that starts anything. Pure, with
/// the platform a parameter and the file system reached through a
/// <see cref="PathProbe"/>: the rules deciding whether to execute typed text are the ones
/// most worth pinning down in tests.
///
/// An item that matches beats what this decides, but only where the line could have been
/// meant as a search for that item — see <see cref="CouldBeASearch"/>. A snippet called
/// "lock" keeps "lock" a filter; a snippet that merely mentions a folder does not take that
/// folder's own path away from someone who typed it. <c>MainViewModel.UpdateOffer</c> is
/// where that rule lives.
///
/// Deliberately narrower than <see cref="ExecutionPolicy"/> is for a marked item, in two
/// ways. URLs here are <c>http</c>, <c>https</c> and a bare <c>www.</c>, where a marked
/// item may also carry <c>mailto</c>, a settings or browser page, a <c>file:</c> link and
/// the kinds of link the settings add: an item was written and marked on purpose, while
/// this is whatever happened to be typed into a filter box. And a path has to be rooted,
/// because a relative one would resolve against wherever Klippy was started from, which
/// is nobody's mental model.
///
/// Variables are not one of the two: <see cref="EnvironmentProbe"/> reads the same
/// shorthands for both routes, so <c>%APPDATA%</c> names one folder whether it was typed
/// into a filter box or written into an item.
/// </summary>
public static class UnmatchedSearch
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
        PathProbe? probe = null,
        EnvironmentProbe? environment = null)
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

        return AsPath(text, verifyPaths, os, probe ?? PathProbe.Real, environment ?? EnvironmentProbe.Real);
    }

    /// <summary>
    /// What a line that names one of Klippy's own defines is offered as: each value the name
    /// stands for, read the way an item marked Execute reads its text. <c>klippy</c>, given
    /// once as a folder and once as the solution in it, offers to open the folder and to
    /// show the solution — or to open the solution, where <c>.slnx</c> is one of the kinds
    /// the settings say to open.
    ///
    /// A marked item's rules rather than a typed line's, because a value is not something
    /// that landed in a filter box: it was written into a file of the user's own, as an
    /// item's text is, and the name that reached it was typed on purpose. So a document
    /// opens, a <c>mailto:</c> or an <c>ms-settings:</c> is a link, and a program keeps the
    /// arguments the value gives it — all of it still the allow-list
    /// <see cref="ExecutionPolicy.Plan"/> applies to every item, and nothing past it.
    ///
    /// Three things are narrower than for an item, since this is an offer made on a
    /// keystroke rather than a line someone chose to run:
    /// <list type="bullet">
    /// <item>A value carrying <c>%P%</c> or <c>%C%</c> is a template for an item rather
    /// than somewhere to go, and is left out: an offer has no arguments to fill it, and
    /// never reads the clipboard to draw itself.</item>
    /// <item>A path is only offered where it is there, as a typed one is — unless
    /// <paramref name="verifyPaths"/> says otherwise, and a file to be shown is looked for
    /// either way, since nothing else says what is being pointed at.</item>
    /// <item>A file that would be opened is also offered to be shown, straight after it:
    /// being able to open a file is no reason to lose the way to where it is.</item>
    /// </list>
    ///
    /// A value that comes to nothing is left out, and so is one that comes to the same
    /// thing as a value before it.
    /// </summary>
    /// <param name="variables">The defines to read the line against — the file in force, for the app.</param>
    /// <param name="alsoOpens">Extra kinds of file and of link to open, as <see cref="ExecutionPolicy.Plan"/> takes them.</param>
    public static IReadOnlyList<ExecutionPlan> PlanDefine(
        string? query,
        KlippyVariables variables,
        bool verifyPaths = true,
        ExecutionPlatform? platform = null,
        PathProbe? probe = null,
        EnvironmentProbe? environment = null,
        IReadOnlyCollection<string>? alsoOpens = null)
    {
        var os = platform ?? ExecutionPolicy.CurrentPlatform;
        var disk = probe ?? PathProbe.Real;

        var plans = new List<ExecutionPlan>();
        foreach (var value in variables.ValuesOf((query ?? "").Trim()))
        {
            if (Macros.IsPresent(value)) continue;

            var plan = ExecutionPolicy.Plan(
                value, platform: os, environment: environment, paths: disk, alsoOpens: alsoOpens);
            if (!IsThere(plan, verifyPaths, disk)) continue;

            Add(plan);
            if (plan.Kind == ExecutionKind.Document
                && ExecutionPolicy.Reveal(plan.Target, isFolder: false, os) is var shown
                && IsThere(shown, verifyPaths, disk))
                Add(shown);
        }
        return plans;

        void Add(ExecutionPlan plan)
        {
            if (plan.Kind != ExecutionKind.None && !plans.Exists(p => IsSame(p, plan))) plans.Add(plan);
        }
    }

    /// <summary>
    /// Whether what a plan points at is there to be offered: always for a link, which has
    /// no disk to ask, and for a path as <see cref="Plan"/> decides it for a typed one.
    ///
    /// A path has to be rooted, as a typed one does, with one exception: a bare program
    /// name, <c>notepad.exe</c>, which the OS finds on <c>PATH</c> as Run would when an item
    /// names one. There is nowhere on the disk to look for that, so it is taken on trust.
    /// Anything else relative would resolve against wherever Klippy was started from.
    /// </summary>
    private static bool IsThere(ExecutionPlan plan, bool verifyPaths, PathProbe disk)
    {
        switch (plan.Kind)
        {
            case ExecutionKind.None:
                return false;
            case ExecutionKind.Url or ExecutionKind.System:
                return true;
            case ExecutionKind.Reveal:
                return disk.FileExists(plan.Target) || disk.DirectoryExists(plan.Target);
        }

        if (!IsRooted(plan.Target))
            return plan.Kind == ExecutionKind.Application && plan.Target.IndexOfAny(['\\', '/']) < 0;

        if (!verifyPaths) return true;
        return plan.Kind == ExecutionKind.Folder
            ? disk.DirectoryExists(plan.Target)
            : disk.FileExists(plan.Target) || disk.DirectoryExists(plan.Target); // a .app is a folder
    }

    /// <summary>
    /// Whether two offers would do the same thing, so that one line is never offered the
    /// same folder twice — once because it resolved there, and again because a define
    /// named it. A path is compared without regard to case, as Windows and macOS read one.
    /// </summary>
    public static bool IsSame(ExecutionPlan a, ExecutionPlan b) =>
        a.Kind == b.Kind
        && a.Action == b.Action
        && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase)
        && a.Arguments.AsSpan().SequenceEqual(b.Arguments);

    /// <summary>
    /// Whether what the line names could also have been meant as a search for an item.
    ///
    /// Only a machine control could: <c>lock</c> and <c>restart</c> are ordinary words, and
    /// a snippet may legitimately answer to them. Everything else here had to carry a
    /// scheme, a <c>www.</c>, or a drive, UNC or <c>/</c> root to be recognised at all —
    /// nobody types <c>D:\work\tools\</c> hoping to filter a list, so a snippet that
    /// happens to mention that path has not been asked for.
    ///
    /// The distinction the offer needs: a line that could be a search is beaten by an item
    /// that matches it, and a line that could not is not.
    /// </summary>
    public static bool CouldBeASearch(ExecutionPlan plan) => plan.Kind == ExecutionKind.System;

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
    ///
    /// The same now goes for a variable, in the other direction: <c>%APPDATA%</c> had
    /// always resolved here and never in a marked item, and two routes to the same
    /// launcher should not disagree about that either.
    /// </summary>
    public static string Unquote(string text) => ExecutionPolicy.Unquote(text);

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
    /// The URL a typed line stands for, or null. The two web schemes and a bare
    /// <c>www.</c>: a dev server on <c>localhost:8000</c>, or a box on the LAN, answers
    /// on <c>http://</c> and nothing else, and a launcher that refuses what the browser
    /// beside it would open is the one being unhelpful. Every other scheme —
    /// <c>file:</c>, <c>shell:</c>, <c>javascript:</c> — stays out, being a program
    /// waiting to be launched under another name. A marked item's own rules are wider
    /// still; see the class summary.
    /// </summary>
    public static string? AsUrl(string text)
    {
        // A URL carries no spaces, and allowing them would let a sentence beginning
        // "www. …" through as a host.
        foreach (var c in text)
            if (char.IsWhiteSpace(c)) return null;

        bool typedScheme = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!typedScheme && !text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return null;

        // Past the narrowing, the app's own rule decides — including putting the https in
        // front of a www., which is the one place both agree exactly. A typed http:// is
        // left on the scheme it was typed with: promoting it would send the request to a
        // port the host may well have nothing listening on.
        if (ExecutionPolicy.AsUrl(text) is not { } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Typing the scheme out is the line saying it meant a URL, so the host stands as
        // written: localhost:8000 is a dev server, and a LAN machine's bare name is how
        // its own network reaches it. A scheme with nothing behind it never gets this
        // far, "https://" and "http://" being unparseable rather than hosts.
        if (typedScheme) return url;

        // A bare www. has said no such thing, and still wants a dot with something either
        // side of it — which is what keeps a half-typed "www." from counting as a host.
        int dot = uri.Host.IndexOf('.');
        return dot > 0 && dot < uri.Host.Length - 1 ? url : null;
    }

    private static ExecutionPlan AsPath(
        string text, bool verifyPaths, ExecutionPlatform os, PathProbe probe, EnvironmentProbe machine)
    {
        // Quotes again once the variables are in, since a value may be written the way a
        // shell wants it — klippy="D:\main\Klippy" — and running takes that pair back off,
        // as a marked item's line always has. %klippy% typed here is the same request.
        var path = Unquote(machine.Expand(text));
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

        // Anything else that is there is shown where it lies: a folder opened, a file
        // selected in the folder it is in. A file is never *opened* from here — a path typed
        // into a filter box is more often one being hunted for than one to open — but
        // showing it runs nothing, so any kind of file may be shown where only a few may be
        // opened. ExecutionPolicy.Reveal holds what Explorer and Finder may be handed.
        bool folder = probe.DirectoryExists(path);
        if (folder || probe.FileExists(path)) return ExecutionPolicy.Reveal(path, folder, os);

        // Nothing there to look at, so the shape decides — and a trailing separator is the
        // only thing left that still says "folder". Anything else is not offered at all:
        // a file has to be there to be shown, and nothing else is on the list.
        return !verifyPaths && EndsWithSeparator(path) ? ExecutionPolicy.Reveal(path, isFolder: true, os) : Nothing;
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
}
