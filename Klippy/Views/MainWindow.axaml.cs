using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Klippy.Services;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class MainWindow : Window
{
    private const double DefaultPreviewHeight = 180;
    private const double MinPreviewHeight = 90;
    private const double MinListHeight = 72; // keep in sync with the list row's MinHeight
    private const int PreviewRowIndex = 2;   // list, splitter, preview

    /// <summary>Height to restore the preview to; updated to wherever the splitter was left.</summary>
    private double _previewHeight = DefaultPreviewHeight;

    // x:Name on a RowDefinition generates no field, so reach it through the Grid.
    private RowDefinition PreviewRow => ListPreviewGrid.RowDefinitions[PreviewRowIndex];

    private MainViewModel? _watched;

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Set by the desktop head when running as a resident launcher.</summary>
    public Action? HideRequested { get; set; }

    /// <summary>
    /// Set by the desktop head: what to do once the user has confirmed they want Klippy
    /// closed. Left null there is no launcher to end, so the confirmation simply does
    /// nothing rather than half-closing a window the hotkey still expects to find.
    /// </summary>
    public Action? QuitRequested { get; set; }

    /// <summary>
    /// Set when a snippet shortcut has just taken a key press, so the character that press would type — the
    /// <c>L</c> of <c>Ctrl+K, L</c> — doesn't land in the search box as well. Cleared by the next press.
    /// </summary>
    private bool _swallowTextInput;

    public MainWindow()
    {
        InitializeComponent();
        // Tunnel so arrows/enter reach us before the search TextBox consumes them.
        AddHandler(KeyDownEvent, PreviewKeyDown, RoutingStrategies.Tunnel);

        // Snippet shortcuts. A first press is offered once everything else has had its say — Klippy's own
        // keys above, then the search box's — so a shortcut never takes a key either of them uses. The
        // second press of a chord is taken in PreviewKeyDown instead, ahead of all of them.
        AddHandler(KeyDownEvent, ShortcutKeyDown, RoutingStrategies.Bubble);
        AddHandler(TextInputEvent, ShortcutTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A chord waits for its second press as long as it takes — but not across a click, or the window
        // losing the keyboard: by then the user has plainly moved on.
        AddHandler(PointerPressedEvent, (_, _) => Vm?.CancelChord(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => Vm?.CancelChord();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm)
        {
            vm.ClipboardWriter = payload => RichTextClipboard.WriteAsync(Clipboard, payload);
            vm.ClipboardReader = () => Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null);
            // Only the desktop head can open a browser or start a script, so this is
            // where execution is wired in — the shared view model only asks for it.
            vm.Executor = ProcessLauncher.RunAsync;
        }
        SearchBox.Focus();
    }

    // ---- preview pane sizing ----
    //
    // The splitter owns the row height while the pane is open, so opening and closing
    // is a matter of swapping that height in and out rather than binding it.

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_watched is { } previous)
        {
            previous.PropertyChanged -= VmPropertyChanged;
            previous.Copied -= SelectSearchText;
            previous.CloseRequested -= HideAfterCopy;
            previous.QuitRequested -= Quit;
        }
        _watched = Vm;
        if (_watched is { } current)
        {
            current.PropertyChanged += VmPropertyChanged;
            current.Copied += SelectSearchText;
            current.CloseRequested += HideAfterCopy;
            current.QuitRequested += Quit;
        }

        ApplyPreviewHeight();
    }

    /// <summary>
    /// Hands focus back to the search box with its text selected, so the next keystroke
    /// starts a fresh search instead of appending to the old one. What a copy wants once
    /// its search term has done its job, and what the launcher asks for once the other
    /// hotkey has switched views with a line still in the box.
    /// </summary>
    public void SelectSearchText()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// "Close after copy" means what Esc means: dismiss the launcher, leaving it resident.
    /// Nothing to do when there is no launcher — a plain window run has nowhere to hide to.
    /// </summary>
    private void HideAfterCopy() => HideRequested?.Invoke();

    /// <summary>Confirmed quit: the head owns the tray icon and the hotkeys, so it ends it.</summary>
    private void Quit() => QuitRequested?.Invoke();

    private void VmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPreviewOpen))
            ApplyPreviewHeight();
    }

    private void ApplyPreviewHeight()
    {
        bool open = Vm?.IsPreviewOpen == true;

        if (!open)
        {
            // Remember where the user left the splitter. ActualHeight rather than
            // Height, because the splitter may leave the row in star units.
            if (PreviewRow.ActualHeight > 0)
                _previewHeight = PreviewRow.ActualHeight;

            PreviewRow.MinHeight = 0;
            PreviewRow.Height = new GridLength(0);
            return;
        }

        PreviewRow.MinHeight = MinPreviewHeight;
        PreviewRow.Height = new GridLength(ClampPreviewHeight(_previewHeight), GridUnitType.Pixel);
    }

    /// <summary>Stops a remembered height from crowding out the list in a shorter window.</summary>
    private double ClampPreviewHeight(double height)
    {
        double total = ListPreviewGrid.Bounds.Height;
        if (total <= 0) return height; // not laid out yet; the Grid will sort it out

        double max = total - MinListHeight - PreviewSplitter.Bounds.Height;
        return max <= MinPreviewHeight ? MinPreviewHeight : Math.Clamp(height, MinPreviewHeight, max);
    }

    private void PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        _swallowTextInput = false; // a fresh press: whatever the last one left to hold back is stale now

        // A key field in the editor is listening: every press is its own, Esc and Cmd/Ctrl+Enter included.
        if (vm.Editor is { IsRecording: true }) return;

        // The second press of a chord, heard ahead of everything — so it is the chord's alone, and the N of
        // Ctrl+K, Ctrl+N doesn't also open a new snippet. A modifier going down is only the run-up to it.
        if (vm.IsChordPending)
        {
            if (vm.PressShortcutKey(KeyStroke.From(e.Key, e.KeyModifiers)).Kind != ChordOutcomeKind.StillWaiting)
            {
                e.Handled = true;
                _swallowTextInput = true;
            }
            return;
        }

        var cmdMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        // While an overlay is open only Esc (cancel) and Cmd/Ctrl+Enter (save) are global.
        // A bare Enter is left to the focused button, which in a confirmation starts out
        // as Cancel — see ConfirmFocus.
        if (IsOverlayOpen(vm))
        {
            if (e.Key == Key.Escape)
            {
                vm.HandleEscape();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(cmdMod)
                     && vm.Editor?.SaveCommand.CanExecute(null) == true)
            {
                vm.Editor.SaveCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (e.KeyModifiers.HasFlag(cmdMod))
        {
            switch (e.Key)
            {
                case Key.N:
                    vm.NewCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.F:
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    e.Handled = true;
                    return;
                case Key.E:
                    vm.OpenTransferCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D:
                    vm.DuplicateSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.I:
                    // The Mac half of the edit shortcut: F2 is a brightness key on a
                    // laptop keyboard unless the function-key setting says otherwise.
                    vm.EditSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.P:
                    vm.TogglePreviewCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.R:
                    // R for reveal: where the path the selected row names is, in Explorer
                    // or Finder, whatever the row is marked for.
                    vm.RevealSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    vm.OpenSettingsCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    // Enter does whatever the item is marked for; Cmd/Ctrl+Enter always
                    // copies, which is how you get the text of an item marked to run.
                    vm.CopySelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.F2:
                // What F2 does to the selected thing everywhere else: open it for editing.
                vm.EditSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                // The MRU while it is open — and, from an empty search box, the key that
                // opens it. Otherwise the list, as it has always been.
                vm.Navigate(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Navigate(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // A selected row always wins: running an unmatched search is what Enter
                // falls back to, never what it prefers.
                if (vm.SelectedSnippet is not null)
                {
                    vm.ActivateSelectedCommand.Execute(null);
                    e.Handled = true;
                }
                else if (vm.SelectedOffer is { } offer)
                {
                    vm.RunOfferCommand.Execute(offer);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                // Esc unwinds overlays/filter first; once there is nothing left to clear it
                // dismisses the launcher, which is the only keyboard way back to your work.
                if (vm.HandleEscape())
                    e.Handled = true;
                else if (HideRequested is { } hide)
                {
                    hide();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Whether an overlay — the editor, a confirmation, export/import, settings — has the window.</summary>
    private static bool IsOverlayOpen(MainViewModel vm) =>
        vm.Editor is not null || vm.DeleteTarget is not null || vm.Transfer is not null
        || vm.Settings is not null || vm.PendingOffer is not null;

    /// <summary>
    /// A press that neither Klippy's own keys nor the search box wanted, offered to the snippet shortcuts: a
    /// shortcut triggers its snippet, and the first press of a chord raises the pill and waits for the second
    /// (taken by <see cref="PreviewKeyDown"/>). Not while an overlay is open — the editor's own fields, a
    /// confirmation's buttons — since triggering a snippet behind one would be a surprise.
    /// </summary>
    private void ShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Handled || IsOverlayOpen(vm)) return;

        var outcome = vm.PressShortcutKey(KeyStroke.From(e.Key, e.KeyModifiers));
        if (outcome.Kind is ChordOutcomeKind.Waiting or ChordOutcomeKind.Matched)
        {
            e.Handled = true;
            _swallowTextInput = true; // Ctrl+Alt is AltGr on some layouts, and AltGr types
        }
    }

    /// <summary>Holds back the character a key press would have typed, when a shortcut has just taken that press.</summary>
    private void ShortcutTextInput(object? sender, TextInputEventArgs e)
    {
        if (!_swallowTextInput) return;
        _swallowTextInput = false;
        e.Handled = true;
    }

    /// <summary>
    /// Clicking a command in the MRU takes it as the line to work with rather than
    /// running it: the text is already in the box (the click selected it), so this only
    /// puts the list away and hands the keyboard back to the search box. Recalling a
    /// command you want to edit before running it is the common case, and running one is
    /// then the Enter it always was.
    /// </summary>
    private void CommandTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.AcceptCommand();
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        e.Handled = true;
    }

    /// <summary>
    /// Clicking a row triggers it — a copy, or a run where the item is marked for one —
    /// unless the click landed on a button.
    /// </summary>
    private void RowTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (sender is Control { DataContext: RowViewModel row })
        {
            vm.SelectedSnippet = row;
            vm.ActivateCommand.Execute(row);
        }
    }
}
