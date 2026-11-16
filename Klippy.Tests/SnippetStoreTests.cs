using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The snippet store: one file per snippet in a folder, recency kept on this machine, and a folder that
/// another machine — through a sync client — is writing to as well.
/// </summary>
public class SnippetStoreTests : IDisposable
{
    /// <summary>Stands in for DataDirectory: the snippets folder, the old file and the conflicts folder all sit here.</summary>
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"klippy-tests-{Guid.NewGuid():N}");

    private string Folder => Path.Combine(_data, "snippets");
    private string Legacy => Path.Combine(_data, "snippets.json");
    private string Conflicts => Path.Combine(_data, "conflicts");

    /// <summary>This machine's usage file — somewhere that is not the data folder, as the real one is not.</summary>
    private string UsagePath => Path.Combine(_data + "-appdata", "snippet.usage.json");

    public void Dispose()
    {
        foreach (var dir in new[] { _data, _data + "-appdata", _data + "-moved" })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private SnippetStore Open(bool seed = false, SnippetUsage? usage = null) =>
        new(Folder, seedIfEmpty: seed, usage: usage);

    private string FileOf(Guid id) => Path.Combine(Folder, $"{id:D}.json");

    private static Snippet New(string label, string content = "x", DateTimeOffset? created = null) =>
        new() { Label = label, Content = content, CreatedAt = created ?? DateTimeOffset.UtcNow };

    /// <summary>Writes a snippet's file the way another machine's Klippy would have.</summary>
    private void WriteAsAnotherMachine(Snippet snippet, string? name = null)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, name ?? $"{snippet.Id:D}.json"),
            JsonSerializer.Serialize(snippet, KlippyJsonContext.Default.Snippet));
    }

    /// <summary>Every file under the data folder, with what it holds and when it was written.</summary>
    private Dictionary<string, (string Text, DateTime Written)> Snapshot() =>
        Directory.GetFiles(_data, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (File.ReadAllText(f), File.GetLastWriteTimeUtc(f)));

    // ---- one file per snippet ----

    [Fact]
    public void SeedsOnFirstRun_ThenPersists()
    {
        var store = Open(seed: true);
        Assert.True(store.Count > 0);
        Assert.Equal(store.Count, Directory.GetFiles(Folder, "*.json").Length);

        var reloaded = Open(seed: true);
        Assert.Equal(store.Count, reloaded.Count);
    }

    [Fact]
    public void AnEmptiedFolder_IsNotAFirstRun()
    {
        // Every snippet deleted is a store with nothing in it, not an invitation to put the examples back.
        var store = Open(seed: true);
        foreach (var snippet in store.Snippets.ToList()) store.Delete(snippet.Id);

        Assert.Equal(0, Open(seed: true).Count);
    }

    [Fact]
    public void AddUpdateDelete_RoundTrip()
    {
        var store = Open();
        Assert.Equal(0, store.Count);

        var snippet = new Snippet { Label = "Test", Content = "hello world", Tag = "dev", QuickCode = "tst" };
        store.Add(snippet);

        var reloaded = Open();
        var loaded = Assert.Single(reloaded.Entries).Item;
        Assert.Equal("Test", loaded.Label);
        Assert.Equal("tst", loaded.QuickCode);

        loaded.Label = "Renamed";
        reloaded.Update(loaded);
        Assert.Equal("Renamed", Assert.Single(Open().Entries).Item.Label);

        reloaded.Delete(loaded.Id);
        Assert.Empty(Open().Entries);
        Assert.False(File.Exists(FileOf(loaded.Id)));
    }

    [Fact]
    public void EachSnippetIsAFileOfItsOwn_NamedForItsId()
    {
        var store = Open();
        var a = New("a");
        var b = New("b");
        store.Add(a);
        store.Add(b);

        Assert.Equal(
            new[] { FileOf(a.Id), FileOf(b.Id) }.OrderBy(p => p),
            Directory.GetFiles(Folder).OrderBy(p => p));
    }

    [Fact]
    public void AFile_SaysWhenAndWhereItWasSaved_ButNotWhenItWasUsed()
    {
        // Recency changes on every copy and is about one machine; a file that changed on every copy is one
        // two machines would forever be writing over each other.
        var store = Open();
        var before = DateTimeOffset.UtcNow;
        var snippet = New("a");
        store.Add(snippet);

        var json = File.ReadAllText(FileOf(snippet.Id));
        Assert.DoesNotContain("LastUsedAt", json);
        Assert.Contains("ModifiedAt", json);
        Assert.Equal(Environment.MachineName, snippet.ModifiedBy);
        Assert.InRange(snippet.ModifiedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void AnEdit_WritesOnlyThatSnippetsFile()
    {
        var store = Open();
        var edited = New("edited");
        var untouched = New("untouched");
        store.Add(edited);
        store.Add(untouched);
        File.SetLastWriteTimeUtc(FileOf(untouched.Id), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = File.ReadAllText(FileOf(untouched.Id));

        edited.Label = "edited again";
        store.Update(edited);

        Assert.Equal(before, File.ReadAllText(FileOf(untouched.Id)));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(FileOf(untouched.Id)));
    }

    [Fact]
    public void EditedSnippet_IsReindexedForSearch()
    {
        var store = Open();
        var snippet = new Snippet { Label = "Alpha", Content = "one" };
        store.Add(snippet);
        Assert.Empty(store.Search("beta"));

        snippet.Label = "Beta";
        store.Update(snippet);
        Assert.Single(store.Search("beta"));
    }

    [Fact]
    public void IsMarkdownFlag_RoundTrips_AndDefaultsToPlain()
    {
        var store = Open();
        store.Add(new Snippet { Label = "md", Content = "**hi**", IsMarkdown = true });
        store.Add(new Snippet { Label = "plain", Content = "hi" });

        var reloaded = Open();
        Assert.True(reloaded.Entries.Single(e => e.Item.Label == "md").Item.IsMarkdown);
        Assert.False(reloaded.Entries.Single(e => e.Item.Label == "plain").Item.IsMarkdown);
    }

    [Fact]
    public void Tags_AreDistinctSortedAndNonEmpty()
    {
        var store = Open();
        store.Add(new Snippet { Label = "a", Content = "x", Tag = "work" });
        store.Add(new Snippet { Label = "b", Content = "y", Tag = "dev" });
        store.Add(new Snippet { Label = "c", Content = "z", Tag = "work" });
        store.Add(new Snippet { Label = "d", Content = "w", Tag = "" });
        Assert.Equal(new[] { "dev", "work" }, store.Tags().ToArray());
    }

    [Fact]
    public void FilesThatAreNotSnippets_AreLeftAlone()
    {
        // Half-written files — Klippy's own .tmp, a sync client's dot-file — are not snippets, and nothing
        // reads them or moves them.
        Directory.CreateDirectory(Folder);
        var tmp = Path.Combine(Folder, $"{Guid.NewGuid():D}.json.tmp");
        var hidden = Path.Combine(Folder, ".syncthing.a.json");
        File.WriteAllText(tmp, "{");
        File.WriteAllText(hidden, "{");

        var store = Open();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.UnreadableFiles);
        Assert.True(File.Exists(tmp));
        Assert.True(File.Exists(hidden));
    }

    // ---- creation order ----

    [Fact]
    public void Snippets_AreInCreationOrder_ThenById()
    {
        // With one file per snippet there is no file order: the order a key clash is settled in has to be one
        // every machine can work out for itself.
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var late = New("late", created: t.AddDays(2));
        var early = New("early", created: t);
        var tieA = new Snippet { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Label = "tie a", Content = "x", CreatedAt = t.AddDays(1) };
        var tieB = new Snippet { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Label = "tie b", Content = "x", CreatedAt = t.AddDays(1) };
        var store = Open();
        foreach (var s in new[] { late, tieB, early, tieA }) store.Add(s);

        var expected = new[] { "early", "tie a", "tie b", "late" };
        Assert.Equal(expected, store.Snippets.Select(s => s.Label));
        Assert.Equal(expected, Open().Snippets.Select(s => s.Label));
    }

    [Fact]
    public void AQuickCodeTwoSnippetsAnswerTo_GoesToTheEarlierOne()
    {
        var t = DateTimeOffset.UtcNow;
        var store = Open();
        store.Add(new Snippet { Label = "newer", Content = "x", QuickCode = "dup", CreatedAt = t });
        store.Add(new Snippet { Label = "older", Content = "x", QuickCode = "dup", CreatedAt = t.AddDays(-1) });

        Assert.Equal("older", store.FindByQuickCode("dup")!.Label);
    }

    // ---- recency stays on this machine ----

    [Fact]
    public void CopyingOrRunning_WritesNothingUnderTheDataFolder()
    {
        var store = Open(usage: new SnippetUsage(UsagePath));
        var snippet = New("a");
        store.Add(snippet);
        var before = Snapshot();

        store.MarkUsed(snippet);
        store.MarkUsed(snippet);

        Assert.Equal(before, Snapshot());
        Assert.True(File.Exists(UsagePath)); // it went somewhere — just not there
    }

    [Fact]
    public void Recency_SurvivesARestart_OnThisMachine()
    {
        var store = Open(usage: new SnippetUsage(UsagePath));
        var older = New("older");
        var newer = New("newer");
        store.Add(older);
        store.Add(newer);
        store.MarkUsed(older);

        var reopened = Open(usage: new SnippetUsage(UsagePath));

        Assert.Equal("older", reopened.Search("").First().Item.Label);
        Assert.Equal(older.LastUsedAt, reopened.Find(older.Id)!.LastUsedAt);
    }

    [Fact]
    public void ASnippetNeverUsedHere_RanksByWhenItWasMade()
    {
        // Another machine's use of it is that machine's business.
        var made = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        WriteAsAnotherMachine(new Snippet { Label = "from elsewhere", Content = "x", CreatedAt = made, LastUsedAt = DateTimeOffset.UtcNow });

        Assert.Equal(made, Assert.Single(Open().Snippets).LastUsedAt);
    }

    [Fact]
    public void Deleting_ForgetsTheRecencyToo()
    {
        var usage = new SnippetUsage(UsagePath);
        var store = Open(usage: usage);
        var snippet = New("a");
        store.Add(snippet);
        store.MarkUsed(snippet);

        store.Delete(snippet.Id);

        Assert.Null(new SnippetUsage(UsagePath).LastUsed(snippet.Id));
    }

    // ---- two machines, one folder ----

    [Fact]
    public void TwoInstances_SeeEachOthersAddsEditsAndDeletes_WithoutARestart()
    {
        var here = Open();
        var there = Open();

        var added = New("added there");
        there.Add(added);
        Assert.True(here.Rescan().Changed.Contains(added.Id));
        Assert.Equal("added there", Assert.Single(here.Snippets).Label);

        var mine = here.Find(added.Id)!;
        mine.Label = "edited here";
        here.Update(mine);
        there.Rescan();
        Assert.Equal("edited here", Assert.Single(there.Snippets).Label);

        there.Delete(added.Id);
        Assert.True(here.Rescan().Changed.Contains(added.Id));
        Assert.Empty(here.Snippets);
    }

    [Fact]
    public void TwoInstancesAddingAtOnce_LoseNothing()
    {
        // The case that used to lose snippets: each machine adding to its own copy of the list and writing
        // it over the other's. Two adds are now two files.
        var here = Open();
        var there = Open();

        here.Add(New("added here"));
        there.Add(New("added there"));
        here.Add(New("added here too"));

        here.Rescan();
        there.Rescan();
        var expected = new[] { "added here", "added here too", "added there" };
        Assert.Equal(expected, here.Snippets.Select(s => s.Label).OrderBy(l => l));
        Assert.Equal(expected, there.Snippets.Select(s => s.Label).OrderBy(l => l));
    }

    [Fact]
    public void ALookThatFindsNothingNew_SaysSo()
    {
        var store = Open();
        store.Add(New("a"));

        Assert.False(store.Rescan().Any);
    }

    [Fact]
    public void AnUnchangedSnippet_KeepsTheInstanceTheListIsShowing()
    {
        // The list's rows hold snippets; a look that changed nothing about one must not swap it under them.
        var here = Open();
        var kept = New("kept");
        here.Add(kept);
        Open().Add(New("other"));

        here.Rescan();

        Assert.Same(kept, here.Find(kept.Id));
    }

    [Fact]
    public void AFolderThatGoesAway_TakesNothingWithIt()
    {
        // A synced drive that is not mounted right now is not a store whose snippets were all deleted.
        var store = Open();
        store.Add(New("a"));
        var moved = _data + "-moved";
        MoveDirectory(_data, moved);
        try
        {
            Assert.False(store.Rescan().Any);
            Assert.Single(store.Snippets);
        }
        finally
        {
            MoveDirectory(moved, _data);
        }
    }

    /// <summary>
    /// Directory.Move, retried briefly: a file just written can be held open for a moment by something
    /// outside the test — an antivirus or indexer scan — and the rename is then denied.
    /// </summary>
    private static void MoveDirectory(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void SavingAVersionThatChangedElsewhere_KeepsTheOtherOneAside()
    {
        // The editor was open here while the same snippet was edited on another machine. The save made
        // here wins — it is the one just asked for — and the other is not lost either.
        var here = Open();
        var snippet = New("original");
        here.Add(snippet);

        var there = Open();
        var theirs = there.Find(snippet.Id)!;
        theirs.Label = "edited there";
        there.Update(theirs);
        here.Rescan();

        snippet.Label = "edited here"; // the instance the editor had open
        Assert.True(here.Update(snippet));

        Assert.Equal("edited here", Open().Find(snippet.Id)!.Label);
        var aside = Assert.Single(Directory.GetFiles(Conflicts));
        Assert.Contains("edited there", File.ReadAllText(aside));
    }

    [Fact]
    public void SavingTheVersionItHas_SetsNothingAside()
    {
        var store = Open();
        var snippet = New("a");
        store.Add(snippet);
        snippet.Label = "b";

        Assert.False(store.Update(snippet));
        Assert.False(Directory.Exists(Conflicts));
    }

    // ---- sync conflicts ----

    private const string ProtonCopy = " (# Edit conflict 2026-09-01 KNHTB #).json";

    [Fact]
    public void AConflictCopy_IsSettledByWhenEachWasModified_AndTheLoserIsKept()
    {
        var id = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        WriteAsAnotherMachine(new Snippet { Id = id, Label = "older", Content = "x", ModifiedAt = t, ModifiedBy = "WORK" });
        WriteAsAnotherMachine(new Snippet { Id = id, Label = "newer", Content = "x", ModifiedAt = t.AddMinutes(5), ModifiedBy = "HOME" },
            $"{id:D}{ProtonCopy}");

        var store = Open();
        var changes = store.Rescan(); // the constructor's look settled it already; this one finds nothing more

        Assert.Equal("newer", Assert.Single(store.Snippets).Label);
        Assert.False(changes.Any);
        Assert.Equal(new[] { FileOf(id) }, Directory.GetFiles(Folder));
        Assert.Contains("newer", File.ReadAllText(FileOf(id)));
        var loser = Assert.Single(Directory.GetFiles(Conflicts));
        Assert.Contains("older", File.ReadAllText(loser));
    }

    [Fact]
    public void AnOlderConflictCopy_IsMovedAside_AndTheSnippetsOwnFileStays()
    {
        var store = Open();
        var snippet = New("current");
        store.Add(snippet);
        var stale = new Snippet
        {
            Id = snippet.Id, Label = "stale", Content = "x", CreatedAt = snippet.CreatedAt,
            ModifiedAt = snippet.ModifiedAt.AddHours(-1),
        };
        WriteAsAnotherMachine(stale, $"{snippet.Id:D} (Sam's conflicted copy 2026-09-01).json");

        var changes = store.Rescan();

        Assert.Equal(1, changes.ConflictsResolved);
        Assert.Equal("current", Assert.Single(store.Snippets).Label);
        Assert.Contains("stale", File.ReadAllText(Assert.Single(Directory.GetFiles(Conflicts))));
        Assert.Equal(new[] { FileOf(snippet.Id) }, Directory.GetFiles(Folder));
    }

    [Fact]
    public void ACopyThatSaysNothingNew_IsSimplyRemoved()
    {
        // What two machines carrying the same snippets forward at once leave behind: the same bytes twice.
        var snippet = New("same");
        snippet.ModifiedAt = snippet.CreatedAt;
        WriteAsAnotherMachine(snippet);
        WriteAsAnotherMachine(snippet, $"{snippet.Id:D}-DESKTOP-ABC.json");

        var store = Open();

        Assert.Single(store.Snippets);
        Assert.Equal(new[] { FileOf(snippet.Id) }, Directory.GetFiles(Folder));
        Assert.False(Directory.Exists(Conflicts));
    }

    [Fact]
    public void ACopyWithoutItsOwnFile_BecomesTheSnippetsOwnFile()
    {
        // Deleted on one machine while edited on another: keeping the edit is the side that loses nothing.
        var snippet = New("edited");
        WriteAsAnotherMachine(snippet, $"{snippet.Id:D}.sync-conflict-20260901-120000-ABCDEF.json");

        var store = Open();

        Assert.Equal("edited", Assert.Single(store.Snippets).Label);
        Assert.Equal(new[] { FileOf(snippet.Id) }, Directory.GetFiles(Folder));
    }

    [Fact]
    public void ACopyIsNeverMovedOverAnOwnFileThatCouldNotBeRead()
    {
        // What the unreadable file holds is unknown, and may be the newer of the two.
        var id = Guid.NewGuid();
        WriteAsAnotherMachine(new Snippet { Id = id, Label = "own", Content = "x", ModifiedAt = DateTimeOffset.UtcNow });
        WriteAsAnotherMachine(new Snippet { Id = id, Label = "copy", Content = "x", ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            $"{id:D}{ProtonCopy}");

        SnippetStore store;
        using (new FileStream(FileOf(id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = Open();
            Assert.Equal("copy", Assert.Single(store.Snippets).Label); // the only version there is to show
            Assert.Equal(2, Directory.GetFiles(Folder).Length); // nothing moved over the one it could not read
        }

        store.Rescan();
        Assert.Equal("own", Assert.Single(store.Snippets).Label);
        Assert.Equal(new[] { FileOf(id) }, Directory.GetFiles(Folder));
    }

    // ---- files that cannot be read ----

    [Fact]
    public void AnUnreadableFile_IsNeitherEmptiedNorDeleted_AndIsPickedUpOnceReadable()
    {
        var here = Open();
        var snippet = New("before");
        here.Add(snippet);
        here.Add(New("other"));

        var there = Open();
        var theirs = there.Find(snippet.Id)!;
        theirs.Label = "after";
        there.Update(theirs);

        using (new FileStream(FileOf(snippet.Id), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            here.Rescan();
            Assert.Equal(2, here.Count);
            Assert.Equal("before", here.Find(snippet.Id)!.Label);
            Assert.Contains($"{snippet.Id:D}.json", here.UnreadableFiles);
        }

        here.Rescan();
        Assert.Equal("after", here.Find(snippet.Id)!.Label);
        Assert.Empty(here.UnreadableFiles);
    }

    [Fact]
    public void AHalfWrittenFile_IsTriedAgain_WhenItIsWhole()
    {
        var snippet = New("arrived");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(snippet.Id), "{ \"Id\": \"");

        var store = Open();
        Assert.Equal(0, store.Count);

        WriteAsAnotherMachine(snippet);
        store.Rescan();
        Assert.Equal("arrived", Assert.Single(store.Snippets).Label);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsNeverSavedOver()
    {
        // Nothing is written as a result of reading: an edit elsewhere in the store leaves it exactly as it was.
        Directory.CreateDirectory(Folder);
        var broken = Path.Combine(Folder, $"{Guid.NewGuid():D}.json");
        File.WriteAllText(broken, "{ not json !!");

        var store = Open(seed: true);
        store.Add(New("another"));

        Assert.Equal("{ not json !!", File.ReadAllText(broken));
        Assert.Single(store.Snippets); // and the examples were not put back beside it
    }

    // ---- the single file this used to be ----

    private const string LegacyJson = """
        [
          {"Id":"11111111-1111-1111-1111-111111111111","Label":"Old","Content":"a_b_c","Tag":"dev",
           "CreatedAt":"2026-01-01T00:00:00+00:00","LastUsedAt":"2026-08-01T00:00:00+00:00"},
          {"Id":"22222222-2222-2222-2222-222222222222","Label":"Older","Content":"x","QuickCode":"ol",
           "CreatedAt":"2025-01-01T00:00:00+00:00","LastUsedAt":"2026-07-01T00:00:00+00:00"}
        ]
        """;

    [Fact]
    public void AnOlderStore_IsCarriedForward_WithNothingLost()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Legacy, LegacyJson);

        var store = new SnippetStore(Folder, usage: new SnippetUsage(UsagePath));

        Assert.Equal(new[] { "Older", "Old" }, store.Snippets.Select(s => s.Label)); // creation order
        Assert.Equal(2, Directory.GetFiles(Folder, "*.json").Length);
        Assert.False(File.Exists(Legacy));
        Assert.Equal(LegacyJson, File.ReadAllText(Path.Combine(_data, "snippets.migrated.json")));

        // Recency now lives on this machine, and arrived there intact.
        var usage = new SnippetUsage(UsagePath);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T00:00:00+00:00"), usage.LastUsed(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.Equal("Old", store.Search("").First().Item.Label);

        // Last modified when it was made, which is all that is known.
        var old = store.Find(Guid.Parse("11111111-1111-1111-1111-111111111111"))!;
        Assert.Equal(old.CreatedAt, old.ModifiedAt);
        Assert.Equal("", old.ModifiedBy);
    }

    [Fact]
    public void LegacyFileWithoutFormatField_LoadsAsPlain()
    {
        // Files written before Markdown support must not suddenly render as rich text.
        Directory.CreateDirectory(_data);
        File.WriteAllText(Legacy, """[{"Id":"11111111-1111-1111-1111-111111111111","Label":"Old","Content":"a_b_c","Tag":"dev"}]""");
        var store = Open(seed: true);
        Assert.False(Assert.Single(store.Entries).Item.IsMarkdown);
    }

    [Fact]
    public void ACorruptOlderStore_LoadsEmpty_SeedsNothing_AndIsLeftAlone()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Legacy, "{ not json !!");

        var store = Open(seed: true);

        Assert.Equal(0, store.Count); // existing (broken) file: no reseed, no crash
        Assert.Equal("{ not json !!", File.ReadAllText(Legacy));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void AnOlderStoreThatComesBack_AddsWhatIsNew_AndOverwritesNothing()
    {
        // An older Klippy on another machine goes on writing snippets.json after this one has moved on.
        // The snippets it adds there must not be stranded; the ones it holds stale copies of must not win.
        Directory.CreateDirectory(_data);
        File.WriteAllText(Legacy, LegacyJson);
        var store = Open();
        var old = store.Find(Guid.Parse("11111111-1111-1111-1111-111111111111"))!;
        old.Label = "Edited since";
        store.Update(old);

        File.WriteAllText(Legacy, """
            [
              {"Id":"11111111-1111-1111-1111-111111111111","Label":"Old","Content":"a_b_c"},
              {"Id":"33333333-3333-3333-3333-333333333333","Label":"Added by the old version","Content":"y"}
            ]
            """);
        var reopened = Open();

        Assert.Equal("Edited since", reopened.Find(old.Id)!.Label);
        Assert.NotNull(reopened.Find(Guid.Parse("33333333-3333-3333-3333-333333333333")));
        Assert.True(File.Exists(Path.Combine(_data, "snippets.migrated.json")));
        Assert.True(File.Exists(Path.Combine(_data, "snippets.migrated.2.json")));
        Assert.False(File.Exists(Legacy));
    }

    [Fact]
    public void TwoMachinesCarryingTheSameFileForward_WriteTheSameBytes()
    {
        // So the sync client has nothing to keep two of — or keeps two the same, which a look removes.
        var other = _data + "-other";
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(other);
        File.WriteAllText(Legacy, LegacyJson);
        File.WriteAllText(Path.Combine(other, "snippets.json"), LegacyJson);
        try
        {
            _ = Open();
            _ = new SnippetStore(Path.Combine(other, "snippets"), seedIfEmpty: false);

            foreach (var mine in Directory.GetFiles(Folder))
                Assert.Equal(
                    File.ReadAllText(mine),
                    File.ReadAllText(Path.Combine(other, "snippets", Path.GetFileName(mine))));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    // ---- export and import ----

    [Fact]
    public void Export_IsStillOneList_RecencyAndAll()
    {
        var store = Open();
        var snippet = New("a");
        store.Add(snippet);
        store.MarkUsed(snippet);

        using var buffer = new MemoryStream();
        Assert.Equal(1, store.Export(buffer));

        buffer.Position = 0;
        var parsed = Assert.Single(SnippetStore.TryParseSnippets(buffer)!);
        Assert.Equal(snippet.LastUsedAt, parsed.LastUsedAt);
        Assert.Equal(snippet.Id, parsed.Id);
    }

    [Fact]
    public void AnImport_IsAnEditMadeHere_SoItWinsOverOlderVersionsElsewhere()
    {
        var store = Open();
        var before = DateTimeOffset.UtcNow;
        var incoming = new Snippet
        {
            Label = "imported", Content = "x", ModifiedAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var (added, _) = store.Merge([incoming]);

        Assert.Equal(1, added);
        Assert.InRange(store.Find(incoming.Id)!.ModifiedAt, before, DateTimeOffset.UtcNow);
        Assert.True(File.Exists(FileOf(incoming.Id)));
    }

    [Fact]
    public void AnImport_KeepsThisMachinesOwnRecency()
    {
        var usage = new SnippetUsage(UsagePath);
        var store = Open(usage: usage);
        var snippet = New("a");
        store.Add(snippet);
        store.MarkUsed(snippet);
        var usedHere = snippet.LastUsedAt;

        store.Merge([new Snippet { Id = snippet.Id, Label = "a", Content = "y", LastUsedAt = usedHere.AddYears(-1) }]);

        Assert.Equal(usedHere, store.Find(snippet.Id)!.LastUsedAt);
    }
}
