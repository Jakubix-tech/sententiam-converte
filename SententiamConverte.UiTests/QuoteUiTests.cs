using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Services;

namespace SententiamConverte.UiTests;

/// <summary>Angebot aus der Analyse im Reiter Projekt.</summary>
public class QuoteUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TheAnalysisGivesAPriceAndAWordQuote()
    {
        var (vm, window, platform) = await ReviewUiTests.TranslatedProject("Angebot");
        Assert.False(vm.HasQuote);
        await Run(vm.AnalyzeCommand);
        Assert.True(vm.HasQuote);
        vm.RatePrice = 0.20m;
        vm.WeightRows.First(w => w.Band == MatchBand.Repetition).Weight = 0;    // Wiederholungen gratis
        Assert.Contains("EUR", vm.QuoteText);
        Assert.Contains("gesamt", vm.QuoteText);
        Assert.Equal(0, vm.Project!.Rates.Weights[MatchBand.Repetition]);       // im Projekt gespeichert
        Assert.Equal(0.20m, AppServices.Ui.LastRates!.Price);                   // Vorlage für neue Projekte

        await Run(vm.ExportQuoteCommand);
        Assert.Contains(platform.Saved.Keys, k => k.EndsWith("_Angebot.docx"));
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Angebot als Word speichern …").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "44-angebot.png"));
    }
}
