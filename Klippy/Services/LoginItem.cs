using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Klippy.Services;

/// <summary>What this machine starts for Klippy when you log in.</summary>
/// <param name="Program">The program it starts: the executable on Windows, the app bundle on macOS.</param>
/// <param name="IsThisCopy">
/// Whether that is the copy of Klippy now running. A portable copy, a development build, or an install that has
/// since moved leaves a registration naming somewhere else, and the setting says so rather than claiming it is on.
/// </param>
public sealed record LoginRegistration(string Program, bool IsThisCopy);

/// <summary>
/// Starting Klippy when you log in, as the platform keeps it. The platform's own record is the setting — there is no
/// key in settings.json to disagree with it — so turning it off in Task Manager or deleting the LaunchAgent shows
/// in Settings as off, and nothing turns it back on behind your back.
/// </summary>
public interface ILoginItem
{
    /// <summary>What starts at login now, or null when nothing does. Never throws.</summary>
    LoginRegistration? Read();

    /// <summary>Starts this copy of Klippy at login, in the background, replacing whatever was there.</summary>
    void Register();

    /// <summary>Stops Klippy starting at login.</summary>
    void Unregister();
}

/// <summary>
/// The parts of starting at login that are the same everywhere, or that are only text: the argument a login start
/// carries, the command line Windows keeps, and the LaunchAgent macOS reads. Pure apart from
/// <see cref="LaunchAgentLoginItem"/>, which is a file and nothing more.
/// </summary>
public static class LoginItem
{
    /// <summary>
    /// What a login start passes, so Klippy comes up put away behind its tray or menu-bar icon rather than
    /// opening its window over whatever the desktop is doing. It is also how to start it that way by hand.
    /// </summary>
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// The registration for this platform and this copy, set by the desktop head where it knows how to keep one —
    /// Windows and macOS. Null leaves the setting out of Settings: mobile has no login to start at, and Linux
    /// desktops each keep their own.
    /// </summary>
    public static ILoginItem? Current { get; set; }

    /// <summary>Whether Klippy was started to sit in the background — by a login start, or by hand.</summary>
    public static bool StartsInBackground(IEnumerable<string>? args) =>
        args?.Any(a => string.Equals(a, BackgroundArgument, StringComparison.OrdinalIgnoreCase)) == true;

    // ---- Windows: a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run ----

    /// <summary>The value's name, which is also what Task Manager's Startup apps list shows.</summary>
    public const string WindowsValueName = "Klippy";

    /// <summary>
    /// The command line Windows runs at login. Quoted always, since Program Files has a space in it and an unquoted
    /// path there is the classic way to start <c>C:\Program.exe</c> instead.
    /// </summary>
    public static string WindowsCommand(string program) => $"\"{program}\" {BackgroundArgument}";

    /// <summary>
    /// The program a Run value starts: the quoted part, or the first word where nothing is quoted — written by hand,
    /// or by something other than Klippy. Null for an empty value.
    /// </summary>
    public static string? WindowsProgram(string? command)
    {
        var text = command?.Trim() ?? "";
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            var quoted = close < 0 ? text[1..] : text[1..close];
            return quoted.Length > 0 ? quoted : null;
        }

