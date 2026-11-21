using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Verbund mehrerer Modelle: gleichzeitig verteilen, bei Mengenbegrenzung und Fehlern ausweichen.</summary>
public class PooledChatModelTests
{
    private sealed class FakeModel(string name, Func<int, Task<string>> behaviour) : IChatModel
    {
        private int _calls;
        private int _active;
        public int Calls => _calls;
        public int MaxActive;
        public string DisplayName => name;

        public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            lock (this) MaxActive = Math.Max(MaxActive, active);
            try { return await behaviour(n); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FakeToolModel(string name) : IToolChatModel
    {
        public int ToolCalls;
        public string DisplayName => name;
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) => Task.FromResult(name);
        public Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
        {
            ToolCalls++;
            return Task.FromResult(new ToolReply(name, []));
        }
    }

    private static AiProviderConfig Pool(params string[] models) => new()
    {
        Name = "Verbund", Kind = AiProviderKind.Pool,
        Members = models.Select(m => new AiProviderConfig { Name = m, Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://x/v1", Model = m }).ToList(),
    };

    [Fact]
    public async Task RequestsAreSpreadOverAllModelsAtTheSameTime()
    {
        var fakes = new[] { "a", "b", "c" }.ToDictionary(n => n, n => new FakeModel(n, async _ => { await Task.Delay(80); return n; }));
        var pool = new PooledChatModel(Pool("a", "b", "c"), c =>
        {
            Assert.True(c.FailFast);                                   // Mitglieder warten nicht auf die Mengenbegrenzung
            return fakes[c.Model];
        });
        Assert.Equal(3, pool.Parallelism);

        // wie die Stapelübersetzung: so viele Arbeitsstränge wie Modelle, jeder mit mehreren Segmenten nacheinander
        await Task.WhenAll(Enumerable.Range(0, pool.Parallelism).Select(async _ =>
        {
            for (var i = 0; i < 2; i++) await pool.AskAsync("s", "u", TestContext.Current.CancellationToken);
        }));
        Assert.Equal(6, fakes.Values.Sum(f => f.Calls));
        Assert.All(fakes.Values, f => Assert.True(f.Calls >= 1));      // alle arbeiten mit
        Assert.All(fakes.Values, f => Assert.Equal(1, f.MaxActive));   // kein Modell bekommt zwei Anfragen auf einmal
        Assert.Equal(6, pool.Usage.Sum(u => u.Done));

        // mehr gleichzeitige Anfragen als Modelle: gleichmäßig reihum
        var burst = new PooledChatModel(Pool("a", "b", "c"), c => new FakeModel(c.Model, async _ => { await Task.Delay(50); return c.Model; }));
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => burst.AskAsync("s", "u", TestContext.Current.CancellationToken)));
        Assert.All(new[] { "a", "b", "c" }, m => Assert.Equal(2, answers.Count(a => a == m)));
    }

    [Fact]
    public async Task ARateLimitedModelPausesAndAnotherTakesOver()
    {
        var limited = new FakeModel("a", _ => throw new RateLimitException("a: Mengenbegrenzung", TimeSpan.FromMinutes(1)));
        var other = new FakeModel("b", _ => Task.FromResult("b"));
        var reports = new List<string>();
        var pool = new PooledChatModel(Pool("a", "b"), c => c.Model == "a" ? limited : other) { Report = reports.Add };
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("b", await pool.AskAsync("s", "u", ct));
        Assert.Equal("b", await pool.AskAsync("s", "u", ct));
        Assert.Equal("b", await pool.AskAsync("s", "u", ct));
        Assert.Equal(1, limited.Calls);                                // pausiert, wird nicht ständig wieder gefragt
        Assert.Contains(reports, r => r.Contains("a: ausgelastet"));
    }

    [Fact]
    public async Task AFailingModelIsSkippedAndAllFailingGivesAClearError()
    {
        var ct = TestContext.Current.CancellationToken;
        var broken = new FakeModel("a", _ => throw new AiException("a: leere Antwort"));
        var good = new FakeModel("b", _ => Task.FromResult("b"));
        var pool = new PooledChatModel(Pool("a", "b"), c => c.Model == "a" ? broken : good);
        Assert.Equal("b", await pool.AskAsync("s", "u", ct));

        var allBroken = new PooledChatModel(Pool("a", "b"), c => new FakeModel(c.Model, _ => throw new AiException(c.Model + ": HTTP 500")));
        var ex = await Assert.ThrowsAsync<AiException>(() => allBroken.AskAsync("s", "u", ct));
        Assert.Contains("HTTP 500", ex.Message);
        Assert.Throws<AiException>(() => new PooledChatModel(new AiProviderConfig { Name = "leer", Kind = AiProviderKind.Pool }));
    }

    [Fact]
    public async Task ToolCallsGoOnlyToModelsWithFunctionCalling()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = new FakeToolModel("b");
        var pool = new PooledChatModel(Pool("a", "b"), c => c.Model == "a" ? new FakeModel("a", _ => Task.FromResult("a")) : tools);
        for (var i = 0; i < 3; i++)
            Assert.Equal("b", (await pool.CompleteWithToolsAsync("s", [AgentMessage.User("u")], [], ct)).Text);
        Assert.Equal(3, tools.ToolCalls);

        var none = new PooledChatModel(Pool("a"), c => new FakeModel("a", _ => Task.FromResult("a")));
        await Assert.ThrowsAsync<NotSupportedException>(() => none.CompleteWithToolsAsync("s", [AgentMessage.User("u")], [], ct));
    }

