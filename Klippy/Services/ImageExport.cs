using System;
using System.IO;

namespace Klippy.Services;

/// <summary>
/// Writes an image clip out as an ordinary file, so it can be attached, uploaded or
/// dragged somewhere that takes a file rather than a paste.
///
/// The blob in the clips folder is not that file: it is named by the clip's id, lives
/// in app data, and goes the moment the clip is evicted. A saved copy is the user's,
/// in a folder they chose, and outlives the history.
/// </summary>
public static class ImageExport
{
    /// <summary>
    /// Where an empty <see cref="AppSettings.ImageSaveFolder"/> saves to: a Klippy folder
    /// in the user's Pictures, which is where a screenshot is looked for.
    /// </summary>
    public static string DefaultFolder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures))
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pictures, "Klippy");
        }
    }

    /// <summary>The folder a configured value names: empty is <see cref="DefaultFolder"/>, anything else is expanded.</summary>
    public static string ResolveFolder(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultFolder : StorageLocations.ExpandPath(configured);

    /// <summary>
    /// The name a clip is saved under, from when it was captured — "klippy-2026-11-05-143012.png" —
    /// so a folder of them sorts in the order they were copied.
    /// </summary>
    public static string FileNameFor(DateTimeOffset capturedAt, string extension, int attempt = 1)
    {
        var stamp = capturedAt.ToLocalTime().ToString("yyyy-MM-dd-HHmmss");
        var suffix = attempt > 1 ? $"-{attempt}" : "";
        return $"klippy-{stamp}{suffix}.{extension.TrimStart('.').ToLowerInvariant()}";
    }

    /// <summary>
    /// Saves <paramref name="bytes"/> into <paramref name="folder"/> and returns the full path.
    ///
    /// Saving the same clip twice gives back the file already there rather than a second
    /// copy of it: the two actions that save are both one click, and a folder that fills
    /// with duplicates each time someone wants the path again is litter. A different
    /// picture captured in the same second takes the next free name instead.
    /// </summary>
    /// <exception cref="IOException">The folder cannot be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    /// <exception cref="ArgumentException">The folder is not an absolute path.</exception>
    public static string Save(byte[] bytes, string extension, DateTimeOffset capturedAt, string folder)
    {
        if (!Path.IsPathRooted(folder))
            throw new ArgumentException($"\"{folder}\" is not an absolute path", nameof(folder));

        Directory.CreateDirectory(folder);

        for (int attempt = 1; ; attempt++)
        {
            var path = Path.Combine(folder, FileNameFor(capturedAt, extension, attempt));

            if (File.Exists(path))
            {
                if (SameBytes(path, bytes)) return path;
                continue;
            }

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            AtomicFile.Replace(tmp, path);
            return path;
        }
    }

    private static bool SameBytes(string path, byte[] bytes)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
