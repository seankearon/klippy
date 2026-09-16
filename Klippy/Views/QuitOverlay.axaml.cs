using Avalonia.Controls;
using Avalonia.Input;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class QuitOverlay : UserControl
{
    public QuitOverlay()
    {
        InitializeComponent();
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CancelQuitCommand.Execute(null);
    }
}
