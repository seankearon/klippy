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

    /// <summary>Why nothing can be run, when <see cref="Kind"/> is None. Shown to the user.</summary>
    public string Problem { get; init; } = "";

    /// <summary>One line saying what is about to happen, for the toast.</summary>
    public string Description => Kind switch
    {
        ExecutionKind.Url => "Opening " + Shorten(Target),
        ExecutionKind.Script => "Running " + ExecutionPolicy.FileNameOf(Target),
        ExecutionKind.Application => "Starting " + ExecutionPolicy.ApplicationName(Target),
        ExecutionKind.Folder => "Opening " + ExecutionPolicy.FolderName(Target),
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
/// would carry that out. Pure: no file system, no processes, no Win32 — the platform is
/// a parameter rather than something read from the environment, so every rule below is
/// testable on any machine. <see cref="ProcessLauncher"/> does the actual starting.
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
/// bare <c>docker system prune -af</c> is text however firmly it is marked to run.
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
    /// </summary>
    private const string CmdMetaCharacters = "&|<>^\"%";

    private static readonly char[] CmdMetaCharacterSet = CmdMetaCharacters.ToCharArray();

    public static ExecutionPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? ExecutionPlatform.Windows
        : OperatingSystem.IsMacOS() ? ExecutionPlatform.MacOS
        : ExecutionPlatform.Linux;

    /// <summary>
    /// Works out what running <paramref name="text"/> would mean, filling its macros
    /// from <paramref name="arguments"/> and <paramref name="clipboardText"/> first.
    /// Never throws: anything it cannot run comes back as
    /// <see cref="ExecutionKind.None"/> with a <see cref="ExecutionPlan.Problem"/>.
    /// </summary>
    public static ExecutionPlan Plan(
        string? text,
        IReadOnlyList<string>? arguments = null,
        string? clipboardText = null,
        ExecutionPlatform? platform = null)
    {
        var os = platform ?? CurrentPlatform;
        var tokens = Macros.SplitArguments(text);
        if (tokens.Length == 0) return Nothing("There is nothing to run.");

        // A URL the item itself declares has its macro values percent-encoded, so a
        // multi-word argument lands in the query string instead of breaking the URL in
        // two. A URL that arrives *through* a macro is taken exactly as it stands —
        // encoding that would turn its own slashes into %2F.
        if (AsUrl(tokens[0]) is not null)
            return UrlPlan(Macros.Expand(tokens[0], arguments, clipboardText, Uri.EscapeDataString));

        var parts = new List<string>(Macros.ExpandAll(tokens, arguments, clipboardText));
        if (parts.Count == 0 || parts[0].Length == 0) return Nothing("There is nothing to run.");

        // A link is used whole, spaces and all: a URL that came off the clipboard with a
        // space in it is still one URL, and cutting it at the space opens the wrong page.
        if (AsUrl(parts[0]) is not null) return UrlPlan(parts[0]);

        // A macro can otherwise hand back a whole command line — "%C%" with a script path
        // and its switches sitting on the clipboard. Split that apart again so its first
        // word is the command and the rest are arguments, as if it had been typed. Only
        // when a macro produced it: a quoted path is one path however many spaces it has.
        if (Macros.IsPresent(tokens[0]) && ContainsWhitespace(parts[0]))
        {
            var head = Macros.SplitArguments(parts[0]);
            parts.RemoveAt(0);
            parts.InsertRange(0, head);
            if (parts.Count == 0 || parts[0].Length == 0) return Nothing("There is nothing to run.");
        }

        var command = parts[0];
        var rest = parts.GetRange(1, parts.Count - 1).ToArray();

        // Something carrying a scheme is not a path, whatever it happens to end in:
        // file:///C:/Windows/System32/cmd.exe names an .exe without being one, and
        // javascript: names nothing at all. Neither survived the URL allow-list above,
        // and neither may sneak back in through the extension rules below.
        if (HasScheme(command))
            return Nothing($"\"{Ellipsis(command)}\" is not a URL Klippy can open.");

        if (ScriptExtension(command) is { } script)
        {
            if (!Supports(script, os))
                return Nothing($"{script} scripts only run on Windows.");

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
    /// Whether text has any chance of running: its first word is a URL, a script this
    /// platform can run, or a macro that might resolve to either. What the editor asks
    /// while the Execute marker is being ticked, so marking something that can never run
    /// is caught there rather than as a toast afterwards. It says nothing about whether
    /// an item <em>should</em> run — only the marker does — and it deliberately does not
    /// read the clipboard: a <c>%C%</c> is taken on trust until the item is actually run.
    /// </summary>
    public static bool LooksExecutable(string? text, ExecutionPlatform? platform = null)
    {
        // Only the first word, never the whole item: the editor asks this on every
        // keystroke in a content box that may be pages long.
        var first = Macros.FirstArgument(text);
        if (first.Length == 0) return false;

        if (Macros.IsPresent(first)) return true;
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
        ExecutionKind.Url => platform switch
        {
            // ShellExecute is what consults the user's default browser; macOS and Linux
            // have a command that does the same job.
            ExecutionPlatform.Windows => new LaunchCommand(plan.Target, Array.Empty<string>(), UseShellExecute: true),
            ExecutionPlatform.MacOS => new LaunchCommand("open", new[] { plan.Target }),
            _ => new LaunchCommand("xdg-open", new[] { plan.Target }),
        },
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

    /// <summary>Keeps a whole snippet out of a one-line message.</summary>
    private static string Ellipsis(string text, int max = 40) =>
        text.Length <= max ? text : text[..max] + "…";
}
