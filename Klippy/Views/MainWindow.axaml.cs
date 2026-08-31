using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    public MainWindow()
    {
        InitializeComponent();
        // Tunnel so arrows/enter reach us before the search TextBox consumes them.
        AddHandler(KeyDownEvent, PreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm)
            vm.ClipboardWriter = payload => RichTextClipboard.WriteAsync(Clipboard, payload);
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
        }
        _watched = Vm;
        if (_watched is { } current)
        {
            current.PropertyChanged += VmPropertyChanged;
            current.Copied += SelectSearchText;
        }

        ApplyPreviewHeight();
    }

    /// <summary>
    /// After a copy the search term has done its job, so hand focus back to the box with the
    /// text selected — the next keystroke starts a fresh search instead of appending to the old one.
    /// </summary>
    private void SelectSearchText()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

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

        var cmdMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        // While an overlay is open only Esc (cancel) and Cmd/Ctrl+Enter (save) are global.
        if (vm.Editor is not null || vm.DeleteTarget is not null || vm.Transfer is not null)
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
                    vm.DuplicateCommand.Execute(vm.SelectedSnippet);
                    e.Handled = true;
                    return;
                case Key.P:
                    vm.TogglePreviewCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (vm.SelectedSnippet is not null)
                {
                    vm.CopySelectedCommand.Execute(null);
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

    /// <summary>Clicking a row copies it (design: copy on click) — unless the click landed on a button.</summary>
    private void RowTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (sender is Control { DataContext: SnippetViewModel row })
        {
            vm.SelectedSnippet = row;
            vm.CopyCommand.Execute(row);
        }
    }
}
