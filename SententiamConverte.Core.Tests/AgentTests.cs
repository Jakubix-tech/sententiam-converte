using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Die Agenten-Abläufe mit einem nachgebildeten Modell, ohne Netz.</summary>
public class AgentTests
{
    private sealed class ScriptedModel(Func<string, string> answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer(messages[^1].Text));
        }
    }

    private sealed class FakeSearch : ISearchProvider
    {
        public string Name => "fake";
        public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default) =>
            Task.FromResult(new List<SearchResult>
            {
                new("Seite " + language, $"https://{language}.example/1", "…"),
                new("Video", "https://www.youtube.com/watch?v=1", "…"),
            });
    }

    private sealed class FakeFetcher : IPageFetcher
    {
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) =>
            Task.FromResult<string?>(url.Contains("de-DE")
                ? new string('x', 50) + " Die Kreiselpumpe fördert Wasser. Die Gleitringdichtung dichtet die Welle ab. " + new string('y', 300)
                : new string('x', 50) + " The centrifugal pump delivers water. The mechanical seal seals the shaft. " + new string('y', 300));
    }

    [Fact]
    public async Task ResearchBuildsMultilingualTermList()
    {
        var model = new ScriptedModel(prompt =>
            prompt.Contains("write 3 web search queries") ? """{"queries": {"de-DE": ["Kreiselpumpe Dichtung"], "en-GB": ["centrifugal pump seal"]}}"""
            : prompt.Contains("List up to 40") && prompt.Contains("(de-DE)") ? """{"terms": [{"term": "Gleitringdichtung", "context": "dichtet die Welle ab", "url": "https://de-DE.example/1"}]}"""
            : prompt.Contains("List up to 40") ? """{"terms": [{"term": "mechanical seal", "context": "seals the shaft", "url": "https://en-GB.example/1"}]}"""
            : """Hier das Ergebnis: {"concepts": [{"terms": {"de": "Gleitringdichtung", "en": "mechanical seal"}, "definition": "Dichtung an rotierenden Wellen", "confidence": 0.9, "evidence": [{"language": "de-DE", "url": "https://de-DE.example/1", "quote": "Die Gleitringdichtung dichtet die Welle ab."}]}], "summary": "Ein sicheres Paar."}""");
        var ctx = new AgentContext { Model = model, Search = new FakeSearch(), Fetcher = new FakeFetcher() };

        var result = await new TermResearchAgent(ctx).RunAsync("Kreiselpumpen", "de-DE", ["en-GB"], ["Gleitringdichtung"], 1, TestContext.Current.CancellationToken);

        var c = Assert.Single(result.Candidates);
        Assert.Equal("Gleitringdichtung", c.Terms["de-DE"]);           // Sprachcodes auf Projektcodes abgebildet
        Assert.Equal("mechanical seal", c.Terms["en-GB"]);
        Assert.Equal(2, result.Sources.Count);                         // YouTube ausgefiltert
        Assert.DoesNotContain(result.Sources, s => s.Url.Contains("youtube"));
        Assert.Contains("Gleitringdichtung", model.Prompts[0]);         // Suchbegriff in die Planung übernommen
    }

    [Fact]
    public async Task EvaluationRanksCandidatesAndChecksUsage()
    {
        var model = new ScriptedModel(prompt =>
            prompt.Contains("might be unidiomatic") ? """{"phrases": ["Drehkraft anziehen"]}"""
            : prompt.Contains("List up to 3 alternative") ? """{"variants": []}"""
            : prompt.Contains("Expression under test") ? """{"verdict": "unattested", "recommended": "Anzugsdrehmoment", "explanation": "Keine Belege."}"""
            : """{"scores": [{"label": "A", "score": 55, "accuracy": 60, "fluency": 50, "terminology": 40, "style": 60, "comment": "Lehnübersetzung", "issues": ["Drehkraft"]}, {"label": "B", "score": 92, "accuracy": 95, "fluency": 90, "terminology": 95, "style": 90, "comment": "gut", "issues": []}], "best": "B", "suggestion": null, "summary": "B ist fachlich korrekt."}""");
        var ctx = new AgentContext { Model = model, Search = new FakeSearch(), Fetcher = new FakeFetcher() };

        var result = await new EvaluationAgent(ctx).RunAsync("Tighten to 5 Nm.", "en-GB", "de-DE",
            [new("A", "Mit 5 Nm Drehkraft anziehen."), new("B", "Mit einem Anzugsdrehmoment von 5 Nm anziehen.")],
            "Maschinenbau", checkUsage: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal("B", result.BestLabel);
        Assert.Equal("B", result.Scores[0].Label);
        Assert.Equal("Mit einem Anzugsdrehmoment von 5 Nm anziehen.", result.Scores[0].Text);
        var usage = Assert.Single(result.UsageChecks);
        Assert.Equal(UsageVerdict.Unattested, usage.Verdict);
        Assert.Contains(model.Prompts, p => p.Contains("Web usage evidence"));   // Belege gingen in die Bewertung ein
    }
}
