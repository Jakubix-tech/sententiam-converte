using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>
/// Zwischen Projekten umschalten: von jedem Reiter aus über die Statusleiste, im Reiter Projekt über die Liste der
/// anderen Projekte (mit Namen statt Ordnerpfaden). Im Editor geht es dort weiter, wo man im Projekt stand (Datei,
/// Segment); laufende KI-Arbeit hält vor dem Wechsel an, statt im neuen Projekt weiterzuschreiben.
/// </summary>
public class ProjectSwitchUiTests
{
    [AvaloniaFact]
    public async Task SwitchingProjectsReturnsToWhereYouWereAndStopsRunningAiWork()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        var started = 0;
        var cancelled = 0;
        var before = (EditorViewModel.FindOverride, EditorViewModel.ResearchOverride);
        EditorViewModel.FindOverride = segments => Task.FromResult(segments[0].Source.Contains("Laufrad")
            ? [new ResearchItem { Expression = "Laufrad", Category = "term", SegmentIds = [segments[0].Id] }]
            : new List<ResearchItem>());
        EditorViewModel.ResearchOverride = async (item, from, to, ct) =>
        {
            started++;
            try { await Task.Delay(Timeout.Infinite, ct); }            // antwortet nie – bis angehalten wird
            catch (OperationCanceledException) { cancelled++; throw; }
            throw new InvalidOperationException();
        };
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            var tag = Guid.NewGuid().ToString("N")[..4];

            // Projekt A: Datei öffnen, Segment 2 wählen
            var a = await NewProject(vm, platform, "Pumpe " + tag, "pumpe.txt", "Die Pumpe läuft.\nDer Motor brummt.\nDas Ventil schließt.\n");
            vm.Editor.Selected = vm.Editor.Segments[1];

            // Projekt B anlegen – A wird verlassen und steht in der Liste mit Namen und Sprachen
            var b = await NewProject(vm, platform, "Laufrad " + tag, "laufrad.txt", "Das Gehäuse ist dicht.\nDas Laufrad dreht sich.\nDie Welle ruht.\n");
            Assert.Equal(b.Name, vm.ProjectSwitchLabel);
            var rowA = Assert.Single(vm.OtherProjects, r => r.Name == a.Name);
            Assert.StartsWith("de-DE → en-GB · ", rowA.Details);
            Assert.True(vm.RecentRows.Single(r => r.Name == b.Name).IsCurrent);
            Assert.DoesNotContain(vm.OtherProjects, r => r.Name == b.Name);

            // die Statusleiste zeigt das Projekt; ihre Liste nennt beide, das offene mit Haken
            var switcher = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ProjectSwitcher");
            Assert.Contains(switcher.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == b.Name);
            switcher.Flyout!.ShowAt(switcher);
            Dispatcher.UIThread.RunJobs();
            var listed = ((Flyout)switcher.Flyout).Content is Control c ? c.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList() : [];
            Assert.Contains(a.Name, listed);
            Assert.Contains(b.Name, listed);
            switcher.Flyout.Hide();

            // in B: Segment 2 recherchieren lassen – die KI arbeitet noch, als umgeschaltet wird
            var editor = vm.Editor;
            editor.SidePanelTab = 8;
            editor.Selected = editor.Segments[1];
            await WaitFor(() => started == 1 && editor.IsResearching);
            Assert.True(editor.IsAiWorking);
            Assert.Equal(1, vm.SelectedTab);

            await Run(vm.SwitchProjectCommand, a.Folder);
            Assert.Equal(a.Folder, vm.Project!.Folder);
            Assert.Equal(1, cancelled);                                    // angehalten, nicht im Hintergrund weiter
            Assert.False(editor.IsAiWorking);
            Assert.Equal(1, vm.SelectedTab);                               // man bleibt im Editor …
            Assert.StartsWith("pumpe.txt", editor.DocumentTitle);           // … in der Datei von A …
            Assert.Equal(2, editor.Selected!.Id);                          // … am Segment, an dem man stand
            Assert.Equal(1, started);                                       // nichts neu begonnen
            Assert.True(vm.OtherProjects.Single(r => r.Name == b.Name) is { IsCurrent: false });

            // und zurück: B wieder an seiner Stelle
            editor.SidePanelTab = 0;
            await Run(vm.SwitchProjectCommand, b.Folder);
            Assert.StartsWith("laufrad.txt", vm.Editor.DocumentTitle);
            Assert.Equal(2, vm.Editor.Selected!.Id);

            // aus dem Reiter Projekt umschalten: die Stelle wird erst beim Gang in den Editor geöffnet
            vm.SelectedTab = 0;
            await Run(vm.SwitchProjectCommand, a.Folder);
            Assert.Equal((a.Folder, 0), (vm.Project!.Folder, vm.SelectedTab));
            Assert.Null(vm.Editor.CurrentFile);
            vm.SelectedTab = 1;
            await WaitFor(() => vm.Editor.CurrentFile != null);
            Assert.Equal(2, vm.Editor.Selected!.Id);

            // das offene Projekt nochmals wählen: nichts geschieht
            await Run(vm.SwitchProjectCommand, a.Folder);
            Assert.Equal(2, vm.Editor.Selected!.Id);

            // aus der Liste nehmen: Ordner bleibt, Projekt verschwindet nur aus der Liste
            await Run(vm.ForgetProjectCommand, b.Folder);
            Assert.DoesNotContain(vm.OtherProjects, r => r.Name == b.Name);
            Assert.DoesNotContain(vm.RecentProjects, f => UiSettings.SamePath(f, b.Folder));
            Assert.True(File.Exists(Path.Combine(b.Folder, TranslationProject.FileName)));
            // das offene Projekt lässt sich nicht herausnehmen
            await Run(vm.ForgetProjectCommand, a.Folder);
            Assert.Contains(vm.RecentRows, r => r.Name == a.Name);
        }
        finally
        {
            (EditorViewModel.FindOverride, EditorViewModel.ResearchOverride) = before;
        }
    }

    private static async Task<TranslationProject> NewProject(MainViewModel vm, FakePlatform platform, string name, string file, string text)
    {
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = name;
        vm.NewSource = "de-DE";
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        Assert.Equal(name, vm.Project?.Name);
        platform.FilesToPick.Clear();
        platform.FilesToPick.Add((file, System.Text.Encoding.UTF8.GetBytes(text)));
        await Run(vm.AddFilesCommand);
        await Run(vm.OpenInEditorCommand, vm.Files.Single(f => f.Name == file));
        return vm.Project!;
    }

    private static async Task Run(CommunityToolkit.Mvvm.Input.IRelayCommand command, object? parameter = null)
    {
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Bedingung nicht erreicht");
    }
}
