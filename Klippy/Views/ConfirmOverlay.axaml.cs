using Avalonia.Controls;
using Avalonia.Input;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class ConfirmOverlay : UserControl
{
    public ConfirmOverlay()
    {
        InitializeComponent();
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelDeleteCommand.Execute(null);
    }
}
