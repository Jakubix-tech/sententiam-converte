using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Fuzzy-Treffer gezielt anpassen: Knopf am Treffer, Änderung gegenüber dem TM-Treffer sichtbar.</summary>
public class FuzzyRepairUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AFuzzyMatchIsAdaptedOnlyWhereTheSourceChanged()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        string prompt = "";
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                prompt = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "";
                var bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Vor der Reinigung die Pumpe ausschalten." } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var before = AppServices.Ai;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var platform = new FakePlatform();
            platform.FilesToPick.Add(("pumpe.txt", "Switch off the pump before cleaning.\n"u8.ToArray()));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Pumpe " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "en-GB";
            vm.NewTargets = "de-DE";
            await Run(vm.CreateProjectCommand);
            vm.Project!.ProjectTm.Add("en-GB", "de-DE", "Switch off the pump before maintenance.", "Vor der Wartung die Pumpe ausschalten.");
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;
            await Until(() => editor.Matches.Count > 0);
            var match = Assert.Single(editor.Matches);
            Assert.True(match.CanRepair);                                                          // Fuzzy-Treffer → „Anpassen (KI)“
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Anpassen (KI)");

            await Run(editor.RepairMatchCommand, match.Number);
            Assert.Equal("Vor der Reinigung die Pumpe ausschalten.", editor.Selected!.Target);
            Assert.True(editor.HasRepair);
            Assert.Equal("Vor der Wartung die Pumpe ausschalten.", editor.RepairBefore);
            Assert.Equal("", editor.RepairNote);
            Assert.Contains("Approved translation: Vor der Wartung die Pumpe ausschalten.", prompt);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "33-fuzzy-anpassen.png"));
        }
        finally { AppServices.Ai = before; }
    }
}
