using System;
using System.IO;
using System.Linq;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>The store behind the command MRU: what it keeps, in what order, and for how long.</summary>
public class CommandHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"klippy-commands-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ---- ordering and dedup ----

    [Fact]
    public void NewestCommandIsFirst()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats");
        store.Record("dp");

        Assert.Equal(new[] { "dp", "? cats" }, store.Commands);
    }

    [Fact]
    public void RunningTheSameCommandAgainMovesItBackToTheTop()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats");
        store.Record("dp");
        store.Record("? cats");

        // Used twice is one command, not two: an MRU full of copies would recall nothing.
        Assert.Equal(new[] { "? cats", "dp" }, store.Commands);
    }

    [Fact]
    public void CommandsDifferingOnlyInCaseAreDifferentCommands()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats");
        store.Record("? Cats");

        // They search for different things, so recalling one must not give the other.
        Assert.Equal(new[] { "? Cats", "? cats" }, store.Commands);
    }

    [Fact]
    public void BlankLinesAreNotCommands()
    {
        var store = CommandHistory.InMemory();

        Assert.False(store.Record(""));
        Assert.False(store.Record("   "));
        Assert.False(store.Record(null));
        Assert.Empty(store.Commands);
    }

    [Fact]
    public void ALineIsKeptExactlyAsItWasTyped()
    {
        var store = CommandHistory.InMemory();
        store.Record("slf ");

        // "slf " is an invocation of slf with nothing typed after it yet; "slf" is an
        // ordinary search. Trimming would recall the other one.
        Assert.Equal("slf ", store.Commands[0]);
    }

    // ---- the cap ----

    [Fact]
    public void TheOldestCommandsGoAtTheCap()
    {
        var store = CommandHistory.InMemory(capacity: 2);
        store.Record("one");
        store.Record("two");
        store.Record("three");

        Assert.Equal(new[] { "three", "two" }, store.Commands);
    }

    [Fact]
    public void LoweringTheCapEvictsImmediately()
    {
        var store = CommandHistory.InMemory();
        foreach (var line in new[] { "one", "two", "three" }) store.Record(line);

        store.Capacity = 1;

        Assert.Equal(new[] { "three" }, store.Commands);
    }

    [Fact]
    public void ACapOfZeroTurnsTheMruOff()
    {
        var store = CommandHistory.InMemory(capacity: 0);

        Assert.False(store.IsEnabled);
        Assert.False(store.Record("? cats"));
        Assert.Empty(store.Commands);
    }

    [Fact]
    public void TurningItOffForgetsWhatWasAlreadyRecorded()
    {
        new CommandHistory(_path).Record("? cats");

        // The point of the setting is to not have the record, so it has to leave.
        var reopened = new CommandHistory(_path, capacity: 0);

        Assert.Empty(reopened.Commands);
        Assert.Empty(new CommandHistory(_path).Commands);
    }

    [Fact]
    public void DefaultCapacityIsAHundred()
    {
        Assert.Equal(100, CommandHistory.DefaultCapacity);
        Assert.Equal(100, new AppSettings().CommandHistoryLimit);
    }

    // ---- matching what has been typed ----

    [Fact]
    public void MatchingIsOnTheStartOfTheLine_IgnoringCase()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats and dogs");
        store.Record("dp --volumes");
        store.Record("? Cats sleeping");

        Assert.Equal(new[] { "? Cats sleeping", "? cats and dogs" }, store.Match("? c"));
        Assert.Equal(new[] { "? Cats sleeping", "? cats and dogs" }, store.Match("? C"));
        Assert.Equal(new[] { "dp --volumes" }, store.Match("dp"));
    }

    [Fact]
    public void NothingMatchesAWordFromTheMiddle()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats and dogs");

        // Deliberately not the snippet list's word search: anything looser would hold the
        // MRU — and the arrow keys — open over ordinary searches that were never commands.
        Assert.Empty(store.Match("cats"));
    }

    [Fact]
    public void ALineTypedInFullIsNotOfferedBackToItself()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats");

        Assert.Empty(store.Match("? cats"));
        Assert.Empty(store.Match("? CATS"));
    }

    [Fact]
    public void AnEmptyLineMatchesEverything()
    {
        var store = CommandHistory.InMemory();
        store.Record("one");
        store.Record("two");

        Assert.Equal(new[] { "two", "one" }, store.Match(""));
    }

    // ---- persistence ----

    [Fact]
    public void CommandsSurviveAReopen()
    {
        var store = new CommandHistory(_path);
        store.Record("? cats");
        store.Record("dp");

        Assert.Equal(new[] { "dp", "? cats" }, new CommandHistory(_path).Commands);
    }

    [Fact]
    public void AnUnreadableFileCostsTheMruAndNothingElse()
    {
        File.WriteAllText(_path, "{ not a json array");

        var store = new CommandHistory(_path);

        Assert.Empty(store.Commands);
        store.Record("? cats"); // and it still works from here
        Assert.Equal(new[] { "? cats" }, new CommandHistory(_path).Commands);
    }

    [Fact]
    public void AHandEditedFileIsTakenAsFarAsItMakesSense()
    {
        File.WriteAllText(_path, """["dp", "", "  ", "dp", "? cats"]""");

        // Blanks and repeats are nothing anyone typed here, whatever the file says.
        Assert.Equal(new[] { "dp", "? cats" }, new CommandHistory(_path).Commands);
    }

    [Fact]
    public void AnInMemoryStoreWritesNothing()
    {
        var store = CommandHistory.InMemory();
        store.Record("? cats");

        Assert.Null(store.FilePath);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void TheLimitSurvivesASaveAndLoadOfTheSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klippy-settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { CommandHistoryLimit = 25 }.Save(path);

            Assert.Equal(25, AppSettings.Load(path).CommandHistoryLimit);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
