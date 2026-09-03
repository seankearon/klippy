using System;
using Avalonia;

namespace Klippy.Services;

/// <summary>Where the launcher window lands when a hotkey summons it.</summary>
public enum LauncherPlacement
{
    /// <summary>Wherever it was left. What Klippy has always done, and the default.</summary>
    Remembered,

    /// <summary>Centred on the screen the pointer is on.</summary>
    Centre,

    /// <summary>Hung from the mouse pointer, nudged to stay fully on screen.</summary>
    Pointer,
}

/// <summary>
/// Works out where to put the launcher window.
///
/// Deliberately pure: no Window, no Screens, no cursor. The desktop head describes the
/// screen it found and this decides — which is what makes the multi-monitor and HiDPI
/// arithmetic something tests can pin down. Headless Avalonia only ever reports one
/// unscaled screen with no taskbar, so a test that went through a real window could not
/// reach a single case that actually goes wrong.
/// </summary>
public static class PlacementPolicy
{
    /// <summary>
    /// The title strip's height, matching MainWindow's ExtendClientAreaTitleBarHeightHint.
    /// Hanging the window this far below the pointer puts the search box under the cursor
    /// and the first row of the list clear of it — summon, type, Enter, without the
    /// pointer sitting on a snippet you did not mean to pick.
    /// </summary>
    public const double TitleStripHeight = 34;

    /// <summary>
    /// The placement, or <see cref="LauncherPlacement.Remembered"/> when the value is
    /// missing or unreadable — settings.json is a file people edit by hand, and one typo
    /// must not be worth an exception that <see cref="AppSettings.Load"/> would answer by
    /// discarding every other preference in the file.
    /// </summary>
    public static LauncherPlacement Parse(string? raw) =>
        // IsDefined earns its place: TryParse accepts comma lists and ORs them, so
        // "Centre, Pointer" would otherwise parse to a value matching no chip at all.
        Enum.TryParse<LauncherPlacement>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : LauncherPlacement.Remembered;

    /// <summary>Centred on the given screen's working area.</summary>
    public static PixelPoint Centred(PixelRect workingArea, double scaling, Size logicalSize)
    {
        var size = PixelSize.FromSize(logicalSize, scaling);
        return Clamp(workingArea.CenterRect(new PixelRect(size)).Position, size, workingArea);
    }

    /// <summary>Hung from the pointer, then pulled back onto the screen it belongs to.</summary>
    public static PixelPoint AtPointer(PixelPoint pointer, PixelRect workingArea, double scaling, Size logicalSize)
    {
        var size = PixelSize.FromSize(logicalSize, scaling);
        return Clamp(
            new PixelPoint(
                pointer.X - size.Width / 2,
                pointer.Y - (int)Math.Round(TitleStripHeight * scaling)),
            size, workingArea);
    }

    /// <summary>
    /// Holds the whole window inside the working area, which excludes the taskbar.
    ///
    /// The Math.Max guards a window bigger than the screen — a 640x480 projector, or a 4K
    /// panel at 300%. Without it the upper bound falls below the lower one and Math.Clamp
    /// throws, turning a bad position into a crash on the hotkey.
    /// </summary>
    private static PixelPoint Clamp(PixelPoint desired, PixelSize size, PixelRect workingArea) =>
        new(Math.Clamp(desired.X, workingArea.X, workingArea.X + Math.Max(0, workingArea.Width - size.Width)),
            Math.Clamp(desired.Y, workingArea.Y, workingArea.Y + Math.Max(0, workingArea.Height - size.Height)));
}
