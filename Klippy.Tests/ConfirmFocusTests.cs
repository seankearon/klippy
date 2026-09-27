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
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Where the keyboard is while a confirmation is up. Both ask about something that cannot be
/// taken back, so each opens on Cancel: Enter straight away goes back, and going ahead is Tab
/// or an arrow away. The delete confirmation and the one for a machine control or quitting
/// share the arrangement, so each check here runs against both.
/// </summary>
public class ConfirmFocusTests
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

    public enum Dialog { Delete, Offer }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required MainWindow Window { get; init; }

        /// <summary>The confirmation under test, whose buttons are looked up by name.</summary>
        public required UserControl Overlay { get; init; }

        /// <summary>Plans that reached the execution engine: a restart must only get here once confirmed.</summary>
        public List<ExecutionPlan> Ran { get; } = new();

        /// <summary>The snippet the delete confirmation asks about.</summary>
        public SnippetViewModel? Doomed { get; set; }

        public Button Cancel => Button("CancelButton");
        public Button Confirm => Button("ConfirmButton");

        private Button Button(string name) => Overlay.FindControl<Button>(name)!;

        public IInputElement? Focused => Window.FocusManager!.GetFocusedElement();

        public TextBox SearchBox =>
            Window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "SearchBox");
    }

    private static Fixture Open(Dialog dialog)
    {
        var vm = new MainViewModel(
            new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-confirm-{Guid.NewGuid():N}.json")),
            settings: new AppSettings());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var f = new Fixture
        {
            Vm = vm,
            Window = window,
            Overlay = dialog == Dialog.Delete
                ? window.GetVisualDescendants().OfType<ConfirmOverlay>().Single()
                : window.GetVisualDescendants().OfType<OfferConfirmOverlay>().Single(),
        };
        // MainWindow wired the real launcher on the way up, and that one would restart the machine.
        vm.Executor = plan =>
        {
            f.Ran.Add(plan);
            return Task.FromResult(new ExecutionResult(true, "ran"));
        };

        if (dialog == Dialog.Delete)
        {
            f.Doomed = Assert.IsType<SnippetViewModel>(vm.Filtered.First(r => r.Label == "Send log files"));
            vm.RequestDeleteCommand.Execute(f.Doomed);
        }
        else
        {
            // Typed and asked for from the keyboard, the way a restart is actually reached.
            vm.FilterText = "restart";
            Press(f, Key.Enter);
            Assert.NotNull(vm.PendingOffer);
        }
        Dispatcher.UIThread.RunJobs();
        return f;
    }

    private static void Press(Fixture f, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var physical = key switch
        {
            Key.Enter => PhysicalKey.Enter,
            Key.Escape => PhysicalKey.Escape,
            Key.Tab => PhysicalKey.Tab,
            Key.Left => PhysicalKey.ArrowLeft,
            Key.Right => PhysicalKey.ArrowRight,
            Key.Up => PhysicalKey.ArrowUp,
            Key.Down => PhysicalKey.ArrowDown,
            _ => PhysicalKey.None,
        };
        f.Window.KeyPress(key, modifiers, physical, key == Key.Enter ? "\n" : null);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsOpen(Fixture f, Dialog dialog) =>
        dialog == Dialog.Delete ? f.Vm.DeleteTarget is not null : f.Vm.PendingOffer is not null;

    /// <summary>Whether the dialog's action happened: the snippet gone, or the restart run.</summary>
    private static bool WentAhead(Fixture f, Dialog dialog) =>
        dialog == Dialog.Delete ? !f.Vm.Filtered.Contains(f.Doomed!) : f.Ran.Count > 0;

    [AvaloniaTheory]
    [InlineData(Dialog.Delete)]
    [InlineData(Dialog.Offer)]
    public void TheConfirmationOpensOnCancel_AndSaysSo(Dialog dialog)
    {
        var f = Open(dialog);

        Assert.Same(f.Cancel, f.Focused);
        // Shown as keyboard focus, since nothing else on screen says which way Enter goes.
        Assert.Contains(":focus-visible", f.Cancel.Classes);

        var frame = f.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, $"screenshot-confirm-{dialog.ToString().ToLowerInvariant()}.png"));
    }

    [AvaloniaTheory]
    [InlineData(Dialog.Delete)]
    [InlineData(Dialog.Offer)]
    public void EnterStraightAway_GoesBack(Dialog dialog)
    {
        var f = Open(dialog);

        Press(f, Key.Enter);

        Assert.False(IsOpen(f, dialog));
        Assert.False(WentAhead(f, dialog));
    }

    [AvaloniaTheory]
    [InlineData(Dialog.Delete)]
    [InlineData(Dialog.Offer)]
    public void Tab_ReachesTheActionButton_AndEnterTakesIt(Dialog dialog)
    {
        var f = Open(dialog);

        Press(f, Key.Tab);
        Assert.Same(f.Confirm, f.Focused);
        Assert.Contains(":focus-visible", f.Confirm.Classes);

        Press(f, Key.Enter);

        Assert.False(IsOpen(f, dialog));
        Assert.True(WentAhead(f, dialog));
    }

    [AvaloniaTheory]
    [InlineData(Dialog.Delete, Key.Right, Key.Left)]
    [InlineData(Dialog.Delete, Key.Down, Key.Up)]
    [InlineData(Dialog.Offer, Key.Right, Key.Left)]
    [InlineData(Dialog.Offer, Key.Down, Key.Up)]
    public void TheArrowsMoveBetweenTheButtons(Dialog dialog, Key toConfirm, Key toCancel)
    {
        var f = Open(dialog);

        Press(f, toConfirm);
        Assert.Same(f.Confirm, f.Focused);

        Press(f, toConfirm); // already at the far end: stays put rather than wrapping
        Assert.Same(f.Confirm, f.Focused);

        Press(f, toCancel);
        Assert.Same(f.Cancel, f.Focused);

        Assert.True(IsOpen(f, dialog)); // moving about answers nothing
        Assert.False(WentAhead(f, dialog));
    }

    [AvaloniaTheory]
    [InlineData(Dialog.Delete)]
    [InlineData(Dialog.Offer)]
    public void TabGoesRoundTheTwoButtons_NeverOutToTheWindowBehind(Dialog dialog)
    {
        var f = Open(dialog);

        Press(f, Key.Tab);
        Press(f, Key.Tab);
        Assert.Same(f.Cancel, f.Focused);

        Press(f, Key.Tab, RawInputModifiers.Shift);
        Assert.Same(f.Confirm, f.Focused);
    }

    [AvaloniaTheory]
    [InlineData(Dialog.Delete)]
    [InlineData(Dialog.Offer)]
    public void OnceItCloses_TheKeyboardGoesBackWhereItWas(Dialog dialog)
    {
        // The window opens with the search box focused, and after a typed "restart" is
        // backed out of, the next keystroke belongs there rather than to a hidden button.
        var f = Open(dialog);
        Assert.NotSame(f.SearchBox, f.Focused);

        Press(f, Key.Escape);

        Assert.False(IsOpen(f, dialog));
        Assert.True(f.SearchBox.IsFocused);
    }

    [AvaloniaFact]
    public void TheMobileScreenOpensItsDeleteConfirmationOnCancelToo()
    {
        var vm = new MainViewModel(
            new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-confirm-{Guid.NewGuid():N}.json")));
        var window = new Window
        {
            Width = 390,
            Height = 780,
            SystemDecorations = SystemDecorations.None,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.RequestDeleteCommand.Execute(vm.Filtered.OfType<SnippetViewModel>().First());
        Dispatcher.UIThread.RunJobs();

        var cancel = window.GetVisualDescendants().OfType<ConfirmOverlay>().Single()
            .FindControl<Button>("CancelButton")!;
        Assert.Same(cancel, window.FocusManager!.GetFocusedElement());
    }
}
