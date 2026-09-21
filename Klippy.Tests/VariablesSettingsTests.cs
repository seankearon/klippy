using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The variables file as the Settings screen offers it: a path you can type, a file you
/// can open, and a folder you can get to — the three things you would otherwise need
/// settings.json and a file manager for.
/// </summary>
[Collection("storage-locations")] // resolves bare names against StorageLocations.Directory
public class VariablesSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-vs-{Guid.NewGuid():N}");
    private readonly AppSettings _settings;
    private readonly List<ExecutionPlan> _opened = new();

    public VariablesSettingsTests()
    {
        Directory.CreateDirectory(_root);
        // Loaded from a temp path, not constructed: Save() follows SourcePath, and a bare
        // `new AppSettings()` would write over the developer's own settings.json.
        _settings = AppSettings.Load(Path.Combine(_root, "settings.json"));
        _settings.VariablesFile = Path.Combine(_root, "klippy.vars");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SettingsViewModel Vm(bool canOpen = true) =>
        new(_settings, close: () => { }, executor: canOpen ? Open : null);

    private Task<ExecutionResult> Open(ExecutionPlan plan)
    {
        _opened.Add(plan);
        return Task.FromResult(new ExecutionResult(true, plan.Description));
    }

    // ---- typing a path ----

    [Fact]
    public void ThePathIsShownAsConfigured_AndResolved()
    {
        var vm = Vm();

        Assert.Equal(Path.Combine(_root, "klippy.vars"), vm.Variables.File);
        Assert.Equal(Path.Combine(_root, "klippy.vars"), vm.Variables.Path);
        Assert.Equal("no file yet", vm.Variables.Summary);
        Assert.False(vm.Variables.FileExists);
    }

    [Fact]
    public void TypingAPath_WritesThroughAndSaves()
    {
        var elsewhere = Path.Combine(_root, "work.vars");
        File.WriteAllText(elsewhere, "ws=C:\\tools\\webstorm64.exe\nsrc=D:\\src");

        var vm = Vm();
        vm.Variables.File = elsewhere;

        // It has to reach the instance a copy reads...
        Assert.Equal(elsewhere, _settings.VariablesFile);
        // ...and disk, or it would not survive a restart.
        Assert.Equal(elsewhere, AppSettings.Load(_settings.SourcePath).VariablesFile);
        // ...and the screen has to follow it to the new file.
        Assert.Equal(elsewhere, vm.Variables.Path);
        Assert.Equal("2 variables", vm.Variables.Summary);
        Assert.True(vm.Variables.FileExists);
    }

    [Fact]
    public void ABareName_LandsInTheDataFolder_AndTheLineUnderIsSaysWhere()
    {
        var vm = Vm();
        vm.Variables.File = "work.vars";

        var resolved = Path.Combine(StorageLocations.Directory, "work.vars");
        Assert.Equal(resolved, vm.Variables.Path);
        Assert.Equal($"{resolved}  —  no file yet", vm.Variables.StatusText);
    }

    [Fact]
    public void AnAbsolutePathIsNotRepeatedUnderItself()
    {
        // The box already shows it; a second copy underneath reads like a second setting.
        Assert.Equal("no file yet", Vm().Variables.StatusText);
    }

    [Fact]
    public void NorIsItRepeatedWhenItWasTypedWithTheOtherSeparator()
    {
        // "D:/klippy/work.vars" is how the same path arrives from a person who types Unix
        // separators out of habit. Klippy settles it to backslashes on the way through, so
        // the resolved path stops matching the box character for character while still
        // being the very same answer — which is not worth a second line.
        var vm = Vm();
        vm.Variables.File = Path.Combine(_root, "work.vars")
                               .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.Equal(Path.Combine(_root, "work.vars"), vm.Variables.Path);
        Assert.Equal("no file yet", vm.Variables.StatusText);
    }

    [Fact]
    public void ClearingTheBox_MeansTheDefaultName_NotNoFile()
    {
        // There is no such thing as "no variables file": one that is not there defines
        // nothing, which is the same answer as an empty box asking for. The setting is
        // cleared rather than filled in with the default, so all five boxes read the same
        // way — empty is the usual name in the usual folder.
        var vm = Vm();
        vm.Variables.File = "   ";

        Assert.Equal("", _settings.VariablesFile);
        Assert.Equal(
            Path.Combine(StorageLocations.Directory, KlippyVariables.DefaultFileName),
            vm.Variables.Path);
        Assert.Equal(KlippyVariables.DefaultFileName, vm.Variables.Watermark);
    }

    [Fact]
    public void TheCountDistinguishesAnEmptyFileFromAMissingOne()
    {
        File.WriteAllText(_settings.VariablesFile, "# nothing but a comment\n");

        Assert.Equal("no variables in it", Vm().Variables.Summary);
    }

    [Fact]
    public void TheScreenReadsTheSettingsItEdits_NotTheGlobalOnes()
    {
        // A screen that read AppSettings.Current while writing to an instance would show
        // one file and edit another the moment the two were not the same object.
        var mine = Path.Combine(_root, "mine.vars");
        File.WriteAllText(mine, "only=here");
        _settings.VariablesFile = mine;

        Assert.Equal("1 variable", Vm().Variables.Summary);
        Assert.NotEqual(AppSettings.Current.VariablesFile, _settings.VariablesFile);
    }

    // ---- opening it ----

    [Fact]
    public async Task OpeningAMissingFile_WritesTheExampleFirst()
    {
        var vm = Vm();
        Assert.Equal("Create", vm.Variables.OpenVerb);

        await vm.Variables.OpenCommand.ExecuteAsync(null);

        Assert.True(File.Exists(_settings.VariablesFile));
        Assert.Equal("Open", vm.Variables.OpenVerb);   // it is there now
        Assert.True(vm.Variables.FileExists);

        // Entirely comments, so a file created by accident changes nothing about copying.
        Assert.Equal("no variables in it", vm.Variables.Summary);
        Assert.Contains("%ws%", File.ReadAllText(_settings.VariablesFile));

        var plan = Assert.Single(_opened);
        Assert.Equal(ExecutionKind.Document, plan.Kind);
        Assert.Equal(_settings.VariablesFile, plan.Target);
    }

    [Fact]
    public async Task OpeningAFileThatIsThere_LeavesItExactlyAsItWas()
    {
        const string written = "ws=C:\\tools\\webstorm64.exe";
        File.WriteAllText(_settings.VariablesFile, written);

        var vm = Vm();
        Assert.Equal("Open", vm.Variables.OpenVerb);

        await vm.Variables.OpenCommand.ExecuteAsync(null);

        Assert.Equal(written, File.ReadAllText(_settings.VariablesFile));
        Assert.Equal(ExecutionKind.Document, Assert.Single(_opened).Kind);
    }

    [Fact]
    public async Task TheFoldersOpenAsFolders()
    {
        var vm = Vm();

        await vm.Variables.OpenFolderCommand.ExecuteAsync(null);
        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(2, _opened.Count);
        Assert.All(_opened, plan => Assert.Equal(ExecutionKind.Folder, plan.Kind));
        Assert.Equal(_root, _opened[0].Target);           // the variables file's own
        Assert.Equal(vm.DataFolderPath, _opened[1].Target); // where a bare name lands
    }

    [Fact]
    public async Task AFailureToOpen_IsReportedRatherThanSwallowed()
    {
        var vm = new SettingsViewModel(
            _settings,
            close: () => { },
            executor: _ => Task.FromResult(ExecutionResult.Failed("No file manager here.")));

        await vm.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.Equal("No file manager here.", vm.StatusText);
    }

    [Fact]
    public void WithNothingToOpenWith_TheButtonsAreAbsentRatherThanDead()
    {
        // Mobile has no launcher, so the buttons would be three things that do nothing.
        Assert.False(Vm(canOpen: false).CanOpenFiles);
        Assert.False(Vm(canOpen: false).Variables.CanOpen);
        Assert.False(Vm(canOpen: false).Variables.CanOpenFolder);
        Assert.True(Vm().Variables.CanOpen);
    }

    [Fact]
    public void OnlyTheVariablesFileOffersToCreateItself()
    {
        // A "Create" means Klippy knows what belongs in an empty file. It does for the
        // variables file, which is a commented example; for the rest the store that owns
        // the file writes the first one, when it has something to say.
        var vm = Vm();

        Assert.True(vm.Variables.CanOpen);              // nothing there yet, and still offered
        Assert.Equal("Create", vm.Variables.OpenVerb);
        Assert.False(vm.Snippets.CanOpen);

        // The folder is somewhere to look either way.
        Assert.All(vm.Files, file => Assert.True(file.CanOpenFolder));
    }

    // ---- what "open a document" means to the launcher ----

    [Theory]
    [InlineData(ExecutionPlatform.Windows)]
    [InlineData(ExecutionPlatform.MacOS)]
    [InlineData(ExecutionPlatform.Linux)]
    public void ADocument_GoesToThePlatformsOwnDefault(ExecutionPlatform platform)
    {
        // The same gesture as double-clicking it: whatever the user opens a .vars with.
        var plan = new ExecutionPlan { Kind = ExecutionKind.Document, Target = "/tmp/klippy.vars" };
        var url = new ExecutionPlan { Kind = ExecutionKind.Url, Target = "/tmp/klippy.vars" };

        var command = ExecutionPolicy.Resolve(plan, platform);
        var asLink = ExecutionPolicy.Resolve(url, platform);

        Assert.NotNull(command);
        Assert.NotNull(asLink);

        // A link and a document are one gesture to the OS, so they had better agree.
        // Field by field: LaunchCommand carries a string[], and a record compares one of
        // those by reference.
        Assert.Equal(asLink.FileName, command.FileName);
        Assert.Equal(asLink.Arguments, command.Arguments);
        Assert.Equal(asLink.UseShellExecute, command.UseShellExecute);

        Assert.Equal("Opening klippy.vars", plan.Description);
    }
}
