using System;
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
            : "This is not a link or a script Klippy can run, so triggering it will say so";

    public bool ExecuteHintIsWarning => IsExecutable && !ExecutionPolicy.LooksExecutable(Content);

    public bool IsNew => _existing is null;
    public string Title => _title ?? (IsNew ? "New snippet" : "Edit snippet");

    /// <summary>Whether this editor can branch into a duplicate (only when editing an existing snippet).</summary>
    public bool HasDuplicate => _duplicate is not null;

    public EditorViewModel(Snippet? existing, Action<Snippet, bool> save, Action close,
        Action? duplicate = null, string? title = null)
    {
        _existing = existing;
        _save = save;
        _close = close;
        _duplicate = duplicate;
        _title = title;
        if (existing is not null)
        {
            _label = existing.Label;
            _content = existing.Content;
            _tag = existing.Tag;
            _quickCode = existing.QuickCode;
            _isMarkdown = existing.IsMarkdown;
            _isExecutable = existing.IsExecutable;
        }
    }

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
