using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Amtliche Quellen für die Recherche-KI: Rechtsdatenbanken (DE, PL, UK, EU), DWDS, Fachquellen-Profile, Seitensuche
/// und der Rechtsvergleich mit Übersetzerkommentar – gegen einen Prüfserver, der die Schnittstellen nachbildet.
/// </summary>
[Collection("BuiltInAi")]
public sealed class OfficialSourcesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _base;
    private readonly (string Gii, string Sejm, string Uk, string Sparql, string EurLex, string Dwds) _old =
        (LawSources.GiiBase, LawSources.SejmBase, LawSources.UkBase, LawSources.CellarSparql, LawSources.EurLexBase, Dictionaries.DwdsBase);
    private readonly Func<string, string, string> _oldWiki = WikiApi.Host;

    public OfficialSourcesTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
        LawSources.GiiBase = _base + "gii";
        LawSources.SejmBase = _base + "sejm";
        LawSources.UkBase = _base + "uk";
        LawSources.CellarSparql = _base + "sparql";
        LawSources.EurLexBase = _base + "eurlex";
        Dictionaries.DwdsBase = _base + "dwds";
        WikiApi.Host = (site, lang) => $"{_base}{site}/{lang}";
        LawSources.ResetCache();
    }

    public void Dispose()
    {
        (LawSources.GiiBase, LawSources.SejmBase, LawSources.UkBase, LawSources.CellarSparql, LawSources.EurLexBase, Dictionaries.DwdsBase) = _old;
        WikiApi.Host = _oldWiki;
        LawSources.ResetCache();
        _listener.Close();
        _dir.Dispose();
    }

    private static byte[] KpaPdf()
    {
        using var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var page = b.AddPage(PageSize.A4);
        page.AddText("Kodeks postepowania administracyjnego", 12, new PdfPoint(40, 780), font);
        page.AddText("Art. 104. Organ administracji publicznej zalatwia sprawe przez wydanie decyzji, chyba ze indywidual-", 9, new PdfPoint(40, 750), font);
        page.AddText("ne przepisy stanowia inaczej. Decyzje rozstrzygaja sprawe co do istoty.", 9, new PdfPoint(40, 738), font);
        return b.Build();
    }

    private async Task Serve()
    {
        var pdf = KpaPdf();
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            var query = WebUtility.UrlDecode(ctx.Request.Url.Query);
            _requests.Enqueue(path + query);
            var (type, body) = path switch
            {
                "/robots.txt" => ("text/plain", Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /gii/geheim\n")),
                "/gii/gii-toc.xml" => ("application/xml", Encoding.UTF8.GetBytes("""
                    <?xml version="1.0" encoding="UTF-8" ?><!DOCTYPE items SYSTEM "http://www.gesetze-im-internet.de/dtd/1.0/gii-toc.dtd">
                    <items>
                    <item><title>Bürgerliches Gesetzbuch</title><link>http://www.gesetze-im-internet.de/bgb/xml.zip</link></item>
                    <item><title>Verwaltungsverfahrensgesetz</title><link>http://www.gesetze-im-internet.de/vwvfg/xml.zip</link></item>
                    <item><title>Zehntes Buch Sozialgesetzbuch - Sozialverwaltungsverfahren und Sozialdatenschutz -</title><link>http://www.gesetze-im-internet.de/sgb_10/xml.zip</link></item>
                    <item><title>Straßenverkehrs-Ordnung</title><link>http://www.gesetze-im-internet.de/stvo_2013/xml.zip</link></item>
                    </items>
                    """)),
                "/gii/vwvfg/__35.html" => ("text/html", Encoding.UTF8.GetBytes("""
                    <html><body><h1>§ 35 Begriff des Verwaltungsaktes</h1>
                    <div class="jurAbsatz">Verwaltungsakt ist jede Verfügung, Entscheidung oder andere hoheitliche Maßnahme, die eine Behörde zur Regelung eines Einzelfalls auf dem Gebiet des öffentlichen Rechts trifft und die auf unmittelbare Rechtswirkung nach außen gerichtet ist.</div>
                    </body></html>
                    """)),
                "/sejm/eli/acts/search" => ("application/json", Encoding.UTF8.GetBytes(query.Contains("inForce=1") ? """
                    {"items":[
                     {"address":"WDU20210001491","displayAddress":"Dz.U. 2021 poz. 1491","publisher":"DU","year":2021,"pos":1491,"status":"obowiązujący","type":"Ustawa","textHTML":true,"textPDF":true,"title":"Ustawa z dnia 11 sierpnia 2021 r. o zmianie ustawy - Kodeks postępowania administracyjnego"},
                     {"address":"WDU20250001691","displayAddress":"Dz.U. 2025 poz. 1691","publisher":"DU","year":"2025","pos":"1691","status":"obowiązujący","type":"Obwieszczenie","textHTML":false,"textPDF":true,"title":"Obwieszczenie Marszałka Sejmu Rzeczypospolitej Polskiej z dnia 7 listopada 2025 r. w sprawie ogłoszenia jednolitego tekstu ustawy - Kodeks postępowania administracyjnego"}
                    ]}
                    """ : """{"items":[]}""")),
                "/sejm/eli/acts/DU/2025/1691/text.pdf" => ("application/pdf", pdf),
                "/uk/all/data.feed" => ("application/atom+xml", Encoding.UTF8.GetBytes(query.Contains("title=") ? """
                    <?xml version="1.0" encoding="utf-8"?><feed xmlns="http://www.w3.org/2005/Atom">
                    <entry><id>http://www.legislation.gov.uk/id/ukpga/2007/15</id><title>Tribunals, Courts and Enforcement Act 2007</title></entry>
                    </feed>
                    """ : """<?xml version="1.0"?><feed xmlns="http://www.w3.org/2005/Atom"/>""")),
                "/sparql" => ("application/sparql-results+json", Encoding.UTF8.GetBytes(query.Contains("32016R0679") ? """
                    {"results":{"bindings":[{"celex":{"value":"32016R0679"},"title":{"value":"Verordnung (EU) 2016/679 (Datenschutz-Grundverordnung)"}}]}}
                    """ : """{"results":{"bindings":[]}}""")),
                "/dwds/api/wb/snippet/" => ("application/json", Encoding.UTF8.GetBytes(query.Contains("Bescheid")
                    ? """[{"url":"https://www.dwds.de/wb/Bescheid","wortart":"Substantiv","input":"Bescheid","lemma":"Bescheid"}]""" : "[]")),
                "/dwds/api/frequency/" => ("application/json", Encoding.UTF8.GetBytes("""{"lemma":"Bescheid","hits":966335,"frequency":3,"q":"Bescheid","total":"53410843153"}""")),
                "/dwds/api/ipa/" => ("application/json", Encoding.UTF8.GetBytes("""[{"ipa":"bəˈʃaɪ̯t","status":"proved"}]""")),
                _ when path.StartsWith("/wikipedia/") || path.StartsWith("/wiktionary/") =>
                    ("application/json", Encoding.UTF8.GetBytes("""{"query":{"search":[],"pages":{"-1":{"missing":""}}}}""")),
                _ => ("text/plain", Encoding.UTF8.GetBytes("")),
            };
            if (body.Length == 0) ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = type;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    [Fact]
    public async Task GermanFederalLawByCitationAndTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        var hits = await LawSources.SearchAsync("de-DE", "§ 35 VwVfG", ["de-DE"], 5, ct);
        Assert.Equal($"{_base}gii/vwvfg/__35.html", hits[0].Url);
        Assert.Equal("§ 35 VwVfG", hits[0].Citation);
        Assert.Contains(hits, h => h.Url.EndsWith("/vwvfg/index.html"));

        Assert.EndsWith("/sgb_10/__31.html", (await LawSources.SearchAsync("de", "§ 31 SGB X", ["de-DE"], 5, ct))[0].Url);      // römische Zahl
        Assert.EndsWith("/vwvfg/__35.html", (await LawSources.SearchAsync("de", "§ 35 VwVfG Verwaltungsakt", ["de-DE"], 5, ct))[0].Url);   // Begriff dahinter
        Assert.EndsWith("/stvo_2013/index.html", (await LawSources.SearchAsync("Deutschland", "StVO", ["de-DE"], 5, ct))[0].Url); // Ordner mit Jahreszahl
        Assert.Equal("Bürgerliches Gesetzbuch", Assert.Single(await LawSources.SearchAsync("de", "bürgerliches gesetzbuch", ["de-DE"], 5, ct)).Title);
        Assert.Single(_requests, r => r.EndsWith("gii-toc.xml"));                                    // Inhaltsverzeichnis nur einmal geladen
    }

    [Fact]
    public async Task PolishLawPrefersTheConsolidatedTextAndReadsThePdf()
    {
        var ct = TestContext.Current.CancellationToken;
        var hits = await LawSources.SearchAsync("pl-PL", "Kodeks postępowania administracyjnego", ["pl-PL"], 5, ct);
        Assert.Equal(2, hits.Count);
        Assert.Contains("tekst jednolity", hits[0].Note);                                           // jednolity tekst vor Änderungsgesetz
        Assert.Equal($"{_base}sejm/eli/acts/DU/2025/1691/text.pdf", hits[0].Url);
        Assert.EndsWith("/2021/1491/text.html", hits[1].Url);
        Assert.Contains(_requests, r => r.Contains("title=Kodeks postępowania administracyjnego"));

        var text = await new PageFetcher().FetchTextAsync(hits[0].Url, 100_000, ct);
        Assert.NotNull(text);
        Assert.Contains("indywidualne przepisy", text);                                             // Silbentrennung aufgehoben
        var passage = Assert.Single(TextPassages.Find(text, "wydanie decyzji"));
        Assert.Contains("Art. 104", passage);
    }

    [Fact]
    public async Task BritishAndEuropeanLaw()
    {
        var ct = TestContext.Current.CancellationToken;
        var uk = Assert.Single(await LawSources.SearchAsync("en-GB", "Tribunals", ["en-GB"], 5, ct));
        Assert.Equal($"{_base}uk/ukpga/2007/15", uk.Url);

        var eu = Assert.Single(await LawSources.SearchAsync("eu", "Verordnung (EU) 2016/679", ["de-DE", "pl-PL"], 5, ct));
        Assert.Equal("CELEX 32016R0679", eu.Citation);
        Assert.Equal($"{_base}eurlex/legal-content/DE/TXT/HTML/?uri=CELEX:32016R0679", eu.Url);
        Assert.Contains("PL: " + $"{_base}eurlex/legal-content/PL/TXT/HTML/?uri=CELEX:32016R0679", eu.Note);
        Assert.Contains(_requests, r => r.Contains("\"32016R0679\"^^xsd:string") && !r.Contains("32016L0679"));   // Verordnung = R

        // EUR-Lex-Seiten werden über die Dokumentenschnittstelle (CELLAR) gelesen, in der Sprache der Adresse
        var cellar = LawSources.CellarFor(new Uri("https://eur-lex.europa.eu/legal-content/PL/TXT/HTML/?uri=CELEX:32016R0679"));
        Assert.Equal(("https://publications.europa.eu/resource/celex/32016R0679", "pol"), (cellar?.Uri.ToString(), cellar?.Language));
        Assert.Null(LawSources.CellarFor(new Uri("https://example.org/legal-content/PL/TXT/?uri=CELEX:32016R0679")));
        Assert.Null(LawSources.Normalize("de-AT"));                                                    // Österreich: keine offene Datenbank
    }

    [Fact]
    public async Task DwdsAndRobotsRulesAlsoApplyToInterfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var dwds = await Dictionaries.DwdsAsync("Bescheid", ct);
        Assert.Equal(("Bescheid", "Substantiv"), (dwds.Entries[0].Lemma, dwds.Entries[0].PartOfSpeech));
        Assert.Equal(3, dwds.FrequencyClass);
        Assert.Contains("Frequency class 3 of 6", dwds.Describe("Bescheid"));
        Assert.Contains("[bəˈʃaɪ̯t]", dwds.Describe("Bescheid"));

        Assert.Equal("robots.txt untersagt den Abruf", await PageFetcher.PermitAsync(new Uri(_base + "gii/geheim/x"), ct));

        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /fragmenty/\nCrawl-delay: 10\n\nUser-agent: GPTBot\nDisallow: /", "SententiamConverte");
        Assert.Equal(10, rules.CrawlDelay);
        Assert.False(rules.Allows("/fragmenty/x"));
        Assert.True(rules.Allows("/sjp/slowo"));
    }

    [Fact]
    public void SkillsAreSuggestedOnlyWhenTheyClearlyFit()
    {
        var all = SkillLibrary.BuiltIn;
        Assert.Equal("rechtsbegriff-vergleichen", ResearchAgent.SuggestSkill("Wie übersetze ich den Rechtsbegriff „Verwaltungsakt“? Vergleiche beide Rechtsordnungen.", all)?.Name);
        Assert.Null(ResearchAgent.SuggestSkill("Wie heißt Abkantpresse auf Polnisch?", all));
    }

    [Fact]
    public void PassagesFindInflectedForms()
    {
        var text = "Einleitung. " + new string('x', 2000) + " Der Erlass eines Verwaltungsaktes setzt voraus … " + new string('y', 2000) + " Ende.";
        var p = Assert.Single(TextPassages.Find(text, "Verwaltungsakt"));
        Assert.Contains("Erlass eines Verwaltungsaktes", p);
        Assert.StartsWith("… ", p);
        Assert.Single(TextPassages.Find("Organ wydaje decyzję administracyjną.", "decyzja administracyjna"));   // Wortanfänge genügen
        Assert.Empty(TextPassages.Find("nichts davon", "Bescheid"));
    }

    [Fact]
    public void ProfilesKnowFieldsLanguagesAndUpgradeOldSettings()
    {
        var defaults = SourceProfile.Defaults();
        Assert.Equal("Recht", SourceProfile.Find(defaults, "law")?.Name);
        Assert.Equal("Recht", SourceProfile.Find(defaults, "prawo")?.Name);
        Assert.Equal("Sprache", SourceProfile.Find(defaults, "language")?.Name);
        Assert.Contains("sjp.pwn.pl", SourceProfile.Find(defaults, "Sprache")!.SitesFor("pl-PL"));
        Assert.Contains("eur-lex.europa.eu", SourceProfile.Find(defaults, "Recht")!.SitesFor("pl-PL"));

        // ältere Einstellungen: „Sprache“ und EUR-Lex kommen dazu, eigene Profile bleiben
        var old = defaults.Where(p => p.Name != "Sprache").Append(new SourceProfile { Name = "Eigenes" }).ToList();
        old.First(p => p.Name == "Recht").Languages[0].Sites.Remove("eur-lex.europa.eu");
        SourceProfile.Upgrade(old, 0);
        Assert.Contains(old, p => p.Name == "Sprache");
        Assert.Contains(old, p => p.Name == "Eigenes");
        Assert.Contains("eur-lex.europa.eu", old.First(p => p.Name == "Recht").Languages[0].Sites);
        SourceProfile.Upgrade(old, SourceProfile.DefaultsVersion);
        Assert.Single(old, p => p.Name == "Sprache");
    }

    [Fact]
    public void NewerBuiltInSkillsReplaceOlderOnesButNotOwnEdits()
    {
        var folder = _dir.File("skills");
        var skills = new SkillLibrary(folder);
        Assert.Contains("`law`", skills.Get("rechtsbegriff-vergleichen")!.Instructions);
        Assert.NotNull(skills.Get("amtliche-quellen-finden"));

        skills.Save(new Skill("rechtsbegriff-vergleichen", "alt", "1. alte Fassung", false, 4, Version: 1));
        skills.Save(new Skill("usus-pruefen", "meins", "1. eigene Fassung", false, 2, Version: 1, Edited: true));
        skills = new SkillLibrary(folder);
        var updated = skills.Get("rechtsbegriff-vergleichen")!;
        Assert.Contains("`law`", updated.Instructions);
        Assert.Equal(4, updated.Uses);                                                             // Zähler bleibt
        Assert.Equal("1. eigene Fassung", skills.Get("usus-pruefen")!.Instructions);              // eigene Änderung bleibt
    }

    /// <summary>Modell mit Function Calling, das ein festes Drehbuch abarbeitet und die Werkzeugergebnisse mitschreibt.</summary>
    private sealed class ScriptedToolModel(params ToolCall[][] script) : IToolChatModel
    {
        private int _turn;
        public readonly List<string> ToolResults = new();
        public string? System;
        public string DisplayName => "Drehbuch";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult("""{"name": null}""");

        public Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
        {
            System = system;
            ToolResults.AddRange(messages.Where(m => m.Role == "tool").Skip(ToolResults.Count).Select(m => m.Text ?? ""));
            return Task.FromResult(new ToolReply("", script[_turn++]));
        }
    }

    private static ToolCall Call(string name, string argsJson) => new(Guid.NewGuid().ToString("N")[..8], name, (JsonObject)JsonNode.Parse(argsJson)!);

    [Fact]
    public async Task TheAiComparesLegalSystemsAndWritesATranslatorsNote()
    {
        var ct = TestContext.Current.CancellationToken;
        var model = new ScriptedToolModel(
            [Call("sources", """{"field":"law","language":"pl-PL"}""")],
            [Call("law", """{"jurisdiction":"de","query":"§ 35 VwVfG"}""")],
            [Call("open", $$"""{"url":"{{_base}}gii/vwvfg/__35.html","find":"Verwaltungsakt"}""")],
            [Call("law", """{"jurisdiction":"pl","query":"Kodeks postępowania administracyjnego"}""")],
            [Call("open", $$"""{"url":"{{_base}}sejm/eli/acts/DU/2025/1691/text.pdf","find":"decyzji"}""")],
            [Call("law", """{"jurisdiction":"at","query":"AVG"}""")],
            [Call("dictionary", """{"word":"Bescheid","language":"de-DE"}""")],
            [Call("search", """{"query":"decyzja administracyjna","language":"pl-PL","source":"web","site":"Recht"}""")],
            [Call("add_source", """{"field":"Recht","language":"pl-PL","site":"https://www.nsa.gov.pl/","system":"polnisches Verwaltungsrecht"}""")],
            [Call("finish", """{"answer":"Verwaltungsakt (§ 35 VwVfG) entspricht weitgehend der decyzja administracyjna (Art. 104 KPA).","translation":"decyzja administracyjna","note":"Pojęcie „Verwaltungsakt” (§ 35 VwVfG) jest szersze niż decyzja administracyjna.","sources":["https://www.gesetze-im-internet.de/vwvfg/__35.html"]}""")]);
        var profiles = SourceProfile.Defaults();
        var saved = 0;
        var log = new StringBuilder();
        var ctx = new AgentContext
        {
            Model = model, Search = null, MaxCharsPerPage = 3000, Log = new SyncLog(log), Profiles = profiles, SaveProfiles = () => saved++,
        };

        var result = await new ResearchAgent(ctx, new SkillLibrary(_dir.File("skills")), null)
            .RunAsync("Verwaltungsakt ins Polnische – mit Rechtsvergleich und Anmerkung", "de-DE", ["pl-PL"], 12, false, false, ct);

        Assert.True(result.Finished);
        Assert.Contains("Recommended procedure for this task (skill rechtsbegriff-vergleichen", model.System);   // Skill passt zur Aufgabe
        Assert.Contains("written in the target language pl-PL", model.System);
        Assert.Contains("polnisches Recht", model.ToolResults[0]);                                     // Rechtsordnung und Seiten
        Assert.Contains("law with jurisdiction \"pl\"", model.ToolResults[0]);
        Assert.Contains("__35.html", model.ToolResults[1]);
        Assert.Contains("hoheitliche Maßnahme", model.ToolResults[2]);                                 // Fundstelle in der Norm
        Assert.Contains("tekst jednolity", model.ToolResults[3]);
        Assert.Contains("Art. 104", model.ToolResults[4]);                                             // Stelle aus dem PDF
        Assert.Contains("No legislation database is connected for \"at\"", model.ToolResults[5]);
        Assert.Contains($"open (url \"{_base}gii/vwvfg/__35.html\"", model.ToolResults[1]);            // konkreter nächster Schritt
        Assert.Contains("Frequency class 3", model.ToolResults[6]);
        Assert.Contains("needs a web search service", model.ToolResults[7]);                           // ohne Websuche ehrlich
        Assert.Contains("nsa.gov.pl", profiles.First(p => p.Name == "Recht").SitesFor("pl-PL"));       // neue Fachquelle gelernt
        Assert.Equal(1, saved);
        Assert.Equal("decyzja administracyjna", result.Translation);
        Assert.StartsWith("Pojęcie", result.Note);
        Assert.Contains(result.Sources, s => s.Url.EndsWith("text.pdf"));
        Assert.Contains("⚖", log.ToString());
        Assert.Contains("📌 Neue Fachquelle: nsa.gov.pl", log.ToString());
    }

    private sealed class SyncLog(StringBuilder sb) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sb) sb.AppendLine(value);
        }
    }
}
