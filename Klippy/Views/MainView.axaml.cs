using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Klippy.Services;
using Klippy.ViewModels;

namespace Klippy.Views;

/// <summary>
/// Mobile screen. Hosts the touch layout plus a small hand-rolled
/// swipe-left gesture on rows that reveals Edit/Delete actions.
/// </summary>
public partial class MainView : UserControl
{
    private const double OpenX = -128; // two 64px action buttons
    private const double DragThreshold = 8;

    private Border? _activeRow;   // row the pointer went down on
    private Border? _openRow;     // row currently showing its actions
    private Point _pressPoint;
    private double _startX;
    private bool _dragging;

    private MainViewModel? Vm => DataContext as MainViewModel;

    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is not { } top) return;

        if (Vm is { } vm)
            vm.ClipboardWriter = payload => RichTextClipboard.WriteAsync(top.Clipboard, payload);

        // Keep content clear of notches/status bars and match the system bars to the theme.
        if (top.InsetsManager is { } insets)
        {
            insets.SystemBarColor = Color.Parse("#16181D");
            ApplySafeArea(insets.SafeAreaPadding);
            insets.SafeAreaChanged += (_, args) => ApplySafeArea(args.SafeAreaPadding);
        }
    }

    private void ApplySafeArea(Thickness padding) => Root.Margin = padding;

    // ---- swipe gesture ----

    private static TranslateTransform Transform(Border row) => (TranslateTransform)row.RenderTransform!;

    private void RowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row) return;
        _activeRow = row;
        _pressPoint = e.GetPosition(this);
        _startX = Transform(row).X;
        _dragging = false;
    }

    private void RowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeRow is not { } row || !ReferenceEquals(sender, row)) return;

        var p = e.GetPosition(this);
        double dx = p.X - _pressPoint.X;
        double dy = p.Y - _pressPoint.Y;

        if (!_dragging && Math.Abs(dx) > DragThreshold && Math.Abs(dx) > Math.Abs(dy))
        {
            _dragging = true;
            e.Pointer.Capture(row);
        }

        if (_dragging)
            Transform(row).X = Math.Clamp(_startX + dx, OpenX, 0);
    }

    private void RowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeRow is not { } row) return;

        if (_dragging)
        {
            Snap(row, open: Transform(row).X < OpenX / 2);
        }
        else if (_openRow is not null)
        {
            Snap(_openRow, open: false); // tap anywhere closes an open row first
        }
        else if (row.DataContext is SnippetViewModel snippet && Vm is { } vm)
        {
            vm.CopyCommand.Execute(snippet); // plain tap: copy
        }

        _activeRow = null;
        _dragging = false;
    }

    private void RowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // e.g. the list's scroll gesture took over — settle to nearest state.
        if (_activeRow is { } row && _dragging)
            Snap(row, open: Transform(row).X < OpenX / 2);
        _activeRow = null;
        _dragging = false;
    }

    private void Snap(Border row, bool open)
    {
        if (open && _openRow is not null && !ReferenceEquals(_openRow, row))
            Snap(_openRow, open: false);

        Transform(row).X = open ? OpenX : 0;
        _openRow = open ? row : (ReferenceEquals(_openRow, row) ? null : _openRow);
    }

    // ---- swipe action buttons ----

    private void SwipeEditClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseOpenRow();
        if (sender is Control { DataContext: SnippetViewModel snippet } && Vm is { } vm)
            vm.EditCommand.Execute(snippet);
    }

    private void SwipeDeleteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CloseOpenRow();
        if (sender is Control { DataContext: SnippetViewModel snippet } && Vm is { } vm)
            vm.RequestDeleteCommand.Execute(snippet);
    }

    private void CloseOpenRow()
    {
        if (_openRow is { } row)
            Snap(row, open: false);
    }
}
