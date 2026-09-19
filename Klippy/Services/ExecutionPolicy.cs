using System;
using System.Collections.Generic;

namespace Klippy.Services;

/// <summary>What executing an item turns out to mean.</summary>
public enum ExecutionKind
{
    /// <summary>Nothing runnable; <see cref="ExecutionPlan.Problem"/> says why.</summary>
    None,

    /// <summary>A link, opened in the default browser.</summary>
    Url,

    /// <summary>A script file, run by its interpreter.</summary>
    Script,

    /// <summary>An application, started the way its platform starts one.</summary>
    Application,

    /// <summary>A folder, opened in the platform's file manager.</summary>
    Folder,

    /// <summary>
    /// A file, handed to whatever the platform opens its kind with — the same gesture as
    /// double-clicking it. Distinct from <see cref="Application"/>, which starts a
    /// program, and from <see cref="Script"/>, which hands one to an interpreter: a
    /// klippy.vars is neither, and wants the user's text editor.
    /// </summary>
    Document,

    /// <summary>A machine-level action: lock, sleep, hibernate or restart.</summary>
    System,
}

/// <summary>
/// The machine-level actions Klippy can ask for. Reached only by typing one of the four
/// words into the search box (see <see cref="UnmatchedSearch"/>) — an item cannot be
/// marked to restart the computer.
/// </summary>
public enum SystemAction
{
    None,
    Lock,
    Sleep,
    Hibernate,
    Restart,
}

/// <summary>The platforms execution differs between. Passed in so the rules can be tested anywhere.</summary>
public enum ExecutionPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>What executing an item resolves to, after its macros are filled in.</summary>
public sealed record ExecutionPlan
{
    public ExecutionKind Kind { get; init; } = ExecutionKind.None;

    /// <summary>The URL to open, or the path of the script or application to run.</summary>
    public string Target { get; init; } = "";

    /// <summary>
    /// Arguments for a script or an application. Always empty for a URL, which carries
    /// its own.
    /// </summary>
    public string[] Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Which machine-level action, when <see cref="Kind"/> is System.</summary>
    public SystemAction Action { get; init; } = SystemAction.None;

    /// <summary>
    /// Whether to bring the target's repository up to date before starting it. Stamped on
    /// by the view model from the user's preference rather than decided here: it is a
    /// choice about how to run something, not about what the text means.
    /// </summary>
    public bool PullFirst { get; init; }

    /// <summary>Why nothing can be run, when <see cref="Kind"/> is None. Shown to the user.</summary>
    public string Problem { get; init; } = "";

    /// <summary>One line saying what is about to happen, for the toast.</summary>
    public string Description => Kind switch
    {
        ExecutionKind.Url => "Opening " + Shorten(Target),
        ExecutionKind.Script => "Running " + ExecutionPolicy.FileNameOf(Target),
        ExecutionKind.Application => "Starting " + ExecutionPolicy.ApplicationName(Target),
        ExecutionKind.Folder => "Opening " + ExecutionPolicy.FolderName(Target),
        ExecutionKind.Document => "Opening " + ExecutionPolicy.FileNameOf(Target),
        ExecutionKind.System => Action switch
        {
            SystemAction.Lock => "Locking the screen",
            SystemAction.Sleep => "Going to sleep",
            SystemAction.Hibernate => "Hibernating",
            _ => "Restarting",
        },
        _ => Problem,
    };

