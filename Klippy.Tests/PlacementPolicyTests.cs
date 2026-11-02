using Avalonia;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The placement arithmetic, driven directly rather than through a window.
///
/// Headless Avalonia reports exactly one screen at the desktop origin, scaling 1, with the
/// working area equal to the bounds — so it can never see a second monitor, a taskbar or
/// any scaling at all, and a test written against it would pass for the wrong reason.
/// </summary>
public class PlacementPolicyTests
{
    // MainWindow.axaml's own Width/Height: the real numbers, not a fixture.
    private static readonly Size Launcher = new(680, 560);

    [Fact]
    public void Centre_UsesTheScreenItWasGiven_NotTheDesktopOrigin()
    {
        // A second monitor's working area starts where the first one ends. Centring on the
        // desktop rather than the screen would put the window on the wrong display.
        var placed = PlacementPolicy.Centred(new PixelRect(1920, 0, 1920, 1080), 1.0, Launcher);
        Assert.Equal(new PixelPoint(2540, 260), placed);
    }

    [Fact]
    public void Centre_UsesTheWorkingArea_SoTheTaskbarDoesNotCoverTheWindow()
    {
        // WorkingArea is shorter than Bounds by the height of the taskbar; centring in the
        // full bounds would sit the window slightly too low.
        var placed = PlacementPolicy.Centred(new PixelRect(0, 0, 1920, 1032), 1.0, Launcher);
        Assert.Equal(new PixelPoint(620, 236), placed);
    }

    [Fact]
    public void Centre_OnATwoTimesScaledScreen_WorksInPhysicalPixels()
    {
        // 680x560 logical is 1360x1120 of actual pixels. Position is physical, size is not,
        // and forgetting to convert is the classic HiDPI half-off-screen bug.
        var placed = PlacementPolicy.Centred(new PixelRect(0, 0, 3840, 2160), 2.0, Launcher);
        Assert.Equal(new PixelPoint(1240, 520), placed);
    }

    [Fact]
    public void Pointer_HangsTheWindowFromTheCursor_ByTheTitleStrip()
    {
        // Horizontally centred, and dropped by the title strip's height so the cursor lands
        // on the search box rather than on the first row of the list.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(960, 540), new PixelRect(0, 0, 1920, 1080), 1.0, Launcher);
        Assert.Equal(new PixelPoint(620, 506), placed);
    }

    [Fact]
    public void Pointer_ScalesThatDropWithTheScreen()
    {
        // The 34 is logical, so on a 2x screen the window hangs 68 physical pixels below.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(1920, 1080), new PixelRect(0, 0, 3840, 2160), 2.0, Launcher);
        Assert.Equal(new PixelPoint(1240, 1012), placed);
    }

    [Fact]
    public void Pointer_ClampsAtTheRightEdge()
    {
        // Summoning with the cursor near the right edge must not push the window off it.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(1900, 500), new PixelRect(0, 0, 1920, 1080), 1.0, Launcher);
        Assert.Equal(new PixelPoint(1240, 466), placed);
        Assert.Equal(1920, placed.X + 680); // flush with the edge, not over it
    }

    [Fact]
    public void Pointer_ClampsAtTheBottomEdge()
    {
        // The bottom bound is the working area's, so the window stops above the taskbar.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(600, 1030), new PixelRect(0, 0, 1920, 1040), 1.0, Launcher);
        Assert.Equal(new PixelPoint(260, 480), placed);
        Assert.Equal(1040, placed.Y + 560);
    }

    [Fact]
    public void Pointer_ClampsToTheScreensOwnOrigin_NotZero()
    {
        // Clamping to 0,0 would drag a window on the second monitor back onto the first.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(1930, 10), new PixelRect(1920, 0, 1920, 1080), 1.0, Launcher);
        Assert.Equal(new PixelPoint(1920, 0), placed);
    }

    [Fact]
    public void Placement_OnAScreenSmallerThanTheWindow_PinsToTheWorkingAreaOrigin()
    {
        // A 640x480 projector cannot hold a 680x560 window. The upper clamp bound then falls
        // below the lower one, which Math.Clamp answers with an exception - so the hotkey
        // would take the process down rather than show a window slightly too big.
        var area = new PixelRect(0, 40, 640, 480);
        Assert.Equal(new PixelPoint(0, 40), PlacementPolicy.Centred(area, 1.0, Launcher));
        Assert.Equal(new PixelPoint(0, 40), PlacementPolicy.AtPointer(new PixelPoint(300, 300), area, 1.0, Launcher));
    }

    [Fact]
    public void Placement_AtFractionalScaling_RoundsTheWindowUp_SoClampingStaysConservative()
    {
        // 681 x 1.5 is 1021.5 physical pixels. PixelSize.FromSize rounds up, which is the
        // safe direction: the clamp reserves the whole pixel the window actually covers.
        var placed = PlacementPolicy.AtPointer(
            new PixelPoint(1900, 500), new PixelRect(0, 0, 1920, 1080), 1.5, new Size(681, 561));
        Assert.Equal(1920 - 1022, placed.X);
    }

