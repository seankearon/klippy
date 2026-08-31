using System;
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

        var settings = AppSettings.Load();

        // Built before the app, because OnFrameworkInitializationCompleted constructs the
        // view models and they need to know whether there is a history to show.
        // File and image clips need clipboard formats Avalonia cannot express; text and
        // HTML still go through Avalonia, which is what the Markdown flavours rely on.
        if (OperatingSystem.IsWindows())
            RichTextClipboard.NativeWriter = WindowsClipboardWriter.TryWriteAsync;

        using var history = new ClipboardHistoryService(settings);
        ClipboardHistory.Store = settings.HistoryEnabled ? history.Store : null;

        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            // Hiding the window must not end the process, or the hotkey has nothing to show.
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        BuildAvaloniaApp().SetupWithLifetime(lifetime);

        using var host = new LauncherHost(lifetime, settings);
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

        return lifetime.Start(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
