using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace Klippy.Desktop;

/// <summary>
/// Where the mouse pointer is, in the same desktop coordinates as
/// <see cref="Avalonia.Controls.Window.Position"/> — or null where the platform will not say.
///
/// Avalonia has no API for this: every pointer coordinate it exposes arrives on a pointer
/// event, and a hidden window gets none. So each platform is asked directly.
/// </summary>
internal static class CursorPosition
{
    public static PixelPoint? TryGet()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows.TryGet();
            if (OperatingSystem.IsMacOS()) return Mac.TryGet();
            if (OperatingSystem.IsLinux()) return X11.TryGet();
        }
        catch (Exception)
        {
            // A missing library or entry point means "no pointer position", not a crash:
            // the caller falls back to centring.
        }

        return null;
    }

    // ---- Windows ----------------------------------------------------------

    /// <summary>
    /// GetCursorPos reports in whatever space the process's DPI awareness implies. Avalonia
    /// sets PerMonitorV2 during platform init (Win32PlatformOptions.DpiAwareness defaults to
    /// PerMonitorDpiAware) — app.manifest says nothing about DPI — so from any point after
    /// SetupWithLifetime this is physical pixels, which is exactly Avalonia's PixelPoint here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        public static PixelPoint? TryGet() =>
            GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Fails when the input desktop is not the current one — the lock screen, the UAC
        // desktop — so the bool is worth honouring rather than ignoring.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);
    }

    // ---- macOS ------------------------------------------------------------

    /// <summary>
    /// A "null event" from CGEventCreate carries the current pointer location, and unlike a
    /// CGEventTap it needs no Accessibility permission — the same reason MacHotkey prefers
    /// Carbon. Plain C functions, so none of objc_msgSend's per-shape ABI hazards apply.
    ///
    /// CGEventGetLocation is already in the space Avalonia uses: top-left origin at the main
    /// display, measured in AppKit points. Avalonia's own backend builds its PixelPoints the
    /// same way — NSScreen.frame with Scaling pinned to 1 and Y flipped — so there is nothing
    /// to convert, on Retina or otherwise. NSEvent.mouseLocation would be bottom-left origin
    /// and would need that flip undone.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static class Mac
    {
        private const string CoreGraphics =
            "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public static PixelPoint? TryGet()
        {
            var nullEvent = CGEventCreate(IntPtr.Zero);
            if (nullEvent == IntPtr.Zero) return null;

            try
            {
                var p = CGEventGetLocation(nullEvent);
                return new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
            }
            finally
            {
                CFRelease(nullEvent);
            }
        }

        // CGFloat is double on every 64-bit target, and osx-arm64/osx-x64 are the only mac
        // runtimes Klippy publishes. Two doubles come back in floating-point registers on
        // both, so a plain struct return is correct — no _stret shape needed.
        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint
        {
            public double X;
            public double Y;
        }

        [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreate(IntPtr source);

        [DllImport(CoreGraphics)] private static extern CGPoint CGEventGetLocation(IntPtr @event);

        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    }

    // ---- Linux / X11 ------------------------------------------------------

    /// <summary>
    /// X11 root-window coordinates are plain device pixels with no DPI virtualisation, which
    /// is the same space Avalonia's X11 screens report, so the numbers transfer directly.
    ///
    /// Under Wayland this runs through XWayland (Avalonia 11.3 has no Wayland backend) and
    /// the answer is only as fresh as the last motion XWayland saw — Wayland deliberately
    /// gives clients no way to ask where the pointer is globally. A wrong-but-plausible
    /// point is caught by the caller's ScreenFromPoint check.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static class X11
    {
        private const string LibX11 = "libX11.so.6";

        // Our own connection, opened once: Avalonia's is not ours to share, and a fresh
        // XOpenDisplay per summon would be a round trip for every keypress.
        private static IntPtr _display;
        private static bool _opened;

        public static PixelPoint? TryGet()
        {
            if (!_opened)
            {
                _opened = true;
                _display = XOpenDisplay(IntPtr.Zero); // NULL: use $DISPLAY
            }

            if (_display == IntPtr.Zero) return null;

            var root = XDefaultRootWindow(_display);
            if (root == IntPtr.Zero) return null;

            // False means the pointer is on a different X screen; root_x/root_y are then
            // not ours to trust.
            if (XQueryPointer(_display, root, out _, out _,
                              out var rootX, out var rootY, out _, out _, out _) == 0)
                return null;

            return new PixelPoint(rootX, rootY);
        }

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);

        // Xlib's Bool is a 4-byte int; Window is an XID (unsigned long), hence IntPtr.
        [DllImport(LibX11)]
        private static extern int XQueryPointer(IntPtr display, IntPtr window,
            out IntPtr root, out IntPtr child,
            out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    }
}
