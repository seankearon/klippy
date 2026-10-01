using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The editor's two key fields: a <em>shortcut</em> that triggers the snippet while Klippy is up — one press,
/// or a two-press chord such as <c>Ctrl+K, Ctrl+L</c> — and a system-wide <em>hotkey</em> that triggers it
/// from any application.
///
/// Both are set by recording. Click a field and press the keys. For the shortcut, the first press is held:
/// a second makes it a chord and is taken there and then, and Enter keeps the single press. The hotkey is one
/// press, taken at once. Esc, or clicking away, leaves either as it was. Keys that could never fire — plain
/// typing, one of Klippy's own keys, a summon key — are refused with a line saying why. Keys another snippet
/// has are taken, and the line says from whom, before Save makes it so.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>The shortcut as the snippet will store it, e.g. <c>Ctrl+K, Ctrl+L</c>; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutText), nameof(HasShortcut), nameof(ShortcutHint), nameof(ShortcutHintIsWarning))]
    private string _shortcut = "";

    /// <summary>The system-wide hotkey as the snippet will store it, e.g. <c>Ctrl+Alt+1</c>; empty for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyText), nameof(HasHotkey), nameof(HotkeyHint), nameof(HotkeyHintIsWarning))]
    private string _hotkey = "";

    /// <summary>Which field is listening for keys, if either.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsRecordingShortcut), nameof(IsRecordingHotkey),
        nameof(ShortcutText), nameof(HotkeyText), nameof(ShortcutHint), nameof(ShortcutHintIsWarning),
        nameof(HotkeyHint), nameof(HotkeyHintIsWarning))]
    private KeyField _recording;

    private KeyStroke? _recordedFirst;
    private string? _shortcutRefusal;
    private string? _hotkeyRefusal;

    /// <summary>Shortcuts are a keyboard thing: offered wherever there is one to press them on.</summary>
    public bool ShowsShortcuts { get; } = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>The hotkey field, only where the head registers system-wide hotkeys.</summary>
    public bool ShowsHotkey => ShowsShortcuts && _keys.CanRegisterHotkeys;

    public bool IsRecording => Recording != KeyField.None;
    public bool IsRecordingShortcut => Recording == KeyField.Shortcut;
    public bool IsRecordingHotkey => Recording == KeyField.Hotkey;

    public bool HasShortcut => Shortcut.Length > 0;
    public bool HasHotkey => Hotkey.Length > 0;

    private static string Meta => KeyStroke.PlatformMetaName;

    /// <summary>What the shortcut field shows: the keys, or what recording has heard so far.</summary>
    public string ShortcutText =>
        IsRecordingShortcut
            ? _recordedFirst is { } first ? $"{first.DisplayText}, …" : "press keys…"
            : Services.Shortcut.TryParse(Shortcut, out var keys) ? keys.DisplayText : "";

    /// <summary>What the hotkey field shows.</summary>
    public string HotkeyText =>
        IsRecordingHotkey ? "press keys…"
        : SnippetHotkeys.TryRead(Hotkey, out var spec) ? spec.ToString() : "";

    /// <summary>The line under the shortcut field.</summary>
    public string ShortcutHint
    {
        get
        {
            if (IsRecordingShortcut)
                return _recordedFirst is { } first
                    ? $"{first.DisplayText} — press another key to make it a chord, or Enter to keep it as it is. Esc cancels."
                    : "Press the keys: one, or two in a row for a chord. Esc cancels.";
            if (_shortcutRefusal is { } refusal) return refusal;
            if (!Services.Shortcut.TryParse(Shortcut, out var keys))
                return "Keys that trigger this snippet while Klippy is up — e.g. Ctrl+K, Ctrl+L";
            if (ShortcutOwner(keys) is { } other)
                return $"{keys.DisplayText} is “{other.Label}”’s — saving moves it here.";
            return $"{keys.DisplayText} triggers this snippet while Klippy is up, as Enter on its row does.";
        }
    }

    public bool ShortcutHintIsWarning => !IsRecordingShortcut && _shortcutRefusal is not null;

    /// <summary>The line under the hotkey field.</summary>
    public string HotkeyHint
    {
        get
        {
            if (IsRecordingHotkey)
                return $"Press a letter, digit, punctuation key, Space or F1–F20 with Ctrl, Alt or {Meta} held. " +
                       "Esc cancels.";
            if (_hotkeyRefusal is { } refusal) return refusal;
            if (!SnippetHotkeys.TryRead(Hotkey, out var spec))
                return "A hotkey that triggers this snippet from any application — e.g. Ctrl+Alt+1";
            if (HotkeyIsUnclaimed)
                return $"Another application holds {spec}, so it isn't triggering this snippet — pick another.";
            if (HotkeyOwner(spec) is { } other)
                return $"{spec} is “{other.Label}”’s — saving moves it here.";
            return $"{spec} triggers this snippet from any application, without opening Klippy.";
        }
    }

    public bool HotkeyHintIsWarning => !IsRecordingHotkey && (_hotkeyRefusal is not null || HotkeyIsUnclaimed);

    /// <summary>The launcher could not claim the hotkey the snippet already had, and it hasn't been changed since.</summary>
    private bool HotkeyIsUnclaimed =>
        _keys.HotkeyUnclaimed && Hotkey.Length > 0 && string.Equals(Hotkey, _originalHotkey, StringComparison.Ordinal);

    /// <summary>
    /// What the keys will do for a snippet that takes an argument — a <c>%P%</c> — which they can't fill:
    /// bring Klippy up with the quick-code typed, waiting for it; or, with no quick-code to type it after,
    /// only say so. Empty for a snippet with no keys, or no argument to take.
    /// </summary>
    public string ArgumentsHint
    {
        get
        {
            if (!ShowsShortcuts || (!HasShortcut && !HasHotkey)) return "";
            if (!Macros.TakesArguments(KlippyVariables.Current.Expand(Content))) return "";

            var code = QuickCode.Trim().ToLowerInvariant();
            return code.Length > 0
                ? $"It takes an argument, so its keys bring Klippy up with \u201c{code} \u201d typed — type the " +
                  "argument, then Enter."
                : "It takes an argument, which is typed after a quick-code — give it one, or its keys can only " +
                  "say so.";
        }
    }

    public bool HasArgumentsHint => ArgumentsHint.Length > 0;

    /// <summary>An argument with no quick-code to type it after: the keys can't do their job.</summary>
    public bool ArgumentsHintIsWarning => HasArgumentsHint && QuickCode.Trim().Length == 0;

    partial void OnContentChanged(string value) => NotifyArgumentsHint();
    partial void OnQuickCodeChanged(string value) => NotifyArgumentsHint();
    partial void OnShortcutChanged(string value) => NotifyArgumentsHint();
    partial void OnHotkeyChanged(string value) => NotifyArgumentsHint();

    private void NotifyArgumentsHint()
    {
        OnPropertyChanged(nameof(ArgumentsHint));
        OnPropertyChanged(nameof(HasArgumentsHint));
        OnPropertyChanged(nameof(ArgumentsHintIsWarning));
    }

    [RelayCommand]
    private void RecordShortcut() => BeginRecording(KeyField.Shortcut);

    [RelayCommand]
    private void RecordHotkey() => BeginRecording(KeyField.Hotkey);

    [RelayCommand]
    private void ClearShortcut()
    {
        EndRecording();
        _shortcutRefusal = null;
        Shortcut = "";
        OnPropertyChanged(nameof(ShortcutHint));
        OnPropertyChanged(nameof(ShortcutHintIsWarning));
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        EndRecording();
        _hotkeyRefusal = null;
        Hotkey = "";
        OnPropertyChanged(nameof(HotkeyHint));
        OnPropertyChanged(nameof(HotkeyHintIsWarning));
    }

    /// <summary>Starts listening on <paramref name="field"/>; one field at a time.</summary>
    public void BeginRecording(KeyField field)
    {
        _recordedFirst = null;
        if (field == KeyField.Shortcut) _shortcutRefusal = null;
        if (field == KeyField.Hotkey) _hotkeyRefusal = null;
        Recording = KeyField.None; // re-raises everything even when restarting the same field
        Recording = field;
    }

    /// <summary>Stops listening, leaving the field as it was.</summary>
    public void CancelRecording() => EndRecording();

    private void EndRecording()
    {
        _recordedFirst = null;
        Recording = KeyField.None;
    }

    /// <summary>A key pressed while a field is listening.</summary>
    public void Press(KeyStroke stroke)
    {
        if (!IsRecording || stroke.IsModifierKey) return;

        if (stroke.IsEscape)
        {
            EndRecording();
            return;
        }

        if (IsRecordingHotkey)
            PressHotkey(stroke);
        else
            PressShortcut(stroke);
    }

    private void PressShortcut(KeyStroke stroke)
    {
        if (_recordedFirst is not { } first)
        {
            if (RefuseFirstPress(stroke) is { } refusal)
            {
                _shortcutRefusal = refusal;
                EndRecording();
                return;
            }

            _recordedFirst = stroke;
            OnPropertyChanged(nameof(ShortcutText));
            OnPropertyChanged(nameof(ShortcutHint));
            return;
        }

        var keys = stroke.IsEnter ? new Services.Shortcut(first) : new Services.Shortcut(first, stroke);
        EndRecording();
        Shortcut = keys.ToString();
    }

    private void PressHotkey(KeyStroke stroke)
    {
        EndRecording();

        if (!SnippetHotkeys.TryFromStroke(stroke, out var spec))
        {
            _hotkeyRefusal = $"{stroke.DisplayText} can't be a system-wide hotkey: hold Ctrl, Alt or {Meta} " +
                             "with a letter, a digit, a punctuation key, Space or F1–F20.";
            OnPropertyChanged(nameof(HotkeyHint));
            OnPropertyChanged(nameof(HotkeyHintIsWarning));
            return;
        }

        var refusal = SummonKeyName(spec) is { } summons
            ? $"{spec} summons Klippy's {summons} — try another."
            // A hotkey is the OS's before it is the window's, so one where a shortcut starts would silently
            // stop that shortcut working in Klippy.
            : StartsAShortcut(spec) is { } shadowed
                ? $"{spec} starts {shadowed}'s shortcut in Klippy, which would stop working — try another."
                : null;
        if (refusal is not null)
        {
            _hotkeyRefusal = refusal;
            OnPropertyChanged(nameof(HotkeyHint));
            OnPropertyChanged(nameof(HotkeyHintIsWarning));
            return;
        }

        Hotkey = spec.ToString();
    }

    /// <summary>
    /// Why <paramref name="stroke"/> can't start a shortcut, or null when it can: it would be typing, the system
    /// or Klippy already answers to it, or a system-wide hotkey would take it before the window saw it.
    /// </summary>
    private string? RefuseFirstPress(KeyStroke stroke)
    {
        if (stroke.IsReserved) return $"{stroke.DisplayText} belongs to the system — try another.";
        if (!stroke.CanBegin)
            return $"A shortcut can't start with plain {stroke.DisplayText} — that would be typing. Hold Ctrl, " +
                   $"Alt or {Meta} for the first key, or use a function key.";
        if (KlippyKeys.OwnerOf(stroke) is { } owner)
            return $"Klippy already uses {stroke.DisplayText} ({owner}) — try another.";
        if (SnippetHotkeys.TryFromStroke(stroke, out var asHotkey))
        {
            if (SummonKeyName(asHotkey) is { } summons)
                return $"{stroke.DisplayText} summons Klippy's {summons} — try another.";
            if (_keys.CanRegisterHotkeys && HotkeyOwner(asHotkey) is { } other)
                return $"{stroke.DisplayText} is “{other.Label}”’s system-wide hotkey — try another.";
            if (_keys.CanRegisterHotkeys && SnippetHotkeys.TryRead(Hotkey, out var mine) && mine == asHotkey)
                return $"{stroke.DisplayText} is this snippet's system-wide hotkey already.";
        }
        return null;
    }

    /// <summary>
    /// Whose in-Klippy shortcut <paramref name="spec"/> is the first press of — "this snippet" or another's name
    /// in quotes — or null when it starts none.
    /// </summary>
    private string? StartsAShortcut(HotkeySpec spec)
    {
        bool Starts(string? text) => Services.Shortcut.TryParse(text, out var keys)
                                     && SnippetHotkeys.TryFromStroke(keys.First, out var first) && first == spec;

        if (Starts(Shortcut)) return "this snippet";
        return _keys.Others.FirstOrDefault(other => Starts(other.Shortcut)) is { } owner
            ? $"\u201c{owner.Label}\u201d"
            : null;
    }

    private string? SummonKeyName(HotkeySpec spec) =>
        spec == _keys.SnippetsKey ? "snippets"
        : spec == _keys.HistoryKey ? "clipboard history"
        : null;

    /// <summary>The other snippet whose in-Klippy keys clash with <paramref name="keys"/>, if any.</summary>
    private Snippet? ShortcutOwner(Services.Shortcut keys) =>
        _keys.Others.FirstOrDefault(other =>
            Services.Shortcut.TryParse(other.Shortcut, out var theirs) && theirs.CollidesWith(keys));

    /// <summary>The other snippet with <paramref name="spec"/> as its system-wide hotkey, if any.</summary>
    private Snippet? HotkeyOwner(HotkeySpec spec) =>
        _keys.Others.FirstOrDefault(other => SnippetHotkeys.TryRead(other.Hotkey, out var theirs) && theirs == spec);
}
