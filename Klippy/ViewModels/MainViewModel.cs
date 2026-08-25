using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private bool _isSelected;

    public TagChipViewModel(string name, bool isSelected = false)
    {
        Name = name;
        _isSelected = isSelected;
    }
}

public partial class MainViewModel : ViewModelBase
{
    public const string AllTag = "All";

    private readonly SnippetStore _store;
    private readonly Dictionary<Guid, SnippetViewModel> _rowCache = new();
    private readonly DispatcherTimer _toastTimer;

    public ObservableCollection<SnippetViewModel> Filtered { get; } = new();
    public ObservableCollection<TagChipViewModel> Tags { get; } = new();

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private SnippetViewModel? _selectedSnippet;

    [ObservableProperty]
    private EditorViewModel? _editor;

    [ObservableProperty]
    private SnippetViewModel? _deleteTarget;

    [ObservableProperty]
    private TransferViewModel? _transfer;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _snippetCountText = "";

    private string _activeTag = AllTag;

    /// <summary>Set by the view; writes a copy payload to the platform clipboard.</summary>
    public Func<CopyPayload, Task>? ClipboardWriter { get; set; }

    public string KeyHints { get; } = OperatingSystem.IsMacOS()
        ? "↑↓ navigate  ↵ copy  ⌘N new  ⌘F filter"
        : "↑↓ navigate  ↵ copy  Ctrl+N new  Ctrl+F filter";

    public string SearchKeyHint { get; } = OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl F";

    public MainViewModel() : this(new SnippetStore()) { }

    public MainViewModel(SnippetStore store)
    {
        _store = store;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); IsToastVisible = false; };
        RebuildTags();
        Refresh();
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    /// <summary>Re-runs the search and updates the visible rows, keeping row VMs (and their expanded state) stable.</summary>
    private void Refresh()
    {
        var results = _store.Search(FilterText);

        Filtered.Clear();
        foreach (var entry in results)
        {
            if (_activeTag != AllTag && !string.Equals(entry.Snippet.Tag, _activeTag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_rowCache.TryGetValue(entry.Snippet.Id, out var row))
                _rowCache[entry.Snippet.Id] = row = new SnippetViewModel(entry.Snippet);
            Filtered.Add(row);
        }

        SelectedSnippet = Filtered.Count > 0 ? Filtered[0] : null;
        SnippetCountText = Filtered.Count == 1 ? "1 snippet" : $"{Filtered.Count} snippets";
    }

    private void RebuildTags()
    {
        Tags.Clear();
        Tags.Add(new TagChipViewModel(AllTag, _activeTag == AllTag));
        bool activeStillExists = _activeTag == AllTag;
        foreach (var tag in _store.Tags())
        {
            bool isActive = string.Equals(tag, _activeTag, StringComparison.OrdinalIgnoreCase);
            activeStillExists |= isActive;
            Tags.Add(new TagChipViewModel(tag, isActive));
        }
        // e.g. the last snippet with the active tag was deleted
        if (!activeStillExists)
        {
            _activeTag = AllTag;
            Tags[0].IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectTag(TagChipViewModel chip)
    {
        _activeTag = chip.Name;
        foreach (var t in Tags)
            t.IsSelected = ReferenceEquals(t, chip);
        Refresh();
    }

    [RelayCommand]
    private async Task Copy(SnippetViewModel? row)
    {
        if (row is null) return;
        if (ClipboardWriter is { } write)
            await write(RichTextClipboard.BuildPayload(row.Model));
        _store.MarkUsed(row.Model);
        ShowToast();
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

    /// <summary>Opens a new-snippet editor prefilled from an existing row. Saving creates a copy.</summary>
    [RelayCommand]
    private void Duplicate(SnippetViewModel? row)
    {
        if (row is null) return;
        Editor = CreateDuplicateEditor(row.Label, row.Content, row.Tag, row.IsMarkdown);
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

    /// <summary>Esc: close whichever overlay is open, else clear the filter. Returns false if there was nothing to do.</summary>
    public bool HandleEscape()
    {
        if (Editor is not null) { Editor = null; return true; }
        if (DeleteTarget is not null) { DeleteTarget = null; return true; }
        if (Transfer is not null) { Transfer = null; return true; }
        if (FilterText.Length > 0) { FilterText = ""; return true; }
        return false;
    }

    private void ShowToast()
    {
        _toastTimer.Stop();
        IsToastVisible = true;
        _toastTimer.Start();
    }
}
