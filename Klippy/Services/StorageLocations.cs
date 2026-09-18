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
/// </summary>
public static class StorageLocations
{
    public const string FileName = "snippets.json";
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// The platform's own app-data folder for Klippy: <c>%APPDATA%\Klippy</c> on Windows,
    /// <c>~/.config/Klippy</c> elsewhere. Where Klippy looks before it is told otherwise,
    /// and where <see cref="SettingsPath"/> stays whatever it is told.
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Klippy");

    /// <summary>Normal, backed-up location for Klippy's data. Defaults to <see cref="DefaultDirectory"/>.</summary>
    public static string Directory { get; set; } = DefaultDirectory;

    /// <summary>
    /// Directory the platform excludes from backup, or null if it has no such concept.
    /// Set by the platform head before the app starts.
    /// </summary>
    public static string? BackupExemptDirectory { get; set; }

    /// <summary>Whether this platform can offer the "wipe on uninstall" choice at all.</summary>
    public static bool SupportsBackupOptOut => BackupExemptDirectory is not null;

    public static string BackedUpPath => Path.Combine(Directory, FileName);

    /// <summary>
    /// Preferences live in <see cref="DefaultDirectory"/>, never the backup-exempt one and
    /// never a redirected one: the "wipe on uninstall" choice is about snippet data, not
    /// settings — and settings.json is the file that *names* the redirected folder, so it
    /// is the one thing that cannot move into it.
    /// </summary>
    public static string SettingsPath => Path.Combine(DefaultDirectory, SettingsFileName);

    /// <summary>
    /// Captured clipboard history. Always the normal directory: history is a desktop-only
    /// feature (Android forbids background clipboard reads), and desktop has no
    /// backup-exempt location to choose between.
    /// </summary>
    public static string HistoryPath => Path.Combine(Directory, "history.json");

    public static string? BackupExemptPath =>
        BackupExemptDirectory is { } dir ? Path.Combine(dir, FileName) : null;

    /// <summary>
    /// The file to open: the backup-exempt copy when one exists, otherwise the normal one.
    /// </summary>
    public static string ActivePath =>
        BackupExemptPath is { } exempt && File.Exists(exempt) ? exempt : BackedUpPath;

    /// <summary>
    /// Resolves a configured file name: a bare name (or relative path) lands in
    /// <see cref="Directory"/> beside the snippets, an absolute one is taken as given.
    /// Environment variables and a leading <c>~</c> are expanded first, because that is
    /// how people write paths into a file by hand.
    /// </summary>
    public static string Resolve(string fileName)
    {
        var expanded = ExpandPath(fileName);
        if (expanded.Length == 0) return Directory;
        return Path.IsPathRooted(expanded) ? expanded : Path.Combine(Directory, expanded);
    }

    /// <summary>
    /// <c>%APPDATA%\Klippy</c> and <c>~/Klippy</c> are how a hand-edited path is written,
    /// and .NET expands neither on its own. Environment variables only — Klippy's own
    /// variables file lives *inside* the folder this decides, so it cannot help name it.
    /// </summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith(@"~\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        return expanded;
    }

    /// <summary>
    /// Points snippets, history, clip blobs and the variables file at
    /// <paramref name="directory"/>. Settings stay in <see cref="DefaultDirectory"/>, as
    /// <see cref="SettingsPath"/> explains.
    ///
    /// Nothing is moved for the caller: a redirect that quietly relocated files would be
    /// the one operation in Klippy that can lose data, and the old folder left untouched
    /// is what makes changing your mind free. Returns false, with <paramref name="problem"/>
    /// saying why, when the path is unusable — a mistyped folder must cost the preference
    /// and not the snippets.
    /// </summary>
    public static bool TryUseDirectory(string? directory, out string problem)
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
            problem = $"\"{expanded}\" is not an absolute path";
            return false;
        }

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
}
