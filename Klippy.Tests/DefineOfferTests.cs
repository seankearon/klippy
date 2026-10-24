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
/// Typing a name the variables file defines: each value it stands for is offered to run,
/// one band apiece, and the arrows, Enter and a click choose between them.
/// </summary>
// Points AppSettings.Current at a variables file of its own, which is process-wide while it
// is in force — the same collection as the other tests that do.
[Collection("storage-locations")]
public class DefineOfferTests
{
    /// <summary>
    /// A real folder with a solution in it, because the view model probes the real file
    /// system, and a variables file naming both as <c>klippy</c> — the reported file,
    /// quotes and all.
    /// </summary>
    private sealed class Checkout : IDisposable
    {
        private readonly string _top = Path.Combine(Path.GetTempPath(), $"klippy-define-{Guid.NewGuid():N}");
        private readonly VariablesTests.VariablesFileScope _scope;

        public string Folder { get; }
        public string Solution { get; }

        public Checkout(string moreDefines = "")
        {
            Folder = Path.Combine(_top, "Klippy");
            Solution = Path.Combine(Folder, "Klippy.slnx");
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Solution, "");

            _scope = new VariablesTests.VariablesFileScope(
                $"klippy=\"{Folder}\"\nklippy=\"{Solution}\"\n{moreDefines}");
        }