    /// <summary>A URL as a person would name it: the host, or the whole thing if it has none.</summary>
    private static string Shorten(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url;
}

/// <summary>A process to start: what to run, and with which arguments.</summary>
/// <param name="UseShellExecute">
/// True for a URL on Windows, where handing it to the shell is what picks the default
/// browser. Everything else is started directly, so a macro's value can never be
/// re-read as part of a command line.
/// </param>
public sealed record LaunchCommand(string FileName, string[] Arguments, bool UseShellExecute = false);

/// <summary>
/// Decides what an item's text means when the user picks Execute, and which process
/// would carry that out. Pure: no file system, no processes, no Win32 — the platform and
/// the environment are both parameters, so every rule below is testable on any machine by
/// describing one. The environment differs from the platform in its default: left out, it
/// is the machine Klippy is running on, because the alternative is a caller who forgets it
/// silently getting a path with percent signs in it. <see cref="ProcessLauncher"/> does the
/// actual starting.
///
/// Three things can be executed:
///
/// <list type="bullet">
/// <item><b>URLs</b> — http, https and mailto (and a bare <c>www.</c>, which gets an
/// https in front of it, as a browser would). Opened in the default browser.</item>
/// <item><b>Scripts</b> — <c>.bat</c>/<c>.cmd</c> on Windows only, <c>.ps1</c> and
/// <c>.sh</c> everywhere, run with whatever is left on the line as arguments.</item>
/// <item><b>Applications</b> — whatever the platform calls one: an <c>.exe</c> on
/// Windows, an <c>.app</c> bundle on macOS, an <c>.AppImage</c> on Linux. Started with
/// the rest of the line as arguments, as a shortcut on the desktop would.</item>
/// </list>
///
/// The allow-list is still the point, even now that it has applications on it: a
/// snippet naming something that is none of the three is simply not executable, so a
/// bare <c>docker system prune -af</c> is text however firmly it is marked to run. It is
/// read off the first word once that word's variables have resolved — a variable may say
/// where a program lives, and it still has to be one of the three when it gets there.
/// </summary>
public static class ExecutionPolicy
{
    private static readonly string[] UrlSchemes = { "http://", "https://", "mailto:" };

    private const string WwwPrefix = "www.";

    /// <summary>
    /// Characters cmd.exe would read as command syntax rather than as text. .NET quotes
    /// arguments by the C runtime's rules, which cmd.exe does not honour, so an argument
    /// carrying one of these could start a second command inside a .bat run. Refusing is
    /// the honest answer; pretending to escape it would not be.
    ///
    /// Judged on the argument as it stands here, which is the string cmd.exe will be
    /// handed: a <c>%APPDATA%</c> written into an argument still carries its percent signs
    /// and is refused, and an argument that named a variable was resolved before it
    /// arrived, so a define worth <c>a&amp;b</c> is refused on the ampersand rather than
    /// passed on the strength of the short name it was reached by.
    /// </summary>
    private const string CmdMetaCharacters = "&|<>^\"%";

    private static readonly char[] CmdMetaCharacterSet = CmdMetaCharacters.ToCharArray();

    /// <summary>
    /// The same characters as they matter in a <c>.bat</c> file's own path, which rides
    /// the line cmd.exe re-reads just as its arguments do. Narrower by one: a percent sign
    /// there can only make cmd read the wrong path, and "Script not found" names that
    /// better than a lecture about command syntax would. The rest still split the line in
    /// two — .NET quotes only what carries a space, and cmd strips the quotes it does add.
    /// </summary>
    private static readonly char[] CmdPathMetaCharacterSet = "&|<>^\"".ToCharArray();

    public static ExecutionPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ExecutionPlatform.Windows
        : OperatingSystem.IsMacOS() ? ExecutionPlatform.MacOS
        : ExecutionPlatform.Linux;

