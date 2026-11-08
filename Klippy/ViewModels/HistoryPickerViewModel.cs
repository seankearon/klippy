using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// The clipboard history, offered over whatever overlay has the window — the editor, most often — when the
/// history key is pressed there. The main list is behind that overlay and switching it would show nothing, so
/// this is a list of its own: filter, pick, and the clip is on the clipboard with the overlay still waiting
/// underneath, ready for the paste.
/// </summary>
public partial class HistoryPickerViewModel : ViewModelBase
{
    private readonly ClipHistoryStore _history;

    /// <summary>Rows kept for as long as the picker is up, so filtering back and forth decodes no thumbnail twice.</summary>
    private readonly Dictionary<Guid, ClipViewModel> _rows = new();

    public HistoryPickerViewModel(ClipHistoryStore history)
    {
        _history = history;
        Refresh();
    }

    /// <summary>The clips matching <see cref="FilterText"/>, newest first.</summary>
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    [ObservableProperty]
    private string _filterText = "";

    /// <summary>The clip Enter will copy. The first match whenever the filter changes.</summary>
    [ObservableProperty]
    private ClipViewModel? _selected;

    [ObservableProperty]
    private string _countText = "";

    partial void OnFilterTextChanged(string value) => Refresh();

    private void Refresh()
    {
        Clips.Clear();
        foreach (var entry in _history.Search(FilterText))
        {
            if (!_rows.TryGetValue(entry.Item.Id, out var row))
                _rows[entry.Item.Id] = row = new ClipViewModel(entry.Item, _history.TryLoadImage);
            Clips.Add(row);
        }

        Selected = Clips.Count > 0 ? Clips[0] : null;
        CountText = Clips.Count == 1 ? "1 clip" : $"{Clips.Count} clips";
    }

    /// <summary>Moves the selection by <paramref name="delta"/>, stopping at either end.</summary>
    public void Navigate(int delta)
    {
        if (Clips.Count == 0) return;

        var at = Selected is null ? -1 : Clips.IndexOf(Selected);
        Selected = Clips[Math.Clamp(at + delta, 0, Clips.Count - 1)];
    }
}