        public void Dispose()
        {
            _scope.Dispose();
            try { Directory.Delete(_top, recursive: true); }
            catch (IOException) { /* a temp folder left behind is not a failed test */ }
        }
    }

    private static string ArtifactsDir
    {
        get
        {
            // bin/<config>/<tfm> -> project dir -> repo root
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required AppSettings Settings { get; init; }
        public List<ExecutionPlan> Ran { get; } = new();

        /// <summary>Called again once a window is up: MainWindow wires the real launcher on the way.</summary>
        public Fixture Attach()
        {
            Vm.Executor = plan =>
            {
                Ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, plan.Description));
            };
            return this;
        }
    }

    private static Fixture NewVm(params (string Label, string Content)[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-define-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var (label, content) in snippets)
            store.Add(new Snippet { Label = label, Content = content });

        // Constructed rather than loaded, so nothing here can write over a real settings.json.
        var settings = new AppSettings();
        return new Fixture { Vm = new MainViewModel(store, settings: settings), Settings = settings }.Attach();
    }

    private static (MainWindow Window, Fixture F) NewWindow(params (string Label, string Content)[] snippets)
    {
        var f = NewVm(snippets);
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();
        return (window, f);
    }

    private static void Press(MainWindow window, Key key, PhysicalKey physical, string? text = null)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, text);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressWithCtrl(MainWindow window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.Control, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<Border> Bands(MainWindow window) =>
        window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "OfferList")
            .GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("offer")).ToList();

    // ---- what is offered ----

    [Fact]
    public void TypingAName_OffersEachOfItsValues_TheOneItMeansOnItsOwnFirst()
    {
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "klippy";

        Assert.Collection(f.Vm.Offers,
            solution =>
            {
                // The last line, which is what %klippy% means in an item: Enter does that.
                Assert.Equal(ExecutionKind.Reveal, solution.Plan.Kind);
                Assert.Equal(checkout.Solution, solution.Detail);
            },
            folder =>
            {
                Assert.Equal("Open folder", folder.Verb);
                Assert.Equal(checkout.Folder, folder.Detail);
            },
            console =>
            {
                // After the values: one console between them, the solution being in the folder.
                Assert.Equal(ExecutionKind.Console, console.Plan.Kind);
                Assert.Equal("Open console", console.Verb);
                Assert.Equal(checkout.Folder, console.Detail);
            });

        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer);
        Assert.True(f.Vm.Offers[0].IsSelected);
        Assert.False(f.Vm.Offers[1].IsSelected);
        Assert.False(f.Vm.Offers[2].IsSelected);
    }

    [Fact]
    public void PercentSigns_MakeNoDifference_AndTheLineIsNotOfferedTwice()
    {
        // %klippy% was already offered as the path it resolves to; that offer and the define's
        // own for the same line are one offer, not two.
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "%klippy%";

        Assert.Equal(
            new[] { checkout.Solution, checkout.Folder, checkout.Folder },
            f.Vm.Offers.Select(o => o.Detail));
        Assert.Equal(ExecutionKind.Console, f.Vm.Offers[2].Plan.Kind);
    }

    [Fact]
    public void AFlavourNamedInTheLine_OffersThatOneAlone()
    {
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "klippy:folder";

        // That one value, and a console in it.
        Assert.Equal(new[] { "Open folder", "Open console" }, f.Vm.Offers.Select(o => o.Verb));
        Assert.All(f.Vm.Offers, o => Assert.Equal(checkout.Folder, o.Detail));
    }

    [Fact]
    public void ALink_IsOfferedToOpen()
    {
        using var checkout = new Checkout("wiki=https://wiki.example.com/team");
        var f = NewVm();

        f.Vm.FilterText = "wiki";

        var offer = Assert.Single(f.Vm.Offers);
        Assert.Equal("Open link", offer.Verb);
        Assert.Equal("https://wiki.example.com/team", offer.Detail);
    }

    [Fact]
    public void QuotedItIsTheWordItself()
    {
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "\"klippy\"";

        Assert.Empty(f.Vm.Offers);
    }

    [Fact]
    public void TheOfferCanBeTurnedOff_WithTheRestOfThem()
    {
        using var checkout = new Checkout();
        var f = NewVm();
        f.Settings.ExecuteUnmatched = false;

        f.Vm.FilterText = "klippy";

        Assert.Empty(f.Vm.Offers);
    }

    [Fact]
    public void InTheHistory_ANameIsASearchForAClip()
    {
        using var checkout = new Checkout();
        var vm = new MainViewModel(
            new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-define-{Guid.NewGuid():N}.json"),
                seedIfEmpty: false),
            ClipHistoryStore.InMemory(), new AppSettings());
        vm.ShowHistory();

        vm.FilterText = "klippy";

        Assert.Empty(vm.Offers);
    }

    // ---- beside the snippets that use it ----

    [Fact]
    public void ASnippetUsingTheName_KeepsEnter_AndTheValuesStandAboveIt()
    {
        // Every snippet written with %klippy% in it answers to "klippy", so a matching row
        // cannot be what takes the offers away — nor can they take Enter from it, since a
        // bare name is as often a search as anything.
        using var checkout = new Checkout();
        var f = NewVm(("Open Klippy in Rider", "%r% %klippy:file%"));

        f.Vm.FilterText = "klippy";

        Assert.Single(f.Vm.Filtered);
        Assert.Equal(3, f.Vm.Offers.Count);
        Assert.Same(f.Vm.Filtered[0], f.Vm.SelectedSnippet);
        Assert.False(f.Vm.IsOfferSelected);
        Assert.DoesNotContain(f.Vm.Offers, o => o.IsSelected);
    }

    [Fact]
    public void UpFromTheRow_ClimbsThroughTheOffers_NearestFirst()
    {
        using var checkout = new Checkout();
        var f = NewVm(("Open Klippy in Rider", "%r% %klippy:file%"));
        f.Vm.FilterText = "klippy";

        f.Vm.MoveSelection(-1);
        Assert.Same(f.Vm.Offers[2], f.Vm.SelectedOffer); // the band nearest the list
        Assert.Null(f.Vm.SelectedSnippet);

        f.Vm.MoveSelection(-1);
        Assert.Same(f.Vm.Offers[1], f.Vm.SelectedOffer);

        f.Vm.MoveSelection(-1);
        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer);

        f.Vm.MoveSelection(-1);
        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer); // the top stays the top

        f.Vm.MoveSelection(1);
        f.Vm.MoveSelection(1);
        f.Vm.MoveSelection(1);
        Assert.Same(f.Vm.Filtered[0], f.Vm.SelectedSnippet);
        Assert.DoesNotContain(f.Vm.Offers, o => o.IsSelected);
    }

    [Fact]
    public void AQuickCodeThatIsAlsoAName_StillHasItsItemOnEnter()
    {
        // The file defining a name must never put the item behind the same code out of reach
        // of the line that invokes it: the item keeps Enter, and the values stand above it.
        using var checkout = new Checkout();
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-define-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        store.Add(new Snippet { Label = "Open in Rider", Content = "%r% %P:file%", QuickCode = "klippy" });
        var vm = new MainViewModel(store, settings: new AppSettings());

        vm.FilterText = "klippy";

        Assert.Equal("Open in Rider", vm.SelectedSnippet?.Label);
        Assert.Equal(3, vm.Offers.Count);
        Assert.False(vm.IsOfferSelected);
    }

    [Fact]
    public void WhatTheLineItselfNamed_StillTakesEnter_AheadOfTheDefine()
    {
        // %klippy% beside a row always took Enter from it, being a path rather than a word,
        // and still does — with klippy's own first value on it, so the two spellings agree.
        using var checkout = new Checkout();
        var f = NewVm(("Open Klippy in Rider", "%r% %klippy:file%"));

        f.Vm.FilterText = "%klippy%";

        Assert.Null(f.Vm.SelectedSnippet);
        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer);
        Assert.Equal(checkout.Solution, f.Vm.SelectedOffer!.Detail);
    }

    // ---- kinds the settings say to open ----

    [Fact]
    public void WithSolutionsOnTheList_ANameOffersToOpenIt_ShowIt_OrOpenItsFolder()
    {
        using var checkout = new Checkout();
        var f = NewVm();
        f.Settings.ExecuteOpenExtensions = [".slnx"];

        f.Vm.FilterText = "klippy";

        Assert.Collection(f.Vm.Offers,
            open =>
            {
                Assert.Equal(ExecutionKind.Document, open.Plan.Kind);
                Assert.Equal(checkout.Solution, open.Detail);
            },
            show =>
            {
                // Right after it: being able to open it is no reason to lose where it is.
                Assert.StartsWith("Show in ", show.Verb);
                Assert.Equal(checkout.Solution, show.Detail);
            },
            folder => Assert.Equal(checkout.Folder, folder.Detail),
            console => Assert.Equal(ExecutionKind.Console, console.Plan.Kind));

        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer);
    }

    [Fact]
    public async Task Enter_OpensTheSolution()
    {
        using var checkout = new Checkout();
        var f = NewVm();
        f.Settings.ExecuteOpenExtensions = [".slnx"];

        f.Vm.FilterText = "klippy";
        await f.Vm.RunOfferCommand.ExecuteAsync(null);

        var ran = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Document, ran.Kind);
        Assert.Equal(checkout.Solution, ran.Target);
    }

    [Fact]
    public void PercentSigns_PutTheSameThingOnEnter()
    {
        // Typed as %klippy%, the line resolves to the solution and would only show it; as
        // the name of a define it is klippy, and klippy opens it.
        using var checkout = new Checkout();
        var f = NewVm(("Open Klippy in Rider", "%r% %klippy:file%"));
        f.Settings.ExecuteOpenExtensions = [".slnx"];

        f.Vm.FilterText = "%klippy%";

        Assert.Equal(4, f.Vm.Offers.Count);
        Assert.Same(f.Vm.Offers[0], f.Vm.SelectedOffer);
        Assert.Equal(ExecutionKind.Document, f.Vm.SelectedOffer!.Plan.Kind);
    }

    [Fact]
    public async Task AMarkedItem_OpensAListedKindToo()
    {
        using var checkout = new Checkout();
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-define-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        store.Add(new Snippet { Label = "Klippy solution", Content = checkout.Solution, IsExecutable = true });

        var ran = new List<ExecutionPlan>();
        var vm = new MainViewModel(store, settings: new AppSettings { ExecuteOpenExtensions = [".slnx"] })
        {
            Executor = plan =>
            {
                ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, plan.Description));
            },
        };

        await vm.ActivateCommand.ExecuteAsync(vm.Filtered[0]);

        Assert.Equal(ExecutionKind.Document, Assert.Single(ran).Kind);
    }

    [Fact]
    public void TheEditorHint_ReadsTheSameList()
    {
        using var checkout = new Checkout();
        var previous = AppSettings.Current.ExecuteOpenExtensions;
        try
        {
            var editor = new EditorViewModel(null, (_, _) => { }, () => { })
            {
                Content = checkout.Solution,
                IsExecutable = true,
            };
            Assert.StartsWith("Shows ", editor.ExecuteHint);

            AppSettings.Current.ExecuteOpenExtensions = [".slnx"];
            editor.Content += " "; // the hint is read as the content changes
            Assert.StartsWith("Opened or run", editor.ExecuteHint);
        }
        finally
        {
            AppSettings.Current.ExecuteOpenExtensions = previous;
        }
    }

    // ---- the keyboard and the pointer ----

    [AvaloniaFact]
    public void DownThenEnter_RunsTheSecondValue()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow();

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();

        var bands = Bands(window);
        Assert.Equal(3, bands.Count);
        Assert.True(bands[0].Classes.Contains("selected"));
        Assert.False(bands[1].Classes.Contains("selected"));

        Press(window, Key.Down, PhysicalKey.ArrowDown);
        Assert.False(bands[0].Classes.Contains("selected"));
        Assert.True(bands[1].Classes.Contains("selected")); // one badge, and it moved

        Press(window, Key.Enter, PhysicalKey.Enter, "\n");

        var ran = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Folder, ran.Kind);
        Assert.Equal(checkout.Folder, ran.Target);
    }

    [AvaloniaFact]
    public void Enter_RunsTheFirst()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow();

        f.Vm.FilterText = "klippy";
        Press(window, Key.Enter, PhysicalKey.Enter, "\n");

        var ran = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Reveal, ran.Kind);
        Assert.Equal(checkout.Solution, ran.Target);
    }

    [AvaloniaFact]
    public void ClickingABand_RunsThatOne_WhicheverHoldsTheSelection()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow();

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();

        var button = Bands(window)[1].GetVisualDescendants().OfType<Button>().First();
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(checkout.Folder, Assert.Single(f.Ran).Target);
    }

    [AvaloniaFact]
    public void BesideAMatchingRow_EnterStillActivatesTheRow()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow(("Klippy notes", "Remember the release checklist"));

        CopyPayload? copied = null;
        f.Vm.ClipboardWriter = payload => { copied = payload; return Task.CompletedTask; };

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, Bands(window).Count);

        Press(window, Key.Enter, PhysicalKey.Enter, "\n");

        Assert.Equal("Remember the release checklist", copied?.Plain);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public void TheBandsAreDrawn()
    {
        // A frame for looking at: with solutions on the list to open, four bands over the
        // one row that uses the name — the console last, each with its number — and ↑ having
        // climbed from that row into the band nearest it, the list letting go of the
        // selection as it does.
        using var checkout = new Checkout();
        var (window, f) = NewWindow(("Open Klippy in Rider", "%r% %klippy:file%"));
        f.Settings.ExecuteOpenExtensions = [".slnx"];

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();
        f.Vm.MoveSelection(-1);
        Dispatcher.UIThread.RunJobs();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SnippetList");
        Assert.Null(list.SelectedItem);
        var bands = Bands(window);
        Assert.Equal(4, bands.Count);
        Assert.True(bands[3].Classes.Contains("selected"));
        for (int i = 0; i < bands.Count; i++)
            Assert.Contains(bands[i].GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == $"Ctrl+{i + 1}" && t.IsEffectivelyVisible);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-define-offers.png"));
    }

    // ---- a console in each place ----

    [Fact]
    public async Task TheConsole_GoesToTheLauncher_LikeAnyOffer()
    {
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "klippy";
        await f.Vm.RunOfferCommand.ExecuteAsync(f.Vm.Offers[^1]);

        var ran = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Console, ran.Kind);
        Assert.Equal(checkout.Folder, ran.Target);
    }

    [Fact]
    public void ALinkOnItsOwn_IsNoPlaceForAConsole()
    {
        using var checkout = new Checkout("wiki=https://wiki.example.com/team");
        var f = NewVm();

        f.Vm.FilterText = "wiki";

        Assert.Equal(ExecutionKind.Url, Assert.Single(f.Vm.Offers).Plan.Kind);
    }

    // ---- the numbers ----

    [Fact]
    public void WithMoreThanOneBand_EachCarriesItsNumber()
    {
        using var checkout = new Checkout();
        var f = NewVm();

        f.Vm.FilterText = "klippy";

        Assert.Equal(new[] { 1, 2, 3 }, f.Vm.Offers.Select(o => o.Number));
        Assert.Equal(new[] { "Ctrl+1", "Ctrl+2", "Ctrl+3" }, f.Vm.Offers.Select(o => o.NumberHint));
    }

    [Fact]
    public void ABandOnItsOwn_HasNoNumber_ForThereIsNothingToChooseBetween()
    {
        using var checkout = new Checkout("wiki=https://wiki.example.com/team");
        var f = NewVm();

        f.Vm.FilterText = "wiki";

        Assert.False(Assert.Single(f.Vm.Offers).HasNumber);
        Assert.False(f.Vm.RunNumberedOffer(1)); // Enter does it
        Assert.Empty(f.Ran);
    }

    [Fact]
    public void ByItsNumber_AMachineControlStillAsksFirst()
    {
        // What the line already meant comes first, and is numbered first — and the number is
        // the same gesture as a click, confirmation and all.
        using var checkout = new Checkout("restart=https://wiki.example.com/restarting");
        var f = NewVm();

        f.Vm.FilterText = "restart";
        Assert.Equal(new[] { ExecutionKind.System, ExecutionKind.Url }, f.Vm.Offers.Select(o => o.Plan.Kind));

        Assert.True(f.Vm.RunNumberedOffer(1));
        Assert.Same(f.Vm.Offers[0], f.Vm.PendingOffer);
        Assert.Empty(f.Ran);

        f.Vm.CancelOfferCommand.Execute(null);
        Assert.True(f.Vm.RunNumberedOffer(2));
        Assert.Equal("https://wiki.example.com/restarting", Assert.Single(f.Ran).Target);
    }

    [AvaloniaFact]
    public void CtrlAndADigit_RunsThatBand_WhereverTheSelectionIs()
    {
        // The row keeps Enter; Ctrl+3 reaches past it, and past the two bands above the
        // console, without moving anything first.
        using var checkout = new Checkout();
        var (window, f) = NewWindow(("Klippy notes", "Remember the release checklist"));
        CopyPayload? copied = null;
        f.Vm.ClipboardWriter = payload => { copied = payload; return Task.CompletedTask; };

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(f.Vm.SelectedSnippet);

        PressWithCtrl(window, Key.D3, PhysicalKey.Digit3);

        var ran = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Console, ran.Kind);
        Assert.Equal(checkout.Folder, ran.Target);
        Assert.Null(copied);
    }

    [AvaloniaFact]
    public void TheNumberPad_CountsToo()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow();

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();
        PressWithCtrl(window, Key.NumPad2, PhysicalKey.NumPad2);

        Assert.Equal(ExecutionKind.Folder, Assert.Single(f.Ran).Kind);
    }

    [AvaloniaFact]
    public void ADigitPastTheLastBand_DoesNothing()
    {
        using var checkout = new Checkout();
        var (window, f) = NewWindow();

        f.Vm.FilterText = "klippy";
        Dispatcher.UIThread.RunJobs();
        PressWithCtrl(window, Key.D4, PhysicalKey.Digit4);

        Assert.Empty(f.Ran);
        Assert.Equal("klippy", f.Vm.FilterText);
    }
}