    /// <summary>
    /// Works out what running <paramref name="text"/> would mean, filling its macros
    /// from <paramref name="arguments"/> and <paramref name="clipboardText"/> first. A
    /// path may name itself the way it does everywhere else on the machine —
    /// <c>%LOCALAPPDATA%\…</c>, <c>$HOME/…</c>, <c>~/…</c>; see
    /// <see cref="EnvironmentProbe"/>. Only the first word, so an argument's percent
    /// signs are still the user's own. Never throws: anything it cannot run comes back as
    /// <see cref="ExecutionKind.None"/> with a <see cref="ExecutionPlan.Problem"/>.
    /// </summary>
    /// <param name="arguments">
    /// Values for the item's <c>%P%</c> placeholders, as they are to be passed. Already
    /// standing for whatever they named, since only the caller reading the typed line can
    /// tell a name from a word in quotes; here they are values, and nothing re-reads a
    /// value for a name.
    /// </param>
    /// <param name="environment">
    /// The environment the first word is resolved against, described by the caller so a
    /// test need not arrange a real one. Null is the machine Klippy is running on — not
    /// "leave the variables alone", so a caller that forgets it gets the right answer
    /// rather than a path with percent signs in it. Unlike <see cref="Resolve"/>'s
    /// execute bit, whose null default is to ask nothing, this default does read the
    /// machine; a test that means to stay portable has to pass one.
    /// </param>
    public static ExecutionPlan Plan(
        string? text,
        IReadOnlyList<string>? arguments = null,
        string? clipboardText = null,
        ExecutionPlatform? platform = null,
        EnvironmentProbe? environment = null)
    {
        var os = platform ?? CurrentPlatform;
        var machine = environment ?? EnvironmentProbe.Real;
        var tokens = Macros.SplitArguments(text);
        if (tokens.Length == 0) return Nothing("There is nothing to run.");

        // A URL the item itself declares has its macro values percent-encoded, so a
        // multi-word argument lands in the query string instead of breaking the URL in
        // two. A URL that arrives *through* a macro is taken exactly as it stands —
        // encoding that would turn its own slashes into %2F.
        if (AsUrl(tokens[0]) is not null)
            return UrlPlan(Macros.Expand(tokens[0], arguments, clipboardText, Uri.EscapeDataString));

        // A path names itself here the way it does everywhere else on the machine.
        // Windows will not do it for us: CreateProcess takes a file name rather than a
        // command line, so a %LOCALAPPDATA% in one is a folder with percent signs in its
        // name and the start fails on a path that plainly exists. Only cmd.exe ever
        // looked inside a name, and nothing here goes through cmd. The typed route has
        // always resolved these before deciding anything; two routes to the same launcher
        // should not disagree about what a variable means any more than about what a pair
        // of quotes means.
        //
        // After the URL above, or a percent-encoded query string is read as a name.
        // Before the macros below, because a macro's value is data rather than more text
        // to read — the same reason it can never become a second command. And the first
        // word only: an argument keeps its percent signs, so the .bat refusal further down
        // still refuses them, and a child process inherits the environment anyway.
        //
        // It buys one thing and not another. A clipboard value is never read for variable
        // names, which is the direction that matters: a path like C:\100%discount%off\x.exe
        // keeps its middle. The other direction is open — a variable whose *value* spells
        // %P% has it filled in below like any other, since by then it is one string. That
        // needs someone to have put a Klippy macro in an environment variable, and anyone
        // who can do that can set PATH.
        var typed = tokens[0];
        tokens[0] = machine.Expand(typed);

        var parts = new List<string>(Macros.ExpandAll(tokens, arguments, clipboardText));
        if (parts.Count == 0 || parts[0].Length == 0) return Nothing("There is nothing to run.");

        // A link is used whole, spaces and all: a URL that came off the clipboard with a
        // space in it is still one URL, and cutting it at the space opens the wrong page.
        if (AsUrl(parts[0]) is not null) return UrlPlan(parts[0]);

        // A macro can otherwise hand back a whole command line — "%C%" with a script path
        // and its switches sitting on the clipboard. Split that apart again so its first
        // word is the command and the rest are arguments, as if it had been typed. Only
        // when a macro produced it: a quoted path is one path however many spaces it has,
        // and so is one that came from a variable — "C:\Users\John Smith" is a folder
        // whose name has a space in it, not two words somebody typed.
        if (MacroBroughtAWholeLine(tokens, typed, arguments, clipboardText))
        {
            var head = Macros.SplitArguments(parts[0]);
            parts.RemoveAt(0);
            parts.InsertRange(0, head);
            if (parts.Count == 0 || parts[0].Length == 0) return Nothing("There is nothing to run.");
        }

        // Quotes come off here, the last stop before the process. A typed argument lost
        // its pair when the line was split, and one that arrived through a variable or a
        // %C% must not be left carrying one the program would read as part of the name:
        // ArgumentList puts back whatever quoting the OS needs. A value written as
        // klippy="D:\main\Klippy" is the way a path meant for a shell is written, and
        // that same value still copies with its quotes intact.
        var command = Unquote(parts[0]);
        var rest = parts.GetRange(1, parts.Count - 1).ConvertAll(Unquote).ToArray();

        // Something carrying a scheme is not a path, whatever it happens to end in:
        // file:///C:/Windows/System32/cmd.exe names an .exe without being one, and
        // javascript: names nothing at all. Neither survived the URL allow-list above,
        // and neither may sneak back in through the extension rules below.
        if (HasScheme(command))
            return Nothing($"\"{Ellipsis(command)}\" is not a URL Klippy can open.");

        // A double quote inside the path would decide what starts, behind the allow-list's
        // back. The extension is read off the last segment, while Windows takes the whole
        // string as a command line and stops the program name at the quote — so
        // C:\x\payload.scr"y.exe passes as an .exe and starts the .scr. Nothing legitimate
        // is lost: a Windows path cannot contain a double quote at all, which is the same
        // fact that lets UnmatchedSearch.Unquote strip a pasted pair with no ambiguity.
        if (command.Contains('"'))
            return Nothing($"\"{Ellipsis(command)}\" is not a path Klippy can run: paths carry no quotes.");

        if (ScriptExtension(command) is { } script)
        {
            if (!Supports(script, os))
                return Nothing($"{script} scripts only run on Windows.");

            // The path rides the same line cmd.exe re-reads, so an ampersand in a folder's
            // name starts a second command exactly as one in an argument would. Refusing
            // is the same honest answer, and it matters more now that a variable can
            // supply the path rather than only the person who typed it.
            if (IsBatch(script) && command.IndexOfAny(CmdPathMetaCharacterSet) >= 0)
                return Nothing(
                    $"\"{Ellipsis(command)}\" cannot be run as a {script} file: " +
                    "cmd.exe would read &|<>^\" in its path as commands rather than text.");

            if (IsBatch(script) && FindUnsafeArgument(rest) is { } unsafeArgument)
                return Nothing(
                    $"\"{Ellipsis(unsafeArgument)}\" cannot be passed to a {script} file: " +
                    $"cmd.exe would read {CmdMetaCharacters} as commands rather than text.");

            return new ExecutionPlan
            {
                Kind = ExecutionKind.Script,
                Target = command,
                Arguments = rest,
            };
        }

        if (ApplicationExtension(command) is { } application)
        {
            if (HomeOf(application) is { } home && home != os)
                return Nothing($"{application} applications only run on {NameOf(home)}.");

            return new ExecutionPlan
            {
                Kind = ExecutionKind.Application,
                // A bundle is a directory, so its path can arrive with the trailing
                // separator a shell's tab-completion leaves behind.
                Target = command.TrimEnd(PathSeparators),
                Arguments = rest,
            };
        }

        return Nothing($"\"{Ellipsis(command)}\" is not a URL, an application or a script Klippy can run.");
    }

