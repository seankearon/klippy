using System;
using System.Collections.Generic;
using Avalonia;

namespace Klippy.Services;

/// <summary>
/// A screen as the placement rules need to know it: where a window may go on it, which
/// leaves out the taskbar, and how many physical pixels a logical one is there.
/// </summary>
public readonly record struct ScreenArea(PixelRect WorkingArea, double Scaling);

/// <summary>Where the launcher window lands when a hotkey summons it.</summary>
public enum LauncherPlacement
{
    /// <summary>
    /// Wherever it was left — across a restart too, through <see cref="WindowBounds"/>. What
    /// Klippy has always done, and the default.
    /// </summary>
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
    /// How much of the title strip has to be on a screen, in logical pixels across, for the
    /// window to count as left there: enough to take hold of and drag back. Less, and a
    /// window restored to where it was would be on screen in name only.
    /// </summary>
    public const double MinimumGrip = 120;

    /// <summary>
    /// The window as it was last left, fitted to the screens there are now. Where it was, if
    /// enough of its title strip is still on one of them to grab; otherwise centred on
    /// <paramref name="fallback"/>, since a monitor unplugged since last time is the usual
    /// reason it is not, and a window restored onto a screen that is no longer there is a
    /// window you cannot see. Its size either way, held to <paramref name="minimum"/> and to
    /// the screen it lands on, so a window left filling a 4K panel still fits a laptop's.
    /// </summary>
    /// <param name="minimum">The window's own MinWidth and MinHeight, which a hand-edited file may undercut.</param>
    /// <param name="screens">Every screen there is, by working area and scaling.</param>
    /// <param name="fallback">Where it goes when it is on none of them — the primary, as a rule.</param>
    public static (PixelPoint Position, Size Size) Restore(
        WindowBounds saved, Size minimum, IReadOnlyList<ScreenArea> screens, ScreenArea fallback)
    {
        var size = new Size(Math.Max(saved.Width, minimum.Width), Math.Max(saved.Height, minimum.Height));
        var at = new PixelPoint(saved.X, saved.Y);

        foreach (var screen in screens)
        {
            if (!ShowsTitleStrip(at, size, screen)) continue;

            // Exactly where it was, which is the point — unless it had to shrink to fit, in
            // which case where it was may now hang off the edge it used to reach.
            var fitted = Fit(size, minimum, screen);
            return fitted == size
                ? (at, size)
                : (Clamp(at, PixelSize.FromSize(fitted, screen.Scaling), screen.WorkingArea), fitted);
        }

        var shrunk = Fit(size, minimum, fallback);
        return (Centred(fallback.WorkingArea, fallback.Scaling, shrunk), shrunk);
    }

    /// <summary>
    /// Whether at least half the title strip's height, and <see cref="MinimumGrip"/> of its
    /// width, are inside the screen's working area — measured at that screen's scaling,
    /// since that is the scaling the window would have there.
    /// </summary>
    private static bool ShowsTitleStrip(PixelPoint at, Size size, ScreenArea screen)
    {
        var strip = new PixelRect(at, PixelSize.FromSize(new Size(size.Width, TitleStripHeight), screen.Scaling));
        var seen = strip.Intersect(screen.WorkingArea);
        var grip = Math.Min(strip.Width, (int)Math.Round(MinimumGrip * screen.Scaling));
        return seen.Width >= grip && seen.Height * 2 >= strip.Height;
    }

    /// <summary>
    /// No bigger than the screen's working area, and no smaller than the window allows —
    /// which wins if the two disagree, as on a screen smaller than the window's minimum.
    /// </summary>
    private static Size Fit(Size size, Size minimum, ScreenArea screen)
    {
        var room = screen.WorkingArea.Size.ToSize(screen.Scaling);
        return new Size(
            Math.Max(minimum.Width, Math.Min(size.Width, room.Width)),
            Math.Max(minimum.Height, Math.Min(size.Height, room.Height)));
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
