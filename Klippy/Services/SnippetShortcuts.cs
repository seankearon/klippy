using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>A snippet shortcut that is live: these keys trigger this snippet.</summary>
public sealed record SnippetBinding(Guid SnippetId, string Label, Shortcut Keys);

/// <summary>
/// The in-Klippy shortcuts in force — which keys trigger which snippet — built from the snippets themselves
/// (<see cref="Snippet.Shortcut"/>).
///
/// It never holds two that get in each other's way: not the same keys twice, and not a single press that is
/// also where a chord starts (with <c>Ctrl+K</c> bound, <c>Ctrl+K, Ctrl+L</c> could never be reached). The
/// editor keeps that from happening, but the snippet file is one people can edit and import, so a clash is
/// settled the same way every time: the first snippet in the file keeps the keys. A shortcut that starts on a
/// key Klippy already uses (<see cref="KlippyKeys"/>), on a system-wide hotkey — which the OS hands to Klippy
/// before the window ever sees the press — or on plain typing, is left out too: it could never fire.
/// </summary>
public sealed class SnippetShortcutMap
{
    private readonly Dictionary<Guid, SnippetBinding> _bySnippet = new();
    private readonly Dictionary<Shortcut, SnippetBinding> _byKeys = new();
    private readonly HashSet<KeyStroke> _chordStarts = new();

    private SnippetShortcutMap(IEnumerable<SnippetBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (_bySnippet.ContainsKey(binding.SnippetId)) continue;
            if (_bySnippet.Values.Any(taken => taken.Keys.CollidesWith(binding.Keys))) continue;

            _bySnippet[binding.SnippetId] = binding;
            _byKeys[binding.Keys] = binding;
            if (binding.Keys.IsChord) _chordStarts.Add(binding.Keys.First);
        }
        Bindings = _bySnippet.Values.ToList();
    }

    public static SnippetShortcutMap Empty { get; } = new([]);

    /// <summary>The live bindings, in the order they won their keys.</summary>
    public IReadOnlyList<SnippetBinding> Bindings { get; }

    /// <summary>
    /// The shortcuts <paramref name="snippets"/> ask for, with any clash settled. <paramref name="globalKeys"/>
    /// are the system-wide hotkeys in force, which no shortcut may start on.
    /// </summary>
    public static SnippetShortcutMap Build(IEnumerable<Snippet> snippets,
        IReadOnlyCollection<HotkeySpec>? globalKeys = null, bool? mac = null)
    {
        var bindings = new List<SnippetBinding>();
        foreach (var snippet in snippets)
            if (TryRead(snippet.Shortcut, out var keys, globalKeys, mac))
                bindings.Add(new SnippetBinding(snippet.Id, snippet.Label, keys));
        return new SnippetShortcutMap(bindings);
    }

    /// <summary>
    /// Reads a snippet's shortcut, and says whether it could ever fire: it parses, it starts with a press that
    /// can't be typing, and that press is neither one of Klippy's own keys nor one of
    /// <paramref name="globalKeys"/>.
    /// </summary>
    public static bool TryRead(string? text, out Shortcut keys,
        IReadOnlyCollection<HotkeySpec>? globalKeys = null, bool? mac = null)
    {
        if (!Shortcut.TryParse(text, out keys) || !keys.IsValid) return false;
        if ((mac is { } isMac ? KlippyKeys.OwnerOf(keys.First, isMac) : KlippyKeys.OwnerOf(keys.First)) is not null)
            return false;
        return globalKeys is null || !SnippetHotkeys.TryFromStroke(keys.First, out var asHotkey)
               || !globalKeys.Contains(asHotkey);
    }

    /// <summary>The keys that trigger <paramref name="snippetId"/>, or null when it has none that work.</summary>
    public Shortcut? For(Guid snippetId) => _bySnippet.TryGetValue(snippetId, out var b) ? b.Keys : null;

    /// <summary>What <paramref name="keys"/> trigger, or null when no snippet answers to exactly them.</summary>
    public SnippetBinding? Find(Shortcut keys) => _byKeys.GetValueOrDefault(keys);

    /// <summary>True when <paramref name="stroke"/> is the first press of some bound chord.</summary>
    public bool StartsChord(KeyStroke stroke) => _chordStarts.Contains(stroke);
}

