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
public class LaunchTests
{
    /// <summary>Unseeded, so the only snippets in play are the ones a test asks for.</summary>
    private static SnippetStore NewStore(params (string Label, string Content)[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-launch-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var (label, content) in snippets)
            store.Add(new Snippet { Label = label, Content = content });
        return store;
    }

    /// <summary>Records what it was asked to run instead of running it.</summary>
    private sealed class FakeLauncher
    {
        public List<LaunchTarget> Ran { get; } = new();
        public Exception? Throws { get; set; }

        public void Run(LaunchTarget target)
        {
            Ran.Add(target);
            if (Throws is { } error) throw error;
        }
    }

    private static (MainViewModel Vm, FakeLauncher Launcher, AppSettings Settings) NewVm(
        params (string Label, string Content)[] snippets)
    {
        var launcher = new FakeLauncher();
        // Constructed rather than loaded: nothing here is saved, so there is no path by
        // which a test could write over the developer's own settings.json.
        var settings = new AppSettings();
        return (new MainViewModel(NewStore(snippets), settings: settings, launcher: launcher.Run),
            launcher, settings);
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
        // answers to it, however tempting the OS control looks.
        var (vm, _, _) = NewVm(("Lock the server room door", "Ask reception for the code"));

        vm.FilterText = "lock";

        Assert.Single(vm.Filtered);
        Assert.Null(vm.Launch);
    }

    [Fact]
    public void TheOfferAppearsOnlyOnceTheLastMatchHasGone()
    {
        var (vm, _, _) = NewVm(("Restart the build agent", "Ask ops, or use the runbook"));

        vm.FilterText = "restart";
        Assert.Single(vm.Filtered);
        Assert.Null(vm.Launch);

        // Delete it and type nothing new: the same word, now answered by nothing, becomes
        // the OS control. Which is the rule stated from the other side.
        vm.RequestDeleteCommand.Execute((SnippetViewModel)vm.Filtered[0]);
        vm.ConfirmDeleteCommand.Execute(null);

        Assert.Empty(vm.Filtered);
        Assert.NotNull(vm.Launch);
        Assert.Equal(SystemAction.Restart, vm.Launch!.Target.Action);
    }

    [Fact]
    public void AnEmptySearchNeverOffersAnything()
    {
        var (vm, _, _) = NewVm();

        Assert.Empty(vm.Filtered); // no snippets at all, so nothing matched
        Assert.Null(vm.Launch);
    }

    [Fact]
    public void AnUnmatchedSearchThatNamesNothingRunnableOffersNothing()
    {
        var (vm, _, _) = NewVm(("Send log files", "Please send us the log files"));

        vm.FilterText = "qwertyuiop";

        Assert.Empty(vm.Filtered);
        Assert.Null(vm.Launch);
    }

    // ---- switched off ----

    [Fact]
    public void TheOfferCanBeTurnedOff()
    {
        var (vm, _, settings) = NewVm();
        settings.LaunchEnabled = false;

        vm.FilterText = "https://example.com/docs";

        Assert.Null(vm.Launch);
    }

    [Fact]
    public void WithoutAHeadThatCanRunAnything_ThereIsNoOffer()
    {
        // Android and iOS: no shell to hand a path to, so the feature is absent rather than
        // present and failing.
        var vm = new MainViewModel(NewStore(), settings: new AppSettings(), launcher: null);

        vm.FilterText = "https://example.com/docs";

        Assert.Null(vm.Launch);
    }

    // ---- running ----

    [Fact]
    public void RunningAUrlHandsItToTheHead_AndDismissesTheWindow()
    {
        var (vm, launcher, _) = NewVm();
        bool closed = false;
        vm.CloseRequested += () => closed = true;

        vm.FilterText = "www.qwe.com";
        vm.RunLaunchCommand.Execute(null);

        var ran = Assert.Single(launcher.Ran);
        Assert.Equal(LaunchKind.Url, ran.Kind);
        Assert.Equal("https://www.qwe.com", ran.Target);
        Assert.True(closed); // whatever was opened is what the user is looking at now
    }

    [Fact]
    public void AFailedLaunchIsReported_AndTheWindowStaysUp()
    {
        var (vm, launcher, _) = NewVm();
        launcher.Throws = new InvalidOperationException("No application is associated with this file.");
        bool closed = false;
        vm.CloseRequested += () => closed = true;

        vm.FilterText = "https://example.com/docs";
        vm.RunLaunchCommand.Execute(null);

        Assert.Equal("No application is associated with this file.", vm.Launch!.Error);
        Assert.False(closed); // it has to stay up, or the message is never read
    }

    [Fact]
    public void RetryingClearsTheLastFailure()
    {
        var (vm, launcher, _) = NewVm();
        launcher.Throws = new InvalidOperationException("nope");

        vm.FilterText = "https://example.com/docs";
        vm.RunLaunchCommand.Execute(null);
        Assert.NotNull(vm.Launch!.Error);

        launcher.Throws = null;
        vm.RunLaunchCommand.Execute(null);
        Assert.Null(vm.Launch!.Error);
    }

    // ---- confirmations ----

