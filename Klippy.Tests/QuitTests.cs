using System;
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
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Closing Klippy from the window it is showing you. It is a resident launcher, so the
/// only other way out is the tray menu — a mouse trip away from a window summoned by a
/// hotkey. Typing "quit" offers it; a confirmation is what actually does it.
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

    private static MainViewModel NewVm() =>
        new(new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-quit-{Guid.NewGuid():N}.json")));

    /// <summary>A window whose quit is recorded rather than performed — ending the test app would take the suite with it.</summary>
    private static (MainWindow Window, MainViewModel Vm, Func<int> Quits) NewWindow()
    {
        var vm = NewVm();
        int quits = 0;
        var window = new MainWindow { DataContext = vm, QuitRequested = () => quits++ };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, () => quits);
    }

    private static void PressEnter(MainWindow window)
    {
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\n");
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressEscape(MainWindow window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Theory]
    [InlineData("quit", true)]
    [InlineData("QUIT", true)]
    [InlineData("  quit  ", true)]  // a typed word, so whitespace around it is not a different word
    [InlineData("quits", false)]
    [InlineData("qui", false)]      // still mid-word: nothing offered until it is finished
    [InlineData("quit now", false)]
    [InlineData("", false)]
    public void TheQuitWordIsRecognisedExactly(string typed, bool offered)
    {
        var vm = NewVm();
        vm.FilterText = typed;
        Assert.Equal(offered, vm.IsQuitOffered);
    }

    [AvaloniaFact]
    public void TypingQuit_ShowsThePromptStrip()
    {
        var (window, vm, _) = NewWindow();
        var prompt = window.GetControl<Border>("QuitPrompt");
        Assert.False(prompt.IsVisible); // nothing typed yet

        vm.FilterText = "quit";
        Dispatcher.UIThread.RunJobs();
        Assert.True(prompt.IsVisible);

        vm.FilterText = "quitting time";
        Dispatcher.UIThread.RunJobs();
        Assert.False(prompt.IsVisible);
    }

    [AvaloniaFact]
    public void TypingQuit_ThenEnter_AsksBeforeClosing()
    {
        var (window, vm, quits) = NewWindow();

        vm.FilterText = "quit";
        PressEnter(window);

        Assert.True(vm.IsQuitPending);
        Assert.Equal(0, quits()); // the word alone never closes anything

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-quit.png"));

        PressEnter(window);
        Assert.False(vm.IsQuitPending);
        Assert.Equal(1, quits());
    }

    [AvaloniaFact]
    public void TypingQuit_ThenEnter_DoesNotCopyWhateverMatched()
    {
        var (window, vm, _) = NewWindow();

        string? copied = null;
        vm.ClipboardWriter = payload => { copied = payload.Plain; return Task.CompletedTask; };

        // A snippet the word does match, so Enter has something to copy if it is still the
        // copy key — the point being that for this one word it is not.
        vm.NewCommand.Execute(null);
        vm.Editor!.Label = "Quit the app";
        vm.Editor.Content = "not this";
        vm.Editor.SaveCommand.Execute(null);

        vm.FilterText = "quit";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Filtered, r => r.Label == "Quit the app");

        PressEnter(window);

        Assert.Null(copied);
        Assert.False(vm.IsToastVisible);
        Assert.True(vm.IsQuitPending);
    }

    [AvaloniaFact]
    public void EscapeBacksOutOfTheConfirmation_KeepingTheWord()
    {
        var (window, vm, quits) = NewWindow();

        vm.FilterText = "quit";
        PressEnter(window);
        Assert.True(vm.IsQuitPending);

        PressEscape(window);
        Assert.False(vm.IsQuitPending);
        Assert.Equal(0, quits());
        Assert.Equal("quit", vm.FilterText); // one Esc, one thing undone

        PressEscape(window);
        Assert.Equal("", vm.FilterText);
    }

    [AvaloniaFact]
    public void ClickingBesideTheConfirmation_CancelsIt()
    {
        var (window, vm, quits) = NewWindow();
        vm.RequestQuitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // well left of the 380-wide centred dialog, so this lands on the scrim
        var outside = new Point(8, 60);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsQuitPending);
        Assert.Equal(0, quits());
    }

    [AvaloniaFact]
    public void ShortcutsDoNotFireBehindTheConfirmation()
    {
        var (window, vm, _) = NewWindow();
        vm.RequestQuitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.P, cmdMod, PhysicalKey.P, "p");
        window.KeyPress(Key.N, cmdMod, PhysicalKey.N, "n");
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsPreviewOpen);
        Assert.Null(vm.Editor);
        Assert.True(vm.IsQuitPending);
    }

    [AvaloniaFact]
    public void TheFooterLinkOffersTheSameConfirmation()
    {
        // The typed word is the keyboard route; the footer is the one for a mouse.
        var (window, vm, quits) = NewWindow();

        var link = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, vm.RequestQuitCommand) && b.Classes.Contains("footerLink"));
        link.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsQuitPending);
        Assert.Equal(0, quits());

        vm.ConfirmQuitCommand.Execute(null);
        Assert.Equal(1, quits());
    }

    [AvaloniaFact]
    public void AWindowWithNoLauncherBehindIt_JustDoesNothing()
    {
        // Nothing wires QuitRequested when Klippy is not running as a resident launcher.
        // Half-closing a window the hotkey still expects to find would be worse.
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.RequestQuitCommand.Execute(null);
        vm.ConfirmQuitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsQuitPending);
        Assert.True(window.IsVisible);
    }
}
