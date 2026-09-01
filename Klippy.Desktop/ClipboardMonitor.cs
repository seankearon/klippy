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
    /// Starts watching the clipboard, calling <paramref name="onCopy"/> for each change.
    /// Returns null where the platform cannot capture — Linux today, and permanently so
    /// on Android and iOS, where background clipboard reads are forbidden by the OS.
    /// </summary>
    public static IClipboardMonitor? TryStart(Action<ClipboardSnapshot> onCopy)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsClipboardMonitor.Create(onCopy);
            if (OperatingSystem.IsMacOS()) return MacClipboardMonitor.Create(onCopy);
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

/// <summary>
/// Clipboard capture by polling <c>NSPasteboard.changeCount</c>.
///
/// macOS posts no clipboard-change notification — there is no equivalent of
/// <c>WM_CLIPBOARDUPDATE</c> — so polling the counter is the standard approach rather
/// than a shortcut. Only the counter is read on each tick, which is a single objc call;
/// the pasteboard's contents are only touched once it has actually moved.
///
/// This runs on the UI thread, unlike the Windows monitor's own message pump: AppKit
/// expects <c>NSWorkspace</c> and <c>NSPasteboard</c> to be used from the main thread,
/// and Avalonia's dispatcher is that thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacClipboardMonitor : IClipboardMonitor
{
    // Fast enough that a copy feels instantly recorded, slow enough to be invisible in
    // a CPU trace: the tick costs one objc_msgSend when nothing has changed.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly Avalonia.Threading.DispatcherTimer _timer;
    private long _lastChangeCount;

    /// <summary>Always zero: nothing on macOS needs a window handle to own the pasteboard.</summary>
    public IntPtr Handle => IntPtr.Zero;

    public bool IsRunning => _timer.IsEnabled;

    private MacClipboardMonitor(Action<ClipboardSnapshot> onCopy)
    {
        // Seed from whatever is already on the pasteboard, so starting Klippy does not
        // record a clip the user copied before it was running.
        _lastChangeCount = MacClipboardReader.ChangeCount();

        _timer = new Avalonia.Threading.DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) =>
        {
            var current = MacClipboardReader.ChangeCount();
            if (current == _lastChangeCount) return;

            // Move the mark before reading: a clip we cannot make sense of must not be
            // retried on every tick from here to shutdown.
            _lastChangeCount = current;

            if (MacClipboardReader.TryRead() is { } snapshot)
                onCopy(snapshot);
        };
    }

    public static MacClipboardMonitor? Create(Action<ClipboardSnapshot> onCopy)
    {
        var monitor = new MacClipboardMonitor(onCopy);
        monitor._timer.Start();

        if (monitor.IsRunning) return monitor;

        monitor.Dispose();
        return null;
    }

    public void Dispose() => _timer.Stop();
}