    [Fact]
    public void AnOsControlAsksFirst()
    {
        var (vm, launcher, _) = NewVm();

        vm.FilterText = "restart";
        vm.RunLaunchCommand.Execute(null);

        Assert.Empty(launcher.Ran);           // nothing has happened yet
        Assert.NotNull(vm.PendingLaunch);
        Assert.Equal("Restart this computer?", vm.PendingLaunch!.Question);

        vm.ConfirmLaunchCommand.Execute(null);

        Assert.Equal(SystemAction.Restart, Assert.Single(launcher.Ran).Action);
        Assert.Null(vm.PendingLaunch);
    }

    [Fact]
    public void CancellingAnOsControlRunsNothing()
    {
        var (vm, launcher, _) = NewVm();

        vm.FilterText = "hibernate";
        vm.RunLaunchCommand.Execute(null);
        vm.CancelLaunchCommand.Execute(null);

        Assert.Null(vm.PendingLaunch);
        Assert.Empty(launcher.Ran);
    }

    [Fact]
    public void EscapeClosesTheConfirmationBeforeItTouchesTheFilter()
    {
        var (vm, launcher, _) = NewVm();

        vm.FilterText = "sleep";
        vm.RunLaunchCommand.Execute(null);

        Assert.True(vm.HandleEscape());
        Assert.Null(vm.PendingLaunch);
        Assert.Equal("sleep", vm.FilterText); // the filter is the *next* thing Esc clears
        Assert.Empty(launcher.Ran);
    }

    [Fact]
    public void ConfirmationCanBeTurnedOff()
    {
        var (vm, launcher, settings) = NewVm();
        settings.LaunchConfirmSystemActions = false;

        vm.FilterText = "lock";
        vm.RunLaunchCommand.Execute(null);

        Assert.Null(vm.PendingLaunch);
        Assert.Equal(SystemAction.Lock, Assert.Single(launcher.Ran).Action);
    }

    [Fact]
    public void OnlyOsControlsAreEverConfirmed()
    {
        // Opening a page is undone by closing it; a restart is not undone at all.
        var (vm, launcher, _) = NewVm();

        vm.FilterText = "https://example.com/docs";
        vm.RunLaunchCommand.Execute(null);

        Assert.Null(vm.PendingLaunch);
        Assert.Single(launcher.Ran);
    }

    // ---- the keyboard ----

    [AvaloniaFact]
    public void Enter_RunsTheOffer_WhenThereIsNoRowToCopy()
    {
        var (vm, launcher, _) = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "www.qwe.com";
        Enter(window);

        Assert.Equal("https://www.qwe.com", Assert.Single(launcher.Ran).Target);
    }

    [AvaloniaFact]
    public void Enter_StillCopies_WhenSomethingMatched()
    {
        // The same keystroke with a snippet in the way: copy wins, and nothing is run.
        var (vm, launcher, _) = NewVm(("www.qwe.com", "the address of the thing"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // After Show: OnOpened installs the real clipboard writer over anything set earlier.
        CopyPayload? copied = null;
        vm.ClipboardWriter = payload => { copied = payload; return Task.CompletedTask; };

        vm.FilterText = "www.qwe.com";
        Enter(window);

        Assert.Equal("the address of the thing", copied?.Plain);
        Assert.Empty(launcher.Ran);
    }

    [AvaloniaFact]
    public void Enter_ConfirmsAnOsControl_SoTheGestureStaysOnTheKeyboard()
    {
        var (vm, launcher, _) = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "restart";
        Enter(window);
        Assert.NotNull(vm.PendingLaunch);
        Assert.Empty(launcher.Ran);

        Enter(window);

        Assert.Equal(SystemAction.Restart, Assert.Single(launcher.Ran).Action);
    }

    [AvaloniaFact]
    public void Escape_LeavesTheConfirmationWithoutRunningIt()
    {
        var (vm, launcher, _) = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "restart";
        Enter(window);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.PendingLaunch);
        Assert.Empty(launcher.Ran);
    }

    // ---- the band ----

    [AvaloniaFact]
    public void TheOfferIsShown_WhereTheFirstRowWouldHaveBeen()
    {
        var (vm, _, _) = NewVm(("Send log files", "Please send us the log files"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var band = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LaunchBand");
        Assert.False(band.IsVisible);

        vm.FilterText = @"C:\work\invoices";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Launch); // no such folder on this machine, and paths are verified

        vm.FilterText = "www.qwe.com";
        Dispatcher.UIThread.RunJobs();

        Assert.True(band.IsVisible);
        var shown = band.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Open link", shown);
        Assert.Contains("https://www.qwe.com", shown);

        // A snippet match takes the band away again.
        vm.FilterText = "log";
        Dispatcher.UIThread.RunJobs();
        Assert.False(band.IsVisible);
    }

    [AvaloniaFact]
    public void ClickingTheOfferRunsIt()
    {
        var (vm, launcher, _) = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "www.qwe.com";
        Dispatcher.UIThread.RunJobs();

        var band = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LaunchBand");
        var button = band.GetVisualDescendants().OfType<Button>().First();
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("https://www.qwe.com", Assert.Single(launcher.Ran).Target);
    }
}
