using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
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
