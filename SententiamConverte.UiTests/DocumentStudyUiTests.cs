using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Wissen → Einarbeiten → Eigene Dokumente: Begriffe mit Definitionssatz herausziehen, abhaken, übernehmen.</summary>
public class DocumentStudyUiTests
{
    [AvaloniaFact]
    public async Task StudiesOwnDocumentsIntoTermbaseAndKnowledge()
    {
        using var home = TestSetup.OwnDataFolder();
        var folder = Path.Combine(TestSetup.Home, "dokumente-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Wartung.txt"),
            "Stand: 01.03.2021\n\nUnter einer Gleitringdichtung versteht man eine axiale Dichtung zwischen Welle und Gehäuse. "
            + "Die Gleitringdichtung (GLRD) wird alle 500 Stunden geprüft.\n\nAls Kavitation bezeichnet man die Bildung von Dampfblasen in der Flüssigkeit.");
        new ReferenceLibrarySettings { Folders = [folder] }.Save();
        var before = AppServices.Ai;
        try
        {
            AppServices.Ai = new AiSettings();                                              // keine KI: nur die Regeln
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            await vm.Resources.ScanLibraryCommand.ExecuteAsync(null);
            vm.SelectedTab = MainViewModel.KnowledgeTab;
            var k = vm.Knowledge;
            k.OnProjectChanged();
            var file = Assert.Single(k.StudyFiles);
            Assert.Equal(("Wartung.txt", true), (file.Name, file.IsChecked));

            await k.ExtractDocumentTermsCommand.ExecuteAsync(null);
            Assert.Contains("Keine KI eingerichtet – es gelten nur die Regeln.", k.DocLog);
            Assert.Contains("2 Begriffe gefunden – prüfen, abhaken und „Übernehmen“.", k.DocLog);
            Assert.Equal(["Gleitringdichtung (GLRD) [de]", "Kavitation [de]"], k.DocumentTerms.Select(r => r.Header).Order());
            var seal = k.DocumentTerms.Single(r => r.Term.Term == "Gleitringdichtung");
            Assert.Equal("Wartung · 2× in den eigenen Fachtexten · Definitionssatz", seal.Info);

            k.DocumentTerms.Single(r => r.Term.Term == "Kavitation").IsChecked = false;      // nur abgehakte werden übernommen
            k.SaveDocumentTermsCommand.Execute(null);
            Assert.Contains("Übernommen: 1 Begriffe in „Terminologie de-DE–en-GB“, 1 Einträge in „Wissen de-DE“.", k.DocLog);
            Assert.Equal("Kavitation", Assert.Single(k.DocumentTerms).Term.Term);
            Assert.Equal("Wissen de-DE", k.SelectedBase?.Name);

            var (_, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, "de-DE", "en-GB", null);
            using (var tb = new Termbase(path))
            {
                var concept = tb.Lookup("de-DE", "Gleitringdichtung").Single();
                Assert.Contains(concept.Terms, t => t is { Text: "GLRD", Status: TermStatus.Admitted });
                Assert.EndsWith("(Wartung)", concept.Definition);
            }
            var entry = k.SelectedBase!.Search("de-DE", "Gleitringdichtung").First();
            Assert.Equal("Wartung (Stand: 01.03.2021)", entry.SourceTitle);

            // im Reiter „Einarbeiten“ sichtbar
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => Equals(i.Header, "Einarbeiten")));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(i => Equals(i.Header, "Einarbeiten"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var header = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Kavitation [de]");
            header.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.True(header.IsEffectivelyVisible);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "52-dokumente-einarbeiten.png"));
        }
        finally
        {
            AppServices.Ai = before;
            ReferenceLibrary.CloseShared();
        }
    }
}
