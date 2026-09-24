using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>Backs the new/edit snippet overlay. Works on a copy; applies on Save.</summary>
public partial class EditorViewModel : ViewModelBase
{
    private readonly Snippet? _existing;
    private readonly Action<Snippet, bool> _save;
    private readonly Action _close;
    private readonly Action? _duplicate;
    private readonly string? _title;
    private readonly List<string> _knownTags = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _label = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(ExecuteHint))]
    [NotifyPropertyChangedFor(nameof(ExecuteHintIsWarning))]
    private string _content = "";

    [ObservableProperty]
    private string _tag = "";

    [ObservableProperty]
    private string _quickCode = "";

    /// <summary>Marks the content as Markdown so copies carry rich formatting.</summary>
    [ObservableProperty]
    private bool _isMarkdown;

    /// <summary>Marks the snippet as one to run rather than copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecuteHint))]
    [NotifyPropertyChangedFor(nameof(ExecuteHintIsWarning))]
    private bool _isExecutable;

    private static readonly string CopyKeyHint = OperatingSystem.IsMacOS() ? "⌘+Enter" : "Ctrl+Enter";

    /// <summary>
    /// What the Execute marker will mean for this snippet, said while it is being
    /// ticked rather than as a toast after the fact — marking something to run only to
    /// find it cannot be is the mistake worth catching here.
    /// </summary>
    public string ExecuteHint =>
        !IsExecutable ? "Copied to the clipboard when triggered, as usual"
        : ExecutionPolicy.LooksExecutable(Content)
            ? $"Opened or run when triggered — {CopyKeyHint} still copies it"
            : "This is not a link, a document, an application or a script Klippy can run, so triggering it will say so";

    public bool ExecuteHintIsWarning => IsExecutable && !ExecutionPolicy.LooksExecutable(Content);

    /// <summary>
    /// The tags already in use, offered as chips under the TAG field so a snippet joins
    /// one that exists instead of quietly coining "wrk" beside "work". Which of them are
    /// on offer follows what the field holds — see <see cref="RefreshTagSuggestions"/>.
    /// </summary>
    public ObservableCollection<TagChipViewModel> TagSuggestions { get; } = new();

    /// <summary>
    /// False only when no snippet carries a tag yet, which is the one case where there is
    /// nothing to offer; the row is then hidden rather than left as an empty gap. The
    /// narrowing below never empties the list, so this does not change while the dialog is open.
    /// </summary>
    public bool HasTagSuggestions => _knownTags.Count > 0;

    public bool IsNew => _existing is null;
    public string Title => _title ?? (IsNew ? "New snippet" : "Edit snippet");

    /// <summary>Whether this editor can branch into a duplicate (only when editing an existing snippet).</summary>
    public bool HasDuplicate => _duplicate is not null;

    /// <param name="knownTags">Tags already in use, offered under the TAG field.</param>
    public EditorViewModel(Snippet? existing, Action<Snippet, bool> save, Action close,
        Action? duplicate = null, string? title = null, IEnumerable<string>? knownTags = null)
    {
        _existing = existing;
        _save = save;
        _close = close;
        _duplicate = duplicate;
        _title = title;
        if (knownTags is not null) _knownTags.AddRange(knownTags);
        if (existing is not null)
        {
            _label = existing.Label;
            _content = existing.Content;
            _tag = existing.Tag;
            _quickCode = existing.QuickCode;
            _isMarkdown = existing.IsMarkdown;
            _isExecutable = existing.IsExecutable;
        }
        RefreshTagSuggestions();
    }

    // The constructor writes the backing field, so this covers every later change: typing
    // in the TAG box, a chip click, and the tag a duplicate or promoted clip is given.
    partial void OnTagChanged(string value) => RefreshTagSuggestions();

    /// <summary>
    /// Works out which existing tags to offer, and highlights the one the field currently
    /// holds. A part-typed entry narrows the list to the tags it could still become; an
    /// empty field or a complete tag shows the whole set, so the next click can move the
    /// snippet elsewhere rather than stranding it on its own tag. A part-typed entry that
    /// matches nothing shows the whole set too — there is nothing to narrow to, and the
    /// tags are still worth offering.
    /// </summary>
    private void RefreshTagSuggestions()
    {
        string typed = Tag.Trim();

        var wanted = new List<string>();
        if (typed.Length > 0 && !IsKnownTag(typed))
            foreach (var tag in _knownTags)
                if (tag.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    wanted.Add(tag);
        if (wanted.Count == 0)
            wanted.AddRange(_knownTags);

        // Rebuild only when the set really changes, so the chips do not churn under the
        // pointer on every keystroke.
        if (!SameChips(wanted))
        {
            TagSuggestions.Clear();
            foreach (var tag in wanted)
                TagSuggestions.Add(new TagChipViewModel(tag));
        }

        foreach (var chip in TagSuggestions)
            chip.IsSelected = string.Equals(chip.Name, typed, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsKnownTag(string tag)
    {
        foreach (var known in _knownTags)
            if (string.Equals(known, tag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private bool SameChips(List<string> tags)
    {
        if (tags.Count != TagSuggestions.Count) return false;
        for (int i = 0; i < tags.Count; i++)
            if (!string.Equals(tags[i], TagSuggestions[i].Name, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Puts an existing tag in the field — or takes it back out: clicking the tag the
    /// snippet already carries clears it, which is how a tagged snippet goes back to
    /// untagged without reaching for the keyboard.
    /// </summary>
    [RelayCommand]
    private void SelectTag(TagChipViewModel chip) => Tag = chip.IsSelected ? "" : chip.Name;

    private bool CanSave() => Label.Trim().Length > 0 && Content.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var snippet = _existing ?? new Snippet();
        snippet.Label = Label.Trim();
        snippet.Content = Content;
        snippet.Tag = Tag.Trim().ToLowerInvariant();
        snippet.QuickCode = QuickCode.Trim().ToLowerInvariant();
        snippet.IsMarkdown = IsMarkdown;
        snippet.IsExecutable = IsExecutable;
        _save(snippet, IsNew);
    }

    [RelayCommand]
    private void Cancel() => _close();

    [RelayCommand]
    private void Duplicate() => _duplicate?.Invoke();
}
