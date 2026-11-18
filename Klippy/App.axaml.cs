using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Klippy.ViewModels;
using Klippy.Views;

namespace Klippy;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Nothing to strip to stop Avalonia validating alongside the CommunityToolkit: from
            // Avalonia 12 its DataAnnotations plugin is opt-in (AppBuilder.WithDataAnnotationsValidation).
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // Android, from Avalonia 12: the app outlives its activities, and each one asks
            // for a view of its own. They share one view model, so an activity Android
            // recreates comes back to the snippets and search it left.
            var vm = new MainViewModel();
            activity.MainViewFactory = () => new MainView { DataContext = vm };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