    [Fact]
    public void PoolsAreSavedWithTheirModelsAndRankedSensibly()
    {
        using var dir = new TempDir();
        var pool = Pool("gpt-oss-120b", "Mistral-Small-3.2-24B-Instruct-2506");
        pool.Members![0].FailFast = true;
        var settings = new AiSettings { Providers = [pool] };
        settings.Save(dir.File("ai.json"));
        var json = File.ReadAllText(dir.File("ai.json"));
        Assert.DoesNotContain("FailFast", json);                                    // nur zur Laufzeit
        var loaded = AiSettings.Load(dir.File("ai.json")).Providers.Single();
        Assert.Equal(AiProviderKind.Pool, loaded.Kind);
        Assert.Equal(["gpt-oss-120b", "Mistral-Small-3.2-24B-Instruct-2506"], loaded.Members!.Select(m => m.Model));
        Assert.False(loaded.IsLocal);
        Assert.IsType<PooledChatModel>(ChatModelFactory.Create(loaded));
        Assert.DoesNotContain("\"Members\"", new AiSettings { Providers = [new AiProviderConfig { Name = "x" }] }.Let(s =>
        {
            s.Save(dir.File("einzeln.json"));
            return File.ReadAllText(dir.File("einzeln.json"));
        }));

        var ovh = new[] { "Qwen3.8-27B", "Qwen3.6-27B", "gpt-oss-120b", "Qwen3.5-9B", "Mistral-Small-3.2-24B-Instruct-2506", "Qwen3.5-397B-A17B",
                          "Qwen2.5-VL-72B-Instruct", "Meta-Llama-3_3-70B-Instruct", "gpt-oss-20b" };
        Assert.Equal(["gpt-oss-120b", "Meta-Llama-3_3-70B-Instruct", "Mistral-Small-3.2-24B-Instruct-2506", "Qwen3.8-27B"],
            AiDiscovery.Rank(ovh, "gpt-oss-120b").Take(4));
        Assert.Equal("Qwen2.5-VL-72B-Instruct", AiDiscovery.Rank(ovh).Last());                  // Bildmodelle zuletzt
    }

    [Fact]
    public async Task AnExhaustedOpenCodeQuotaIsReportedAtOnceAndTheGroupMovesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = OpenCodeChatModel.FreeQuotaResetsAt;
        try
        {
            OpenCodeChatModel.FreeQuotaResetsAt = DateTime.Now.AddHours(3);
            var openCode = new AiProviderConfig { Name = "OpenCode: beam-free", Kind = AiProviderKind.OpenCode, Model = "opencode/beam-free" };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<RateLimitException>(() => ChatModelFactory.Create(openCode).AskAsync("s", "u", ct));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));                           // ohne OpenCode zu starten
            Assert.Contains("für alle kostenlosen OpenCode-Modelle gemeinsam", ex.Message);
            Assert.Contains("Kostenlos – mehrere Modelle gleichzeitig", ex.Message);
            Assert.True(ex.RetryAfter > TimeSpan.FromHours(2));

            // im Verbund pausiert OpenCode bis zum Ende des Kontingents, die anderen übernehmen
            var pool = new PooledChatModel(new AiProviderConfig
            {
                Name = "Verbund", Kind = AiProviderKind.Pool,
                Members = [openCode, new AiProviderConfig { Name = "b", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://x/v1", Model = "b" }],
            }, c => c.Kind == AiProviderKind.OpenCode ? ChatModelFactory.Create(c) : new FakeModel("b", _ => Task.FromResult("b")));
            Assert.Equal("b", await pool.AskAsync("s", "u", ct));
            Assert.Equal("b", await pool.AskAsync("s", "u", ct));
            Assert.Equal(1, pool.Usage.Single(u => u.Name.StartsWith("OpenCode")).Failed);
        }
        finally { OpenCodeChatModel.FreeQuotaResetsAt = before; }
    }

    [Fact]
    public void ProvidersWithAnInvalidKindAreRepairedOnLoad()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("ai.json"), """
            {"Providers":[
              {"Id":"e6f8b734","Name":"Kostenlose KI auf diesem Rechner (Gemma 4 E2B)","Kind":-1,"BaseUrl":"","Model":"gemma-4-e2b"},
              {"Id":"9258ef8b","Name":"OpenCode: beam-free","Kind":-1,"BaseUrl":"","Model":"opencode/beam-free"},
              {"Id":"68ac2cb2","Name":"Lokal","Kind":-1,"BaseUrl":"http://localhost:8080/v1","Model":"local"}],
             "Search":{"Kind":-1}}
            """);
        var s = AiSettings.Load(dir.File("ai.json"));
        Assert.Equal([AiProviderKind.BuiltIn, AiProviderKind.OpenCode, AiProviderKind.OpenAiCompatible], s.Providers.Select(p => p.Kind));
        Assert.Equal(SearchProviderKind.Wikipedia, s.Search.Kind);
    }

    [Fact]
    public async Task MembersReportRateLimitsImmediately()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var hits = 0;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                Interlocked.Increment(ref hits);
                ctx.Response.StatusCode = 429;
                ctx.Response.AddHeader("Retry-After", "7");
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("""{"error":"rate limit"}"""));
                ctx.Response.Close();
            }
        });
        var config = new AiProviderConfig { Name = "OVH", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "m", FailFast = true };
        var ex = await Assert.ThrowsAsync<RateLimitException>(() => ChatModelFactory.Create(config).AskAsync("s", "u", TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Equal(1, hits);                                                      // kein Warten und Wiederholen
    }
}

internal static class TestExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
