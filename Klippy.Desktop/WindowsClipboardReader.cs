using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Klippy.Models;

namespace Klippy.Desktop;

/// <summary>What was on the clipboard when it last changed.</summary>
internal sealed record ClipboardSnapshot
{
    public ClipKind Kind { get; init; } = ClipKind.Text;
    public string Text { get; init; } = "";
    public string? Html { get; init; }

    /// <summary>Paths for a file clip.</summary>
    public string[] Files { get; init; } = Array.Empty<string>();

    /// <summary>Encoded image bytes for an image clip, in <see cref="ImageFormat"/>.</summary>
    public byte[]? Image { get; init; }

    /// <summary>"PNG" or "BMP".</summary>
    public string ImageFormat { get; init; } = "";

    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public IReadOnlyCollection<string> Formats { get; init; } = Array.Empty<string>();
    public bool? CanIncludeInClipboardHistory { get; init; }

    /// <summary>Process that owns the clipboard data, or null if it could not be identified.</summary>
    public string? SourceApp { get; init; }

    /// <summary>True when this process put the data there.</summary>
    public bool IsSelfWrite { get; init; }
}

/// <summary>
/// Reads the clipboard through Win32 rather than Avalonia.
///
/// Avalonia's clipboard cannot answer the question capture actually turns on: which
/// registered formats are present, and what value <c>CanIncludeInClipboardHistory</c>
/// carries. Those are exactly the markers a password manager sets, so reading natively
/// is what lets <see cref="Klippy.Services.CapturePolicy"/> do its job.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboardReader
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const int ERROR_ACCESS_DENIED = 5;

    // The clipboard is a single system-wide resource and the app that just wrote to it may
    // still hold it open. Failing to open is routine, not exceptional, so retry briefly
    // before giving up on the clip.
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Reads the clipboard, or null if it could not be opened or holds no text.</summary>
    public static ClipboardSnapshot? TryRead(IntPtr hwnd)
    {
        // Read before opening: GetClipboardOwner does not need the clipboard open, and the
        // owner is a better answer than the foreground window, which may already have moved on.
        var (sourceApp, isSelfWrite) = IdentifyOwner();

        if (!TryOpen(hwnd)) return null;

        try
        {
            var formats = EnumerateFormats();

            // Order matters. A file copy from Explorer also carries the paths as text, and
            // a picture copied from a browser often carries its URL — so the richer kind
            // is recognised first and text is the fallback rather than the default.
            var snapshot = ReadFiles(formats) ?? ReadImage(formats) ?? ReadText(formats);
            if (snapshot is null) return null;

            return snapshot with
            {
                Formats = formats,
                CanIncludeInClipboardHistory = ReadHistoryOptOut(formats),
                SourceApp = sourceApp,
                IsSelfWrite = isSelfWrite,
            };
        }
        catch (Exception)
        {
            // A clip that races us into being freed is not worth propagating.
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpen(IntPtr hwnd)
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (OpenClipboard(hwnd)) return true;
            if (Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED) return false;
            Thread.Sleep(OpenRetryDelay);
        }
        return false;
    }

    private static (string? App, bool IsSelf) IdentifyOwner()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return (null, false);

            GetWindowThreadProcessId(owner, out uint pid);
            if (pid == 0) return (null, false);

            // Klippy's own copies come back through the very notification that copying
            // raised them. Comparing the owning process is more reliable than a flag set
            // around the write, which would have to guess how long to stay set.
            if (pid == (uint)Environment.ProcessId) return (null, true);

            using var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, false);
        }
        catch (Exception)
        {
            // The owner can exit between the handle and the lookup.
            return (null, false);
        }
    }

    private static List<string> EnumerateFormats()
    {
        var names = new List<string>();
        var buffer = new StringBuilder(256);

        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            // Registered (custom) formats are the ones that carry names worth having;
            // the standard ones are identified by number and are not what policy tests.
            int length = GetClipboardFormatName(format, buffer, buffer.Capacity);
            names.Add(length > 0 ? buffer.ToString(0, length) : StandardFormatName(format));
        }
        return names;
    }

    private static ClipboardSnapshot? ReadText(List<string> formats)
    {
        var text = ReadUnicodeText();
        if (text.Length == 0) return null;

        return new ClipboardSnapshot { Kind = ClipKind.Text, Text = text, Html = ReadHtml(formats) };
    }

    /// <summary>
    /// A file selection. CF_HDROP is a DROPFILES header followed by a double-null
    /// terminated path list, which DragQueryFile walks for us.
    /// </summary>
    private static ClipboardSnapshot? ReadFiles(List<string> formats)
    {
        if (!formats.Contains(StandardFormatName(CF_HDROP))) return null;

        var handle = GetClipboardData(CF_HDROP);
        if (handle == IntPtr.Zero) return null;

        uint count = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
        if (count == 0) return null;

        var paths = new List<string>((int)count);
        var buffer = new StringBuilder(1024);
        for (uint i = 0; i < count; i++)
        {
            int length = (int)DragQueryFile(handle, i, buffer, (uint)buffer.Capacity);
            if (length > 0) paths.Add(buffer.ToString(0, length));
        }
        if (paths.Count == 0) return null;

        return new ClipboardSnapshot
        {
            Kind = ClipKind.Files,
            Files = paths.ToArray(),
            // Also kept as text, so pasting a file clip into an editor gives the paths —
            // which is what dropping files on a text target does anyway.
            Text = string.Join(Environment.NewLine, paths),
        };
    }

    /// <summary>
    /// A picture. PNG is preferred where the source app offers it (browsers and the
    /// Snipping Tool do) because it is already a file format; otherwise CF_DIB is wrapped
    /// in a BMP file header, which costs fourteen bytes and no image codec at all.
    /// </summary>
    private static ClipboardSnapshot? ReadImage(List<string> formats)
    {
        if (formats.Contains("PNG", StringComparer.OrdinalIgnoreCase) &&
            ReadBytes(RegisterClipboardFormat("PNG")) is { Length: > 24 } png)
        {
            var (pngWidth, pngHeight) = PngSize(png);
            return new ClipboardSnapshot
            {
                Kind = ClipKind.Image,
                Image = png,
                ImageFormat = "PNG",
                PixelWidth = pngWidth,
                PixelHeight = pngHeight,
            };
        }

        if (!formats.Contains(StandardFormatName(CF_DIB))) return null;
        if (ReadBytes(CF_DIB) is not { Length: >= 40 } dib) return null;

        return new ClipboardSnapshot
        {
            Kind = ClipKind.Image,
            Image = WrapDibAsBmp(dib),
            ImageFormat = "BMP",
            PixelWidth = BitConverter.ToInt32(dib, 4),
            PixelHeight = Math.Abs(BitConverter.ToInt32(dib, 8)), // negative means top-down
        };
    }

    /// <summary>
    /// Prefixes a BITMAPFILEHEADER to a DIB, turning the clipboard's headerless bitmap
    /// into a .bmp file that anything can read back.
    /// </summary>
    public static byte[] WrapDibAsBmp(byte[] dib)
    {
        const int FileHeaderSize = 14;
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToInt16(dib, 14);
        int paletteEntries = BitConverter.ToInt32(dib, 32);

        // A palette sits between the header and the pixels. Left at zero it means "all of
        // them" at 8bpp and below, and none at all above that.
        if (paletteEntries == 0 && bitCount <= 8) paletteEntries = 1 << bitCount;

        int offset = FileHeaderSize + headerSize + paletteEntries * 4;

        var bmp = new byte[FileHeaderSize + dib.Length];
        bmp[0] = 0x42; // B
        bmp[1] = 0x4D; // M
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, FileHeaderSize);
        return bmp;
    }

    /// <summary>Width and height from a PNG's IHDR, which is always its first chunk.</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        // 8-byte signature, 4-byte length, "IHDR", then width and height, big-endian.
        if (png.Length < 24) return (0, 0);

        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    /// <summary>How <see cref="EnumerateFormats"/> names a numbered (non-registered) format.</summary>
    private static string StandardFormatName(uint format) => "#" + format;

    private static string ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return "";

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return "";

        try
        {
            return Marshal.PtrToStringUni(pointer) ?? "";
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadHtml(List<string> formats)
    {
        if (!formats.Contains("HTML Format", StringComparer.OrdinalIgnoreCase)) return null;

        uint format = RegisterClipboardFormat("HTML Format");
        var bytes = ReadBytes(format);
        return bytes is null ? null : ExtractCfHtmlFragment(bytes);
    }

    private static bool? ReadHistoryOptOut(List<string> formats)
    {
        if (!formats.Contains(Services.CapturePolicy.HistoryOptOutFormat, StringComparer.OrdinalIgnoreCase))
            return null;

        uint format = RegisterClipboardFormat(Services.CapturePolicy.HistoryOptOutFormat);
        var bytes = ReadBytes(format);

        // A DWORD: 0 opts out, anything else opts in. A truncated value is treated as
        // absent rather than as permission.
        return bytes is { Length: >= 4 } ? BitConverter.ToUInt32(bytes, 0) != 0 : null;
    }

    private static byte[]? ReadBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;

        try
        {
            int size = (int)GlobalSize(handle);
            if (size <= 0) return null;

            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// Pulls the fragment out of a CF_HTML payload — the counterpart to
    /// <see cref="Klippy.Services.RichTextClipboard.WrapCfHtml"/>.
    ///
    /// StartFragment/EndFragment are *byte* offsets into the UTF-8 payload, so slicing
    /// has to happen before decoding. Not every app writes honest offsets, hence the
    /// fallback to the comment markers and then to the whole payload.
    /// </summary>
    public static string? ExtractCfHtmlFragment(byte[] payload)
    {
        var text = Encoding.UTF8.GetString(payload);

        int start = ReadHeaderOffset(text, "StartFragment:");
        int end = ReadHeaderOffset(text, "EndFragment:");
        if (start >= 0 && end > start && end <= payload.Length)
            return Encoding.UTF8.GetString(payload, start, end - start).Trim();

        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        int markerStart = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int markerEnd = text.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (markerStart >= 0 && markerEnd > markerStart)
            return text[(markerStart + startMarker.Length)..markerEnd].Trim();

        return text.Length > 0 ? text : null;
    }

    private static int ReadHeaderOffset(string text, string key)
    {
        int at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;

        int from = at + key.Length;
        int to = from;
        while (to < text.Length && char.IsAsciiDigit(text[to])) to++;

        return to > from && int.TryParse(text[from..to], out int value) ? value : -1;
    }

    // ---- interop ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")] private static extern bool CloseClipboard();

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint max);
}
