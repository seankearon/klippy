using Avalonia;
using Avalonia.Headless;
using Klippy.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Klippy.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia() // real (Skia) rendering so captured frames show fonts and brushes
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
