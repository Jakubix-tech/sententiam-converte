using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Wissen → Fragen: Antwort nur aus den Einträgen, mit Quellen; zitierte Notizen stehen oben.</summary>
public class KnowledgeQaUiTests
{
    [AvaloniaFact]
    public async Task AsksTheKnowledgeAndShowsTheSources()
    {
        using var home = TestSetup.OwnDataFolder();
        Directory.CreateDirectory(KnowledgeViewModel.LocalFolder);
        using (var kb = new KnowledgeBase(Path.Combine(KnowledgeViewModel.LocalFolder, "Schweißen.sckb"), "Schweißen"))
        {
            kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Schweißen", Title = "MIG/MAG-Schweißgerät", Content = "Ein MIG-Schweißgerät besteht aus Stromquelle, Drahtvorschub, Schlauchpaket und Brenner.", SourceUrl = "https://example.de/mig" });
            kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Schweißen", Title = "Löten", Content = "Beim Löten schmilzt nur das Lot." });
        }

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
                var prompt = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "";
                var answer = prompt.Contains("most important search terms")
                    ? """{"terms": {"de": ["MIG", "Schweißgerät", "Drahtvorschub"]}}"""
                    : """{"answer": "Ein MIG-Schweißgerät besteht aus Stromquelle, Drahtvorschub, Schlauchpaket und Brenner [1].", "found": true, "missing": ""}""";
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
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            vm.SelectedTab = MainViewModel.KnowledgeTab;
            var k = vm.Knowledge;
            k.OnProjectChanged();
            k.Question = "Wie ist ein MIG-Schweißgerät aufgebaut?";
            await k.AskKnowledgeCommand.ExecuteAsync(null);

            Assert.Equal("Ein MIG-Schweißgerät besteht aus Stromquelle, Drahtvorschub, Schlauchpaket und Brenner [1].", k.AnswerText);
            Assert.False(k.HasMissing);
            var first = k.AnswerNotes[0];
            Assert.Equal(("MIG/MAG-Schweißgerät", true, "https://example.de/mig"), (first.Note.Title, first.Cited, first.Source));
            Assert.StartsWith("Antwort aus 1 von", k.AskStatus);

            // im Reiter „Fragen“ sichtbar
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Fragen"));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(i => i.Header as string == "Fragen");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == k.AnswerText);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "38-fragen-an-das-wissen.png"));
        }
        finally
        {
            AppServices.Ai = before;
        }
    }
}
