using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Klippy.Desktop;

/// <summary>
/// The two things the launcher needs from an <c>NSWindow</c> that Avalonia does not
/// expose. Hand-rolled objc_msgSend, for the same reasons as <see cref="MacClipboardReader"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindow
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    private const nuint MoveToActiveSpaceBehavior = 1 << 1; // NSWindowCollectionBehaviorMoveToActiveSpace

    /// <summary>
    /// The macOS answer to what hiding and re-showing does on Windows: a window left up on
    /// another Space comes to the Space you are on when it is activated, rather than
    /// taking you over to it. Set once: Avalonia only sets the behaviour when it creates
    /// the window, and its Show puts back whatever it found.
    /// </summary>
    public static void MoveToActiveSpace(Window window)
    {
        if (NSWindow(window) is not { } ns) return;
        var behavior = SendGetUInt(ns, Sel("collectionBehavior"));
        SendSetUInt(ns, Sel("setCollectionBehavior:"), behavior | MoveToActiveSpaceBehavior);
    }

    /// <summary>
    /// Brings the window to the front even when macOS has declined to make Klippy the
    /// active app. Since Sonoma an app may ask to be activated and be refused, and a
    /// window ordered front by an inactive app stays behind the one you were using — so
    /// a summons that succeeded as far as Klippy could tell would show nothing at all.
    /// </summary>
    public static void OrderFrontRegardless(Window window)
    {
        if (NSWindow(window) is { } ns)
            Send(ns, Sel("orderFrontRegardless"));
    }

    private static IntPtr? NSWindow(Window window) =>
        window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow", Handle: var h } && h != IntPtr.Zero
            ? h
            : null;

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend is variadic in C, so each calling shape needs its own declaration
    // with the real signature. NSWindowCollectionBehavior is NSUInteger.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nuint SendGetUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void SendSetUInt(IntPtr receiver, IntPtr selector, nuint value);
}
