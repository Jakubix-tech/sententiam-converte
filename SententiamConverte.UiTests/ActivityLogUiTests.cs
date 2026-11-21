using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Diagnostics;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Ablaufprotokoll: Schritte der Oberfläche landen in der Datei; ausschalten wirkt sofort und bleibt gespeichert.</summary>
[Collection("Outbound")]
public class ActivityLogUiTests
{
    private static string Log() => File.Exists(ActivityLog.CurrentFile) ? File.ReadAllText(ActivityLog.CurrentFile) : "";

    [AvaloniaFact]
    public void StepsOfTheInterfaceAreLoggedAndLoggingCanBeSwitchedOff()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var marker = Guid.NewGuid().ToString("N")[..10];
        try
        {
            vm.SelectedTab = MainViewModel.AiTab;
            vm.Status = "Projekt geöffnet " + marker;
            Assert.Contains("\"schritt\":\"Reiter gewechselt\",\"ziel\":\"KI einrichten\"", Log());
            Assert.Contains($"\"bereich\":\"Oberfläche\",\"schritt\":\"Main.Status\",\"inhalt\":\"Projekt geöffnet {marker}\"", Log());

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Ablaufprotokoll schreiben");

            vm.Settings.Outbound.ActivityLogEnabled = false;
            Assert.False(AppServices.Ai.ActivityLog.Enabled);
            vm.Status = "Nicht im Protokoll " + marker;
            Assert.DoesNotContain("Nicht im Protokoll " + marker, Log());
        }
        finally
        {
            vm.Settings.Outbound.ActivityLogEnabled = true;
        }
        vm.Status = "Wieder im Protokoll " + marker;
        Assert.Contains("Wieder im Protokoll " + marker, Log());
    }
}
