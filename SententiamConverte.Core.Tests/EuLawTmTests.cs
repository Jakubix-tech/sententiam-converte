using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Thema → TM: EU-Rechtsakte zum Thema finden, beide Sprachfassungen Absatz für Absatz und Satz für Satz paaren.</summary>
[Collection("BuiltInAi")]
public sealed class EuLawTmTests : IDisposable
{
    private const string German = """
        <html><body>
        <p class="oj-doc-ti">VERORDNUNG (EU) 2023/1230 DES EUROPÄISCHEN PARLAMENTS UND DES RATES</p>
        <p class="oj-ti-art">Artikel 1</p>
        <p class="oj-sti-art">Gegenstand</p>
        <p class="oj-normal">Mit dieser Verordnung werden Anforderungen an Maschinen festgelegt. Sie gilt ab dem 20. Januar 2027.</p>
        <p class="oj-normal">(1)</p>
        <p class="oj-normal">Maschinen müssen so konstruiert werden, dass sie z. B. sicher bewegt werden können. Dies gilt für alle Teile.</p>
        <p class="oj-normal">Die Kosten von 5 Unfällen lassen sich verringern.</p>
        <p class="oj-note">ABl. L 165 vom 29.6.2023, S. 1.</p>
        <p class="oj-normal">Hersteller.</p>
        </body></html>
        """;

    private const string English = """
        <html><body>
        <p class="oj-doc-ti">REGULATION (EU) 2023/1230 OF THE EUROPEAN PARLIAMENT AND OF THE COUNCIL</p>
        <p class="oj-ti-art">Article 1</p>
        <p class="oj-sti-art">Subject matter</p>
        <p class="oj-normal">This Regulation lays down requirements for machinery. It shall apply from 20 January 2027.</p>
        <p class="oj-normal">(1)</p>
        <p class="oj-normal">Machinery shall be designed so that it can, for example, be moved safely, and this applies to all parts.</p>
        <p class="oj-normal">The cost of 6 accidents can be reduced.</p>
        <p class="oj-note">OJ L 165, 29.6.2023, p. 1.</p>
        <p class="oj-normal">Manufacturer.</p>
        </body></html>
        """;

    private readonly HttpListener _listener = new();
    private readonly (string Sparql, string Resource) _old = (LawSources.CellarSparql, LawSources.CellarResource);
    private readonly TempDir _dir = new();
    private string _lastQuery = "";

