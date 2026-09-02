using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Klippy.Services;
using Klippy.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Klippy.Tests;

internal static class TestSettings
{
    /// <summary>
    /// Points the suite at a throwaway settings file before any test runs. The UI and
    /// <c>RichTextClipboard.BuildPayload</c> both reach for <see cref="AppSettings.Current"/>
    /// by default, and neither reading nor writing the developer's real settings.json is
    /// acceptable: it makes screenshots machine-dependent and a toggle test destructive.
    /// </summary>
    [ModuleInitializer]
    internal static void UseAThrowawayFile() =>
        AppSettings.Current = AppSettings.Load(
            Path.Combine(Path.GetTempPath(), $"klippy-test-settings-{Guid.NewGuid():N}.json"));
}

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia() // real (Skia) rendering so captured frames show fonts and brushes
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
