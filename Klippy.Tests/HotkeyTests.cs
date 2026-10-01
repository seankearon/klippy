using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class HotkeyTests
{
    [Fact]
    public void Default_DoesNotRecurse_AndIsUsable()
    {
        // Regression: Default used to be defined via TryParse while TryParse seeded its
        // out-parameter from Default, which stack-overflowed the app on startup.
        var spec = HotkeySpec.Default;
        Assert.Equal("K", spec.Key);
        Assert.True(HotkeySpec.TryParse(HotkeySpec.PlatformDefault, out var parsed));
        Assert.Equal(parsed, spec);
    }

    [Theory]
    [InlineData("Ctrl+Alt+K", HotkeyModifiers.Control | HotkeyModifiers.Alt, "K")]
    [InlineData("cmd+alt+k", HotkeyModifiers.Meta | HotkeyModifiers.Alt, "K")]
    [InlineData("Alt+Ctrl+K", HotkeyModifiers.Control | HotkeyModifiers.Alt, "K")]   // order free
    [InlineData("Control + Shift + Space", HotkeyModifiers.Control | HotkeyModifiers.Shift, "SPACE")]
    [InlineData("Win+Shift+7", HotkeyModifiers.Meta | HotkeyModifiers.Shift, "7")]
    [InlineData("Option+Cmd+V", HotkeyModifiers.Alt | HotkeyModifiers.Meta, "V")]
    [InlineData("Ctrl+Alt+/", HotkeyModifiers.Control | HotkeyModifiers.Alt, "/")]     // punctuation
    [InlineData("Ctrl+Alt+Slash", HotkeyModifiers.Control | HotkeyModifiers.Alt, "/")] // …or by name
    [InlineData("Alt+\\", HotkeyModifiers.Alt, "\\")]
    [InlineData("ctrl+shift+f13", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F13")]
    public void TryParse_AcceptsUsualSpellings(string text, HotkeyModifiers mods, string key)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(mods, spec.Modifiers);
        Assert.Equal(key, spec.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("K")]              // no modifier would swallow the key system-wide
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]       // modifiers only
    [InlineData("Ctrl+Alt+K+J")]   // two real keys
    [InlineData("Ctrl+Alt+F21")]   // outside the supported key table: a Mac stops at F20
    [InlineData("Ctrl+Alt+Home")]
    public void TryParse_RejectsUnusable(string? text)
    {
        Assert.False(HotkeySpec.TryParse(text, out _));
    }

    [Fact]
    public void EveryParsableKey_HasCodesForBothPlatforms()
    {
        // A key that parses but has no macOS code would register on Windows and silently
        // do nothing on a Mac.
        foreach (var key in HotkeySpec.VirtualKeys.Keys)
            Assert.True(HotkeySpec.MacKeyCodes.ContainsKey(key), $"no macOS key code for '{key}'");
    }

    [Theory]
    [InlineData("/", 0xBFu, 44u)]   // VK_OEM_2, kVK_ANSI_Slash
    [InlineData(";", 0xBAu, 41u)]   // VK_OEM_1, kVK_ANSI_Semicolon
    [InlineData("=", 0xBBu, 24u)]   // VK_OEM_PLUS, kVK_ANSI_Equal
    [InlineData("`", 0xC0u, 50u)]   // VK_OEM_3, kVK_ANSI_Grave
    [InlineData("F1", 0x70u, 122u)]
    [InlineData("F5", 0x74u, 96u)]
    [InlineData("F20", 0x83u, 90u)]
    public void PunctuationAndFunctionKeys_CarryEachPlatformsCode(string key, uint windows, uint mac)
    {
        Assert.Equal(windows, HotkeySpec.VirtualKeys[key]);
        Assert.Equal(mac, HotkeySpec.MacKeyCodes[key]);
    }

    [Fact]
    public void BothPlatformDefaultsParse()
    {
        Assert.True(HotkeySpec.TryParse(HotkeySpec.PlatformDefault, out _));
        Assert.True(HotkeySpec.TryParse(HotkeySpec.HistoryPlatformDefault, out _));
        Assert.NotEqual(HotkeySpec.PlatformDefault, HotkeySpec.HistoryPlatformDefault);
    }

    [Fact]
    public void AnEmptyHistoryHotkeyTurnsThatKeyOffRatherThanFallingBack()
    {
        // Falling back to a default here would re-register a key the user just removed.
        Assert.Null(new AppSettings { HistoryHotkey = "" }.ParsedHistoryHotkey);
        Assert.Null(new AppSettings { HistoryHotkey = "nonsense" }.ParsedHistoryHotkey);
        Assert.NotNull(new AppSettings { HistoryHotkey = "Ctrl+Alt+J" }.ParsedHistoryHotkey);
    }

    [Fact]
    public void RoundTripsThroughToString()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Alt+K", out var spec));
        Assert.Equal("Ctrl+Alt+K", spec.ToString());
        Assert.True(HotkeySpec.TryParse(spec.ToString(), out var again));
        Assert.Equal(spec, again);
    }

    [Fact]
    public void APunctuationKey_RoundTripsAsItsCharacter()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+Alt+Comma", out var spec));
        Assert.Equal("Ctrl+Alt+,", spec.ToString());
        Assert.True(HotkeySpec.TryParse(spec.ToString(), out var again));
        Assert.Equal(spec, again);
    }
}
