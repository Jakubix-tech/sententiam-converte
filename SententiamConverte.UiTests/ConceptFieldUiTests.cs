using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Dossier mit Begriffsfeld: Geschwisterbegriffe, der gemeinte hervorgehoben, Unterscheidung, Beratung, Rückfrage.</summary>
public class ConceptFieldUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task DossierShowsTheConceptFieldAndAdvice()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("turm.txt", "Der Turm trägt ein steiles Spitzdach mit vier gleichen Flächen.\n"u8.ToArray()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
        window.Show();
        AppServices.Platform = platform;
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Turm " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "de-DE";
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        DossierStore.Add(vm.Project!, new TermDossier
        {
            Expression = "Spitzdach", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Meaning = "Steiles Dach mit Spitze.",
            Recommended = "pyramid roof", Strategy = "funktionale Entsprechung",
            Sources = [new SourceRef("en-GB", "Roof shapes", "https://roofs.example/types") { Number = 1, Quote = "A pyramid roof has four equal slopes." }],
            ConceptField = new ConceptField
            {
                Umbrella = "Dachform", UmbrellaTarget = "roof shape", Meant = "Zeltdach",
                Members =
                [
                    new() { SourceTerm = "Spitzdach", IsQueried = true },
                    new() { SourceTerm = "Satteldach", Features = ["zwei geneigte Flächen", "Giebel"], TargetTerm = "gable roof", TargetEvidence = 3, LinkedByWikipedia = true },
                    new() { SourceTerm = "Zeltdach", Features = ["vier gleich geneigte Flächen", "Spitze"], TargetTerm = "pyramid roof", TargetAlternatives = ["tented roof"], TargetEvidence = 2 },
                ],
                HowToTell = ["Wie viele Dachflächen?", "Gibt es Giebel?"],
                ForThisContext = "Vier gleiche Flächen mit Spitze: Zeltdach – pyramid roof.",
                AskTheClient = "Hat das Dach Giebel oder treffen sich alle Flächen in einer Spitze?",
            },
        });
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;
        editor.Selected = editor.Segments[0];
        await editor.ShowOrResearchAsync("Spitzdach");
        var row = Assert.Single(editor.Dossiers);

        Assert.True(row.HasConceptField);
        Assert.Equal("Begriffsfeld: Dachform → roof shape", row.ConceptFieldHeader);
        Assert.Equal(["Spitzdach  →  ?", "Satteldach  →  gable roof", "Zeltdach  →  pyramid roof  (auch: tented roof)"], row.ConceptMembers.Select(m => m.Terms));
        Assert.Equal("▶", row.ConceptMembers[2].Marker);                                  // gemeint ist das Zeltdach
        Assert.Equal("belegt in 3 Quellen · Wikipedia verknüpft", row.ConceptMembers[1].Evidence);
        Assert.Equal("nicht belegt", row.ConceptMembers[0].Evidence);
        Assert.Equal("• Wie viele Dachflächen?\n• Gibt es Giebel?", row.ConceptHowToTell);
        Assert.True(row.HasConceptAskTheClient);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Vier gleiche Flächen mit Spitze: Zeltdach – pyramid roof.");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Rückfrage an den Auftraggeber");
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Rückfrage an den Auftraggeber").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "39-begriffsfeld.png"));
    }
}
