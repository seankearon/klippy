using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class HistoryPickerOverlay : UserControl
{
    /// <summary>What had the keyboard when the picker came up — a field in the editor, usually — for the paste.</summary>
    private IInputElement? _returnTo;

    public HistoryPickerOverlay()
    {
        InitializeComponent();

        Overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;

            if (Overlay.IsVisible)
            {
                _returnTo = TopLevel.GetTopLevel(Overlay)?.FocusManager?.GetFocusedElement();
            }
            else
            {
                if (_returnTo is Visual previous && TopLevel.GetTopLevel(previous) is not null)
                    _returnTo.Focus();
                _returnTo = null;
            }
        };
    }

    // The content is built afresh each time the picker opens, so this fires per open.
    private void FilterLoaded(object? sender, RoutedEventArgs e) => (sender as TextBox)?.Focus();

    /// <summary>
    /// Hands the keyboard to the filter. For the launcher, once it has brought the window forward: that
    /// settles where focus goes, and can take it off the box the picker gave it to as it opened.
    /// </summary>
    public void FocusFilter() =>
        Overlay.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Name == "PickerFilter")?.Focus();

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CloseHistoryPickerCommand.Execute(null);
    }

    /// <summary>Clicking a clip copies it, as Enter does.</summary>
    private void RowTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Control { DataContext: ClipViewModel clip })
            vm.PickClipCommand.Execute(clip);
    }
}
