using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The history key pressed while an overlay has the window: the clipboard history comes up over it, a clip
/// picked there is copied, and the overlay is still waiting underneath for the paste.
/// </summary>
public class HistoryPickerTests
{
    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public CopyPayload? Copied { get; set; }
        public int Closes { get; set; }
    }

    private static Fixture NewVm(params string[] clips)
    {
        var history = ClipHistoryStore.InMemory();
        foreach (var text in clips)
            history.Add(new ClipEntry { Text = text, SourceApp = "chrome" });

        // Closing after a clipboard copy is on, as it is by default: the picker must stand aside from it.
        var settings = AppSettings.Load(
            Path.Combine(Path.GetTempPath(), $"klippy-picker-settings-{Guid.NewGuid():N}.json"));
        settings.CloseAfterClipboardCopy = true;

        var store = new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-picker-{Guid.NewGuid():N}.json"));
        var f = new Fixture { Vm = new MainViewModel(store, history, settings) };
        f.Vm.ClipboardWriter = payload => { f.Copied = payload; return Task.CompletedTask; };
        f.Vm.CloseRequested += () => f.Closes++;
        return f;
    }

    private static MainWindow Show(Fixture f)
    {
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Opening hands the view model the real clipboard; the test's own goes back on top of it.
        f.Vm.ClipboardWriter = payload => { f.Copied = payload; return Task.CompletedTask; };
        return window;
    }

    private static void Press(MainWindow window, Key key, PhysicalKey physical,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    // ---- when it comes up ----

    [AvaloniaFact]
    public void OverTheEditor_TheHistoryKeyBringsUpThePicker_NewestFirst()
    {
        var f = NewVm("older", "newer");
        f.Vm.NewCommand.Execute(null);

        Assert.True(f.Vm.ToggleHistoryPicker());

        Assert.NotNull(f.Vm.HistoryPicker);
        Assert.Equal(new[] { "newer", "older" }, f.Vm.HistoryPicker!.Clips.Select(c => c.Label));
        Assert.Equal("newer", f.Vm.HistoryPicker.Selected?.Label);
        Assert.NotNull(f.Vm.Editor);
        Assert.False(f.Vm.IsHistoryMode); // the list behind is left as it was
    }

    [AvaloniaFact]
    public void AtTheMainWindow_ThePickerIsNotWhatTheKeyMeans()
    {
        var f = NewVm("a clip");

        Assert.False(f.Vm.ToggleHistoryPicker());
        Assert.Null(f.Vm.HistoryPicker);
    }

    [AvaloniaFact]
    public void WithNoHistory_ThereIsNoPicker()
    {
        var store = new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-picker-{Guid.NewGuid():N}.json"));
        var vm = new MainViewModel(store, history: null);
        vm.NewCommand.Execute(null);

        Assert.False(vm.ToggleHistoryPicker());
        Assert.Null(vm.HistoryPicker);
    }

    [AvaloniaFact]
    public void PressedAgain_ThePickerGoesAway_AndTheEditorStays()
    {
        var f = NewVm("a clip");
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();

        Assert.True(f.Vm.ToggleHistoryPicker());

        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void Escape_ClosesThePickerAlone()
    {
        var f = NewVm("a clip");
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();

        Assert.True(f.Vm.HandleEscape());

        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void BeingDismissed_PutsThePickerAway_ButNotTheEditor()
    {
        var f = NewVm("a clip");
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();

        f.Vm.Dismissed();

        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void TheFilterNarrowsTheClips()
    {
        var f = NewVm("connection string", "docker prune", "another connection");
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();

        f.Vm.HistoryPicker!.FilterText = "conn";

        Assert.Equal(new[] { "another connection", "connection string" },
            f.Vm.HistoryPicker.Clips.Select(c => c.Label).Order());
        Assert.Same(f.Vm.HistoryPicker.Clips[0], f.Vm.HistoryPicker.Selected);
    }

    // ---- picking ----

    [AvaloniaFact]
    public async Task Picking_CopiesTheClip_AndLeavesTheEditorAndWindowUp()
    {
        var f = NewVm("older", "newer");
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();
        f.Vm.HistoryPicker!.Navigate(1);

        await f.Vm.PickClipCommand.ExecuteAsync(null);

        Assert.Equal("older", f.Copied?.Plain);
        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
        Assert.Equal(0, f.Closes);
    }

    // ---- in the window ----

    [AvaloniaFact]
    public void InTheWindow_ArrowsAndEnterPickAClip_AndTheKeyboardGoesBackToTheEditor()
    {
        var f = NewVm("older", "newer");
        var window = Show(f);
        f.Vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editorField = window.FocusManager?.GetFocusedElement();
        Assert.IsType<TextBox>(editorField);

        f.Vm.ToggleHistoryPicker();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("PickerFilter", (window.FocusManager?.GetFocusedElement() as TextBox)?.Name);

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Press(window, Key.Enter, PhysicalKey.Enter);

        Assert.Equal("older", f.Copied?.Plain);
        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
        Assert.Same(editorField, window.FocusManager?.GetFocusedElement());
    }

    [AvaloniaFact]
    public void InTheWindow_CtrlEnterInThePicker_DoesNotSaveTheEditor()
    {
        var f = NewVm("a clip");
        var window = Show(f);
        f.Vm.NewCommand.Execute(null);
        f.Vm.Editor!.Label = "half-written";
        f.Vm.Editor.Content = "body";
        f.Vm.ToggleHistoryPicker();
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.Control);

        Assert.NotNull(f.Vm.Editor); // still open, so not saved
    }

    [AvaloniaFact]
    public void InTheWindow_EscapeInThePicker_LeavesTheEditorOpen()
    {
        var f = NewVm("a clip");
        var window = Show(f);
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Escape, PhysicalKey.Escape);

        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void InTheWindow_TheHistoryKeyItself_OpensAndClosesThePicker()
    {
        // Where the key is not registered system-wide, the window sees it — and answers it the same way.
        if (OperatingSystem.IsMacOS()) return; // ⌥⌘J there; the combination below is the Windows default

        var f = NewVm("a clip");
        var window = Show(f);
        f.Vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.J, PhysicalKey.J, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.NotNull(f.Vm.HistoryPicker);

        Press(window, Key.J, PhysicalKey.J, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.Null(f.Vm.HistoryPicker);
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void InTheWindow_TheHistoryKeyAtTheMainWindow_ShowsTheHistory()
    {
        if (OperatingSystem.IsMacOS()) return;

        var f = NewVm("a clip");
        var window = Show(f);

        Press(window, Key.J, PhysicalKey.J, RawInputModifiers.Control | RawInputModifiers.Alt);

        Assert.True(f.Vm.IsHistoryMode);
        Assert.Null(f.Vm.HistoryPicker);
    }

    [AvaloniaFact]
    public void OverTheEditor_Renders_AndCapturesScreenshot()
    {
        var f = NewVm("https://github.com/seankearon/Klippy/pull/2", "docker system prune -af --volumes",
            "DE44 5001 0517 5407 3249 31");
        var window = Show(f);
        f.Vm.NewCommand.Execute(null);
        f.Vm.ToggleHistoryPicker();
        Dispatcher.UIThread.RunJobs();

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
        Directory.CreateDirectory(dir);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(dir, "screenshot-history-picker.png"));
    }
}
