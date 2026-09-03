using System;
using Avalonia;
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

        // The first appearance is a summon too. Nothing has been anywhere yet, so there is
        // nothing for Remembered to remember, and a user who asked for Centre means it from
        // the start. Before the framework's own Show, so it opens in place: the window is
        // built by then but not yet shown, and Screens and Width/Height are already good.
        if (_lifetime.MainWindow is { } startup && PlaceFor(startup) is { } at)
            startup.Position = at;

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

        // Only a window that is actually coming up gets placed. Switching views with the
        // other hotkey comes through here too, and a window already in front of you should
        // not leap across the desk because you asked it for the history instead.
        //
        // "In front of you" is visible AND focused — the same test Toggle uses to decide
        // that a second press means dismiss. A window left sitting behind another app is
        // still being summoned, and is exactly the case the setting exists for. Minimised
        // counts as coming up too: the restore discards whatever position it had anyway.
        var summoning = !(window.IsVisible && window.IsActive)
                        || window.WindowState == WindowState.Minimized;

        // Ahead of the move: positioning a minimised window writes a rect that the restore
        // throws away. Safe to do while hidden — un-minimising a hidden window does not
        // reveal it, so there is no flash.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        // Computed once and assigned twice. Asking twice would re-read the cursor, and the
        // window would jump if it had moved a pixel in between.
        var placed = summoning ? PlaceFor(window) : null;

        // Before Show, so the window appears where it belongs rather than appearing and
        // then jumping.
        if (placed is { } target) window.Position = target;

        window.Show();

        // And again after. Un-minimising a window that was also hidden only takes effect at
        // the Show, and that restore reinstates the old position over ours; a move between
        // monitors of different DPI likewise gets answered with a rect of Windows' choosing.
        // Assigning the same point twice costs nothing on the summons where the first held.
        if (placed is { } again) window.Position = again;

        window.Activate();
        window.Focus();
    }

    /// <summary>
    /// Where the window should land, or null to leave it where the user left it — which is
    /// the default, and also the answer when nothing will say where the screens are.
    /// </summary>
    private PixelPoint? PlaceFor(Window window)
    {
        var mode = _settings.ParsedSummonPlacement;
        if (mode == LauncherPlacement.Remembered) return null;

        try
        {
            var screens = window.Screens;
            var cursor = CursorPosition.TryGet();

            // The pointer is the best answer available to "which screen am I looking at",
            // and a reading that lands on no screen at all is a reading to ignore.
            var screen = (cursor is { } c ? screens.ScreenFromPoint(c) : null)
                         ?? screens.ScreenFromWindow(window)
                         ?? screens.Primary;
            if (screen is null) return null;

            // Width/Height, not ClientSize: before the first Show ClientSize is nonsense,
            // and Window.HandleResized keeps Width/Height in step with every user resize.
            // The window extends its client area over its decorations, so this is also its
            // frame size.
            var size = new Size(window.Width, window.Height);

            // The target screen's scaling, never the window's: RenderScaling describes the
            // monitor it is on now, not the one it is about to be on.
            return mode == LauncherPlacement.Pointer && cursor is { } p
                ? PlacementPolicy.AtPointer(p, screen.WorkingArea, screen.Scaling, size)
                : PlacementPolicy.Centred(screen.WorkingArea, screen.Scaling, size);
        }
        catch (Exception)
        {
            // No windowing backend to ask: WindowBase.Screens throws rather than returning
            // null. Showing the window where it was beats not showing it.
            return null;
        }
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
