using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Fehlende gemeinsame Datenbanken: Meldung beim Öffnen, mit einem Klick anlegen, in der Übersicht finden.</summary>
public class DatabasesUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task MissingDatabasesAreReportedAndCreatedWithOneClick()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Datenbanken " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "de-DE";
        vm.NewTargets = "fi-FI";                                       // Sprachpaar, das sonst kein Test benutzt
        await Run(vm.CreateProjectCommand);

        Assert.Contains("TM de-DE–fi-FI", vm.DatabaseHint);              // Meldung beim Öffnen
        var hint = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == vm.DatabaseHint);
        Assert.True(hint.IsEffectivelyVisible);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "34-datenbanken-fehlen.png"));

        await Run(vm.CreateRecommendedDatabasesCommand);
        Assert.Equal("", vm.DatabaseHint);
        var project = vm.Project!;
        Assert.Contains(project.Tms, t => t.Location.EndsWith("TM de-DE–fi-FI.sctm"));
        Assert.Contains(project.Termbases, t => t.Location.EndsWith("Terminologie de-DE–fi-FI.sctb"));
        Assert.Contains(project.KnowledgeBases, t => t.Location.EndsWith("Wissen fi-FI.sckb"));
        Assert.Contains(LanguageResources.Load(), r => r.Location.EndsWith("TM de-DE–fi-FI.sctm") && r.Languages.SequenceEqual(["de-DE", "fi-FI"]));

        vm.SelectedTab = 2;                                              // Übersicht in „TM & Terminologie“
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(vm.Resources.Databases, d => d.Name == "TM de-DE–fi-FI");
        Assert.StartsWith(DatabaseCatalog.Folder, row.Path);
        Assert.Contains("fi-FI", row.Origin);
        Assert.Empty(vm.Resources.Recommendations);
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Datenbanken: Empfehlungen und Übersicht")
            .FindAncestorOfType<Border>()!.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "35-datenbanken-uebersicht.png"));
    }
}