    /// <summary>
    /// Takes off a pair of double quotes wrapping a whole word — a path pasted from
    /// Explorer's <b>Copy as path</b>, or a value written the way one meant for a shell
    /// is written. By the time either lands here it is one argument already, which is
    /// what the quotes were for.
    ///
    /// Both quotes or neither: an unmatched one is a half-finished paste, and guessing at
    /// it would be worse than leaving it alone. Nothing is lost by taking a matched pair
    /// off, since a Windows path cannot contain a double quote at all — the same fact the
    /// refusals above rest on, and they still refuse a quote anywhere else.
    /// </summary>
    public static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Trim() : text;

    /// <summary>
    /// Whether text has any chance of running: its first word is a URL, a script this
    /// platform can run, or a macro that might resolve to either — a variable in it
    /// resolved first, since that is the word <see cref="Plan"/> will judge. What the editor asks
    /// while the Execute marker is being ticked, so marking something that can never run
    /// is caught there rather than as a toast afterwards. It says nothing about whether
    /// an item <em>should</em> run — only the marker does — and it deliberately does not
    /// read the clipboard: a <c>%C%</c> is taken on trust until the item is actually run.
    /// </summary>
    public static bool LooksExecutable(
        string? text,
        ExecutionPlatform? platform = null,
        EnvironmentProbe? environment = null)
    {
        // Only the first word, never the whole item: the editor asks this on every
        // keystroke in a content box that may be pages long.
        var first = Macros.FirstArgument(text);
        if (first.Length == 0) return false;

        if (Macros.IsPresent(first)) return true;
        if (AsUrl(first) is not null) return true;

        // The word Plan will actually judge, not the word before its variables resolve:
        // the warning beside the marker is there to be read while the marker is being
        // ticked, and a hint that disagrees with Enter is worse than none. It still only
        // ever looks at the first word, so a .bat whose *arguments* Plan will refuse
        // reaches Enter looking fine — that gap is older than the variables and unchanged
        // by them.
        first = (environment ?? EnvironmentProbe.Real).Expand(first);
        if (AsUrl(first) is not null) return true;
        if (HasScheme(first)) return false; // a scheme the allow-list above turned down

        var os = platform ?? CurrentPlatform;
        if (ScriptExtension(first) is { } script) return Supports(script, os);

        return ApplicationExtension(first) is { } application && Supports(application, os);
    }

