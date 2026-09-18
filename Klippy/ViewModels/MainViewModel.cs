using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

public partial class TagChipViewModel : ViewModelBase
{
    public string Name { get; }

    /// <summary>
    /// True for the History chip, which switches what the list is showing rather than
    /// filtering it. It sits in the same row because it is the same gesture, but the view
    /// sets it apart so it does not read as just another tag.
    /// </summary>
    public bool IsMode { get; }

    [ObservableProperty]
    private bool _isSelected;

    public TagChipViewModel(string name, bool isSelected = false, bool isMode = false)
    {
        Name = name;
        IsMode = isMode;
        _isSelected = isSelected;
    }
}

public partial class MainViewModel : ViewModelBase
{
    public const string AllTag = "All";
    public const string HistoryTag = "History";

    /// <summary>
    /// The word that offers to close Klippy, matched as the whole line the way
    /// <see cref="UnmatchedSearch.SystemActionFor"/> matches "lock" and "restart". Klippy
    /// is a resident launcher, so without it the only way out is the tray menu — a mouse
    /// trip away from a window you reached by hotkey.
    /// </summary>
    public const string QuitWord = "quit";

    private readonly SnippetStore _store;
    private readonly ClipHistoryStore? _history;
    private readonly CommandHistory? _commands;
    private readonly AppSettings _prefs;
    private readonly Dictionary<Guid, SnippetViewModel> _rowCache = new();
    private readonly Dictionary<Guid, ClipViewModel> _clipCache = new();
    private readonly DispatcherTimer _toastTimer;

    public ObservableCollection<RowViewModel> Filtered { get; } = new();
    public ObservableCollection<TagChipViewModel> Tags { get; } = new();

