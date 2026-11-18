using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Klippy.Android;

// Only the window onto the app: the app builder and its customisation are in MainApplication.
[Activity(
    Label = "Klippy",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
