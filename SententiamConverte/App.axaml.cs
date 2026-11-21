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

        StartActivityLog();
        _ = Services.AppServices.StartupAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private static bool _logStarted;

    /// <summary>Ablaufprotokoll: Programmstart mit Fassung und System, alte Protokolle aufräumen, Abstürze festhalten.</summary>
    private static void StartActivityLog()
    {
        if (_logStarted) return;
        _logStarted = true;
        _ = Services.AppServices.Ai;                                       // Einstellungen laden (Protokoll ein/aus)
        Core.Diagnostics.ActivityLog.Cleanup();
        Core.Diagnostics.ActivityLog.Write("Programm", "gestartet", null,
            $"Sententiam converte {typeof(App).Assembly.GetName().Version?.ToString(3)}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
            $"Oberfläche {Services.L.Language}");
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Diagnostics.ActivityLog.Write("Programm", "Absturz", null, e.ExceptionObject.ToString());
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            Core.Diagnostics.ActivityLog.Write("Programm", "Fehler im Hintergrund", null, e.Exception.ToString());
    }
}