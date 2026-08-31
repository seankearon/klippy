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
