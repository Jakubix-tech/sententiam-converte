using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Desktop;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Lokale API der Desktop-App: ein Skript arbeitet am Dokument, das gerade im Editor offen ist.</summary>
public class LocalApiUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Wartet auf eine Aufgabe, während die Oberfläche weiterläuft (die API ruft den Editor auf).</summary>
    private static async Task<T> Pump<T>(Task<T> task)
    {
        for (var i = 0; i < 1000 && !task.IsCompleted; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        return await task;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    [AvaloniaFact]
    public async Task ScriptsWorkOnTheOpenDocument()
    {
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        var host = new LocalApiHost();
        AppServices.LocalApi = host;
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;                    // das Fenster setzt beim Anzeigen die echte Dateiauswahl

        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Skriptprojekt";
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        Assert.True(vm.Files.Count == 1, vm.Status);
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;

        // API in den Einstellungen einschalten – ohne Schlüssel wird einer erzeugt
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var settings = vm.Settings;
        Assert.True(settings.LocalApiAvailable);
        settings.LocalApiEnabled = true;
        settings.LocalApiPort = port;
        await Run(settings.ApplyLocalApiCommand);
        Assert.StartsWith("Läuft", settings.LocalApiStatus);
        Assert.True(settings.LocalApiToken.Length >= 16);
        try
        {
            // Im Editor ist etwas getippt, aber noch nicht gespeichert
            editor.Selected = editor.Segments[0];
            editor.Selected.Target = "Operating instructions";

            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v2/") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await Pump(Task.Run(() => http.GetAsync("projects")))).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.LocalApiToken);

            var fileId = vm.Files[0].File.Id;
            var segId = editor.Segments[1].Model.Id;
            var response = await Pump(Task.Run(() => http.PutAsJsonAsync($"projects/Skriptprojekt/files/{fileId}/en-GB/segments/{segId}",
                new { target = "Switch off the device first.", status = "Translated" })));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

            // Der Editor zeigt die Änderung des Skripts – und das Getippte ist nicht verloren
            await Until(() => editor.Segments.Count > 1 && editor.Segments[1].Target == "Switch off the device first.");
            Assert.Equal("Switch off the device first.", editor.Segments[1].Target);
            Assert.Equal("Operating instructions", editor.Segments[0].Target);

            // Dasselbe Projekt wie im Fenster
            var project = JsonNode.Parse(await Pump(Task.Run(() => http.GetStringAsync("projects/Skriptprojekt"))))!;
            Assert.Equal(vm.Files.Count, project["files"]!.AsArray().Count);

            vm.SelectedTab = 5;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Schnittstelle (API)").GetVisualParent<Control>()!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "17-einstellungen-api.png"));
        }
        finally
        {
            settings.LocalApiEnabled = false;
            await Run(settings.ApplyLocalApiCommand);
            AppServices.LocalApi = null;
            editor.Close();
        }
        Assert.Equal("Aus.", settings.LocalApiStatus);
    }
}
