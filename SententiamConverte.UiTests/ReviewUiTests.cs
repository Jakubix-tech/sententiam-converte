using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Model;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.UiTests;

/// <summary>Lektorat außerhalb des Programms: Prüfdatei (Word) hin und zurück.</summary>
public class ReviewUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task<(MainViewModel Vm, MainWindow Window, FakePlatform Platform)> TranslatedProject(string name)
    {
        var platform = new FakePlatform();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = name + " " + Guid.NewGuid().ToString("N")[..4];
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        await Run(vm.AddFilesCommand);
        platform.FilesToPick.Clear();
        var row = vm.Files[0];
        var doc = vm.Project!.LoadDocument(row.File, row.TargetLanguage);
        string[] targets = ["Operating instructions", "Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", "Then switch on the device.", "Then switch on the device."];
        foreach (var (s, t) in doc.Segments.Where(s => !s.Locked).Zip(targets))
        {
            s.Target = t;
            s.Status = SegmentStatus.Translated;
        }
        vm.Project.SaveDocument(row.File, doc);
        vm.RefreshFiles();
        vm.SelectedFile = vm.Files[0];
        return (vm, window, platform);
    }

    [AvaloniaFact]
    public async Task AReviewFileGoesToWordAndComesBack()
    {
        var (vm, _, platform) = await TranslatedProject("Lektorat");
        await Run(vm.ExportReviewCommand, vm.Files[0]);
        var saved = Assert.Single(platform.Saved);
        Assert.EndsWith("_Pruefung.docx", saved.Key);
        var bytes = saved.Value.ToArray();
        File.WriteAllBytes(Path.Combine(TestSetup.ScreenshotDir, "43-pruefdatei.docx"), bytes);

        // Prüfer korrigiert „50 Nm“ in Word
        var ms = new MemoryStream();
        ms.Write(bytes);
        using (var word = WordprocessingDocument.Open(ms, true))
        {
            var cell = word.MainDocumentPart!.Document.Body!.Elements<W.Table>().First().Elements<W.TableRow>()
                .First(r => r.InnerText.Contains("50 Nm")).Elements<W.TableCell>().ElementAt(2);
            foreach (var t in cell.Descendants<W.Text>()) t.Text = t.Text.Replace("50 Nm", "5 Nm");
        }
        platform.FilesToPick.Add(("anleitung_en-GB_Pruefung.docx", ms.ToArray()));
        vm.ReviewMarksUnchanged = true;
        await Run(vm.ImportReviewCommand);
        Assert.Contains("1 Segmente geändert", vm.Status);

        var doc = vm.Project!.LoadDocument(vm.Files[0].File, vm.Files[0].TargetLanguage);
        var fixedSeg = doc.Segments.First(s => s.Target.Contains("5 Nm"));
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", fixedSeg.Target);
        Assert.Equal(SegmentStatus.Reviewed, fixedSeg.Status);
        Assert.All(doc.Segments.Where(s => !s.Locked), s => Assert.Equal(SegmentStatus.Reviewed, s.Status));   // Rest unverändert → lektoriert
    }

    [AvaloniaFact]
    public async Task PseudoTranslationAndNumberFormatsWorkFromTheProjectTab()
    {
        var (vm, _, platform) = await TranslatedProject("Werkzeuge");
        await Run(vm.PseudoTranslateCommand);
        var saved = Assert.Single(platform.Saved);
        Assert.Contains("_pseudo", saved.Key);
        using (var word = WordprocessingDocument.Open(new MemoryStream(saved.Value.ToArray()), false))
            Assert.Contains("[Ɓéţŕîéƀšàñĺéîţüñĝ", word.MainDocumentPart!.Document.Body!.InnerText);                // „Betriebsanleitung“
        var row = vm.Files[0];
        Assert.Contains(vm.Project!.LoadDocument(row.File, row.TargetLanguage).Segments, s => s.Target == "Operating instructions");   // Projekt unverändert

        // ein Segment mit deutscher Zahl in der englischen Übersetzung
        var doc = vm.Project.LoadDocument(row.File, row.TargetLanguage);
        var seg = doc.Segments.First(s => s.Target == "Operating instructions");
        seg.Source = "Betriebsanleitung 2,5";
        seg.Target = "Operating instructions 2,5";
        vm.Project.SaveDocument(row.File, doc);
        await Run(vm.LocalizeNumbersCommand);
        Assert.Contains("1 Segmente angepasst", vm.Status);
        Assert.Contains(vm.Project.LoadDocument(row.File, row.TargetLanguage).Segments, s => s.Target == "Operating instructions 2.5");
    }

    [AvaloniaFact]
    public async Task TheReviewerTracksChangesAndScoresErrors()
    {
        var (vm, window, platform) = await TranslatedProject("Bewertung");
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        await Task.Delay(200);
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Editor;
        var seg = editor.Segments.First(s => s.Target.Contains("50 Nm"));
        editor.Selected = seg;
        editor.TrackChangesOn = true;
        seg.Target = "Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.";                  // Prüfer korrigiert
        Assert.True(seg.HasChange);
        Assert.Equal("Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", seg.Baseline);
        Assert.Equal(1, editor.ChangeCount);

        editor.LqaCategoryIndex = 0;                                            // Genauigkeit: Fehlübersetzung
        editor.LqaSeverityIndex = 1;                                            // schwer
        editor.LqaNote = "50 statt 5 Nm";
        await Run(editor.AddLqaErrorCommand);
        Assert.Single(editor.SegmentErrors);
        Assert.Contains("5 Strafpunkte", editor.LqaSummary);
        editor.SidePanelTab = 6;
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "45-lektorat.png"));

        editor.FilterIndex = 4;                                                 // vom Prüfer geändert
        Assert.Equal([seg], editor.Segments);
        await Run(editor.RejectChangeCommand);
        Assert.Equal("Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", seg.Target);
        Assert.Equal(0, editor.ChangeCount);
        editor.FilterIndex = 5;                                                 // mit Fehlerbewertung
        Assert.Single(editor.Segments);

        await Run(editor.ExportLqaReportCommand);
        Assert.Contains(platform.Saved.Keys, k => k.EndsWith("_Bewertung.docx"));
        editor.TrackChangesOn = false;
        editor.Close();
    }
}
