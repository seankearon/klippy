using System;
using System.IO;
using System.Linq;
using System.Text;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

public class TransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"klippy-transfer-{Guid.NewGuid():N}");

    private SnippetStore NewStore(string name = "store.json") =>
        new(Path.Combine(_dir, name), seedIfEmpty: false);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Snippet Make(string label, string tag, string content = "x") =>
        new() { Label = label, Tag = tag, Content = content };

    // ---- store primitives ----

    [Fact]
    public void Export_WholeSet_And_PerTag()
    {
        var store = NewStore();
        store.Add(Make("A", "work"));
        store.Add(Make("B", "dev"));
        store.Add(Make("C", "work"));

        using var all = new MemoryStream();
        Assert.Equal(3, store.Export(all));
        all.Position = 0;
        Assert.Equal(3, SnippetStore.TryParseSnippets(all)!.Count);

        using var work = new MemoryStream();
        Assert.Equal(2, store.Export(work, "work"));
        work.Position = 0;
        var parsed = SnippetStore.TryParseSnippets(work)!;
        Assert.All(parsed, s => Assert.Equal("work", s.Tag));
    }

    [Fact]
    public void TryParseSnippets_RejectsGarbage()
    {
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("{ not json !!"));
        Assert.Null(SnippetStore.TryParseSnippets(garbage));

        using var wrongShape = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}"));
        Assert.Null(SnippetStore.TryParseSnippets(wrongShape));
    }

    [Fact]
    public void Merge_AddsNewIds_ReplacesExistingIds()
    {
        var store = NewStore();
        var original = Make("Original", "work");
        store.Add(original);

        var edited = new Snippet { Id = original.Id, Label = "Edited", Tag = "work", Content = "y" };
        var fresh = Make("Fresh", "dev");

        var (added, updated) = store.Merge(new[] { edited, fresh });
        Assert.Equal(1, added);
        Assert.Equal(1, updated);

        var reloaded = NewStore();
        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded.Entries, e => e.Snippet.Label == "Edited");
        Assert.DoesNotContain(reloaded.Entries, e => e.Snippet.Label == "Original");
    }

    [Fact]
    public void Merge_WithTagFilter_OnlyTakesThatTag()
    {
        var store = NewStore();
        var (added, updated) = store.Merge(new[] { Make("A", "work"), Make("B", "dev") }, tag: "dev");
        Assert.Equal(1, added);
        Assert.Equal(0, updated);
        Assert.Equal("B", Assert.Single(store.Entries).Snippet.Label);
    }

    [Fact]
    public void Merge_NormalizesNullsAndMissingIds_SkipsEmpties()
    {
        var store = NewStore();
        var json = """[{"Label":"No id","Content":"x"},{"Label":null,"Content":null}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parsed = SnippetStore.TryParseSnippets(stream)!;

        var (added, _) = store.Merge(parsed);
        Assert.Equal(1, added);
        var snippet = Assert.Single(store.Entries).Snippet;
        Assert.NotEqual(Guid.Empty, snippet.Id);
        Assert.Equal("", snippet.Tag);
    }

    // ---- external identity (Source + ExternalId) ----

    [Fact]
    public void Merge_MatchesOnSourceAndExternalId_WhenIdDiffers()
    {
        var store = NewStore();
        store.Add(new Snippet { Label = "Old title", Content = "old", Source = "BoldDesk Aug 2026", ExternalId = "13" });

        // a re-export from the external system: same external record, brand new Guid
        var reimported = new Snippet { Label = "New title", Content = "new", Source = "BoldDesk Aug 2026", ExternalId = "13" };
        var (added, updated) = store.Merge(new[] { reimported });

        Assert.Equal(0, added);
        Assert.Equal(1, updated);
        var only = Assert.Single(store.Entries).Snippet;
        Assert.Equal("New title", only.Label);
    }

    [Fact]
    public void Merge_OnExternalMatch_KeepsKlippysOwnIdStable()
    {
        var store = NewStore();
        var original = new Snippet { Label = "a", Content = "x", Source = "BoldDesk Aug 2026", ExternalId = "7" };
        store.Add(original);

        store.Merge(new[] { new Snippet { Label = "b", Content = "y", Source = "BoldDesk Aug 2026", ExternalId = "7" } });

        Assert.Equal(original.Id, Assert.Single(store.Entries).Snippet.Id);
    }

    [Fact]
    public void Merge_SameExternalId_DifferentSource_AreDistinctSnippets()
    {
        var store = NewStore();
        store.Add(new Snippet { Label = "from A", Content = "x", Source = "System A", ExternalId = "1" });
        var (added, _) = store.Merge(new[] { new Snippet { Label = "from B", Content = "y", Source = "System B", ExternalId = "1" } });

        Assert.Equal(1, added);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Merge_WithoutExternalIdentity_StillFallsBackToIdMatching()
    {
        var store = NewStore();
        store.Add(new Snippet { Label = "plain", Content = "x" }); // no Source/ExternalId
        var (added, _) = store.Merge(new[] { new Snippet { Label = "another", Content = "y" } });

        Assert.Equal(1, added); // must NOT collapse together on two empty external ids
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void SourceAndExternalId_SurviveExportAndReimport()
    {
        var store = NewStore();
        store.Add(new Snippet { Label = "a", Content = "x", Source = "BoldDesk Aug 2026", ExternalId = "42" });

        using var file = new MemoryStream();
        store.Export(file);
        file.Position = 0;

        var target = NewStore("target.json");
        target.Merge(SnippetStore.TryParseSnippets(file)!);
        var loaded = Assert.Single(target.Entries).Snippet;
        Assert.Equal("BoldDesk Aug 2026", loaded.Source);
        Assert.Equal("42", loaded.ExternalId);
    }

    // ---- view model flow ----

    [Fact]
    public void TransferViewModel_ExportScope_DefaultsToActiveTag()
    {
        var store = NewStore();
        store.Add(Make("A", "work"));
        store.Add(Make("B", "dev"));

        var vm = new TransferViewModel(store, "dev", dataChanged: () => { }, close: () => { });
        Assert.Equal("dev", vm.ExportScope.Single(t => t.IsSelected).Name);
        Assert.Equal("klippy-dev.json", vm.SuggestedFileName);

        using var stream = new MemoryStream();
        vm.ExportTo(stream);
        Assert.Equal("Exported 1 snippet", vm.StatusText);
    }

    [Fact]
    public void TransferViewModel_ImportPreview_ThenTagFilteredImport()
    {
        var source = NewStore("source.json");
        source.Add(Make("A", "work"));
        source.Add(Make("B", "dev"));
        source.Add(Make("C", "dev"));
        using var file = new MemoryStream();
        source.Export(file);
        file.Position = 0;

        var target = NewStore("target.json");
        bool changed = false;
        var vm = new TransferViewModel(target, MainViewModel.AllTag, () => changed = true, () => { });

        vm.LoadImportPreview(file, "backup.json");
        Assert.True(vm.HasImportPreview);
        Assert.Equal("backup.json · 3 snippets", vm.ImportSummary);
        Assert.Equal(new[] { MainViewModel.AllTag, "dev", "work" }, vm.ImportScope.Select(t => t.Name));

        vm.SelectImportTagCommand.Execute(vm.ImportScope.First(t => t.Name == "dev"));
        vm.ImportCommand.Execute(null);

        Assert.True(changed);
        Assert.False(vm.HasImportPreview);
        Assert.Equal("Imported 2 new · 0 updated", vm.StatusText);
        Assert.Equal(2, target.Count);
        Assert.All(target.Entries, e => Assert.Equal("dev", e.Snippet.Tag));
    }

    [Fact]
    public void TransferViewModel_BadFile_ShowsError()
    {
        var vm = new TransferViewModel(NewStore(), MainViewModel.AllTag, () => { }, () => { });
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("nope"));
        vm.LoadImportPreview(garbage, "nope.txt");
        Assert.False(vm.HasImportPreview);
        Assert.Contains("isn't a Klippy export", vm.StatusText);
    }
}
