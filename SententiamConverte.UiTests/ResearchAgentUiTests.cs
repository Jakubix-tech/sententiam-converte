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

/// <summary>„Recherche (KI steuert)“ und „Skills &amp; Gedächtnis“ – mit einem Prüf-KI-Server, der Werkzeuge aufruft.</summary>
public class ResearchAgentUiTests
{
    private static async Task Run(IRelayCommand command)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(null);
        else command.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TheAiResearchesAndSkillsAreShown()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var baseUrl = $"http://127.0.0.1:{port}/";
        using var server = new HttpListener();
        server.Prefixes.Add(baseUrl);
        server.Start();
        var chatCalls = 0;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                string json;
                if (path.EndsWith("/chat/completions"))
                {
                    var body = JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync())!;
                    var withTools = body["tools"] != null;
                    json = (withTools, Interlocked.Increment(ref chatCalls)) switch
                    {
                        (true, 1) => """{"choices":[{"message":{"role":"assistant","content":"Erst in Wikipedia suchen.","tool_calls":[{"id":"c1","type":"function","function":{"name":"search","arguments":"{\"query\":\"Abkantpresse\",\"language\":\"de-DE\",\"source\":\"wikipedia\"}"}}]}}]}""",
                        (true, _) => """{"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"c2","type":"function","function":{"name":"finish","arguments":"{\"answer\":\"Auf Polnisch: prasa krawędziowa.\",\"translation\":\"prasa krawędziowa\",\"note\":\"Termin techniczny z obróbki blach.\",\"sources\":[\"https://pl.wikipedia.org/wiki/Prasa_krawędziowa\"]}"}}]}}]}""",
                        _ => """{"choices":[{"message":{"role":"assistant","content":"{\"name\": null}"}}]}""",
                    };
                }
                else if (ctx.Request.Url.Query.Contains("list=search"))
                    json = """{"query":{"search":[{"title":"Gesenkbiegepresse","snippet":"Abkantpresse oder Kantbank"}]}}""";
                else
                    json = """{"query":{"pages":{"1":{"title":"Gesenkbiegepresse","langlinks":[{"lang":"pl","*":"Prasa krawędziowa"}]}}}}""";
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var before = AppServices.Ai;
        var oldHost = WikiApi.Host;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = baseUrl + "v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], ResearchProviderId = provider.Id };
            WikiApi.Host = (site, lang) => $"{baseUrl}{site}/{lang}";

            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            vm.SelectedTab = MainViewModel.AgentsTab;
            Dispatcher.UIThread.RunJobs();
            var agents = vm.Agents;
            Assert.True(agents.SkillList.Count >= 4);                                    // mitgelieferte Skills
            Assert.Contains(agents.SkillList, s => s.Name == "fachbegriff-recherchieren");

            agents.AgentTask = "Wie heißt Abkantpresse auf Polnisch?";
            agents.AgentTargetLanguages = "pl-PL";
            await Run(agents.RunAgentCommand);

            Assert.Equal("Auf Polnisch: prasa krawędziowa.", agents.AgentAnswer);
            Assert.Equal("Agent fertig – Ergebnis im Reiter „Agenten“.", vm.Status);         // auch in anderen Reitern sichtbar
            Assert.Contains(agents.AgentSteps, s => s.Tool == "search" && s.Observation.Contains("pl: Prasa krawędziowa"));
            Assert.Contains(agents.AgentSources, s => s.Url.Contains("Prasa_kraw"));
            Assert.Contains("🔎", agents.Log);
            Assert.Contains("Gedächtnis gespeichert", agents.Log);
            Assert.True(File.Exists(AgentsViewModel.GlobalMemoryPath));                 // ohne Projekt: allgemeines Gedächtnis

            var answer = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == "Auf Polnisch: prasa krawędziowa.");
            Assert.True(answer.IsEffectivelyVisible);
            Assert.Equal("prasa krawędziowa", agents.AgentTranslation);
            Assert.StartsWith("✔ „prasa krawędziowa“ steht in: Suchergebnis", agents.AgentEvidence);      // vom Programm geprüft
            var note = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == "Termin techniczny z obróbki blach.");
            Assert.True(note.IsEffectivelyVisible);                                     // Anmerkung des Übersetzers
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "31-recherche-ki-steuert.png"));

            // Skills-Reiter: in den Datenbanken nachsehen, gespeicherte Recherchen ansehen
            agents.DbQuery = "Abkantpresse";
            await Run(agents.SearchDatabasesCommand);
            Assert.Contains("prasa krawędziowa", agents.DbResult);
            await Run(agents.ShowMemoryCommand);
            Assert.Equal(MainViewModel.KnowledgeTab, vm.SelectedTab);
            Assert.Equal(AgentsViewModel.GlobalMemoryPath, vm.Knowledge.SelectedBase!.Location);
            Assert.Contains(vm.Knowledge.Entries, e => e.Title.Contains("Abkantpresse"));
            vm.SelectedTab = MainViewModel.AgentsTab;
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Recherche (KI steuert)"));
            tabs.SelectedIndex = 1;
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            agents.SelectedSkill = agents.SkillList.First(s => s.Name == "fachbegriff-recherchieren");
            Assert.Contains("Sprachversionen", agents.SkillInstructions);
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "32-skills.png"));
        }
        finally
        {
            AppServices.Ai = before;
            WikiApi.Host = oldHost;
            server.Stop();
        }
    }
}
