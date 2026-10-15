using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Hilfe im Programm: F1, Kapitel, Suche.</summary>
public class HelpUiTests
{
    [AvaloniaFact]
    public void F1OpensTheManualAndSearchFindsChapters()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainViewModel.HelpTab, vm.SelectedTab);

        // Alle drei Texte sind eingebettet
        var help = vm.Help;
        var titles = help.Chapters.Select(c => c.Title).ToList();
        Assert.Contains("Tastenkürzel", titles);
        Assert.Contains("Translation Memory und Kontext", titles);
        Assert.Contains("API-Referenz", titles);
        Assert.Contains("Erweiterungen entwickeln", titles);
        Assert.True(titles.Count >= 20, string.Join(", ", titles));

        // Tabellen werden zu Zeilen mit Schlüssel und Wert
        var keys = help.Chapters.First(c => c.Title == "Tastenkürzel").Blocks.Where(b => b.IsRow).ToList();
        Assert.Contains(keys, b => b.Key == "Strg+Eingabe" && b.Text.StartsWith("Segment bestätigen"));

        // Suche
        help.Query = "Kontexttreffer";
        Assert.Contains(help.Chapters, c => c.Title == "Translation Memory und Kontext");
        Assert.True(help.Chapters.Count < titles.Count);
        help.Query = "gibtesnichtxyz";
        Assert.Empty(help.Chapters);
        Assert.Equal("Nichts gefunden.", help.ResultInfo);
        help.Query = "";
        help.Show("Translation Memory");
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "20-hilfe.png"));

        help.Show("API-Referenz");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(help.Selected!.Blocks, b => b.IsRow && b.Key.Contains("/projects/{project}/pretranslate"));
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "21-hilfe-api.png"));
    }
}
