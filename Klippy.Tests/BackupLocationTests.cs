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
    private readonly string? _originalExempt = StorageLocations.BackupExemptDirectory;

    public BackupLocationTests()
    {
        StorageLocations.Directory = Path.Combine(_root, "appdata", "Klippy");
        StorageLocations.BackupExemptDirectory = Path.Combine(_root, "no_backup", "Klippy");
    }

    public void Dispose()
    {
        StorageLocations.Directory = _originalDir;
        StorageLocations.BackupExemptDirectory = _originalExempt;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FreshStore_UsesBackedUpLocation()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });

        Assert.True(store.IsIncludedInBackup);
        Assert.Equal(StorageLocations.BackedUpPath, store.FilePath);
        Assert.True(File.Exists(StorageLocations.BackedUpPath));
    }

    [Fact]
    public void OptingOut_MovesFileToBackupExemptLocation()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "secret", Content = "DE44 5001" });

        store.SetBackupParticipation(false);

        Assert.False(store.IsIncludedInBackup);
        Assert.True(File.Exists(StorageLocations.BackupExemptPath!));
        Assert.False(File.Exists(StorageLocations.BackedUpPath)); // no copy left behind to be backed up
        Assert.Equal("secret", Assert.Single(store.Entries).Snippet.Label);
    }

    [Fact]
    public void OptingBackIn_MovesFileBack()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });
        store.SetBackupParticipation(false);
        store.SetBackupParticipation(true);

        Assert.True(store.IsIncludedInBackup);
        Assert.True(File.Exists(StorageLocations.BackedUpPath));
        Assert.False(File.Exists(StorageLocations.BackupExemptPath!));
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
        Assert.Equal("kept", Assert.Single(reopened.Entries).Snippet.Label);
    }

    [Fact]
    public void SettingSameParticipationTwice_IsHarmless()
    {
        var store = new SnippetStore(seedIfEmpty: false);
        store.Add(new Snippet { Label = "a", Content = "x" });
        store.SetBackupParticipation(true);   // already included
        Assert.True(File.Exists(StorageLocations.BackedUpPath));
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
        Assert.Equal(StorageLocations.BackedUpPath, store.FilePath);
    }
}

[CollectionDefinition("storage-locations", DisableParallelization = true)]
public class StorageLocationsCollection { }
