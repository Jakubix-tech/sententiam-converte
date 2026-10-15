using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Englische Oberfläche: Tabelle vollständig, Platzhalter stimmig, Fenster tatsächlich englisch.</summary>
public class LocalizationUiTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "SententiamConverte.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Quellcode nicht gefunden");
    }

    private static string Placeholders(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order());

    [Fact]
    public void EveryTextOfTheUserInterfaceHasAnEnglishTranslation()
    {
        var table = L.Table("en");
        Assert.True(table.Count > 500, $"nur {table.Count} Einträge");

        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "SententiamConverte", "Views"), "*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{l:T '([^']*)'\}"))
            {
                var key = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!table.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        Assert.Empty(missing);

        Assert.Empty(table.Where(kv => Placeholders(kv.Key) != Placeholders(kv.Value)).Select(kv => kv.Key));
    }

    [AvaloniaFact]
    public void TheUserInterfaceSpeaksEnglish()
    {
        L.SetLanguage("en");
        try
        {
            // Meldungen mit Werten: Vorlage greift, der Wert bleibt erhalten
            Assert.NotEqual("Gespeichert.", L.T("Gespeichert."));
            var created = L.T("Projekt „Vertrag 7“ angelegt. Jetzt Dateien hinzufügen.");
            Assert.Contains("Vertrag 7", created);
            Assert.DoesNotContain("angelegt", created);
            Assert.Equal("unbekannter Text bleibt", L.T("unbekannter Text bleibt"));

            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var headers = window.GetVisualDescendants().OfType<TabItem>().Select(t => t.Header as string).ToList();
            Assert.Contains("Editor", headers);
            Assert.DoesNotContain("Einstellungen", headers);
            Assert.DoesNotContain("Erweiterungen", headers);
            Assert.DoesNotContain("Hilfe", headers);

            // Laufzeitmeldung in der Statusleiste geht durch den Konverter
            vm.Status = "Gespeichert.";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == L.T("Gespeichert."));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Gespeichert.");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "22-englisch-projekt.png"));

            // Auswahllisten aus dem ViewModel
            Assert.DoesNotContain("Alle Segmente", vm.Editor.FilterNames);

            // Englische Hilfe
            vm.SelectedTab = MainViewModel.HelpTab;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(vm.Help.Chapters, c => c.Title == "Tastenkürzel");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "23-englisch-hilfe.png"));
            vm.SelectedTab = 5;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "24-englisch-einstellungen.png"));
        }
        finally
        {
            L.SetLanguage("de");
        }
    }
}