    public EuLawTmTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
        LawSources.CellarSparql = $"http://localhost:{port}/sparql";
        LawSources.CellarResource = $"http://localhost:{port}/resource";
    }

    public void Dispose()
    {
        (LawSources.CellarSparql, LawSources.CellarResource) = _old;
        _listener.Close();
        _dir.Dispose();
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            var language = ctx.Request.Headers["Accept-Language"] ?? "";
            byte[] body;
            ctx.Response.ContentType = "application/xhtml+xml; charset=UTF-8";
            if (path == "/sparql")
            {
                _lastQuery = ctx.Request.QueryString["query"] ?? "";
                JsonObject Row(string celex, string title, string target) => new()
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
                            Row("32023D1586", "Durchführungsbeschluss (EU) 2023/1586 der Kommission über die harmonisierten Normen für Maschinen", "Commission Implementing Decision (EU) 2023/1586 on harmonised standards for machinery"),
                            Row("32023R1230", "Verordnung (EU) 2023/1230 über Maschinen", "Regulation (EU) 2023/1230 on machinery"),
                            Row("32006L0042", "Richtlinie 2006/42/EG über Maschinen", "Directive 2006/42/EC on machinery")),
                    },
                }.ToJsonString());
                ctx.Response.ContentType = "application/sparql-results+json";
            }
            else if (path == "/resource/celex/32023R1230" && language == "deu") body = Encoding.UTF8.GetBytes(German);
            else if (path == "/resource/celex/32023R1230" && language == "eng") body = Encoding.UTF8.GetBytes(English);
            else if (path == "/resource/celex/32006L0042" && language == "deu") body = Encoding.UTF8.GetBytes(German);
            else if (path == "/resource/celex/32006L0042" && language == "eng") body = Encoding.UTF8.GetBytes(English.Replace("<p class=\"oj-normal\">Manufacturer.</p>", ""));
            else if (path == "/robots.txt") body = "User-agent: *\nDisallow:\n"u8.ToArray();
            else { ctx.Response.StatusCode = 404; body = []; }
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    [Fact]
    public void TopicsBecomeTitleSearches()
    {
        Assert.Equal("\"schweiß*\"", EuLawTm.TitleQuery("Schweißen"));
        Assert.Equal("(\"sicherheit*\" AND \"maschin*\") OR \"pump*\"", EuLawTm.TitleQuery("Sicherheit von Maschinen, Pumpen"));
        Assert.Equal("\"weld*\"", EuLawTm.TitleQuery("welding"));
        Assert.Null(EuLawTm.TitleQuery("EU, Rat"));
    }

    [Fact]
    public void ParallelVersionsArePairedSentenceBySentence()
    {
        var pairs = EuLawTm.Align(German, English, "de-DE", "en-GB")!;
        Assert.Equal(
        [
            ("VERORDNUNG (EU) 2023/1230 DES EUROPÄISCHEN PARLAMENTS UND DES RATES", "REGULATION (EU) 2023/1230 OF THE EUROPEAN PARLIAMENT AND OF THE COUNCIL"),
            ("Gegenstand", "Subject matter"),                                                    // „Artikel 1“ allein bringt nichts
            ("Mit dieser Verordnung werden Anforderungen an Maschinen festgelegt.", "This Regulation lays down requirements for machinery."),
            ("Sie gilt ab dem 20. Januar 2027.", "It shall apply from 20 January 2027."),
            // zwei deutsche Sätze, ein englischer: der ganze Absatz als Einheit („z. B.“ trennt keinen Satz)
            ("Maschinen müssen so konstruiert werden, dass sie z. B. sicher bewegt werden können. Dies gilt für alle Teile.",
             "Machinery shall be designed so that it can, for example, be moved safely, and this applies to all parts."),
            ("Hersteller.", "Manufacturer."),
        ], pairs);
        // „5 Unfällen“ / „6 accidents“: abweichende Zahl → verworfen; Fußnote und „(1)“ fehlen ebenso
        Assert.Null(EuLawTm.Align(German, English.Replace("<p class=\"oj-normal\">Manufacturer.</p>", ""), "de-DE", "en-GB"));   // Gliederung weicht ab
    }

    [Fact]
    public async Task FindsActsAndBuildsATopicTm()
    {
        var ct = TestContext.Current.CancellationToken;
        var acts = await EuLawTm.FindAsync("Maschinen", "de-DE", "en-GB", ct: ct);
        Assert.Contains("bif:contains '\"maschin*\"'", _lastQuery);
        Assert.Contains("language/DEU", _lastQuery);
        Assert.Contains("language/ENG", _lastQuery);
        Assert.Equal(["32023R1230", "32006L0042", "32023D1586"], acts.Select(a => a.Celex));       // Stammakte vor Durchführungsbeschlüssen
        Assert.Equal(('R', 2023, "Regulation (EU) 2023/1230 on machinery"), (acts[0].Type, acts[0].Year, acts[0].TargetTitle));
        Assert.EndsWith("/legal-content/EN/TXT/HTML/?uri=CELEX:32023R1230", acts[0].Url("en-GB"));

        using var tm = new TranslationMemory(_dir.File("maschinen.sctm"));
        var log = new List<string>();
        var result = await EuLawTm.BuildAsync(acts.Take(2).ToList(), "de-DE", "en-GB", tm, new SyncProgress(log), ct);
        Assert.Equal((6, 6), (result.Pairs, result.Added));
        Assert.Equal("unterschiedlich gegliedert", result.Acts[1].Problem);
        Assert.Contains("32023R1230: 6 Satzpaare, 6 neu in der TM.", log);
        var match = tm.Search("de-DE", "en-GB", "Mit dieser Verordnung werden Anforderungen an Maschinen festgelegt.").First();
        Assert.Equal(("This Regulation lays down requirements for machinery.", "EUR-Lex CELEX 32023R1230"), (match.Unit.Target, match.Unit.Origin));

        // ein zweiter Lauf legt nichts doppelt an
        var again = await EuLawTm.BuildAsync(acts.Take(1).ToList(), "de-DE", "en-GB", tm, ct: ct);
        Assert.Equal((6, 0), (again.Pairs, again.Added));
    }

    [Fact]
    public async Task OnlyOfficialLanguagesOfTheUnion()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => EuLawTm.FindAsync("Maschinen", "de-DE", "uk-UA", ct: TestContext.Current.CancellationToken));
        Assert.Contains("uk-UA ist keine Amtssprache der EU", ex.Message);
    }

    /// <summary>Echte Maschinenverordnung von CELLAR (nur auf Anforderung).</summary>
    [Fact(Explicit = true)]
    public async Task RealMachineryRegulation()
    {
        (LawSources.CellarSparql, LawSources.CellarResource) = _old;
        var ct = TestContext.Current.CancellationToken;
        var acts = await EuLawTm.FindAsync("Maschinen", "de-DE", "en-GB", 8, ct);
        using var tm = new TranslationMemory(_dir.File("real.sctm"));
        var result = await EuLawTm.BuildAsync(acts.Where(a => a.Celex == "32023R1230").ToList(), "de-DE", "en-GB", tm, ct: ct);
        var sample = tm.Browse(null, 400, 12);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-eu-tm.txt"),
            string.Join("\n", acts.Select(a => $"{a.Celex} {a.Title[..Math.Min(100, a.Title.Length)]}")) + $"\n\n{result.Pairs} Paare, {result.Added} neu\n\n"
            + string.Join("\n\n", sample.Select(u => $"DE: {u.Source}\nEN: {u.Target}")));
        Assert.True(result.Pairs > 1500);
    }

    private sealed class SyncProgress(List<string> log) : IProgress<string>
    {
        public void Report(string value) => log.Add(value);
    }
}
