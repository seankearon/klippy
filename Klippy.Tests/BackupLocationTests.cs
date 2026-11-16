using System;
using System.IO;
using System.Linq;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

[Collection("storage-locations")] // mutates StorageLocations statics
public class BackupLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-backup-{Guid.NewGuid():N}");
    private readonly string _originalDir = StorageLocations.Directory;
    private readonly string _originalDefaultDir = StorageLocations.DefaultDirectory;
    private readonly string? _originalExempt = StorageLocations.BackupExemptDirectory;

    public BackupLocationTests()
    {
        StorageLocations.Directory = Path.Combine(_root, "appdata", "Klippy");
        // The app's own store keeps its usage in the app-data folder, and a test must not write the
        // developer's.
        StorageLocations.DefaultDirectory = StorageLocations.Directory;
        StorageLocations.BackupExemptDirectory = Path.Combine(_root, "no_backup", "Klippy");
    }

    public void Dispose()
    {
        StorageLocations.Directory = _originalDir;
        StorageLocations.DefaultDirectory = _originalDefaultDir;
        StorageLocations.BackupExemptDirectory = _originalExempt;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FreshStore_UsesBackedUpLocation()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });

        Assert.True(store.IsIncludedInBackup);
        Assert.Equal(StorageLocations.BackedUpFolder, store.FolderPath);
        Assert.Single(Directory.GetFiles(StorageLocations.BackedUpFolder, "*.json"));
    }

    [Fact]
    public void OptingOut_MovesFileToBackupExemptLocation()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "secret", Content = "DE44 5001" });

        store.SetBackupParticipation(false);

        Assert.False(store.IsIncludedInBackup);
        Assert.Single(Directory.GetFiles(StorageLocations.BackupExemptFolder!, "*.json"));
        Assert.False(Directory.Exists(StorageLocations.BackedUpFolder)); // no copy left behind to be backed up
        Assert.Equal("secret", Assert.Single(store.Entries).Item.Label);
    }

    [Fact]
    public void OptingBackIn_MovesFileBack()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });
        store.SetBackupParticipation(false);
        store.SetBackupParticipation(true);

        Assert.True(store.IsIncludedInBackup);
        Assert.Single(Directory.GetFiles(StorageLocations.BackedUpFolder, "*.json"));
        Assert.False(Directory.Exists(StorageLocations.BackupExemptFolder!));
    }

    [Fact]
    public void ExemptLocation_IsPreferredOnNextLaunch()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "kept", Content = "x" });
        store.SetBackupParticipation(false);

        // simulates relaunching the app: the choice is inferred from where the file is
        var reopened = new SnippetStore(seedIfEmpty: false);
        Assert.False(reopened.IsIncludedInBackup);
        Assert.Equal("kept", Assert.Single(reopened.Entries).Item.Label);
    }

    [Fact]
    public void SettingSameParticipationTwice_IsHarmless()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });
        store.SetBackupParticipation(true);   // already included
        Assert.Single(Directory.GetFiles(StorageLocations.BackedUpFolder, "*.json"));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void WithoutPlatformSupport_OptOutIsANoOp()
    {
        StorageLocations.BackupExemptDirectory = null; // desktop
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });

        store.SetBackupParticipation(false);

        Assert.True(store.IsIncludedInBackup);
        Assert.Equal(StorageLocations.BackedUpFolder, store.FolderPath);
    }

    [Fact]
    public void AnOlderStoreLeftInTheExemptLocation_IsStillTheOneOpened()
    {
        // Someone who opted out on 1.0.33 has snippets.json in no_backup. The choice is read from where the
        // store is, and an upgrade must not quietly start a fresh one in the backed-up folder instead.
        Directory.CreateDirectory(StorageLocations.BackupExemptDirectory!);
        File.WriteAllText(
            Path.Combine(StorageLocations.BackupExemptDirectory!, StorageLocations.LegacySnippetsFileName),
            """[{"Id":"11111111-1111-1111-1111-111111111111","Label":"kept","Content":"x"}]""");

        var store = new SnippetStore(seedIfEmpty: false);

        Assert.False(store.IsIncludedInBackup);
        Assert.Equal("kept", Assert.Single(store.Entries).Item.Label);
        Assert.False(Directory.Exists(StorageLocations.BackedUpFolder));
    }

    [Fact]
    public void OptingOut_TakesTheCarriedForwardCopyAlong()
    {
        // snippets.migrated.json is every snippet an older Klippy had. Left in the backed-up folder, a
        // "wipe on uninstall" would wipe nothing.
        Directory.CreateDirectory(StorageLocations.Directory);
        File.WriteAllText(
            Path.Combine(StorageLocations.Directory, StorageLocations.LegacySnippetsFileName),
            """[{"Id":"11111111-1111-1111-1111-111111111111","Label":"secret","Content":"DE44 5001"}]""");
        var store = new SnippetStore(seedIfEmpty: false);
        Assert.True(File.Exists(Path.Combine(StorageLocations.Directory, "snippets.migrated.json")));

        store.SetBackupParticipation(false);

        Assert.False(File.Exists(Path.Combine(StorageLocations.Directory, "snippets.migrated.json")));
        Assert.True(File.Exists(Path.Combine(StorageLocations.BackupExemptDirectory!, "snippets.migrated.json")));
    }

    [Fact]
    public void Moving_LeavesAFileItCouldNotRead_WhereItWas()
    {
        // Never treat a file it cannot read as one it can throw away: the move deletes only what it carried.
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });
        var stray = Path.Combine(StorageLocations.BackedUpFolder, "not-a-snippet.json");
        File.WriteAllText(stray, "{ half written");

        store.SetBackupParticipation(false);

        Assert.True(File.Exists(stray));
        Assert.Single(Directory.GetFiles(StorageLocations.BackupExemptFolder!, "*.json"));
    }
}

[CollectionDefinition("storage-locations", DisableParallelization = true)]
public class StorageLocationsCollection { }
