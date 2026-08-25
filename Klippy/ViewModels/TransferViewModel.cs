using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// Backs the Export / Import overlay. Export writes the whole set or one tag;
/// import previews a picked file (count + its tags) and then merges either
/// everything or just one tag into the store. The file dialogs themselves live
/// in the view (they need a TopLevel); this VM only sees streams.
/// </summary>
public partial class TransferViewModel : ViewModelBase
{
    private readonly SnippetStore _store;
    private readonly Action _dataChanged;
    private readonly Action _close;

    private string _exportTag = MainViewModel.AllTag;
    private string _importTag = MainViewModel.AllTag;
    private List<Snippet>? _pending;

    public ObservableCollection<TagChipViewModel> ExportScope { get; } = new();
    public ObservableCollection<TagChipViewModel> ImportScope { get; } = new();

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _importSummary;

    [ObservableProperty]
    private bool _hasImportPreview;

    public TransferViewModel(SnippetStore store, string activeTag, Action dataChanged, Action close)
    {
        _store = store;
        _dataChanged = dataChanged;
        _close = close;

        // Default the export scope to the tag currently filtering the main list.
        ExportScope.Add(new TagChipViewModel(MainViewModel.AllTag, isSelected: true));
        foreach (var tag in store.Tags())
        {
            bool isActive = string.Equals(tag, activeTag, StringComparison.OrdinalIgnoreCase);
            ExportScope.Add(new TagChipViewModel(tag, isActive));
            if (isActive)
            {
                _exportTag = tag;
                ExportScope[0].IsSelected = false;
            }
        }
    }

    public string SuggestedFileName =>
        _exportTag == MainViewModel.AllTag ? "klippy-snippets.json" : $"klippy-{_exportTag}.json";

    [RelayCommand]
    private void SelectExportTag(TagChipViewModel chip)
    {
        _exportTag = chip.Name;
        foreach (var t in ExportScope)
            t.IsSelected = ReferenceEquals(t, chip);
    }

    [RelayCommand]
    private void SelectImportTag(TagChipViewModel chip)
    {
        _importTag = chip.Name;
        foreach (var t in ImportScope)
            t.IsSelected = ReferenceEquals(t, chip);
    }

    /// <summary>Writes the selected scope to <paramref name="destination"/>.</summary>
    public void ExportTo(Stream destination)
    {
        int count = _store.Export(destination, _exportTag == MainViewModel.AllTag ? null : _exportTag);
        StatusText = count == 1 ? "Exported 1 snippet" : $"Exported {count} snippets";
    }

    /// <summary>Parses a picked file and shows what it contains before anything is merged.</summary>
    public void LoadImportPreview(Stream source, string fileName)
    {
        var parsed = SnippetStore.TryParseSnippets(source);
        if (parsed is null || parsed.Count == 0)
        {
            _pending = null;
            HasImportPreview = false;
            StatusText = parsed is null
                ? "Couldn't read that file — it isn't a Klippy export."
                : "That file contains no snippets.";
            return;
        }

        _pending = parsed;
        _importTag = MainViewModel.AllTag;

        ImportScope.Clear();
        ImportScope.Add(new TagChipViewModel(MainViewModel.AllTag, isSelected: true));
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in parsed)
            if (!string.IsNullOrWhiteSpace(s.Tag))
                seen.Add(s.Tag.Trim().ToLowerInvariant());
        foreach (var tag in seen)
            ImportScope.Add(new TagChipViewModel(tag));

        ImportSummary = parsed.Count == 1 ? $"{fileName} · 1 snippet" : $"{fileName} · {parsed.Count} snippets";
        HasImportPreview = true;
        StatusText = null;
    }

    [RelayCommand]
    private void Import()
    {
        if (_pending is null) return;
        var (added, updated) = _store.Merge(_pending, _importTag == MainViewModel.AllTag ? null : _importTag);
        _pending = null;
        HasImportPreview = false;
        _dataChanged();
        StatusText = $"Imported {added} new · {updated} updated";
    }

    [RelayCommand]
    private void CancelImport()
    {
        _pending = null;
        HasImportPreview = false;
        StatusText = null;
    }

    [RelayCommand]
    private void Close() => _close();
}
