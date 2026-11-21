using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Editor: Übersetzung prüfen (Rückübersetzung, Abgleich mehrerer Modelle), Alternative übernehmen, Dokument prüfen.</summary>
public class TranslationCheckUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int ms = 400)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    [AvaloniaFact]
    public async Task DoubtfulTranslationsAreReportedAndAlternativesOffered()
    {
        // Prüf-KI: findet in der Rückübersetzung „50 Nm“ statt „5 Nm“
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var system = (string?)JsonNode.Parse(body)?["messages"]?[0]?["content"] ?? "";
                var answer = system switch
                {
                    _ when system.Contains("as literally as possible") => "Die Schraube mit 50 Nm anziehen.",
                    _ when system.Contains("compare meanings") => """{"score": 0.3, "issues": ["Zahl geändert: 50 statt 5 Nm"]}""",
                    _ when system.Contains("professional translator") => "Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.",
                    _ => """{"score": 0.4, "issues": ["Drehmoment weicht ab"]}""",
                };
                var bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = answer } }),
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
            AppServices.Ai = new AiSettings
            {
                Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id,
                Search = new SearchConfig { Kind = SearchProviderKind.None },
            };
            var platform = new FakePlatform();
            platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Prüfen " + Guid.NewGuid().ToString("N")[..4];
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            await Pump();

            var editor = vm.Editor;
            var seg = editor.Segments.First(s => s.Source.Contains("Schraube"));
            seg.Target = "Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.";
            editor.Selected = seg;
            await Run(editor.CheckSegmentCommand);
            Assert.Contains("Urteil: wahrscheinlich fehlerhaft", editor.AiResult);
            Assert.Contains("Zahl geändert: 50 statt 5 Nm", editor.AiResult);
            Assert.Contains("Rückübersetzung: Die Schraube mit 50 Nm anziehen.", editor.AiResult);
            Assert.Contains(editor.QaIssues, q => q.SegmentId == seg.Id && q.Check == EditorViewModel.AiCheckName && q.Severity == "Fehler");
            Assert.True(seg.HasIssues);
            var alternative = Assert.Single(editor.CheckAlternatives);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "37-uebersetzung-pruefen.png"));

            // Das Ergebnis gehört zu diesem Segment: beim nächsten keine Vorschläge, beim Zurückwechseln wieder da
            editor.Selected = editor.Segments.First(s => s != seg);
            Assert.Empty(editor.CheckAlternatives);
            Assert.Equal("", editor.AiResult);
            editor.Selected = seg;
            Assert.Equal(alternative, Assert.Single(editor.CheckAlternatives));
            Assert.Contains("Zahl geändert: 50 statt 5 Nm", editor.AiResult);

            await Run(editor.UseAlternativeCommand, alternative);
            Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", seg.Target);

            // ganzes Dokument: jedes übersetzte Segment wird geprüft und (hier) gemeldet
            foreach (var s in editor.Segments.Where(s => s.Target.Length == 0)) s.Target = "Something.";
            await Run(editor.CheckDocumentWithAiCommand);
            await Pump(200);
            var translated = editor.Segments.Count(s => s.Target.Trim().Length > 0 && !s.IsLocked);
            Assert.Equal(translated, editor.QaIssues.Count(q => q.Check == EditorViewModel.AiCheckName));
            Assert.Equal(3, editor.SidePanelTab);                                   // QA-Liste wird gezeigt
            editor.Close();
        }
        finally
        {
            AppServices.Ai = before;
            server.Stop();
        }
    }
}
