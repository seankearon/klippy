using Avalonia.Controls;
using Avalonia.Input;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class OfferConfirmOverlay : UserControl
{
    public OfferConfirmOverlay()
    {
        InitializeComponent();
    }

    /// <summary>A click beside the panel cancels, as it does for a delete: nothing has happened yet.</summary>
    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelOfferCommand.Execute(null);
    }
}
