using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Klippy.Models;

namespace Klippy.Desktop;

/// <summary>
/// Reads the clipboard through AppKit's <c>NSPasteboard</c>.
///
/// The Windows reader exists because Avalonia cannot report which formats are present;
/// the same is true here, and it matters for the same reason. macOS has no
/// <c>CanIncludeInClipboardHistory</c>, but it has a widely honoured convention —
/// password managers mark a clip with <c>org.nspasteboard.ConcealedType</c> — and
/// answering that question is what lets <see cref="Klippy.Services.CapturePolicy"/> do
/// its job on this platform too.
///
/// Interop is hand-rolled objc_msgSend rather than a binding library: the surface used
/// here is a dozen selectors, and NativeAOT is happier with plain DllImports.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacClipboardReader
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    // Uniform Type Identifiers, as NSPasteboard names them.
    private const string TypeString = "public.utf8-plain-text";
    private const string TypeHtml = "public.html";
    private const string TypePng = "public.png";
    private const string TypeTiff = "public.tiff";
    private const string TypeFileUrl = "public.file-url";

    /// <summary>
    /// The convention password managers use to say "do not record this". Not an Apple
    /// API — an agreement between apps (nspasteboard.org) — but Keychain, 1Password and
    /// Bitwarden all set it, which makes it the closest analogue to the Windows format.
    /// </summary>
    private const string TypeConcealed = "org.nspasteboard.ConcealedType";

    /// <summary>Reads the general pasteboard, or null if it holds nothing we capture.</summary>
    public static ClipboardSnapshot? TryRead()
    {
        var pasteboard = GeneralPasteboard();
        if (pasteboard == IntPtr.Zero) return null;

        var formats = TypesOf(pasteboard);
        if (formats.Count == 0) return null;

        var (sourceApp, isSelfWrite) = IdentifyFrontmostApp();

        // Concealed is the whole reason for reading formats: CapturePolicy drops the clip
        // on false, and null means "no opinion", so only ever say false, never true.
        bool? canRecord = formats.Contains(TypeConcealed) ? false : null;

        var common = new ClipboardSnapshot
        {
            Formats = formats,
            CanIncludeInClipboardHistory = canRecord,
            SourceApp = sourceApp,
            IsSelfWrite = isSelfWrite,
        };

        // Files first: a file copy also carries a text flavour holding the path, and
        // recording that as text would lose the fact that it was a file at all.
        if (formats.Contains(TypeFileUrl) && ReadFiles(pasteboard) is { Length: > 0 } files)
            return common with { Kind = ClipKind.Files, Files = files, Text = string.Join('\n', files) };

        // PNG only, deliberately. Plenty of apps offer TIFF alone, but the history's
        // thumbnails go through Skia, which does not decode TIFF — storing those would
        // fill the list with entries that cannot be drawn.
        if (formats.Contains(TypePng) && DataForType(pasteboard, TypePng) is { Length: > 0 } png)
        {
            var (width, height) = PngSize(png);
            return common with
            {
                Kind = ClipKind.Image,
                Image = png,
                ImageFormat = "PNG",
                PixelWidth = width,
                PixelHeight = height,
            };
        }

        var text = StringForType(pasteboard, TypeString);
        if (string.IsNullOrEmpty(text)) return null;

        return common with
        {
            Kind = ClipKind.Text,
            Text = text,
            Html = formats.Contains(TypeHtml) ? StringForType(pasteboard, TypeHtml) : null,
        };
    }

    /// <summary>The pasteboard's change counter, which ticks once per copy by anyone.</summary>
    public static long ChangeCount()
    {
        var pasteboard = GeneralPasteboard();
        return pasteboard == IntPtr.Zero ? 0 : SendLong(pasteboard, Sel("changeCount"));
    }

    // --- pasteboard readers ------------------------------------------------

    private static IntPtr GeneralPasteboard()
    {
        var cls = objc_getClass("NSPasteboard");
        return cls == IntPtr.Zero ? IntPtr.Zero : Send(cls, Sel("generalPasteboard"));
    }

    private static HashSet<string> TypesOf(IntPtr pasteboard)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        var array = Send(pasteboard, Sel("types"));
        if (array == IntPtr.Zero) return result;

        var count = SendLong(array, Sel("count"));
        for (nint i = 0; i < count; i++)
        {
            var item = SendIndex(array, Sel("objectAtIndex:"), i);
            if (ToManaged(item) is { } name) result.Add(name);
        }

        return result;
    }

    private static string? StringForType(IntPtr pasteboard, string type)
    {
        var key = ToNSString(type);
        if (key == IntPtr.Zero) return null;
        return ToManaged(SendArg(pasteboard, Sel("stringForType:"), key));
    }

    private static byte[]? DataForType(IntPtr pasteboard, string type)
    {
        var key = ToNSString(type);
        if (key == IntPtr.Zero) return null;

        var data = SendArg(pasteboard, Sel("dataForType:"), key);
        if (data == IntPtr.Zero) return null;

        var length = SendLong(data, Sel("length"));
        if (length <= 0) return null;

        var bytes = Send(data, Sel("bytes"));
        if (bytes == IntPtr.Zero) return null;

        var buffer = new byte[length];
        Marshal.Copy(bytes, buffer, 0, (int)length);
        return buffer;
    }

    /// <summary>
    /// File paths for a file copy. Each pasteboard item carries its own file URL, so a
    /// multiple selection needs the items walked rather than the pasteboard read once.
    /// </summary>
    private static string[]? ReadFiles(IntPtr pasteboard)
    {
        var items = Send(pasteboard, Sel("pasteboardItems"));
        if (items == IntPtr.Zero) return null;

        var count = SendLong(items, Sel("count"));
        var paths = new List<string>((int)count);

        for (nint i = 0; i < count; i++)
        {
            var item = SendIndex(items, Sel("objectAtIndex:"), i);
            if (item == IntPtr.Zero) continue;

            var key = ToNSString(TypeFileUrl);
            if (ToManaged(SendArg(item, Sel("stringForType:"), key)) is not { } url) continue;

            // Percent-encoded file:// URL; Uri handles the decoding, including non-ASCII names.
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.IsFile)
                paths.Add(parsed.LocalPath);
        }

        return paths.Count == 0 ? null : paths.ToArray();
    }

    /// <summary>
    /// The frontmost app, used as the clip's source. Unlike Windows there is no clipboard
    /// owner to ask, but whoever was frontmost when the pasteboard changed is almost
    /// always who copied — and comparing its pid to ours is how a paste-back from Klippy's
    /// own history is recognised instead of being recorded all over again.
    /// </summary>
    private static (string? Name, bool IsSelf) IdentifyFrontmostApp()
    {
        var workspace = objc_getClass("NSWorkspace");
        if (workspace == IntPtr.Zero) return (null, false);

        var shared = Send(workspace, Sel("sharedWorkspace"));
        if (shared == IntPtr.Zero) return (null, false);

        var app = Send(shared, Sel("frontmostApplication"));
        if (app == IntPtr.Zero) return (null, false);

        var name = ToManaged(Send(app, Sel("localizedName")));
        var pid = SendInt(app, Sel("processIdentifier"));
        return (name, pid == Environment.ProcessId);
    }

    /// <summary>Width and height from a PNG's IHDR, which is always the first chunk.</summary>
    private static (int Width, int Height) PngSize(byte[] png)
    {
        // 8-byte signature, 4-byte length, 4-byte "IHDR", then width and height big-endian.
        if (png.Length < 24) return (0, 0);

        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (width, height);
    }

    // --- objc plumbing -----------------------------------------------------

    private static IntPtr ToNSString(string value)
    {
        var cls = objc_getClass("NSString");
        if (cls == IntPtr.Zero) return IntPtr.Zero;

        var utf8 = Marshal.StringToHGlobalAnsi(value);
        try
        {
            return SendArg(cls, Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeHGlobal(utf8);
        }
    }

    private static string? ToManaged(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend is variadic in C, so each calling shape needs its own declaration
    // with the real signature — a single IntPtr-taking overload would pass arguments in
    // the wrong registers on arm64.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendArg(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nint index);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint SendLong(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern int SendInt(IntPtr receiver, IntPtr selector);
}