/// <summary>What a key press came to, as far as the snippet shortcuts are concerned.</summary>
public enum ChordOutcomeKind
{
    /// <summary>Not a shortcut, nor the start of one: the key belongs to whatever has focus.</summary>
    Unbound,

    /// <summary>The first press of a chord: Klippy is waiting for the second.</summary>
    Waiting,

    /// <summary>A modifier going down while a chord waits — the start of the second press, not a press.</summary>
    StillWaiting,

    /// <summary>A shortcut, single or completed chord: trigger <see cref="ChordOutcome.Binding"/>.</summary>
    Matched,

    /// <summary>The second press of a chord that leads nowhere — <see cref="ChordOutcome.Keys"/> are both presses.</summary>
    Missed,

    /// <summary><c>Esc</c> after the first press of a chord: called off.</summary>
    Cancelled,
}

/// <summary>The result of one <see cref="ChordMatcher.Press"/>: what happened, the keys it was about, and for a
/// match which snippet.</summary>
public readonly record struct ChordOutcome(ChordOutcomeKind Kind, Shortcut? Keys = null, SnippetBinding? Binding = null);

/// <summary>
/// Turns key presses into snippet shortcuts, two-press chords included: <c>Ctrl+K</c> on its own may do
/// nothing yet but wait, and <c>Ctrl+K</c> then <c>Ctrl+L</c> triggers the snippet bound to the pair.
///
/// Just the state machine — it knows nothing of windows or focus. As in VS Code, a waiting chord waits as
/// long as it takes; the window calls it off when the keyboard or the pointer goes elsewhere, and <c>Esc</c>
/// calls it off from the keyboard.
/// </summary>
public sealed class ChordMatcher
{
    private SnippetShortcutMap _map;

    public ChordMatcher(SnippetShortcutMap map) => _map = map;

    /// <summary>The shortcuts in force. Replacing them calls off a chord in progress.</summary>
    public SnippetShortcutMap Map
    {
        get => _map;
        set
        {
            _map = value;
            Pending = null;
        }
    }

    /// <summary>The first press of a chord still waiting for its second, or null.</summary>
    public KeyStroke? Pending { get; private set; }

    public bool IsPending => Pending is not null;

    /// <summary>Takes one key press, and says what it came to.</summary>
    public ChordOutcome Press(KeyStroke stroke)
    {
        if (Pending is { } first)
        {
            if (stroke.IsModifierKey) return new ChordOutcome(ChordOutcomeKind.StillWaiting, new Shortcut(first));

            Pending = null;
            if (stroke.IsEscape) return new ChordOutcome(ChordOutcomeKind.Cancelled, new Shortcut(first));

            var chord = new Shortcut(first, stroke);
            return _map.Find(chord) is { } binding
                ? new ChordOutcome(ChordOutcomeKind.Matched, chord, binding)
                : new ChordOutcome(ChordOutcomeKind.Missed, chord);
        }

        if (stroke.IsModifierKey) return new ChordOutcome(ChordOutcomeKind.Unbound);

        var single = new Shortcut(stroke);
        if (_map.Find(single) is { } direct) return new ChordOutcome(ChordOutcomeKind.Matched, single, direct);
        if (!_map.StartsChord(stroke)) return new ChordOutcome(ChordOutcomeKind.Unbound);

        Pending = stroke;
        return new ChordOutcome(ChordOutcomeKind.Waiting, single);
    }

    /// <summary>Calls off a chord in progress, if there is one. True when there was.</summary>
    public bool Reset()
    {
        var was = IsPending;
        Pending = null;
        return was;
    }
}

/// <summary>
/// The system-wide hotkeys the snippets ask for (<see cref="Snippet.Hotkey"/>), worked out before anything is
/// registered: each one parsed, any that is one of Klippy's own summon keys left out, and a combination two
/// snippets both carry given to the first — so a clash in the file reads as the clash it is rather than as
/// "another application holds it".
/// </summary>
public static class SnippetHotkeys
{
    /// <summary>The (snippet, hotkey) pairs to register, in file order.</summary>
    public static IReadOnlyList<(Guid SnippetId, HotkeySpec Spec)> Plan(
        IEnumerable<Snippet> snippets, IEnumerable<HotkeySpec?> summonKeys)
    {
        var taken = new HashSet<HotkeySpec>(summonKeys.OfType<HotkeySpec>());
        var plan = new List<(Guid, HotkeySpec)>();
        foreach (var snippet in snippets)
            if (TryRead(snippet.Hotkey, out var spec) && taken.Add(spec))
                plan.Add((snippet.Id, spec));
        return plan;
    }

