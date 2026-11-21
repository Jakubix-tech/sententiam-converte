using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Fachbegriffe vor dem Übersetzen aus den Projektdateien gewinnen.</summary>
public class TermHarvestUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TermsFromTheProjectFilesGoIntoTheTermbase()
    {
        using var home = TestSetup.OwnDataFolder();                        // legt gemeinsame Datenbanken an
        using var server = ImportTranslationsUiTests.FakeAi(out var port);
        var before = AppServices.Ai;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var platform = new FakePlatform();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Begriffe " + Guid.NewGuid().ToString("N")[..4];
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            platform.FilesToPick.Add(("mietvertrag.docx", ImportTranslationsUiTests.Docx(
                "Der Mieter zahlt die Kaution vor Beginn des Mietvertrags.",
                "Die Kaution beträgt drei Monatsmieten.",
                "Der Mietvertrag kann vom Mieter gekündigt werden.")));
            await Run(vm.AddFilesCommand);

            var harvest = vm.Resources.Harvest;
            Assert.Equal("en-GB", harvest.TargetLanguage);
            Assert.True(harvest.AiReady);

            // ohne KI: Kandidaten nach Häufigkeit, Übersetzung von Hand
            harvest.UseAi = false;
            await Run(harvest.FindCommand);
            Assert.Contains(harvest.Terms, t => t.Source == "Kaution" && t.Target == "");

            // mit KI: Auswahl und Übersetzungsvorschläge
            harvest.UseAi = true;
            await Run(harvest.FindCommand);
            Assert.Contains(harvest.Terms, t => t is { Source: "Mietvertrag", Target: "tenancy agreement" });
            vm.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == "Fachbegriffe aus den Projektdateien gewinnen").BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "46-fachbegriffe.png"));

            await Run(harvest.AddTermsCommand);
            Assert.Empty(harvest.Terms);
            var tb = vm.Project!.Termbases.OfType<Termbase>().First(t => t.Name == "Terminologie de-DE–en-GB");
            Assert.Contains(tb.AllConcepts(), c => c.Terms.Any(t => t.Text == "tenancy agreement"));

            await Run(harvest.FindCommand);                                     // schon Bekanntes kommt nicht wieder
            Assert.DoesNotContain(harvest.Terms, t => t.Source == "Mietvertrag");
        }
        finally
        {
            AppServices.Ai = before;
            server.Stop();
        }
    }
}
