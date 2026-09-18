using System;
using System.IO;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Where Klippy keeps its files, and the settings that say so: <c>DataDirectory</c> and
/// <c>VariablesFile</c>.
/// </summary>
[Collection("storage-locations")] // mutates StorageLocations statics
public class StorageConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-config-{Guid.NewGuid():N}");
    private readonly string _originalDir = StorageLocations.Directory;
    private readonly string _originalVariablesFile = AppSettings.Current.VariablesFile;

    public void Dispose()
    {
        StorageLocations.Directory = _originalDir;
        AppSettings.Current.VariablesFile = _originalVariablesFile;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- defaults ----

    [Fact]
    public void ByDefault_EverythingIsInThePlatformAppDataFolder()
    {
        Assert.Equal("", new AppSettings().DataDirectory);
        Assert.Equal(StorageLocations.DefaultDirectory, _originalDir);
        Assert.Equal(KlippyVariables.DefaultFileName, new AppSettings().VariablesFile);
    }

    [Fact]
    public void SettingsStayPut_WhereverTheDataGoes()
    {
        // settings.json is the file that says where everything else went, so it is the one
        // thing that cannot follow it there.
        var before = StorageLocations.SettingsPath;
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));

        Assert.Equal(before, StorageLocations.SettingsPath);
        Assert.StartsWith(StorageLocations.DefaultDirectory, StorageLocations.SettingsPath);
    }

    // ---- DataDirectory ----

    [Fact]
    public void ADataDirectory_MovesSnippetsHistoryAndVariables()
    {
        Assert.True(StorageLocations.TryUseDirectory(_root, out var problem));
        Assert.Equal("", problem);

        // The suite points this at a throwaway absolute path; put the shipped default back
        // so the assertion is about the folder rather than about the test harness.
        AppSettings.Current.VariablesFile = KlippyVariables.DefaultFileName;

        Assert.Equal(Path.Combine(_root, "snippets.json"), StorageLocations.BackedUpPath);
        Assert.Equal(Path.Combine(_root, "history.json"), StorageLocations.HistoryPath);
        Assert.Equal(Path.Combine(_root, KlippyVariables.DefaultFileName), KlippyVariables.CurrentPath);
        Assert.True(Directory.Exists(_root)); // created, so the first save has somewhere to land
    }

    [Fact]
    public void ARelativeDataDirectory_IsRefused_AndCostsOnlyItself()
    {
        // Relative to what? The exe, the shell's working directory and the app-data folder
        // are three different answers, so the setting insists on being told.
        Assert.False(StorageLocations.TryUseDirectory("klippy-data", out var problem));
        Assert.Contains("not an absolute path", problem);
        Assert.Equal(_originalDir, StorageLocations.Directory);
    }

    [Fact]
    public void AnEmptyDataDirectory_IsRefused()
    {
        Assert.False(StorageLocations.TryUseDirectory("   ", out _));
        Assert.Equal(_originalDir, StorageLocations.Directory);
    }

    [Fact]
    public void EnvironmentVariablesInTheDataDirectory_AreExpanded()
    {
        // "%OneDrive%\Klippy" is how a person writes this into a file by hand.
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, _root);
        try
        {
            Assert.True(StorageLocations.TryUseDirectory($"%{name}%", out _));
            Assert.Equal(_root, StorageLocations.Directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ATildePath_IsExpandedToTheHomeFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Klippy"), StorageLocations.ExpandPath("~/Klippy"));
        Assert.Equal(home, StorageLocations.ExpandPath("~"));
    }

    [Fact]
    public void DataDirectoryAndVariablesFile_SurviveASaveAndLoad()
    {
        var path = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);

        new AppSettings { DataDirectory = _root, VariablesFile = "work.vars" }.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal(_root, loaded.DataDirectory);
        Assert.Equal("work.vars", loaded.VariablesFile);
    }

    // ---- VariablesFile ----

    [Fact]
    public void ABareVariablesFileName_SitsBesideTheSnippets()
    {
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        AppSettings.Current.VariablesFile = "work.vars";

        Assert.Equal(Path.Combine(_root, "work.vars"), KlippyVariables.CurrentPath);
    }

    [Fact]
    public void AnAbsoluteVariablesFile_IsTakenAsGiven()
    {
        // The point of allowing one: a snippet store synced between two machines still
        // reads a variables file that is local to each.
        var elsewhere = Path.Combine(_root, "machine", "local.vars");
        AppSettings.Current.VariablesFile = elsewhere;

        Assert.Equal(elsewhere, KlippyVariables.CurrentPath);
    }

    [Fact]
    public void ABlankVariablesFile_FallsBackToTheDefaultName()
    {
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        AppSettings.Current.VariablesFile = "  ";

        Assert.Equal(Path.Combine(_root, KlippyVariables.DefaultFileName), KlippyVariables.CurrentPath);
    }

    [Fact]
    public void CurrentRereadsTheFileWhenItChanges()
    {
        // klippy.vars is hand-edited, and "restart Klippy" is a poor answer to a typo.
        var path = Path.Combine(_root, "live.vars");
        Directory.CreateDirectory(_root);
        AppSettings.Current.VariablesFile = path;

        Assert.False(KlippyVariables.Current.Exists);

        File.WriteAllText(path, "klippylive=one");
        Assert.Equal("one", KlippyVariables.Current.Get("klippylive"));

        // Stamped to the second on some file systems, so nudge it rather than race it.
        File.WriteAllText(path, "klippylive=two");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("two", KlippyVariables.Current.Get("klippylive"));
    }
}
