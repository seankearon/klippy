using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Turns Klippy into a resident launcher: it keeps running behind a tray icon, and a global
/// hotkey toggles the window. Closing the window hides it rather than quitting, so the
/// hotkey always has something to summon — and on Windows it also keeps the OLE-served HTML
/// clipboard flavour alive after a copy.
/// </summary>
internal sealed class LauncherHost : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly AppSettings _settings;
    private TrayIcon? _tray;
    private bool _shuttingDown;
    private IGlobalHotkey? _hotkey;

    public LauncherHost(IClassicDesktopStyleApplicationLifetime lifetime, AppSettings settings)
    {
        _lifetime = lifetime;
        _settings = settings;
    }

    /// <summary>True when the hotkey was actually claimed; false means another app holds it.</summary>
    public bool HotkeyRegistered => _hotkey?.IsRegistered == true;

    public void Start()
    {
        if (_lifetime.MainWindow is { } window)
        {
            // Closing must not quit: ShutdownMode is OnExplicitShutdown, so without this the
            // window would be destroyed and the hotkey would have nothing left to show.
            window.Closing += (_, e) =>
            {
                if (_shuttingDown) return; // real quit, let it through
                e.Cancel = true;
                window.Hide();
            };
        }

        if (_lifetime.MainWindow is Klippy.Views.MainWindow main)
            main.HideRequested = () => main.Hide();

        InstallTray();

        if (_settings.HotkeyEnabled)
            _hotkey = GlobalHotkey.TryRegister(_settings.ParsedHotkey,
                () => Dispatcher.UIThread.Post(Toggle));
    }

    private void InstallTray()
    {
        var show = new NativeMenuItem("Show Klippy");
        show.Click += (_, _) => Dispatcher.UIThread.Post(ShowWindow);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Dispatcher.UIThread.Post(Quit);

        _tray = new TrayIcon
        {
            ToolTipText = $"Klippy — {_settings.ParsedHotkey}",
            Menu = new NativeMenu { show, quit },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(Toggle);

        try
        {
            _tray.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://Klippy/Assets/klippy.ico")));
        }
        catch (Exception)
        {
            // No tray icon art is survivable; the menu still works.
        }
    }

    /// <summary>Hotkey behaviour: summon if hidden or in the background, dismiss if already in front.</summary>
    public void Toggle()
    {
        if (_lifetime.MainWindow is not { } window) return;

        if (window.IsVisible && window.IsActive)
            window.Hide();
        else
            ShowWindow();
    }

    private void ShowWindow()
    {
        if (_lifetime.MainWindow is not { } window) return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Focus();
    }

    private void Quit()
    {
        _shuttingDown = true;
        Dispose();
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