    /// <summary>
    /// Reads a snippet's system-wide hotkey: it parses, and holds something besides Shift — Shift alone is
    /// capitals, and would take them from every application.
    /// </summary>
    public static bool TryRead(string? text, out HotkeySpec spec)
    {
        if (string.IsNullOrWhiteSpace(text) || !HotkeySpec.TryParse(text, out spec))
        {
            spec = HotkeySpec.Default;
            return false;
        }
        return (spec.Modifiers & ~HotkeyModifiers.Shift) != HotkeyModifiers.None;
    }

    /// <summary>
    /// The key press a hotkey stands for — what the OS handed Klippy instead of the press itself, when the
    /// combination is registered. Read back into a recording key field, so a combination Klippy already holds
    /// can still be recorded rather than firing.
    /// </summary>
    public static KeyStroke ToStroke(HotkeySpec spec)
    {
        var mods = KeyModifiers.None;
        if (spec.Modifiers.HasFlag(HotkeyModifiers.Control)) mods |= KeyModifiers.Control;
        if (spec.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= KeyModifiers.Alt;
        if (spec.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= KeyModifiers.Shift;
        if (spec.Modifiers.HasFlag(HotkeyModifiers.Meta)) mods |= KeyModifiers.Meta;
        return KeyStroke.From(KeyFor(spec.Key), mods);
    }

    /// <summary>
    /// The system-wide hotkey for a key press, when it can be one: a letter, a digit, Space, F1–F20 or a
    /// punctuation key, held with at least one modifier — the keys every platform Klippy registers on can
    /// take (<see cref="HotkeySpec.VirtualKeys"/>).
    /// </summary>
    public static bool TryFromStroke(KeyStroke stroke, out HotkeySpec spec)
    {
        spec = HotkeySpec.Default;
        if (NameFor(stroke.Key) is not { } key || !HotkeySpec.VirtualKeys.ContainsKey(key)) return false;

        var mods = HotkeyModifiers.None;
        if (stroke.Modifiers.HasFlag(KeyModifiers.Control)) mods |= HotkeyModifiers.Control;
        if (stroke.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= HotkeyModifiers.Alt;
        if (stroke.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= HotkeyModifiers.Shift;
        if (stroke.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= HotkeyModifiers.Meta;

        // Shift alone is typing — capitals — and would swallow them system-wide.
        if ((mods & ~HotkeyModifiers.Shift) == HotkeyModifiers.None) return false;

        spec = new HotkeySpec(mods, key);
        return true;
    }

    /// <summary>
    /// The punctuation keys, by the Avalonia key and the character <see cref="HotkeySpec"/> writes for it.
    /// Avalonia reads these through the same layout mapping the OS registers them with (VK_OEM_2 is
    /// <see cref="Key.OemQuestion"/> either way), so what is recorded is what gets registered.
    /// </summary>
    private static readonly (Key Key, string Name)[] Punctuation =
    [
        (Key.OemQuestion, "/"), (Key.OemSemicolon, ";"), (Key.OemQuotes, "'"), (Key.OemComma, ","),
        (Key.OemPeriod, "."), (Key.OemMinus, "-"), (Key.OemPlus, "="), (Key.OemOpenBrackets, "["),
        (Key.OemCloseBrackets, "]"), (Key.OemPipe, "\\"), (Key.OemTilde, "`"),
    ];

    /// <summary><see cref="HotkeySpec"/>'s name for <paramref name="key"/>, or null for one it has no name for.</summary>
    private static string? NameFor(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
        Key.Space => "SPACE",
        _ => Punctuation.FirstOrDefault(p => p.Key == key).Name,
    };

    /// <summary>The Avalonia key <see cref="HotkeySpec"/>'s <paramref name="name"/> stands for, or <see cref="Key.None"/>.</summary>
    private static Key KeyFor(string name) => name switch
    {
        "SPACE" => Key.Space,
        [var c] when c is >= 'A' and <= 'Z' => Key.A + (c - 'A'),
        [var c] when c is >= '0' and <= '9' => Key.D0 + (c - '0'),
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => Key.F1 + (f - 1),
        _ => Punctuation.FirstOrDefault(p => p.Name == name).Key,
    };
}
