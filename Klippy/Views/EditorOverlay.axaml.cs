using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Klippy.Services;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class EditorOverlay : UserControl
{
    public EditorOverlay()
    {
        InitializeComponent();

        // While a key field is listening, every press is its own — Enter and Esc included, which would
        // otherwise press a button or close the editor. Tunnel, so it hears them before anything inside.
        AddHandler(KeyDownEvent, RecordingKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(TextInputEvent, (_, e) => { if (Recording is not null) e.Handled = true; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // A click anywhere but a key field leaves the listening one as it was.
        AddHandler(PointerPressedEvent, PointerPressedAnywhere, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The open editor, when one of its key fields is listening.</summary>
    private EditorViewModel? Recording =>
        DataContext is MainViewModel { Editor: { IsRecording: true } editor } ? editor : null;

    private void RecordingKeyDown(object? sender, KeyEventArgs e)
    {
        if (Recording is not { } editor) return;
        e.Handled = true;
        editor.Press(KeyStroke.From(e.Key, e.KeyModifiers));
    }

    private void PointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        if (Recording is not { } editor) return;
        // A key field's own click starts (or restarts) listening, which ends any other.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is { } button
            && button.Classes.Contains("keyField"))
            return;
        editor.CancelRecording();
    }

    // The template instantiates when Editor becomes non-null, so this fires per open.
    private void FirstFieldLoaded(object? sender, RoutedEventArgs e)
    {
        (sender as TextBox)?.Focus();
    }

    /// <summary>
    /// Scrolls the snippet's own tag into view. With more tags than the capped chip area
    /// shows, the highlighted one would otherwise sit below the fold on open — which is
    /// the one case where the highlight has something to say.
    /// </summary>
    private void TagChipLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TagChipViewModel { IsSelected: true } } chip)
            chip.BringIntoView();
    }

    // ---- dialog sizing ----
    //
    // The dialog keeps whatever size it was last dragged to for the rest of the
    // session, so reopening the editor does not undo the resize.

    /// <summary>Keeps the dialog inside the window: a size dragged out in a large
    /// window must not hang off the edges of a smaller one.</summary>
    private void OverlaySizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var margin = Dialog.Margin;
        Dialog.MaxWidth = Math.Max(0, e.NewSize.Width - margin.Left - margin.Right);
        Dialog.MaxHeight = Math.Max(0, e.NewSize.Height - margin.Top - margin.Bottom);
    }

    /// <summary>Corner grip. The dialog is centred, so a size change splits itself
    /// between both edges: doubling the delta keeps the corner under the pointer.
    /// The base is Bounds rather than Width because that is the size the grip was
    /// laid out against, which keeps the arithmetic honest if a move arrives before
    /// the previous one has been measured. MinWidth/MaxWidth do the clamping.</summary>
    private void ResizeDelta(object? sender, VectorEventArgs e)
    {
        // First drag of the session: pin the floor to the size the dialog opens at,
        // so shrinking can never squeeze the fields out through the border. Height is
        // still unset here, which is what makes Bounds the natural size.
        if (double.IsNaN(Dialog.Height))
            Dialog.MinHeight = Dialog.Bounds.Height;

        Dialog.Width = Dialog.Bounds.Width + e.Vector.X * 2;
        Dialog.Height = Dialog.Bounds.Height + e.Vector.Y * 2;
    }
}
