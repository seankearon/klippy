using System;
using System.IO;
using Avalonia.Media.Imaging;
using Klippy.Models;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a captured clip.</summary>
public partial class ClipViewModel : RowViewModel
{
    /// <summary>Wide enough to stay crisp on the row, small enough that decoding is cheap.</summary>
    private const int ThumbnailWidth = 96;

    private readonly Func<ClipEntry, byte[]?>? _loadImage;
    private Bitmap? _thumbnail;
    private bool _thumbnailAttempted;

    public ClipEntry Model { get; }

    public ClipViewModel(ClipEntry model, Func<ClipEntry, byte[]?>? loadImage = null)
    {
        Model = model;
        _loadImage = loadImage;
    }

    public override string Label => Model.Label;
    public override string Content => Model.Text;

    public bool IsPinned => Model.IsPinned;
    public bool IsImage => Model.Kind == ClipKind.Image;
    public bool IsFiles => Model.Kind == ClipKind.Files;

    /// <summary>Where it was copied from, e.g. "chrome". Empty when the owner could not be identified.</summary>
    public string SourceApp => Model.SourceApp;

    /// <summary>Marks clips that carry formatting, matching the "md" marker on snippet rows.</summary>
    public bool HasHtml => Model.Kind == ClipKind.Text && Model.Html is { Length: > 0 };

    /// <summary>The kind marker on the row: "png", "bmp" or a file count.</summary>
    public string KindMarker => Model.Kind switch
    {
        ClipKind.Image => Model.BlobFormat.ToLowerInvariant(),
        ClipKind.Files => Model.Files.Length == 1 ? "file" : $"{Model.Files.Length} files",
        _ => "",
    };

    public bool HasKindMarker => KindMarker.Length > 0;

    /// <summary>
    /// A small preview of an image clip, decoded once and only when the row is first
    /// shown — a history of screenshots would otherwise decode every picture it holds
    /// the moment the list opens.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnailAttempted || !IsImage) return _thumbnail;
            _thumbnailAttempted = true;

            if (_loadImage?.Invoke(Model) is not { Length: > 0 } bytes) return null;

            try
            {
                using var stream = new MemoryStream(bytes);
                _thumbnail = Bitmap.DecodeToWidth(stream, ThumbnailWidth);
            }
            catch (Exception)
            {
                // An unreadable blob costs a thumbnail, not the row: the clip can still be
                // copied back, since that hands over the bytes untouched.
                _thumbnail = null;
            }
            return _thumbnail;
        }
    }

    /// <summary>
    /// Coarse age, e.g. "now", "5m", "3h", "2d". Rebuilt whenever the list refreshes
    /// rather than ticking on a timer — a clipboard history is read in glances, and a
    /// row that rewrites itself while being looked at is worse than one a minute stale.
    /// </summary>
    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - Model.CapturedAt;

            if (elapsed < TimeSpan.FromMinutes(1)) return "now";
            if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h";
            return $"{(int)elapsed.TotalDays}d";
        }
    }
}
