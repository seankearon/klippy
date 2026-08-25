using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class EditorOverlay : UserControl
{
    public EditorOverlay()
    {
        InitializeComponent();
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel { Editor: { } editor })
            editor.CancelCommand.Execute(null);
    }

    // The template instantiates when Editor becomes non-null, so this fires per open.
    private void FirstFieldLoaded(object? sender, RoutedEventArgs e)
    {
        (sender as TextBox)?.Focus();
    }
}
