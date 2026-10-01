using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The rules behind snippet shortcuts, with no window: how keys are written and read, which keys Klippy keeps
/// for itself, how a clash between snippets is settled, the chord state machine, and which system-wide
/// hotkeys get registered.
/// </summary>
public class SnippetShortcutTests
{
    private static Shortcut Keys(string text) =>
        Shortcut.TryParse(text, out var keys) ? keys : throw new ArgumentException($"Not a shortcut: {text}");

    private static Snippet WithKeys(string label, string shortcut = "", string hotkey = "") =>
        new() { Label = label, Content = label, Shortcut = shortcut, Hotkey = hotkey };

    // ---- writing and reading keys ----

    [Theory]
    [InlineData("Ctrl+K, Ctrl+L")]
    [InlineData("ctrl+k ctrl+l")]          // as VS Code writes it
    [InlineData("Ctrl + K, Ctrl + L")]     // as people write it
    [InlineData("Ctrl+K,Ctrl+L")]
    public void AChord_ReadsInEveryUsualNotation(string text)
    {
        Assert.True(Shortcut.TryParse(text, out var chord));
        Assert.Equal(new Shortcut(KeyStroke.Ctrl(Key.K), KeyStroke.Ctrl(Key.L)), chord);
        Assert.Equal("Ctrl+K, Ctrl+L", chord.ToString());
    }

