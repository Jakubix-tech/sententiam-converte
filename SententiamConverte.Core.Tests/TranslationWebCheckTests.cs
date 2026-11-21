using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Übersetzung im Netz prüfen (gegen eine nachgebildete Wikipedia), Quellen-Datenbank und frühere Fassungen von Dossiers.
/// </summary>
[Collection("BuiltInAi")]
public sealed class TranslationWebCheckTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, string, string> _oldHost = WikiApi.Host;
    private readonly List<string> _searched = new();

    /// <summary>Artikelzahlen der nachgebildeten polnischen Wikipedia je genauer Wortfolge.</summary>
    private static readonly Dictionary<string, int> Hits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uszczelkę pierścienia ślizgowego"] = 0,
        ["uszczelnienie mechaniczne"] = 4,
        ["wyłącz pompę"] = 12,
        ["Doświadczenie zawodowe"] = 50,
        ["Praca"] = 5,
        ["programowanie full-stack"] = 0,
        ["rozwój full-stack"] = 0,
    };

    public TranslationWebCheckTests()
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
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var phrase = (ctx.Request.QueryString["srsearch"] ?? "").Trim('"');
            lock (_searched) _searched.Add(phrase);
            var n = Hits.GetValueOrDefault(phrase, 1);
            var json = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["searchinfo"] = new JsonObject { ["totalhits"] = n },
                    ["search"] = new JsonArray(Enumerable.Range(0, Math.Min(n, 2)).Select(i => (JsonNode)new JsonObject
                        { ["title"] = $"Artykuł {i + 1} {phrase}", ["snippet"] = $"… <span class=\"searchmatch\">{phrase}</span> …" }).ToArray()),
                },
            };
            var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>Erst die Wendungen (mit einer, die nicht im Text steht, und einer mit Zahl), dann das Urteil.</summary>
    private sealed class Reviser : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Prüfmodell";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            return Task.FromResult(p.Contains("Pick up to 4 word sequences")
                ? """
                  {"items": [{"phrase": "uszczelkę pierścienia ślizgowego", "source": "Gleitringdichtung", "alternatives": ["uszczelnienie mechaniczne"]},
                             {"phrase": "Wyłącz pompę", "source": "Schalten Sie die Pumpe aus", "alternatives": []},
                             {"phrase": "steht nicht im Satz", "source": "x", "alternatives": []},
                             {"phrase": "co 500 godzin", "source": "alle 500 Stunden", "alternatives": []}]}
                  """
                : """
                  {"phrases": [{"phrase": "uszczelkę pierścienia ślizgowego", "verdict": "wrong", "recommended": "uszczelnienie mechaniczne", "explanation": "0 gegen 4 Artikel; der Fachbegriff ist uszczelnienie mechaniczne."},
                               {"phrase": "Wyłącz pompę", "verdict": "fine", "recommended": "Wyłącz pompę", "explanation": "12 Artikel."}],
                   "improved": "Wyłącz pompę, zanim wymienisz uszczelnienie mechaniczne.", "summary": "Ein Fachbegriff ist wörtlich übersetzt."}
                  """);
        }
    }

    [Fact]
    public async Task PhrasesAreCountedJudgedAndImproved()
    {
        var model = new Reviser();
        var r = await new TranslationWebCheck(new AgentContext { Model = model, Search = new WikipediaSearch() }).RunAsync(
            "Schalten Sie die Pumpe aus, bevor Sie die ⟨1⟩Gleitringdichtung⟨/1⟩ austauschen.", "Wyłącz pompę, zanim zmienisz ⟨1⟩uszczelkę pierścienia ślizgowego⟨/1⟩ co 500 godzin.",
            "de-DE", "pl-PL", "Technik", TestContext.Current.CancellationToken);

        // nur, was wörtlich in der Übersetzung steht und keine Zahl enthält
        Assert.Equal(["uszczelkę pierścienia ślizgowego", "Wyłącz pompę"], r.Phrases.Select(p => p.Phrase));
        Assert.DoesNotContain(_searched, s => s.Contains("500") || s.Contains("steht nicht"));
        var seal = r.Phrases[0];
        Assert.Equal((PhraseVerdict.Wrong, "uszczelnienie mechaniczne", "Gleitringdichtung"), (seal.Verdict, seal.Recommended, seal.SourcePart));
        Assert.Equal([0L, 4L], seal.Variants.Select(v => v.WikipediaArticles!.Value));
        Assert.Equal("Wikipedia: 4 Artikel", TranslationWebCheck.Describe(seal.Variants[1]));
        Assert.Contains(seal.Variants[1].Examples, e => e.Url.Contains("/wikipedia/pl/wiki/"));         // Belege mit Adresse
        Assert.Equal(PhraseVerdict.Fine, r.Phrases[1].Verdict);
        Assert.Equal("Wyłącz pompę, zanim wymienisz uszczelnienie mechaniczne.", r.Improved);
        Assert.Equal(("Prüfmodell", "Wikipedia"), (r.Model, r.Search));
        // die Zahlen gingen in das Urteil ein, die Tags nicht an die KI
        Assert.Contains("\"uszczelnienie mechaniczne\": Wikipedia: 4 Artikel", model.Prompts[1]);
        Assert.DoesNotContain("⟨1⟩", model.Prompts[0]);
        Assert.False(TranslationWebCheck.Searchable("[NAME_1] dziękuje"));
    }

    private sealed class Conventions : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Kennermodell";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult("""
                {"kind": "Lebenslauf eines Softwareentwicklers für eine Bewerbung",
                 "notes": ["Polnische Lebensläufe enden mit der Einwilligung zur Datenverarbeitung (RODO)."],
                 "items": [{"source": "FULLSTACK-ENTWICKLUNG", "candidates": ["programowanie full-stack", "rozwój full-stack"], "why": "IT-Begriff bleibt englisch"},
                           {"source": "Berufserfahrung", "candidates": ["Praca", "Doświadczenie zawodowe"], "why": "Standardüberschrift"},
                           {"source": "steht nicht im Dokument", "candidates": ["x y"], "why": ""},
                           {"source": "Telefon", "candidates": ["tel. 0171"], "why": ""}]}
                """);
        }
    }

    [Fact]
    public async Task DocumentKindAndCountryWordingsAreCountedAndGoIntoTheBrief()
    {
        var doc = new Model.BilingualDocument { FileName = "cv.txt", SourceLanguage = "de-DE", TargetLanguage = "pol" };
        var seg = new Segmentation.Segmenter("de-DE");
        foreach (var line in new[] { "FULLSTACK-ENTWICKLUNG | B2B SAAS", "Berufserfahrung", "Telefon: 0171 1234567" })
        {
            var p = new Model.Paragraph { Locator = "p" + doc.Paragraphs.Count };
            seg.Segment(p, line);
            doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        var model = new Conventions();
        var analysis = await new DocumentConventions(new AgentContext { Model = model, Search = new WikipediaSearch() })
            .AnalyseAsync(doc, "pol", "Bewerbung", ct: TestContext.Current.CancellationToken);

        Assert.Contains("readers in Poland", model.Prompt);                                         // „pol“ → Polen
        Assert.Equal(("Lebenslauf eines Softwareentwicklers für eine Bewerbung", "PL"), (analysis.DocumentKind, analysis.Country));
        Assert.Equal(["FULLSTACK-ENTWICKLUNG", "Berufserfahrung"], analysis.Conventions.Select(c => c.Source));   // nur Vorhandenes, keine Zahlen
        var heading = analysis.Conventions[1];
        Assert.Equal(("Doświadczenie zawodowe", true), (heading.Target, heading.ByFrequency));         // 50 gegen 5 Artikel
        Assert.Contains("„Doświadczenie zawodowe“: Wikipedia: 50 Artikel", heading.Evidence);
        Assert.NotEmpty(heading.Sources);
        Assert.Equal(("programowanie full-stack", false), (analysis.Conventions[0].Target, analysis.Conventions[0].ByFrequency));   // 0 und 0: Vorschlag der KI

        var brief = new Projects.DocumentBrief { Conventions = [new() { Source = "Berufserfahrung", Target = "Praca", Use = false }] };
        DocumentConventions.Apply(brief, analysis);
        Assert.False(brief.Conventions.Single(c => c.Source == "Berufserfahrung").Use);                // abgewählt bleibt abgewählt
        brief.Conventions[1].Use = true;
        var instructions = brief.Instructions("pol");
        Assert.Contains("Document kind: Lebenslauf eines Softwareentwicklers", instructions);
        Assert.Contains("Conventions of this kind of document in PL: Polnische Lebensläufe enden", instructions);
        Assert.Contains("Established wordings in PL for this kind of document (use them exactly): \"FULLSTACK-ENTWICKLUNG\" → \"programowanie full-stack\"; \"Berufserfahrung\" → \"Doświadczenie zawodowe\"", instructions);

        Assert.Equal(["CZ", "MX", "PL"], new[] { "cs", "es-MX", "pol" }.Select(l => PhraseFrequency.CountryOf(l)));
        Assert.Null(PhraseFrequency.ClearlyMostCommon([new("a", 2, null, null, []), new("b", 0, null, null, [])]));   // unter 3 Belegen
        Assert.Equal("a", PhraseFrequency.ClearlyMostCommon([new("a", 2, 6, 10, []), new("b", 90, 3, 10, [])])!.Variant);  // Netz vor Wikipedia
    }

    [Fact]
    public void SiteCatalogLearnsFromEvidenceAndSuggestions()
    {
        var home = Path.Combine(TestEnvironment.Root, "quellen-" + Guid.NewGuid().ToString("N")[..6]);
        var old = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", home);
        try
        {
            Assert.Equal("gesetze-im-internet.de", SiteCatalog.DomainOf("https://www.Gesetze-im-Internet.de/brao/__1.html"));
            Assert.Null(SiteCatalog.DomainOf("kein Link"));
            var topics = SiteCatalog.Topics("Zivilprozess", SourceProfile.Defaults().First(p => p.Name == "Recht"));
            Assert.Equal(["recht", "zivilprozess"], topics);

            SiteCatalog.RecordEvidence([new SourceRef("de-DE", "BRAO", "https://www.gesetze-im-internet.de/brao/__1.html"),
                                        new SourceRef("de-DE", "BRAO § 2", "https://www.gesetze-im-internet.de/brao/__2.html"),
                                        new SourceRef("pl-PL", "Prawo", "https://isap.sejm.gov.pl/x")], topics);
            SiteCatalog.RecordSuggestions([new SiteHint("www.brak.de", "de-DE", "Berufsrecht der Anwälte"), new SiteHint("isap.sejm.gov.pl", "pl-PL", "Gesetze")], topics);

            var all = SiteCatalog.Load();
            var brao = all.Single(e => e.Domain == "gesetze-im-internet.de");
            Assert.Equal((SiteOrigin.Evidence, 1), (brao.Origin, brao.Uses));                          // einmal je Dossier
            Assert.Equal((SiteOrigin.Suggested, "Berufsrecht der Anwälte"), (all.Single(e => e.Domain == "brak.de").Origin, all.Single(e => e.Domain == "brak.de").Note));
            Assert.Equal((SiteOrigin.Evidence, 1), (all.Single(e => e.Domain == "isap.sejm.gov.pl").Origin, all.Single(e => e.Domain == "isap.sejm.gov.pl").Suggestions));

            // belegt vor vorgeschlagen; anderes Thema: nur oft belegte
            Assert.Equal(["gesetze-im-internet.de", "brak.de"], SiteCatalog.SitesFor("de-AT", topics));
            Assert.Empty(SiteCatalog.SitesFor("de-DE", ["medizin"]));

            // Profil ergänzen, ohne das gespeicherte zu ändern; die Rechtsordnung bleibt
            var recht = SourceProfile.Defaults().First(p => p.Name == "Recht");
            var before = recht.SitesFor("pl-PL").Count;
            var augmented = SiteCatalog.Augment(recht, topics, "de-DE", "pl-PL")!;
            Assert.Contains("brak.de", augmented.SitesFor("de-DE"));
            Assert.Equal(recht.SystemFor("de-DE"), augmented.SystemFor("de-DE"));
            Assert.Equal(before, recht.SitesFor("pl-PL").Count);
            Assert.Same(recht, SiteCatalog.Augment(recht, ["medizin"], "de-DE", "pl-PL"));            // nichts Gelerntes → unverändert

            SiteCatalog.Remove("brak.de");
            Assert.DoesNotContain(SiteCatalog.Load(), e => e.Domain == "brak.de");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENTENTIAM_HOME", old);
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ResearchingAgainKeepsTheEarlierVersion()
    {
        using var dir = new TempDir();
        using var project = TranslationProject.Create(dir.File("p"), "Kauf", "es-ES", ["es-MX"]);
        var first = new TermDossier { Expression = "zumo", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Recommended = "jugo", Created = DateTime.UtcNow.AddHours(-1) };
        DossierStore.Add(project, first);
        DossierStore.Add(project, new TermDossier { Expression = "Zumo", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Recommended = "jugo natural" });

        var current = Assert.Single(DossierStore.Load(project));
        Assert.Equal(("jugo natural", "jugo"), (current.Recommended, current.Previous!.Recommended));
        Assert.Null(current.Previous.Previous);                                                      // nur eine Stufe

        DossierStore.RestorePrevious(project, current);
        var restored = Assert.Single(DossierStore.Load(project));
        Assert.Equal(("jugo", "jugo natural"), (restored.Recommended, restored.Previous!.Recommended));  // hin und zurück
    }
}
