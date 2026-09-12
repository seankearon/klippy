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

    private readonly SnippetStore _store;
    private readonly ClipHistoryStore? _history;
    private readonly AppSettings _prefs;
    private readonly Dictionary<Guid, SnippetViewModel> _rowCache = new();
    private readonly Dictionary<Guid, ClipViewModel> _clipCache = new();
    private readonly DispatcherTimer _toastTimer;

    public ObservableCollection<RowViewModel> Filtered { get; } = new();
    public ObservableCollection<TagChipViewModel> Tags { get; } = new();

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private RowViewModel? _selectedSnippet;

    [ObservableProperty]
    private EditorViewModel? _editor;

    [ObservableProperty]
    private SnippetViewModel? _deleteTarget;

    [ObservableProperty]
    private TransferViewModel? _transfer;

    [ObservableProperty]
    private SettingsViewModel? _settings;

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

    [ObservableProperty]
    private string _snippetCountText = "";

    /// <summary>Whether the list is showing captured clips instead of saved snippets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchWatermark))]
    private bool _isHistoryMode;

    /// <summary>The search box says what it is searching, since the list holds two different things.</summary>
    public string SearchWatermark => IsHistoryMode
        ? "Filter clipboard history"
        : "Filter snippets or type a quick-code";

    private string _activeTag = AllTag;

    /// <summary>The row a quick-code invocation is currently pointed at, if any.</summary>
    private SnippetViewModel? _invoked;

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

    /// <summary>Whether there is a clipboard history to switch to. False on mobile.</summary>
    public bool HasHistory => _history is not null;

    public MainViewModel() : this(new SnippetStore(), ClipboardHistory.Store) { }

    /// <param name="settings">Defaults to <see cref="AppSettings.Current"/>; passed in by tests.</param>
    public MainViewModel(SnippetStore store, ClipHistoryStore? history = null, AppSettings? settings = null)
    {
        _store = store;
        _history = history;
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
        Refresh();
    }

    /// <summary>Re-runs the search and updates the visible rows, keeping row VMs (and their expanded state) stable.</summary>
    private void Refresh()
    {
        if (IsHistoryMode) RefreshHistory();
        else RefreshSnippets();

        SelectedSnippet = Filtered.Count > 0 ? Filtered[0] : null;
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
        CopyPayload payload;
        if (row is SnippetViewModel snippet)
        {
            // Macros resolve on the way to the clipboard, so what lands there is the
            // expansion — a half-typed invocation must never paste as "%P%".
            var content = await ResolveMacrosAsync(snippet.Model.Content, snippet.Arguments);
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
    /// Runs the item: its link opens in the default browser, or the script it names
    /// runs, with the same macros a copy would have resolved. Reached by triggering an
    /// item marked Execute.
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

        if (Executor is not { } run)
        {
            // Nothing wired up to run things: say so rather than leaving Enter looking
            // broken on an item that is marked to run.
            ShowToast("Klippy cannot run items on this device.", isError: true);
            return;
        }

        var text = row.Template;
        var plan = ExecutionPolicy.Plan(text, row.Arguments, await ReadClipboardForAsync(text));

        if (plan.Kind == ExecutionKind.None)
        {
            ShowToast(plan.Problem, isError: true);
            return;
        }

        var result = await run(plan);
        ShowToast(result.Message, isError: !result.Started);
        if (!result.Started) return;

        MarkUsed(row);
        Copied?.Invoke();

        // Executing is a launcher gesture: the browser or the script is where you are
        // going next, so Klippy gets out of the way — the copy preferences are about
        // staying put to copy a second thing, which does not apply here.
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private Task ExecuteSelected() => Execute(SelectedSnippet);

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

        Editor = new EditorViewModel(null, SaveSnippet, CloseEditor, title: "New snippet from clip")
        {
            Label = row.Label,
            Content = row.Model.Text,
            IsMarkdown = false, // captured text is not known to be Markdown
        };
    }

    [RelayCommand]
    private Task CopySelected() => Copy(SelectedSnippet);

    public void MoveSelection(int delta)
    {
        if (Filtered.Count == 0) return;
        int index = SelectedSnippet is null ? -1 : Filtered.IndexOf(SelectedSnippet);
        index = Math.Clamp(index + delta, 0, Filtered.Count - 1);
        SelectedSnippet = Filtered[index];
    }

    [RelayCommand]
    private void New() => Editor = new EditorViewModel(null, SaveSnippet, CloseEditor);

    [RelayCommand]
    private void Edit(SnippetViewModel row) =>
        Editor = new EditorViewModel(row.Model, SaveSnippet, CloseEditor, duplicate: DuplicateFromEditor);

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

    private EditorViewModel CreateDuplicateEditor(string label, string content, string tag, bool isMarkdown) =>
        new(null, SaveSnippet, CloseEditor, title: "Duplicate snippet")
        {
            Label = label + " (copy)",
            Content = content,
            Tag = tag,
            IsMarkdown = isMarkdown,
            // deliberately no quick-code: two snippets must not answer to the same code
        };

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
        Settings = new SettingsViewModel(_prefs, close: () => Settings = null);

    /// <summary>Esc: close whichever overlay is open, else clear the filter. Returns false if there was nothing to do.</summary>
    public bool HandleEscape()
    {
        if (Editor is not null) { Editor = null; return true; }
        if (DeleteTarget is not null) { DeleteTarget = null; return true; }
        if (Transfer is not null) { Transfer = null; return true; }
        if (Settings is not null) { Settings = null; return true; }
        if (FilterText.Length > 0) { FilterText = ""; return true; }
        return false;
    }

    private const string CopiedToast = "Copied to clipboard";

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
