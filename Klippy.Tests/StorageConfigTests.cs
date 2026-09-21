using System;
using System.IO;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Where Klippy keeps its files, and the settings that say so: <c>DataDirectory</c> for
/// the folder, and <c>SnippetsFile</c> and <c>VariablesFile</c> for the two that may name
/// their way out of it.
/// </summary>
[Collection("storage-locations")] // mutates StorageLocations statics
public class StorageConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-config-{Guid.NewGuid():N}");
    private readonly string _originalDir = StorageLocations.Directory;
    private readonly string _originalVariablesFile = AppSettings.Current.VariablesFile;
    private readonly string _originalSnippetsFile = StorageLocations.SnippetsFile;
    private readonly string _originalDefaultDir = StorageLocations.DefaultDirectory;

    public void Dispose()
    {
        StorageLocations.Directory = _originalDir;
        StorageLocations.DefaultDirectory = _originalDefaultDir;
        StorageLocations.SnippetsFile = _originalSnippetsFile;
        AppSettings.Current.VariablesFile = _originalVariablesFile;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A throwaway app-data folder, so a test that migrates files never moves anything in
    /// the developer's real one. Returns it, already created.
    /// </summary>
    private string UseAThrowawayAppDataFolder()
    {
        var appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(appData);
        StorageLocations.DefaultDirectory = appData;
        return appData;
    }

    // ---- defaults ----

    [Fact]
    public void ByDefault_EverythingIsInThePlatformAppDataFolder()
    {
        var fresh = new AppSettings();

        // Both empty: a setting left alone asks for the usual name in the usual folder,
        // not for no file at all.
        Assert.Equal("", fresh.DataDirectory);
        Assert.Equal("", fresh.SnippetsFile);
        Assert.Equal("", fresh.VariablesFile);
        Assert.Equal(StorageLocations.DefaultDirectory, _originalDir);
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
        Assert.Equal(Path.Combine(_root, KlippyVariables.DefaultFileName), KlippyVariables.CurrentPath);
        Assert.True(Directory.Exists(_root)); // created, so the first save has somewhere to land

        // But not the history or the MRU: those are about this machine and stay with the
        // settings, whatever the data folder is doing.
        Assert.Equal(
            Path.Combine(StorageLocations.DefaultDirectory, StorageLocations.HistoryFileName),
            StorageLocations.HistoryPath);
        Assert.Equal(
            Path.Combine(StorageLocations.DefaultDirectory, StorageLocations.CommandsFileName),
            StorageLocations.CommandsPath);
    }

    [Fact]
    public void TheHistoryAndMru_StayWithTheSettings_WhereverTheDataGoes()
    {
        // They are the record of what passed through *this* machine. A data folder is a
        // thing people point at a synced drive, and a log of everything copied must not
        // follow the snippets there as a side effect of moving them.
        var appData = UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));

        Assert.Equal(Path.Combine(appData, "clipboard.history.json"), StorageLocations.HistoryPath);
        Assert.Equal(Path.Combine(appData, "command.history.json"), StorageLocations.CommandsPath);
        Assert.Equal(Path.Combine(appData, "settings.json"), StorageLocations.SettingsPath);
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
    public void AFolderCanBeJudgedWithoutBeingTakenUp()
    {
        // What the Settings screen asks as a path is typed: it has to say what is wrong
        // with one without creating the folder or moving the running app into it.
        var unused = Path.Combine(_root, "never-created");

        Assert.True(StorageLocations.IsUsableDirectory(unused, out _));
        Assert.False(Directory.Exists(unused));
        Assert.Equal(_originalDir, StorageLocations.Directory);

        Assert.False(StorageLocations.IsUsableDirectory("klippy-data", out var problem));
        Assert.Contains("not an absolute path", problem);
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
    public void ForwardSlashes_AreSettledToThisMachinesSeparator()
    {
        // Not a rule about tildes: every spelling gets the same treatment, because what is
        // being settled is the path written for this machine, not the shorthand that named
        // it. "%OneDrive%/Klippy" is as easy to type by hand as "~/Klippy" is.
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, _root);
        try
        {
            Assert.Equal(Path.Combine(_root, "Klippy"), StorageLocations.ExpandPath($"%{name}%/Klippy"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void EveryFileSetting_SurvivesASaveAndLoad()
    {
        var path = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(_root);

        new AppSettings
        {
            DataDirectory = _root,
            SnippetsFile = "/shared/snippets.json",
            VariablesFile = "work.vars",
        }.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal(_root, loaded.DataDirectory);
        Assert.Equal("/shared/snippets.json", loaded.SnippetsFile);
        Assert.Equal("work.vars", loaded.VariablesFile);
    }

    // ---- the snippets and variables files ----

    [Fact]
    public void TheSnippetsCanBeSharedWhileEverythingElseStaysPut()
    {
        // The point of the whole arrangement: one snippet store shared between a Mac and a
        // Windows box, while the files that are about *this* machine stay on it.
        var shared = Path.Combine(_root, "OneDrive", "snippets.json");
        Assert.True(StorageLocations.TryApply(
            new AppSettings { DataDirectory = _root, SnippetsFile = shared },
            out var problem));
        Assert.Equal("", problem);

        Assert.Equal(shared, StorageLocations.BackedUpPath);

        // Neither the data folder nor the synced one: the history and the MRU are about
        // this machine, so they sit with the settings.
        Assert.Equal(
            Path.Combine(StorageLocations.DefaultDirectory, StorageLocations.HistoryFileName),
            StorageLocations.HistoryPath);
        Assert.Equal(
            Path.Combine(StorageLocations.DefaultDirectory, StorageLocations.CommandsFileName),
            StorageLocations.CommandsPath);
    }

    [Fact]
    public void ABareFileName_LandsInTheDataFolder()
    {
        // A name without a path is the second file in the same folder — a work set beside
        // a personal one — rather than a path relative to who-knows-what.
        StorageLocations.TryApply(
            new AppSettings { DataDirectory = _root, SnippetsFile = "work-snippets.json" },
            out _);

        Assert.Equal(Path.Combine(_root, "work-snippets.json"), StorageLocations.BackedUpPath);
    }

    [Fact]
    public void ShorthandInAFileName_IsExpandedTheWayAFolderIs()
    {
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, _root);
        try
        {
            StorageLocations.TryApply(new AppSettings { SnippetsFile = $"%{name}%/team.json" }, out _);
            Assert.Equal(Path.Combine(_root, "team.json"), StorageLocations.BackedUpPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AnUnusableDataFolder_StillLeavesTheNamedFilesWhereTheyWereNamed()
    {
        // A mistyped folder costs that one preference. An absolute path elsewhere never
        // depended on it, so it must survive the refusal intact.
        var elsewhere = Path.Combine(_root, "shared", "snippets.json");

        Assert.False(StorageLocations.TryApply(
            new AppSettings { DataDirectory = "klippy-data", SnippetsFile = elsewhere },
            out var problem));
        Assert.Contains("not an absolute path", problem);

        Assert.Equal(elsewhere, StorageLocations.BackedUpPath);
        Assert.Equal(_originalDir, StorageLocations.Directory);
    }

    [Fact]
    public void ClearingTheSnippetsSetting_PutsItBackWhereItStarted()
    {
        StorageLocations.TryApply(new AppSettings { DataDirectory = _root, SnippetsFile = "away.json" }, out _);
        Assert.Equal(Path.Combine(_root, "away.json"), StorageLocations.BackedUpPath);

        StorageLocations.TryApply(new AppSettings { DataDirectory = _root }, out _);
        Assert.Equal(Path.Combine(_root, StorageLocations.FileName), StorageLocations.BackedUpPath);
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

    // ---- the names 1.0.20 used ----

    [Fact]
    public void TheFilesThatHadOtherNames_AreCarriedForward()
    {
        // An upgrade must carry the clipboard history, the command MRU and the defines
        // across rather than start empty beside three files nothing reads.
        var appData = UseAThrowawayAppDataFolder();
        StorageLocations.Directory = appData; // the ordinary case: no DataDirectory set
        File.WriteAllText(Path.Combine(appData, "history.json"), "[]");
        File.WriteAllText(Path.Combine(appData, "commands.json"), "[\"slf\"]");
        File.WriteAllText(Path.Combine(appData, "klippy.vars"), "ws=/usr/bin/webstorm");

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.Equal("[]", File.ReadAllText(StorageLocations.HistoryPath));
        Assert.Equal("[\"slf\"]", File.ReadAllText(StorageLocations.CommandsPath));
        Assert.Equal(
            "ws=/usr/bin/webstorm",
            File.ReadAllText(Path.Combine(appData, KlippyVariables.DefaultFileName)));

        // Moved, not copied: a leftover under the old name is one more file to wonder about.
        Assert.False(File.Exists(Path.Combine(appData, "history.json")));
        Assert.False(File.Exists(Path.Combine(appData, "commands.json")));
        Assert.False(File.Exists(Path.Combine(appData, "klippy.vars")));
    }

    [Fact]
    public void AHistoryLeftInADataFolder_ComesBackToTheAppDataFolder()
    {
        // Anyone who had set DataDirectory on 1.0.20 has a history and an MRU sitting in
        // it. They no longer belong there, so the upgrade fetches them home rather than
        // leaving them behind to be found years later.
        var appData = UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        File.WriteAllText(Path.Combine(_root, "history.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "commands.json"), "[\"slf\"]");

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.Equal("[]", File.ReadAllText(Path.Combine(appData, "clipboard.history.json")));
        Assert.Equal("[\"slf\"]", File.ReadAllText(Path.Combine(appData, "command.history.json")));
        Assert.False(File.Exists(Path.Combine(_root, "history.json")));
        Assert.False(File.Exists(Path.Combine(_root, "commands.json")));
    }

    [Fact]
    public void ClipImagesTravelWithTheHistory()
    {
        // A clip names its blob, so an image left behind in the old folder is a picture
        // the history can no longer show.
        var appData = UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        Directory.CreateDirectory(Path.Combine(_root, "clips"));
        File.WriteAllBytes(Path.Combine(_root, "clips", "abc123.png"), new byte[] { 1, 2, 3 });

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.Equal(
            new byte[] { 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(appData, "clips", "abc123.png")));
        Assert.False(File.Exists(Path.Combine(_root, "clips", "abc123.png")));
    }

    [Fact]
    public void AFileAlreadyAtTheNewName_IsNeverWrittenOver()
    {
        // One already at the new name is the current file; an old one beside it is a
        // leftover, not an update — and this runs on every launch.
        var appData = UseAThrowawayAppDataFolder();
        StorageLocations.Directory = appData;
        File.WriteAllText(Path.Combine(appData, "history.json"), "stale");
        File.WriteAllText(StorageLocations.HistoryPath, "current");

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.Equal("current", File.ReadAllText(StorageLocations.HistoryPath));
        Assert.Equal("stale", File.ReadAllText(Path.Combine(appData, "history.json")));
    }

    [Fact]
    public void AVariablesFileTheSettingsName_IsLeftExactlyWhereItIs()
    {
        // Only the *default* name changed. Someone whose settings say "klippy.vars" has
        // said where they want their defines, and renaming it would point the setting at
        // a file that is no longer there.
        UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        var named = Path.Combine(_root, "klippy.vars");
        File.WriteAllText(named, "ws=/usr/bin/webstorm");

        StorageLocations.MigrateLegacyNames(new AppSettings { VariablesFile = "klippy.vars" });

        Assert.True(File.Exists(named));
        Assert.False(File.Exists(Path.Combine(_root, KlippyVariables.DefaultFileName)));
    }

    [Fact]
    public void TheVariablesFileStaysInTheDataFolder()
    {
        // Unlike the history, it belongs beside the snippets it translates — so it is
        // renamed where it stands rather than fetched into the app-data folder.
        var appData = UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));
        File.WriteAllText(Path.Combine(_root, "klippy.vars"), "ws=/usr/bin/webstorm");

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.True(File.Exists(Path.Combine(_root, KlippyVariables.DefaultFileName)));
        Assert.False(File.Exists(Path.Combine(appData, KlippyVariables.DefaultFileName)));
    }

    [Fact]
    public void WithNothingToCarryForward_MigratingIsHarmless()
    {
        // It runs on every launch, so much the commonest case is that there is nothing
        // to do — and a fresh install must not end up with files it never wrote.
        var appData = UseAThrowawayAppDataFolder();
        Assert.True(StorageLocations.TryUseDirectory(_root, out _));

        StorageLocations.MigrateLegacyNames(new AppSettings());

        Assert.Empty(Directory.GetFiles(_root));
        Assert.Empty(Directory.GetFiles(appData));
    }

    [Fact]
    public void CurrentRereadsTheFileWhenItChanges()
    {
        // The variables file is hand-edited, and "restart Klippy" is a poor answer to a typo.
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