    /// <summary>
    /// The process that carries out <paramref name="plan"/>, or null if it carries
    /// nothing to run.
    /// </summary>
    /// <param name="isExecutable">
    /// Whether a file carries the execute bit — asked of the caller rather than the file
    /// system so this stays pure. Only consulted for POSIX shell scripts, where an
    /// executable script's <c>#!</c> line should pick its own interpreter.
    /// </param>
    public static LaunchCommand? Resolve(
        ExecutionPlan plan,
        ExecutionPlatform platform,
        Func<string, bool>? isExecutable = null) => plan.Kind switch
    {
        // A link and a document are the same gesture to the OS: hand it the thing and let
        // the user's own default decide what opens it.
        ExecutionKind.Url or ExecutionKind.Document => ShellOpen(plan.Target, platform),
        ExecutionKind.Script => ScriptCommand(plan, platform, isExecutable),
        ExecutionKind.Application => ApplicationCommand(plan, platform),
        ExecutionKind.Folder => platform switch
        {
            // Explorer takes the path as its one argument; open and xdg-open are the same
            // commands a URL uses, since to both a folder is just another thing to open.
            ExecutionPlatform.Windows => new LaunchCommand("explorer.exe", [plan.Target]),
            ExecutionPlatform.MacOS => new LaunchCommand("open", [plan.Target]),
            _ => new LaunchCommand("xdg-open", [plan.Target]),
        },
        ExecutionKind.System => SystemCommand(plan.Action, platform),
        _ => null,
    };

    /// <summary>
    /// What the platform opens something with when it is not told: ShellExecute consults
    /// the user's file associations and default browser, and <c>open</c> / <c>xdg-open</c>
    /// are the same job by another name.
    /// </summary>
    private static LaunchCommand ShellOpen(string target, ExecutionPlatform platform) => platform switch
    {
        ExecutionPlatform.Windows => new LaunchCommand(target, Array.Empty<string>(), UseShellExecute: true),
        ExecutionPlatform.MacOS => new LaunchCommand("open", new[] { target }),
        _ => new LaunchCommand("xdg-open", new[] { target }),
    };

