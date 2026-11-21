using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Recherche wie ein Mensch: anders weitersuchen, bis ein Kandidat in zwei unabhängigen Quellen der Zielsprache steht,
/// Suchen nicht wiederholen, die Sprachversion einer Seite lesen – und nach der Höchstzahl an Runden ehrlich aufhören.
/// </summary>
public sealed class PersistentResearchTests
{
    private sealed class Model(bool helpful = true) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        private int _next;

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("Explain what the term means IN THIS CONTEXT"))
                return Task.FromResult("""{"meaning": "Dichtung an der Pumpenwelle", "candidates": [{"term": "sliding ring seal", "type": "formal"}], "queries": {"de-DE": ["Gleitringdichtung Pumpe"], "en-GB": ["sliding ring seal"]}}""");
            if (p.Contains("Find up to 4 passages")) return Task.FromResult("""{"items": []}""");
            if (p.Contains("is not conclusive yet"))
                return Task.FromResult(helpful
                    ? """{"queries": {"en-GB": ["seal between pump shaft and housing", "sliding ring seal"]}, "candidates": ["mechanical seal"]}"""
                    : $$"""{"queries": {"en-GB": ["neue Suche {{++_next}}"]}, "candidates": []}""");
            if (p.Contains("version of a"))
                return Task.FromResult("""{"term": "mechanical seal", "quote": "Replace the mechanical seal before restarting the pump."}""");
            return Task.FromResult("""{"source": {"term": "Gleitringdichtung"}, "candidates": [{"term": "mechanical seal", "equivalence": "Full"}], "recommendation": {"term": "mechanical seal", "strategy": "funktionale Entsprechung"}, "warnings": []}""");
        }
    }

    private sealed class Search : ISearchProvider
    {
        public List<string> Queries { get; } = new();
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default)
        {
            Queries.Add(q);
            return Task.FromResult(q switch
            {
                "Gleitringdichtung Pumpe" => [new SearchResult("Dichtungen", "https://hersteller.example/de/dichtung", "")],
                "sliding ring seal" => [new SearchResult("Seals", "https://blog.example/seals", "")],
                "seal between pump shaft and housing" => [new SearchResult("Pumps", "https://pumps.example/a", ""), new SearchResult("Seals", "https://seals.example/b", "")],
                _ => new List<SearchResult>(),
            });
        }
        public Task<List<SearchResult>> SearchInSitesAsync(string q, string l, IReadOnlyList<string> s, int m, CancellationToken ct = default) => SearchAsync(q, l, m, ct);
    }

    private sealed class Fetcher(bool languageVersions) : IPageFetcher
    {
        private static readonly string Pad = new string('.', 220) + " ";
        public string? LastSkipReason => null;
        public IReadOnlyDictionary<string, string> LastLanguageVersions { get; private set; } = new Dictionary<string, string>();
        public List<string> Urls { get; } = new();

        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default)
        {
            Urls.Add(url);
            LastLanguageVersions = languageVersions && url.Contains("/de/")
                ? new Dictionary<string, string> { ["en"] = "https://hersteller.example/en/seal", ["fr"] = "https://hersteller.example/fr/joint" }
                : new Dictionary<string, string>();
            return Task.FromResult<string?>(Pad + (url switch
            {
                "https://hersteller.example/de/dichtung" => "Wechseln Sie die Gleitringdichtung, bevor Sie die Pumpe wieder starten.",
                "https://hersteller.example/en/seal" => "Replace the mechanical seal before restarting the pump.",
                "https://blog.example/seals" => "Some people call it a sliding ring seal. It keeps fluid inside.",
                "https://pumps.example/a" => "A mechanical seal prevents leakage between the rotating shaft and the pump housing.",
                "https://seals.example/b" => "Mechanical seals are used in centrifugal pumps.",
                _ => "Nichts Passendes.",
            }));
        }
    }

    [Fact]
    public async Task SearchesOnUntilTwoIndependentSourcesAgree()
    {
        var model = new Model();
        var search = new Search();
        var r = await new TermComparisonAgent(new AgentContext { Model = model, Search = search, Fetcher = new Fetcher(false) })
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB", "Wechseln Sie die Gleitringdichtung."), 3, TestContext.Current.CancellationToken);

        Assert.Equal((2, true), (r.Rounds, r.Conclusive));
        Assert.Equal(4, r.PagesRead);
        Assert.Equal(search.Queries.Count, search.Queries.Distinct().Count());              // nichts doppelt gesucht
        var next = Assert.Single(model.Prompts, p => p.Contains("is not conclusive yet"));
        Assert.Contains("Searches already tried (do not repeat them): Gleitringdichtung Pumpe | sliding ring seal", next);
        Assert.Contains("sliding ring seal: 1", next);
        // die Fundstellen sucht das Programm selbst, auch wenn das Modell keine Zitate liefert
        var found = r.Evidence.Where(e => e.Term == "mechanical seal").ToList();
        Assert.Equal(["pumps.example", "seals.example"], found.Select(e => new Uri(e.Url).Host));
        Assert.All(found, e => Assert.Equal("Fundstelle auf der Seite", e.Note));
        Assert.Contains("Research: 2 search round(s), 4 pages read", model.Prompts[^1]);
        Assert.Contains("mechanical seal: 2", model.Prompts[^1]);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Suchrunden"));
    }

    [Fact]
    public async Task ARepeatedResearchBuildsOnThePreviousOne()
    {
        // die frühere Recherche: zwei Anfragen gestellt, die Seite der Pumpenfirma gelesen, ein Beleg für „mechanical seal“
        var previous = new TermDossier
        {
            Expression = "Gleitringdichtung", TargetLanguage = "en-GB", Recommended = "mechanical seal", Created = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            Candidates = [new() { Term = "mechanical seal" }, new() { Term = "face seal" }],
            Queries = ["Gleitringdichtung Pumpe", "sliding ring seal"],
            Sources = [new SourceRef("en-GB", "Pumps", "https://pumps.example/a")
                { Number = 1, Term = "mechanical seal", Quote = "A mechanical seal prevents leakage between the rotating shaft and the pump housing.", Retrieved = DateTime.UtcNow }],
            Rounds = 1, Conclusive = false,
        };
        var model = new Model();
        var search = new Search();
        var fetcher = new Fetcher(false);
        var r = await new TermComparisonAgent(new AgentContext { Model = model, Search = search, Fetcher = fetcher })
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB", Prior: PriorResearch.From(previous)), 3, TestContext.Current.CancellationToken);

        // nichts doppelt: keine Anfrage wiederholt, die schon gelesene Seite nicht noch einmal gelesen
        Assert.DoesNotContain("Gleitringdichtung Pumpe", search.Queries);
        Assert.DoesNotContain("sliding ring seal", search.Queries);
        Assert.DoesNotContain("https://pumps.example/a", fetcher.Urls);
        // der alte Beleg zählt weiter: mit einer neuen Quelle ist „mechanical seal“ jetzt eindeutig belegt
        Assert.True(r.Conclusive);
        Assert.Contains(r.Evidence, e => e.Url == "https://pumps.example/a" && e.Note == "aus der früheren Recherche");
        Assert.Contains(r.Evidence, e => e.Url == "https://seals.example/b");
        // die KI erfährt, was schon versucht ist, und die alten Kandidaten sind der Startpunkt
        Assert.Contains("Already searched (do NOT repeat these", model.Prompts[0]);
        Assert.Contains("face seal", model.Prompts[0]);
        // alle Anfragen – alte und neue – gehen ins neue Dossier, damit auch die nächste Recherche dort ansetzt
        Assert.Equal(["Gleitringdichtung Pumpe", "sliding ring seal", "seal between pump shaft and housing"], r.Queries);
    }

    [Fact]
    public async Task StopsHonestlyAfterTheLastRound()
    {
        var r = await new TermComparisonAgent(new AgentContext { Model = new Model(helpful: false), Search = new Search(), Fetcher = new Fetcher(false) }) { MaxRounds = 3 }
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB"), 3, TestContext.Current.CancellationToken);
        Assert.Equal((3, false), (r.Rounds, r.Conclusive));
        Assert.Contains(r.Warnings, w => w.StartsWith("Nach 3 Suchrunden ist kein Kandidat"));

        // im Dossier: Runden und „nicht eindeutig“ – das hebt die Schwierigkeit
        var d = new TermDossier { Rounds = 3, Conclusive = false, Candidates = [new() { Term = "x", Equivalence = Equivalence.Full }] };
        var (level, why) = DocumentResearch.Assess(new ResearchItem { Difficulty = ResearchDifficulty.Easy }, d, 5);
        Assert.Equal(ResearchDifficulty.Medium, level);
        Assert.EndsWith("Nach 3 Suchrunden nicht eindeutig belegt.", why);
    }

    [Fact]
    public async Task StopsAfterTheTimeBudgetWithWhatItFound()
    {
        // Zeitbudget schon verbraucht: die erste Runde läuft noch, danach schließt die Recherche ab und sagt es
        var r = await new TermComparisonAgent(new AgentContext { Model = new Model(helpful: false), Search = new Search(), Fetcher = new Fetcher(false) })
                { MaxRounds = 6, Deadline = DateTime.UtcNow.AddSeconds(-1) }
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB"), 3, TestContext.Current.CancellationToken);
        Assert.Equal((1, false), (r.Rounds, r.Conclusive));
        Assert.Contains(r.Warnings, w => w.StartsWith("Zeitbudget erreicht: nach 1 von höchstens 6 Suchrunden"));
    }

    [Fact]
    public async Task ReadsTheLanguageVersionOfTheSamePage()
    {
        var model = new Model(helpful: false);
        var r = await new TermComparisonAgent(new AgentContext { Model = model, Search = new Search(), Fetcher = new Fetcher(true) }) { MaxRounds = 1 }
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB"), 3, TestContext.Current.CancellationToken);

        var parallel = Assert.Single(r.Evidence, e => e.Note?.StartsWith("Sprachversion derselben Seite") == true);
        Assert.Equal(("mechanical seal", "https://hersteller.example/en/seal", "en-GB"), (parallel.Term, parallel.Url, parallel.Language));
        Assert.Contains("„Wechseln Sie die Gleitringdichtung, bevor Sie die Pumpe wieder starten.“", parallel.Note);
        Assert.Contains("Candidates: sliding ring seal, mechanical seal", model.Prompts[^1]);   // neuer Kandidat im Vergleich
        Assert.Contains(model.Prompts, p => p.Contains("is the Englisch") || p.Contains("version of a Deutsch"));
    }

    [Fact]
    public void PageLanguageVersions()
    {
        var html = """
            <link rel="alternate" hreflang="en-GB" href="/en/seal">
            <link href="https://hersteller.example/fr/joint" hreflang="fr" rel="alternate">
            <link rel="alternate" hreflang="x-default" href="/">
            <link rel="stylesheet" href="/s.css">
            <link rel="alternate" hreflang="pl" href="javascript:alert(1)">
            """;
        var v = PageFetcher.LanguageVersions(html, new Uri("https://hersteller.example/de/dichtung"));
        Assert.Equal(["en-gb", "fr"], v.Keys.Order());
        Assert.Equal("https://hersteller.example/en/seal", v["en-gb"]);
        Assert.Equal("https://hersteller.example/en/seal", PageFetcher.VersionFor(v, "en-US"));        // Landesvariante genügt
        Assert.Equal("https://hersteller.example/en/seal", PageFetcher.VersionFor(v, "en-GB"));
        Assert.Equal("https://hersteller.example/fr/joint", PageFetcher.VersionFor(v, "fr-FR"));
        Assert.Null(PageFetcher.VersionFor(v, "pl-PL"));
    }
}
