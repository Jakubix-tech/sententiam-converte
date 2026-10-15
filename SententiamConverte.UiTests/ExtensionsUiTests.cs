using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Reiter „Erweiterungen“: Beispiel-Erweiterung installieren, bewusst einschalten und ihre Befehle im Editor nutzen.</summary>
public class ExtensionsUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task InstallEnableAndRunExtensionCommands()
    {
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;

        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Erweiterungstest";
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;
        editor.Selected = editor.Segments[0];
        editor.Selected.Target = "The \"operating\" instructions";

        // Installieren: ausgeschaltet, keine Befehle
        var ext = vm.Extensions;
        platform.FilesToPick.Clear();
        platform.FilesToPick.Add(("Beispiel.Erweiterung.scext", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Beispiel.Erweiterung.scext"))));
        vm.SelectedTab = 6;
        Dispatcher.UIThread.RunJobs();
        await Run(ext.InstallCommand);
        var row = Assert.Single(ext.Items, i => i.Id == "beispiel.erweiterung");
        Assert.False(row.Enabled);
        Assert.DoesNotContain(ext.Commands, c => c.Command.Id.StartsWith("beispiel."));
        Assert.Contains("ausgeschaltet", ext.Message);

        // Einschalten nur nach Bestätigung
        ext.Selected = row;
        await Run(ext.RequestEnableCommand);
        Assert.True(ext.ConfirmEnable);
        Assert.False(row.Enabled);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "18-erweiterung-einschalten.png"));
        await Run(ext.EnableCommand);
        Assert.False(ext.ConfirmEnable);
        Assert.Equal("Eingeschaltet", ext.Selected!.State);
        Assert.Equal(4, ext.Commands.Count(c => c.Command.Id.StartsWith("beispiel.")));
        Assert.Contains("anleitung.docx (en-GB)", ext.ContextInfo);

        // Befehl für das offene Dokument – der Editor zeigt das Ergebnis, das Getippte wird mitgenommen
        ext.SelectedCommand = ext.Commands.First(c => c.Command.Id == "beispiel.anfuehrungszeichen");
        await Run(ext.RunCommandCommand);
        Assert.Contains("1 Segment(e) angepasst.", ext.Output);
        for (var i = 0; i < 300 && editor.Segments.FirstOrDefault()?.Target != "The “operating” instructions"; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.Equal("The “operating” instructions", editor.Segments[0].Target);

        // Befehl für das gewählte Segment, mit Argumenten
        editor.Selected = editor.Segments[0];
        ext.SelectedCommand = ext.Commands.First(c => c.Command.Id == "beispiel.statistik");
        await Run(ext.RunCommandCommand);
        Assert.Contains($"Segment {editor.Segments[0].Model.Id}:", ext.Output);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "19-erweiterungen.png"));

        await Run(ext.RemoveCommand);
        Assert.DoesNotContain(ext.Items, i => i.Id == "beispiel.erweiterung");
        editor.Close();
    }
}
