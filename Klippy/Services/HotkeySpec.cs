using System;
using System.Collections.Generic;
using System.Text;

namespace Klippy.Services;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Meta = 8, // Windows key / macOS Command
}

/// <summary>
/// A global hotkey written the way a user would type it, e.g. "Ctrl+Alt+K" or "Cmd+Alt+K".
/// Parsing lives here (pure and testable); the OS-specific registration lives in the
/// desktop head, which needs the platform key codes below.
/// </summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>Sensible default per platform: Ctrl+Alt+K on Windows/Linux, ⌥⌘K on macOS.</summary>
    public static string PlatformDefault => OperatingSystem.IsMacOS() ? "Cmd+Alt+K" : "Ctrl+Alt+K";

    /// <summary>
    /// Plain literal, deliberately NOT routed through <see cref="TryParse"/>: TryParse seeds
    /// its out-parameter from this, so making this parse would recurse forever.
    /// </summary>
    private static readonly HotkeySpec Fallback = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K");

    public static HotkeySpec Default => TryParse(PlatformDefault, out var s) ? s : Fallback;

    /// <summary>
    /// Parses "Ctrl+Alt+K". Accepts Ctrl/Control, Alt/Option/Opt, Shift, Cmd/Command/Win/Meta,
    /// in any order and any case. Requires at least one modifier — a bare key would swallow
    /// that keystroke system-wide.
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = Fallback;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Control; break;
                case "alt" or "option" or "opt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "cmd" or "command" or "win" or "meta" or "super": mods |= HotkeyModifiers.Meta; break;
                default:
                    if (key is not null) return false; // more than one non-modifier key
                    key = raw.ToUpperInvariant();
                    break;
            }
        }

        if (key is null || mods == HotkeyModifiers.None) return false;
        if (!VirtualKeys.ContainsKey(key)) return false;

        spec = new HotkeySpec(mods, key);
        return true;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) sb.Append(OperatingSystem.IsMacOS() ? "Cmd+" : "Win+");
        return sb.Append(Key).ToString();
    }

    /// <summary>Windows virtual-key codes (VK_*). Letters and digits share their ASCII values.</summary>
    public static readonly IReadOnlyDictionary<string, uint> VirtualKeys = BuildVirtualKeys();

    /// <summary>Carbon virtual key codes (kVK_ANSI_*), which are positional and not ASCII.</summary>
    public static readonly IReadOnlyDictionary<string, uint> MacKeyCodes = new Dictionary<string, uint>
    {
        ["A"] = 0, ["B"] = 11, ["C"] = 8, ["D"] = 2, ["E"] = 14, ["F"] = 3, ["G"] = 5,
        ["H"] = 4, ["I"] = 34, ["J"] = 38, ["K"] = 40, ["L"] = 37, ["M"] = 46, ["N"] = 45,
        ["O"] = 31, ["P"] = 35, ["Q"] = 12, ["R"] = 15, ["S"] = 1, ["T"] = 17, ["U"] = 32,
        ["V"] = 9, ["W"] = 13, ["X"] = 7, ["Y"] = 16, ["Z"] = 6,
        ["0"] = 29, ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21,
        ["5"] = 23, ["6"] = 22, ["7"] = 26, ["8"] = 28, ["9"] = 25,
        ["SPACE"] = 49,
    };

    private static Dictionary<string, uint> BuildVirtualKeys()
    {
        var map = new Dictionary<string, uint>();
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;        // VK_A..VK_Z == 'A'..'Z'
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = c;        // VK_0..VK_9 == '0'..'9'
        map["SPACE"] = 0x20;
        return map;
    }
}
