using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
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
        vm.ClipboardWriter = text => { copied = text; return Task.CompletedTask; };

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
    public void Duplicate_PrefillsEditor_AndSaveCreatesCopy()
    {
        var vm = NewVm();
        int before = vm.Filtered.Count;
        var row = vm.Filtered.First(r => r.Label == "Send log files");

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
}
