using System;
using System.Collections.Generic;
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
/// Running a search that matched nothing: what gets offered, what it takes to run it, and
/// the rule the whole feature hangs on — that an item match always wins.
/// </summary>
public class OfferTests
{
    /// <summary>Unseeded, so the only snippets in play are the ones a test asks for.</summary>
    private static SnippetStore NewStore(params (string Label, string Content)[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-offer-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var (label, content) in snippets)
            store.Add(new Snippet { Label = label, Content = content });
        return store;
    }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required AppSettings Settings { get; init; }
        public List<ExecutionPlan> Ran { get; } = new();
        public ExecutionResult Result { get; set; } = new(true, "Opening qwe.com");
        public int Closes { get; set; }

        /// <summary>
        /// Puts the test's execution engine in place. Called again after a window opens,
        /// since MainWindow wires the real one on the way up — and that one would go
        /// looking for a browser.
        /// </summary>
        public Fixture Attach()
        {
            Vm.Executor = plan => { Ran.Add(plan); return Task.FromResult(Result); };
            return this;
        }
    }

    private static Fixture NewVm(params (string Label, string Content)[] snippets)
    {
        // Settings constructed rather than loaded: nothing here is saved, so no test can
        // write over the developer's own settings.json.
        var settings = new AppSettings();
        var vm = new MainViewModel(NewStore(snippets), settings: settings);
        var fixture = new Fixture { Vm = vm, Settings = settings };
        vm.CloseRequested += () => fixture.Closes++;
        return fixture.Attach();
    }

    private static void Enter(Window window)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\n");
        Dispatcher.UIThread.RunJobs();
    }

    // ---- an item match always wins ----

    [Fact]
    public void ASnippetThatMatchesLeavesNothingToRun()
    {
        // The rule the feature rests on: "lock" is a filter for as long as one snippet
        // answers to it, however tempting the machine control looks.
        var f = NewVm(("Lock the server room door", "Ask reception for the code"));

        f.Vm.FilterText = "lock";

        Assert.Single(f.Vm.Filtered);
        Assert.Null(f.Vm.Offer);
    }

    [Fact]
    public void TheOfferAppearsOnlyOnceTheLastMatchHasGone()
    {
        var f = NewVm(("Restart the build agent", "Ask ops, or use the runbook"));

        f.Vm.FilterText = "restart";
        Assert.Single(f.Vm.Filtered);
        Assert.Null(f.Vm.Offer);

        // Delete it and type nothing new: the same word, now answered by nothing, becomes
        // the machine control. Which is the rule stated from the other side.
        f.Vm.RequestDeleteCommand.Execute((SnippetViewModel)f.Vm.Filtered[0]);
        f.Vm.ConfirmDeleteCommand.Execute(null);

        Assert.Empty(f.Vm.Filtered);
        Assert.NotNull(f.Vm.Offer);
        Assert.Equal(SystemAction.Restart, f.Vm.Offer!.Plan.Action);
    }

    [Fact]
    public void AnEmptySearchNeverOffersAnything()
    {
        var f = NewVm();

        Assert.Empty(f.Vm.Filtered); // no snippets at all, so nothing matched
        Assert.Null(f.Vm.Offer);
    }

    [Fact]
    public void AnUnmatchedSearchThatNamesNothingRunnableOffersNothing()
    {
        var f = NewVm(("Send log files", "Please send us the log files"));

        f.Vm.FilterText = "qwertyuiop";

        Assert.Empty(f.Vm.Filtered);
        Assert.Null(f.Vm.Offer);
    }

    [Fact]
    public void TheOfferCanBeTurnedOff()
    {
        var f = NewVm();
        f.Settings.ExecuteUnmatched = false;

        f.Vm.FilterText = "https://example.com/docs";

        Assert.Null(f.Vm.Offer);
    }

    // ---- running ----

    [Fact]
    public void RunningALinkGoesThroughTheSameEngineAsAMarkedItem_AndDismissesTheWindow()
    {
        var f = NewVm();

        f.Vm.FilterText = "www.qwe.com";
        f.Vm.RunOfferCommand.Execute(null);

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://www.qwe.com", plan.Target);
        Assert.Equal(1, f.Closes); // whatever was opened is what the user is looking at now
    }

    [Fact]
    public void AFailedRunIsReported_AndTheWindowStaysUp()
    {
        var f = NewVm();
        f.Result = ExecutionResult.Failed("Script not found: C:\\tools\\gone.ps1");

        f.Vm.FilterText = "https://example.com/docs";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.True(f.Vm.IsToastError);
        Assert.Equal("Script not found: C:\\tools\\gone.ps1", f.Vm.ToastText);
        Assert.Equal(0, f.Closes); // it has to stay up, or the message is never read
    }

    [Fact]
    public void WithNothingWiredUpToRun_ItSaysSoRatherThanDoingNothing()
    {
        var f = NewVm();
        f.Vm.Executor = null;

        f.Vm.FilterText = "https://example.com/docs";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.True(f.Vm.IsToastError);
        Assert.Equal(0, f.Closes);
    }

    [Fact]
    public void ARunLineIsRemembered_LikeAnyOtherCommandTyped()
    {
        var commands = CommandHistory.InMemory(capacity: 10); // never the real file
        var vm = new MainViewModel(NewStore(), settings: new AppSettings(), commands: commands);
        vm.Executor = plan => Task.FromResult(new ExecutionResult(true, plan.Description));

        vm.FilterText = "www.qwe.com";
        vm.RunOfferCommand.Execute(null);

        Assert.Contains("www.qwe.com", commands.Commands);
    }

    // ---- confirmations ----

    [Fact]
    public void AMachineControlAsksFirst()
    {
        var f = NewVm();

        f.Vm.FilterText = "restart";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.Empty(f.Ran);           // nothing has happened yet
        Assert.NotNull(f.Vm.PendingOffer);
        Assert.Equal("Restart this computer?", f.Vm.PendingOffer!.Question);

        f.Vm.ConfirmOfferCommand.Execute(null);

        Assert.Equal(SystemAction.Restart, Assert.Single(f.Ran).Action);
        Assert.Null(f.Vm.PendingOffer);
    }

    [Fact]
    public void CancellingAMachineControlRunsNothing()
    {
        var f = NewVm();

        f.Vm.FilterText = "hibernate";
        f.Vm.RunOfferCommand.Execute(null);
        f.Vm.CancelOfferCommand.Execute(null);

        Assert.Null(f.Vm.PendingOffer);
        Assert.Empty(f.Ran);
    }

    [Fact]
    public void EscapeClosesTheConfirmationBeforeItTouchesTheFilter()
    {
        var f = NewVm();

        f.Vm.FilterText = "sleep";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.True(f.Vm.HandleEscape());
        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal("sleep", f.Vm.FilterText); // the filter is the *next* thing Esc clears
        Assert.Empty(f.Ran);
    }

    [Fact]
    public void ConfirmationCanBeTurnedOff()
    {
        var f = NewVm();
        f.Settings.ExecuteConfirmSystemActions = false;

        f.Vm.FilterText = "lock";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal(SystemAction.Lock, Assert.Single(f.Ran).Action);
    }

    [Fact]
    public void OnlyMachineControlsAreEverConfirmed()
    {
        // Opening a page is undone by closing it; a restart is not undone at all.
        var f = NewVm();

        f.Vm.FilterText = "https://example.com/docs";
        f.Vm.RunOfferCommand.Execute(null);

        Assert.Null(f.Vm.PendingOffer);
        Assert.Single(f.Ran);
    }

    // ---- the keyboard ----

    [AvaloniaFact]
    public void Enter_RunsTheOffer_WhenThereIsNoRowToActivate()
    {
        var f = NewVm();
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach(); // MainWindow wired the real launcher on the way up

        f.Vm.FilterText = "www.qwe.com";
        Enter(window);

        Assert.Equal("https://www.qwe.com", Assert.Single(f.Ran).Target);
    }

    [AvaloniaFact]
    public void Enter_StillActivatesTheRow_WhenSomethingMatched()
    {
        // The same keystroke with a snippet in the way: the item wins, and nothing is run.
        var f = NewVm(("www.qwe.com", "the address of the thing"));
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        CopyPayload? copied = null;
        f.Vm.ClipboardWriter = payload => { copied = payload; return Task.CompletedTask; };

        f.Vm.FilterText = "www.qwe.com";
        Enter(window);

        Assert.Equal("the address of the thing", copied?.Plain);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public void Enter_ConfirmsAMachineControl_SoTheGestureStaysOnTheKeyboard()
    {
        var f = NewVm();
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        f.Vm.FilterText = "restart";
        Enter(window);
        Assert.NotNull(f.Vm.PendingOffer);
        Assert.Empty(f.Ran);

        Enter(window);

        Assert.Equal(SystemAction.Restart, Assert.Single(f.Ran).Action);
    }

    [AvaloniaFact]
    public void Escape_LeavesTheConfirmationWithoutRunningIt()
    {
        var f = NewVm();
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        f.Vm.FilterText = "restart";
        Enter(window);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(f.Vm.PendingOffer);
        Assert.Empty(f.Ran);
    }

    // ---- the band ----

    [AvaloniaFact]
    public void TheOfferIsShown_WhereTheFirstRowWouldHaveBeen()
    {
        var f = NewVm(("Send log files", "Please send us the log files"));
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var band = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OfferBand");
        Assert.False(band.IsVisible);

        f.Vm.FilterText = @"C:\work\invoices";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(f.Vm.Offer); // no such folder on this machine, and paths are verified

        f.Vm.FilterText = "www.qwe.com";
        Dispatcher.UIThread.RunJobs();

        Assert.True(band.IsVisible);
        var shown = band.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Open link", shown);
        Assert.Contains("https://www.qwe.com", shown);

        // A snippet match takes the band away again.
        f.Vm.FilterText = "log";
        Dispatcher.UIThread.RunJobs();
        Assert.False(band.IsVisible);
    }

    [AvaloniaFact]
    public void ClickingTheOfferRunsIt()
    {
        var f = NewVm();
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        f.Vm.FilterText = "www.qwe.com";
        Dispatcher.UIThread.RunJobs();

        var band = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OfferBand");
        var button = band.GetVisualDescendants().OfType<Button>().First();
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("https://www.qwe.com", Assert.Single(f.Ran).Target);
    }
}