    /// <summary>
    /// The machine-level actions as processes, so the one launcher starts these too
    /// rather than growing a second path with P/Invoke down it.
    ///
    /// Windows carries a documented wrinkle: with hibernation enabled, SetSuspendState
    /// hibernates whatever its first argument says. It is a request and the power policy
    /// decides — turning hibernation off behind the user's back is not Klippy's to do.
    /// </summary>
    private static LaunchCommand? SystemCommand(SystemAction action, ExecutionPlatform platform) =>
        platform switch
        {
            ExecutionPlatform.Windows => action switch
            {
                SystemAction.Lock => new LaunchCommand("rundll32.exe", ["user32.dll,LockWorkStation"]),
                SystemAction.Sleep => new LaunchCommand("rundll32.exe", ["powrprof.dll,SetSuspendState", "0,1,0"]),
                SystemAction.Hibernate => new LaunchCommand("shutdown.exe", ["/h"]),
                // /t 0 skips the minute-long warning the default would put up.
                SystemAction.Restart => new LaunchCommand("shutdown.exe", ["/r", "/t", "0"]),
                _ => null,
            },

            ExecutionPlatform.MacOS => action switch
            {
                // The lock the Apple menu offers — a fast-user-switch suspend, not a
                // display sleep that only locks if "require password" happens to be set.
                SystemAction.Lock => new LaunchCommand(
                    "/System/Library/CoreServices/Menu Extras/User.menu/Contents/Resources/CGSession", ["-suspend"]),
                SystemAction.Sleep => new LaunchCommand("pmset", ["sleepnow"]),
                SystemAction.Restart => new LaunchCommand(
                    "osascript", ["-e", "tell application \"System Events\" to restart"]),
                _ => null, // hibernate: see Supports below
            },

            _ => action switch
            {
                SystemAction.Lock => new LaunchCommand("loginctl", ["lock-session"]),
                SystemAction.Sleep => new LaunchCommand("systemctl", ["suspend"]),
                SystemAction.Hibernate => new LaunchCommand("systemctl", ["hibernate"]),
                SystemAction.Restart => new LaunchCommand("systemctl", ["reboot"]),
                _ => null,
            },
        };

    /// <summary>
    /// Whether this plan asks for its target to be brought up to date first.
    ///
    /// Only a script or an application: those are files kept somewhere, and somewhere is
    /// often a checkout that has moved on since. A link has no working copy, and a folder
    /// is being opened rather than run.
    /// </summary>
    public static bool PullsFirst(ExecutionPlan plan) =>
        plan.PullFirst && plan.Kind is ExecutionKind.Script or ExecutionKind.Application;

    /// <summary>
    /// The pull itself, as a process like any other — <c>-C</c> rather than a working
    /// directory so it is one command with its own target, and no shell reads the line.
    /// </summary>
    public static LaunchCommand PullCommand(string repository) =>
        new("git", ["-C", repository, "pull"]);

    /// <summary>
    /// Whether a platform has the action at all. Only macOS lacks one: hibernation there
    /// is a sleep <em>mode</em> (pmset hibernatemode) rather than something to ask for,
    /// so the word is left as an ordinary search rather than offered and then refused.
    /// </summary>
    public static bool Supports(SystemAction action, ExecutionPlatform platform) =>
        action != SystemAction.None
        && !(action == SystemAction.Hibernate && platform == ExecutionPlatform.MacOS);

    private static LaunchCommand? ApplicationCommand(ExecutionPlan plan, ExecutionPlatform platform) =>
        ApplicationExtension(plan.Target) switch
        {
            // Started directly, with its arguments as arguments — no shell reads this
            // line, so a macro's value can never become a second command. Directly also
            // means Windows resolves a bare "notepad.exe" on PATH, as Run would.
            ".exe" when platform == ExecutionPlatform.Windows =>
                new LaunchCommand(plan.Target, [.. plan.Arguments]),

            // A bundle is a directory rather than a program: `open` is what knows which
            // executable inside it to start, and -a takes the bundle's own path. What
            // follows --args reaches the application as its argv.
            ".app" when platform == ExecutionPlatform.MacOS => plan.Arguments.Length == 0
                ? new LaunchCommand("open", ["-a", plan.Target])
                : new LaunchCommand("open", ["-a", plan.Target, "--args", .. plan.Arguments]),

            // An AppImage is one executable file and runs itself; without the execute bit
            // there is nothing to hand it to, and the start fails saying so.
            ".appimage" when platform == ExecutionPlatform.Linux =>
                new LaunchCommand(plan.Target, [.. plan.Arguments]),

            _ => null,
        };

