using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class ImagePreviewOverlay : UserControl
{
    private IInputElement? _returnTo;

    public ImagePreviewOverlay()
    {
        InitializeComponent();

        // Tab goes round the buttons rather than out to the list behind the scrim, and the
        // keyboard starts on Copy image — the one thing here that is undone by copying again.
        KeyboardNavigation.SetTabNavigation(Overlay, KeyboardNavigationMode.Cycle);
        Overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;

            if (Overlay.IsVisible)
            {
                _returnTo = TopLevel.GetTopLevel(Overlay)?.FocusManager?.GetFocusedElement();
                CopyButton.Focus(NavigationMethod.Tab);
            }
            else
            {
                if (_returnTo is Visual previous && TopLevel.GetTopLevel(previous) is not null)
                    _returnTo.Focus();
                _returnTo = null;
            }
        };
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CloseImagePreviewCommand.Execute(null);
    }
}
