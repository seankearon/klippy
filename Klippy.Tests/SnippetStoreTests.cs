using System;
using System.IO;
using System.Linq;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class SnippetStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"klippy-tests-{Guid.NewGuid():N}", "snippets.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void SeedsOnFirstRun_ThenPersists()
    {
        var store = new SnippetStore(_path);
        Assert.True(store.Count > 0);
        Assert.True(File.Exists(_path));

        var reloaded = new SnippetStore(_path);
        Assert.Equal(store.Count, reloaded.Count);
    }

    [Fact]
    public void AddUpdateDelete_RoundTrip()
    {
        var store = new SnippetStore(_path, seedIfEmpty: false);
        Assert.Equal(0, store.Count);

        var snippet = new Snippet { Label = "Test", Content = "hello world", Tag = "dev", QuickCode = "tst" };
        store.Add(snippet);

        var reloaded = new SnippetStore(_path);
        var loaded = Assert.Single(reloaded.Entries).Snippet;
        Assert.Equal("Test", loaded.Label);
        Assert.Equal("tst", loaded.QuickCode);

        loaded.Label = "Renamed";
        reloaded.Update(loaded);
        Assert.Equal("Renamed", Assert.Single(new SnippetStore(_path).Entries).Snippet.Label);

        reloaded.Delete(loaded.Id);
        Assert.Empty(new SnippetStore(_path, seedIfEmpty: false).Entries);
    }

    [Fact]
    public void EditedSnippet_IsReindexedForSearch()
    {
        var store = new SnippetStore(_path, seedIfEmpty: false);
        var snippet = new Snippet { Label = "Alpha", Content = "one" };
        store.Add(snippet);
        Assert.Empty(store.Search("beta"));

        snippet.Label = "Beta";
        store.Update(snippet);
        Assert.Single(store.Search("beta"));
    }

    [Fact]
    public void CorruptFile_LoadsEmpty_WithoutSeeding()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json !!");
        var store = new SnippetStore(_path);
        Assert.Equal(0, store.Count); // existing (broken) file: no reseed, no crash
    }

    [Fact]
    public void Tags_AreDistinctSortedAndNonEmpty()
    {
        var store = new SnippetStore(_path, seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x", Tag = "work" });
        store.Add(new Snippet { Label = "b", Content = "y", Tag = "dev" });
        store.Add(new Snippet { Label = "c", Content = "z", Tag = "work" });
        store.Add(new Snippet { Label = "d", Content = "w", Tag = "" });
        Assert.Equal(new[] { "dev", "work" }, store.Tags().ToArray());
    }
}
