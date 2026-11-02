using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Klippy.Services;

namespace Klippy.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // A global hotkey only makes sense with exactly one listener, and a second copy
        // would fight the first for the key and for snippets.json. Bail out early if one
        // is already resident — the running instance still answers the hotkey.
        using var single = new Mutex(initiallyOwned: true, "Klippy.SingleInstance.v1", out bool isFirst);
        if (!isFirst)
        {
            Console.Error.WriteLine("Klippy is already running — use the hotkey or tray icon.");
            return 0;
        }

        // Current, not Load(): the settings overlay writes through the same instance, so a
        // second copy here would go stale the moment a preference changed.
        var settings = AppSettings.Current;

        // Before anything opens a file: the history service, the snippet store, the
        // command MRU and the variables file all resolve their paths against
        // StorageLocations, and each of them can be named separately.
        if (!StorageLocations.TryApply(settings, out var problem))
            Console.Error.WriteLine(
                $"Ignoring \"DataDirectory\" in {AppSettings.FilePath}: {problem}. " +
                $"Using {StorageLocations.Directory}.");

        // After the folder is settled and before anything opens a file: 1.0.20 and earlier
        // wrote the history, the command MRU and the defines under other names, and an
        // upgrade should carry them across rather than start empty beside them.
        StorageLocations.MigrateLegacyNames(settings);

        // Built before the app, because OnFrameworkInitializationCompleted constructs the
        // view models and they need to know whether there is a history to show.
        // File and image clips need clipboard formats Avalonia cannot express; text and
        // HTML still go through Avalonia, which is what the Markdown flavours rely on.
        if (OperatingSystem.IsWindows())
            RichTextClipboard.NativeWriter = WindowsClipboardWriter.TryWriteAsync;

        // Start at login, where this platform has a way to keep one: Settings offers the
        // toggle only when this is set.
        LoginItem.Current = LoginItemForThisCopy();

        using var history = new ClipboardHistoryService(settings);
        ClipboardHistory.Store = settings.HistoryEnabled ? history.Store : null;

        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            // Hiding the window must not end the process, or the hotkey has nothing to show.
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        BuildAvaloniaApp().SetupWithLifetime(lifetime);

        // A login start comes up put away, behind the tray icon, as if it had been dismissed:
        // nobody asked for a window just by logging in.
        using var host = new LauncherHost(lifetime, settings, startHidden: LoginItem.StartsInBackground(args));
        host.Start();

        // After the app is up: the flush timer needs Avalonia's dispatcher.
        history.Start();

        if (settings.HistoryEnabled && !history.IsCapturing)
            Console.Error.WriteLine(
                "Clipboard history is on but this platform could not start capture; " +
                "snippets still work.");

        if (settings.HotkeyEnabled && !host.HotkeyRegistered)
            Console.Error.WriteLine(
                $"Could not register the global hotkey ({settings.ParsedHotkey}); another app may hold it. " +
                $"Change \"Hotkey\" in {AppSettings.FilePath} and restart.");

        // Reported separately: the two register independently, so losing one key does not
        // cost the other, and saying which failed is the difference between the two.
        if (settings.HotkeyEnabled && ClipboardHistory.IsAvailable &&
            settings.ParsedHistoryHotkey is { } historyHotkey && !host.HistoryHotkeyRegistered)
            Console.Error.WriteLine(
                $"Could not register the clipboard-history hotkey ({historyHotkey}); another app may hold it. " +
                $"Change \"HistoryHotkey\" in {AppSettings.FilePath} and restart.");

        return lifetime.Start(args);
    }

    /// <summary>
    /// How this copy would start at login, or null where it cannot say. Not when run through
    /// the dotnet host — <c>dotnet Klippy.Desktop.dll</c> — since the program then is dotnet
    /// itself, and a login that started it would start nothing of Klippy's.
    /// </summary>
    private static ILoginItem? LoginItemForThisCopy()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path) ||
            string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            return null;

        if (OperatingSystem.IsWindows()) return new WindowsLoginItem(path);
        if (OperatingSystem.IsMacOS())
            return new LaunchAgentLoginItem(
                LoginItem.MacPlistPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), path);
        return null;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // LSUIElement in Info.plist is not enough on its own: Avalonia sets the
            // activation policy at startup from this option, and its default puts Klippy
            // back in the Dock and the Cmd-Tab list. Only read on macOS.
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
