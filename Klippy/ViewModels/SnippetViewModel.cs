using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a snippet; one instance per snippet, reused across filtering.</summary>
public partial class SnippetViewModel : ViewModelBase
{
    public Snippet Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    private bool _isExpanded;

    public SnippetViewModel(Snippet model) => Model = model;

    public string Label => Model.Label;
    public string Content => Model.Content;
    public string Tag => Model.Tag;
    public string QuickCode => Model.QuickCode;
    public bool HasQuickCode => Model.QuickCode.Length > 0;
    public bool IsMultiline => Model.Content.Contains('\n');
    public bool CanExpand => IsMultiline && !IsExpanded;

    /// <summary>First line of the content, for the one-line ellipsized preview.</summary>
    public string Preview
    {
        get
        {
            int nl = Model.Content.IndexOf('\n');
            var line = nl < 0 ? Model.Content : Model.Content[..nl];
            return line.TrimEnd('\r');
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Re-raises change notifications after the underlying model was edited.</summary>
    public void NotifyModelChanged()
    {
        OnPropertyChanged(string.Empty);
    }
}
