using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Klippy's files as the Settings screen offers them: one box per file, plus the folder
/// the ones without a path of their own fall into.
///
/// The arrangement exists for a single reason — a snippet store is worth sharing between
/// a Mac and a Windows box, and the variables file that says where <c>%ws%</c> is on each
/// one is exactly what must not go with it.
/// </summary>
[Collection("storage-locations")] // resolves bare names against StorageLocations.Directory
public class StorageFilesSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-files-{Guid.NewGuid():N}");
    private readonly AppSettings _settings;
    private readonly List<ExecutionPlan> _opened = new();

    public StorageFilesSettingsTests()
    {
        Directory.CreateDirectory(_root);
        // Loaded from a temp path, not constructed: Save() follows SourcePath, and a bare
        // `new AppSettings()` would write over the developer's own settings.json.
        _settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SettingsViewModel Vm(OpenFiles? open = null) =>
        new(_settings, close: () => { }, executor: Open, open: open);

    private Task<ExecutionResult> Open(ExecutionPlan plan)
    {
        _opened.Add(plan);
        return Task.FromResult(new ExecutionResult(true, plan.Description));
    }

    // ---- each file on its own ----

    [Fact]
    public void EveryFileHasABoxOfItsOwn()
    {
        var vm = Vm();

        Assert.Equal(new[] { vm.Snippets, vm.History, vm.Commands, vm.Variables }, vm.Files);
        Assert.Equal(
            new[] { "snippets.json", "history.json", "commands.json", "klippy.vars" },
            new[] { vm.Snippets.Watermark, vm.History.Watermark, vm.Commands.Watermark, vm.Variables.Watermark });
    }

    [Fact]
    public void TypingAPath_WritesThroughAndSaves()
    {
        var shared = Path.Combine(_root, "OneDrive", "snippets.json");

        var vm = Vm();
        vm.Snippets.File = shared;

        // It has to reach the instance the app reads...
        Assert.Equal(shared, _settings.SnippetsFile);
        // ...and disk, or it would not survive the restart it is asking for.
        Assert.Equal(shared, AppSettings.Load(_settings.SourcePath).SnippetsFile);
        Assert.Equal(shared, vm.Snippets.Path);
    }

    [Fact]
    public void OneFileMoving_LeavesTheOthersWhereTheyWere()
    {
        // The whole point: shared snippets, local everything else.
        var vm = Vm();
        vm.Snippets.File = Path.Combine(_root, "OneDrive", "snippets.json");

        Assert.Equal("", _settings.HistoryFile);
        Assert.Equal("", _settings.CommandsFile);
        Assert.Equal("", _settings.VariablesFile);
        Assert.Equal(
            Path.Combine(StorageLocations.Directory, StorageLocations.HistoryFileName),
            vm.History.Path);
    }

    [Fact]
    public void ABareName_LandsInTheDataFolder_AndTheLineUnderItSaysWhere()
    {
        var vm = Vm();
        vm.History.File = "work-history.json";

        var resolved = Path.Combine(StorageLocations.Directory, "work-history.json");
        Assert.Equal(resolved, vm.History.Path);
        Assert.Equal($"{resolved}  —  no file yet", vm.History.StatusText);
    }

    [Fact]
    public void ClearingABox_PutsTheFileBackWhereItStarted()
    {
        var vm = Vm();
        vm.Commands.File = Path.Combine(_root, "elsewhere.json");
        vm.Commands.File = "   ";

        Assert.Equal("", _settings.CommandsFile);
        Assert.Equal(
            Path.Combine(StorageLocations.Directory, StorageLocations.CommandsFileName),
            vm.Commands.Path);
    }

    // ---- what the line under the box says ----

    [Fact]
    public void AFileTheRunningAppHasOpen_SaysSo()
    {
        var open = Path.Combine(_root, "snippets.json");
        File.WriteAllText(open, "[]");
        _settings.SnippetsFile = open;

        var vm = Vm(new OpenFiles(Snippets: open));

        Assert.False(vm.Snippets.NeedsRestart);
        Assert.Equal("in use", vm.Snippets.Summary);
    }

    [Fact]
    public void ANewPath_SaysItIsWaitingOnARestart()
    {
        // Every file but the variables one is read at startup and held from then on, so a
        // path typed here is a promise about the next launch. Saying nothing would look
        // like a setting that did not take.
        var open = Path.Combine(_root, "snippets.json");
        File.WriteAllText(open, "[]");

        var vm = Vm(new OpenFiles(Snippets: open));
        vm.Snippets.File = Path.Combine(_root, "somewhere-else.json");

        Assert.True(vm.Snippets.NeedsRestart);
        Assert.Contains("takes effect on restart", vm.Snippets.StatusText);
    }

    [Fact]
    public void TheSamePathInAnotherCase_IsNotANewFile()
    {
        // A Windows user who retypes their own path in another case has not asked for
        // anything to change, and a restart notice would say they had.
        var open = Path.Combine(_root, "snippets.json");
        _settings.SnippetsFile = open.ToUpperInvariant();

        Assert.False(Vm(new OpenFiles(Snippets: open)).Snippets.NeedsRestart);
    }

    [Fact]
    public void WithNothingOpen_NoRestartIsClaimed()
    {
        // A history that is switched off holds no file, so there is none to be stale.
        var vm = Vm(new OpenFiles(History: null));
        vm.History.File = Path.Combine(_root, "history.json");

        Assert.False(vm.History.NeedsRestart);
        Assert.Equal("no file yet", vm.History.Summary);
    }

    [Fact]
    public void TheVariablesFileNeverAsksForARestart()
    {
        // It is re-read whenever it changes, so an edit applies to the next copy. Only
        // the count is worth reporting, and that is what it reports.
        var vm = Vm(new OpenFiles(Snippets: Path.Combine(_root, "snippets.json")));
        var elsewhere = Path.Combine(_root, "work.vars");
        File.WriteAllText(elsewhere, "ws=/usr/bin/webstorm");

        vm.Variables.File = elsewhere;

        Assert.False(vm.Variables.NeedsRestart);
        Assert.Equal("1 variable", vm.Variables.Summary);
    }

    // ---- the folder they fall back into ----

    [Fact]
    public void AnEmptyDataFolderBox_MeansThePlatformsOwnFolder()
    {
        var vm = Vm();

        Assert.Equal("", vm.DataFolder);
        Assert.Equal(StorageLocations.DefaultDirectory, vm.DataFolderPath);
        Assert.Equal(StorageLocations.DefaultDirectory, vm.DataFolderWatermark);

        // The watermark is the folder itself, so printing it again underneath would read
        // like a second setting. A file's watermark is a bare name, which is why those
        // rows do resolve it on screen.
        Assert.DoesNotContain(StorageLocations.DefaultDirectory, vm.DataFolderStatusText);
        Assert.Contains(StorageLocations.Directory, vm.Snippets.StatusText);
    }

    [Fact]
    public void TypingADataFolder_SavesIt_AndSaysItWaitsForARestart()
    {
        var vm = Vm();
        vm.DataFolder = _root;

        Assert.Equal(_root, _settings.DataDirectory);
        Assert.Equal(_root, AppSettings.Load(_settings.SourcePath).DataDirectory);
        Assert.Equal("takes effect on restart", vm.DataFolderSummary);
    }

    [Fact]
    public void TypingADataFolder_CreatesNothingAndMovesNothing()
    {
        // A folder taken up mid-session would leave every open store deciding what to do
        // with the file it already had; and a folder conjured up by typing is one more
        // empty folder to wonder about.
        var never = Path.Combine(_root, "not-yet");
        var before = StorageLocations.Directory;

        Vm().DataFolder = never;

        Assert.False(Directory.Exists(never));
        Assert.Equal(before, StorageLocations.Directory);
    }

    [Fact]
    public void AFolderKlippyCannotUse_IsKeptButSaidToBeUnusable()
    {
        // Losing what was typed under the cursor teaches nobody why it was wrong.
        var vm = Vm();
        vm.DataFolder = "klippy-data";

        Assert.Equal("klippy-data", _settings.DataDirectory);
        Assert.Contains("not an absolute path", vm.DataFolderStatusText);
        Assert.Contains(StorageLocations.Directory, vm.DataFolderStatusText);
    }

    [Fact]
    public async Task TheDataFolderOpensWhereTheBoxPointsIt()
    {
        var vm = Vm();
        vm.DataFolder = _root;

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        var plan = Assert.Single(_opened);
        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(_root, plan.Target);
    }

    // ---- the backup choice, where there is one ----

    [Fact]
    public void NamingTheSnippetsFile_RetiresTheBackupChoice()
    {
        // The toggle moves the store between a backed-up folder and a backup-exempt one.
        // A named path has already answered that, and the toggle would have nowhere to
        // move it to that the setting would not immediately name back.
        var exempt = StorageLocations.BackupExemptDirectory;
        var snippets = StorageLocations.SnippetsFile;
        try
        {
            StorageLocations.BackupExemptDirectory = Path.Combine(_root, "no_backup");
            StorageLocations.SnippetsFile = "";
            Assert.True(StorageLocations.SupportsBackupOptOut);

            StorageLocations.SnippetsFile = Path.Combine(_root, "OneDrive", "snippets.json");
            Assert.False(StorageLocations.SupportsBackupOptOut);
            Assert.Null(StorageLocations.BackupExemptPath);
        }
        finally
        {
            StorageLocations.BackupExemptDirectory = exempt;
            StorageLocations.SnippetsFile = snippets;
        }
    }
}
