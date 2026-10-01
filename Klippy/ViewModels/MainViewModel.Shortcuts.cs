using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>What triggering a snippet by its keys came to.</summary>
public enum TriggerOutcome
{
    /// <summary>Copied, or run.</summary>
    Done,

    /// <summary>Nothing happened, and the toast says why — the window should come up so it can be read.</summary>
    Failed,

    /// <summary>
    /// The snippet takes an argument, so its quick-code is in the search box with the caret after it, waiting
    /// for the argument and Enter — the window should come up, ready for typing.
    /// </summary>
    AwaitingArguments,
}

/// <summary>
/// Snippet shortcuts and system-wide hotkeys: a snippet can carry keys that trigger it while Klippy is up —
/// a chord such as <c>Ctrl+K, Ctrl+L</c> included — and a hotkey that triggers it from any application.
/// Either way, triggering does what Enter on its row does: a copy, or a run for one marked Execute.
/// </summary>
public partial class MainViewModel
{
    /// <summary>How long the chord pill says a chord led nowhere before it clears itself.</summary>
    public static readonly TimeSpan ChordMissNotice = TimeSpan.FromSeconds(2.5);

    private SnippetShortcutMap _shortcuts = SnippetShortcutMap.Empty;
    private readonly ChordMatcher _chords = new(SnippetShortcutMap.Empty);
    private readonly HashSet<Guid> _unclaimedHotkeys = new();
    private DispatcherTimer? _chordMissTimer;

    /// <summary>
    /// Raised when a snippet's keys have put its quick-code in the search box to wait for an argument: the
    /// view hands the box the keyboard with the caret at the end, ready for typing.
    /// </summary>
    public event Action? ArgumentsRequested;

    /// <summary>
    /// Raised after snippets were saved, deleted or imported — anything that can change which keys trigger
    /// what. The desktop head re-registers the system-wide hotkeys on it.
    /// </summary>
    public event Action? SnippetsChanged;

    /// <summary>
    /// Whether this head registers system-wide hotkeys, so the editor offers a snippet one. Set by the desktop
    /// launcher once it is running with hotkeys on; false on mobile, and in a plain window with no launcher.
    /// </summary>
    [ObservableProperty]
    private bool _canRegisterHotkeys;

    /// <summary>The in-Klippy shortcuts in force.</summary>
    public SnippetShortcutMap Shortcuts => _shortcuts;

    /// <summary>
    /// The chord pill's text: the first press of a two-press shortcut, waiting for the second — or, briefly, a
    /// second press that led nowhere. Empty hides the pill.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChordStatus))]
    private string _chordStatus = "";

    public bool HasChordStatus => ChordStatus.Length > 0;

    /// <summary>True while the pill reports a chord that isn't a shortcut, rather than one in progress.</summary>
    [ObservableProperty]
    private bool _isChordMiss;

    /// <summary>True while the first press of a chord waits for its second.</summary>
    public bool IsChordPending => _chords.IsPending;

    partial void OnCanRegisterHotkeysChanged(bool value) => RebuildShortcuts();

    /// <summary>
    /// A key press on the main window, offered to the snippet shortcuts. A match triggers its snippet; the first
    /// press of a chord raises the pill and waits; a second press that finishes nothing says so. The window
    /// decides which presses to offer (see <c>MainWindow</c>) — this only says what they came to.
    /// </summary>
    public ChordOutcome PressShortcutKey(KeyStroke stroke)
    {
        var outcome = _chords.Press(stroke);
        switch (outcome)
        {
            case { Kind: ChordOutcomeKind.Waiting, Keys: { } first }:
                _chordMissTimer?.Stop();
                IsChordMiss = false;
                ChordStatus = $"{first.DisplayText} — waiting for the second key… (Esc cancels)";
                break;
            case { Kind: ChordOutcomeKind.Matched, Binding: { } binding }:
                ClearChordStatus();
                _ = TriggerSnippetAsync(binding.SnippetId);
                break;
            case { Kind: ChordOutcomeKind.Missed, Keys: { } keys }:
                IsChordMiss = true;
                ChordStatus = $"{keys.DisplayText} isn't a shortcut";
                StartChordMissTimer();
                break;
            case { Kind: ChordOutcomeKind.Cancelled }:
                ClearChordStatus();
                break;
        }
        OnPropertyChanged(nameof(IsChordPending));
        return outcome;
    }

    /// <summary>Calls off a chord waiting for its second press, pill and all.</summary>
    public void CancelChord()
    {
        if (!_chords.Reset()) return;
        ClearChordStatus();
        OnPropertyChanged(nameof(IsChordPending));
    }

    private void ClearChordStatus()
    {
        _chordMissTimer?.Stop();
        ChordStatus = "";
        IsChordMiss = false;
    }

