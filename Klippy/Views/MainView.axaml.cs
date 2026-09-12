using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    // Where the drag has actually got to. Not read back from the transform: its X has a
    // transition, so every set during a drag is animated and reading it gives the
    // in-flight value, tens of pixels behind the finger. Deciding open-vs-closed from
    // that snapped a fast swipe shut just as the buttons came into view.
    private double _dragX;
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
        {
            vm.ClipboardWriter = payload => RichTextClipboard.WriteAsync(top.Clipboard, payload);
            vm.ClipboardReader = () => top.Clipboard?.TryGetTextAsync() ?? Task.FromResult<string?>(null);
            // A phone has a browser but no shell, so a snippet marked Execute opens its
            // link here and says so plainly when it names a script instead.
            vm.Executor = plan => OpenOnDeviceAsync(top, plan);
        }

        // Keep content clear of notches/status bars and match the system bars to the theme.
        if (top.InsetsManager is { } insets)
        {
            insets.SystemBarColor = Color.Parse("#16181D");
            ApplySafeArea(insets.SafeAreaPadding);
            insets.SafeAreaChanged += (_, args) => ApplySafeArea(args.SafeAreaPadding);
        }
    }

    private void ApplySafeArea(Thickness padding) => Root.Margin = padding;

    /// <summary>
    /// Executing on a mobile device: the platform's own launcher opens a link. There is
    /// no shell to run a script in, and saying that is better than a tap that does
    /// nothing.
    /// </summary>
    private static async Task<ExecutionResult> OpenOnDeviceAsync(TopLevel top, ExecutionPlan plan)
    {
        if (plan.Kind != ExecutionKind.Url)
            return ExecutionResult.Failed("Only links can be opened on this device.");

        if (top.Launcher is not { } launcher || !Uri.TryCreate(plan.Target, UriKind.Absolute, out var uri))
            return ExecutionResult.Failed($"Could not open {plan.Target}");

        try
        {
            return await launcher.LaunchUriAsync(uri)
                ? new ExecutionResult(true, plan.Description)
                : ExecutionResult.Failed($"Could not open {plan.Target}");
        }
        catch (Exception e)
        {
            // Whatever the platform makes of a link it will not take, a tap must not
            // bring the app down with it.
            return ExecutionResult.Failed($"Could not open {plan.Target}: {e.Message}");
        }
    }

    // ---- swipe gesture ----

    private static TranslateTransform Transform(Border row) => (TranslateTransform)row.RenderTransform!;

    private void RowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row) return;
        _activeRow = row;
        _pressPoint = e.GetPosition(this);
        _startX = ReferenceEquals(_openRow, row) ? OpenX : 0;
        _dragX = _startX;
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
        {
            _dragX = Math.Clamp(_startX + dx, OpenX, 0);
            Transform(row).X = _dragX;
        }
    }

    private void RowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeRow is not { } row) return;

        if (_dragging)
        {
            Snap(row, open: _dragX < OpenX / 2);
        }
        else if (_openRow is not null)
        {
            Snap(_openRow, open: false); // tap anywhere closes an open row first
        }
        else if (row.DataContext is SnippetViewModel snippet && Vm is { } vm)
        {
            // Plain tap: whatever the snippet is marked for — a copy, or a run.
            vm.ActivateCommand.Execute(snippet);
        }

        _activeRow = null;
        _dragging = false;
    }

    private void RowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // e.g. the list's scroll gesture took over — settle to nearest state.
        if (_activeRow is { } row && _dragging)
            Snap(row, open: _dragX < OpenX / 2);
        _activeRow = null;
        _dragging = false;
    }

    private void Snap(Border row, bool open)
    {
        if (open && _openRow is not null && !ReferenceEquals(_openRow, row))
            Snap(_openRow, open: false);

        Transform(row).X = open ? OpenX : 0;
        _dragX = open ? OpenX : 0;
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
