using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
/// The command MRU as the keyboard meets it: what gets remembered, what the arrows do
/// with it, and — just as much the point — what they still do without it.
/// </summary>
public class CommandMruTests
{
    private static string ArtifactsDir
    {
        get
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static SnippetStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), $"klippy-mru-{Guid.NewGuid():N}.json"));

    private static (MainViewModel Vm, CommandHistory Commands) NewVm(params string[] remembered)
    {
        var commands = CommandHistory.InMemory();
        // Oldest first, so the last one listed is the one the MRU offers first.
        foreach (var line in remembered) commands.Record(line);

        var vm = new MainViewModel(NewStore(), commands: commands);
        vm.ClipboardWriter = _ => Task.CompletedTask;
        return (vm, commands);
    }

    /// <summary>A window around the view model, with the real clipboard and launcher taken back off.</summary>
    private static MainWindow Open(MainViewModel vm, List<ExecutionPlan>? ran = null)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // MainWindow wires the real ones on the way up, and the real executor would go
        // looking for a browser.
        vm.ClipboardWriter = _ => Task.CompletedTask;
        vm.Executor = plan =>
        {
            ran?.Add(plan);
            return Task.FromResult(new ExecutionResult(true, plan.Description));
        };
        return window;
    }

    private static void Press(Window window, Key key) =>
        window.KeyPress(key, RawInputModifiers.None, key switch
        {
            Key.Down => PhysicalKey.ArrowDown,
            Key.Up => PhysicalKey.ArrowUp,
            Key.Enter => PhysicalKey.Enter,
            _ => PhysicalKey.Escape,
        }, null);

    // ---- what gets remembered ----

    [AvaloniaFact]
    public void ALineThatCopiedSomethingIsRemembered()
    {
        var (vm, commands) = NewVm();

        vm.FilterText = "slf";
        vm.CopySelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "slf" }, commands.Commands);
    }

    [AvaloniaFact]
    public void BrowsingAndCopyingWithoutTypingRemembersNothing()
    {
        var (vm, commands) = NewVm();

        // Nothing was typed, so there is no line to recall — arrowing to a row and
        // pressing Enter is not a command.
        vm.CopySelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(commands.Commands);
    }

    [AvaloniaFact]
    public void RunningASnippetRemembersTheInvocationThatRanIt()
    {
        var (vm, commands) = NewVm();
        var ran = new List<ExecutionPlan>();
        Open(vm, ran);

        vm.FilterText = "? cats"; // the seeded Google search, marked Execute
        vm.ActivateSelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(ran);
        Assert.Equal(new[] { "? cats" }, commands.Commands);
    }

    [AvaloniaFact]
    public void AFilterTypedAgainstClipsIsNotACommand()
    {
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "invoice-2026-08.pdf", SourceApp = "explorer" });
        var commands = CommandHistory.InMemory();
        var vm = new MainViewModel(NewStore(), history, commands: commands)
        {
            ClipboardWriter = _ => Task.CompletedTask,
        };

        vm.ShowHistory();
        vm.FilterText = "invoice";
        vm.CopySelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The app already says a filter typed against clips means nothing against
        // snippets; it means nothing as a command either.
        Assert.Empty(commands.Commands);
    }

    // ---- opening and browsing ----

    [AvaloniaFact]
    public void DownOnAnEmptySearchBoxOpensTheMruAtTheNewestCommand()
    {
        var (vm, _) = NewVm("dp", "? cats");
        var window = Open(vm);

        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsCommandsOpen);
        Assert.Equal(new[] { "? cats", "dp" }, vm.Commands);
        // The line is in the box, so the row underneath already shows what Enter will do.
        Assert.Equal("? cats", vm.FilterText);
        Assert.Equal("Google search", vm.SelectedSnippet?.Label);
    }

    [AvaloniaFact]
    public void TheArrowsThenMoveThroughTheCommands()
    {
        var (vm, _) = NewVm("dp", "? cats");
        var window = Open(vm);

        Press(window, Key.Down);
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("dp", vm.FilterText);

        Press(window, Key.Up);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("? cats", vm.FilterText);

        // The bottom is the bottom, as it is in the list.
        Press(window, Key.Down);
        Press(window, Key.Down);
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("dp", vm.FilterText);
    }

    [AvaloniaFact]
    public void UpPastTheTopLeavesTheMruAndPutsBackWhatWasTyped()
    {
        var (vm, _) = NewVm("? cats and dogs");
        var window = Open(vm);

        vm.FilterText = "? c";
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("? cats and dogs", vm.FilterText);

        Press(window, Key.Up);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCommandsOpen);
        Assert.Equal("? c", vm.FilterText);
    }

    [AvaloniaFact]
    public void TypingOpensTheMruOnWhatTheLineCouldStillBecome()
    {
        var (vm, _) = NewVm("dp --volumes", "? cats", "? dogs");

        vm.FilterText = "? ";

        Assert.True(vm.IsCommandsOpen);
        Assert.Equal(new[] { "? dogs", "? cats" }, vm.Commands);
        // Nothing is selected yet: writing a command into a box still being typed in
        // would take the line away from the person typing it.
        Assert.Null(vm.SelectedCommand);
        Assert.Equal("? ", vm.FilterText);
    }

    [AvaloniaFact]
    public void ALineNoCommandStartsWithClosesItAgain()
    {
        var (vm, _) = NewVm("? cats");

        vm.FilterText = "? c";
        Assert.True(vm.IsCommandsOpen);

        vm.FilterText = "? cx";
        Assert.False(vm.IsCommandsOpen);
        Assert.Empty(vm.Commands);
    }

    [AvaloniaFact]
    public void BeingDismissedTakesTheMruWithTheLineItWasBrowsing()
    {
        // The window has been put away, so restoring the line being typed would only be
        // restoring it into a box that is about to be emptied anyway.
        var (vm, _) = NewVm("? cats");
        var window = Open(vm);

        vm.FilterText = "? c";
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCommandsOpen);

        vm.Dismissed();

        Assert.False(vm.IsCommandsOpen);
        Assert.Empty(vm.Commands);
        Assert.Equal("", vm.FilterText);
    }

    [AvaloniaFact]
    public void ClearingTheBoxByHandClosesTheMruRatherThanOfferingEverything()
    {
        var (vm, _) = NewVm("? cats");

        vm.FilterText = "? c";
        Assert.True(vm.IsCommandsOpen);

        vm.FilterText = "";
        Assert.False(vm.IsCommandsOpen);
    }

    // ---- what the arrows still do ----

    [AvaloniaFact]
    public void WithTheMruClosedTheArrowsAreTheListsAsBefore()
    {
        var (vm, _) = NewVm("? cats");
        var window = Open(vm);

        vm.FilterText = "e"; // matches snippets, starts no command
        Assert.False(vm.IsCommandsOpen);

        var first = vm.SelectedSnippet;
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(first, vm.SelectedSnippet);
        Assert.Equal(vm.Filtered[1], vm.SelectedSnippet);
    }

    [AvaloniaFact]
    public void WithNothingRememberedDownStillStepsIntoTheList()
    {
        var (vm, _) = NewVm();
        var window = Open(vm);

        var first = vm.SelectedSnippet;
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCommandsOpen);
        Assert.Equal(vm.Filtered[1], vm.SelectedSnippet);
        Assert.NotEqual(first, vm.SelectedSnippet);
    }

    [AvaloniaFact]
    public void TheHistoryHasNoCommandsOfItsOwn()
    {
        var history = ClipHistoryStore.InMemory();
        foreach (var text in new[] { "one", "two" })
            history.Add(new ClipEntry { Text = text, SourceApp = "chrome" });
        var vm = new MainViewModel(NewStore(), history, commands: CommandHistory.InMemory());
        var window = Open(vm);
        vm.ShowHistory();

        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCommandsOpen);
        Assert.Equal(vm.Filtered[1], vm.SelectedSnippet);
    }

    [AvaloniaFact]
    public void SwitchingViewsPutsTheMruAway()
    {
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "one", SourceApp = "chrome" });
        var commands = CommandHistory.InMemory();
        commands.Record("? cats");
        var vm = new MainViewModel(NewStore(), history, commands: commands);
        var window = Open(vm);

        Press(window, Key.Down);
        Assert.True(vm.IsCommandsOpen);

        vm.ShowHistory();

        Assert.False(vm.IsCommandsOpen);
        Assert.Equal("", vm.FilterText);
    }

    // ---- leaving and taking ----

    [AvaloniaFact]
    public void EscapeClosesTheMruBeforeItTouchesTheFilter()
    {
        var (vm, _) = NewVm("? cats and dogs");
        var window = Open(vm);

        vm.FilterText = "? c";
        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("? cats and dogs", vm.FilterText);

        Press(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsCommandsOpen);
        Assert.Equal("? c", vm.FilterText); // what was typed, not what was browsed

        Press(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.FilterText);
    }

    [AvaloniaFact]
    public void EnterRunsTheRecalledCommandAndPutsTheMruAway()
    {
        var (vm, commands) = NewVm("? cats");
        var ran = new List<ExecutionPlan>();
        var window = Open(vm, ran);

        Press(window, Key.Down);
        Press(window, Key.Enter);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCommandsOpen);
        Assert.Single(ran);
        Assert.Equal("https://www.google.com/search?q=cats", ran[0].Target);
        // Recalled and run is used again: it stays at the top rather than doubling up.
        Assert.Equal(new[] { "? cats" }, commands.Commands);
    }

    [AvaloniaFact]
    public void ClickingACommandTakesTheLineWithoutRunningIt()
    {
        var (vm, _) = NewVm("? cats");
        var ran = new List<ExecutionPlan>();
        var window = Open(vm, ran);

        vm.FilterText = "?";
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCommandsOpen);

        var row = CommandRows(window).Single();
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCommandsOpen);
        Assert.Equal("? cats", vm.FilterText); // the line is yours to edit, or to run with Enter
        Assert.Empty(ran);
    }

    [AvaloniaFact]
    public void RecentCommands_Render_AndCaptureScreenshot()
    {
        var (vm, _) = NewVm("dp --volumes", "ooo", "? cats and dogs");
        var window = Open(vm);

        Press(window, Key.Down);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, CommandRows(window).Count);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-commands.png"));
    }

    [AvaloniaFact]
    public void TheSearchBoxOffersTheKeyOnlyOnceThereIsSomethingToRecall()
    {
        var (vm, _) = NewVm();
        Assert.DoesNotContain("recent", vm.SearchWatermark);

        vm.FilterText = "slf";
        vm.CopySelectedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("↓ recent", vm.SearchWatermark);
        Assert.Equal("Filter clipboard history", NewHistoryVm().SearchWatermark);
    }

    /// <summary>A view model showing clips, where the search box is a filter and not a command line.</summary>
    private static MainViewModel NewHistoryVm()
    {
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "one", SourceApp = "chrome" });
        var vm = new MainViewModel(NewStore(), history, commands: CommandHistory.InMemory());
        vm.ShowHistory();
        return vm;
    }

    private static List<ListBoxItem> CommandRows(Window window) =>
        window.GetVisualDescendants().OfType<ListBox>()
            .Single(l => l.Classes.Contains("commands"))
            .GetVisualDescendants().OfType<ListBoxItem>()
            .ToList();
}
