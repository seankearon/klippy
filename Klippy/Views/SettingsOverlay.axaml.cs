using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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
    ///
    /// Two controls raise this: the scrim, and the ScrollViewer that sits over it so the
    /// panel can scroll on a short window. Presses from inside the panel reach the latter by
    /// bubbling, and a toggle flipped is not a dialog dismissed — hence the ancestor test.
    /// </summary>
    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && IsInsideThePanel(source)) return;

        if (DataContext is MainViewModel { Settings: { } settings })
            settings.CloseCommand.Execute(null);
    }

    /// <summary>Enter in the box that adds a kind of file or link to open adds it, as the button beside it does.</summary>
    private void NewOpenKindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel { Settings: { } settings }) return;

        settings.AddOpenKindCommand.Execute(null);
        e.Handled = true;
    }

    private bool IsInsideThePanel(Visual source)
    {
        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, Panel))
                return true;

        return false;
    }
}
