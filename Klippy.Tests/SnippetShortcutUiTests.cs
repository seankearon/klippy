using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Snippet shortcuts as the user meets them: keys pressed in the real window — a chord's second press
/// taking nothing from the search box — the editor's two key fields, and keys moving between snippets when
/// one takes another's.
/// </summary>
public class SnippetShortcutUiTests
{
    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required SnippetStore Store { get; init; }
        public List<string> Copies { get; } = new();
        public List<ExecutionPlan> Ran { get; } = new();

        /// <summary>What the clipboard holds as text — null for nothing, or a picture or files.</summary>
        public string? Clipboard { get; set; }

        public Snippet Snippet(string label) => Store.Snippets.Single(s => s.Label == label);

        /// <summary>The test's clipboard and launcher — again after a window opens, which wires the real ones.</summary>
        public Fixture Attach()
        {
            Vm.ClipboardWriter = payload => { Copies.Add(payload.Plain); return Task.CompletedTask; };
            Vm.ClipboardReader = () => Task.FromResult(Clipboard);
            Vm.Executor = plan =>
            {
                Ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, plan.Description));
            };
            return this;
        }
    }

    private static Fixture NewVm(params Snippet[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-keys-ui-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var snippet in snippets) store.Add(snippet);
        return new Fixture { Vm = new MainViewModel(store), Store = store }.Attach();
    }

    private static Snippet Log => new()
    {
        Label = "Send log files", Content = "Please send the logs", Shortcut = "Ctrl+K, L",
    };

    private static Snippet Mail => new() { Label = "Work email", Content = "sam@example.com", Shortcut = "Ctrl+Shift+M" };

    /// <summary>Takes an argument: its keys can't run it on the spot, only ask for the argument.</summary>
    private static Snippet Search => new()
    {
        Label = "Search", Content = "https://example.com/search?q=%P%", IsExecutable = true, QuickCode = "?",
        Shortcut = "Ctrl+K, S",
    };

    /// <summary>Marked to run, with nothing to ask for.</summary>
    private static Snippet Docs => new()
    {
        Label = "Docs", Content = "https://example.com/docs", IsExecutable = true, Shortcut = "Ctrl+K, D",
    };

    private static (Window Window, Fixture Fixture) Open(Fixture f)
    {
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();
        return (window, f);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None,
        string? text = null)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, text);
        if (text is not null) window.KeyTextInput(text);
        window.KeyRelease(key, modifiers, PhysicalKey.None, text);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A real click — pointer down and up at the control's centre — so the window's own pointer handling runs.</summary>
    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(
            new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static RawInputModifiers CommandKey =>
        OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    // ---- triggering ----

    [AvaloniaFact]
    public async Task Triggering_CopiesAPlainSnippet_AndRunsAMarkedOne()
    {
        var f = NewVm(Log, Docs);

        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Send log files").Id));
        Assert.Equal("Please send the logs", Assert.Single(f.Copies));

        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Docs").Id));
        Assert.Equal("https://example.com/docs", Assert.Single(f.Ran).Target);
        Assert.Single(f.Copies); // it ran, rather than copying
    }

    [AvaloniaFact]
    public async Task Triggering_IgnoresWhatIsTyped_AndRecordsNothing()
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-keys-ui-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        store.Add(Log);
        var commands = new CommandHistory(Path.Combine(Path.GetTempPath(), $"klippy-mru-{Guid.NewGuid():N}.json"));
        var f = new Fixture { Vm = new MainViewModel(store, commands: commands), Store = store }.Attach();

        f.Vm.FilterText = "something else entirely";
        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Send log files").Id));

        Assert.Equal("Please send the logs", Assert.Single(f.Copies));
        Assert.Equal(0, commands.Count); // the line in the box had nothing to do with it
    }

    [AvaloniaFact]
    public async Task Triggering_ARunThatCannotStart_SaysSo()
    {
        var f = NewVm(Docs);
        f.Vm.Executor = null;   // nothing here can run things

        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(f.Snippet("Docs").Id));
        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(Guid.NewGuid())); // and no such snippet
    }

    // ---- a snippet that takes an argument ----

    [AvaloniaFact]
    public async Task ASnippetTakingAnArgument_PutsItsQuickCodeInTheBox_AndWaits()
    {
        var f = NewVm(Search, Mail);
        f.Vm.IsHistoryMode = true;   // wherever the list was
        var asked = 0;
        f.Vm.ArgumentsRequested += () => asked++;

        Assert.Equal(TriggerOutcome.AwaitingArguments, await f.Vm.TriggerSnippetAsync(f.Snippet("Search").Id));

        Assert.False(f.Vm.IsHistoryMode);
        Assert.Equal("? ", f.Vm.FilterText);
        Assert.Equal("Search", Assert.Single(f.Vm.Filtered).Label);   // the invocation, on screen
        Assert.Equal(1, asked);
        Assert.Empty(f.Ran);    // nothing runs until the argument and Enter are given
        Assert.Empty(f.Copies);
    }

    [AvaloniaFact]
    public async Task ASnippetTakingAnArgument_WithNoQuickCode_SaysWhatItNeeds()
    {
        var noCode = Search;
        noCode.QuickCode = "";
        var f = NewVm(noCode);

        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(f.Snippet("Search").Id));

        Assert.True(f.Vm.IsToastError);
        Assert.Contains("quick-code", f.Vm.ToastText);
        Assert.Equal("", f.Vm.FilterText);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public async Task ASnippetTakingAnArgument_LeavesAnOpenEditorAlone()
    {
        var f = NewVm(Search, Mail);
        var editor = Edit(f, "Work email");

        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(f.Snippet("Search").Id));

        Assert.Same(editor, f.Vm.Editor);
        Assert.Equal("", f.Vm.FilterText);
        Assert.Contains("close this first", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public void ItsShortcut_LeavesTheCaretAfterTheQuickCode_AndEnterRunsWhatIsTyped()
    {
        var (window, f) = Open(NewVm(Search, Mail));
        var box = window.GetVisualDescendantsOf<TextBox>().Single(b => b.Name == "SearchBox");
        f.Vm.FilterText = "something";
        box.SelectAll();

        Press(window, Key.K, RawInputModifiers.Control);
        Press(window, Key.S, text: "s");

        Assert.Equal("? ", box.Text);
        Assert.True(box.IsFocused);
        Assert.Equal(2, box.CaretIndex);
        Assert.Equal(box.SelectionStart, box.SelectionEnd);   // nothing selected: typing adds, it doesn't replace
        Assert.Empty(f.Ran);

        foreach (var c in "cats")
            Press(window, Key.A + (c - 'a'), text: c.ToString());
        Press(window, Key.Enter, text: "\n");

        Assert.Equal("https://example.com/search?q=cats", Assert.Single(f.Ran).Target);
    }

    // ---- a snippet that uses the clipboard ----

    private static Snippet SearchClipboard => new()
    {
        Label = "Search the clipboard", Content = "https://example.com/search?q=%C%", IsExecutable = true,
        Shortcut = "Ctrl+K, C",
    };

    [AvaloniaFact]
    public async Task ASnippetUsingTheClipboard_GoesAheadWhenItHoldsText()
    {
        var f = NewVm(SearchClipboard);
        f.Clipboard = "cats and dogs";

        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Search the clipboard").Id));

        var plan = Assert.Single(f.Ran);
        Assert.Contains("cats", plan.Target);
        Assert.DoesNotContain("%C%", plan.Target);
        Assert.Equal("", f.Vm.FilterText); // no prompt: there was nothing to ask for
    }

    [AvaloniaTheory]
    [InlineData(null)]       // nothing on it — or a picture, or files
    [InlineData("")]
    [InlineData("   \n")]    // only whitespace: nothing to search for
    public async Task ASnippetUsingTheClipboard_DoesNothingAndSaysSo_WhenItHoldsNoText(string? clipboard)
    {
        var f = NewVm(SearchClipboard);
        f.Clipboard = clipboard;

        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(f.Snippet("Search the clipboard").Id));

        Assert.Empty(f.Ran);
        Assert.True(f.Vm.IsToastError);
        Assert.Contains("no text on it", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public async Task ACopyUsingTheClipboard_IsHeldToTheSameRule()
    {
        var reply = new Snippet { Label = "Reply", Content = "Re: %C%", Shortcut = "Ctrl+K, R" };
        var f = NewVm(reply);

        Assert.Equal(TriggerOutcome.Failed, await f.Vm.TriggerSnippetAsync(f.Snippet("Reply").Id));
        Assert.Empty(f.Copies);

        f.Clipboard = "the invoice";
        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Reply").Id));
        Assert.Equal("Re: the invoice", Assert.Single(f.Copies));
    }

    [AvaloniaFact]
    public async Task ASnippetWithBothPlaceholders_StillWaitsForItsArgument()
    {
        var both = Search;
        both.Content = "https://example.com/search?q=%P%+%C%";
        var f = NewVm(both);
        f.Clipboard = "cats";

        Assert.Equal(TriggerOutcome.AwaitingArguments, await f.Vm.TriggerSnippetAsync(f.Snippet("Search").Id));
        Assert.Equal("? ", f.Vm.FilterText);
        Assert.Empty(f.Ran);
    }

    [AvaloniaFact]
    public void ItsShortcut_SearchesTheClipboard_StraightAway()
    {
        var (window, f) = Open(NewVm(SearchClipboard));
        f.Clipboard = "cats";

        Press(window, Key.K, RawInputModifiers.Control);
        Press(window, Key.C, text: "c");

        Assert.Equal("https://example.com/search?q=cats", Assert.Single(f.Ran).Target);
        Assert.Equal("", f.Vm.FilterText);
    }

    [AvaloniaFact]
    public void TheEditor_SaysItsKeysUseTheClipboard()
    {
        var f = NewVm(SearchClipboard);
        var editor = Edit(f, "Search the clipboard");

        Assert.Contains("uses the clipboard", editor.ArgumentsHint);
        Assert.False(editor.ArgumentsHintIsWarning);   // no quick-code needed for this one
    }

    [AvaloniaFact]
    public void TheEditor_SaysWhatTheKeysDoForASnippetTakingAnArgument()
    {
        var f = NewVm(Search);
        var editor = Edit(f, "Search");

        Assert.Contains("\u201c? \u201d typed", editor.ArgumentsHint);
        Assert.False(editor.ArgumentsHintIsWarning);

        editor.QuickCode = "";
        Assert.Contains("give it one", editor.ArgumentsHint);
        Assert.True(editor.ArgumentsHintIsWarning);

        editor.Content = "https://example.com/";   // no argument to take: nothing to say
        Assert.False(editor.HasArgumentsHint);
    }

    // ---- the keyboard ----

    [AvaloniaFact]
    public void AChord_InTheSearchBox_CopiesItsSnippet_AndTypesNothing()
    {
        var (window, f) = Open(NewVm(Log, Mail));
        Press(window, Key.A, text: "a");
        Assert.Equal("a", f.Vm.FilterText); // typing still types

        Press(window, Key.K, RawInputModifiers.Control);
        Assert.True(f.Vm.HasChordStatus);
        Assert.Contains("Ctrl+K", f.Vm.ChordStatus);
        Assert.Empty(f.Copies);

        Press(window, Key.L, text: "l");

        Assert.Equal("Please send the logs", Assert.Single(f.Copies));
        Assert.False(f.Vm.HasChordStatus);
        Assert.Equal("a", f.Vm.FilterText); // the L was the chord's, not the box's
    }

    [AvaloniaFact]
    public void ASinglePressShortcut_CopiesAtOnce()
    {
        var (window, f) = Open(NewVm(Log, Mail));

        Press(window, Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.Equal("sam@example.com", Assert.Single(f.Copies));
    }

    [AvaloniaFact]
    public void AChordThatLeadsNowhere_SaysSo_AndTypesNothing()
    {
        var (window, f) = Open(NewVm(Log));

        Press(window, Key.K, RawInputModifiers.Control);
        Press(window, Key.X, text: "x");

        Assert.True(f.Vm.IsChordMiss);
        Assert.Equal("Ctrl+K, X isn't a shortcut", f.Vm.ChordStatus);
        Assert.Equal("", f.Vm.FilterText);
        Assert.Empty(f.Copies);
    }

    [AvaloniaFact]
    public void Esc_CallsOffAWaitingChord_WithoutClearingTheSearch()
    {
        var (window, f) = Open(NewVm(Log));
        f.Vm.FilterText = "send";

        Press(window, Key.K, RawInputModifiers.Control);
        Press(window, Key.Escape);

        Assert.False(f.Vm.IsChordPending);
        Assert.False(f.Vm.HasChordStatus);
        Assert.Equal("send", f.Vm.FilterText); // Esc was the chord's, not the box's
    }

    [AvaloniaFact]
    public void AChordsSecondPress_TakesPrecedenceOverKlippysOwnKeys()
    {
        var withNew = new Snippet
        {
            Label = "Chord on N", Content = "chord", Shortcut = OperatingSystem.IsMacOS() ? "Ctrl+K, Cmd+N" : "Ctrl+K, Ctrl+N",
        };
        var (window, f) = Open(NewVm(withNew));

        Press(window, Key.K, RawInputModifiers.Control);
        Press(window, Key.N, CommandKey);

        Assert.Equal("chord", Assert.Single(f.Copies));
        Assert.Null(f.Vm.Editor); // not a new snippet as well
    }

    [AvaloniaFact]
    public void KlippysOwnKeys_StillDoWhatTheyDid()
    {
        var (window, f) = Open(NewVm(Log));

        Press(window, Key.N, CommandKey);

        Assert.NotNull(f.Vm.Editor);
        Assert.Empty(f.Copies);
    }

    [AvaloniaFact]
    public void NoShortcutFires_WhileAnOverlayIsOpen()
    {
        var (window, f) = Open(NewVm(Mail));
        f.Vm.OpenSettingsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.Empty(f.Copies);
    }

    [AvaloniaFact]
    public void AShortcut_WorksInTheClipboardHistoryToo()
    {
        var (window, f) = Open(NewVm(Mail));
        f.Vm.IsHistoryMode = true;

        Press(window, Key.M, RawInputModifiers.Control | RawInputModifiers.Shift);

        Assert.Equal("sam@example.com", Assert.Single(f.Copies));
    }

    [AvaloniaFact]
    public void TheRow_ShowsItsKeys()
    {
        var f = NewVm(new Snippet { Label = "Both", Content = "x", Shortcut = "Ctrl+K, L", Hotkey = "Ctrl+Alt+1" });

        var row = (SnippetViewModel)f.Vm.Filtered.Single();

        Assert.True(row.HasKeys);
        Assert.Equal("Ctrl+K, L · Ctrl+Alt+1 anywhere", row.KeysHint);
    }

    // ---- the editor ----

    private static EditorViewModel Edit(Fixture f, string label)
    {
        f.Vm.FilterText = "";
        var row = f.Vm.Filtered.OfType<SnippetViewModel>().Single(r => r.Label == label);
        f.Vm.EditCommand.Execute(row);
        return f.Vm.Editor!;
    }

    [AvaloniaFact]
    public void TheEditor_RecordsAChord_ThroughTheRealKeyboard_AndEscOnlyStopsListening()
    {
        var (window, f) = Open(NewVm(Log, Mail));
        var editor = Edit(f, "Work email");
        Dispatcher.UIThread.RunJobs();

        var field = window.GetVisualDescendantsOf<Button>().Single(b => b.Name == "ShortcutField");
        Click(window, field);
        Assert.True(editor.IsRecordingShortcut);

        // Esc stops the recording — and only the recording: the editor stays open.
        Press(window, Key.Escape);
        Assert.False(editor.IsRecording);
        Assert.Same(editor, f.Vm.Editor);
        Assert.Equal("Ctrl+Shift+M", editor.Shortcut);

        Click(window, field);
        Press(window, Key.K, RawInputModifiers.Control);
        Assert.Equal("Ctrl+K, …", editor.ShortcutText);
        Press(window, Key.M, RawInputModifiers.Control);   // Ctrl+M: a chord, not a second shortcut

        Assert.False(editor.IsRecording);
        Assert.Equal("Ctrl+K, Ctrl+M", editor.Shortcut);

        editor.SaveCommand.Execute(null);
        Assert.Equal("Ctrl+K, Ctrl+M", f.Snippet("Work email").Shortcut);
    }

    [AvaloniaFact]
    public void TheEditor_KeepsASinglePressOnEnter()
    {
        var f = NewVm(Mail);
        var editor = Edit(f, "Work email");

        editor.BeginRecording(KeyField.Shortcut);
        editor.Press(KeyStroke.From(Key.F6));
        editor.Press(KeyStroke.From(Key.Enter));

        Assert.Equal("F6", editor.Shortcut);
    }

    [AvaloniaFact]
    public void TheEditor_RefusesKeysThatCouldNeverFire()
    {
        var f = NewVm(Mail);
        var editor = Edit(f, "Work email");

        editor.BeginRecording(KeyField.Shortcut);
        editor.Press(KeyStroke.From(Key.C));
        Assert.True(editor.ShortcutHintIsWarning);
        Assert.Contains("would be typing", editor.ShortcutHint);
        Assert.Equal("Ctrl+Shift+M", editor.Shortcut); // left as it was

        editor.BeginRecording(KeyField.Shortcut);
        editor.Press(KeyStroke.From(Key.N, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
        Assert.Contains("New snippet", editor.ShortcutHint);

        editor.BeginRecording(KeyField.Shortcut);
        editor.Press(KeyStroke.From(Key.K, AppSettings.Current.ParsedHotkey.Modifiers.HasFlag(HotkeyModifiers.Meta)
            ? KeyModifiers.Meta | KeyModifiers.Alt
            : KeyModifiers.Control | KeyModifiers.Alt));
        Assert.Contains("summons Klippy", editor.ShortcutHint);
        Assert.Equal("Ctrl+Shift+M", editor.Shortcut);
    }

    [AvaloniaFact]
    public void TheEditor_SaysWhoseKeysItWillTake_AndSavingTakesThem()
    {
        var f = NewVm(Log, Mail);
        var editor = Edit(f, "Work email");

        // Ctrl+K on its own is where Send log files' chord starts.
        editor.BeginRecording(KeyField.Shortcut);
        editor.Press(KeyStroke.Ctrl(Key.K));
        editor.Press(KeyStroke.From(Key.Enter));
        Assert.Contains("“Send log files”", editor.ShortcutHint);
        Assert.Contains("saving moves it here", editor.ShortcutHint);

        editor.SaveCommand.Execute(null);

        Assert.Equal("Ctrl+K", f.Snippet("Work email").Shortcut);
        Assert.Equal("", f.Snippet("Send log files").Shortcut);
        Assert.Equal(f.Snippet("Work email").Id, f.Vm.Shortcuts.Find(new Shortcut(KeyStroke.Ctrl(Key.K)))!.SnippetId);
    }

    [AvaloniaFact]
    public void TheHotkeyField_OnlyAppearsWhereHotkeysAreRegistered()
    {
        var f = NewVm(Mail);
        Assert.False(Edit(f, "Work email").ShowsHotkey);

        f.Vm.Editor = null;
        f.Vm.CanRegisterHotkeys = true;
        Assert.True(Edit(f, "Work email").ShowsHotkey);
    }

    [AvaloniaFact]
    public void TheHotkeyField_TakesOnePress_RefusesSummonKeys_AndTakesFromOthersOnSave()
    {
        var f = NewVm(Mail, new Snippet { Label = "Other", Content = "o", Hotkey = "Ctrl+Alt+1" });
        f.Vm.CanRegisterHotkeys = true;
        var editor = Edit(f, "Work email");

        editor.BeginRecording(KeyField.Hotkey);
        editor.Press(KeyStroke.From(Key.Home, KeyModifiers.Control));
        Assert.True(editor.HotkeyHintIsWarning);   // not a key every platform can register
        Assert.Equal("", editor.Hotkey);

        var summon = AppSettings.Current.ParsedHotkey;
        editor.BeginRecording(KeyField.Hotkey);
        editor.Press(KeyStroke.From(Key.K, ToKeyModifiers(summon.Modifiers)));
        Assert.Contains("summons Klippy", editor.HotkeyHint);
        Assert.Equal("", editor.Hotkey);

        editor.BeginRecording(KeyField.Hotkey);
        editor.Press(KeyStroke.From(Key.D1, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.False(editor.IsRecording);
        Assert.Equal("Ctrl+Alt+1", editor.Hotkey);
        Assert.Contains("“Other”", editor.HotkeyHint);

        editor.SaveCommand.Execute(null);

        Assert.Equal("Ctrl+Alt+1", f.Snippet("Work email").Hotkey);
        Assert.Equal("", f.Snippet("Other").Hotkey);
        Assert.Equal(f.Snippet("Work email").Id, Assert.Single(f.Vm.PlannedHotkeys()).SnippetId);
    }

    [AvaloniaFact]
    public void SnippetsChanged_IsRaisedForEveryChangeThatCanMoveKeys_AndAnUnclaimedHotkeyIsReported()
    {
        var f = NewVm(Mail);
        f.Vm.CanRegisterHotkeys = true;
        var raised = 0;
        // Stands in for the launcher: it re-registers on every change, and can't claim this one.
        f.Vm.SnippetsChanged += () =>
        {
            raised++;
            f.Vm.ReportUnclaimedHotkeys(f.Vm.PlannedHotkeys().Select(p => p.SnippetId));
        };

        var editor = Edit(f, "Work email");
        editor.BeginRecording(KeyField.Hotkey);
        editor.Press(KeyStroke.From(Key.D9, KeyModifiers.Control | KeyModifiers.Alt));
        editor.SaveCommand.Execute(null);

        Assert.Equal(1, raised);
        Assert.True(f.Vm.IsToastError);
        Assert.Contains("Another application holds Ctrl+Alt+9", f.Vm.ToastText);
        Assert.True(Edit(f, "Work email").HotkeyHintIsWarning); // and the editor says so next time

        f.Vm.Editor = null;
        f.Vm.RequestDeleteCommand.Execute(f.Vm.Filtered.OfType<SnippetViewModel>().Single());
        f.Vm.ConfirmDeleteCommand.Execute(null);
        Assert.Equal(2, raised);
    }

    [AvaloniaFact]
    public void TheHotkeyField_TakesPunctuationAndFunctionKeys_ThroughTheRealKeyboard()
    {
        var (window, f) = Open(NewVm(Mail));
        f.Vm.CanRegisterHotkeys = true;
        var editor = Edit(f, "Work email");
        Dispatcher.UIThread.RunJobs();

        var field = window.GetVisualDescendantsOf<Button>().Single(b => b.Name == "HotkeyField");
        Click(window, field);
        Press(window, Key.OemQuestion, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.Equal("Ctrl+Alt+/", editor.Hotkey);
        Assert.Equal("Ctrl+Alt+/", editor.HotkeyText);

        Click(window, field);
        Press(window, Key.F9, RawInputModifiers.Control);
        Assert.Equal("Ctrl+F9", editor.Hotkey);

        editor.SaveCommand.Execute(null);
        Assert.Equal(f.Snippet("Work email").Id, Assert.Single(f.Vm.PlannedHotkeys()).SnippetId);
        Assert.Equal("Ctrl+F9", f.Vm.PlannedHotkeys()[0].Spec.ToString());
    }

    [AvaloniaFact]
    public void AHotkeyPressedWhileAFieldListens_IsRecorded_RatherThanFiring()
    {
        var f = NewVm(Mail, new Snippet { Label = "Other", Content = "o", Hotkey = "Ctrl+Alt+1" });
        f.Vm.CanRegisterHotkeys = true;
        var editor = Edit(f, "Work email");
        editor.BeginRecording(KeyField.Hotkey);

        // What the launcher does when the OS hands it Other's hotkey instead of the editor.
        Assert.True(f.Vm.OfferHotkeyToRecorder(new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, "1")));

        Assert.Equal("Ctrl+Alt+1", editor.Hotkey);
        Assert.Empty(f.Copies);
        Assert.False(f.Vm.OfferHotkeyToRecorder(new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, "2")));
    }

    [AvaloniaFact]
    public async Task AHotkeyPressedWithAnOverlayUp_StillCopies_ButLeavesTheWindowBe()
    {
        var f = NewVm(Mail);
        var copiedEvents = 0;
        var closes = 0;
        f.Vm.Copied += () => copiedEvents++;
        f.Vm.CloseRequested += () => closes++;
        Edit(f, "Work email");

        Assert.Equal(TriggerOutcome.Done, await f.Vm.TriggerSnippetAsync(f.Snippet("Work email").Id));

        Assert.Equal("sam@example.com", Assert.Single(f.Copies));
        Assert.Equal(0, copiedEvents);   // the keyboard stays in the editor…
        Assert.Equal(0, closes);         // …and nothing closes mid-edit
        Assert.NotNull(f.Vm.Editor);
    }

    [AvaloniaFact]
    public void AHotkeyWhereAShortcutStarts_IsRefused()
    {
        var f = NewVm(Mail, new Snippet { Label = "Other", Content = "o", Shortcut = "Ctrl+Alt+2, X" });
        f.Vm.CanRegisterHotkeys = true;
        var editor = Edit(f, "Work email");

        editor.BeginRecording(KeyField.Hotkey);
        editor.Press(KeyStroke.From(Key.D2, KeyModifiers.Control | KeyModifiers.Alt));

        Assert.Equal("", editor.Hotkey);
        Assert.Contains("\u201cOther\u201d's shortcut in Klippy", editor.HotkeyHint);
    }

    [AvaloniaFact]
    public void ADuplicate_DoesNotCopyTheKeys()
    {
        var f = NewVm(new Snippet { Label = "Keyed", Content = "x", Shortcut = "Ctrl+K, L", Hotkey = "Ctrl+Alt+1" });

        f.Vm.DuplicateCommand.Execute(f.Vm.Filtered.OfType<SnippetViewModel>().Single());

        Assert.Equal("", f.Vm.Editor!.Shortcut);
        Assert.Equal("", f.Vm.Editor.Hotkey);
    }

    private static KeyModifiers ToKeyModifiers(HotkeyModifiers m)
    {
        var result = KeyModifiers.None;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= KeyModifiers.Control;
        if (m.HasFlag(HotkeyModifiers.Alt)) result |= KeyModifiers.Alt;
        if (m.HasFlag(HotkeyModifiers.Shift)) result |= KeyModifiers.Shift;
        if (m.HasFlag(HotkeyModifiers.Meta)) result |= KeyModifiers.Meta;
        return result;
    }
}

internal static class VisualTreeTestExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOf<T>(this Window window) where T : class =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<T>();
}
