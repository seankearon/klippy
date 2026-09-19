using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>The main list showing captured clips instead of saved snippets.</summary>
public class HistoryModeTests
{
    private static SnippetStore NewSnippetStore() =>
        new(Path.Combine(Path.GetTempPath(), $"klippy-hist-ui-{Guid.NewGuid():N}.json"));

    private static (MainViewModel Vm, ClipHistoryStore History) NewVm(params string[] clips)
    {
        var history = ClipHistoryStore.InMemory();
        foreach (var text in clips)
            history.Add(new ClipEntry { Text = text, SourceApp = "chrome" });

        return (new MainViewModel(NewSnippetStore(), history), history);
    }

    private static TagChipViewModel HistoryChip(MainViewModel vm) =>
        vm.Tags.First(t => t.Name == MainViewModel.HistoryTag);

    /// <summary>A real PNG, so the row's thumbnail is exercised rather than stubbed.</summary>
    private static byte[] SamplePng()
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(160, 90));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.SlateGray, new Rect(0, 0, 160, 90));
            ctx.FillRectangle(Brushes.Goldenrod, new Rect(20, 20, 60, 50));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    [AvaloniaFact]
    public void HistoryView_Renders_AndCapturesScreenshot()
    {
        var history = ClipHistoryStore.InMemory();
        foreach (var (text, app) in new[]
                 {
                     ("https://github.com/seankearon/Klippy/pull/2", "chrome"),
                     ("docker system prune -af --volumes", "WindowsTerminal"),
                     ("Thanks — I have raised this with the team and will come back to you\nas soon as I hear anything.", "msedge"),
                     ("DE44 5001 0517 5407 3249 31", "explorer"),
                 })
            history.Add(new ClipEntry { Text = text, SourceApp = app });

        history.Add(new ClipEntry
        {
            Kind = ClipKind.Files,
            Files = new[] { @"C:\work\invoice-2026-08.pdf", @"C:\work\receipts.zip" },
            Text = "C:\\work\\invoice-2026-08.pdf\r\nC:\\work\\receipts.zip",
            SourceApp = "explorer",
        });
        history.AddImage(SamplePng(), "PNG", 1280, 720, "snippingtool");

        var vm = new MainViewModel(NewSnippetStore(), history);
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        vm.TogglePinCommand.Execute(vm.Filtered.Last());

        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
        Directory.CreateDirectory(dir);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(dir, "screenshot-history.png"));
    }

    // ---- the chip ----

    [AvaloniaFact]
    public void HistoryChipIsAbsentWhereThereIsNoHistory()
    {
        // Mobile: the OS forbids background clipboard reads, so the mode must not be offered.
        var vm = new MainViewModel(NewSnippetStore(), history: null);

        Assert.False(vm.HasHistory);
        Assert.DoesNotContain(vm.Tags, t => t.Name == MainViewModel.HistoryTag);
        Assert.DoesNotContain(vm.Tags, t => t.IsMode);
    }

    [AvaloniaFact]
    public void HistoryChipLeadsTheChipRowAndIsMarkedAsAMode()
    {
        var (vm, _) = NewVm("one");

        Assert.True(vm.HasHistory);
        Assert.Equal(MainViewModel.HistoryTag, vm.Tags[0].Name);
        Assert.True(vm.Tags[0].IsMode);
        Assert.False(vm.Tags[1].IsMode); // "All" and the tags are filters, not modes
    }

    // ---- switching modes ----

    [AvaloniaFact]
    public void SelectingHistoryShowsClipsNewestFirst()
    {
        var (vm, _) = NewVm("older", "newer");

        vm.SelectTagCommand.Execute(HistoryChip(vm));

        Assert.True(vm.IsHistoryMode);
        Assert.Equal(new[] { "newer", "older" }, vm.Filtered.Select(r => r.Label));
        Assert.All(vm.Filtered, row => Assert.IsType<ClipViewModel>(row));
    }

    [AvaloniaFact]
    public void LeavingHistoryPutsTheSnippetsBack()
    {
        var (vm, _) = NewVm("a clip");
        vm.SelectTagCommand.Execute(HistoryChip(vm));

        vm.SelectTagCommand.Execute(vm.Tags.First(t => t.Name == MainViewModel.AllTag));

        Assert.False(vm.IsHistoryMode);
        Assert.All(vm.Filtered, row => Assert.IsType<SnippetViewModel>(row));
        Assert.True(vm.Tags.First(t => t.Name == MainViewModel.AllTag).IsSelected);
        Assert.False(HistoryChip(vm).IsSelected);
    }

    [AvaloniaFact]
    public void OnlyOneChipIsLitAtATime()
    {
        var (vm, _) = NewVm("a clip");

        vm.SelectTagCommand.Execute(HistoryChip(vm));

        Assert.Single(vm.Tags.Where(t => t.IsSelected));
        Assert.True(HistoryChip(vm).IsSelected);
    }

    [AvaloniaFact]
    public void TheCountAndWatermarkSayWhichListThisIs()
    {
        var (vm, _) = NewVm("one", "two");

        vm.SelectTagCommand.Execute(HistoryChip(vm));
        Assert.Equal("2 clips", vm.SnippetCountText);
        Assert.Contains("history", vm.SearchWatermark, StringComparison.OrdinalIgnoreCase);

        vm.SelectTagCommand.Execute(vm.Tags.First(t => t.Name == MainViewModel.AllTag));
        Assert.Contains("snippet", vm.SnippetCountText);
        Assert.Contains("snippets", vm.SearchWatermark, StringComparison.OrdinalIgnoreCase);
    }

    // ---- summoning a view directly, as the global hotkeys do ----

    [AvaloniaFact]
    public void ShowHistorySwitchesTheListAndLightsTheChip()
    {
        var (vm, _) = NewVm("a clip");

        vm.ShowHistory();

        Assert.True(vm.IsHistoryMode);
        Assert.True(HistoryChip(vm).IsSelected);
        Assert.All(vm.Filtered, row => Assert.IsType<ClipViewModel>(row));
    }

    [AvaloniaFact]
    public void ShowSnippetsGoesBackToEveryTag()
    {
        var (vm, _) = NewVm("a clip");
        vm.ShowHistory();

        vm.ShowSnippets();

        Assert.False(vm.IsHistoryMode);
        Assert.True(vm.Tags.First(t => t.Name == MainViewModel.AllTag).IsSelected);
        Assert.All(vm.Filtered, row => Assert.IsType<SnippetViewModel>(row));
    }

    [AvaloniaFact]
    public void SwitchingViewsKeepsWhatWasTyped()
    {
        // You are looking for the same thing either way, so the other half of the answer
        // is one chip away rather than one chip and a retype.
        var (vm, _) = NewVm("a clip");
        vm.FilterText = "clip";

        vm.ShowHistory();
        Assert.Equal("clip", vm.FilterText);

        vm.ShowSnippets();
        Assert.Equal("clip", vm.FilterText);
    }

    [AvaloniaFact]
    public void ChoosingATagStillClearsTheFilter()
    {
        // A tag chip narrows the mode you are already in rather than changing it, and a
        // search drops the tag filter the moment you type — so the two cannot both stand.
        var (vm, _) = NewVm("a clip");
        vm.FilterText = "docker";

        vm.SelectTagCommand.Execute(vm.Tags.First(t => t.Name == MainViewModel.AllTag));

        Assert.Equal("", vm.FilterText);
    }

    [AvaloniaFact]
    public void ShowHistoryDoesNothingWhereThereIsNoHistory()
    {
        // The hotkey is not registered in this case, but the view model must not be
        // willing to enter a mode it cannot populate.
        var vm = new MainViewModel(NewSnippetStore(), history: null);

        vm.ShowHistory();

        Assert.False(vm.IsHistoryMode);
        Assert.All(vm.Filtered, row => Assert.IsType<SnippetViewModel>(row));
    }

    [AvaloniaFact]
    public void SummoningTheViewYouAreAlreadyOnIsIdempotent()
    {
        // The launcher turns a second press into a dismiss; the view model itself simply
        // stays put rather than toggling underneath it.
        var (vm, _) = NewVm("a clip");
        vm.ShowHistory();

        vm.ShowHistory();

        Assert.True(vm.IsHistoryMode);
        Assert.Single(vm.Tags.Where(t => t.IsSelected));
    }

    // ---- searching ----

    [AvaloniaFact]
    public void SearchFiltersClips()
    {
        var (vm, _) = NewVm("docker system prune", "an unrelated clip");
        vm.SelectTagCommand.Execute(HistoryChip(vm));

        vm.FilterText = "docker";

        Assert.Equal("docker system prune", Assert.Single(vm.Filtered).Label);
    }

    // ---- capture arriving while the window is open ----

    [AvaloniaFact]
    public void ANewlyCapturedClipAppearsWithoutAUserAction()
    {
        var (vm, history) = NewVm("first");
        vm.SelectTagCommand.Execute(HistoryChip(vm));

        history.Add(new ClipEntry { Text = "captured while open" });

        Assert.Equal("captured while open", vm.Filtered[0].Label);
    }

    [AvaloniaFact]
    public void CaptureDoesNotDisturbTheSnippetList()
    {
        var (vm, history) = NewVm("first");
        var before = vm.Filtered.Count;

        history.Add(new ClipEntry { Text = "captured while looking at snippets" });

        Assert.False(vm.IsHistoryMode);
        Assert.Equal(before, vm.Filtered.Count);
        Assert.All(vm.Filtered, row => Assert.IsType<SnippetViewModel>(row));
    }

    // ---- copying ----

    [AvaloniaFact]
    public void CopyingAClipWritesItsOwnFlavours()
    {
        var (vm, history) = NewVm();
        history.Add(new ClipEntry { Text = "**bold**", Html = "<b>bold</b>" });
        vm.SelectTagCommand.Execute(HistoryChip(vm));

        CopyPayload? written = null;
        vm.ClipboardWriter = payload => { written = payload; return System.Threading.Tasks.Task.CompletedTask; };

        vm.CopyCommand.Execute(vm.Filtered[0]);

        Assert.NotNull(written);
        Assert.Equal("**bold**", written!.Plain);
        Assert.Equal("<b>bold</b>", written.Html);
    }

    [AvaloniaFact]
    public void RecopyingAClipPromotesItBackToTheTop()
    {
        var (vm, _) = NewVm("older", "newer");
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        vm.ClipboardWriter = _ => System.Threading.Tasks.Task.CompletedTask;

        vm.CopyCommand.Execute(vm.Filtered[1]); // "older"
        Dispatcher.UIThread.RunJobs(); // the copy command is async

        Assert.Equal("older", vm.Filtered[0].Label);
        Assert.Equal(2, vm.Filtered.Count);
    }

    // ---- pinning, deleting, clearing ----

    [AvaloniaFact]
    public void PinningIsReflectedOnTheRow()
    {
        var (vm, _) = NewVm("keep me");
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        var row = (ClipViewModel)vm.Filtered[0];

        vm.TogglePinCommand.Execute(row);
        Assert.True(((ClipViewModel)vm.Filtered[0]).IsPinned);

        vm.TogglePinCommand.Execute(vm.Filtered[0]);
        Assert.False(((ClipViewModel)vm.Filtered[0]).IsPinned);
    }

    [AvaloniaFact]
    public void DeletingAClipTakesItStraightOutWithNoConfirmation()
    {
        var (vm, _) = NewVm("go away", "stay");
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        var doomed = vm.Filtered.First(r => r.Label == "go away");

        vm.DeleteClipCommand.Execute(doomed);

        Assert.Null(vm.DeleteTarget); // a clip is transient; no dialog stands in the way
        Assert.Equal("stay", Assert.Single(vm.Filtered).Label);
    }

    [AvaloniaFact]
    public void ClearingHistoryKeepsPinnedClips()
    {
        var (vm, _) = NewVm("transient", "pinned");
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        vm.TogglePinCommand.Execute(vm.Filtered.First(r => r.Label == "pinned"));

        vm.ClearHistoryCommand.Execute(null);

        Assert.Equal("pinned", Assert.Single(vm.Filtered).Label);
    }

    // ---- promotion, the bridge between the two halves ----

    [AvaloniaFact]
    public void PromotingKeepsTheClipAndPrefillsTheEditor()
    {
        var (vm, history) = NewVm();
        history.Add(new ClipEntry { Text = "Send us the log files\nand the config" });
        vm.SelectTagCommand.Execute(HistoryChip(vm));

        vm.PromoteToSnippetCommand.Execute(vm.Filtered[0]);

        Assert.NotNull(vm.Editor);
        Assert.Equal("Send us the log files", vm.Editor!.Label); // first line becomes the name
        Assert.Equal("Send us the log files\nand the config", vm.Editor.Content);
        Assert.False(vm.Editor.IsMarkdown); // captured text is not known to be Markdown
        Assert.Single(history.Entries);     // promotion copies, it does not consume
    }

    [AvaloniaFact]
    public void APromotedClipIsSavedAsAnOrdinarySnippet()
    {
        var (vm, _) = NewVm("a captured line");
        vm.SelectTagCommand.Execute(HistoryChip(vm));
        vm.PromoteToSnippetCommand.Execute(vm.Filtered[0]);

        vm.Editor!.Label = "Captured";
        vm.Editor.SaveCommand.Execute(null);

        vm.SelectTagCommand.Execute(vm.Tags.First(t => t.Name == MainViewModel.AllTag));
        Assert.Contains(vm.Filtered, row => row.Label == "Captured");
    }
}
