using System;
using System.IO;

namespace Klippy.Services;

/// <summary>
/// Where the snippet store lives, and whether that location participates in the
/// platform's device backup.
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

    /// <summary>Normal, backed-up location. Defaults to the platform app-data folder.</summary>
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "Klippy");

    /// <summary>
    /// Directory the platform excludes from backup, or null if it has no such concept.
    /// Set by the platform head before the app starts.
    /// </summary>
    public static string? BackupExemptDirectory { get; set; }

    /// <summary>Whether this platform can offer the "wipe on uninstall" choice at all.</summary>
    public static bool SupportsBackupOptOut => BackupExemptDirectory is not null;

    public static string BackedUpPath => Path.Combine(Directory, FileName);

    /// <summary>
    /// Preferences always live in the normal directory, never the backup-exempt one:
    /// the "wipe on uninstall" choice is about snippet data, not settings.
    /// </summary>
    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    public static string? BackupExemptPath =>
        BackupExemptDirectory is { } dir ? Path.Combine(dir, FileName) : null;

    /// <summary>
    /// The file to open: the backup-exempt copy when one exists, otherwise the normal one.
    /// </summary>
    public static string ActivePath =>
        BackupExemptPath is { } exempt && File.Exists(exempt) ? exempt : BackedUpPath;
}
