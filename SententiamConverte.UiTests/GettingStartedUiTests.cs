using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>„Erste Schritte“: neue Benutzer kommen ohne Vorwissen vom leeren Programm bis zur ersten offenen Datei.</summary>
public class GettingStartedUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ANewUserGetsFromAnEmptyProgramToTheFirstFile()
    {
        using var home = TestSetup.OwnDataFolder();                        // legt gemeinsame Datenbanken an
        var before = AppServices.Ai;
        try
        {
            AppServices.Ai = new AiSettings();                                     // noch keine KI
            AppServices.Ui.HideGettingStarted = false;
            var platform = new FakePlatform();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            AppServices.Platform = platform;
            var start = vm.Start;
            Assert.True(start.IsVisible);
            Assert.False(start.AiDone || start.ProjectDone || start.CanTranslate);
            Assert.Contains("0 von 3", start.Progress);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "40-erste-schritte.png"));

            vm.SelectedTab = 1;                                                    // leerer Editor erklärt, wie es weitergeht
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.Editor.HasDocument);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "41-editor-leer.png"));
            await Run(vm.Editor.GoToProjectCommand);
            Assert.Equal(0, vm.SelectedTab);

            // 1. KI: eine eingerichtete KI zählt als erledigt
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://127.0.0.1:9/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            start.Refresh();
            Assert.True(start.AiDone);
            Assert.Contains("Prüf-KI", start.AiStatus);

            // 2. Projekt: Dateien wählen → Name kommt aus der Datei, alles andere in einem Schritt
            platform.FilesToPick.Add(("Mietvertrag Müller.docx", UiFlowTests.SampleDocx()));
            await Run(start.PickFilesCommand);
            Assert.Equal("Mietvertrag Müller", start.Name);
            start.Name += " " + Guid.NewGuid().ToString("N")[..4];
            start.Targets = "en-GB";
            start.Confidential = true;
            await Run(start.CreateProjectCommand);
            var p = vm.Project!;
            Assert.True(start.ProjectDone);
            Assert.True(p.Privacy.Enabled && p.Privacy.Asked);                     // vertraulich, ohne Rückfrage danach
            Assert.Single(vm.Files);
            Assert.Contains(LanguageResources.Load(), r => r.Kind == ResourceKind.Tm && r.AppliesTo("en-GB"));   // gemeinsame Datenbanken angelegt
            Assert.Empty(vm.DatabaseHint);
            Assert.Contains("3 von 3", start.Progress);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "42-erste-schritte-erledigt.png"));

            // 3. alte Übersetzungen
            await Run(start.ImportOldCommand);
            Assert.True(vm.Resources.ImportTranslations.IsOpen);
            await Run(vm.Resources.ImportTranslations.CloseCommand);

            // 4. übersetzen
            await Run(start.TranslateCommand);
            Assert.Equal(1, vm.SelectedTab);
            Assert.True(vm.Editor.HasDocument);
            vm.Editor.Close();

            await Run(start.HideCommand);
            Assert.False(start.IsVisible);
            await Run(start.ShowCommand);
            Assert.True(start.IsVisible);
        }
        finally
        {
            AppServices.Ai = before;
        }
    }
}
