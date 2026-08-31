using System;
using System.Runtime.Versioning;

namespace Klippy.Desktop;

/// <summary>A running clipboard capture source. Disposing stops it.</summary>
internal interface IClipboardMonitor : IDisposable
{
    bool IsRunning { get; }

    /// <summary>A window in this process, for code that must own the clipboard to write it.</summary>
    IntPtr Handle { get; }
}

internal static class ClipboardMonitor
{
    /// <summary>
    /// Starts watching the clipboard, calling <paramref name="onCopy"/> on a background
    /// thread for each change. Returns null where the platform cannot capture — which is
    /// every platform but Windows today, and permanently so on Android and iOS, where
    /// background clipboard reads are forbidden by the OS.
    /// </summary>
    public static IClipboardMonitor? TryStart(Action<ClipboardSnapshot> onCopy)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsClipboardMonitor.Create(onCopy);
        }
        catch (Exception)
        {
            // Missing entry points or a sandbox: degrade to no capture rather than fail to start.
        }
        return null;
    }
}

/// <summary>
/// Clipboard capture via <c>AddClipboardFormatListener</c>, which posts
/// WM_CLIPBOARDUPDATE to a window — so this shares <see cref="MessageOnlyWindow"/> with
/// the global hotkey.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboardMonitor : IClipboardMonitor
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;

    private readonly MessageOnlyWindow _window;

    public bool IsRunning => _window.IsRunning;

    public IntPtr Handle => _window.Handle;

    private WindowsClipboardMonitor(Action<ClipboardSnapshot> onCopy)
    {
        IntPtr listener = IntPtr.Zero;

        _window = new MessageOnlyWindow(
            "Clipboard",
            attach: hwnd =>
            {
                listener = hwnd;
                return AddClipboardFormatListener(hwnd);
            },
            onMessage: (msg, _, _) =>
            {
                if (msg != WM_CLIPBOARDUPDATE) return;

                // Read on the pump thread. The clipboard is a shared resource with a
                // short useful life — marshalling to the UI thread first would mean
                // reading whatever replaced this clip in the meantime.
                if (WindowsClipboardReader.TryRead(listener) is { } snapshot)
                    onCopy(snapshot);
            },
            detach: hwnd => RemoveClipboardFormatListener(hwnd));
    }

    public static WindowsClipboardMonitor? Create(Action<ClipboardSnapshot> onCopy)
    {
        var monitor = new WindowsClipboardMonitor(onCopy);
        if (monitor.IsRunning) return monitor;

        monitor.Dispose();
        return null;
    }

    public void Dispose() => _window.Dispose();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
}
