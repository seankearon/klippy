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
    /// Summons the clipboard history rather than the snippets. Next to the snippet key on
    /// the keyboard, because the two are the same gesture aimed at different halves of
    /// the app and the fingers should not have to travel between them.
    /// </summary>
    public static string HistoryPlatformDefault => OperatingSystem.IsMacOS() ? "Cmd+Alt+J" : "Ctrl+Alt+J";

    /// <summary>
    /// Plain literal, deliberately NOT routed through <see cref="TryParse"/>: TryParse seeds
    /// its out-parameter from this, so making this parse would recurse forever.
    /// </summary>
    private static readonly HotkeySpec Fallback = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K");

    public static HotkeySpec Default => TryParse(PlatformDefault, out var s) ? s : Fallback;

    /// <summary>
    /// Parses "Ctrl+Alt+K". Accepts Ctrl/Control, Alt/Option/Opt, Shift, Cmd/Command/Win/Meta,
    /// in any order and any case. Requires at least one modifier — a bare key would swallow
    /// that keystroke system-wide. The key is a letter, a digit, Space, F1–F20, or one of the
    /// punctuation keys by its character (<c>/</c>, <c>;</c>, <c>'</c>, <c>,</c>, <c>.</c>,
    /// <c>-</c>, <c>=</c>, <c>[</c>, <c>]</c>, <c>\</c>, <c>`</c>) or its name (Slash, Comma…).
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
                    if (PunctuationNames.TryGetValue(key, out var character)) key = character;
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

    /// <summary>
    /// Words a punctuation key may be written as, for anyone who would rather not put a
    /// backslash or a comma in a settings file. Each reads as the character.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PunctuationNames = new Dictionary<string, string>
    {
        ["SLASH"] = "/", ["SEMICOLON"] = ";", ["QUOTE"] = "'", ["COMMA"] = ",", ["PERIOD"] = ".",
        ["DOT"] = ".", ["MINUS"] = "-", ["EQUALS"] = "=", ["PLUS"] = "=", ["LEFTBRACKET"] = "[",
        ["RIGHTBRACKET"] = "]", ["BACKSLASH"] = "\\", ["BACKTICK"] = "`", ["GRAVE"] = "`",
    };

    /// <summary>
    /// Windows virtual-key codes (VK_*). Letters and digits share their ASCII values.
    ///
    /// The punctuation codes (VK_OEM_*) name a key by what it types on a US layout, and Windows
    /// maps them through the active layout — so on a UK keyboard "/" is the key marked "/", but
    /// on a German one VK_OEM_2 is the "#" key. Recording and registering agree either way,
    /// since both go through the same mapping; only the label can differ from the keycap.
    /// </summary>
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
        // Punctuation, kVK_ANSI_*: the key in that position on a US keyboard.
        ["="] = 24, ["-"] = 27, ["]"] = 30, ["["] = 33, ["'"] = 39, [";"] = 41,
        ["\\"] = 42, [","] = 43, ["/"] = 44, ["."] = 47, ["`"] = 50,
        // kVK_F1…kVK_F20, which follow no pattern. Carbon has no F21–F24.
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97,
        ["F7"] = 98, ["F8"] = 100, ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111,
        ["F13"] = 105, ["F14"] = 107, ["F15"] = 113, ["F16"] = 106, ["F17"] = 64, ["F18"] = 79,
        ["F19"] = 80, ["F20"] = 90,
    };

    private static Dictionary<string, uint> BuildVirtualKeys()
    {
        var map = new Dictionary<string, uint>();
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;        // VK_A..VK_Z == 'A'..'Z'
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = c;        // VK_0..VK_9 == '0'..'9'
        map["SPACE"] = 0x20;

        // VK_OEM_* — see the remarks on VirtualKeys about layouts.
        map[";"] = 0xBA;  // VK_OEM_1
        map["="] = 0xBB;  // VK_OEM_PLUS
        map[","] = 0xBC;  // VK_OEM_COMMA
        map["-"] = 0xBD;  // VK_OEM_MINUS
        map["."] = 0xBE;  // VK_OEM_PERIOD
        map["/"] = 0xBF;  // VK_OEM_2
        map["`"] = 0xC0;  // VK_OEM_3
        map["["] = 0xDB;  // VK_OEM_4
        map["\\"] = 0xDC; // VK_OEM_5
        map["]"] = 0xDD;  // VK_OEM_6
        map["'"] = 0xDE;  // VK_OEM_7

        // VK_F1..VK_F20. Windows goes to F24, but a Mac stops at F20, and a key that registers
        // on one platform and silently does nothing on the other is worse than not offering it.
        for (uint n = 1; n <= 20; n++) map[$"F{n}"] = 0x70 + n - 1;
        return map;
    }
}
