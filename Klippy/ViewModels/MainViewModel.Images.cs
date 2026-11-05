using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// What an image clip can do besides being copied back: be looked at full size inside
/// Klippy, and be written out as a file — either to be shown in its folder, or to have
/// its path put on the clipboard for something that wants a file rather than a paste.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The image clip shown full size over the window, or null. Drives the image overlay.</summary>
    [ObservableProperty]
    private ClipViewModel? _imagePreview;

    /// <summary>The decoded picture for <see cref="ImagePreview"/>; null when it could not be read.</summary>
    [ObservableProperty]
    private Bitmap? _imagePreviewBitmap;

    /// <summary>
    /// The selected clip's picture for the bottom preview pane, decoded only while the
    /// pane is open — arrowing through a history of screenshots with it shut must not
    /// decode each one on the way past.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _selectedImage;

    /// <summary>Whether the bottom preview pane is showing a picture rather than text.</summary>
    public bool IsSelectedImage => SelectedSnippet is ClipViewModel { IsImage: true };

    /// <summary>Opens the picture full size, inside Klippy.</summary>
    [RelayCommand]
    private void PreviewImage(ClipViewModel? row)
    {
        if (row is not { IsImage: true }) return;

        CloseCommands(restore: false);
        var previous = ImagePreviewBitmap;
        ImagePreview = row;
        ImagePreviewBitmap = Decode(_history?.TryLoadImage(row.Model));
        previous?.Dispose();

        if (ImagePreviewBitmap is null)
        {
            ImagePreview = null;
            ShowToast("That picture could not be read.", isError: true);
        }
    }

    [RelayCommand]
    private void PreviewSelectedImage() => PreviewImage(SelectedSnippet as ClipViewModel);

    [RelayCommand]
    private void CloseImagePreview()
    {
        var bitmap = ImagePreviewBitmap;
        ImagePreview = null;
        ImagePreviewBitmap = null;
        bitmap?.Dispose();
    }

    /// <summary>Copies the picture being previewed back to the clipboard, as the row's own copy would.</summary>
    [RelayCommand]
    private async Task CopyPreviewedImage()
    {
        if (ImagePreview is not { } row) return;
        CloseImagePreview();
        await Copy(row);
    }

    /// <summary>
    /// Saves the picture to the image folder and shows it there, selected, in Explorer or
    /// Finder. The window gets out of the way afterwards, as it does for any reveal.
    /// </summary>
    [RelayCommand]
    private async Task SaveImageAndShow(ClipViewModel? row)
    {
        if (TrySaveImage(row) is not { } path) return;
        CloseImagePreview();

        if (Executor is not { } run)
        {
            ShowToast($"Saved to {path}");
            return;
        }

        var plan = ExecutionPolicy.PlanReveal(path);
        if (plan.Kind == ExecutionKind.None)
        {
            ShowToast($"Saved to {path}, but it cannot be shown: {plan.Problem}", isError: true);
            return;
        }

        var result = await run(plan);
        ShowToast(result.Started ? $"Saved to {path}" : result.Message, isError: !result.Started);
        if (!result.Started) return;

        Copied?.Invoke();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Saves the picture to the image folder and puts the file's path on the clipboard, for
    /// an upload box, a terminal or a chat that wants a file rather than pasted pixels.
    /// Dismisses the window the way copying a clip does, since a paste is what comes next.
    /// </summary>
    [RelayCommand]
    private async Task SaveImageAndCopyPath(ClipViewModel? row)
    {
        if (TrySaveImage(row) is not { } path) return;
        CloseImagePreview();

        if (ClipboardWriter is { } write)
            await write(new CopyPayload(path, null));

        ShowToast($"Copied {path}");
        Copied?.Invoke();
        if (_prefs.CloseAfterClipboardCopy) CloseRequested?.Invoke();
    }

    [RelayCommand]
    private Task SaveSelectedImageAndShow() => SaveImageAndShow(SelectedSnippet as ClipViewModel);

    [RelayCommand]
    private Task SaveSelectedImageAndCopyPath() => SaveImageAndCopyPath(SelectedSnippet as ClipViewModel);

    /// <summary>
    /// Writes an image clip into the image folder and returns its path, or null — having
    /// said why in the toast — when there is no picture or the folder will not take it.
    ///
    /// A PNG is written as captured. A BMP is re-encoded as PNG first: a bitmap is what
    /// Windows hands over for a screenshot, and a file several times the size of the same
    /// picture as PNG is nobody's idea of a saved image. If it will not decode, the BMP
    /// itself is saved rather than nothing.
    /// </summary>
    private string? TrySaveImage(ClipViewModel? row)
    {
        if (row is not { IsImage: true }) return null;
        CloseCommands(restore: false);

        if (_history?.TryLoadImage(row.Model) is not { Length: > 0 } bytes)
        {
            ShowToast("That picture is no longer in the history.", isError: true);
            return null;
        }

        var extension = row.Model.BlobFormat.Length > 0 ? row.Model.BlobFormat : "png";
        if (!string.Equals(extension, "png", StringComparison.OrdinalIgnoreCase) && ToPng(bytes) is { } png)
        {
            bytes = png;
            extension = "png";
        }

        var folder = ImageExport.ResolveFolder(_prefs.ImageSaveFolder);
        try
        {
            return ImageExport.Save(bytes, extension, row.Model.CapturedAt, folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                    or NotSupportedException)
        {
            ShowToast($"Could not save to {folder}: {e.Message}", isError: true);
            return null;
        }
    }

    private static byte[]? ToPng(byte[] bytes)
    {
        try
        {
            using var bitmap = Decode(bytes);
            if (bitmap is null) return null;
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // an unreadable blob costs the preview, not the app
        }
    }

    partial void OnIsPreviewOpenChanged(bool value) => UpdateSelectedImage();

    /// <summary>Keeps the pane's picture in step with the selection, while the pane is open to show it.</summary>
    private void UpdateSelectedImage()
    {
        OnPropertyChanged(nameof(IsSelectedImage));

        var previous = SelectedImage;
        SelectedImage = IsPreviewOpen && SelectedSnippet is ClipViewModel { IsImage: true } clip
            ? Decode(_history?.TryLoadImage(clip.Model))
            : null;
        previous?.Dispose();
    }
}
