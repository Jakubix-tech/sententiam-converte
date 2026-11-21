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
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Wissen → Einarbeiten → Thema → TM: EU-Rechtsakte suchen, auswählen, TM aus beiden Sprachfassungen aufbauen.</summary>
public class TopicTmUiTests
{
    private const string German = """
        <html><body>
        <p class="oj-ti-art">Artikel 1</p>
        <p class="oj-sti-art">Gegenstand</p>
        <p class="oj-normal">Mit dieser Verordnung werden Anforderungen an Maschinen festgelegt. Sie gilt ab dem 20. Januar 2027.</p>
        <p class="oj-note">ABl. L 165 vom 29.6.2023, S. 1.</p>
        </body></html>
        """;

    private const string English = """
        <html><body>
        <p class="oj-ti-art">Article 1</p>
        <p class="oj-sti-art">Subject matter</p>
        <p class="oj-normal">This Regulation lays down requirements for machinery. It shall apply from 20 January 2027.</p>
        <p class="oj-note">OJ L 165, 29.6.2023, p. 1.</p>
        </body></html>
        """;

    [AvaloniaFact]
    public async Task BuildsATmFromEuLawOnATopic()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var language = ctx.Request.Headers["Accept-Language"];
                byte[] body;
                ctx.Response.ContentType = "application/xhtml+xml; charset=UTF-8";
                switch (ctx.Request.Url!.AbsolutePath)
                {
                    case "/sparql":
                        static JsonObject Row(string celex, string title, string target) => new()
                        {
                            ["celex"] = new JsonObject { ["value"] = celex },
                            ["title"] = new JsonObject { ["value"] = title },
                            ["ttitle"] = new JsonObject { ["value"] = target },
                        };
                        body = Encoding.UTF8.GetBytes(new JsonObject
                        {
                            ["results"] = new JsonObject
                            {
                                ["bindings"] = new JsonArray(
                                    Row("32023R1230", "Verordnung (EU) 2023/1230 über Maschinen", "Regulation (EU) 2023/1230 on machinery"),
                                    Row("32023D1586", "Durchführungsbeschluss (EU) 2023/1586 über die harmonisierten Normen für Maschinen", "Commission Implementing Decision (EU) 2023/1586 on harmonised standards for machinery")),
                            },
                        }.ToJsonString());
                        ctx.Response.ContentType = "application/sparql-results+json";
                        break;
                    case "/resource/celex/32023R1230": body = Encoding.UTF8.GetBytes(language == "deu" ? German : English); break;
                    case "/robots.txt": body = "User-agent: *\nDisallow:\n"u8.ToArray(); break;
                    default: ctx.Response.StatusCode = 404; body = []; break;
                }
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        var old = (LawSources.CellarSparql, LawSources.CellarResource);
        LawSources.CellarSparql = $"http://localhost:{port}/sparql";
        LawSources.CellarResource = $"http://localhost:{port}/resource";
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            vm.SelectedTab = MainViewModel.KnowledgeTab;
            var k = vm.Knowledge;
            k.OnProjectChanged();
            k.TopicTmTopic = "Maschinen";
            await k.FindTopicActsCommand.ExecuteAsync(null);

            Assert.Equal(["32023R1230", "32023D1586"], k.TopicActs.Select(a => a.Act.Celex));
            Assert.All(k.TopicActs, a => Assert.True(a.IsChecked));                            // bis zu drei vorausgewählt
            Assert.Equal(("CELEX 32023R1230 · Verordnung", "Regulation (EU) 2023/1230 on machinery"), (k.TopicActs[0].Info, k.TopicActs[0].TargetTitle));
            Assert.Equal("TM Maschinen (EU-Recht) de-DE–en-GB", k.TopicTmName);
            Assert.Equal("2 Rechtsakte gefunden – die passenden anhaken und „TM aufbauen“.", k.TopicTmLog);

            await k.BuildTopicTmCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();                                                   // Fortschrittsmeldungen kommen über den UI-Thread
            Assert.Contains("32023R1230: 3 Satzpaare, 3 neu in der TM.", k.TopicTmLog);
            Assert.Contains("32023D1586: übersprungen – keine Textfassung in dieser Sprache", k.TopicTmLog);
            Assert.Contains("Fertig: 3 neue Satzpaare aus 1 Rechtsakten in „TM Maschinen (EU-Recht) de-DE–en-GB“ (Quelle: EUR-Lex, © Europäische Union).", k.TopicTmLog);

            // an das Sprachpaar gebunden, mit CELEX als Herkunft
            var bound = Assert.Single(LanguageResources.Load(), r => r.Kind == ResourceKind.Tm);
            Assert.Equal(["de-DE", "en-GB"], bound.Languages);
            using (var tm = new TranslationMemory(bound.Location))
            {
                var hit = tm.Search("de-DE", "en-GB", "Sie gilt ab dem 20. Januar 2027.").First();
                Assert.Equal(("It shall apply from 20 January 2027.", "EUR-Lex CELEX 32023R1230", 100), (hit.Unit.Target, hit.Unit.Origin, hit.Score));
            }

            // im Reiter „Einarbeiten“ sichtbar
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => Equals(i.Header, "Einarbeiten")));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(i => Equals(i.Header, "Einarbeiten"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Verordnung (EU) 2023/1230 über Maschinen");
            title.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.True(title.IsEffectivelyVisible);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "51-thema-tm.png"));
        }
        finally
        {
            (LawSources.CellarSparql, LawSources.CellarResource) = old;
        }
    }
}
