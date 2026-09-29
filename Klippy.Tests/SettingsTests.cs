using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

public class SettingsTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"klippy-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void MarkdownPreferences_DefaultToTodaysBehaviour()
    {
        // Existing users must not find their copies changed by an upgrade.
        var settings = new AppSettings();
        Assert.True(settings.MarkdownToHtml);
        Assert.True(settings.MarkdownDoubleSpaced);
        Assert.False(settings.MarkdownSanitiseLinks); // a workaround, so opt-in
    }

    [Fact]
    public void MarkdownPreferences_SurviveASaveAndLoad()
    {
        var path = TempSettingsPath();
        try
        {
            new AppSettings { MarkdownToHtml = false, MarkdownDoubleSpaced = false, MarkdownSanitiseLinks = true }.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.False(loaded.MarkdownToHtml);
            Assert.False(loaded.MarkdownDoubleSpaced);
            Assert.True(loaded.MarkdownSanitiseLinks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtraKindsToOpen_AreNoneUntilSomeoneWritesSome()
    {
        // Opening a kind of file is running it, for some kinds, so the list starts empty and
        // is written by hand — which is how it has to read back.
        Assert.Empty(new AppSettings().ExecuteOpenExtensions);

        var path = TempSettingsPath();
        try
        {
            File.WriteAllText(path, """{ "ExecuteOpenExtensions": [".slnx", "sln"] }""");
            Assert.Equal(new[] { ".slnx", "sln" }, AppSettings.Load(path).ExecuteOpenExtensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsViewModel_TogglingWritesThroughToTheSharedSettings()
    {
        // Loaded from a temp path, not constructed: Save() follows SourcePath, and a bare
        // `new AppSettings()` here would write over the developer's own settings.json.
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new SettingsViewModel(settings, close: () => { });

            Assert.True(vm.MarkdownToHtml);

            // A toggle has to reach the instance BuildPayload reads, or the next copy
            // would still use the old preference...
            vm.MarkdownToHtml = false;
            Assert.False(settings.MarkdownToHtml);

            vm.MarkdownDoubleSpaced = false;
            Assert.False(settings.MarkdownDoubleSpaced);

            vm.MarkdownSanitiseLinks = true;
            Assert.True(settings.MarkdownSanitiseLinks);

            // ...and it has to reach disk, or it would not survive a restart.
            var reloaded = AppSettings.Load(path);
            Assert.False(reloaded.MarkdownToHtml);
            Assert.False(reloaded.MarkdownDoubleSpaced);
            Assert.True(reloaded.MarkdownSanitiseLinks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CloseAfterCopyPreferences_DefaultPerHalfOfTheApp()
    {
        var settings = new AppSettings();

        // A clip is picked to paste it somewhere else, so the window has done its job...
        Assert.True(settings.CloseAfterClipboardCopy);
        // ...whereas snippets get browsed, and copying two in a row is ordinary.
        Assert.False(settings.CloseAfterSnippetCopy);
    }

    [Fact]
    public void CloseAfterCopyPreferences_SurviveASaveAndLoad()
    {
        var path = TempSettingsPath();
        try
        {
            new AppSettings { CloseAfterClipboardCopy = false, CloseAfterSnippetCopy = true }.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.False(loaded.CloseAfterClipboardCopy);
            Assert.True(loaded.CloseAfterSnippetCopy);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsViewModel_CloseAfterCopyTogglesWriteThroughAndSave()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new SettingsViewModel(settings, close: () => { });

            Assert.True(vm.CloseAfterClipboardCopy);
            Assert.False(vm.CloseAfterSnippetCopy);

            vm.CloseAfterClipboardCopy = false;
            vm.CloseAfterSnippetCopy = true;

            Assert.False(settings.CloseAfterClipboardCopy);
            Assert.True(settings.CloseAfterSnippetCopy);

            var reloaded = AppSettings.Load(path);
            Assert.False(reloaded.CloseAfterClipboardCopy);
            Assert.True(reloaded.CloseAfterSnippetCopy);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlacementPreference_DefaultsToTodaysBehaviour()
    {
        // An upgrade must not start moving people's windows about. Remembered is also a real
        // choice, not just the old behaviour: a launcher that always comes back to the same
        // corner is one you stop having to look for.
        Assert.Equal(LauncherPlacement.Remembered, new AppSettings().ParsedSummonPlacement);
    }

    [Fact]
    public void PlacementPreference_SurvivesASaveAndLoad()
    {
        var path = TempSettingsPath();
        try
        {
            new AppSettings { SummonPlacement = nameof(LauncherPlacement.Pointer) }.Save(path);
            Assert.Equal(LauncherPlacement.Pointer, AppSettings.Load(path).ParsedSummonPlacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsViewModel_PlacementWritesThroughAndSaves()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new SettingsViewModel(settings, close: () => { });

            Assert.Equal(LauncherPlacement.Remembered, vm.SummonPlacement);
            Assert.True(vm.IsPlacementRemembered);

            vm.PickPlacementCommand.Execute(LauncherPlacement.Centre);

            // The accent has to move off the chip that lost the pick and onto the one that
            // took it — nothing groups the three, so both ends are manual.
            Assert.False(vm.IsPlacementRemembered);
            Assert.True(vm.IsPlacementCentre);

            Assert.Equal(LauncherPlacement.Centre, settings.ParsedSummonPlacement);
            Assert.Equal(LauncherPlacement.Centre, AppSettings.Load(path).ParsedSummonPlacement);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMistypedPlacement_CostsOnlyThePlacement_NotTheWholeFile()
    {
        // settings.json invites hand-editing, and "Center" for "Centre" is the obvious slip
        // in a codebase that spells things British. A throwing parse would reach Load's
        // catch-all and hand back all-defaults — losing both hotkeys, the history limit and
        // the exclusion list — and the next toggle would write that over the file for good.
        var path = TempSettingsPath();
        try
        {
            new AppSettings
            {
                Hotkey = "Ctrl+Alt+P",
                HistoryLimit = 42,
                SummonPlacement = "Center",
            }.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.Equal(LauncherPlacement.Remembered, loaded.ParsedSummonPlacement);
            Assert.Equal("Ctrl+Alt+P", loaded.Hotkey);
            Assert.Equal(42, loaded.HistoryLimit);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- what the preferences actually do ----

    private static MainViewModel CopyVm(AppSettings settings, out ClipHistoryStore history)
    {
        var store = new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-{Guid.NewGuid():N}.json"));
        history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "a captured clip" });

        var vm = new MainViewModel(store, history, settings)
        {
            ClipboardWriter = _ => Task.CompletedTask,
        };
        return vm;
    }

    [AvaloniaFact]
    public void CopyRaisesCloseRequested_OnlyForTheHalfTheUserAskedFor()
    {
        var settings = new AppSettings { CloseAfterClipboardCopy = true, CloseAfterSnippetCopy = false };
        var vm = CopyVm(settings, out _);

        int closes = 0;
        vm.CloseRequested += () => closes++;

        vm.CopySelectedCommand.Execute(null); // a snippet
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, closes);

        vm.ShowHistory();
        vm.CopyCommand.Execute(vm.Filtered[0]); // a clip
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, closes);
    }

    [AvaloniaFact]
    public void CopyRaisesCloseRequested_WithThePreferencesTheOtherWayRound()
    {
        var settings = new AppSettings { CloseAfterClipboardCopy = false, CloseAfterSnippetCopy = true };
        var vm = CopyVm(settings, out _);

        int closes = 0;
        vm.CloseRequested += () => closes++;

        vm.ShowHistory();
        vm.CopyCommand.Execute(vm.Filtered[0]); // a clip
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, closes);

        vm.ShowSnippets();
        vm.CopySelectedCommand.Execute(null); // a snippet
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, closes);
    }

    [AvaloniaFact]
    public void ATogglePutsTheNextCopyOnTheNewSetting_WithNoRestart()
    {
        // The overlay writes through to the same instance the copy reads, which is the
        // whole reason there is no OK button.
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = CopyVm(settings, out _);

            int closes = 0;
            vm.CloseRequested += () => closes++;

            var overlay = new SettingsViewModel(settings, close: () => { });
            overlay.CloseAfterSnippetCopy = true;

            vm.CopySelectedCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, closes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LaunchPreferences_DefaultToTheCautiousAnswer()
    {
        var settings = new AppSettings();

        // On, because the offer only ever appears once the list is empty and still takes a
        // deliberate Enter...
        Assert.True(settings.ExecuteUnmatched);
        // ...and the two guards around it start on, because this is the one feature that
        // runs things.
        Assert.True(settings.ExecuteVerifyPaths);
        Assert.True(settings.ExecuteConfirmSystemActions);
    }

    [Fact]
    public void LaunchPreferences_SurviveASaveAndLoad()
    {
        var path = TempSettingsPath();
        try
        {
            new AppSettings
            {
                ExecuteUnmatched = false,
                ExecuteVerifyPaths = false,
                ExecuteConfirmSystemActions = false,
            }.Save(path);

            var loaded = AppSettings.Load(path);
            Assert.False(loaded.ExecuteUnmatched);
            Assert.False(loaded.ExecuteVerifyPaths);
            Assert.False(loaded.ExecuteConfirmSystemActions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SettingsViewModel_LaunchTogglesWriteThroughAndSave()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new SettingsViewModel(settings, close: () => { });

            Assert.True(vm.ExecuteUnmatched);
            Assert.True(vm.ExecuteVerifyPaths);
            Assert.True(vm.ExecuteConfirmSystemActions);

            vm.ExecuteVerifyPaths = false;
            vm.ExecuteConfirmSystemActions = false;

            // Has to reach the instance the main view model reads, or the very next search
            // would still be checked and the next "restart" would still ask.
            Assert.False(settings.ExecuteVerifyPaths);
            Assert.False(settings.ExecuteConfirmSystemActions);

            var reloaded = AppSettings.Load(path);
            Assert.False(reloaded.ExecuteVerifyPaths);
            Assert.False(reloaded.ExecuteConfirmSystemActions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- kinds of file to open ----

    [Fact]
    public void OpenKinds_ShowWhatTheFileHolds_AsTheListKeepsThem()
    {
        var settings = new AppSettings { ExecuteOpenExtensions = ["sln", "*.SLNX", ".sln", ""] };
        var vm = new SettingsViewModel(settings, close: () => { });

        Assert.Equal(new[] { ".sln", ".slnx" }, vm.OpenKinds);
        Assert.True(vm.HasOpenKinds);
    }

    [Fact]
    public void AddingAKind_SavesIt_AndEmptiesTheBox()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new SettingsViewModel(settings, close: () => { });

            vm.NewOpenKind = "*.SLNX";
            vm.AddOpenKindCommand.Execute(null);

            Assert.Equal(new[] { ".slnx" }, vm.OpenKinds);
            Assert.Equal("", vm.NewOpenKind);
            Assert.Equal(new[] { ".slnx" }, settings.ExecuteOpenExtensions); // what Enter reads
            Assert.Equal(new[] { ".slnx" }, AppSettings.Load(path).ExecuteOpenExtensions);

            // The same kind again is nothing new, however it is written.
            vm.NewOpenKind = "slnx";
            vm.AddOpenKindCommand.Execute(null);
            Assert.Single(vm.OpenKinds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemovingAKind_SavesThat()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            settings.ExecuteOpenExtensions = [".slnx", ".sln"];
            var vm = new SettingsViewModel(settings, close: () => { });

            vm.RemoveOpenKindCommand.Execute(".sln");

            Assert.Equal(new[] { ".slnx" }, vm.OpenKinds);
            Assert.Equal(new[] { ".slnx" }, AppSettings.Load(path).ExecuteOpenExtensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".ps1", "rules of their own")]  // a script already runs
    [InlineData("exe", "rules of their own")]   // and so does an application
    [InlineData(".pdf", "open already")]        // built in
    [InlineData("tar.gz", "not an extension")]  // a file's extension is its last one
    [InlineData(@"C:\x.slnx", "not an extension")]
    public void WhatTheListCannotHold_IsSaid_AndLeftInTheBoxToCorrect(string typed, string said)
    {
        var path = TempSettingsPath();
        try
        {
            var vm = new SettingsViewModel(AppSettings.Load(path), close: () => { });

            vm.NewOpenKind = typed;
            vm.AddOpenKindCommand.Execute(null);

            Assert.Empty(vm.OpenKinds);
            Assert.Contains(said, vm.StatusText);
            Assert.Equal(typed, vm.NewOpenKind);
            Assert.False(File.Exists(path)); // nothing was saved
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- kinds of link to open, on the same list ----

    [Fact]
    public void KindsOfLink_AreNoneUntilSomeoneAddsSome_AndReachTheRulesWithTheirColon()
    {
        Assert.Empty(new AppSettings().ExecuteOpenSchemes);

        // Written by hand, the setting already says which list it is: the colon is optional
        // there, and the rules are handed one either way.
        var settings = new AppSettings { ExecuteOpenExtensions = [".slnx"], ExecuteOpenSchemes = ["vscode:", "obsidian", " "] };
        Assert.Equal(new[] { ".slnx", "vscode:", "obsidian:" }, settings.AlsoOpens);
    }

    [Fact]
    public void AKindOfLink_GoesOnTheSameList_AndIsSavedWithTheLinks()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            settings.ExecuteOpenExtensions = [".slnx"];
            var vm = new SettingsViewModel(settings, close: () => { });

            vm.NewOpenKind = "VSCode://";
            vm.AddOpenKindCommand.Execute(null);

            Assert.Equal(new[] { ".slnx", "vscode:" }, vm.OpenKinds);
            Assert.Equal("", vm.NewOpenKind);
            Assert.Equal(new[] { ".slnx" }, settings.ExecuteOpenExtensions);
            Assert.Equal(new[] { "vscode:" }, settings.ExecuteOpenSchemes);
            Assert.Equal(new[] { "vscode:" }, AppSettings.Load(path).ExecuteOpenSchemes);

            // Taken off again from its chip, and the kinds of file are left as they were.
            vm.RemoveOpenKindCommand.Execute("vscode:");
            Assert.Empty(AppSettings.Load(path).ExecuteOpenSchemes);
            Assert.Equal(new[] { ".slnx" }, AppSettings.Load(path).ExecuteOpenExtensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KindsOfLink_ShowWhatTheFileHolds_AfterTheKindsOfFile()
    {
        var settings = new AppSettings { ExecuteOpenExtensions = ["sln"], ExecuteOpenSchemes = ["Obsidian", "vscode://", "obsidian:"] };
        var vm = new SettingsViewModel(settings, close: () => { });

        Assert.Equal(new[] { ".sln", "obsidian:", "vscode:" }, vm.OpenKinds);

        // One written into the other list is shown as what its colon says it is.
        var misplaced = new SettingsViewModel(new AppSettings { ExecuteOpenExtensions = ["zoommtg:"] }, close: () => { });
        Assert.Equal(new[] { "zoommtg:" }, misplaced.OpenKinds);
    }

    [Theory]
    [InlineData("file:", "rules of their own")]   // a file: link is the path it spells
    [InlineData("https://", "open already")]
    [InlineData("ms-settings:", "open already")]
    [InlineData("javascript:", "never opened")]
    [InlineData("c:", "not a kind of link")]
    public void AKindOfLinkTheListCannotHold_IsSaid_AndLeftInTheBoxToCorrect(string typed, string said)
    {
        var path = TempSettingsPath();
        try
        {
            var vm = new SettingsViewModel(AppSettings.Load(path), close: () => { });

            vm.NewOpenKind = typed;
            vm.AddOpenKindCommand.Execute(null);

            Assert.Empty(vm.OpenKinds);
            Assert.Contains(said, vm.StatusText);
            Assert.Equal(typed, vm.NewOpenKind);
            Assert.False(File.Exists(path)); // nothing was saved
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddWaitsForSomethingToAdd()
    {
        var vm = new SettingsViewModel(new AppSettings(), close: () => { });
        Assert.False(vm.AddOpenKindCommand.CanExecute(null));

        vm.NewOpenKind = ".slnx";
        Assert.True(vm.AddOpenKindCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TheSettingsScreen_AddsAKindOnEnter_AndAChipClickTakesItOff()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = AppSettings.Load(path);
            var vm = new MainViewModel(
                new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-{Guid.NewGuid():N}.json")),
                settings: settings);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.OpenSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NewOpenKindBox");
            box.Focus();
            box.Text = ".slnx";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\n");
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(vm.Settings); // Enter added; it did not close the screen
            Assert.Equal(new[] { ".slnx" }, settings.ExecuteOpenExtensions);

            var chip = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.CommandParameter, ".slnx"));
            chip.Command!.Execute(chip.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(settings.ExecuteOpenExtensions);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
                b => Equals(b.CommandParameter, ".slnx"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_OpenAndEscapeCloseTheSettingsOverlay()
    {
        var store = new SnippetStore(Path.Combine(Path.GetTempPath(), $"klippy-{Guid.NewGuid():N}.json"));
        var vm = new MainViewModel(store);

        Assert.Null(vm.Settings);

        vm.OpenSettingsCommand.Execute(null);
        Assert.NotNull(vm.Settings);

        Assert.True(vm.HandleEscape());
        Assert.Null(vm.Settings);
    }
}
