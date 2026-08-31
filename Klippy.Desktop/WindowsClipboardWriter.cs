using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Puts file and image clips back on the clipboard through Win32.
///
/// Text and HTML still go through Avalonia — that path already works and is what the
/// Markdown flavours depend on. But CF_HDROP and CF_DIB have no expression in Avalonia's
/// clipboard API at all, so copying a file selection or a picture back has to be done
/// here or not at all.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsClipboardWriter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int BmpFileHeaderSize = 14;

    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// A window in this process to own the clipboard with.
    ///
    /// It matters that this is not zero: capture identifies Klippy's own writes by the
    /// clipboard's owning process, and an ownerless write would come back looking like
    /// somebody else's copy and be recorded straight into the history.
    /// </summary>
    public static IntPtr OwnerWindow { get; set; }

    public static Task<bool> TryWriteAsync(CopyPayload payload) => Task.FromResult(TryWrite(payload));

    public static bool TryWrite(CopyPayload payload)
    {
        if (!payload.NeedsNativeWrite) return false;
        if (!TryOpen(OwnerWindow)) return false;

        try
        {
            if (!EmptyClipboard()) return false;

            bool wrote = false;

            // Text first, so a target that understands nothing else still gets something:
            // the paths for a file clip, and for an image whatever caption came with it.
            if (payload.Plain.Length > 0)
                wrote |= SetText(payload.Plain);

            if (payload.Files is { Length: > 0 } files)
                wrote |= SetFiles(files);

            if (payload.Image is { Length: > 0 } image)
                wrote |= SetImage(image, payload.ImageFormat);

            return wrote;
        }
        catch (Exception)
        {
            return false;
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

    private static bool SetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return SetBytes(CF_UNICODETEXT, bytes);
    }

    /// <summary>
    /// CF_HDROP: a DROPFILES header, then the paths as one double-null terminated block.
    /// This is what Explorer reads on paste, so pasting a file clip actually copies files.
    /// </summary>
    private static bool SetFiles(string[] files)
    {
        var paths = new StringBuilder();
        foreach (var file in files)
        {
            paths.Append(file);
            paths.Append('\0');
        }
        paths.Append('\0'); // the list itself is null-terminated too

        var pathBytes = Encoding.Unicode.GetBytes(paths.ToString());

        // DROPFILES: pFiles (offset to the list), pt (x, y), fNC, fWide.
        const int DropFilesSize = 20;
        var buffer = new byte[DropFilesSize + pathBytes.Length];
        BitConverter.GetBytes(DropFilesSize).CopyTo(buffer, 0);
        BitConverter.GetBytes(1).CopyTo(buffer, 16); // fWide: the paths are Unicode
        pathBytes.CopyTo(buffer, DropFilesSize);

        return SetBytes(CF_HDROP, buffer);
    }

    /// <summary>
    /// A picture. A BMP blob is a DIB with a 14-byte file header on the front, so
    /// stripping it gives CF_DIB, which every Windows app accepts. PNG goes on under its
    /// registered name — browsers, Office and chat clients take it, though a few
    /// paint-style apps that only speak DIB will not see it.
    /// </summary>
    private static bool SetImage(byte[] image, string format)
    {
        if (string.Equals(format, "BMP", StringComparison.OrdinalIgnoreCase))
            return image.Length > BmpFileHeaderSize && SetBytes(CF_DIB, image[BmpFileHeaderSize..]);

        return SetBytes(RegisterClipboardFormat("PNG"), image);
    }

    /// <summary>
    /// Hands a copy of the bytes to the clipboard, which takes ownership of the memory on
    /// success — so the handle is only freed when SetClipboardData refuses it.
    /// </summary>
    private static bool SetBytes(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) return false;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;

        GlobalFree(handle);
        return false;
    }

    // ---- interop ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")] private static extern bool CloseClipboard();

    [DllImport("user32.dll")] private static extern bool EmptyClipboard();

    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
}
