using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Klippy.Services;

/// <summary>
/// Where image bytes live.
///
/// Kept out of <c>clipboard.history.json</c> because the store rewrites that file whole on every
/// flush: megabytes of base64 in it would make each copy cost the entire history. A blob
/// per image, deleted when its clip is, keeps the JSON small and the writes cheap.
/// </summary>
public interface IClipBlobStore
{
    void Save(string name, byte[] bytes);

    /// <summary>Null when the blob is missing — a hand-deleted file, or an interrupted write.</summary>
    byte[]? TryLoad(string name);

    void Delete(string name);
}

/// <summary>Blobs as files in their own directory beside the history JSON.</summary>
public sealed class FileClipBlobStore : IClipBlobStore
{
    private readonly string _directory;

    public FileClipBlobStore(string directory) => _directory = directory;

    public void Save(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);

        // Written through a temp file for the same reason the JSON is: a half-written
        // blob that a later run tries to put on the clipboard is worse than none.
        var path = Path.Combine(_directory, name);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public byte[]? TryLoad(string name)
    {
        try
        {
            var path = Path.Combine(_directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Delete(string name)
    {
        try
        {
            var path = Path.Combine(_directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A blob that outlives its clip is litter, not a failure worth surfacing.
        }
    }
}

/// <summary>Blobs held in memory, for session-only history.</summary>
public sealed class MemoryClipBlobStore : IClipBlobStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.OrdinalIgnoreCase);

    public void Save(string name, byte[] bytes) => _blobs[name] = bytes;

    public byte[]? TryLoad(string name) => _blobs.TryGetValue(name, out var bytes) ? bytes : null;

    public void Delete(string name) => _blobs.Remove(name);
}

public static class ClipBlobs
{
    /// <summary>Directory name for blobs, a sibling of the history file.</summary>
    public const string DirectoryName = "clips";

    /// <summary>Content hash, used to recognise the same picture copied twice.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    /// <summary>Blob file name for a clip: the id, so a stray blob is traceable to its entry.</summary>
    public static string FileName(Guid id, string format) =>
        $"{id:N}.{(format.Length > 0 ? format.ToLowerInvariant() : "bin")}";
}
