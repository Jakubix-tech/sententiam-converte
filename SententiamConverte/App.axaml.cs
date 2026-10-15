using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte;

public partial class App : Application
{
#if DEBUG
    private static bool _devToolsAttached;
#endif

    public override void Initialize()
    {
        Services.L.SetLanguage(Services.AppServices.Ui.UiLanguage);
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        // Nur einmal je Prozess – die Oberflächentests erzeugen pro Test eine eigene App.
        if (!_devToolsAttached)
        {
            _devToolsAttached = true;
            this.AttachDeveloperTools();
        }
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel() };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        _ = Services.AppServices.StartupAsync();
        base.OnFrameworkInitializationCompleted();
    }
}