        int space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? text : text[..space];
    }

    /// <summary>
    /// Whether Task Manager's Startup apps — or Settings' — has switched the value off. It records that beside the
    /// Run value, under <c>Explorer\StartupApproved\Run</c>, as twelve bytes whose first one is even while the
    /// entry is enabled and odd once it is disabled. No record at all means nobody has touched it: enabled.
    /// </summary>
    public static bool IsDisabledByWindows(byte[]? startupApproved) =>
        startupApproved is { Length: > 0 } && (startupApproved[0] & 1) == 1;

    // ---- macOS: a LaunchAgent in ~/Library/LaunchAgents ----

    /// <summary>
    /// The LaunchAgent's label, and its file's name. The bundle identifier and what the agent is for, so it is not
    /// mistaken for the app itself in System Settings' Login Items.
    /// </summary>
    public const string MacLabel = "com.seankearon.klippy.login";

    /// <summary>Where a user's own LaunchAgents live; launchd reads them at every login.</summary>
    public static string MacPlistPath(string home) =>
        Path.Combine(home, "Library", "LaunchAgents", MacLabel + ".plist");

    /// <summary>
    /// What the agent runs. For an app bundle, <c>open</c>: it is LaunchServices that starts an app as an app, and
    /// <c>-g</c> keeps it from taking the foreground at login. For a bare executable — a development build, say —
    /// the executable itself. The background argument either way, after <c>--args</c> where <c>open</c> is the one
    /// reading the line.
    /// </summary>
    public static string[] MacProgramArguments(string processPath)
    {
        var bundle = ExecutionPolicy.BundleOf(processPath);
        return bundle is not null
            ? ["/usr/bin/open", "-g", "-a", bundle, "--args", BackgroundArgument]
            : [processPath, BackgroundArgument];
    }

    /// <summary>The program those arguments start: the bundle <c>open -a</c> is given, or the first word.</summary>
    public static string? MacProgram(IReadOnlyList<string>? arguments)
    {
        if (arguments is not { Count: > 0 }) return null;
        if (arguments[0] != "/usr/bin/open") return arguments[0];

        for (int i = 1; i + 1 < arguments.Count; i++)
            if (arguments[i] == "-a")
                return arguments[i + 1];
        return null;
    }

    /// <summary>
    /// The LaunchAgent itself. RunAtLoad starts it once at login and nothing more: no KeepAlive, so quitting Klippy
    /// leaves it quit until the next login, as quitting any app does.
    /// </summary>
    public static string MacPlist(IReadOnlyList<string> programArguments)
    {
        var xml = new StringBuilder();
        xml.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>
            """.Replace("\r\n", "\n"));
        xml.Append(Escape(MacLabel)).Append("</string>\n");
        xml.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var argument in programArguments)
            xml.Append("    <string>").Append(Escape(argument)).Append("</string>\n");
        xml.Append("  </array>\n  <key>RunAtLoad</key>\n  <true/>\n</dict>\n</plist>\n");
        return xml.ToString();
    }

    /// <summary>
    /// The ProgramArguments of a LaunchAgent, or null when the text is not one Klippy can read. The DTD is ignored
    /// rather than fetched: the file is read to find a path, not validated.
    /// </summary>
    public static string[]? MacProgramArguments(TextReader plist)
    {
        try
        {
            using var reader = XmlReader.Create(plist,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var entries = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToList();
            if (entries is null) return null;

            for (int i = 0; i + 1 < entries.Count; i++)
                if (entries[i].Name == "key" && entries[i].Value == "ProgramArguments" && entries[i + 1].Name == "array")
                    return [.. entries[i + 1].Elements("string").Select(e => e.Value)];
        }
        catch (XmlException)
        {
            // Not a plist: nothing Klippy registered, as far as it can tell.
        }

        return null;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Whether two paths name the same program. Without regard to case, since both Windows' and macOS' default
    /// file systems ignore it, and a registration that differs from this copy only by a capital letter is this copy.
    /// </summary>
    public static bool SameProgram(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Start at login as a macOS LaunchAgent: one plist in <c>~/Library/LaunchAgents</c>, read by launchd at every
/// login. Rather than <c>SMAppService</c>, which only exists from macOS 13 — Klippy runs on 12 — and starts the app
/// with no arguments, so a login start could not be told from any other and would open the window at login.
///
/// Nothing is loaded into launchd now: the agent's work is the next login, and loading it here would start a second
/// Klippy for the single-instance check to turn away. Since macOS 13 the system notes the new agent with a
/// notification and lists it under Login Items, where it can be switched off as well as here.
/// </summary>
public sealed class LaunchAgentLoginItem(string plistPath, string processPath) : ILoginItem
{
    /// <summary>What this copy is, as the agent would name it: its bundle, or its executable outside one.</summary>
    private string ThisCopy => ExecutionPolicy.BundleOf(processPath) ?? processPath;

    public LoginRegistration? Read()
    {
        try
        {
            if (!File.Exists(plistPath)) return null;
            using var reader = File.OpenText(plistPath);
            return LoginItem.MacProgram(LoginItem.MacProgramArguments(reader)) is { } program
                ? new LoginRegistration(program, LoginItem.SameProgram(program, ThisCopy))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Register()
    {
        var dir = Path.GetDirectoryName(plistPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = plistPath + ".tmp";
        File.WriteAllText(tmp, LoginItem.MacPlist(LoginItem.MacProgramArguments(processPath)));
        AtomicFile.Replace(tmp, plistPath);
    }

    public void Unregister()
    {
        if (File.Exists(plistPath)) File.Delete(plistPath);
    }
}
