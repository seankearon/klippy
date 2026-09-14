using System;

namespace Klippy.Services;

/// <summary>
/// How the app runs what <see cref="LaunchPolicy"/> recognised, or null where nothing can be run.
///
/// A static hook, matching how the platform heads already configure
/// <see cref="StorageLocations.Directory"/>, <see cref="RichTextClipboard.PlatformWriter"/>
/// and <see cref="ClipboardHistory.Store"/>: only a head knows whether its OS has a shell to
/// hand a path to, and the shared view models are built before any of them could say.
///
/// Null on Android and iOS. Neither can open a folder, run a program or restart the machine
/// on the user's behalf, and an offer that can only fail is worse than no offer — so the
/// whole feature is simply absent there, as the clipboard history is.
/// </summary>
public static class LaunchRunner
{
    /// <summary>
    /// Set by a head that can execute, and read once by each view model as it is built.
    /// Allowed to throw — the message is shown to the user where the offer was, since
    /// "nothing happened" is the one answer a launcher must never give.
    /// </summary>
    public static Action<LaunchTarget>? Runner { get; set; }

    /// <summary>Whether the app should offer to run an unmatched search at all.</summary>
    public static bool IsAvailable => Runner is not null;
}