    private void StartChordMissTimer()
    {
        if (_chordMissTimer is null)
        {
            _chordMissTimer = new DispatcherTimer { Interval = ChordMissNotice };
            _chordMissTimer.Tick += (_, _) =>
            {
                _chordMissTimer.Stop();
                if (IsChordMiss) ClearChordStatus();
            };
        }
        _chordMissTimer.Stop();
        _chordMissTimer.Start();
    }

    /// <summary>
    /// Triggers the snippet with <paramref name="id"/> as Enter on its row would — a copy, or a run for one
    /// marked Execute — wherever the list happens to be: in the clipboard history, or filtered down to
    /// something else entirely. Reached from a shortcut and from a system-wide hotkey. Nothing typed is
    /// involved, so nothing goes into the command MRU.
    ///
    /// A snippet that takes an argument — a <c>%P%</c> — can't be done on the spot: there is nothing to fill
    /// it with. Its quick-code goes into the search box instead, with a space after it and the caret at the
    /// end, which is the line someone typing it by hand would have got to; the argument and Enter are theirs
    /// to give (<see cref="TriggerOutcome.AwaitingArguments"/>).
    ///
    /// A snippet that uses the clipboard — a <c>%C%</c> — goes ahead when the clipboard holds text, which is
    /// the point of pressing its keys: search for what was just copied. When it holds none — nothing, a
    /// picture, files — it does nothing and says so, rather than search for nothing; Enter on the row is the
    /// way to have it with an empty <c>%C%</c>, for whoever wants that.
    /// </summary>
    public async Task<TriggerOutcome> TriggerSnippetAsync(Guid id)
    {
        if (_store.Find(id) is not { } snippet) return TriggerOutcome.Failed;

        // The variables file first, as copy and run apply it: a define can be what brings the %P%.
        var expanded = KlippyVariables.Current.Expand(snippet.Content);
        if (Macros.TakesArguments(expanded))
            return PromptForArguments(snippet);

        if (Macros.UsesClipboard(expanded) && !await ClipboardHasTextAsync())
        {
            ShowToast($"\u201c{snippet.Label}\u201d uses the clipboard, and there's no text on it — copy some, " +
                      "then press its keys again.", isError: true);
            return TriggerOutcome.Failed;
        }

        // A fresh row rather than the list's own: the list's may be carrying arguments typed after its
        // quick-code, and a shortcut names the snippet, not that line.
        var row = new SnippetViewModel(snippet);

        // Only a system-wide hotkey gets here with an overlay up — the in-window shortcuts stand aside for one.
        // It still does its job, but leaves the window be: the keyboard stays in the editor, where a snippet
        // copied by hotkey is as likely as not about to be pasted, and nothing closes mid-edit.
        var quiet = IsOverlayOpen;
        var done = snippet.IsExecutable
            ? await ExecuteCore(row, record: false, quiet)
            : await CopyCore(row, record: false, quiet);
        return done ? TriggerOutcome.Done : TriggerOutcome.Failed;
    }

    /// <summary>
    /// Whether the clipboard holds text for a <c>%C%</c> — not nothing, not a picture or files, not just
    /// whitespace. Read here and again when the snippet resolves: a second read is cheap, and threading the
    /// first one through copy and run would buy nothing but a moment's difference.
    /// </summary>
    private async Task<bool> ClipboardHasTextAsync() =>
        ClipboardReader is { } read && !string.IsNullOrWhiteSpace(await read());

    /// <summary>
    /// Puts <paramref name="snippet"/>'s quick-code in the search box, ready for its argument — or, where it
    /// can't, says why. Arguments are typed after a quick-code, so a snippet without one has nowhere to take
    /// them; and an overlay up (a hotkey pressed from the editor) is not torn down for it.
    /// </summary>
    private TriggerOutcome PromptForArguments(Snippet snippet)
    {
        if (IsOverlayOpen)
        {
            ShowToast($"\u201c{snippet.Label}\u201d takes an argument — close this first, then press its keys again.",
                isError: true);
            return TriggerOutcome.Failed;
        }

        if (snippet.QuickCode.Length == 0)
        {
            ShowToast($"\u201c{snippet.Label}\u201d takes an argument, which is typed after a quick-code — " +
                      "give it one in the editor.", isError: true);
            return TriggerOutcome.Failed;
        }

        if (IsHistoryMode) ShowSnippets();
        CloseCommands(restore: false);
        FilterText = snippet.QuickCode + " ";
        ArgumentsRequested?.Invoke();
        return TriggerOutcome.AwaitingArguments;
    }

    /// <summary>Whether an overlay — the editor, a confirmation, export/import, settings — is up.</summary>
    private bool IsOverlayOpen =>
        Editor is not null || DeleteTarget is not null || Transfer is not null || Settings is not null
        || PendingOffer is not null;

