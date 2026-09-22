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
/// Closing Klippy from the window it is showing you. It is a resident launcher, so the
/// only other way out was the tray menu — a mouse trip away from a window summoned by a
/// hotkey. Typing "quit" offers it, and a confirmation is what actually does it.
///
/// The offer stands where a runnable one stands and is deliberately not one: it reaches no
/// executor, and it is outside the setting that governs running typed text.
/// </summary>
public class QuitTests
{
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

    /// <summary>Unseeded, so the only snippets in play are the ones a test asks for.</summary>
    private static SnippetStore NewStore(params (string Label, string Content)[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-quit-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var (label, content) in snippets)
            store.Add(new Snippet { Label = label, Content = content });
        return store;
    }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required AppSettings Settings { get; init; }
        public int Quits { get; set; }

        /// <summary>Plans that reached the execution engine. Quitting must never add one.</summary>
        public List<ExecutionPlan> Ran { get; } = new();

        /// <summary>
        /// Puts the test's execution engine in place. Called again after a window opens,
        /// since MainWindow wires the real one on the way up — and that one would go
        /// looking for a browser.
        /// </summary>
        public Fixture Attach()
        {
            Vm.Executor = plan =>
            {
                Ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, "ran"));
            };
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
        vm.QuitRequested += () => fixture.Quits++;
        return fixture.Attach();
    }

    /// <summary>A window whose quit is recorded rather than performed — ending the test app would take the suite with it.</summary>
    private static (MainWindow Window, Fixture F) NewWindow(params (string Label, string Content)[] snippets)
    {
        var fixture = NewVm(snippets);
        var window = new MainWindow { DataContext = fixture.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        fixture.Attach(); // MainWindow wired the real launcher on the way up
        return (window, fixture);
    }

    private static void Press(MainWindow window, Key key) =>
        Press(window, key, key == Key.Enter ? "\n" : null);

    private static void Press(MainWindow window, Key key, string? text)
    {
        var physical = key switch
        {
            Key.Enter => PhysicalKey.Enter,
            Key.Escape => PhysicalKey.Escape,
            Key.Down => PhysicalKey.ArrowDown,
            _ => PhysicalKey.None,
        };
        window.KeyPress(key, RawInputModifiers.None, physical, text);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("QUIT")]
    [InlineData("  quit  ")]  // a typed word, so whitespace around it is not a different word
    public void TheWordIsRecognisedTrimmedAndWhateverItsCase(string typed)
    {
        var f = NewVm();
        f.Vm.FilterText = typed;
        Assert.True(f.Vm.Offer?.IsQuit);
    }

    [Theory]
    [InlineData("quits")]
    [InlineData("qui")]       // still mid-word: nothing offered until it is finished
    [InlineData("quit now")]
    [InlineData("")]
    public void AnythingElseIsAnOrdinarySearch(string typed)
    {
        var f = NewVm();
        f.Vm.FilterText = typed;
        Assert.Null(f.Vm.Offer);
    }

    [AvaloniaFact]
    public void TypingQuit_ThenEnter_AsksBeforeClosing()
    {
        var (window, f) = NewWindow();

        f.Vm.FilterText = "quit";
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Vm.Offer?.IsQuit);

        Press(window, Key.Enter);
        Assert.True(f.Vm.PendingOffer?.IsQuit);
        Assert.Equal(0, f.Quits); // the word alone never closes anything

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-quit.png"));

        Press(window, Key.Enter);
        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal(1, f.Quits);
    }

    [AvaloniaFact]
    public void QuittingNeverReachesTheExecutionEngine()
    {
        // It is the one offer that runs nothing: Klippy closing is not Klippy starting
        // something, so no plan may reach ProcessLauncher's seat.
        var (window, f) = NewWindow();

        f.Vm.FilterText = "quit";
        Press(window, Key.Enter);
        Press(window, Key.Enter);

        Assert.Equal(1, f.Quits);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public void TheConfirmationIsNotTheOneTheSettingCanWaive()
    {
        // "Confirm OS actions" governs the machine's controls. Quitting is not one, so
        // turning it off must not make the word close the app on a single Enter.
        var (window, f) = NewWindow();
        f.Settings.ExecuteConfirmSystemActions = false;

        f.Vm.FilterText = "quit";
        Press(window, Key.Enter);

        Assert.True(f.Vm.PendingOffer?.IsQuit);
        Assert.Equal(0, f.Quits);
    }

    [AvaloniaFact]
    public void QuitIsOfferedEvenWithRunningTypedTextSwitchedOff()
    {
        // Being unwilling to hand typed text to the machine is no reason to be unable to
        // close the app, so the offer stands outside that setting.
        var f = NewVm();
        f.Settings.ExecuteUnmatched = false;

        f.Vm.FilterText = "quit";
        Assert.True(f.Vm.Offer?.IsQuit);

        // ...while the setting still does its job for the offers it governs
        f.Vm.FilterText = "https://www.qwe.com";
        Assert.Null(f.Vm.Offer);
    }

    [Theory]
    [InlineData("Quit the trial", "Settings → Account → End trial")]        // a label match
    [InlineData("Trial", "Press Quit to end the trial")]                    // a content-only match
    public void AMatchingSnippetStillLeavesTheWordAWayOut(string label, string content)
    {
        // Unlike "lock", where a matching snippet takes the offer away, "quit" is the only
        // keyboard route out of the app — so a snippet that merely mentions it, in its
        // label or its content, must not silently remove it. The row still keeps the
        // selection and answers to Enter; it is only the offer's standing that no longer
        // depends on the list being empty.
        var f = NewVm((label, content));

        f.Vm.FilterText = "quit";

        Assert.True(f.Vm.Offer?.IsQuit);
        Assert.Equal(label, f.Vm.SelectedSnippet?.Label);
        Assert.False(f.Vm.IsOfferSelected);
    }

    [AvaloniaFact]
    public void EnterOnAMatchingSnippetStillCopiesItRatherThanQuitting()
    {
        // The row still has the keystroke while it holds the selection, offer or no offer.
        var (window, f) = NewWindow(("Quit the trial", "Settings → Account → End trial"));

        f.Vm.FilterText = "quit";
        Dispatcher.UIThread.RunJobs();
        Assert.True(f.Vm.Offer?.IsQuit);
        Assert.False(f.Vm.IsOfferSelected);

        Press(window, Key.Enter);

        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal(0, f.Quits);
    }

    [AvaloniaFact]
    public void UpArrowFromAMatchingSnippetReachesTheQuitOfferAndEnterThenAsks()
    {
        // ↑ off the top row is how the offer is reached at all once a row holds the
        // selection — the same climb that reaches any other offer standing beside a list.
        var (window, f) = NewWindow(("Quit the trial", "Settings → Account → End trial"));

        f.Vm.FilterText = "quit";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Quit the trial", f.Vm.SelectedSnippet?.Label);

        f.Vm.Navigate(-1);
        Assert.True(f.Vm.IsOfferSelected);

        Press(window, Key.Enter);
        Assert.True(f.Vm.PendingOffer?.IsQuit);
        Assert.Equal(0, f.Quits);

        Press(window, Key.Enter);
        Assert.Equal(1, f.Quits);
    }

    [AvaloniaFact]
    public void EscapeBacksOutOfTheConfirmation_KeepingTheWord()
    {
        var (window, f) = NewWindow();

        f.Vm.FilterText = "quit";
        Press(window, Key.Enter);
        Assert.True(f.Vm.PendingOffer?.IsQuit);

        Press(window, Key.Escape);
        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal(0, f.Quits);
        Assert.Equal("quit", f.Vm.FilterText); // one Esc, one thing undone

        Press(window, Key.Escape);
        Assert.Equal("", f.Vm.FilterText);
    }

    [AvaloniaFact]
    public void ClickingBesideTheConfirmation_CancelsIt()
    {
        var (window, f) = NewWindow();
        f.Vm.RequestQuitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // well left of the 380-wide centred dialog, so this lands on the scrim
        var outside = new Point(8, 60);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(f.Vm.PendingOffer);
        Assert.Equal(0, f.Quits);
    }

    [AvaloniaFact]
    public void TheFooterLinkOffersTheSameConfirmation()
    {
        // The typed word is the keyboard route; the footer is the one for a mouse. One
        // question, however it was asked.
        var (window, f) = NewWindow();

        var link = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, f.Vm.RequestQuitCommand));
        Assert.Contains("footerLink", link.Classes);

        link.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(f.Vm.PendingOffer?.IsQuit);
        Assert.Equal(0, f.Quits);

        Press(window, Key.Enter);
        Assert.Equal(1, f.Quits);
    }

    [AvaloniaFact]
    public void TheDialogSaysWhatItCosts_AndNamesKlippyRatherThanTheMachine()
    {
        // The wording is shared with the machine controls, which is exactly why it has to
        // be checked: "Quit Klippy this computer?" is the failure mode.
        var offer = OfferViewModel.Quit();

        Assert.Equal("Quit Klippy", offer.Verb);
        Assert.Equal("Quit Klippy?", offer.Question);
        Assert.DoesNotContain("this computer", offer.Question);
        Assert.DoesNotContain("this computer", offer.Detail);
        Assert.Contains("hotkeys", offer.Consequence);
        Assert.DoesNotContain("sign in", offer.Consequence); // the lock screen's line, not this one
    }

    [AvaloniaFact]
    public void AWindowWithNoLauncherBehindIt_JustDoesNothing()
    {
        // Nothing wires QuitRequested when Klippy is not running as a resident launcher.
        // Half-closing a window the hotkey still expects to find would be worse.
        var settings = new AppSettings();
        var vm = new MainViewModel(NewStore(), settings: settings);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.RequestQuitCommand.Execute(null);
        vm.ConfirmOfferCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.PendingOffer);
        Assert.True(window.IsVisible);
    }
}
