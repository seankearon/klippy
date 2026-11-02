using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>The snippets' own system-wide hotkeys, one registration apiece — see <see cref="RegisterSnippetHotkeys"/>.</summary>
    private readonly List<IGlobalHotkey> _snippetHotkeys = new();

    /// <summary>What <see cref="_snippetHotkeys"/> was registered from, so an unchanged plan isn't re-registered.</summary>
    private IReadOnlyList<(Guid SnippetId, HotkeySpec Spec)> _snippetHotkeyPlan = [];

    private MainViewModel? _watched;

    /// <summary>What window.json holds as far as this run knows, so an Esc that moved nothing writes nothing.</summary>
    private WindowBounds? _rememberedBounds;

    /// <summary>
    /// The launcher window, held here rather than read off the lifetime each time: a window
    /// started hidden is taken off the lifetime so that its startup does not show it — see
    /// <see cref="Start"/> — and it is still the window every hotkey summons.
    /// </summary>
    private readonly Window? _window;

    /// <summary>Whether to start put away rather than showing the window — a login start.</summary>
    private readonly bool _startHidden;

    /// <param name="startHidden">
    /// Come up behind the tray icon, as if dismissed, rather than with the window showing:
    /// what a start at login wants, since logging in is not asking for a window.
    /// </param>
    public LauncherHost(IClassicDesktopStyleApplicationLifetime lifetime, AppSettings settings, bool startHidden = false)
    {
        _lifetime = lifetime;
        _settings = settings;
        _window = lifetime.MainWindow;
        _startHidden = startHidden;
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
        if (_window is { } window)
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

        // Signing out, restarting or shutting down asks every app to end — as does Quit from
        // the macOS app menu — and the lifetime answers by closing the windows. Left to the
        // handler above, that close would be turned into a hide, the request refused, and
        // Windows would name Klippy as what is stopping it. So the request is a quit: it
        // ends what Quit ends, and leaves the closing to the lifetime that asked.
        _lifetime.ShutdownRequested += OnShutdownRequested;

        if (_window is Klippy.Views.MainWindow main)
        {
            main.HideRequested = () => Dismiss(main);
            // The in-app quit ends exactly where the tray menu's does: closing the window
            // only hides it, so without this there is no way out that does not need the
            // tray icon — and on a crowded notification area that can be a hunt.
            main.QuitRequested = Quit;
        }

        // The first appearance is a summon too, so a user who asked for Centre means it from
        // the start. Remembered means where the window was left last time Klippy ran, and its
        // size is remembered whichever placement is chosen. Before the framework's own Show,
        // so it opens in place: the window is built by then but not yet shown, and Screens and
        // Width/Height are already good. Size before PlaceFor, which centres or hangs the
        // window by it.
        if (_window is { } startup)
        {
            var remembered = RestoreBounds(startup);

            // Started hidden, the first summons is the first appearance, and places the
            // window itself — except Remembered, which a summons leaves where it was, and so
            // has to be put there now.
            var at = (_startHidden ? null : PlaceFor(startup))
                     ?? (_settings.ParsedSummonPlacement == LauncherPlacement.Remembered ? remembered : null);
            if (at is { } here)
            {
                startup.Position = here;

                // And again once it is up, for the reason ShowWindow assigns twice: a window
                // that opens on a monitor of different scaling from the one it was created on
                // is answered with a rect of Windows' choosing. The same point twice costs
                // nothing where the first held.
                EventHandler? again = null;
                again = (_, _) =>
                {
                    startup.Opened -= again;
                    startup.Position = here;
                };
                startup.Opened += again;
            }

            if (_startHidden && startup is Klippy.Views.MainWindow hidden)
            {
                // The lifetime shows its main window as it starts, unconditionally; one it
                // does not have, it cannot. Nothing else of Avalonia's reads it, and
                // everything here goes through _window.
                _lifetime.MainWindow = null;

                // What the window hands the view model as it opens — the clipboard, the
                // launcher — is wanted before it has ever opened: a snippet's hotkey copies
                // or runs without bringing the window up at all.
                hidden.ConnectToPlatform();
            }
        }

        InstallAppMenu();
        InstallTray();

        if (OperatingSystem.IsMacOS() && _window is { } spaced)
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

        // The snippets' own hotkeys, under the same master switch: the editor offers one only from here on,
        // and every save, delete or import that could change them registers them again.
        if (GlobalHotkey.IsSupported && _window?.DataContext is MainViewModel vm)
        {
            _watched = vm;
            vm.CanRegisterHotkeys = true;
            vm.SnippetsChanged += OnSnippetsChanged;
            RegisterSnippetHotkeys(vm);
        }
    }

    private void OnSnippetsChanged()
    {
        if (_watched is { } vm) RegisterSnippetHotkeys(vm);
    }

    /// <summary>
    /// Registers each snippet's system-wide hotkey, replacing whatever the last round registered — unless the
    /// plan is what it was, which is the usual case for a save that touched a label. Each is its own
    /// registration, as the summon keys are, so one another application already holds leaves the rest
    /// working; those that could not be claimed are handed back to the view model, whose editor says so, and
    /// named on stderr.
    /// </summary>
    private void RegisterSnippetHotkeys(MainViewModel vm)
    {
        var plan = vm.PlannedHotkeys();
        if (plan.SequenceEqual(_snippetHotkeyPlan)) return;

        DisposeSnippetHotkeys();
        var unclaimed = new List<Guid>();
        foreach (var (id, spec) in plan)
        {
            var hotkey = GlobalHotkey.TryRegister(spec, () => Dispatcher.UIThread.Post(() => TriggerSnippet(id, spec)));
            if (hotkey is not null)
            {
                _snippetHotkeys.Add(hotkey);
                continue;
            }

            unclaimed.Add(id);
            Console.Error.WriteLine($"Could not register a snippet's hotkey ({spec}); another app may hold it.");
        }

        _snippetHotkeyPlan = plan;
        vm.ReportUnclaimedHotkeys(unclaimed);
    }

    /// <summary>
    /// A snippet's hotkey, pressed in whatever application has the keyboard: the snippet is copied, or run if
    /// it is marked Execute, without the window coming up. One that takes an argument brings it up with its
    /// quick-code typed and the caret after it, waiting for the argument and Enter; and one that can't do its
    /// job — a run that could not start — brings it up so the reason it gives can be read.
    /// </summary>
    private async void TriggerSnippet(Guid id, HotkeySpec spec)
    {
        if (_window?.DataContext is not MainViewModel vm) return;

        // Pressed while a key field in the editor is listening: it is the field's, as any other press would be
        // — which is how a combination another snippet holds gets recorded rather than firing.
        if (vm.OfferHotkeyToRecorder(spec)) return;

        TriggerOutcome outcome;
        try
        {
            outcome = await vm.TriggerSnippetAsync(id);
        }
        catch (Exception)
        {
            outcome = TriggerOutcome.Failed;
        }

        if (outcome == TriggerOutcome.Done) return;

        ShowWindow();

        // Its quick-code is in the box waiting for an argument: after the show, as with a summons, since
        // bringing the window forward is what settles where the keyboard goes.
        if (outcome == TriggerOutcome.AwaitingArguments && _window is Klippy.Views.MainWindow main)
            main.PlaceCaretAtEnd();
    }

    private void DisposeSnippetHotkeys()
    {
        foreach (var hotkey in _snippetHotkeys) hotkey.Dispose();
        _snippetHotkeys.Clear();
        _snippetHotkeyPlan = [];
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
        about.Click += (_, _) => _ = _window?.Launcher.LaunchUriAsync(DocumentationHome);

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
        if (_window is not { } window) return;

        var vm = window.DataContext as MainViewModel;

        // A key field in the editor is listening, and the OS handed it the summon key instead: the field says
        // why it can't have it, and the window stays where it is.
        var summon = history ? _settings.ParsedHistoryHotkey : _settings.ParsedHotkey;
        if (summon is not null && vm?.OfferHotkeyToRecorder(summon) == true) return;

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
    /// asked goes with it — see <see cref="MainViewModel.Dismissed"/>. Where it was and how
    /// big are noted on the way, so the next launch can put it back.
    /// </summary>
    private void Dismiss(Window window)
    {
        RememberBounds(window);
        (window.DataContext as MainViewModel)?.Dismissed();
        window.Hide();
    }

    /// <summary>
    /// Puts the window back to the size it was left at, and says where it was left — fitted
    /// to the screens there are now, or centred on the primary one when that place is on no
    /// screen any more. Null when nothing has been remembered yet, which leaves the first
    /// launch where the OS puts it, as it always was.
    /// </summary>
    private PixelPoint? RestoreBounds(Window window)
    {
        if (WindowBounds.Load() is not { } saved) return null;
        _rememberedBounds = saved;

        var minimum = new Size(window.MinWidth, window.MinHeight);
        try
        {
            var all = window.Screens.All;
            if ((window.Screens.Primary ?? all.FirstOrDefault()) is not { } primary)
                throw new InvalidOperationException("No screens to place the window on.");

            var (position, size) = PlacementPolicy.Restore(
                saved, minimum,
                all.Select(s => new ScreenArea(s.WorkingArea, s.Scaling)).ToList(),
                new ScreenArea(primary.WorkingArea, primary.Scaling));

            window.Width = size.Width;
            window.Height = size.Height;
            return position;
        }
        catch (Exception)
        {
            // Nothing will say where the screens are (see PlaceFor). The size is still the
            // size, and the OS can choose where it goes.
            window.Width = Math.Max(saved.Width, minimum.Width);
            window.Height = Math.Max(saved.Height, minimum.Height);
            return null;
        }
    }

    /// <summary>
    /// Writes down where the window is and how big, for <see cref="RestoreBounds"/> to find
    /// next launch. Only a window that is up and neither minimised nor maximised: a minimised
    /// one sits where the OS parks it — -32000 on Windows — and a maximised one is the size
    /// of its screen, and neither is where anybody left it. A hidden one was noted when it
    /// was put away.
    /// </summary>
    private void RememberBounds(Window window)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal) return;

        // ClientSize rather than Width/Height, now that it is up and so describes the window:
        // it is what a resize by hand changes, and Width/Height are set from it.
        var bounds = new WindowBounds
        {
            X = window.Position.X,
            Y = window.Position.Y,
            Width = window.ClientSize.Width,
            Height = window.ClientSize.Height,
        };
        if (!bounds.IsUsable || bounds == _rememberedBounds) return;

        bounds.Save();
        _rememberedBounds = bounds;
    }

    /// <summary>Kept for the tray icon, which has no view of its own to ask for.</summary>
    public void Toggle() => ToggleSnippets();

    private void ShowWindow()
    {
        if (_window is not { } window) return;

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
        EndSession();
        _lifetime.Shutdown();
    }

    /// <summary>
    /// The OS, or the macOS app menu, asking Klippy to end. Unless something else has
    /// already said no, it ends as Quit does — except that the lifetime, which asked, does
    /// the closing, and on Windows answers the OS once the windows have gone.
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (!e.Cancel) EndSession();
    }

    /// <summary>
    /// What every way out does before the lifetime ends: notes where the window is, lets
    /// its close through, and gives back the hotkeys and the tray icon.
    /// </summary>
    private void EndSession()
    {
        // Quitting with the window up never passes through Dismiss.
        if (_window is { } window) RememberBounds(window);

        _shuttingDown = true;
        Dispose();
    }

    public void Dispose()
    {
        _lifetime.ShutdownRequested -= OnShutdownRequested;
        _hotkey?.Dispose();
        _hotkey = null;
        _historyHotkey?.Dispose();
        _historyHotkey = null;
        if (_watched is not null)
        {
            _watched.SnippetsChanged -= OnSnippetsChanged;
            _watched = null;
        }
        DisposeSnippetHotkeys();
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
