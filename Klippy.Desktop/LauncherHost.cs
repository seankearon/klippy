using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
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
    private IActivatableLifetime? _activatable;

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
                Dismiss(window);
            };
        }

        if (_lifetime.MainWindow is Klippy.Views.MainWindow main)
        {
            main.HideRequested = () => Dismiss(main);
            // The in-app quit ends exactly where the tray menu's does: closing the window
            // only hides it, so without this there is no way out that does not need the
            // tray icon — and on a crowded notification area that can be a hunt.
            main.QuitRequested = Quit;
        }

        // The first appearance is a summon too. Nothing has been anywhere yet, so there is
        // nothing for Remembered to remember, and a user who asked for Centre means it from
        // the start. Before the framework's own Show, so it opens in place: the window is
        // built by then but not yet shown, and Screens and Width/Height are already good.
        if (_lifetime.MainWindow is { } startup && PlaceFor(startup) is { } at)
            startup.Position = at;

        InstallAppMenu();
        InstallTray();

        if (OperatingSystem.IsMacOS() && _lifetime.MainWindow is { } spaced)
            MacWindow.MoveToActiveSpace(spaced);

        // Opening Klippy again while it is running — from Spotlight, Finder or Launchpad —
        // starts no second copy on macOS, so the single-instance check in Program never
        // sees it: the system asks the running copy to reopen instead. Answered the way
        // the tray's Show is, or relaunching a dismissed Klippy would appear to do nothing.
        _activatable = Application.Current?.TryGetFeature<IActivatableLifetime>();
        if (_activatable is not null)
            _activatable.Activated += OnActivated;

        if (!_settings.HotkeyEnabled) return;

        _hotkey = GlobalHotkey.TryRegister(_settings.ParsedHotkey,
            () => Dispatcher.UIThread.Post(ToggleSnippets));

        // Only worth a key if there is a history to summon: on a platform that cannot
        // capture, or with history switched off, it would open an empty view.
        if (ClipboardHistory.IsAvailable && _settings.ParsedHistoryHotkey is { } historySpec)
            _historyHotkey = GlobalHotkey.TryRegister(historySpec,
                () => Dispatcher.UIThread.Post(ToggleHistory));
    }

    /// <summary>
    /// Avalonia's default macOS app menu opens with "About Avalonia". As an agent Klippy
    /// shows no menu bar, but the menu is built all the same, so it gets one that names
    /// Klippy instead. Avalonia appends its standard Hide and Quit items after this one.
    /// </summary>
    private void InstallAppMenu()
    {
        if (!OperatingSystem.IsMacOS() || Application.Current is not { } app) return;

        var about = new NativeMenuItem("About Klippy");
        about.Click += (_, _) => _ = _lifetime.MainWindow?.Launcher.LaunchUriAsync(DocumentationHome);

        NativeMenu.SetMenu(app, new NativeMenu { about });
    }

    private static readonly Uri DocumentationHome = new("https://seankearon.github.io/klippy/");

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
            Dismiss(window);
            return;
        }

        if (history) vm?.ShowHistory();
        else vm?.ShowSnippets();

        ShowWindow();

        // The other key, pressed at a window that was already up, keeps what was typed —
        // the same question, asked of the other half — and hands it back selected: the
        // next keystroke replaces it, and → carries on typing where you left off, so
        // asking something else costs no more than asking this again. A window summoned
        // from dismissed has nothing to select, since being put away empties the box.
        //
        // After the show, not before: bringing a window forward settles focus, and a
        // selection made ahead of that is one the search box may have lost by the time
        // you can type into it.
        if (window is Klippy.Views.MainWindow shown && vm is { FilterText.Length: > 0 })
            shown.SelectSearchText();
    }

    /// <summary>
    /// Putting the window away, however it was asked for: its own hotkey pressed twice,
    /// Esc, a copy that asked to be dismissed by, or the close button. What was being
    /// asked goes with it — see <see cref="MainViewModel.Dismissed"/>.
    /// </summary>
    private static void Dismiss(Window window)
    {
        (window.DataContext as MainViewModel)?.Dismissed();
        window.Hide();
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

        // macOS can hide Klippy as an application — Hide Others (⌥⌘H) in another app, for
        // one — which is not the same as the window being hidden: Avalonia still reports
        // it visible, and nothing below would bring it back. On an app that is not hidden
        // this does nothing the Activate below would not do anyway.
        if (summoning && OperatingSystem.IsMacOS())
            _activatable?.TryLeaveBackground();

        // Ahead of the move: positioning a minimised window writes a rect that the restore
        // throws away. Safe to do while hidden — un-minimising a hidden window does not
        // reveal it, so there is no flash.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        // A window is assigned to a virtual desktop when it becomes visible, and stays
        // there. One left showing on another desktop is therefore *found* rather than
        // summoned: Activate takes you to it instead of bringing it to you, which is the
        // opposite of what a hotkey means. Hiding it first drops that assignment, so the
        // Show below puts it on the desktop you are actually looking at.
        //
        // Only while summoning, and only when it is already visible: the ordinary
        // dismissed-and-recalled case is hidden anyway, so the path this is used on most
        // pays nothing for it. A second press on a window that is in front and focused
        // never reaches here — Toggle has already dismissed it.
        //
        // Not on macOS, which gets the same result from MoveToActiveSpace, set once in
        // Start. There a minimised window is still animating its way out of the Dock at
        // this point, and hiding it would cut the restore short rather than finish it.
        if (summoning && window.IsVisible && !OperatingSystem.IsMacOS())
            window.Hide();

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

        // Activate asks macOS to make Klippy the active app, and since Sonoma it may say
        // no. The window then stays behind whatever you were using, so the summons looks
        // like it did nothing. Ordering it front regardless at least puts it in view.
        if (OperatingSystem.IsMacOS())
            MacWindow.OrderFrontRegardless(window);

        window.Focus();
    }

    /// <summary>Opening Klippy while it is already running: see Start.</summary>
    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen)
            Dispatcher.UIThread.Post(ShowWindow);
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
        if (_activatable is not null)
        {
            _activatable.Activated -= OnActivated;
            _activatable = null;
        }
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
