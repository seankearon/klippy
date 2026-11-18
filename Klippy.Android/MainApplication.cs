using System;
using System.IO;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Klippy.Services;

namespace Klippy.Android;

/// <summary>
/// Starts Avalonia with the process rather than with an activity: from Avalonia 12 the
/// app builder belongs to the Android <c>Application</c>, and <see cref="MainActivity"/> only
/// hosts the view.
/// </summary>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    protected MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

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