    [Theory]
    [InlineData("Ctrl+K, T")]
    [InlineData("Ctrl+,, Ctrl+L")]         // the comma key, then the gap
    [InlineData("Ctrl+K, Comma")]
    [InlineData("Shift+Alt+F12")]
    [InlineData("Cmd+Shift+P")]
    public void EveryShortcut_RoundTripsThroughItsText(string text)
    {
        Assert.True(Shortcut.TryParse(text, out var first));
        Assert.True(Shortcut.TryParse(first.ToString(), out var again));
        Assert.Equal(first, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+K, Ctrl+L, Ctrl+M")]  // two presses is the limit
    [InlineData("Ctrl+Nope")]
    public void BlankOrUnreadable_IsNotAShortcut(string? text) => Assert.False(Shortcut.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+K", true)]
    [InlineData("F6", true)]                // types nothing, so it can stand alone
    [InlineData("K", false)]                // typing
    [InlineData("Shift+K", false)]          // typing, in capitals
    [InlineData("Alt+F4", false)]           // the system's
    [InlineData("Ctrl+K, Esc", false)]      // Esc calls a chord off; it can't finish one
    public void AShortcut_IsValidWhenItCanBePressed(string text, bool valid) =>
        Assert.Equal(valid, Keys(text).IsValid);

    [Theory]
    [InlineData("Ctrl+K", "Ctrl+K", true)]
    [InlineData("Ctrl+K", "Ctrl+K, Ctrl+L", true)]          // one is where the other starts
    [InlineData("Ctrl+K, Ctrl+L", "Ctrl+K, Ctrl+M", false)] // sharing a first press is fine
    [InlineData("Ctrl+K, Ctrl+L", "Ctrl+L", false)]
    public void Shortcuts_CollideWhenBothCouldNotBePressed(string a, string b, bool collide) =>
        Assert.Equal(collide, Keys(a).CollidesWith(Keys(b)));

    // ---- Klippy's own keys ----

    [Theory]
    [InlineData(Key.N, KeyModifiers.Control, "New snippet")]
    [InlineData(Key.N, KeyModifiers.Control | KeyModifiers.Shift, "New snippet")]   // whatever else is held
    [InlineData(Key.OemComma, KeyModifiers.Control, "Settings")]
    [InlineData(Key.A, KeyModifiers.Control, "the search box")]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, "the search box")]
    [InlineData(Key.Left, KeyModifiers.Control, "the search box")]
    [InlineData(Key.F2, KeyModifiers.None, "Edit")]
    [InlineData(Key.Enter, KeyModifiers.Alt, "Copy")]
    public void KlippysOwnKeys_AreNamed(Key key, KeyModifiers modifiers, string owner) =>
        Assert.Equal(owner, KlippyKeys.OwnerOf(KeyStroke.From(key, modifiers), mac: false));

    [Theory]
    [InlineData(Key.K, KeyModifiers.Control)]
    [InlineData(Key.L, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Alt)]   // not exactly the search box's select-all
    [InlineData(Key.F6, KeyModifiers.None)]
    public void OtherKeys_AreFree(Key key, KeyModifiers modifiers) =>
        Assert.Null(KlippyKeys.OwnerOf(KeyStroke.From(key, modifiers), mac: false));

    [Fact]
    public void OnAMac_TheCommandKeyIsCmd_AndCtrlIsFree()
    {
        Assert.Equal("New snippet", KlippyKeys.OwnerOf(KeyStroke.From(Key.N, KeyModifiers.Meta), mac: true));
        Assert.Null(KlippyKeys.OwnerOf(KeyStroke.From(Key.N, KeyModifiers.Control), mac: true));
        Assert.Equal("macOS", KlippyKeys.OwnerOf(KeyStroke.From(Key.Q, KeyModifiers.Meta), mac: true));
    }

    // ---- the map ----

    [Fact]
    public void TheMap_BindsEachSnippetsShortcut()
    {
        var log = WithKeys("Send log files", "Ctrl+K, Ctrl+L");
        var mail = WithKeys("Work email", "Ctrl+Shift+M");

        var map = SnippetShortcutMap.Build([log, mail], mac: false);

        Assert.Equal(Keys("Ctrl+K, Ctrl+L"), map.For(log.Id));
        Assert.Equal(mail.Id, map.Find(Keys("Ctrl+Shift+M"))!.SnippetId);
        Assert.True(map.StartsChord(KeyStroke.Ctrl(Key.K)));
    }

    [Fact]
    public void TheMap_LeavesOutWhatCouldNeverFire()
    {
        var owned = WithKeys("Owned", "Ctrl+N, X");          // starts on New snippet
        var ownedToo = WithKeys("Owned too", "Ctrl+Shift+E"); // Export / import takes Ctrl+E however held
        var typing = WithKeys("Typing", "K");
        var garbage = WithKeys("Garbage", "Ctrl+Nonsense");
        var global = WithKeys("Global", "Ctrl+Alt+K");        // the summon key takes it first

        var map = SnippetShortcutMap.Build([owned, ownedToo, typing, garbage, global],
            globalKeys: [new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K")], mac: false);

        Assert.Empty(map.Bindings);
    }

    [Fact]
    public void AClashLeftInTheFile_GoesToTheFirstSnippet()
    {
        var first = WithKeys("First", "Ctrl+K");
        var chord = WithKeys("Chord", "Ctrl+K, Ctrl+L");   // can't be reached with Ctrl+K bound on its own
        var same = WithKeys("Same", "Ctrl+K");

        var map = SnippetShortcutMap.Build([first, chord, same], mac: false);

        Assert.Equal(first.Id, Assert.Single(map.Bindings).SnippetId);
        Assert.False(map.StartsChord(KeyStroke.Ctrl(Key.K)));
    }

    // ---- the chord state machine ----

    [Fact]
    public void AChord_WaitsAfterItsFirstPress_AndMatchesOnItsSecond()
    {
        var log = WithKeys("Send log files", "Ctrl+K, L");
        var matcher = new ChordMatcher(SnippetShortcutMap.Build([log], mac: false));

        Assert.Equal(ChordOutcomeKind.Waiting, matcher.Press(KeyStroke.Ctrl(Key.K)).Kind);
        Assert.Equal(ChordOutcomeKind.StillWaiting,
            matcher.Press(KeyStroke.From(Key.LeftShift, KeyModifiers.Shift)).Kind);

        var outcome = matcher.Press(KeyStroke.From(Key.L));
        Assert.Equal(ChordOutcomeKind.Matched, outcome.Kind);
        Assert.Equal(log.Id, outcome.Binding!.SnippetId);
        Assert.False(matcher.IsPending);
    }

    [Fact]
    public void ASecondPressThatLeadsNowhere_IsAMiss_AndEscCallsTheChordOff()
    {
        var matcher = new ChordMatcher(SnippetShortcutMap.Build([WithKeys("Log", "Ctrl+K, L")], mac: false));

        matcher.Press(KeyStroke.Ctrl(Key.K));
        var miss = matcher.Press(KeyStroke.From(Key.X));
        Assert.Equal(ChordOutcomeKind.Missed, miss.Kind);
        Assert.Equal("Ctrl+K, X", miss.Keys.ToString());

        matcher.Press(KeyStroke.Ctrl(Key.K));
        Assert.Equal(ChordOutcomeKind.Cancelled, matcher.Press(KeyStroke.From(Key.Escape)).Kind);
        Assert.False(matcher.IsPending);
    }

    [Fact]
    public void APressThatIsNothing_IsLeftAlone()
    {
        var matcher = new ChordMatcher(SnippetShortcutMap.Build([WithKeys("Log", "Ctrl+K, L")], mac: false));

        Assert.Equal(ChordOutcomeKind.Unbound, matcher.Press(KeyStroke.From(Key.A)).Kind);
        Assert.Equal(ChordOutcomeKind.Unbound, matcher.Press(KeyStroke.Ctrl(Key.J)).Kind);
        Assert.False(matcher.IsPending);
    }

    // ---- system-wide hotkeys ----

    [Fact]
    public void TheHotkeyPlan_SkipsSummonKeys_Duplicates_AndWhatWontParse()
    {
        var one = WithKeys("One", hotkey: "Ctrl+Alt+1");
        var again = WithKeys("Again", hotkey: "Alt+Ctrl+1");     // the same combination, written differently
        var summon = WithKeys("Summon", hotkey: "Ctrl+Alt+K");
        var shiftOnly = WithKeys("Shouty", hotkey: "Shift+K");   // would take capital K from everyone
        var garbage = WithKeys("Garbage", hotkey: "Ctrl+Alt+F21");
        var two = WithKeys("Two", hotkey: "Ctrl+Alt+2");

        var plan = SnippetHotkeys.Plan([one, again, summon, shiftOnly, garbage, two],
            [new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, "K"), null]);

        Assert.Equal(new[] { one.Id, two.Id }, plan.Select(p => p.SnippetId));
    }

    [Theory]
    [InlineData(Key.D1, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+1")]
    [InlineData(Key.J, KeyModifiers.Alt | KeyModifiers.Shift, "Alt+Shift+J")]
    [InlineData(Key.Space, KeyModifiers.Control, "Ctrl+SPACE")]
    [InlineData(Key.OemQuestion, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+/")]
    [InlineData(Key.OemPipe, KeyModifiers.Alt, "Alt+\\")]
    [InlineData(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt+=")]
    [InlineData(Key.F5, KeyModifiers.Control, "Ctrl+F5")]
    [InlineData(Key.F20, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+F20")]
    public void AKeyPress_BecomesAHotkey(Key key, KeyModifiers modifiers, string expected)
    {
        Assert.True(SnippetHotkeys.TryFromStroke(KeyStroke.From(key, modifiers), out var spec));
        Assert.Equal(expected, spec.ToString());
    }

    [Theory]
    [InlineData(Key.K, KeyModifiers.None)]       // no modifier at all
    [InlineData(Key.K, KeyModifiers.Shift)]      // capitals
    [InlineData(Key.F21, KeyModifiers.Control)]  // not a key every platform can register: a Mac stops at F20
    [InlineData(Key.Home, KeyModifiers.Control)]
    [InlineData(Key.OemBackslash, KeyModifiers.Control)]   // the ISO key beside left Shift: no code for it
    public void SomeKeyPresses_CannotBeHotkeys(Key key, KeyModifiers modifiers) =>
        Assert.False(SnippetHotkeys.TryFromStroke(KeyStroke.From(key, modifiers), out _));

    [Theory]
    [InlineData("Ctrl+Alt+1")]
    [InlineData("Alt+Shift+J")]
    [InlineData("Ctrl+SPACE")]
    [InlineData("Cmd+Alt+K")]
    [InlineData("Ctrl+Alt+/")]
    [InlineData("Alt+Shift+[")]
    [InlineData("Ctrl+Alt+Comma")]
    [InlineData("Ctrl+F13")]
    public void AHotkey_ReadsBackAsThePressItStandsFor(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.True(SnippetHotkeys.TryFromStroke(SnippetHotkeys.ToStroke(spec), out var again));
        Assert.Equal(spec, again);
    }

    // ---- the store ----

    [Fact]
    public void ShortcutAndHotkey_RoundTripThroughTheStore_AndOlderFilesReadAsNone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klippy-keys-{Guid.NewGuid():N}.json");
        var store = new SnippetStore(path, seedIfEmpty: false);
        store.Add(WithKeys("Keyed", "Ctrl+K, Ctrl+L", "Ctrl+Alt+1"));
        store.Add(new Snippet { Label = "Plain", Content = "plain" });

        var reloaded = new SnippetStore(path, seedIfEmpty: false);

        var keyed = reloaded.Snippets.Single(s => s.Label == "Keyed");
        Assert.Equal("Ctrl+K, Ctrl+L", keyed.Shortcut);
        Assert.Equal("Ctrl+Alt+1", keyed.Hotkey);
        Assert.Equal("", reloaded.Snippets.Single(s => s.Label == "Plain").Shortcut);
    }

    [Fact]
    public void AnImport_CarryingNullKeys_ReadsAsNone()
    {
        var store = new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-keys-{Guid.NewGuid():N}.json"),
            seedIfEmpty: false);

        store.Merge([new Snippet { Label = "Imported", Content = "x", Shortcut = null!, Hotkey = null! }]);

        Assert.Equal("", store.Snippets.Single().Shortcut);
        Assert.Equal("", store.Snippets.Single().Hotkey);
    }
}
