using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// What an image clip offers besides being copied back: a full-size preview inside
/// Klippy, and saving it as a file to show in its folder or to copy the path of.
/// </summary>
public class ImageActionsTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"klippy-images-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a temp folder left behind is not a failed test */ }
    }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required ClipHistoryStore History { get; init; }
        public List<ExecutionPlan> Ran { get; } = new();
        public List<CopyPayload> Copied { get; } = new();
        public int Closes { get; set; }

        public ClipViewModel Image => Vm.Filtered.OfType<ClipViewModel>().First(c => c.IsImage);
    }

    private Fixture NewVm(byte[] image, string format = "PNG")
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-images-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        var history = ClipHistoryStore.InMemory();
        history.AddImage(image, format, 4, 3, "snippingtool");

        var settings = AppSettings.Load(Path.Combine(_folder, "settings.json"));
        settings.ImageSaveFolder = _folder;

        var f = new Fixture { Vm = new MainViewModel(store, history, settings), History = history };
        f.Vm.ClipboardWriter = payload => { f.Copied.Add(payload); return Task.CompletedTask; };
        f.Vm.Executor = plan =>
        {
            f.Ran.Add(plan);
            return Task.FromResult(new ExecutionResult(true, plan.Description));
        };
        f.Vm.CloseRequested += () => f.Closes++;
        f.Vm.ShowHistory();
        return f;
    }

    /// <summary>A real, decodable picture, so the preview and the BMP re-encode have something to work on.</summary>
    private static byte[] Png()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(4, 3), new Vector(96, 96), PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    // ---- saving ----

    [Fact]
    public void SavingTheSameBytesTwice_GivesBackTheSameFile()
    {
        var when = DateTimeOffset.Now;
        var first = ImageExport.Save([1, 2, 3], "png", when, _folder);
        var again = ImageExport.Save([1, 2, 3], "png", when, _folder);

        Assert.Equal(first, again);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ADifferentPictureFromTheSameSecond_TakesTheNextName()
    {
        var when = DateTimeOffset.Now;
        var first = ImageExport.Save([1, 2, 3], "png", when, _folder);
        var second = ImageExport.Save([4, 5, 6], "png", when, _folder);

        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.png", second);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(second));
    }

    [Fact]
    public void TheFileIsNamedForWhenItWasCaptured()
    {
        var when = new DateTimeOffset(2026, 11, 5, 14, 30, 12, TimeSpan.Zero);
        var expected = $"klippy-{when.ToLocalTime():yyyy-MM-dd-HHmmss}.png";

        Assert.Equal(expected, ImageExport.FileNameFor(when, "PNG"));
    }

    [Fact]
    public void AnEmptyFolderSetting_MeansPicturesKlippy()
    {
        Assert.Equal(ImageExport.DefaultFolder, ImageExport.ResolveFolder(""));
        Assert.Equal("Klippy", Path.GetFileName(ImageExport.DefaultFolder));
    }

    [AvaloniaFact]
    public async Task SaveAndShow_WritesTheFile_AndShowsItSelectedInItsFolder()
    {
        var png = Png();
        var f = NewVm(png);

        await f.Vm.SaveImageAndShowCommand.ExecuteAsync(f.Image);

        var saved = Assert.Single(Directory.GetFiles(_folder, "*.png"));
        Assert.Equal(png, File.ReadAllBytes(saved));
        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Reveal, plan.Kind);
        Assert.Equal(saved, plan.Target);
        Assert.Empty(f.Copied);  // the clipboard is left alone
        Assert.Equal(1, f.Closes);
    }

    [AvaloniaFact]
    public async Task SaveAndCopyPath_PutsThePathOnTheClipboard_AsText()
    {
        var f = NewVm(Png());

        await f.Vm.SaveImageAndCopyPathCommand.ExecuteAsync(f.Image);

        var saved = Assert.Single(Directory.GetFiles(_folder, "*.png"));
        var payload = Assert.Single(f.Copied);
        Assert.Equal(saved, payload.Plain);
        Assert.Null(payload.Image);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public async Task ABitmapClip_IsSavedAsPng()
    {
        // What Windows hands over for a screenshot, at several times the size of a PNG. The
        // bytes only have to decode: the re-encode does not care what they were.
        var f = NewVm(Png(), format: "BMP");

        await f.Vm.SaveImageAndCopyPathCommand.ExecuteAsync(f.Image);

        var saved = Assert.Single(Directory.GetFiles(_folder));
        Assert.EndsWith(".png", saved);
        var header = File.ReadAllBytes(saved)[..4];
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, header);
    }

    [AvaloniaFact]
    public async Task AFolderThatCannotBeUsed_IsReported_AndNothingIsCopied()
    {
        var f = NewVm(Png());
        var settings = AppSettings.Load(Path.Combine(_folder, "other.json"));
        settings.ImageSaveFolder = "relative/folder";
        var vm = new MainViewModel(new SnippetStore(
            Path.Combine(_folder, "s.json"), seedIfEmpty: false), f.History, settings);
        var copied = new List<CopyPayload>();
        vm.ClipboardWriter = p => { copied.Add(p); return Task.CompletedTask; };
        vm.ShowHistory();

        await vm.SaveImageAndCopyPathCommand.ExecuteAsync(vm.Filtered.OfType<ClipViewModel>().First());

        Assert.Empty(copied);
        Assert.True(vm.IsToastError);
    }

    // ---- previewing ----

    [AvaloniaFact]
    public void Preview_OpensThePictureInsideKlippy_AndEscClosesIt()
    {
        var f = NewVm(Png());

        f.Vm.PreviewImageCommand.Execute(f.Image);

        Assert.Same(f.Image, f.Vm.ImagePreview);
        Assert.NotNull(f.Vm.ImagePreviewBitmap);
        Assert.Equal(new PixelSize(4, 3), f.Vm.ImagePreviewBitmap!.PixelSize);
        Assert.Empty(f.Ran); // nothing outside Klippy was asked to show it

        Assert.True(f.Vm.HandleEscape());
        Assert.Null(f.Vm.ImagePreview);
        Assert.Null(f.Vm.ImagePreviewBitmap);
    }

    [AvaloniaFact]
    public void APictureThatWillNotDecode_SaysSo_RatherThanOpeningBlank()
    {
        var f = NewVm([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        f.Vm.PreviewImageCommand.Execute(f.Image);

        Assert.Null(f.Vm.ImagePreview);
        Assert.True(f.Vm.IsToastError);
    }

    [AvaloniaFact]
    public void ThePreviewPane_ShowsTheSelectedPicture_OnlyWhileOpen()
    {
        var f = NewVm(Png());
        f.Vm.SelectedSnippet = f.Image;

        Assert.True(f.Vm.IsSelectedImage);
        Assert.Null(f.Vm.SelectedImage); // pane shut: nothing decoded

        f.Vm.TogglePreviewCommand.Execute(null);
        Assert.NotNull(f.Vm.SelectedImage);

        f.Vm.TogglePreviewCommand.Execute(null);
        Assert.Null(f.Vm.SelectedImage);
    }
}
