using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Ai.LocalMt;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Offline-Übersetzung auf der Seite „KI einrichten“: Richtung wählen, Zustand sehen, geladene Modelle entfernen.</summary>
public class LocalMtUiTests
{
    [AvaloniaFact]
    public void OfflineCardShowsRouteSizeAndDownloadedModels()
    {
        var folder = Path.Combine(TestSetup.Home, "mt-" + Guid.NewGuid().ToString("N")[..6]);
        LocalTranslation.Folder = folder;
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            vm.SelectedTab = MainViewModel.AiTab;
            var setup = vm.Settings.Setup;
            setup.SelectedMt = MachineTranslationModel.Services.First(s => s.Id == "lokal");
            Assert.True(setup.IsLocalMt);
            setup.MtFrom = "de-DE";
            setup.MtTo = "pl-PL";
            var size = LocalTranslation.Route("de-DE", "pl-PL")!.Steps[0].Model.SizeText;
            Assert.StartsWith($"Direkt: opus-mt-de-pl (Apache-2.0) – einmaliger Download {size}, frei auf dem Laufwerk:", setup.LocalMtStatus);
            Assert.True(setup.CanDownloadLocalMt);
            setup.MtTo = "pt-BR";
            Assert.StartsWith("Über Englisch (zwei Modelle, schwächer): opus-mt-de-en (Apache-2.0) + opus-mt-en-ROMANCE", setup.LocalMtStatus);
            setup.MtTo = "ja-JP";
            Assert.Equal("Für diese Richtung gibt es kein Modell.", setup.LocalMtStatus);
            Assert.False(setup.CanDownloadLocalMt);

            // „Geladen“: Dateien in der richtigen Größe (Inhalt spielt für die Anzeige keine Rolle)
            var model = LocalTranslation.Route("de-DE", "pl-PL")!.Steps[0].Model;
            Directory.CreateDirectory(Path.Combine(folder, model.Id));
            foreach (var f in model.Files)
                using (var s = File.Create(Path.Combine(folder, model.Id, f.Name))) s.SetLength(f.Size);
            setup.MtTo = "pl-PL";
            Assert.Equal("Direkt: opus-mt-de-pl (Apache-2.0) – geladen, bereit.", setup.LocalMtStatus);
            Assert.False(setup.CanDownloadLocalMt);
            Assert.Equal(["opus-mt-de-pl"], setup.LocalMtInstalled.Select(m => m.Id));

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Maschinelle Übersetzung").FindAncestorOfType<Border>()!;
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Geladene Modelle");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "35-offline-uebersetzung.png"));

            setup.RemoveLocalMtCommand.Execute(setup.LocalMtInstalled[0]);
            Assert.Empty(setup.LocalMtInstalled);
            Assert.False(Directory.Exists(Path.Combine(folder, model.Id)));
            Assert.True(setup.CanDownloadLocalMt);
        }
        finally
        {
            LocalTranslation.Folder = "";
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