    private static LaunchCommand? ScriptCommand(
        ExecutionPlan plan,
        ExecutionPlatform platform,
        Func<string, bool>? isExecutable)
    {
        var args = plan.Arguments;
        bool windows = platform == ExecutionPlatform.Windows;

        return ScriptExtension(plan.Target) switch
        {
            ".bat" or ".cmd" when windows =>
                new LaunchCommand("cmd.exe", ["/c", plan.Target, .. args]),

            // -File rather than -Command, so the arguments stay arguments instead of
            // being parsed as more PowerShell. Bypass because the script is one the user
            // keeps in Klippy and has just asked for by name; a machine-wide policy set
            // for downloaded scripts should not silently swallow it.
            ".ps1" when windows =>
                new LaunchCommand("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", plan.Target, .. args]),
            ".ps1" =>
                new LaunchCommand("pwsh", ["-NoProfile", "-File", plan.Target, .. args]),

            // Git Bash / WSL put bash on PATH; without one the start fails and says so.
            ".sh" when windows =>
                new LaunchCommand("bash.exe", [plan.Target, .. args]),

            // An executable script runs itself, so its #! line chooses the interpreter.
            // One that is not executable is handed to sh, which is what running a .sh
            // means when nothing else says otherwise.
            ".sh" => isExecutable?.Invoke(plan.Target) == true
                ? new LaunchCommand(plan.Target, [.. args])
                : new LaunchCommand("/bin/sh", [plan.Target, .. args]),

            _ => null,
        };
    }

    /// <summary>The script types Klippy runs, or null for anything else.</summary>
    public static string? ScriptExtension(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var name = FileNameOf(token);
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return null; // ".sh" on its own is a hidden file, not a script

        var extension = name[dot..].ToLowerInvariant();
        return extension is ".bat" or ".cmd" or ".ps1" or ".sh" ? extension : null;
    }

    /// <summary>
    /// What each platform calls an application, and the one platform it runs on. An
    /// <c>.exe</c> is no more startable on a Mac than a <c>.bat</c> is, so each entry
    /// carries its home rather than being allowed everywhere.
    /// </summary>
    private static readonly (string Extension, ExecutionPlatform Platform)[] Applications =
    {
        (".exe", ExecutionPlatform.Windows),
        (".app", ExecutionPlatform.MacOS),
        (".appimage", ExecutionPlatform.Linux),
    };

    /// <summary>The application types Klippy starts, or null for anything else.</summary>
    public static string? ApplicationExtension(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        // A macOS bundle is a directory, so its path may carry the trailing separator a
        // shell's tab-completion leaves behind.
        var name = FileNameOf(token.TrimEnd(PathSeparators));
        int dot = name.LastIndexOf('.');
        if (dot <= 0) return null; // ".app" on its own is a hidden file, not an application

        var extension = name[dot..].ToLowerInvariant();
        return HomeOf(extension) is null ? null : extension;
    }

    /// <summary>The platform an application extension belongs to, or null if it is not one.</summary>
    private static ExecutionPlatform? HomeOf(string extension)
    {
        foreach (var (candidate, platform) in Applications)
            if (extension == candidate) return platform;
        return null;
    }

    /// <summary>
    /// A folder as a person names it: its last segment, or the whole path when that is
    /// all there is (a drive root, where the last segment is empty).
    /// </summary>
    internal static string FolderName(string path)
    {
        var name = FileNameOf(path.TrimEnd(PathSeparators));
        return name.Length > 0 ? name : path;
    }

