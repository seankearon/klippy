using System;
using System.IO;
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
