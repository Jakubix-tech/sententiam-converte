using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Aus Korrekturen lernen: Hinweis im Editor nach der zweiten gleichen Korrektur, Regel in den Einstellungen.</summary>
public class LearningUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ARepeatedCorrectionIsOfferedAsARule()
    {
        using var home = TestSetup.OwnDataFolder();
        CorrectionLearning.StorePath = Path.Combine(TestSetup.Home, "gelernt-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("hilfe.txt", "The user clicks OK.\nThe user waits.\n"u8.ToArray()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;
        Assert.True(vm.Learning.Enabled);                                                  // Vorgabe: eingeschaltet
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Lernen " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "en-GB";
        vm.NewTargets = "de-DE";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;

        // Zweimal dieselbe Korrektur am KI-Vorschlag
        foreach (var (seg, text) in editor.Segments.Zip(["Der Nutzer klickt auf OK.", "Der Nutzer wartet."]))
        {
            editor.Selected = seg;
            seg.Model.Suggestion = text.Replace("Nutzer", "Benutzer");
            seg.Model.Target = text;
            seg.Refresh();
            await Run(editor.ConfirmAndNextCommand);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Learning.HasHint);
        Assert.Contains("„Benutzer“ in „Nutzer“", vm.Learning.HintText);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == vm.Learning.HintText);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "31-gelernt-hinweis.png"));

        await Run(vm.Learning.AcceptHintCommand);
        Assert.False(vm.Learning.HasHint);
        Assert.Equal(RuleState.Active, CorrectionLearning.Rules.Single(r => r.From == "Benutzer").State);
        Assert.Contains(vm.Learning.Rules, r => r.Text == "„Benutzer“ → „Nutzer“");

        // Abschalten: keine Vorgaben mehr an die KI
        vm.Learning.Enabled = false;
        Assert.Null(CorrectionLearning.Instructions("de-DE"));
        vm.Learning.Enabled = true;
        Assert.NotNull(CorrectionLearning.Instructions("de-DE"));
        vm.SelectedTab = MainViewModel.SettingsTab;
        Dispatcher.UIThread.RunJobs();
        var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Aus Korrekturen lernen");
        card.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "32-gelernt-regeln.png"));
        CorrectionLearning.Remove(CorrectionLearning.Rules.Single(r => r.From == "Benutzer"));
    }
}
