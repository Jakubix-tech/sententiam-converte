using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Die KI steuert die Recherche: Werkzeuge, Datenbanken, Gedächtnis, Skills – mit geskripteten Modellen und einem Wiki-Prüfserver.</summary>
[Collection("BuiltInAi")]
public sealed class ResearchAgentTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Func<string, string, string> _oldHost = WikiApi.Host;

    public ResearchAgentTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var baseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(baseUrl);
        _listener.Start();
        _ = Task.Run(Serve);
        WikiApi.Host = (site, lang) => $"{baseUrl}{site}/{lang}";
    }

    public void Dispose()
    {
        WikiApi.Host = _oldHost;
        _listener.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    /// <summary>Kleine Wikipedia: Suche nach „Abkantpresse“ in de, Artikel „Prasa krawędziowa“ in pl.</summary>
    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var url = WebUtility.UrlDecode(ctx.Request.Url!.PathAndQuery);
            _requests.Enqueue(url);
            var json = url switch
            {
                _ when url.StartsWith("/wikipedia/de/") && url.Contains("list=search") =>
                    """{"query":{"search":[{"title":"Gesenkbiegepresse","snippet":"In einer Gesenkbiegepresse, <span class=\"searchmatch\">Abkantpresse</span> oder Kantbank"}]}}""",
                _ when url.StartsWith("/wikipedia/de/") && url.Contains("prop=langlinks") && !url.Contains("extracts") =>
                    """{"query":{"pages":{"1":{"title":"Gesenkbiegepresse","langlinks":[{"lang":"pl","*":"Prasa krawędziowa"},{"lang":"en","*":"Press brake"},{"lang":"fr","*":"Presse plieuse"}]}}}}""",
                _ when url.StartsWith("/wikipedia/pl/") && url.Contains("extracts") =>
                    """{"query":{"pages":{"7":{"title":"Prasa krawędziowa","extract":"Prasa krawędziowa (krawędziarka) – rodzaj prasy służącej do gięcia arkuszy blachy.","langlinks":[{"lang":"de","*":"Gesenkbiegepresse"}]}}}}""",
                _ => """{"query":{"search":[],"pages":{"-1":{"missing":""}}}}""",
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private sealed class Memory : IAgentMemory
    {
        public readonly List<(string Title, string Content)> Stored = new();
        public string Search(string query, string language) => query.Contains("Biegen") ? "[Wissen] Biegen: zginanie" : "";
        public void Remember(string title, string content, string language, string? sourceUrl) => Stored.Add((title, content));
    }

    /// <summary>Modell mit Function Calling, das ein festes Drehbuch abarbeitet und die Werkzeugergebnisse mitschreibt.</summary>
    private sealed class ScriptedToolModel(params ToolCall[][] script) : IToolChatModel
    {
        private int _turn;
        public readonly List<string> ToolResults = new();
        public string DisplayName => "Drehbuch";
        public string? LearnReply { get; init; }

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult(LearnReply ?? """{"name": null}""");

        public Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
        {
            ToolResults.AddRange(messages.Where(m => m.Role == "tool").Skip(ToolResults.Count).Select(m => m.Text ?? ""));
            Assert.Contains(tools, t => t.Name == "article");
            Assert.Contains("fachbegriff-recherchieren", system);                  // Skills stehen im Systemtext
            return Task.FromResult(new ToolReply("", script[_turn++]));
        }
    }

    private static ToolCall Call(string name, string argsJson) => new(Guid.NewGuid().ToString("N")[..8], name, (JsonObject)JsonNode.Parse(argsJson)!);

    private AgentContext Context(IChatModel model, StringBuilder log) => new()
    {
        Model = model, Search = null, MaxCharsPerPage = 3000, Log = new SyncLog(log), ExplanationLanguage = "Deutsch",
    };

    private sealed class SyncLog(StringBuilder sb) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sb) sb.AppendLine(value);
        }
    }

    [Fact]
    public async Task TheAiSteersTheResearchWithTools()
    {
        var model = new ScriptedToolModel(
            [Call("database", """{"query":"Abkantpresse","language":"de-DE"}""")],
            [Call("search", """{"query":"Abkantpresse","language":"de-DE","source":"wikipedia"}""")],
            [Call("skill", """{"name":"fachbegriff-recherchieren"}"""), Call("article", """{"title":"Prasa krawędziowa","language":"pl-PL"}""")],
            [Call("search", """{"query":"Abkantpresse","language":"de-DE","source":"wikipedia"}""")],      // Wiederholung
            [Call("finish", """{"answer":"Prasa krawędziowa","sources":["https://pl.wikipedia.org/wiki/Prasa_krawędziowa"]}""")])
        {
            LearnReply = """{"name": "Begriff über Sprachversionen", "description": "Fachbegriffe über Wikipedia-Sprachversionen finden.", "instructions": ["Ausgangsartikel suchen", "Sprachversion öffnen"]}""",
        };
        var memory = new Memory();
        var skills = new SkillLibrary(_dir.File("skills"));
        var log = new StringBuilder();

        var result = await new ResearchAgent(Context(model, log), skills, memory)
            .RunAsync("Wie heißt Abkantpresse auf Polnisch?", "de-DE", ["pl-PL"], 10, learn: true, saveResult: true, TestContext.Current.CancellationToken);

        Assert.True(result.Finished);
        Assert.Equal("Prasa krawędziowa", result.Answer);
        Assert.Contains("source language (de-DE)", model.ToolResults[0]);                         // Hinweis bei leerer Datenbank
        Assert.Contains("In other languages: pl: Prasa krawędziowa", model.ToolResults[1]);       // Sprachversionen gleich mitgeliefert
        Assert.DoesNotContain("fr: Presse plieuse", model.ToolResults[1]);                        // nur die gefragten Sprachen
        Assert.Contains("krawędziarka", model.ToolResults[3]);                                    // Artikeltext
        Assert.Contains("You already did exactly this", model.ToolResults[4]);                    // Wiederholung erkannt
        Assert.Contains(result.Sources, s => s.Url.Contains("Prasa_kraw"));
        Assert.Single(memory.Stored);                                                              // Ergebnis gemerkt
        Assert.Equal(1, skills.Get("fachbegriff-recherchieren")!.Uses);
        var learned = Assert.IsType<Skill>(result.LearnedSkill);
        Assert.Equal("begriff-uber-sprachversionen", learned.Name);
        Assert.True(skills.Get(learned.Name)!.Learned);
        Assert.Contains("1. Ausgangsartikel suchen", learned.Instructions);
        Assert.Contains("🧩 Neuer Skill gelernt", log.ToString());
    }

    /// <summary>Modell ohne Function Calling: schreibt ein JSON-Objekt je Schritt (auch mit Fehlern dazwischen).</summary>
    private sealed class ScriptedTextModel(params string[] replies) : IChatModel
    {
        private int _turn;
        public readonly List<string> Seen = new();
        public string DisplayName => "Text";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Seen.Add(messages[^1].Text);
            return Task.FromResult(_turn < replies.Length ? replies[_turn++] : """{"name": null}""");
        }
    }

    [Fact]
    public async Task ModelsWithoutFunctionCallingUseTheJsonProtocol()
    {
        var model = new ScriptedTextModel(
            "Ich schaue zuerst nach.",                                                              // kein JSON → Nachfrage
            "```json\n{\"thought\": \"Artikel lesen\", \"tool\": \"article\", \"title\": \"Prasa krawędziowa\", \"language\": \"pl-PL\"}\n```",
            """{"thought": "fertig", "tool": "finish", "args": {"answer": "Prasa krawędziowa", "sources": "https://pl.wikipedia.org/wiki/Prasa_krawędziowa"}}""");
        var result = await new ResearchAgent(Context(model, new StringBuilder()), new SkillLibrary(_dir.File("skills")), null)
            .RunAsync("Abkantpresse auf Polnisch?", "de-DE", ["pl-PL"], 5, learn: false, saveResult: false, TestContext.Current.CancellationToken);

        Assert.True(result.Finished);
        Assert.Equal("Prasa krawędziowa", result.Answer);
        Assert.Contains("exactly ONE JSON object", model.Seen[1]);
        Assert.Contains("krawędziarka", model.Seen[2]);
        Assert.Contains(result.Sources, s => s.Url == "https://pl.wikipedia.org/wiki/Prasa_krawędziowa");   // aus finish
        Assert.Contains(result.Sources, s => s.Url.Contains("/wikipedia/pl/wiki/Prasa_kraw"));        // gelesener Artikel
    }

    /// <summary>Ein Modell, das sich Zeit lässt.</summary>
    private sealed class SlowModel(TimeSpan delay, string reply) : IChatModel
    {
        public string DisplayName => "Langsam";
        public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return reply;
        }
    }

    [Fact]
    public async Task LongWaitsForTheModelAreShownInTheLog()
    {
        var before = AgentContext.HeartbeatInterval;
        AgentContext.HeartbeatInterval = TimeSpan.FromMilliseconds(60);
        try
        {
            var log = new StringBuilder();
            var model = new SlowModel(TimeSpan.FromMilliseconds(400), """{"thought": "fertig", "tool": "finish", "args": {"answer": "prasa krawędziowa"}}""");
            var result = await new ResearchAgent(Context(model, log), new SkillLibrary(_dir.File("skills")), null)
                .RunAsync("Abkantpresse", "de-DE", ["pl-PL"], 3, learn: false, saveResult: false, TestContext.Current.CancellationToken);
            Assert.Equal("prasa krawędziowa", result.Answer);
            Assert.Contains("⏳ Warte auf Langsam", log.ToString());
        }
        finally { AgentContext.HeartbeatInterval = before; }
    }

    /// <summary>So beobachtet bei NVIDIA: normale Anfragen gehen, Anfragen mit Werkzeugen bleiben ohne Antwort.</summary>
    [Fact]
    public async Task AModelThatNeverAnswersToolRequestsIsUsedWithTheSimpleProtocol()
    {
        var ct = TestContext.Current.CancellationToken;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var toolRequests = 0;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext c;
                try { c = await server.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                    if (body.Contains("\"tools\""))
                    {
                        Interlocked.Increment(ref toolRequests);
                        try { await Task.Delay(TimeSpan.FromSeconds(5)); c.Response.Close(); } catch (Exception) { }
                        return;
                    }
                    var answer = """{"choices":[{"message":{"role":"assistant","content":"{\"thought\": \"fertig\", \"tool\": \"finish\", \"args\": {\"answer\": \"prasa krawędziowa\"}}"}}]}""";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    c.Response.ContentType = "application/json";
                    await c.Response.OutputStream.WriteAsync(bytes);
                    c.Response.Close();
                });
            }
        });
        var before = OpenAiCompatibleChatModel.ToolTimeout;
        OpenAiCompatibleChatModel.ToolTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            var config = new AiProviderConfig { Name = "NVIDIA-artig", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "m" };
            for (var run = 0; run < 2; run++)
            {
                var log = new StringBuilder();
                var result = await new ResearchAgent(Context(ChatModelFactory.Create(config), log), new SkillLibrary(_dir.File("skills")), null)
                    .RunAsync("Abkantpresse", "de-DE", ["pl-PL"], 3, learn: false, saveResult: false, ct);
                Assert.Equal("prasa krawędziowa", result.Answer);
                Assert.Contains("einfaches Protokoll", log.ToString());
            }
            Assert.Equal(1, toolRequests);                   // beim zweiten Mal gleich ohne Werkzeuge
        }
        finally
        {
            OpenAiCompatibleChatModel.ToolTimeout = before;
            server.Stop();
        }
    }

    [Fact]
    public void SkillsAreReadableMarkdownFiles()
    {
        var skills = new SkillLibrary(_dir.File("skills"));
        Assert.Equal(SkillLibrary.BuiltIn.Count, skills.All().Count);
        var path = Path.Combine(skills.Folder, "usus-pruefen", "SKILL.md");
        Assert.StartsWith("---\nname: usus-pruefen\ndescription: ", File.ReadAllText(path).Replace("\r\n", "\n"));

        skills.Save(new Skill("Mein Skill Ä", "Wofür", "1. Tun", true, 0));
        Assert.Equal("mein-skill-a", skills.Get("mein-skill-a")!.Name);
        Assert.Contains("- mein-skill-a: Wofür", skills.Catalog());
        skills.Delete("mein-skill-a");
        Assert.Null(skills.Get("mein-skill-a"));

        // gelöschte mitgelieferte Skills kommen beim nächsten Start wieder
        skills.Delete("usus-pruefen");
        Assert.NotNull(new SkillLibrary(_dir.File("skills")).Get("usus-pruefen"));
    }

    [Fact]
    public void WikipediaReplacesUnusableWebSearch()
    {
        var settings = new AiSettings();
        settings.Search.Kind = SearchProviderKind.ClaudeWebSearch;               // ohne Claude-Zugang
        Assert.IsType<WikipediaSearch>(SearchProviderFactory.Create(settings));
        settings.Search.Kind = SearchProviderKind.Brave;                          // ohne Schlüssel
        Assert.IsType<WikipediaSearch>(SearchProviderFactory.Create(settings));
        settings.Search.Kind = SearchProviderKind.None;                           // bewusst keine Suche
        Assert.Null(SearchProviderFactory.Create(settings));
        settings.Search.Kind = SearchProviderKind.SearXng;
        settings.Search.BaseUrl = "http://localhost:8888";
        Assert.IsType<SearXngSearch>(SearchProviderFactory.Create(settings));
    }

    [Fact]
    public async Task WikipediaSearchAndArticlesComeFromTheOfficialApi()
    {
        var hits = await WikiApi.SearchAsync("wikipedia", "de-DE", "Abkantpresse", 5, TestContext.Current.CancellationToken);
        var hit = Assert.Single(hits);
        Assert.Equal("Gesenkbiegepresse", hit.Title);
        Assert.Contains("Abkantpresse oder Kantbank", hit.Snippet);                // ohne HTML
        var article = await WikiApi.ArticleAsync("wikipedia", "pl-PL", "Prasa krawędziowa", 50, TestContext.Current.CancellationToken);
        Assert.NotNull(article);
        Assert.Equal(50, article.Text.Length);                                    // auf die Länge gekürzt
        Assert.Equal("Gesenkbiegepresse", article.Versions["de"]);
        Assert.Contains(_requests, r => r.Contains("/w/api.php?action=query"));   // offizielle Schnittstelle, nicht /api/
    }
}
