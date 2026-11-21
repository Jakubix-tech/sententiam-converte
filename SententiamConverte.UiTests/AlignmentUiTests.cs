using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.UiTests;

/// <summary>Alignment-Editor: Dokumente ausrichten, von Hand korrigieren, mit Formatierungs-Tags ins TM übernehmen.</summary>
public class AlignmentUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static byte[] EnglishDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Operating instructions"))),
                new W.Paragraph(
                    new W.Run(new W.Text("Tighten the screw to ") { Space = SpaceProcessingModeValues.Preserve }),
                    new W.Run(new W.RunProperties(new W.Bold()), new W.Text("5 Nm")),
                    new W.Run(new W.Text(". Then switch on the device.") { Space = SpaceProcessingModeValues.Preserve })),
                new W.Paragraph(new W.Run(new W.Text("Then switch on the device.")))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    [AvaloniaFact]
    public async Task OldTranslationsAreAlignedCorrectedAndStoredWithFormatting()
    {
        var platform = new FakePlatform();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Alignment " + Guid.NewGuid().ToString("N")[..4];
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);

        vm.SelectedTab = 2;
        var align = vm.Resources.Alignment;
        await Run(align.OpenCommand);
        Assert.True(align.IsOpen);
        Assert.Equal(("de-DE", "en-GB"), (align.SourceLanguage, align.TargetLanguage));

        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        await Run(align.PickSourceCommand);
        platform.FilesToPick.Clear();
        platform.FilesToPick.Add(("manual.docx", EnglishDocx()));
        await Run(align.PickTargetCommand);
        await Run(align.AlignCommand);
        Assert.Equal(4, align.Rows.Count);
        Assert.Contains("⟨1⟩5 Nm⟨/1⟩", align.Rows[1].SourceText);                  // Formatierung als Tag

        await Run(align.MergeWithNextCommand, align.Rows[0]);                      // von Hand verbinden …
        Assert.Equal(3, align.Rows.Count);
        await Run(align.SplitCommand, align.Rows[0]);                              // … und wieder teilen
        Assert.Equal(4, align.Rows.Count);
        align.Rows[1].Confirmed = true;
        Assert.Contains("1 bestätigt", align.Info);

        align.TargetTm = align.Tms.First(t => t.Name == "Projekt-TM");
        await Run(align.ImportCommand);
        var hit = vm.Project!.ProjectTm.Search("de-DE", "en-GB", "Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.").First();
        Assert.Equal(100, hit.Score);
        Assert.Contains("⟨1⟩5 Nm⟨/1⟩", hit.Unit.Target);                           // Formatierung kommt mit

        var view = window.GetVisualDescendants().OfType<AlignmentView>().Single();
        Assert.True(view.IsEffectivelyVisible);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "36-alignment-editor.png"));
        await Run(align.CloseCommand);
        Assert.False(view.IsEffectivelyVisible);
    }
}
