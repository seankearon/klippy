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

    /// <summary>
    /// A real folder, because the view model probes the real file system — a made-up
    /// Windows path would simply not be there when these tests run on Linux, and the
    /// interaction being tested is with the item list rather than with the probe.
    /// </summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"klippy-folder-{Guid.NewGuid():N}");

        /// <summary>As Explorer's address bar and Copy as path both give it: with the separator.</summary>
        public string Trailing => Path + System.IO.Path.DirectorySeparatorChar;

        /// <summary>A file deep inside it — the kind of path a snippet actually holds.</summary>
        public string FileInside =>
            System.IO.Path.Combine(Path, "tooling", "dsleditor", "bin", "DslEditor.exe");

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp folder left behind is not a failed test */ }
        }
    }

    [Fact]
    public void APathIsOfferedEvenWhenASnippetHappensToMentionIt()
    {
        // Reported from real use: a snippet whose body is a path *inside* the folder makes
        // every word of the folder's own path match, so the offer to open it vanished.
        // Nobody types a folder's path to filter a list, so the match is a coincidence.
        using var folder = new TempFolder();
        var f = NewVm(("DslEditor", folder.FileInside));

        f.Vm.FilterText = folder.Trailing;

        Assert.Single(f.Vm.Filtered);   // the snippet still matches, and still shows
        Assert.NotNull(f.Vm.Offer);     // and the folder is offered anyway
        Assert.Equal("Open folder", f.Vm.Offer!.Verb);
    }

    [Fact]
    public void TheOfferTakesTheSelection_SoEnterActsOnWhatItSays()
    {
        // Standing beside rows, the offer is what Enter runs — so no row may sit there
        // wearing the badge that says the keystroke is coming to it.
        using var folder = new TempFolder();
        var f = NewVm(("DslEditor", folder.FileInside));

        f.Vm.FilterText = folder.Trailing;

        Assert.NotNull(f.Vm.Offer);
        Assert.Null(f.Vm.SelectedSnippet);
    }

    [AvaloniaFact]
    public void DownMovesBackIntoTheList_AndEnterThenActivatesTheRow()
    {
        // The way out of the offer taking the selection: the list is one keystroke away,
        // and once you are in it Enter means what it always did.
        using var folder = new TempFolder();
        var f = NewVm(("DslEditor", folder.FileInside));
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        CopyPayload? copied = null;
        f.Vm.ClipboardWriter = payload => { copied = payload; return Task.CompletedTask; };

        f.Vm.FilterText = folder.Trailing;
        Assert.Null(f.Vm.SelectedSnippet);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(f.Vm.SelectedSnippet);

        Enter(window);

        Assert.Equal(folder.FileInside, copied?.Plain);
        Assert.Empty(f.Ran); // the offer did not take the keystroke once a row had it
    }

    [Fact]
    public void AnItemThatIsTheLineStillWins()
    {
        // The other side of it: a snippet whose text *is* the link you typed was plausibly
        // what you were looking for. Mentioning it is not being it.
        var f = NewVm(("Pricing page", "https://www.qwe.com/pricing"));

        f.Vm.FilterText = "https://www.qwe.com/pricing";

        Assert.Single(f.Vm.Filtered);
        Assert.Null(f.Vm.Offer);
        Assert.NotNull(f.Vm.SelectedSnippet); // and the row keeps the selection
    }

    [Fact]
    public void AnItemThatIsWhatTheLineResolvedToWinsToo()
    {
        // A variable and the folder it expands to are the same request, so a snippet
        // holding either spelling counts as the thing named.
        using var folder = new TempFolder();
        Environment.SetEnvironmentVariable("KLIPPY_TEST_FOLDER", folder.Path);
        try
        {
            // The label is what the typed line matches on; the body is what it resolves to.
            var f = NewVm(("Klippy test folder", folder.Path));

            f.Vm.FilterText = "%KLIPPY_TEST_FOLDER%";

            Assert.Single(f.Vm.Filtered);
            Assert.Null(f.Vm.Offer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KLIPPY_TEST_FOLDER", null);
        }
    }

    [Fact]
    public void AMachineControlIsStillBeatenByAMatch()
    {
        // The original rule, unchanged: "lock" is an ordinary word, and a snippet that
        // answers to it was plausibly what was being looked for.
        var f = NewVm(("Lock the server room door", "Ask reception for the code"));

        f.Vm.FilterText = "lock";

        Assert.Single(f.Vm.Filtered);
        Assert.Null(f.Vm.Offer);
        Assert.NotNull(f.Vm.SelectedSnippet);
    }

    [Fact]
    public void InTheClipboardHistory_AMatchingClipAlwaysWins()
    {
        // Clips are mostly paths and links, so a line that looks like one is far more
        // likely to be someone hunting for the clip they copied than an instruction.
        using var folder = new TempFolder();
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = folder.FileInside, SourceApp = "explorer" });

        var vm = new MainViewModel(NewStore(), history, new AppSettings());
        vm.ShowHistory();

        vm.FilterText = folder.Trailing;

        Assert.Single(vm.Filtered);
        Assert.Null(vm.Offer);
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
