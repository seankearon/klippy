using Avalonia.Controls;
using Avalonia.Input;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class SettingsOverlay : UserControl
{
    public SettingsOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// A click beside the panel closes it. Unlike the editor there is no unsaved work to
    /// lose — every toggle has already been written.
    /// </summary>
    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel { Settings: { } settings })
            settings.CloseCommand.Execute(null);
    }
}
