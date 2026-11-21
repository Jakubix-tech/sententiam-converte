using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Resources;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Eigene Fachtexte unter „TM &amp; Terminologie“: Ordner einlesen, Dateien mit Stand sehen, probeweise suchen.</summary>
public class ReferenceLibraryUiTests
{
    [AvaloniaFact]
    public async Task OwnReferenceTextsAreReadListedAndSearched()
    {
        using var home = TestSetup.OwnDataFolder();
        var folder = Path.Combine(TestSetup.Home, "fachtexte-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Wartung.txt"),
            "Stand: 01.03.2021\n\nVor dem Austausch der Gleitringdichtung ist die Pumpe auszuschalten. Die Gleitringdichtungen werden alle 500 Stunden geprüft.");
        new ReferenceLibrarySettings { Folders = [folder] }.Save();
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            vm.SelectedTab = 2;
            var res = vm.Resources;
            res.RefreshLibrary();
            Assert.Equal([folder], res.LibraryFolders);
            Assert.Empty(res.LibraryFiles);

            await res.ScanLibraryCommand.ExecuteAsync(null);
            Assert.Equal("Eingelesen: 1 neu, 0 geändert, 0 entfernt, 0 unverändert.", res.LibraryStatus);
            var row = Assert.Single(res.LibraryFiles);
            Assert.Equal(("Wartung.txt", "Deutsch · 1 Abschnitte · Stand: 01.03.2021"), (row.Name, row.Info));
            Assert.StartsWith("1 Dateien eingelesen, Suchindex", res.LibrarySummary);

            res.LibraryQuery = "Gleitringdichtung";
            res.SearchLibraryCommand.Execute(null);
            var hit = Assert.Single(res.LibraryHits);
            Assert.Equal("Vor dem Austausch der Gleitringdichtung ist die Pumpe auszuschalten.", hit.Quote);
            Assert.Equal("„Gleitringdichtung“ kommt 2-mal vor.", res.LibraryStatus);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Eigene Fachtexte").FindAncestorOfType<Border>()!;
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == hit.Quote);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "36-eigene-fachtexte.png"));

            // nach dem Entfernen des Ordners vergisst der Index die Datei
            await res.RemoveLibraryFolderCommand.ExecuteAsync(folder);
            Assert.Empty(res.LibraryFolders);
            Assert.Empty(res.LibraryFiles);
        }
        finally
        {
            ReferenceLibrary.CloseShared();
        }
    }
}
