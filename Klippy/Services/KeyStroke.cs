using System;
using Avalonia.Input;

namespace Klippy.Services;

/// <summary>
/// One press of a keyboard shortcut: a key and the modifiers held down for it — <c>Ctrl+K</c>, <c>F5</c>,
/// <c>Ctrl+Shift+P</c>. A <see cref="Shortcut"/> is one of these, or two in a row for a chord.
///
/// Built through <see cref="From"/>, which is what makes two presses of "the same keys" compare equal:
/// lock and mouse-button flags are dropped from the modifiers, and the numpad digits fold onto the top-row
/// ones, so <c>Ctrl+1</c> answers to either — which is what anyone pressing it means.
///
/// Its text form (<see cref="ToString"/>) is what a snippet stores and what <see cref="TryParse"/> reads:
/// modifiers in a fixed order — <c>Ctrl+Shift+Alt+Meta</c> — then the key, by the name printed on it where
/// it has one (<c>Ctrl+,</c>, <c>Ctrl+[</c>). <see cref="DisplayText"/> is the same with the platform's name
/// for the Meta key, which a Mac calls Cmd.
/// </summary>
public readonly record struct KeyStroke
{
    private const KeyModifiers Significant =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;

    private KeyStroke(KeyModifiers modifiers, Key key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>Ctrl, Shift, Alt and Meta — never anything else.</summary>
    public KeyModifiers Modifiers { get; }

    public Key Key { get; }

    /// <summary>The stroke for <paramref name="key"/> pressed with <paramref name="modifiers"/>, normalised.</summary>
    public static KeyStroke From(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new(modifiers & Significant, key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (key - Key.NumPad0),
            _ => key,
        });

    /// <summary><c>Ctrl+</c><paramref name="key"/> — the shape most shortcuts take.</summary>
    public static KeyStroke Ctrl(Key key) => From(key, KeyModifiers.Control);

    /// <summary>True for a modifier going down on its own: the first half of pressing something, not a press.</summary>
    public bool IsModifierKey => Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None;

    /// <summary>True for <c>F1</c> … <c>F24</c>, which type nothing and so can stand alone as a shortcut.</summary>
    public bool IsFunctionKey => Key is >= Key.F1 and <= Key.F24;

    /// <summary>Plain <c>Esc</c>: what backs out of a chord half-way, or out of recording one.</summary>
    public bool IsEscape => Key == Key.Escape && Modifiers == KeyModifiers.None;

    /// <summary>Plain <c>Enter</c>: what keeps a single press as the whole shortcut when recording one.</summary>
    public bool IsEnter => Key == Key.Enter && Modifiers == KeyModifiers.None;

    /// <summary>
    /// Keys the operating system answers before Klippy is asked — the window's system menu, and closing the
    /// window. A shortcut on one of them would either never fire or fire as well as the OS's own action.
    /// </summary>
    public bool IsReserved => Modifiers == KeyModifiers.Alt && Key is Key.Space or Key.F4;

    /// <summary>
    /// Whether this stroke can open a shortcut. It has to hold Ctrl, Alt or Meta down — or be a function key —
    /// because anything else is typing: a shortcut on plain <c>K</c>, or <c>Shift+K</c>, would take the letter
    /// away from the search box. The second stroke of a chord is free of that rule: by then Klippy is
    /// listening for it, and nothing gets typed.
    /// </summary>
    public bool CanBegin =>
        !IsModifierKey && !IsReserved
        && ((Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 || IsFunctionKey);

    /// <summary>Whether this stroke can finish a chord: any real key but <c>Esc</c>, which calls the chord off.</summary>
    public bool CanFollow => !IsModifierKey && !IsEscape;

    /// <summary>The stored form, e.g. <c>Ctrl+Shift+K</c>; read back by <see cref="TryParse"/>.</summary>
    public override string ToString() => Format(MetaName);

    /// <summary>What to put on screen: <see cref="ToString"/> with the Meta key called what this OS calls it.</summary>
    public string DisplayText => Format(PlatformMetaName);

    private const string MetaName = "Meta";

    /// <summary>The Meta key by this platform's name for it: Cmd on a Mac, Win on Windows, Super elsewhere.</summary>
    public static string PlatformMetaName =>
        OperatingSystem.IsMacOS() ? "Cmd" : OperatingSystem.IsWindows() ? "Win" : "Super";

    private string Format(string metaName)
    {
        var text = "";
        if (Modifiers.HasFlag(KeyModifiers.Control)) text += "Ctrl+";
        if (Modifiers.HasFlag(KeyModifiers.Shift)) text += "Shift+";
        if (Modifiers.HasFlag(KeyModifiers.Alt)) text += "Alt+";
        if (Modifiers.HasFlag(KeyModifiers.Meta)) text += metaName + "+";

        // A bare comma would read as the gap between a chord's two strokes, so on its own it gets a name.
        return text + (Key == Key.OemComma && Modifiers == KeyModifiers.None ? "Comma" : KeyName(Key));
    }

    /// <summary>
    /// Reads one stroke as <see cref="ToString"/> writes it, and as people type it: modifiers in any order and
    /// any case, <c>Control</c>/<c>Cmd</c>/<c>Win</c>/<c>Option</c> as well, keys by the character on them or
    /// by name. False for anything that isn't a single key with modifiers.
    /// </summary>
    public static bool TryParse(string? text, out KeyStroke stroke)
    {
        stroke = default;
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return false;

        // The key is whatever follows the last '+' — unless it *is* the '+', as in "Ctrl++".
        string keyPart, modifierPart;
        if (t == "+")
        {
            (keyPart, modifierPart) = ("+", "");
        }
        else if (t.EndsWith("++", StringComparison.Ordinal))
        {
            (keyPart, modifierPart) = ("+", t[..^2]);
        }
        else
        {
            var split = t.LastIndexOf('+');
            (keyPart, modifierPart) = split < 0 ? (t, "") : (t[(split + 1)..], t[..split]);
        }

        var modifiers = KeyModifiers.None;
        foreach (var token in modifierPart.Split('+', StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0 && modifierPart.Length == 0) continue;
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": modifiers |= KeyModifiers.Control; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "alt" or "option" or "opt": modifiers |= KeyModifiers.Alt; break;
                case "meta" or "cmd" or "command" or "win" or "windows" or "super" or "⌘": modifiers |= KeyModifiers.Meta; break;
                default: return false;
            }
        }

        if (!TryParseKey(keyPart.Trim(), out var key)) return false;
        stroke = From(key, modifiers);
        return !stroke.IsModifierKey;
    }

    /// <summary>The key's name in a shortcut: its character where it has a plain one, else a word for it.</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.Enter => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        _ => key.ToString(),
    };

    private static bool TryParseKey(string name, out Key key)
    {
        key = Key.None;
        if (name.Length == 0) return false;

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            key = c switch
            {
                >= '0' and <= '9' => Key.D0 + (c - '0'),
                >= 'A' and <= 'Z' => Key.A + (c - 'A'),
                ',' => Key.OemComma,
                '.' => Key.OemPeriod,
                '-' => Key.OemMinus,
                '=' or '+' => Key.OemPlus,
                '/' => Key.OemQuestion,
                ';' => Key.OemSemicolon,
                '\'' => Key.OemQuotes,
                '[' => Key.OemOpenBrackets,
                ']' => Key.OemCloseBrackets,
                '\\' => Key.OemPipe,
                '`' => Key.OemTilde,
                _ => Key.None,
            };
            return key != Key.None;
        }

        key = name.ToLowerInvariant() switch
        {
            "comma" => Key.OemComma,
            "period" or "dot" => Key.OemPeriod,
            "minus" => Key.OemMinus,
            "plus" or "equals" => Key.OemPlus,
            "slash" => Key.OemQuestion,
            "backslash" => Key.OemPipe,
            "semicolon" => Key.OemSemicolon,
            "quote" => Key.OemQuotes,
            "backtick" or "backquote" => Key.OemTilde,
            "enter" or "return" => Key.Enter,
            "esc" or "escape" => Key.Escape,
            "backspace" or "back" => Key.Back,
            "del" or "delete" => Key.Delete,
            "ins" or "insert" => Key.Insert,
            "pageup" or "pgup" => Key.PageUp,
            "pagedown" or "pgdn" => Key.PageDown,
            "space" => Key.Space,
            "tab" => Key.Tab,
            _ => Key.None,
        };
        if (key != Key.None) return true;

        // Everything else by its Avalonia name — F5, Home, Up, MediaPlayPause — but never by number, which
        // Enum.TryParse would otherwise happily accept.
        return !char.IsDigit(name[0]) && Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }
}