    /// <summary>An application as a person names it: Safari, not Safari.app.</summary>
    internal static string ApplicationName(string path)
    {
        var name = FileNameOf(path.TrimEnd(PathSeparators));
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>
    /// The last segment of a path, cut at either separator. Not Path.GetFileName, which
    /// only knows the separator of the machine it is running on — a Windows path named
    /// in a snippet has to read the same when Klippy is looking at it from a Mac.
    /// </summary>
    internal static string FileNameOf(string path)
    {
        int cut = path.LastIndexOfAny(PathSeparators);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>Whether <paramref name="extension"/> can run on <paramref name="platform"/>.</summary>
    public static bool Supports(string extension, ExecutionPlatform platform) => extension switch
    {
        ".bat" or ".cmd" => platform == ExecutionPlatform.Windows,
        ".ps1" or ".sh" => true,
        // An application runs on its own platform and nowhere else; anything that is
        // neither script nor application has no home, and so never runs anywhere.
        _ => HomeOf(extension) == platform,
    };

    /// <summary>
    /// The URL a word stands for, or null if it is not one. A short allow-list on
    /// purpose: these are documents to open, where a scheme like <c>file:</c> — or a
    /// bare path — is a program waiting to be launched under another name.
    /// </summary>
    public static string? AsUrl(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        foreach (var scheme in UrlSchemes)
            if (token.Length > scheme.Length && token.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return token;

        // "www.example.com" is a URL to every browser, so it is one here too.
        return token.Length > WwwPrefix.Length && token.StartsWith(WwwPrefix, StringComparison.OrdinalIgnoreCase)
            ? "https://" + token
            : null;
    }

    /// <summary>
    /// Whether a word opens with a scheme — <c>file:</c>, <c>javascript:</c>, anything
    /// the URL allow-list above already declined — rather than naming a path. A path may
    /// carry a colon, but only ever as a Windows drive letter, so that is the one shape
    /// let through.
    /// </summary>
    private static bool HasScheme(string token)
    {
        int colon = token.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(token[0])) return false;
        if (colon == 1) return false; // C:\tools\build.ps1

        for (int i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(token[i]) && token[i] is not ('+' or '-' or '.'))
                return false;

        return true;
    }

    private static string NameOf(ExecutionPlatform platform) => platform switch
    {
        ExecutionPlatform.Windows => "Windows",
        ExecutionPlatform.MacOS => "macOS",
        _ => "Linux",
    };

    private static ExecutionPlan UrlPlan(string token)
    {
        if (AsUrl(token) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return Nothing($"\"{Ellipsis(token)}\" is not a URL Klippy can open.");

        return new ExecutionPlan { Kind = ExecutionKind.Url, Target = url };
    }

    private static ExecutionPlan Nothing(string problem) => new() { Problem = problem };

    private static bool IsBatch(string extension) => extension is ".bat" or ".cmd";

    private static string? FindUnsafeArgument(string[] arguments)
    {
        foreach (var argument in arguments)
            if (argument.IndexOfAny(CmdMetaCharacterSet) >= 0)
                return argument;
        return null;
    }

    private static bool ContainsWhitespace(string text)
    {
        foreach (var c in text)
            if (char.IsWhiteSpace(c)) return true;
        return false;
    }

    /// <summary>
    /// Whether the first word's macro filled in something with a space in it — the shape
    /// that means a whole command line arrived through a <c>%C%</c> and has to be read
    /// back apart.
    ///
    /// Asked of the word as it was written rather than after its variables have resolved.
    /// A <c>%ProgramFiles%</c> puts a space in the first word without anything having been
    /// handed back, and cutting the line there would leave <c>C:\Program</c> as the
    /// command — the very mistake the quoting rule exists to avoid. Expanded through the
    /// whole line rather than that one word, so the last <c>%P%</c> still takes exactly
    /// the arguments the others leave over.
    /// </summary>
    private static bool MacroBroughtAWholeLine(
        string[] tokens, string typed, IReadOnlyList<string>? arguments, string? clipboardText)
    {
        if (!Macros.IsPresent(typed)) return false;

        var written = (string[])tokens.Clone();
        written[0] = typed;
        return ContainsWhitespace(Macros.ExpandAll(written, arguments, clipboardText)[0]);
    }

    /// <summary>Keeps a whole snippet out of a one-line message.</summary>
    private static string Ellipsis(string text, int max = 40) =>
        text.Length <= max ? text : text[..max] + "…";
}
