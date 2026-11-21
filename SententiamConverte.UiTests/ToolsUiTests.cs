using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Privacy;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Hilfsmittel: Vorschläge zum Projekt, Katalog mit Filtern, Nachschlagen (im Datenausgang sichtbar).</summary>
[Collection("Outbound")]                                                     // prüft den Datenausgang – nicht parallel zu anderen
public class ToolsUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Quellen-Datenbank: belegte Seiten vor vorgeschlagenen, filtern, entfernen.</summary>
    [AvaloniaFact]
    public async Task SourceDatabaseShowsLearnedSites()
    {
        using var home = TestSetup.OwnDataFolder();
        Core.Resources.SiteCatalog.RecordEvidence([new Core.Agents.SourceRef("de-DE", "BRAO", "https://www.gesetze-im-internet.de/brao/__1.html")], ["recht"]);
        Core.Resources.SiteCatalog.RecordSuggestions([new Core.Agents.SiteHint("brak.de", "de-DE", "Berufsrecht der Anwälte")], ["recht"]);
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        vm.SelectedTab = MainViewModel.ToolsTab;
        var tools = vm.Tools;
        Assert.Equal(["gesetze-im-internet.de", "brak.de"], tools.Sites.Select(s => s.Domain));
        Assert.Equal("2 Seiten, davon 1 mit Belegen.", tools.SitesSummary);
        Assert.Contains("1× Beleg", tools.Sites[0].Info);
        Assert.Contains("von der KI vorgeschlagen", tools.Sites[1].Info);
        Assert.Equal("Berufsrecht der Anwälte", tools.Sites[1].Note);

        tools.SiteFilter = "brak";
        Assert.Equal(["brak.de"], tools.Sites.Select(s => s.Domain));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Quellen-Datenbank").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "36-quellen-datenbank.png"));
        await Run(tools.RemoveSiteCommand, tools.Sites[0]);
        Assert.Empty(tools.Sites);
        tools.SiteFilter = "";
        Assert.Equal(["gesetze-im-internet.de"], tools.Sites.Select(s => s.Domain));
    }

    [AvaloniaFact]
    public async Task SuggestsResourcesForTheProjectAndLooksUpTerms()
    {
        using var home = TestSetup.OwnDataFolder();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Mietvertrag " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "de-CH";
        vm.NewTargets = "fr-CH, rm-CH";
        vm.NewTopic = "Mietrecht";
        await Run(vm.CreateProjectCommand);
        Assert.NotNull(vm.Project);

        // Projektseite: passende Hilfsmittel mit Begründung
        var tools = vm.Tools;
        Assert.True(tools.HasSuggestions);
        Assert.Contains(tools.Suggestions, s => s.Tool.Id == "termdat");
        Assert.Contains(tools.Suggestions, s => s.Tool.Id == "pledari");
        vm.SelectedTab = 0;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "TERMDAT" && t.IsEffectivelyVisible);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "28-hilfsmittel-projekt.png"));
        Assert.Contains("Recht und Verwaltung", tools.Suggestions.First(s => s.Tool.Id == "termdat").Reason);

        // Seite „Hilfsmittel“: Filter nach Art und Sprache
        vm.SelectedTab = MainViewModel.ToolsTab;
        Dispatcher.UIThread.RunJobs();
        var all = tools.Rows.Count;
        tools.KindIndex = 4;                                                               // Rechtsquellen
        Assert.All(tools.Rows, r => Assert.Equal(Core.Resources.ToolKind.Law, r.Tool.Kind));
        tools.LanguageFilter = "rm-CH";
        Assert.Contains(tools.Rows, r => r.Tool.Id == "fedlex");                          // dazu EUR-Lex und CURIA (alle Sprachen)
        Assert.All(tools.Rows, r => Assert.True(r.Tool.Covers("rm-CH")));
        Assert.DoesNotContain(tools.Rows, r => r.Tool.Id == "legifrance");
        tools.KindIndex = 0;
        tools.LanguageFilter = null;
        Assert.Equal(all, tools.Rows.Count);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "29-hilfsmittel.png"));

        // Nachschlagen: aus dem Editor vorbereitet, Aufruf erscheint im Datenausgang (Browser gibt es im Test nicht)
        tools.Prepare("Mietzins", "de-CH");
        Assert.Contains(tools.LookupTools, r => r.Tool.Id == "dwds");
        Outbound.Clear();
        await Run(tools.LookUpCommand, tools.LookupTools.First(r => r.Tool.Id == "dwds"));
        Assert.Contains(Outbound.Entries, e => e.Service == "www.dwds.de" && e.Content.Contains("https://www.dwds.de/wb/Mietzins"));
    }
}
