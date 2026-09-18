using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Klippy.ViewModels;

/// <summary>
/// What the main list can show: a saved snippet or a captured clip.
///
/// The two rows look different — one carries a quick-code and a tag, the other an age
/// and a source app — but the list, the keyboard selection, the preview pane and the
/// expand-in-place behaviour are all the same, so they share this much.
/// </summary>
public abstract partial class RowViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    private bool _isExpanded;

    /// <summary>Short name shown on the row.</summary>
    public abstract string Label { get; }

    /// <summary>Full text, shown when the row is expanded and in the preview pane.</summary>
    public abstract string Content { get; }

    public bool IsMultiline => Content.Contains('\n');

    public bool CanExpand => IsMultiline && !IsExpanded;

    /// <summary>
    /// The text as stored, macros and all. <see cref="Content"/> shows the row what it
    /// would expand to; executing works from the original, so the placeholders can be
    /// resolved for the target rather than for the screen.
    /// </summary>
    public virtual string Template => Content;

    /// <summary>
    /// Positional arguments typed after the quick-code, filling the item's <c>%P%</c>
    /// placeholders. Empty for everything but the row a quick-code invoked.
    /// </summary>
    public virtual IReadOnlyList<string> Arguments => Array.Empty<string>();

    /// <summary>
    /// Whether the item is marked to be run rather than copied. A property of the item
    /// itself, not of what its text happens to look like: Klippy never decides on its
    /// own that something should be run.
    /// </summary>
    public virtual bool IsExecutable => false;

    private static readonly string RunHint =
        OperatingSystem.IsMacOS() ? "↵ run · ⌘↵ copy" : "↵ run · Ctrl+↵ copy";

    /// <summary>
    /// What the selected row's badge offers. Enter does whatever the item is marked for;
    /// Ctrl/⌘+Enter always copies, which is the way to get a marked item's text.
    /// </summary>
    public string EnterHint => IsExecutable ? RunHint : "↵ copy";

    /// <summary>First line of the content, for the one-line ellipsized preview.</summary>
    public string Preview
    {
        get
        {
            int nl = Content.IndexOf('\n');
            var line = nl < 0 ? Content : Content[..nl];
            return line.TrimEnd('\r');
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Re-raises change notifications after the underlying model was edited.</summary>
    public void NotifyModelChanged() => OnPropertyChanged(string.Empty);
}
