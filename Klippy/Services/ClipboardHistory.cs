namespace Klippy.Services;

/// <summary>
/// The clipboard history the app should show, or null where there isn't one.
///
/// A static hook, matching how the platform heads already configure
/// <see cref="StorageLocations.Directory"/> and
/// <see cref="RichTextClipboard.PlatformWriter"/>: only a head knows whether its OS can
/// capture the clipboard at all, and the shared view models are built before any of them
/// could hand one over. Null on Android and iOS, which forbid background clipboard
/// reads, and on desktop when the user has turned history off.
/// </summary>
public static class ClipboardHistory
{
    public static ClipHistoryStore? Store { get; set; }

    /// <summary>Whether the app should offer a history view at all.</summary>
    public static bool IsAvailable => Store is not null;
}