    /// <summary>
    /// The remembered command lines currently on offer — everything, or what the typed
    /// line could still become — newest first. Empty while the MRU is closed.
    /// </summary>
    public ObservableCollection<string> Commands { get; } = new();

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOfferSelected))]
    private RowViewModel? _selectedSnippet;

    [ObservableProperty]
    private EditorViewModel? _editor;

    [ObservableProperty]
    private SnippetViewModel? _deleteTarget;

    [ObservableProperty]
    private TransferViewModel? _transfer;

    [ObservableProperty]
    private SettingsViewModel? _settings;

    /// <summary>
    /// What Enter would run instead of copying, when the search matched nothing but named
    /// something runnable. Null the rest of the time, which is nearly always.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOfferSelected))]
    private OfferViewModel? _offer;

    /// <summary>
    /// Whether the offer, rather than a row, is the thing Enter will act on. Exactly one of
    /// the two may say so — the view shows its accent bar and its ↵ badge from this, as the
    /// list shows a row's from being selected.
    /// </summary>
    public bool IsOfferSelected => Offer is not null && SelectedSnippet is null;

    /// <summary>A machine control waiting to be confirmed. Drives the confirmation overlay.</summary>
    [ObservableProperty]
    private OfferViewModel? _pendingOffer;

    [ObservableProperty]
    private bool _isToastVisible;

    /// <summary>What the toast says. A copy is the common case, so that is the default.</summary>
    [ObservableProperty]
    private string _toastText = CopiedToast;

    /// <summary>Whether the toast is reporting a failure rather than confirming something.</summary>
    [ObservableProperty]
    private bool _isToastError;

    /// <summary>Whether the bottom preview pane is showing. Starts closed — the list is the primary surface.</summary>
    [ObservableProperty]
    private bool _isPreviewOpen;

    /// <summary>Whether the recent-commands list is showing under the search box.</summary>
    [ObservableProperty]
    private bool _isCommandsOpen;

    /// <summary>
    /// The command being browsed, or null when the list is merely on offer. Setting it
    /// puts that line in the search box, so the list underneath — and the preview of
    /// what Enter would do — follows the selection.
    /// </summary>
    [ObservableProperty]
    private string? _selectedCommand;

    [ObservableProperty]
    private string _snippetCountText = "";

    /// <summary>Whether the list is showing captured clips instead of saved snippets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private bool _isHistoryMode;

    /// <summary>The search box says what it is searching, since the list holds two different things.</summary>
    public string SearchWatermark => IsHistoryMode
        ? "Filter clipboard history"
        : CanRecallCommands
            ? "Filter snippets or type a quick-code · ↓ recent"
            : "Filter snippets or type a quick-code";

    private string _activeTag = AllTag;

    /// <summary>The row a quick-code invocation is currently pointed at, if any.</summary>
    private SnippetViewModel? _invoked;

    /// <summary>
    /// What was typed when the MRU opened. Browsing writes each command into the search
    /// box, so leaving the list without taking one has to put back what was there.
    /// </summary>
    private string _commandStem = "";

    /// <summary>
    /// True while this view model is writing the search box itself. Typing opens the MRU;
    /// the MRU writing a command into the box must not count as typing, or browsing would
    /// re-ask what the line it just wrote could become.
    /// </summary>
    private bool _writingFilter;

    /// <summary>Set by the view; writes a copy payload to the platform clipboard.</summary>
    public Func<CopyPayload, Task>? ClipboardWriter { get; set; }

    /// <summary>
    /// Set by the view; reads the clipboard's text for the <c>%C%</c> macro. Only called
    /// when an item actually carries one, so an ordinary copy still never reads the
    /// clipboard.
    /// </summary>
    public Func<Task<string?>>? ClipboardReader { get; set; }

    /// <summary>
    /// Set by the view; carries out an execution plan. The desktop starts processes,
    /// mobile can only open a link — each head supplies what its platform can do, and
    /// an item marked to run says so rather than failing quietly where nothing can.
    /// </summary>
    public Func<ExecutionPlan, Task<ExecutionResult>>? Executor { get; set; }

    /// <summary>
    /// Raised after a snippet reaches the clipboard, or is executed, so the view can
    /// reset its search box.
    /// </summary>
    public event Action? Copied;

    /// <summary>
    /// Raised after a copy the user asked to be dismissed by. The view model has no window
    /// to hide, so it says what it wants and the head decides whether it can — nothing
    /// listens on mobile, which has no launcher to dismiss to.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Raised once the user has confirmed they want Klippy closed. Same shape as
    /// <see cref="CloseRequested"/> and for the same reason: the view model has no process
    /// to end, so it says what it wants and the head decides how — the desktop launcher
    /// releases its hotkeys and tray icon and shuts the lifetime down.
    /// </summary>
    public event Action? QuitRequested;

    public string KeyHints { get; } = OperatingSystem.IsMacOS()
        ? "↑↓ navigate  ↵ copy  ⌘N new  ⌘F filter  ⌘P preview"
        : "↑↓ navigate  ↵ copy  Ctrl+N new  Ctrl+F filter  Ctrl+P preview";

    /// <summary>Shown next to the title, as "v1.0.3". The release build stamps the version
    /// into every head from ver.txt; a local build reports the SDK default of 1.0.0.</summary>
    public string VersionText { get; } = ReadVersionText();

    private static string ReadVersionText()
    {
        var version = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version)) return string.Empty;

        // The SDK appends "+<commit sha>" when the repo is a git checkout, which is noise
        // in a title bar.
        var build = version.IndexOf('+');
        return "v" + (build >= 0 ? version[..build] : version);
    }

    public string SearchKeyHint { get; } = OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl F";

    public string PreviewKeyHint { get; } = OperatingSystem.IsMacOS() ? "⌘P" : "Ctrl P";

    /// <summary>
    /// Whether a search that matched nothing may be offered as something to run. Desktop
    /// only, for the reason the command MRU is: it is a keyboard gesture in a launcher,
    /// and a phone has neither a shell to hand a path to nor a machine of its own to
    /// lock. The Settings toggles follow this, so the two can never disagree.
    /// </summary>
    public bool CanExecuteUnmatched { get; } = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether closing the app is a thing this platform does. The desktop head is a
    /// resident launcher with a real exit; on mobile the app *is* the screen, closing it is
    /// the system's gesture to make, and iOS forbids an app quitting itself outright.
    ///
    /// Separate from <see cref="CanExecuteUnmatched"/> although the platforms agree today:
    /// that one is about handing typed text to the machine and can be switched off in
    /// Settings, and being unwilling to do that is no reason to be unable to close the app.
    /// </summary>
    public bool CanQuit { get; } = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>Whether there is a clipboard history to switch to. False on mobile.</summary>
    public bool HasHistory => _history is not null;

    /// <summary>Whether typed commands are remembered and offered. Off at a limit of zero.</summary>
    public bool HasCommandHistory => _commands is { IsEnabled: true };

    /// <summary>
    /// Whether there is actually something to recall. The search box only offers
    /// "↓ recent" once there is — a fresh install would otherwise advertise a key that
    /// does nothing yet.
    /// </summary>
    private bool CanRecallCommands => _commands is { IsEnabled: true, Count: > 0 };

    // The MRU is built here rather than defaulted in the constructor below so that a
    // caller handing in its own store — a test — never gets a file-backed one by accident.
    public MainViewModel() : this(new SnippetStore(), ClipboardHistory.Store, null,
        PlatformCommandHistory()) { }

    /// <summary>
    /// The command MRU where the platform has a command line to recall into, null
    /// otherwise. Desktop only, for the reason the clipboard history is: the gesture is a
    /// keyboard one — down and up in the search box — and a phone has neither the keys to
    /// browse with nor the room for the list they open. Recording commands there would
    /// write a file nothing could read back.
    /// </summary>
    private static CommandHistory? PlatformCommandHistory() =>
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
            ? null
            : new CommandHistory(capacity: AppSettings.Current.CommandHistoryLimit);

    /// <param name="settings">Defaults to <see cref="AppSettings.Current"/>; passed in by tests.</param>
    /// <param name="commands">The command MRU, or null for none.</param>
    public MainViewModel(SnippetStore store, ClipHistoryStore? history = null, AppSettings? settings = null,
        CommandHistory? commands = null)
    {
        _store = store;
        _history = history;
        _commands = commands;
        _prefs = settings ?? AppSettings.Current;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); IsToastVisible = false; };

        // Clips arrive while the window is open, so the list cannot wait for a user action.
        if (_history is not null)
            _history.Changed += OnHistoryChanged;

        RebuildTags();
        Refresh();
    }

    private void OnHistoryChanged()
    {
        if (IsHistoryMode) Refresh();
    }

    partial void OnFilterTextChanged(string value)
    {
        // A search (and a quick-code) looks at every snippet, so a live tag filter is
        // dropped rather than silently ignored — the chips always show what the list
        // is actually doing.
        if (value.Length > 0 && _activeTag != AllTag)
            ActivateTag(AllTag);

        // Typing re-asks what the line could still become. A line that completes nothing
        // closes the MRU, which is what hands the arrow keys back to the list: an
        // ordinary search is not a command and must not have to fight one for them.
        if (!_writingFilter)
        {
            if (value.Length > 0) OpenCommands(selectFirst: false);
            else CloseCommands(restore: false);
        }

        Refresh();
    }

    /// <summary>
    /// Writes the search box from inside the view model — recalling a command, or putting
    /// back what browsing overwrote. Marked as ours so it does not read as typing.
    /// </summary>
    private void SetFilterTextInternally(string text)
    {
        _writingFilter = true;
        try { FilterText = text; }
        finally { _writingFilter = false; }
    }

    /// <summary>Re-runs the search and updates the visible rows, keeping row VMs (and their expanded state) stable.</summary>
    private void Refresh()
    {
        if (IsHistoryMode) RefreshHistory();
        else RefreshSnippets();

        SelectedSnippet = Filtered.Count > 0 ? Filtered[0] : null;
        UpdateOffer();
    }

    /// <summary>
    /// Decides whether the line that was just searched for should also be offered as
    /// something to run.
    ///
    /// An item that matches beats the offer — but only where the line could have been
    /// meant as a search for that item. "lock" could: a snippet called "Lock the server
    /// room door" answers to it, and keeps it a filter. <c>D:\work\tools\</c> could not,
    /// and a snippet whose body merely mentions that folder has not been asked for by
    /// someone typing the folder's own path.
    ///
    /// An item also wins when it <em>is</em> the line — a snippet whose text is the very
    /// link you typed was plausibly the thing you were looking for, however literal the
    /// line. Merely mentioning it is not being it.
    ///
    /// The one place all of that is dropped is the clipboard history, where the clips
    /// themselves are mostly paths and links: there a line that looks like one is far more
    /// likely to be someone hunting for the clip they copied than an instruction, so a
    /// matching clip wins whatever was typed.
    /// </summary>
    private void UpdateOffer()
    {
        // Klippy's own quit, ahead of the gate below and deliberately outside it: that
        // setting governs handing typed text to the machine, and closing the app is
        // neither typed text nor the machine's business. An item still beats it exactly as
        // one beats "lock" — "quit" is an ordinary word, and a snippet answering to it was
        // plausibly what was being looked for. The tray menu and the footer link remain.
        if (CanQuit && Names(FilterText, QuitWord) && Filtered.Count == 0)
        {
            Offer = OfferViewModel.Quit();
            return;
        }

        if (!_prefs.ExecuteUnmatched || !CanExecuteUnmatched)
        {
            Offer = null;
            return;
        }

        // Only a rooted path ever reaches the file system here, so an ordinary search word
        // costs the same as it did when this ran on an empty list alone.
        //
        // The same environment a marked item is resolved against, variables file included:
        // two routes to the same launcher must not disagree about what %ws% means any more
        // than about what %LOCALAPPDATA% does.
        var plan = UnmatchedSearch.Plan(
            FilterText,
            _prefs.ExecuteVerifyPaths,
            environment: KlippyVariables.Current.Ahead(EnvironmentProbe.Real));
        if (plan.Kind == ExecutionKind.None)
        {
            Offer = null;
            return;
        }

        bool beatenByAMatch = Filtered.Count > 0
                              && (IsHistoryMode || UnmatchedSearch.CouldBeASearch(plan) || AnItemIsTheLine(plan));
        if (beatenByAMatch)
        {
            Offer = null;
            return;
        }

        Offer = new OfferViewModel(plan);

        // Standing beside a list that still has rows in it, the offer is what Enter acts
        // on — so the selection comes off the list, since a row that is not getting the
        // keystroke must not sit there wearing the badge that says it is. ↓ moves back in,
        // and from there Enter activates the row as it always did.
        if (Filtered.Count > 0) SelectedSnippet = null;
    }

    private void RefreshSnippets()
    {
        // "code argument…" invokes one snippet rather than filtering the list: the search
        // box has become a command line, so the words after the code are arguments for
        // its %P% placeholders and not search terms.
        if (TryInvoke()) return;

        ClearArguments();
        var results = _store.Search(FilterText);

        Filtered.Clear();
        foreach (var entry in results)
        {
            if (_activeTag != AllTag && !string.Equals(entry.Item.Tag, _activeTag, StringComparison.OrdinalIgnoreCase))
                continue;

            Filtered.Add(RowFor(entry.Item));
        }

        SnippetCountText = Filtered.Count == 1 ? "1 snippet" : $"{Filtered.Count} snippets";
    }

    /// <summary>
    /// Shows just the snippet whose quick-code was typed, carrying whatever was typed
    /// after it. False when the line is not an invocation — no whitespace yet, or no
    /// snippet answers to that exact code — and the ordinary search runs instead.
    /// </summary>
    private bool TryInvoke()
    {
        if (!QuickInvocation.TryParse(FilterText, out var invocation) ||
            _store.FindByQuickCode(invocation.Code) is not { } snippet)
            return false;

        var row = RowFor(snippet);
        if (!ReferenceEquals(_invoked, row)) ClearArguments();

        _invoked = row;
        row.SetArguments(invocation.Arguments);

        Filtered.Clear();
        Filtered.Add(row);
        SnippetCountText = "1 snippet";
        return true;
    }

    /// <summary>Takes the arguments back off the row that last had them.</summary>
    private void ClearArguments()
    {
        _invoked?.SetArguments(Array.Empty<string>());
        _invoked = null;
    }

    private SnippetViewModel RowFor(Snippet snippet)
    {
        if (!_rowCache.TryGetValue(snippet.Id, out var row))
            _rowCache[snippet.Id] = row = new SnippetViewModel(snippet);
        return row;
    }

    private void RefreshHistory()
    {
        ClearArguments(); // a snippet's arguments mean nothing against clips
        Filtered.Clear();
        if (_history is null) return;

        foreach (var entry in _history.Search(FilterText))
        {
            if (!_clipCache.TryGetValue(entry.Item.Id, out var row))
                _clipCache[entry.Item.Id] = row = new ClipViewModel(entry.Item, _history.TryLoadImage);
            else
                row.NotifyModelChanged(); // pin state and age move under the row
            Filtered.Add(row);
        }

        // Rows cached for evicted clips would otherwise accumulate for the whole session.
        if (_clipCache.Count > _history.Count * 2)
            PruneClipCache();

        SnippetCountText = Filtered.Count == 1 ? "1 clip" : $"{Filtered.Count} clips";
    }

    private void PruneClipCache()
    {
        var live = new HashSet<Guid>();
        foreach (var clip in _history!.Entries) live.Add(clip.Id);

        foreach (var id in new List<Guid>(_clipCache.Keys))
            if (!live.Contains(id))
                _clipCache.Remove(id);
    }

    private void RebuildTags()
    {
        Tags.Clear();
        if (HasHistory)
            Tags.Add(new TagChipViewModel(HistoryTag, IsHistoryMode, isMode: true));
        Tags.Add(new TagChipViewModel(AllTag, !IsHistoryMode && _activeTag == AllTag));
        bool activeStillExists = _activeTag == AllTag;
        foreach (var tag in _store.Tags())
        {
            bool isActive = string.Equals(tag, _activeTag, StringComparison.OrdinalIgnoreCase);
            activeStillExists |= isActive;
            Tags.Add(new TagChipViewModel(tag, !IsHistoryMode && isActive));
        }
        // e.g. the last snippet with the active tag was deleted
        if (!activeStillExists)
        {
            _activeTag = AllTag;
            if (!IsHistoryMode) ActivateTag(AllTag);
        }
    }

    [RelayCommand]
    private void SelectTag(TagChipViewModel chip)
    {
        // The History chip switches what the list shows; every other chip filters it.
        // Both leave the search box empty, since a search spans whatever the current mode
        // holds and the chips must always describe what the list is actually doing.
        if (chip.IsMode) ShowHistory();
        else ShowMode(history: false, chip.Name);
    }

    /// <summary>
    /// Switches the list to the clipboard history. Public because a global hotkey summons
    /// this view directly, without any chip being clicked. No-op where there is no history.
    /// </summary>
    public void ShowHistory()
    {
        if (!HasHistory) return;
        ShowMode(history: true, HistoryTag);
    }

    /// <summary>Switches the list back to saved snippets, showing every tag.</summary>
    public void ShowSnippets() => ShowMode(history: false, AllTag);

    private void ShowMode(bool history, string tag)
    {
        CloseCommands(restore: false); // the MRU belongs to the snippet command line
        IsHistoryMode = history;
        ActivateTag(tag);
        // A filter typed against snippets means nothing against clips, and vice versa.
        FilterText = "";
        Refresh(); // FilterText may already have been empty, so nothing fired above
    }

    /// <summary>Makes <paramref name="tag"/> the active filter and moves the chip highlight to it.</summary>
    private void ActivateTag(string tag)
    {
        if (tag != HistoryTag) _activeTag = tag;
        foreach (var t in Tags)
            t.IsSelected = string.Equals(t.Name, tag, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item's own action: one marked Execute runs, everything else is copied. Enter,
    /// a click and a tap all come through here, so what an item does is a property of
    /// the item rather than of how you reached it.
    /// </summary>
    [RelayCommand]
    private Task Activate(RowViewModel? row) =>
        row is { IsExecutable: true } ? Execute(row) : Copy(row);

    [RelayCommand]
    private Task ActivateSelected() => Activate(SelectedSnippet);

    [RelayCommand]
    private async Task Copy(RowViewModel? row)
    {
        CloseCommands(restore: false); // whatever route got here, the line has been chosen

        CopyPayload payload;
        if (row is SnippetViewModel snippet)
        {
            // Local defines first, then macros, which is the order the execute path
            // resolves them in and for the same reason: a %ws% that arrives through %C%
            // is a value off the clipboard rather than a name the item asked to have
            // resolved.
            var template = KlippyVariables.Current.Expand(snippet.Model.Content);

            // Macros resolve on the way to the clipboard, so what lands there is the
            // expansion — a half-typed invocation must never paste as "%P%".
            var content = await ResolveMacrosAsync(template, snippet.Arguments);
            payload = RichTextClipboard.BuildPayload(content, snippet.Model.IsMarkdown);
        }
        // A clip keeps whatever flavours it was captured with, so pasting it back gives
        // what the original copy would have — real files into Explorer, a picture into
        // an image editor, formatting into a rich-text box.
        else if (row is ClipViewModel clip) payload = BuildClipPayload(clip);
        else return;

        if (ClipboardWriter is { } write)
            await write(payload);

        MarkUsed(row);
        RecordCommand();

        ShowToast();
        Copied?.Invoke();

        // Read per copy rather than cached: the Settings overlay writes straight through
        // to the same instance, so a toggle applies to the very next copy.
        bool close = row is ClipViewModel
            ? _prefs.CloseAfterClipboardCopy
            : _prefs.CloseAfterSnippetCopy;
        if (close) CloseRequested?.Invoke();
    }

    /// <summary>
    /// Runs the item: its link opens in the default browser, or the script or
    /// application it names starts, with the same macros a copy would have resolved.
    /// Reached by triggering an item marked Execute.
    ///
    /// The item's stored text goes to the execution engine together with the typed
    /// arguments, rather than the expansion a copy would make — only the engine knows
    /// whether a value is about to land in a URL's query string or in a script's
    /// argument list, and those want it escaped differently.
    /// </summary>
    [RelayCommand]
    private async Task Execute(RowViewModel? row)
    {
        if (row is null) return;

        CloseCommands(restore: false);

        if (Executor is not { } run)
        {
            // Nothing wired up to run things: say so rather than leaving Enter looking
            // broken on an item that is marked to run.
            ShowToast(CannotExecuteHere, isError: true);
            return;
        }

        var text = row.Template;

        // The variables file sits in front of the machine's own environment, so %ws% names
        // WebStorm on the run path exactly as %LOCALAPPDATA% names a folder. Handing it to
        // the policy rather than expanding here keeps the first-word-only rule: an
        // argument's percent signs stay the user's own.
        var plan = WithPreferences(ExecutionPolicy.Plan(
            text,
            row.Arguments,
            await ReadClipboardForAsync(text),
            environment: KlippyVariables.Current.Ahead(EnvironmentProbe.Real)));

        if (plan.Kind == ExecutionKind.None)
        {
            ShowToast(plan.Problem, isError: true);
            return;
        }

        var result = await run(plan);
        ShowToast(result.Message, isError: !result.Started);
        if (!result.Started) return;

        MarkUsed(row);
        RecordCommand();
        Copied?.Invoke();

        // Executing is a launcher gesture: the browser or the script is where you are
        // going next, so Klippy gets out of the way — the copy preferences are about
        // staying put to copy a second thing, which does not apply here.
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private Task ExecuteSelected() => Execute(SelectedSnippet);

    /// <summary>
    /// Stamps the preferences about *how* to run onto a plan the policy has just worked
    /// out from what to run. Read per run rather than cached, as the copy preferences are:
    /// the Settings overlay writes through to the same instance, so a toggle applies to
    /// the very next Enter.
    /// </summary>
    private ExecutionPlan WithPreferences(ExecutionPlan plan) =>
        plan with { PullFirst = _prefs.ExecutePullFirst };

    /// <summary>Fills in an item's macros, reading the clipboard only if it carries a %C%.</summary>
    private async Task<string> ResolveMacrosAsync(string text, IReadOnlyList<string> arguments)
    {
        if (!Macros.IsPresent(text)) return text;
        return Macros.Expand(text, arguments, await ReadClipboardForAsync(text));
    }

    /// <summary>
    /// The clipboard's text when <paramref name="text"/> needs it, null otherwise — which
    /// leaves any %C% as written rather than silently emptying it.
    /// </summary>
    private async Task<string?> ReadClipboardForAsync(string text)
    {
        if (!Macros.UsesClipboard(text) || ClipboardReader is not { } read) return null;

        // An empty clipboard is an empty expansion, not a missing one.
        return await read() ?? "";
    }

    /// <summary>Bumps recency, so what you just used ranks first next time.</summary>
    private void MarkUsed(RowViewModel row)
    {
        if (row is SnippetViewModel snippet) _store.MarkUsed(snippet.Model);
        // Re-using a clip promotes it back to the top, exactly as capture would — and has
        // to say so, since Klippy's own clipboard writes are never captured.
        else if (row is ClipViewModel clip) _history?.MarkUsed(clip.Model.Id);
    }

    private CopyPayload BuildClipPayload(ClipViewModel clip) => clip.Model.Kind switch
    {
        ClipKind.Files => new CopyPayload(clip.Model.Text, null) { Files = clip.Model.Files },
        ClipKind.Image => new CopyPayload("", null)
        {
            Image = _history?.TryLoadImage(clip.Model),
            ImageFormat = clip.Model.BlobFormat,
        },
        _ => new CopyPayload(clip.Model.Text, clip.Model.Html),
    };

    /// <summary>
    /// Whether one of the matching rows <em>is</em> what was typed rather than merely
    /// mentioning it — by the line as typed, or by what that line resolved to, so
    /// <c>%APPDATA%</c> and the folder it expands to are the same request.
    /// </summary>
    private bool AnItemIsTheLine(ExecutionPlan plan)
    {
        var typed = UnmatchedSearch.Unquote(FilterText.Trim());

        foreach (var row in Filtered)
            if (Names(row.Label, typed) || Names(row.Content, typed)
                || Names(row.Label, plan.Target) || Names(row.Content, plan.Target))
                return true;

        return false;
    }

    private static bool Names(string? text, string wanted) =>
        wanted.Length > 0 && string.Equals(text?.Trim(), wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the offer, or puts a machine control up for confirmation first.
    ///
    /// Only ever reachable while <see cref="Offer"/> is set, which is only while the search
    /// matched nothing — so this can never fire instead of a copy.
    /// </summary>
    [RelayCommand]
    private Task RunOffer()
    {
        if (Offer is not { } offer) return Task.CompletedTask;

        if (!offer.NeedsConfirmation(_prefs)) return Run(offer);

        PendingOffer = offer;
        return Task.CompletedTask;
    }

    /// <summary>Goes ahead with a machine control the user has confirmed.</summary>
    [RelayCommand]
    private Task ConfirmOffer()
    {
        if (PendingOffer is not { } offer) return Task.CompletedTask;
        PendingOffer = null;
        return Run(offer);
    }

    [RelayCommand]
    private void CancelOffer() => PendingOffer = null;

    /// <summary>
    /// Asks to close Klippy, from the footer link rather than the typed word — the route
    /// for a pointer already down there, and the one that still works when a snippet
    /// called "quit" has taken the word.
    ///
    /// It puts up the same confirmation the offer does rather than a second dialog of its
    /// own: one question, however it was asked.
    /// </summary>
    [RelayCommand]
    private void RequestQuit()
    {
        if (CanQuit) PendingOffer = OfferViewModel.Quit();
    }

    /// <summary>
    /// Hands the offer's plan to the same engine an item marked Execute goes through, and
    /// reports in the same toast. The only difference is where the plan came from.
    ///
    /// Quit is the one offer with no plan to hand over: it is Klippy closing rather than
    /// Klippy starting something, so it turns back here and never reaches the launcher.
    /// </summary>
    private async Task Run(OfferViewModel offer)
    {
        CloseCommands(restore: false);

        if (offer.IsQuit)
        {
            QuitRequested?.Invoke();
            return;
        }

        if (Executor is not { } run)
        {
            ShowToast(CannotExecuteHere, isError: true);
            return;
        }

        var result = await run(WithPreferences(offer.Plan));
        ShowToast(result.Message, isError: !result.Started);
        if (!result.Started) return; // the window stays up, or the message is never read

        RecordCommand();

        // Executing is a launcher gesture: whatever just started is where the user is
        // going next, and for a machine control there is nothing left here to look at.
        CloseRequested?.Invoke();
    }

    /// <summary>Keeps a clip out of the history's eviction, or releases it.</summary>
    [RelayCommand]
    private void TogglePin(ClipViewModel? row)
    {
        if (row is null || _history is null) return;
        _history.SetPinned(row.Model.Id, !row.Model.IsPinned);
        Refresh();
    }

    /// <summary>
    /// Drops a clip. No confirmation, unlike deleting a snippet: a clip is transient by
    /// nature and the next copy makes another, so the gesture does not deserve a dialog.
    /// </summary>
    [RelayCommand]
    private void DeleteClip(ClipViewModel? row)
    {
        if (row is null || _history is null) return;
        _history.Remove(row.Model.Id);
        Refresh();
    }

    /// <summary>Empties the history, keeping pinned clips.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        _history?.Clear();
        Refresh();
    }

    /// <summary>
    /// Opens the snippet editor prefilled from a clip — the bridge between the two halves
    /// of the app, and the reason a clipboard history belongs in a snippet manager at all.
    /// The clip stays in the history; saving creates a snippet beside it.
    /// </summary>
    [RelayCommand]
    private void PromoteToSnippet(ClipViewModel? row)
    {
        // A snippet is text, so a picture has nothing to promote into one. File clips do:
        // their paths are the text.
        if (row is null || row.Model.Kind == ClipKind.Image) return;

        var editor = NewEditor(null, title: "New snippet from clip");
        editor.Label = row.Label;
        editor.Content = row.Model.Text;
        editor.IsMarkdown = false; // captured text is not known to be Markdown
        Editor = editor;
    }

    [RelayCommand]
    private Task CopySelected() => Copy(SelectedSnippet);

    /// <summary>
    /// Moves the selection through the list — and, where there is an offer standing above
    /// it, on and off that too. The offer is index -1: the up arrow has to be able to get
    /// back to it, or looking at what else matched would put the only thing Enter was going
    /// to run permanently out of reach.
    /// </summary>
    public void MoveSelection(int delta)
    {
        if (Filtered.Count == 0) return;

        int floor = Offer is null ? 0 : -1;
        int index = SelectedSnippet is null ? -1 : Filtered.IndexOf(SelectedSnippet);
        index = Math.Clamp(index + delta, floor, Filtered.Count - 1);
        SelectedSnippet = index < 0 ? null : Filtered[index];
    }

    // ---- the command MRU ----
    //
    // One pair of arrow keys, two lists that could want them. The rule is that the MRU
    // has them only while it is open, and it is only open when it has something to say:
    // a down arrow on an empty box, or a typed line that is the start of a command run
    // before. Anything else leaves ↑/↓ to the snippet list, where they have always been.

    /// <summary>
    /// Down or up: the MRU while it is open, otherwise the list — and, from an empty
    /// search box, a down arrow opens the MRU rather than stepping past the first row,
    /// since an unfiltered list has nothing to step through that recency has not already
    /// put at the top.
    /// </summary>
    public void Navigate(int delta)
    {
        if (IsCommandsOpen)
        {
            MoveCommandSelection(delta);
            return;
        }

        if (delta > 0 && FilterText.Length == 0 && OpenCommands(selectFirst: true)) return;

        MoveSelection(delta);
    }

    /// <summary>
    /// Offers the commands the typed line could still become. Returns false when there is
    /// nothing to offer — no MRU, the wrong list, or nothing matching — leaving the
    /// keystroke to whatever would have had it.
    /// </summary>
    /// <param name="selectFirst">
    /// Whether to land on the newest command straight away. True for the down arrow,
    /// which is one gesture meaning "open and browse"; false while typing, where
    /// selecting something would write it into the box the user is still typing in.
    /// </param>
    private bool OpenCommands(bool selectFirst)
    {
        // A filter typed against clips is not a command, and the history has its own
        // kind of recall — the clips themselves.
        if (_commands is not { IsEnabled: true } || IsHistoryMode) return false;

        var matches = _commands.Match(FilterText);
        if (matches.Count == 0)
        {
            CloseCommands(restore: false); // e.g. one more character ruled the last one out
            return false;
        }

        _commandStem = FilterText;
        Commands.Clear();
        foreach (var command in matches) Commands.Add(command);

        IsCommandsOpen = true;
        SelectedCommand = selectFirst ? Commands[0] : null;
        return true;
    }

    /// <summary>
    /// Puts the MRU away. <paramref name="restore"/> puts back what was typed before
    /// browsing started — for leaving the list empty-handed, not for taking a command
    /// from it.
    /// </summary>
    private void CloseCommands(bool restore)
    {
        if (!IsCommandsOpen) return;

        IsCommandsOpen = false;
        SelectedCommand = null; // ahead of the restore: a null selection writes nothing
        Commands.Clear();

        if (restore && FilterText != _commandStem) SetFilterTextInternally(_commandStem);
        _commandStem = "";
    }

    private void MoveCommandSelection(int delta)
    {
        int index = SelectedCommand is null ? -1 : Commands.IndexOf(SelectedCommand);
        int next = index + delta;

        // Up past the top leaves the MRU. That is the way back to the list — and to the
        // line that was being typed — for anyone who opened it by accident.
        if (next < 0)
        {
            CloseCommands(restore: true);
            return;
        }

        SelectedCommand = Commands[Math.Min(next, Commands.Count - 1)];
    }

    /// <summary>
    /// Takes the command being browsed as the line to work with: the text stays in the
    /// box and the MRU closes. What a click on one does, and the second half of what
    /// Enter does — the first being whatever the row underneath is now pointing at.
    /// </summary>
    public void AcceptCommand() => CloseCommands(restore: false);

    partial void OnSelectedCommandChanged(string? value)
    {
        // Browsing shows the command in the search box, and so on the row underneath:
        // what Enter is about to do is visible before it is pressed, exactly as it is
        // while typing an invocation by hand.
        if (value is not null) SetFilterTextInternally(value);
    }

    /// <summary>
    /// Remembers the line that did this, so the next one like it can be recalled rather
    /// than retyped. Called after the copy or the run, not before: a command is a line
    /// that did something.
    /// </summary>
    private void RecordCommand()
    {
        if (IsHistoryMode || _commands is null) return;
        if (_commands.Record(FilterText))
            OnPropertyChanged(nameof(SearchWatermark)); // the box can now offer ↓ recent
    }

    [RelayCommand]
    private void New() => Editor = NewEditor(null);

    [RelayCommand]
    private void Edit(SnippetViewModel row) =>
        Editor = NewEditor(row.Model, duplicate: DuplicateFromEditor);

    /// <summary>
    /// Builds an editor. Every editor comes through here so that all of them — new, edit,
    /// duplicate, promote — are handed the tags already in use to offer under the TAG field.
    /// The tags are read per open, so one added in the last edit is on offer in the next.
    /// </summary>
    private EditorViewModel NewEditor(Snippet? existing, Action? duplicate = null, string? title = null) =>
        new(existing, SaveSnippet, CloseEditor, duplicate, title, _store.Tags());

    /// <summary>
    /// The keyboard's way into the row actions, which belong to snippets: the selection
    /// may be a clip, and a clip has neither an editor nor a duplicate. Handing one to a
    /// command that only takes snippets throws, so the keyboard asks here instead.
    /// </summary>
    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedSnippet is SnippetViewModel row) Edit(row);
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (SelectedSnippet is SnippetViewModel row) Duplicate(row);
    }

    /// <summary>Opens a new-snippet editor prefilled from an existing row. Saving creates a copy.</summary>
    [RelayCommand]
    private void Duplicate(SnippetViewModel? row)
    {
        if (row is null) return;
        // The snippet as stored, not what the row is currently showing: duplicating
        // during an invocation must copy the %P%, not the word it stands for.
        Editor = CreateDuplicateEditor(row.Label, row.Model.Content, row.Tag, row.IsMarkdown);
    }

    // Branch the open editor into a duplicate, carrying over any unsaved field edits.
    private void DuplicateFromEditor()
    {
        if (Editor is { } editor)
            Editor = CreateDuplicateEditor(editor.Label, editor.Content, editor.Tag, editor.IsMarkdown);
    }

    private EditorViewModel CreateDuplicateEditor(string label, string content, string tag, bool isMarkdown)
    {
        var editor = NewEditor(null, title: "Duplicate snippet");
        editor.Label = label + " (copy)";
        editor.Content = content;
        editor.Tag = tag;
        editor.IsMarkdown = isMarkdown;
        // deliberately no quick-code: two snippets must not answer to the same code
        return editor;
    }

    private void SaveSnippet(Snippet snippet, bool isNew)
    {
        if (isNew)
            _store.Add(snippet);
        else
        {
            _store.Update(snippet);
            if (_rowCache.TryGetValue(snippet.Id, out var row))
                row.NotifyModelChanged();
        }
        Editor = null;
        RebuildTags();
        Refresh();
    }

    private void CloseEditor() => Editor = null;

    [RelayCommand]
    private void RequestDelete(SnippetViewModel row) => DeleteTarget = row;

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (DeleteTarget is null) return;
        _store.Delete(DeleteTarget.Model.Id);
        _rowCache.Remove(DeleteTarget.Model.Id);
        DeleteTarget = null;
        RebuildTags();
        Refresh();
    }

    [RelayCommand]
    private void CancelDelete() => DeleteTarget = null;

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    [RelayCommand]
    private void TogglePreview() => IsPreviewOpen = !IsPreviewOpen;

    [RelayCommand]
    private void OpenTransfer() => Transfer = new TransferViewModel(
        _store,
        _activeTag,
        dataChanged: () =>
        {
            // Imports replace snippet instances, so cached row VMs would go stale.
            _rowCache.Clear();
            RebuildTags();
            Refresh();
        },
        close: () => Transfer = null);

    [RelayCommand]
    private void OpenSettings() =>
        Settings = new SettingsViewModel(_prefs, close: () => Settings = null, canExecuteUnmatched: CanExecuteUnmatched);

    /// <summary>
    /// Esc: close whichever overlay is open, then the MRU, then clear the filter. Returns
    /// false if there was nothing to do.
    /// </summary>
    public bool HandleEscape()
    {
        if (Editor is not null) { Editor = null; return true; }
        if (DeleteTarget is not null) { DeleteTarget = null; return true; }
        if (PendingOffer is not null) { PendingOffer = null; return true; }
        if (Transfer is not null) { Transfer = null; return true; }
        if (Settings is not null) { Settings = null; return true; }
        // Before the filter: leaving the MRU puts back what was being typed, and that
        // line is usually the thing you wanted to keep.
        if (IsCommandsOpen) { CloseCommands(restore: true); return true; }
        if (FilterText.Length > 0) { FilterText = ""; return true; }
        return false;
    }

    private const string CopiedToast = "Copied to clipboard";

    private const string CannotExecuteHere = "Klippy cannot run items on this device.";

    private void ShowToast(string? text = null, bool isError = false)
    {
        _toastTimer.Stop();
        ToastText = text is { Length: > 0 } ? text : CopiedToast;
        IsToastError = isError;
        // A confirmation is glanced at; something that went wrong has to be read.
        _toastTimer.Interval = TimeSpan.FromMilliseconds(isError ? 4000 : 1500);
        IsToastVisible = true;
        _toastTimer.Start();
    }
}
