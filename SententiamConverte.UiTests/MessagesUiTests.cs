using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SententiamConverte.Core.Agents;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>
/// Recherche und Meldungen bleiben: Nach dem Schließen und erneuten Öffnen sind die gefundenen Ausdrücke, ihre Dossiers,
/// die Bewertung und die Meldungen der KI wieder da – ohne dass die KI etwas noch einmal liest oder recherchiert; ein
/// gleicher Satz und ein anders erfasster Ausdruck („Spaltringe“) brauchen ebenfalls nichts Neues. Der Reiter
/// „Meldungen“ zeigt alles zum gewählten Segment, ausgeschaltet alle; die Zeichen am Segment zeigen ihre Meldungen.
/// </summary>
public class MessagesUiTests
{
    [AvaloniaFact]
    public async Task ResearchAndMessagesSurviveClosingAndReopening()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("spaltring.txt", "Der Spaltring sitzt am Laufrad.\nDie Pumpe läuft.\nDer Spaltring sitzt am Laufrad.\nNeue Spaltringe einbauen.\n"u8.ToArray()));
        var scanned = new List<string>();
        var researched = new List<string>();
        var before = (EditorViewModel.FindOverride, EditorViewModel.ResearchOverride);
        EditorViewModel.FindOverride = segments =>
        {
            var source = segments[0].Source;
            scanned.Add(source);
            // die KI erfasst denselben Ausdruck mal so, mal so
            return Task.FromResult(source.Contains("Spaltringe") ? [new ResearchItem { Expression = "Spaltringe", Category = "term", SegmentIds = [segments[0].Id] }]
                : source.Contains("Spaltring") ? [new ResearchItem { Expression = "Spaltring", Category = "term", SegmentIds = [segments[0].Id] }]
                : new List<ResearchItem>());
        };
        EditorViewModel.ResearchOverride = (item, from, to, ct) =>
        {
            researched.Add(item.Expression);
            return Task.FromResult(new TermDossier
            {
                Expression = item.Expression, SourceLanguage = from, TargetLanguage = to, Recommended = "wear ring",
                Meaning = "Verschleißring zwischen Laufrad und Gehäuse.",
            });
        };
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Spaltring " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            var file = vm.Files.First(f => f.Name.Contains("spaltring"));
            await Run(vm.OpenInEditorCommand, file);
            var editor = vm.Editor;

            // Recherche-Reiter offen: Segment 1 wird gelesen und recherchiert, Segment 2 gelesen (nichts gefunden)
            editor.SidePanelTab = 8;
            editor.Selected = editor.Segments[0];
            await WaitFor(() => researched.Count == 1 && !editor.IsResearching && !editor.IsScanning);
            editor.Selected = editor.Segments[1];
            await WaitFor(() => scanned.Count == 2 && !editor.IsScanning);
            // derselbe Satz an anderer Stelle: schon gelesen, schon recherchiert
            editor.Selected = editor.Segments[2];
            await WaitFor(() => !editor.IsResearching && !editor.IsScanning);
            Assert.Equal(2, scanned.Count);
            Assert.Equal("Spaltring  →  wear ring   [Güte D]", Assert.Single(editor.SegmentExpressions).Header);
            // ein anders erfasster Ausdruck („Spaltringe“) ist derselbe: kein neues Dossier
            editor.Selected = editor.Segments[3];
            await WaitFor(() => scanned.Count == 3 && !editor.IsResearching && !editor.IsScanning);
            Assert.Equal(["Spaltring"], researched);
            Assert.Equal("Spaltring  →  wear ring   [Güte D]", Assert.Single(editor.SegmentExpressions).Header);

            // eine Meldung der KI zu Segment 1 (aus dem Bestätigen) und ein erfasster Fehler
            editor.SidePanelTab = 3;
            editor.Selected = editor.Segments[0];
            editor.Segments[0].Model.Target = "The wear ring sits on the impeller.";
            await Run(editor.ConfirmAndNextCommand);
            await editor.FeedbackDone;
            editor.Selected = editor.Segments[0];
            editor.Segments[0].Model.Errors = [new Core.Qa.LqaError("Terminologie", Core.Qa.LqaSeverity.Major, "„wear ring“ passt hier nicht", "Prüferin", DateTime.UtcNow)];
            editor.Segments[0].Refresh();
            Dispatcher.UIThread.RunJobs();

            // schließen und wieder öffnen
            editor.Close();
            await Run(vm.OpenInEditorCommand, file);
            editor = vm.Editor;
            editor.SidePanelTab = 8;
            foreach (var i in new[] { 0, 2, 3 })
            {
                editor.Selected = editor.Segments[i];
                await WaitFor(() => !editor.IsResearching && !editor.IsScanning);
                Assert.Contains("wear ring   [Güte D]", Assert.Single(editor.SegmentExpressions).Header);
            }
            editor.Selected = editor.Segments[1];
            await WaitFor(() => !editor.IsScanning);
            Assert.Equal(3, scanned.Count);                                               // nichts noch einmal gelesen
            Assert.Equal(["Spaltring"], researched);                                       // nichts noch einmal recherchiert

            // Reiter „Meldungen“: alles zum gewählten Segment, gespeichert über das Schließen hinaus
            editor.SidePanelTab = 3;
            editor.Selected = editor.Segments[0];
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Meldungen (", editor.MessagesTabHeader);
            Assert.Equal("Segment 1 · Fehler · Lektorat", editor.Messages[0].Header);   // Fehler zuerst
            Assert.Contains(editor.Messages, m => m.Kind == "Recherche" && m.Text.StartsWith("„Spaltring“ → „wear ring“ – Güte D"));
            Assert.Contains(editor.Messages, m => m.Kind == "Rückmeldung" && m.Text == "Rückmeldung Segment 1: „Spaltring“ – übernommen (wörtlich)");
            // die Zeichen am Segment zeigen ihre Meldungen
            Assert.True(editor.Segments[0].HasNotes);
            Assert.Equal("Rückmeldung Segment 1: „Spaltring“ – übernommen (wörtlich)", editor.Segments[0].NotesInfo);
            Assert.Equal("Terminologie · schwer – „wear ring“ passt hier nicht", editor.Segments[0].ErrorInfo);
            // ein Segment ohne Meldungen
            editor.Selected = editor.Segments[1];
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(editor.Messages);
            Assert.Equal(("Meldungen", "Keine Meldungen zu diesem Segment."), (editor.MessagesTabHeader, editor.MessagesEmptyText));

            // alle Meldungen, nach Segment geordnet; ein Klick springt hin
            editor.MessagesOnlySelected = false;
            Assert.Equal([1, 3, 4], editor.Messages.Select(m => m.SegmentId).Distinct());
            editor.GoToMessageCommand.Execute(editor.Messages.First(m => m.SegmentId == 4));
            Assert.Equal(4, editor.Selected!.Id);
            Assert.Equal(3, editor.Messages.Select(m => m.SegmentId).Distinct().Count());   // die Liste bleibt beim Springen stehen
        }
        finally
        {
            (EditorViewModel.FindOverride, EditorViewModel.ResearchOverride) = before;
        }
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