    /// <summary>
    /// A registered system-wide hotkey was pressed while one of the editor's key fields is listening — so the
    /// OS handed it to Klippy instead of to the field. Gives it to the field, as the press it was. True when
    /// the editor took it, and the hotkey should do nothing else.
    /// </summary>
    public bool OfferHotkeyToRecorder(HotkeySpec spec)
    {
        if (Editor is not { IsRecording: true } editor) return false;
        editor.Press(SnippetHotkeys.ToStroke(spec));
        return true;
    }

    /// <summary>The system-wide hotkeys to register: one per snippet that asks for one, clashes settled.</summary>
    public IReadOnlyList<(Guid SnippetId, HotkeySpec Spec)> PlannedHotkeys() =>
        SnippetHotkeys.Plan(_store.Snippets, SummonKeys);

    /// <summary>Klippy's own two summon keys, which no snippet may take.</summary>
    private IEnumerable<HotkeySpec?> SummonKeys => [_prefs.ParsedHotkey, _prefs.ParsedHistoryHotkey];

    /// <summary>
    /// The launcher's answer to <see cref="PlannedHotkeys"/>: the snippets whose hotkey it could not claim,
    /// because another application holds the combination. The editor says so for those.
    /// </summary>
    public void ReportUnclaimedHotkeys(IEnumerable<Guid> snippetIds)
    {
        _unclaimedHotkeys.Clear();
        _unclaimedHotkeys.UnionWith(snippetIds);
    }

    /// <summary>Whether the snippet's hotkey is one the launcher tried, and failed, to claim.</summary>
    public bool IsHotkeyUnclaimed(Guid snippetId) => _unclaimedHotkeys.Contains(snippetId);

    private void OnSnippetsChanged()
    {
        RebuildShortcuts();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Rebuilds the in-Klippy shortcuts. The system-wide keys are left out of them: the OS hands a press on
    /// one of those to Klippy's hotkey before the window ever sees it.
    /// </summary>
    private void RebuildShortcuts()
    {
        var globals = new HashSet<HotkeySpec>(SummonKeys.OfType<HotkeySpec>());
        if (CanRegisterHotkeys)
            globals.UnionWith(PlannedHotkeys().Select(p => p.Spec));

        _shortcuts = SnippetShortcutMap.Build(_store.Snippets, globals);
        _chords.Map = _shortcuts;
        ClearChordStatus();
        OnPropertyChanged(nameof(IsChordPending));
    }

    /// <summary>What the editor needs to say about keys: who else has which, and what Klippy keeps for itself.</summary>
    private SnippetKeysContext KeysContextFor(Snippet? existing) => new(
        Others: _store.Snippets.Where(s => existing is null || s.Id != existing.Id).ToList(),
        SnippetsKey: _prefs.ParsedHotkey,
        HistoryKey: _prefs.ParsedHistoryHotkey,
        CanRegisterHotkeys: CanRegisterHotkeys,
        HotkeyUnclaimed: existing is not null && IsHotkeyUnclaimed(existing.Id));

    /// <summary>
    /// Takes the saved snippet's keys from any other snippet that had them — the same in-Klippy keys, or one
    /// where the other's chord starts, and the same system-wide hotkey. The editor named them before Save.
    /// </summary>
    private void TakeKeysFromOthers(Snippet saved)
    {
        var hasShortcut = Shortcut.TryParse(saved.Shortcut, out var mine);
        var hasHotkey = SnippetHotkeys.TryRead(saved.Hotkey, out var myHotkey);
        if (!hasShortcut && !hasHotkey) return;

        foreach (var other in _store.Snippets.ToList())
        {
            if (other.Id == saved.Id) continue;

            var changed = false;
            if (hasShortcut && Shortcut.TryParse(other.Shortcut, out var theirs) && theirs.CollidesWith(mine))
            {
                other.Shortcut = "";
                changed = true;
            }
            if (hasHotkey && SnippetHotkeys.TryRead(other.Hotkey, out var theirHotkey) && theirHotkey == myHotkey)
            {
                other.Hotkey = "";
                changed = true;
            }
            if (!changed) continue;

            _store.Update(other);
            if (_rowCache.TryGetValue(other.Id, out var row)) row.NotifyModelChanged();
        }
    }

    /// <summary>
    /// After a save, says so when the snippet's hotkey could not be claimed — the launcher re-registered on
    /// <see cref="SnippetsChanged"/>, so the answer is already in.
    /// </summary>
    private void ReportUnclaimedHotkey(Snippet saved)
    {
        if (CanRegisterHotkeys && IsHotkeyUnclaimed(saved.Id) && SnippetHotkeys.TryRead(saved.Hotkey, out var spec))
            ShowToast($"Another application holds {spec}, so it won't trigger \"{saved.Label}\" — pick another.",
                isError: true);
    }
}
