using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public class UiTests
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
        new(new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-ui-{Guid.NewGuid():N}.json")));

    /// <summary>The tag chips the open editor is showing, in the order they are laid out.</summary>
    private static List<Button> TagChips(Window window, MainViewModel vm) =>
        window.GetVisualDescendants().OfType<EditorOverlay>().Single()
            .GetVisualDescendants().OfType<Button>()
            .Where(b => ReferenceEquals(b.Command, vm.Editor!.SelectTagCommand))
            .ToList();

    private static void ClickCentre(Window window, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DesktopWindow_Renders_AndCapturesScreenshot()
    {
        var window = new MainWindow { DataContext = NewVm() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-desktop.png"));
    }

    [AvaloniaFact]
    public void MobileView_Renders_AndCapturesScreenshot()
    {
        var window = new Window
        {
            Width = 390,
            Height = 780,
            SystemDecorations = SystemDecorations.None,
            Content = new MainView { DataContext = NewVm() },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-mobile.png"));
    }

    [AvaloniaFact]
    public void TypingQuickCode_AndEnter_CopiesSnippet()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        string? copied = null;
        vm.ClipboardWriter = payload => { copied = payload.Plain; return Task.CompletedTask; };

        vm.FilterText = "slf";
        Assert.NotNull(vm.SelectedSnippet);
        Assert.Equal("Send log files", vm.SelectedSnippet!.Label);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\n");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(copied);
        Assert.StartsWith("Please send us the log files", copied);
        Assert.True(vm.IsToastVisible);
    }

    [AvaloniaFact]
    public void ArrowKeys_MoveSelection_AndEscapeClearsFilter()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var first = vm.SelectedSnippet;
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.Equal(vm.Filtered[1], vm.SelectedSnippet);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal(first, vm.SelectedSnippet);

        vm.FilterText = "docker";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal("", vm.FilterText);
    }

    [AvaloniaFact]
    public void PreviewPane_StartsClosed_AndTogglesWithShortcut()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var pane = window.GetControl<Border>("PreviewPane");
        Assert.False(vm.IsPreviewOpen);
        Assert.False(pane.IsVisible);

        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        window.KeyPress(Key.P, cmdMod, PhysicalKey.P, "p");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsPreviewOpen);
        Assert.True(pane.IsVisible);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-preview.png"));

        window.KeyPress(Key.P, cmdMod, PhysicalKey.P, "p");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsPreviewOpen);
        Assert.False(pane.IsVisible);
    }

    [AvaloniaFact]
    public void PreviewSplitter_DragResizesPaneAgainstList()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.IsPreviewOpen = true;
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetControl<Grid>("ListPreviewGrid");
        var splitter = window.GetControl<GridSplitter>("PreviewSplitter");
        double listBefore = grid.RowDefinitions[0].ActualHeight;
        double paneBefore = grid.RowDefinitions[2].ActualHeight;
        Assert.True(paneBefore > 0);

        // drag the splitter upwards: the preview grows, the list gives up the space
        var grip = splitter.TranslatePoint(
            new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, MouseButton.Left);
        window.MouseMove(grip + new Vector(0, -60));
        window.MouseUp(grip + new Vector(0, -60), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        double listAfter = grid.RowDefinitions[0].ActualHeight;
        double paneAfter = grid.RowDefinitions[2].ActualHeight;
        Assert.True(paneAfter > paneBefore, $"preview should grow: {paneBefore} -> {paneAfter}");
        Assert.True(listAfter < listBefore, $"list should shrink: {listBefore} -> {listAfter}");
    }

    [AvaloniaFact]
    public void PreviewPane_RemembersDraggedHeight_AcrossCloseAndReopen()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.IsPreviewOpen = true;
        Dispatcher.UIThread.RunJobs();

        var grid = window.GetControl<Grid>("ListPreviewGrid");
        var splitter = window.GetControl<GridSplitter>("PreviewSplitter");

        var grip = splitter.TranslatePoint(
            new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, MouseButton.Left);
        window.MouseMove(grip + new Vector(0, -50));
        window.MouseUp(grip + new Vector(0, -50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        double dragged = grid.RowDefinitions[2].ActualHeight;

        vm.IsPreviewOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, grid.RowDefinitions[2].ActualHeight); // fully collapsed

        vm.IsPreviewOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(dragged, grid.RowDefinitions[2].ActualHeight, precision: 0);
    }

    [AvaloniaFact]
    public void PreviewPane_ShortcutIsIgnored_WhileAnOverlayIsOpen()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.P, cmdMod, PhysicalKey.P, "p");
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsPreviewOpen); // Ctrl+P must not fire behind the editor
    }

    [AvaloniaFact]
    public void TransferOverlay_Renders_AndCapturesScreenshot()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenTransferCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-transfer.png"));
    }

    [AvaloniaFact]
    public void SettingsOverlay_Renders_AndCapturesScreenshot()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-settings.png"));
    }

    [AvaloniaFact]
    public void SettingsOverlay_ClosesOnAClickBesideIt()
    {
        // Unlike the editor there is no unsaved work: every toggle has already been written.
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // well left of the 460-wide centred panel, so this lands on the scrim
        var outside = new Point(8, 60);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Settings);
    }

    [AvaloniaFact]
    public void MobileView_HasASettingsButton_AndCapturesScreenshot()
    {
        // Android shows no window chrome, so the header button is the only way in.
        var vm = NewVm();
        var window = new Window
        {
            Width = 390,
            Height = 780,
            SystemDecorations = SystemDecorations.None,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, vm.OpenSettingsCommand));

        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Settings);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-mobile-settings.png"));
    }

    [AvaloniaFact]
    public void EditorOverlay_SurvivesAClickOnTheScrim()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // well left of the 520-wide centred dialog, so this lands on the scrim
        var outside = new Point(8, 60);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Editor); // clicking away must not discard the edit

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Editor); // ...but Esc still is a way out
    }

    [AvaloniaFact]
    public void EditorDialog_GripDrag_ResizesAndTheSizeSticks()
    {
        var vm = NewVm();
        // Taller than the 560 the window opens at: the dialog now fills that height on
        // its own, and the grip can only ever drag out to what the window allows.
        var window = new MainWindow { DataContext = vm, Height = 720 };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var overlay = window.GetVisualDescendants().OfType<EditorOverlay>().Single();
        var dialog = overlay.FindControl<Border>("Dialog")!;
        var grip = overlay.FindControl<Thumb>("ResizeGrip")!;
        double widthBefore = dialog.Bounds.Width;
        double heightBefore = dialog.Bounds.Height;

        var from = grip.TranslatePoint(
            new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        var to = from + new Vector(30, 20);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // the dialog is centred, so both edges move: the corner tracks the pointer
        Assert.Equal(widthBefore + 60, dialog.Bounds.Width, precision: 0);
        Assert.Equal(heightBefore + 40, dialog.Bounds.Height, precision: 0);

        vm.Editor!.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(widthBefore + 60, dialog.Bounds.Width, precision: 0); // reopens as left
        Assert.Equal(heightBefore + 40, dialog.Bounds.Height, precision: 0);
    }

    [AvaloniaFact]
    public void EditorDialog_FitsAPhoneWidthHost()
    {
        var vm = NewVm();
        var window = new Window
        {
            Width = 390,
            Height = 780,
            SystemDecorations = SystemDecorations.None,
            Content = new MainView { DataContext = vm },
        };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var dialog = window.GetVisualDescendants().OfType<EditorOverlay>().Single()
            .FindControl<Border>("Dialog")!;

        // the 520 starting width is a starting width, not a promise: on a phone the
        // dialog is held to the window less its margins
        Assert.Equal(350, dialog.Bounds.Width, precision: 0);
    }

    [AvaloniaFact]
    public void EditorOverlay_Renders_AndCapturesScreenshot()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.EditCommand.Execute(vm.Filtered.First(r => r.Label == "Send log files"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Editor!.IsMarkdown); // seeded prose snippet is Markdown
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-editor.png"));
    }

    [AvaloniaFact]
    public void ImportThroughMainViewModel_RefreshesListAndTags()
    {
        var vm = NewVm();
        int before = vm.Filtered.Count;

        using var file = new System.IO.MemoryStream();
        var foreign = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-ui-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreign.Add(new Klippy.Models.Snippet { Label = "Imported one", Content = "zzz", Tag = "imported" });
        foreign.Export(file);
        file.Position = 0;

        vm.OpenTransferCommand.Execute(null);
        Assert.NotNull(vm.Transfer);
        vm.Transfer!.LoadImportPreview(file, "other.json");
        vm.Transfer.ImportCommand.Execute(null);

        Assert.Equal(before + 1, vm.Filtered.Count);
        Assert.Contains(vm.Tags, t => t.Name == "imported");

        vm.Transfer.CloseCommand.Execute(null);
        Assert.Null(vm.Transfer);
    }

    [AvaloniaFact]
    public void F2_OpensTheEditorOnTheSelectedSnippet()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "slf";
        Assert.Null(vm.Editor);

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Editor);
        Assert.False(vm.Editor!.IsNew);
        Assert.Equal("Send log files", vm.Editor.Label);
    }

    [AvaloniaFact]
    public void CtrlI_OpensTheEditorToo_ForKeyboardsWhereF2IsBrightness()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.FilterText = "slf";
        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        window.KeyPress(Key.I, cmdMod, PhysicalKey.I, "i");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Editor);
        Assert.Equal("Send log files", vm.Editor!.Label);
    }

    [AvaloniaFact]
    public void TheEditShortcutIsIgnored_WhileAnOverlayIsOpen()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.OpenSettingsCommand.Execute(null);

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);      // the settings overlay keeps the keyboard
        Assert.NotNull(vm.Settings);
    }

    [AvaloniaFact]
    public void TheRowShortcutsDoNothingOnAClip_RatherThanThrowing()
    {
        // A clip has neither an editor nor a duplicate. Handing one to a command that
        // takes snippets throws, which on a key press means taking the app with it.
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "some clip", SourceApp = "chrome" });

        var vm = new MainViewModel(
            new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-ui-{Guid.NewGuid():N}.json")),
            history);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.ShowHistory();
        Assert.IsType<ClipViewModel>(vm.SelectedSnippet);

        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        window.KeyPress(Key.D, cmdMod, PhysicalKey.D, "d");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Editor);
    }

    [AvaloniaFact]
    public void Duplicate_PrefillsEditor_AndSaveCreatesCopy()
    {
        var vm = NewVm();
        int before = vm.Filtered.Count;
        var row = Assert.IsType<SnippetViewModel>(vm.Filtered.First(r => r.Label == "Send log files"));

        vm.DuplicateCommand.Execute(row);

        Assert.NotNull(vm.Editor);
        Assert.True(vm.Editor!.IsNew);
        Assert.Equal("Duplicate snippet", vm.Editor.Title);
        Assert.Equal("Send log files (copy)", vm.Editor.Label);
        Assert.Equal(row.Content, vm.Editor.Content);
        Assert.Equal(row.Tag, vm.Editor.Tag);
        Assert.Equal("", vm.Editor.QuickCode); // quick-codes must stay unique — not copied

        vm.Editor.SaveCommand.Execute(null);
        Assert.Equal(before + 1, vm.Filtered.Count);

        // the original is untouched and still owns its quick-code
        vm.FilterText = "slf";
        Assert.Equal("Send log files", vm.SelectedSnippet?.Label);
    }

    [AvaloniaFact]
    public void EditorDuplicateButton_BranchesWithUnsavedEdits()
    {
        var vm = NewVm();
        vm.EditCommand.Execute(vm.Filtered[0]);
        Assert.True(vm.Editor!.HasDuplicate);

        vm.Editor.Label = "Edited label";
        vm.Editor.DuplicateCommand.Execute(null);

        Assert.True(vm.Editor.IsNew);
        Assert.Equal("Edited label (copy)", vm.Editor.Label);
        Assert.False(vm.Editor.HasDuplicate); // a duplicate-in-progress can't branch again

        vm.NewCommand.Execute(null);
        Assert.False(vm.Editor.HasDuplicate); // plain new editor has no duplicate either
    }

    [AvaloniaFact]
    public void EditorOverlay_OffersTheTagsInUse_AndAClickOnOneFillsTheField()
    {
        var vm = NewVm();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var chips = TagChips(window, vm);
        Assert.Equal(new[] { "banking", "dev", "personal", "web", "work" },
            chips.Select(c => (string?)c.Content));

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir, "screenshot-editor-tags.png"));

        var work = chips.Single(c => (string?)c.Content == "work");
        ClickCentre(window, work);

        Assert.Equal("work", vm.Editor!.Tag);
        Assert.Contains("selected", TagChips(window, vm).Single(c => (string?)c.Content == "work").Classes);
    }

    [AvaloniaFact]
    public void EditorOverlay_HidesTheTagRow_WhenNothingIsTaggedYet()
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-ui-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        var vm = new MainViewModel(store);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.NewCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.Editor!.HasTagSuggestions);
        Assert.Empty(TagChips(window, vm));
    }

    [AvaloniaFact]
    public void Editor_SaveNewSnippet_AppearsInList()
    {
        var vm = NewVm();
        int before = vm.Filtered.Count;

        vm.NewCommand.Execute(null);
        Assert.NotNull(vm.Editor);

        vm.Editor!.Label = "Test entry";
        vm.Editor.Content = "some text";
        vm.Editor.Tag = "Testing";
        vm.Editor.QuickCode = "TE";
        vm.Editor.SaveCommand.Execute(null);

        Assert.Null(vm.Editor);
        Assert.Equal(before + 1, vm.Filtered.Count);

        vm.FilterText = "te";
        Assert.Equal("Test entry", vm.SelectedSnippet?.Label); // quick-code (lowercased) wins

        // tag chip list picked up the new tag, lowercased
        Assert.Contains(vm.Tags, t => t.Name == "testing");
    }

    [AvaloniaFact]
    public void Search_SpansEveryTag_AndDropsTheTagFilter()
    {
        var vm = NewVm();
        var personal = vm.Tags.First(t => t.Name == "personal");
        vm.SelectTagCommand.Execute(personal);
        Assert.All(vm.Filtered, row => Assert.Equal("personal", Assert.IsType<SnippetViewModel>(row).Tag));

        // a word search reaches a snippet the active tag was hiding
        vm.FilterText = "docker";
        Assert.Equal("Docker prune", vm.SelectedSnippet?.Label);

        // ...and so does a quick-code
        vm.FilterText = "slf";
        Assert.Equal("Send log files", vm.SelectedSnippet?.Label);

        // the chips say so: the tag filter is dropped, not silently ignored
        Assert.True(vm.Tags.First(t => t.Name == MainViewModel.AllTag).IsSelected);
        Assert.False(personal.IsSelected);
    }

    [AvaloniaFact]
    public void SelectingATag_LeavesSearchMode()
    {
        var vm = NewVm();
        vm.FilterText = "slf";
        Assert.Single(vm.Filtered);

        vm.SelectTagCommand.Execute(vm.Tags.First(t => t.Name == "dev"));

        Assert.Equal("", vm.FilterText);
        Assert.All(vm.Filtered, row => Assert.Equal("dev", Assert.IsType<SnippetViewModel>(row).Tag));
    }
}
