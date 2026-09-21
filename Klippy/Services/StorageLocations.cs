using System;
using System.IO;

namespace Klippy.Services;

/// <summary>
/// Where Klippy's files live, and whether that location participates in the platform's
/// device backup.
///
/// Uninstalling an app always deletes its private storage, so what actually decides
/// whether snippets survive an uninstall/reinstall is the *backup*. Android gives every
/// app a "no backup" directory that is deliberately excluded from Auto Backup and
/// device-to-device transfer; putting the store there is what "wipe on uninstall" means
/// in practice. Platform heads supply that directory; where a platform has no such
/// notion (desktop) it stays null and the option is hidden.
///
/// The active location is inferred from where the file actually is, so the choice needs
/// no separate settings file that could disagree with reality.
///
/// Each file is named on its own — snippets, history, the command MRU, the variables
/// file — against a folder they fall back to rather than one they are confined to. That
/// is what lets one snippet store be shared between a Mac and a Windows box while the
/// variables file that translates it stays on each machine.
/// </summary>
public static class StorageLocations
{
    public const string FileName = "snippets.json";
    public const string SettingsFileName = "settings.json";
    public const string HistoryFileName = "history.json";
    public const string CommandsFileName = "commands.json";

    /// <summary>
    /// The platform's own app-data folder for Klippy: <c>%APPDATA%\Klippy</c> on Windows,
    /// <c>~/.config/Klippy</c> elsewhere. Where Klippy looks before it is told otherwise,
    /// and where <see cref="SettingsPath"/> stays whatever it is told.
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Klippy");

    /// <summary>
    /// Normal, backed-up location for Klippy's data. Defaults to
    /// <see cref="DefaultDirectory"/>, and is where a file named by a bare name lands —
    /// the folder each of the settings below falls back to rather than a folder that
    /// overrules them.
    /// </summary>
    public static string Directory { get; set; } = DefaultDirectory;

    /// <summary>
    /// The snippet store, as settings name it. Blank is <see cref="FileName"/> in
    /// <see cref="Directory"/>, a bare name is another file in that folder, and an
    /// absolute path is taken as given.
    ///
    /// One setting per file rather than one folder for all of them, because the files
    /// want different answers: the snippets are worth syncing between two machines, and
    /// the variables file — the one that says where <c>%ws%</c> is on *this* one — is
    /// precisely what must not follow them there.
    ///
    /// Set once at startup by <see cref="TryApply"/>, since a store that changed file
    /// mid-session would have to decide what to do with the one it already had open.
    /// </summary>
    public static string SnippetsFile { get; set; } = "";

    /// <inheritdoc cref="SnippetsFile"/>
    public static string HistoryFile { get; set; } = "";

    /// <inheritdoc cref="SnippetsFile"/>
    public static string CommandsFile { get; set; } = "";

    /// <summary>
    /// Directory the platform excludes from backup, or null if it has no such concept.
    /// Set by the platform head before the app starts.
    /// </summary>
    public static string? BackupExemptDirectory { get; set; }

    /// <summary>
    /// Whether this platform can offer the "wipe on uninstall" choice at all — and
    /// whether there is still a choice to make. Naming the snippets file settles where it
    /// lives, so the toggle would have nowhere to move it to that the setting would not
    /// immediately name back.
    /// </summary>
    public static bool SupportsBackupOptOut => BackupExemptPath is not null;

    /// <summary>The snippet store: <see cref="SnippetsFile"/> resolved.</summary>
    public static string BackedUpPath => ResolveFile(SnippetsFile, FileName);

    /// <summary>
    /// Preferences live in <see cref="DefaultDirectory"/>, never the backup-exempt one and
    /// never a redirected one: the "wipe on uninstall" choice is about snippet data, not
    /// settings — and settings.json is the file that *names* the redirected folder, so it
    /// is the one thing that cannot move into it.
    /// </summary>
    public static string SettingsPath => Path.Combine(DefaultDirectory, SettingsFileName);

    /// <summary>
    /// Captured clipboard history: <see cref="HistoryFile"/> resolved. Never the
    /// backup-exempt directory — history is a desktop-only feature (Android forbids
    /// background clipboard reads), and desktop has no backup-exempt location to choose
    /// between. Clip images go in a <c>clips</c> folder beside whichever file this is,
    /// because a blob is only meaningful next to the entry that names it.
    /// </summary>
    public static string HistoryPath => ResolveFile(HistoryFile, HistoryFileName);

    /// <summary>
    /// The command MRU: <see cref="CommandsFile"/> resolved. Never the backup-exempt
    /// directory, for the reason the settings are not: the "wipe on uninstall" choice is
    /// about snippet data, and a list of lines typed into the search box is not that.
    /// </summary>
    public static string CommandsPath => ResolveFile(CommandsFile, CommandsFileName);

    /// <summary>
    /// Where a backup-exempt snippet store would sit, or null when there is no such place
    /// or no longer any point: a <see cref="SnippetsFile"/> that names a path has already
    /// answered the question this directory exists to ask.
    /// </summary>
    public static string? BackupExemptPath =>
        BackupExemptDirectory is { } dir && string.IsNullOrWhiteSpace(SnippetsFile)
            ? Path.Combine(dir, FileName)
            : null;

    /// <summary>
    /// The file to open: the backup-exempt copy when one exists, otherwise the normal one.
    /// </summary>
    public static string ActivePath =>
        BackupExemptPath is { } exempt && File.Exists(exempt) ? exempt : BackedUpPath;

