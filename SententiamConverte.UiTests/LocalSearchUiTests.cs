using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>„KI einrichten“ → Websuche: ohne Docker bietet ein Knopf an, Docker zu installieren und SearXNG einzurichten.</summary>
public class LocalSearchUiTests
{
    [AvaloniaFact]
    public async Task WithoutDockerOneButtonOffersTheWholeSetup()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
        window.Show();
        vm.SelectedTab = MainViewModel.AiTab;
        await vm.Settings.RefreshLocalSearch();

        Assert.True(vm.Settings.DockerMissing);                          // TestSetup: kein Docker
        Assert.Equal("Docker ist nicht installiert.", vm.Settings.LocalSearchStatus);
        Assert.Equal(OperatingSystem.IsWindows() ? "Docker installieren und Websuche einrichten" : "Websuche einrichten", vm.Settings.SetUpLocalSearchText);
        Assert.EndsWith("docker", vm.Settings.DockerFolder);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Kostenlose Websuche auf diesem Rechner (SearXNG)").FindAncestorOfType<Border>()!;
        card.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == vm.Settings.SetUpLocalSearchText);
        Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Docker ist nicht installiert. Mit „Docker installieren“") == true);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "37-websuche-searxng.png"));
    }

    /// <summary>Mehrere Suchdienste ankreuzen: jeder mit eigener Adresse bzw. eigenem Schlüssel, alle zusammen gespeichert.</summary>
    [AvaloniaFact]
    public void SeveralSearchServicesAreTickedTogether()
    {
        using var home = TestSetup.OwnDataFolder();
        var before = SententiamConverte.Services.AppServices.Ai;
        try
        {
            var vm = new MainViewModel();
            var s = vm.Settings;
            Assert.Equal(["Wikipedia", "SearXng", "Tavily", "Brave", "ClaudeWebSearch"], s.SearchSources.Select(r => r.Kind.ToString()));
            var row = (string kind) => s.SearchSources.First(r => r.Kind.ToString() == kind);
            row("Wikipedia").Enabled = true;
            row("SearXng").Enabled = true;
            row("SearXng").BaseUrl = "http://127.0.0.1:8888";
            row("Tavily").Enabled = true;
            row("Tavily").ApiKey = "tvly-test";
            Assert.True(row("Tavily").ShowKey);
            Assert.False(row("Brave").ShowKey);
            s.SaveCommand.Execute(null);

            var saved = SententiamConverte.Services.AppServices.Ai.Search;
            Assert.Equal(["Wikipedia", "SearXng", "Tavily"], saved.ActiveSources().Select(x => x.Kind.ToString()));
            Assert.Equal(("SearXng", "http://127.0.0.1:8888"), (saved.Kind.ToString(), saved.BaseUrl));          // für ältere Fassungen
            Assert.Equal("Wikipedia + SearXNG + Tavily", SententiamConverte.Core.Ai.SearchProviderFactory.NameFor(SententiamConverte.Services.AppServices.Ai));
            Assert.Equal("tvly-test", s.SearchSources.First(r => r.Kind.ToString() == "Tavily").ApiKey);       // nach dem Neuladen noch da
        }
        finally
        {
            SententiamConverte.Services.AppServices.Ai = before;
        }
    }
}
