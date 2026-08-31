using System;
using System.Collections.Generic;
using System.IO;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>Why a clipboard copy was or was not recorded.</summary>
public enum CaptureVerdict
{
    Capture,

    /// <summary>Klippy's own copy, echoed back by the platform's change notification.</summary>
    SelfWrite,

    Empty,

    /// <summary>Longer than the configured limit; a huge paste is not worth persisting.</summary>
    TooLong,

    /// <summary>The source app marked the clipboard as not-for-history.</summary>
    ExcludedFormat,

    /// <summary>The source app is on the user's exclusion list.</summary>
    ExcludedApp,

    /// <summary>Images or files, which the user has turned off.</summary>
    ExcludedKind,
}

/// <summary>What the platform saw on the clipboard at the moment it changed.</summary>
public sealed record CaptureContext
{
    /// <summary>Names of the clipboard formats present, e.g. "HTML Format".</summary>
    public IReadOnlyCollection<string> Formats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Value of the <c>CanIncludeInClipboardHistory</c> format, or null when absent.
    /// Unlike the other markers this one carries a DWORD, so presence alone means
    /// nothing — an app that sets it to 1 is opting *in*.
    /// </summary>
    public bool? CanIncludeInClipboardHistory { get; init; }

    /// <summary>
    /// Process that owned the foreground window when the copy happened, e.g.
    /// "C:\Program Files\KeePass\KeePass.exe" or just "keepass". Null when unknown.
    /// </summary>
    public string? SourceApp { get; init; }

    /// <summary>What the clip holds, which decides how its size is judged.</summary>
    public ClipKind Kind { get; init; } = ClipKind.Text;

    public int TextLength { get; init; }

    /// <summary>Payload size for an image clip, which is capped in bytes rather than characters.</summary>
    public int ImageBytes { get; init; }

    /// <summary>How many paths a file clip carries.</summary>
    public int FileCount { get; init; }

    /// <summary>True when Klippy itself put this on the clipboard.</summary>
    public bool IsSelfWrite { get; init; }
}

/// <summary>User-configurable limits on what gets recorded.</summary>
public sealed class CaptureRules
{
    /// <summary>Roughly 200 KB of UTF-16. Past this a clip is almost certainly a file dump.</summary>
    public int MaxTextLength { get; set; } = 100_000;

    /// <summary>
    /// 16 MB. Images are kept as blobs on disk, so the cap is about not filling a drive
    /// with copies of whole screens rather than about the JSON staying small.
    /// </summary>
    public int MaxImageBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Set false to leave screenshots and copied pictures out of the history.</summary>
    public bool CaptureImages { get; set; } = true;

    /// <summary>Set false to leave copied file selections out of the history.</summary>
    public bool CaptureFiles { get; set; } = true;

    /// <summary>Process names never recorded from, matched without path or extension.</summary>
    public IReadOnlyCollection<string> ExcludedApps { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Decides whether a clipboard change should be written to the history.
///
/// Deliberately pure: no Win32, no clipboard handle, nothing to mock. The platform
/// monitor's job is to describe what it saw, and this decides — which is what makes
/// the security-critical half of clipboard capture something tests can pin down.
///
/// The default answer for a password is "no", and it is the password manager that says
/// so: KeePass, 1Password and Bitwarden all mark their clipboard writes with formats
/// that ask managers like this one to look away. Honouring them is not optional.
/// </summary>
public static class CapturePolicy
{
    /// <summary>
    /// Clipboard formats whose mere presence means "do not record this".
    ///
    /// <c>ExcludeClipboardContentFromMonitorProcessing</c> is the modern marker set by
    /// password managers; <c>Clipboard Viewer Ignore</c> is the older convention that
    /// predates it and is still in use. Names are compared case-insensitively, matching
    /// how Windows registers them.
    /// </summary>
    public static readonly string[] BlockingFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    };

    /// <summary>The Windows Cloud Clipboard opt-out, which carries a value rather than just existing.</summary>
    public const string HistoryOptOutFormat = "CanIncludeInClipboardHistory";

    public static CaptureVerdict Evaluate(CaptureContext context, CaptureRules rules)
    {
        // Checked first: Klippy's own copies come back through the same notification, and
        // without this every snippet copied would be echoed straight into the history.
        if (context.IsSelfWrite) return CaptureVerdict.SelfWrite;

        if (IsEmpty(context)) return CaptureVerdict.Empty;

        if (context.Kind == ClipKind.Image && !rules.CaptureImages) return CaptureVerdict.ExcludedKind;
        if (context.Kind == ClipKind.Files && !rules.CaptureFiles) return CaptureVerdict.ExcludedKind;

        foreach (var format in context.Formats)
            foreach (var blocking in BlockingFormats)
                if (string.Equals(format, blocking, StringComparison.OrdinalIgnoreCase))
                    return CaptureVerdict.ExcludedFormat;

        if (context.CanIncludeInClipboardHistory == false) return CaptureVerdict.ExcludedFormat;

        if (IsExcludedApp(context.SourceApp, rules.ExcludedApps)) return CaptureVerdict.ExcludedApp;

        // Last, so that a rejection is reported as the specific reason it was refused
        // rather than as a size problem it also happens to have.
        if (IsTooBig(context, rules)) return CaptureVerdict.TooLong;

        return CaptureVerdict.Capture;
    }

    private static bool IsEmpty(CaptureContext context) => context.Kind switch
    {
        ClipKind.Image => context.ImageBytes <= 0,
        ClipKind.Files => context.FileCount <= 0,
        _ => context.TextLength <= 0,
    };

    private static bool IsTooBig(CaptureContext context, CaptureRules rules) => context.Kind switch
    {
        // A file clip is a handful of paths however large the files themselves are.
        ClipKind.Image => context.ImageBytes > rules.MaxImageBytes,
        ClipKind.Files => false,
        _ => context.TextLength > rules.MaxTextLength,
    };

    /// <summary>Convenience for callers that only care whether to store the clip.</summary>
    public static bool ShouldCapture(CaptureContext context, CaptureRules rules) =>
        Evaluate(context, rules) == CaptureVerdict.Capture;

    /// <summary>
    /// Compares on process name alone, so a user can write "keepass" and have it match
    /// "C:\Program Files\KeePass\KeePass.exe" — nobody wants to type a full path, and an
    /// exclusion that silently fails to match is worse than no exclusion at all.
    /// </summary>
    private static bool IsExcludedApp(string? sourceApp, IReadOnlyCollection<string> excluded)
    {
        if (string.IsNullOrWhiteSpace(sourceApp) || excluded.Count == 0) return false;

        var name = ProcessName(sourceApp);
        if (name.Length == 0) return false;

        foreach (var candidate in excluded)
            if (string.Equals(name, ProcessName(candidate), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string ProcessName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "";

        // Handles both a bare name and a full path, with or without the extension.
        var fileName = Path.GetFileName(trimmed);
        if (fileName.Length == 0) fileName = trimmed;

        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }
}