    [Theory]
    [InlineData("Remembered", LauncherPlacement.Remembered)]
    [InlineData("Centre", LauncherPlacement.Centre)]
    [InlineData("pointer", LauncherPlacement.Pointer)]              // case-insensitive
    [InlineData("  Centre  ", LauncherPlacement.Centre)]            // hand-edited whitespace
    [InlineData("Center", LauncherPlacement.Remembered)]            // American spelling, British codebase
    [InlineData("Centre, Pointer", LauncherPlacement.Remembered)]   // TryParse ORs comma lists; IsDefined catches it
    [InlineData("99", LauncherPlacement.Remembered)]
    [InlineData("", LauncherPlacement.Remembered)]
    [InlineData(null, LauncherPlacement.Remembered)]
    public void Parse_TakesAnythingItRecognises_AndShrugsOffTheRest(string? raw, LauncherPlacement expected) =>
        Assert.Equal(expected, PlacementPolicy.Parse(raw));

    // ---- putting it back where it was left, last time Klippy ran ----

    // MainWindow.axaml's MinWidth/MinHeight.
    private static readonly Size Minimum = new(460, 360);

    private static readonly ScreenArea Laptop = new(new PixelRect(0, 0, 1920, 1032), 1.0);
    private static readonly ScreenArea RightHand = new(new PixelRect(1920, 0, 2560, 1400), 1.0);

    private static WindowBounds Left(int x, int y, double width = 680, double height = 560) =>
        new() { X = x, Y = y, Width = width, Height = height };

    [Fact]
    public void Restore_PutsItExactlyWhereItWasLeft()
    {
        var (position, size) = PlacementPolicy.Restore(Left(100, 80, 900, 700), Minimum, [Laptop], Laptop);

        Assert.Equal(new PixelPoint(100, 80), position);
        Assert.Equal(new Size(900, 700), size);
    }

    [Fact]
    public void Restore_OnTheSecondMonitor_WhileThatMonitorIsStillThere()
    {
        var (position, _) = PlacementPolicy.Restore(Left(3000, 200), Minimum, [Laptop, RightHand], Laptop);
        Assert.Equal(new PixelPoint(3000, 200), position);
    }

    [Fact]
    public void Restore_CentresItOnTheFallback_WhenItsMonitorHasGone()
    {
        // Left on the right-hand monitor, which has since been unplugged: restored there it would be a window
        // nobody can see, so it comes back centred where it can be.
        var (position, size) = PlacementPolicy.Restore(Left(3000, 200), Minimum, [Laptop], Laptop);

        Assert.Equal(PlacementPolicy.Centred(Laptop.WorkingArea, Laptop.Scaling, size), position);
        Assert.Equal(new Size(680, 560), size);
    }

    [Fact]
    public void Restore_NeedsEnoughOfTheTitleStripToTakeHoldOf()
    {
        // All but a sliver of it hanging off the right-hand edge: on screen in name only.
        var sliver = PlacementPolicy.Restore(Left(1920 - 40, 200), Minimum, [Laptop], Laptop);
        Assert.NotEqual(new PixelPoint(1880, 200), sliver.Position);

        // Enough to grab and drag back is left where it was — that is where it was left.
        var grip = PlacementPolicy.Restore(Left(1920 - 200, 200), Minimum, [Laptop], Laptop);
        Assert.Equal(new PixelPoint(1720, 200), grip.Position);

        // Above the top of the screen, the strip is out of reach however much of the rest is on it.
        var above = PlacementPolicy.Restore(Left(100, -30), Minimum, [Laptop], Laptop);
        Assert.NotEqual(new PixelPoint(100, -30), above.Position);
    }

    [Fact]
    public void Restore_HoldsTheSizeToTheWindowsOwnMinimum()
    {
        // A hand-edited window.json can say anything; the XAML's minimum is what the window allows.
        var (_, size) = PlacementPolicy.Restore(Left(100, 80, 200, 100), Minimum, [Laptop], Laptop);
        Assert.Equal(Minimum, size);
    }

    [Fact]
    public void Restore_FitsAWindowLeftFillingABiggerScreen_AndPullsItBackOnto()
    {
        // Left 2400 wide on the big monitor; the same place on a smaller one now. It shrinks to the working
        // area, and having shrunk it is held inside it rather than left hanging off where its old size reached.
        var big = new ScreenArea(new PixelRect(0, 0, 1600, 900), 1.0);
        var (position, size) = PlacementPolicy.Restore(Left(500, 100, 2400, 1300), Minimum, [big], big);

        Assert.Equal(new Size(1600, 900), size);
        Assert.Equal(new PixelPoint(0, 0), position);
    }

    [Fact]
    public void Restore_MeasuresTheTitleStripAtTheScreensOwnScaling()
    {
        // On a 2x screen the strip is 68 physical pixels tall, so a window whose top is 40 pixels above the
        // working area still has 28 of them — less than half, so it is not where anyone can grab it.
        var hiDpi = new ScreenArea(new PixelRect(0, 0, 3840, 2100), 2.0);
        var (position, _) = PlacementPolicy.Restore(Left(200, -40), Minimum, [hiDpi], hiDpi);
        Assert.NotEqual(new PixelPoint(200, -40), position);

        // Twenty above leaves 48 of 68, which is.
        var (kept, _) = PlacementPolicy.Restore(Left(200, -20), Minimum, [hiDpi], hiDpi);
        Assert.Equal(new PixelPoint(200, -20), kept);
    }
}
