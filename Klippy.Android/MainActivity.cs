using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Klippy.Services;
using System.IO;

namespace Klippy.Android;

[Activity(
    Label = "Klippy",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Runs before the app (and its store) is created. NoBackupFilesDir is the
        // directory Android deliberately keeps out of Auto Backup and device transfer,
        // which is what makes "wipe on uninstall" actually stick.
        if (NoBackupFilesDir?.AbsolutePath is { } noBackup)
            StorageLocations.BackupExemptDirectory = Path.Combine(noBackup, "Klippy");

        // Avalonia's Android clipboard can't carry an HTML flavour, so Markdown snippets
        // would paste as literal asterisks. Hand copying to the platform API instead.
        RichTextClipboard.PlatformWriter = new AndroidRichTextClipboard(this).WriteAsync;

        return base.CustomizeAppBuilder(builder);
    }
}
