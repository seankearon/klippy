using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klippy.Services;

/// <summary>
/// Where the launcher window was, and how big, when it was last put away — so the next launch opens it there
/// rather than wherever the OS deals out new windows, at the size the XAML was written with.
///
/// Position is in physical pixels, as screens are; size is logical, as the window's Width and Height are. A
/// window moved to a monitor of different scaling between runs therefore comes back the same size to the
/// eye, and <see cref="PlacementPolicy.Restore"/> decides whether the place it was left is still on a screen.
///
/// Its own file, <c>window.json</c>, rather than a key in settings.json: it is written every time the window
/// is dismissed, and settings.json is a file people edit by hand — rewriting it on every Esc would be a
/// standing chance of overwriting an edit made while Klippy was running. It sits beside settings.json for the
/// reason the history does: it is about this machine's monitors, so it must not follow the snippets to a
/// synced folder and onto a laptop whose screens are somewhere else entirely.
/// </summary>
public sealed record WindowBounds
{
    public int X { get; init; }
    public int Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>
    /// Whether these could describe a window at all. A file edited by hand, or written by a window that had
    /// not been laid out yet, may say otherwise, and a NaN width handed to Avalonia is an exception at startup.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;

    public static string FilePath => StorageLocations.WindowPath;

    /// <summary>The bounds last saved, or null when there are none worth using. Never throws.</summary>
    public static WindowBounds? Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, WindowBoundsJsonContext.Default.WindowBounds) is { IsUsable: true } loaded
                ? loaded
                : null;
        }
        catch (Exception)
        {
            // Unreadable: the window opens where the OS puts it, as it did before there was a file.
            return null;
        }
    }

    /// <summary>
    /// Writes the bounds, swallowing failure: remembering where a window was is a nicety, and putting the
    /// window away must not fail because a disk would not take the note.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, this, WindowBoundsJsonContext.Default.WindowBounds);
            AtomicFile.Replace(tmp, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Next time, then.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowBounds))]
public sealed partial class WindowBoundsJsonContext : JsonSerializerContext;
