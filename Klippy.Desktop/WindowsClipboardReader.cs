using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Klippy.Desktop;

/// <summary>What was on the clipboard when it last changed.</summary>
internal sealed record ClipboardSnapshot
{
    public string Text { get; init; } = "";
    public string? Html { get; init; }
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
            var text = ReadUnicodeText();
            if (text.Length == 0) return null;

            return new ClipboardSnapshot
            {
                Text = text,
                Html = ReadHtml(formats),
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
            names.Add(length > 0 ? buffer.ToString(0, length) : $"#{format}");
        }
        return names;
    }

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
}
