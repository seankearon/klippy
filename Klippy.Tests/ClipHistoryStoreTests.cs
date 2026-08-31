using System;
using System.IO;
using System.Linq;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class ClipHistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"klippy-history-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static ClipEntry Clip(string text, string app = "", DateTimeOffset? at = null) =>
        new() { Text = text, SourceApp = app, CapturedAt = at ?? DateTimeOffset.UtcNow };

    // ---- ordering and dedup ----

    [Fact]
    public void NewestClipIsFirst()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("one"));
        store.Add(Clip("two"));

        Assert.Equal(new[] { "two", "one" }, store.Entries.Select(e => e.Text));
    }

    [Fact]
    public void IdenticalClipMovesToTopInsteadOfDuplicating()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("one"));
        store.Add(Clip("two"));
        store.Add(Clip("one"));

        Assert.Equal(new[] { "one", "two" }, store.Entries.Select(e => e.Text));
    }

    [Fact]
    public void MovedClipKeepsItsIdentityAndPin()
    {
        var store = ClipHistoryStore.InMemory();
        var original = store.Add(Clip("keep"));
        store.SetPinned(original.Id, true);
        store.Add(Clip("other"));

        var returned = store.Add(Clip("keep"));

        Assert.Equal(original.Id, returned.Id);
        Assert.True(returned.IsPinned);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void SameTextWithDifferentHtmlIsADistinctClip()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(new ClipEntry { Text = "hi", Html = "<b>hi</b>" });
        store.Add(new ClipEntry { Text = "hi" });

        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void RecopyingRefreshesTimestampAndSourceApp()
    {
        var store = ClipHistoryStore.InMemory();
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        store.Add(Clip("text", "chrome", old));

        var now = DateTimeOffset.UtcNow;
        store.Add(Clip("text", "code", now));

        var only = Assert.Single(store.Entries);
        Assert.Equal(now, only.CapturedAt);
        Assert.Equal("code", only.SourceApp);
    }

    [Fact]
    public void MarkUsedBumpsAClipBackToTheTop()
    {
        // Copying a clip out of the history is not a capture, so re-ranking is explicit.
        var store = ClipHistoryStore.InMemory();
        var first = store.Add(Clip("older"));
        store.Add(Clip("newer"));

        Assert.True(store.MarkUsed(first.Id));

        Assert.Equal(new[] { "older", "newer" }, store.Entries.Select(e => e.Text));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void MarkUsedRefreshesTheTimestampSoSearchAgreesWithTheList()
    {
        var store = ClipHistoryStore.InMemory();
        var first = store.Add(Clip("older", at: DateTimeOffset.UtcNow.AddHours(-1)));
        store.Add(Clip("newer"));

        store.MarkUsed(first.Id);

        // An empty query ranks by recency, so the search order must match the list order.
        Assert.Equal(new[] { "older", "newer" }, store.Search("").Select(r => r.Item.Text));
    }

    [Fact]
    public void MarkUsedIgnoresAClipThatIsNoLongerThere()
    {
        var store = ClipHistoryStore.InMemory();

        Assert.False(store.MarkUsed(Guid.NewGuid()));
    }

    // ---- capacity, eviction, pinning ----

    [Fact]
    public void OldestClipsAreEvictedAtCapacity()
    {
        var store = ClipHistoryStore.InMemory(capacity: 3);
        foreach (var text in new[] { "a", "b", "c", "d" })
            store.Add(Clip(text));

        Assert.Equal(new[] { "d", "c", "b" }, store.Entries.Select(e => e.Text));
    }

    [Fact]
    public void PinnedClipsSurviveEviction()
    {
        var store = ClipHistoryStore.InMemory(capacity: 2);
        var pinned = store.Add(Clip("pinned"));
        store.SetPinned(pinned.Id, true);

        foreach (var text in new[] { "a", "b", "c" })
            store.Add(Clip(text));

        Assert.Contains(store.Entries, e => e.Text == "pinned");
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void AnEntirelyPinnedHistoryStaysOverCapacityRatherThanLosingData()
    {
        var store = ClipHistoryStore.InMemory(capacity: 1);
        foreach (var text in new[] { "a", "b", "c" })
            store.SetPinned(store.Add(Clip(text)).Id, true);

        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void UnpinningLetsTheCapCatchUp()
    {
        var store = ClipHistoryStore.InMemory(capacity: 1);
        var first = store.Add(Clip("a"));
        store.SetPinned(first.Id, true);
        store.Add(Clip("b"));
        Assert.Equal(2, store.Count);

        store.SetPinned(first.Id, false);

        Assert.Equal("b", Assert.Single(store.Entries).Text);
    }

    [Fact]
    public void LoweringCapacityEvictsImmediately()
    {
        var store = ClipHistoryStore.InMemory(capacity: 10);
        foreach (var text in new[] { "a", "b", "c", "d" })
            store.Add(Clip(text));

        store.Capacity = 2;

        Assert.Equal(new[] { "d", "c" }, store.Entries.Select(e => e.Text));
    }

    // ---- removal ----

    [Fact]
    public void ClearKeepsPinnedClipsByDefault()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("go"));
        store.SetPinned(store.Add(Clip("stay")).Id, true);

        store.Clear();

        Assert.Equal("stay", Assert.Single(store.Entries).Text);

        store.Clear(includePinned: true);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void RemoveTakesTheClipOutOfSearchToo()
    {
        var store = ClipHistoryStore.InMemory();
        var clip = store.Add(Clip("findable text"));

        Assert.True(store.Remove(clip.Id));
        Assert.Empty(store.Search("findable"));
        Assert.False(store.Remove(clip.Id));
    }

    // ---- search ----

    [Fact]
    public void SearchRanksLabelHitsOverBodyHits()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("a line mentioning docker halfway through"));
        store.Add(Clip("docker system prune"));

        var results = store.Search("docker");

        Assert.Equal("docker system prune", results[0].Item.Text);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void EmptySearchReturnsEverythingNewestFirst()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("older", at: DateTimeOffset.UtcNow.AddMinutes(-5)));
        store.Add(Clip("newer"));

        Assert.Equal(new[] { "newer", "older" }, store.Search("").Select(r => r.Item.Text));
    }

    [Fact]
    public void SearchIndexStaysAlignedAfterEviction()
    {
        var store = ClipHistoryStore.InMemory(capacity: 2);
        store.Add(Clip("alpha"));
        store.Add(Clip("bravo"));
        store.Add(Clip("charlie"));

        Assert.Empty(store.Search("alpha"));
        Assert.Equal("bravo", Assert.Single(store.Search("bravo")).Item.Text);
    }

    [Fact]
    public void LabelIsTheFirstNonBlankLine()
    {
        var clip = Clip("\n\n  Send log files  \nand the config");
        Assert.Equal("Send log files", clip.Label);
    }

    // ---- persistence ----

    [Fact]
    public void NothingIsWrittenUntilFlush()
    {
        var store = new ClipHistoryStore(_path);
        store.Add(Clip("deferred"));

        Assert.True(store.IsDirty);
        Assert.False(File.Exists(_path));

        store.Flush();

        Assert.False(store.IsDirty);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void FlushedHistoryReloadsNewestFirst()
    {
        var store = new ClipHistoryStore(_path);
        store.Add(Clip("older", "chrome", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var pinned = store.Add(Clip("newer"));
        store.SetPinned(pinned.Id, true);
        store.Flush();

        var reloaded = new ClipHistoryStore(_path);

        Assert.Equal(new[] { "newer", "older" }, reloaded.Entries.Select(e => e.Text));
        Assert.True(reloaded.Entries[0].IsPinned);
        Assert.Equal("chrome", reloaded.Entries[1].SourceApp);
        Assert.False(reloaded.IsDirty);
    }

    [Fact]
    public void SessionOnlyHistoryNeverTouchesDisk()
    {
        var store = ClipHistoryStore.InMemory();
        store.Add(Clip("secret"));
        store.Flush();

        Assert.Null(store.FilePath);
        Assert.False(File.Exists(_path));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void CorruptHistoryStartsEmptyRatherThanThrowing()
    {
        File.WriteAllText(_path, "{ not json");

        var store = new ClipHistoryStore(_path);

        Assert.Empty(store.Entries);
    }

    [Fact]
    public void CapacityLoweredBetweenRunsBitesOnLoad()
    {
        var store = new ClipHistoryStore(_path, capacity: 10);
        for (int i = 0; i < 5; i++)
            store.Add(Clip($"clip {i}", at: DateTimeOffset.UtcNow.AddSeconds(i)));
        store.Flush();

        var reloaded = new ClipHistoryStore(_path, capacity: 2);

        Assert.Equal(new[] { "clip 4", "clip 3" }, reloaded.Entries.Select(e => e.Text));
        Assert.True(reloaded.IsDirty);
    }

    [Fact]
    public void ReloadedHistoryIsStillSearchable()
    {
        var store = new ClipHistoryStore(_path);
        store.Add(Clip("docker system prune"));
        store.Add(Clip("something else"));
        store.Flush();

        var reloaded = new ClipHistoryStore(_path);

        Assert.Equal("docker system prune", Assert.Single(reloaded.Search("docker")).Item.Text);
    }
}
