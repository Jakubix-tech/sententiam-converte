using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Intelligente Recherche: die KI bewertet, wo sich Recherche lohnt und wie viel Zeit; sie prüft, ob ein Ergebnis reicht;
/// und die Recherche hört auf, sobald es reicht.
/// </summary>
public sealed class SmartResearchTests
{
    private sealed class Answering(Func<string, string> answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer(messages[^1].Text));
        }
    }

    private static readonly ResearchItem Glrd = new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Hard };
    private static readonly ResearchItem Raindancer = new() { Expression = "Raindancer", Category = "other", Difficulty = ResearchDifficulty.Medium };
    private static readonly ResearchItem Pumpe = new() { Expression = "Kreiselpumpe", Category = "term", Difficulty = ResearchDifficulty.Easy };

    [Fact]
    public async Task TheAssessmentSkipsNamesAndGivesHardTermsMoreTime()
    {
        var model = new Answering(_ => """
            {"difficulty": "hard", "reason": "Abkürzung ohne Erklärung", "contextBefore": 9, "contextAfter": 1,
             "expressions": [{"expression": "GLRD", "action": "research", "minutes": 40, "why": "Fachabkürzung"},
                             {"expression": "raindancer", "action": "keep", "why": "Produktname"}]}
            """);
        var a = await new SegmentTriage(new AgentContext { Model = model, ExplanationLanguage = "Deutsch" }).AssessAsync(
            "Die GLRD von Raindancer und die Kreiselpumpe.", ["Kapitel 3"], ["Hallo.", "Danke."], [Glrd, Raindancer, Pumpe], "de-DE", "en-GB",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("hard", a.Difficulty);
        Assert.Equal((1, 1), (a.ContextBefore, a.ContextAfter));                        // höchstens so viele, wie es gibt
        Assert.Equal((TriageAction.Research, 15.0), (a.For("GLRD")!.Action, a.For("GLRD")!.Minutes));   // auf 15 min begrenzt
        Assert.Equal(TriageAction.Keep, a.For("Raindancer")!.Action);
        Assert.Equal((TriageAction.Research, 1.0), (a.For("Kreiselpumpe")!.Action, a.For("Kreiselpumpe")!.Minutes));   // ohne Angabe: nach der Schwierigkeit
        Assert.Contains("[-1] Kapitel 3", model.Prompts[0]);
        Assert.Contains("[+2] Danke.", model.Prompts[0]);
        Assert.Contains("in Deutsch", model.Prompts[0]);

        // unbrauchbare Antwort: alles recherchieren, nach der Schwierigkeit, mit je einem Nachbarn
        var fallback = await new SegmentTriage(new AgentContext { Model = new Answering(_ => "Translated.") }).AssessAsync(
            "Die GLRD.", [], ["Hallo."], [Glrd], "de-DE", "en-GB", ct: TestContext.Current.CancellationToken);
        Assert.Equal((TriageAction.Research, 6.0), (fallback.For("GLRD")!.Action, fallback.For("GLRD")!.Minutes));
        Assert.Equal((0, 1), (fallback.ContextBefore, fallback.ContextAfter));
    }

    [Fact]
    public async Task TheCheckSaysWhatIsMissingAndNeverPassesWithoutEvidence()
    {
        var evidence = new SourceRef("en-GB", "Pumps", "https://pumps.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." };
        var model = new Answering(_ => """{"sufficient": true, "missing": "", "why": "klar"}""");
        var check = new ResearchCheck(new AgentContext { Model = model });

        // belegt und eindeutig: reicht, ohne die KI zu fragen
        var clear = await check.CheckAsync(new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Sources = [evidence], Conclusive = true },
            null, "de-DE", "en-GB", TestContext.Current.CancellationToken);
        Assert.True(clear.Sufficient);
        Assert.Empty(model.Prompts);

        // belegt, aber nicht eindeutig: die KI entscheidet
        var once = await check.CheckAsync(new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Sources = [evidence], Conclusive = false },
            "Die GLRD ist verschlissen.", "de-DE", "en-GB", TestContext.Current.CancellationToken);
        Assert.True(once.Sufficient);
        Assert.Contains("Die GLRD ist verschlissen.", model.Prompts[^1]);

        // ohne Beleg reicht es nie – auch wenn die KI es meint
        var none = await check.CheckAsync(new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "seal" },
            null, "de-DE", "en-GB", TestContext.Current.CancellationToken);
        Assert.False(none.Sufficient);
        Assert.Equal("ein Beleg in der Zielsprache", none.Missing);
    }

    private sealed class Search : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) =>
            Task.FromResult(q == "mechanical seal pump"
                ? new List<SearchResult> { new("A", "https://a.example/1", ""), new("B", "https://b.example/2", ""), new("C", "https://c.example/3", "") }
                : new List<SearchResult>());
    }

    private sealed class Fetcher : IPageFetcher
    {
        public List<string> Urls { get; } = new();
        public string? LastSkipReason => null;
        public IReadOnlyDictionary<string, string> LastLanguageVersions => new Dictionary<string, string>();

        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default)
        {
            Urls.Add(url);
            return Task.FromResult<string?>(new string('.', 220) + " Every centrifugal pump needs a mechanical seal on the shaft.");
        }
    }

    [Fact]
    public async Task ResearchStopsAsSoonAsItIsEnough()
    {
        var model = new Answering(p =>
            p.Contains("Explain what the term means IN THIS CONTEXT")
                ? """{"meaning": "Dichtung", "candidates": [{"term": "mechanical seal", "type": "functional"}], "queries": {"de-DE": [], "en-GB": ["mechanical seal pump"]}}"""
                : p.Contains("Find up to 4 passages") ? """{"items": []}"""
                : """{"source": {"term": "Gleitringdichtung"}, "candidates": [{"term": "mechanical seal", "equivalence": "Full"}], "recommendation": {"term": "mechanical seal", "strategy": "funktionale Entsprechung"}}""");
        var fetcher = new Fetcher();
        var r = await new TermComparisonAgent(new AgentContext { Model = model, Search = new Search(), Fetcher = fetcher })
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB", Focus: "die übliche Benennung in Pumpenhandbüchern"), 3, TestContext.Current.CancellationToken);

        // zwei unabhängige Quellen reichen – die dritte Seite wird nicht mehr gelesen
        Assert.True(r.Conclusive);
        Assert.Equal(["https://a.example/1", "https://b.example/2"], fetcher.Urls);
        Assert.Contains("Still missing after the earlier research – search specifically for this: die übliche Benennung", model.Prompts[0]);
    }
}