    /// <summary>
    /// Resolves a configured file name: a bare name (or relative path) lands in
    /// <see cref="Directory"/> beside the snippets, an absolute one is taken as given.
    /// <see cref="ExpandPath"/> runs over it first, because a hand-edited path is written
    /// with the shorthands rather than spelled out.
    /// </summary>
    public static string Resolve(string fileName)
    {
        var expanded = ExpandPath(fileName);
        if (expanded.Length == 0) return Directory;
        return Path.IsPathRooted(expanded) ? expanded : Path.Combine(Directory, expanded);
    }

    /// <summary>
    /// Resolves one of the configured file names, falling back to
    /// <paramref name="defaultName"/> when nothing is set. There is no such thing as "no
    /// snippets file": a setting left alone asks for the usual name in the usual folder,
    /// not for no file at all.
    /// </summary>
    public static string ResolveFile(string? configured, string defaultName) =>
        Resolve(string.IsNullOrWhiteSpace(configured) ? defaultName : configured);

    /// <summary>
    /// <c>%APPDATA%\Klippy</c>, <c>$HOME/Klippy</c> and <c>~/Klippy</c> are how a
    /// hand-edited path is written, and .NET expands none of them on its own.
    ///
    /// Through <see cref="EnvironmentProbe"/>, so a path in settings.json reads exactly as
    /// one typed into the search box or written into an item. The environment only —
    /// Klippy's own variables file lives *inside* the folder this decides, so it cannot
    /// help name it.
    ///
    /// The separator is settled here rather than in <see cref="EnvironmentProbe"/>, whose
    /// <c>Expand</c> is handed the environment instead of asking the machine it is running
    /// on: it has to leave <c>/home/sam/src</c> alone while running on Windows, because a
    /// rule about one platform is routinely tested from another. This method is only ever
    /// about this machine, so <c>~/Klippy</c> and <c>%USERPROFILE%/Klippy</c> arrive
    /// spelled the way everything else here spells a path. What is given up for that is
    /// the user's own spelling — see <see cref="NormalizeSeparators"/> for how little of it.
    ///
    /// Not <c>Path.GetFullPath</c>, which would root a relative path against the working
    /// directory and quietly defeat both the refusal in <see cref="IsUsableDirectory"/>
    /// and the beside-the-snippets branch of <see cref="Resolve"/>.
    /// </summary>
    public static string ExpandPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? ""
            : NormalizeSeparators(EnvironmentProbe.Real.Expand(path.Trim()));

    /// <summary>
    /// A path spelled the way this machine spells one. The separator only: nothing is
    /// resolved, rooted or expanded, so a bare name stays a bare name and a <c>~</c> stays
    /// a tilde — which is what makes it safe to run over a path a person typed, in order to
    /// compare it with one Klippy resolved.
    ///
    /// One character, one direction. On Unix the two are already the same character and
    /// this does nothing at all; the mirror image of it would be a bug, a backslash being
    /// an ordinary character in a Unix file name rather than a separator.
    /// </summary>
    public static string NormalizeSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>
    /// Makes <paramref name="directory"/> the folder Klippy's files sit in unless one of
    /// them says otherwise — snippets, history, clip blobs and the variables file.
    /// Settings stay in <see cref="DefaultDirectory"/>, as <see cref="SettingsPath"/>
    /// explains.
    ///
    /// Nothing is moved for the caller: a redirect that quietly relocated files would be
    /// the one operation in Klippy that can lose data, and the old folder left untouched
    /// is what makes changing your mind free. Returns false, with <paramref name="problem"/>
    /// saying why, when the path is unusable — a mistyped folder must cost the preference
    /// and not the snippets.
    /// </summary>
    public static bool TryUseDirectory(string? directory, out string problem)
    {
        if (!IsUsableDirectory(directory, out problem)) return false;

        var expanded = ExpandPath(directory);
        try
        {
            System.IO.Directory.CreateDirectory(expanded);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            problem = $"\"{expanded}\" could not be created ({e.Message})";
            return false;
        }

        Directory = expanded;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is a folder Klippy could keep its files in —
    /// asked without answering, so the Settings screen can say what is wrong with a path
    /// while it is being typed. Nothing is created and nothing moves: the folder is only
    /// taken up at startup, by <see cref="TryUseDirectory"/>.
    /// </summary>
    public static bool IsUsableDirectory(string? directory, out string problem)
    {
        problem = "";
        var expanded = ExpandPath(directory);
        if (expanded.Length == 0)
        {
            problem = $"\"{directory}\" expands to nothing";
            return false;
        }
        if (!Path.IsPathRooted(expanded))
        {
            // Relative to what? The exe, the shell's working directory and the app-data
            // folder are three different answers, so the setting insists on being told.
            problem = $"\"{expanded}\" is not an absolute path";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Points Klippy at the files <paramref name="settings"/> name: the folder first, then
    /// each file that names its way out of it. Called once at startup, before anything
    /// opens a file.
    ///
    /// Returns false, with <paramref name="problem"/> saying why, when the folder is
    /// unusable — a mistyped folder must cost that preference and not the snippets. The
    /// per-file names cannot fail here: a name only becomes a path when something opens
    /// it, and a file that will not open is reported by whatever wanted it rather than
    /// costing the rest of the layout.
    /// </summary>
    public static bool TryApply(AppSettings settings, out string problem)
    {
        SnippetsFile = settings.SnippetsFile;
        HistoryFile = settings.HistoryFile;
        CommandsFile = settings.CommandsFile;

        problem = "";
        return string.IsNullOrWhiteSpace(settings.DataDirectory)
               || TryUseDirectory(settings.DataDirectory, out problem);
    }
}
