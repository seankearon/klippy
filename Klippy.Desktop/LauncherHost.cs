using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Klippy.Services;
using Klippy.ViewModels;

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
    private IGlobalHotkey? _historyHotkey;

    public LauncherHost(IClassicDesktopStyleApplicationLifetime lifetime, AppSettings settings)
    {
        _lifetime = lifetime;
        _settings = settings;
    }

    /// <summary>True when the hotkey was actually claimed; false means another app holds it.</summary>
    public bool HotkeyRegistered => _hotkey?.IsRegistered == true;

    /// <summary>
    /// True when the history hotkey was claimed. The two register independently, so one
    /// losing the race for its combination leaves the other working.
    /// </summary>
    public bool HistoryHotkeyRegistered => _historyHotkey?.IsRegistered == true;

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

        if (!_settings.HotkeyEnabled) return;

        _hotkey = GlobalHotkey.TryRegister(_settings.ParsedHotkey,
            () => Dispatcher.UIThread.Post(ToggleSnippets));

        // Only worth a key if there is a history to summon: on a platform that cannot
        // capture, or with history switched off, it would open an empty view.
        if (ClipboardHistory.IsAvailable && _settings.ParsedHistoryHotkey is { } historySpec)
            _historyHotkey = GlobalHotkey.TryRegister(historySpec,
                () => Dispatcher.UIThread.Post(ToggleHistory));
    }

    private void InstallTray()
    {
        var show = new NativeMenuItem("Show Klippy");
        show.Click += (_, _) => Dispatcher.UIThread.Post(ShowWindow);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Dispatcher.UIThread.Post(Quit);

        _tray = new TrayIcon
        {
            ToolTipText = TrayTooltip(),
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

    // Names both keys where both exist, since the second one is not guessable.
    private string TrayTooltip()
    {
        if (!_settings.HotkeyEnabled) return "Klippy";

        return ClipboardHistory.IsAvailable && _settings.ParsedHistoryHotkey is { } history
            ? $"Klippy — {_settings.ParsedHotkey} snippets, {history} history"
            : $"Klippy — {_settings.ParsedHotkey}";
    }

    /// <summary>Summons the snippet list, or dismisses it if that is already what is in front.</summary>
    public void ToggleSnippets() => Toggle(history: false);

    /// <summary>Summons the clipboard history, or dismisses it if that is already what is in front.</summary>
    public void ToggleHistory() => Toggle(history: true);

    /// <summary>
    /// Each hotkey means "show me this view". Pressing it while that view is already in
    /// front dismisses, as one key always did; pressing the *other* one switches views
    /// rather than hiding, which is the whole point of having two.
    /// </summary>
    private void Toggle(bool history)
    {
        if (_lifetime.MainWindow is not { } window) return;

        var vm = window.DataContext as MainViewModel;

        if (window.IsVisible && window.IsActive && vm?.IsHistoryMode == history)
        {
            window.Hide();
            return;
        }

        if (history) vm?.ShowHistory();
        else vm?.ShowSnippets();

        ShowWindow();
    }

    /// <summary>Kept for the tray icon, which has no view of its own to ask for.</summary>
    public void Toggle() => ToggleSnippets();

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
        _historyHotkey?.Dispose();
        _historyHotkey = null;
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
