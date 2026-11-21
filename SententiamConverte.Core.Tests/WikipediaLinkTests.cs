using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Der Begriffsvergleich schlägt die Sprachverknüpfung der Wikipedia nach (Gleitringdichtung → Mechanical seal) und gibt
/// sie dem Modell als belegten Kandidaten – gegen ein nachgebildetes Wikipedia.
/// </summary>
[Collection("BuiltInAi")]
public sealed class WikipediaLinkTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, string, string> _oldWiki = WikiApi.Host;
    private readonly List<string> _requests = new();

    private sealed class Model : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("Explain what the term means IN THIS CONTEXT"))
                return Task.FromResult("""{"meaning": "Dichtung an rotierenden Wellen", "candidates": [{"term": "sliding ring seal", "type": "formal", "why": "wörtlich"}], "queries": {}}""");
            if (p.Contains("Find up to 4 passages")) return Task.FromResult("""{"items": []}""");
            return Task.FromResult("""{"source": {"term": "Gleitringdichtung"}, "candidates": [{"term": "mechanical seal", "equivalence": "Full", "evidence": [1]}], "recommendation": {"term": "mechanical seal", "strategy": "funktionale Entsprechung", "rendering": "mechanical seal", "reasoning": "[1]"}, "warnings": []}""");
        }
    }

    private sealed class NoSearch : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) => Task.FromResult(new List<SearchResult>());
        public Task<List<SearchResult>> SearchInSitesAsync(string q, string l, IReadOnlyList<string> s, int m, CancellationToken ct = default) => Task.FromResult(new List<SearchResult>());
    }

    public WikipediaLinkTests()
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
        TermComparisonAgent.WikipediaLinks = true;
    }

    public void Dispose()
    {
        TermComparisonAgent.WikipediaLinks = false;
        WikiApi.Host = _oldWiki;
        _listener.Close();
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            var url = ctx.Request.Url!.ToString();
            lock (_requests) _requests.Add(url);
            var json = url.Contains("/wikipedia/de/") && url.Contains("Gleitringdichtung")
                ? """{"query": {"pages": {"1": {"title": "Gleitringdichtung", "extract": "", "langlinks": [{"lang": "en", "*": "Mechanical seal"}, {"lang": "pl", "*": "Uszczelnienie mechaniczne"}]}}}}"""
                : url.Contains("/wikipedia/en/") && url.Contains("Mechanical")
                    ? """{"query": {"pages": {"2": {"title": "Mechanical seal", "extract": "A mechanical seal is a device that helps join systems or mechanisms together by preventing leakage. It is used in pumps.", "langlinks": []}}}}"""
                    : """{"query": {"pages": {"-1": {"title": "x", "missing": ""}}}}""";
            var body = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    [Fact]
    public async Task LanguageLinkBecomesAnEvidencedCandidate()
    {
        var model = new Model();
        var ctx = new AgentContext { Model = model, Search = new NoSearch() };
        var r = await new TermComparisonAgent(ctx).RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB",
            "Schalten Sie die Pumpe aus, bevor Sie die Gleitringdichtung austauschen."), 2, TestContext.Current.CancellationToken);

        Assert.Contains("article \"Mechanical seal\" (interlanguage link)", model.Prompts[0]);
        var evidence = Assert.Single(r.Evidence);
        Assert.Equal((1, "Mechanical seal", "A mechanical seal is a device that helps join systems or mechanisms together by preventing leakage."),
            (evidence.Number, evidence.Term, evidence.Quote));
        Assert.Contains("Mechanical seal", model.Prompts[^1]);                                   // als Kandidat im Vergleich
        Assert.Contains("[1] (en-GB) Mechanical seal", model.Prompts[^1]);
        Assert.Equal("mechanical seal", r.Recommendation.Term);
    }

    [Fact]
    public async Task NoArticleNoLink()
    {
        var model = new Model();
        var r = await new TermComparisonAgent(new AgentContext { Model = model, Search = new NoSearch() })
            .RunAsync(new TermQuery("Das ist nicht mein Bier", "de-DE", "en-GB"), 2, TestContext.Current.CancellationToken);
        Assert.Empty(r.Evidence);
        Assert.DoesNotContain("interlanguage link", model.Prompts[0]);
    }

    [Fact]
    public void FirstSentenceAndGeneralSites()
    {
        Assert.Equal("Eine Gleitringdichtung ist eine Dichtung.", TermComparisonAgent.FirstSentence("Eine Gleitringdichtung ist eine Dichtung. Sie wird verwendet."));
        Assert.Equal("Die Pumpe (z. B. Kreiselpumpe) fördert Wasser.", TermComparisonAgent.FirstSentence("Die Pumpe (z. B. Kreiselpumpe) fördert Wasser.\nMehr."));
        Assert.Null(TermComparisonAgent.FirstSentence(" "));
        Assert.True(SiteCatalog.IsGeneral("de.wikipedia.org"));
        Assert.True(SiteCatalog.IsGeneral("en.wiktionary.org"));
        Assert.False(SiteCatalog.IsGeneral("gesetze-im-internet.de"));
    }
}
