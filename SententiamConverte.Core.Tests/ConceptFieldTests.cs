using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Begriffsfeld wie bei einem Fachübersetzer: „Spitzdach“ gehört zu den Dachformen; welche gibt es, woran unterscheidet
/// man sie, was meint der Satz – und welche Entsprechung steht in Quellen der Zielsprache wirklich.
/// </summary>
public sealed class ConceptFieldTests
{
    private sealed class Model : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("superordinate concept"))
                return Task.FromResult("""
                    {"umbrella": "Dachform", "umbrellaTarget": "roof shape",
                     "members": [
                       {"source": "Satteldach", "features": ["zwei geneigte Dachflächen", "Giebel an beiden Seiten"], "target": "gable roof", "alternatives": ["saddle roof"]},
                       {"source": "Walmdach", "features": ["vier geneigte Flächen", "keine Giebel"], "target": "hip roof", "alternatives": []},
                       {"source": "Zeltdach", "features": ["vier gleich geneigte Flächen", "treffen sich in einer Spitze"], "target": "tented roof", "alternatives": ["pyramid roof"]}],
                     "howToTell": ["Wie viele Dachflächen hat das Dach?", "Gibt es Giebel?"]}
                    """);
            if (p.Contains("Which concept of the field does the sentence really mean"))
                return Task.FromResult("""{"meant": "Zeltdach", "forThisContext": "Ein Turm mit vier Flächen, die sich in einer Spitze treffen: Zeltdach – pyramid roof.", "askTheClient": ""}""");
            if (p.Contains("Explain what the term means IN THIS CONTEXT"))
                return Task.FromResult("""{"meaning": "Dach mit Spitze", "candidates": [{"term": "pointed roof", "type": "formal"}], "queries": {}}""");
            if (p.Contains("Find up to 4 passages")) return Task.FromResult("""{"items": []}""");
            return Task.FromResult("""{"source": {"term": "Spitzdach"}, "candidates": [{"term": "pyramid roof", "equivalence": "Full"}], "recommendation": {"term": "pyramid roof", "strategy": "funktionale Entsprechung"}, "warnings": []}""");
        }
    }

    private sealed class Search : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) =>
            Task.FromResult(l.StartsWith("en")
                ? new List<SearchResult> { new("Roof shapes", "https://roofs.example/types", ""), new("Towers", "https://towers.example/a", "") }
                : new List<SearchResult> { new("Dachformen", "https://dach.example/formen", "") });
        public Task<List<SearchResult>> SearchInSitesAsync(string q, string l, IReadOnlyList<string> s, int m, CancellationToken ct = default) => SearchAsync(q, l, m, ct);
    }

    private sealed class Fetcher : IPageFetcher
    {
        private static readonly string Pad = new string('.', 220) + " ";
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) => Task.FromResult<string?>(Pad + (url switch
        {
            "https://roofs.example/types" => "Common roof shapes are the gable roof, the hip roof and the pyramid roof. A pyramid roof has four equal slopes.",
            "https://towers.example/a" => "The tower has a pyramid roof covered with slate.",
            "https://dach.example/formen" => "Satteldach, Walmdach und Zeltdach sind die häufigsten Dachformen.",
            _ => "",
        }));
    }

    [Fact]
    public async Task SpitzdachIsPlacedInTheFieldOfRoofShapesAndTheMeantConceptIsFound()
    {
        var model = new Model();
        var ctx = new AgentContext { Model = model, Search = new Search(), Fetcher = new Fetcher() };
        var field = await new ConceptFieldAgent(ctx).RunAsync("Spitzdach", "Der Turm trägt ein steiles Spitzdach mit vier gleichen Flächen.",
            "de-DE", "en-GB", "Bauwesen", TestContext.Current.CancellationToken);

        Assert.NotNull(field);
        Assert.Equal(("Dachform", "roof shape"), (field.Umbrella, field.UmbrellaTarget));
        var queried = Assert.Single(field.Members, m => m.IsQueried);
        Assert.Equal("Spitzdach", queried.SourceTerm);                                  // nicht in der Liste der KI: ergänzt
        var zelt = field.Members.First(m => m.SourceTerm == "Zeltdach");
        Assert.Equal(("pyramid roof", 2), (zelt.TargetTerm, zelt.TargetEvidence));     // belegt in zwei Quellen schlägt „tented roof“
        Assert.Contains("tented roof", zelt.TargetAlternatives);
        Assert.Equal(1, field.Members.First(m => m.SourceTerm == "Satteldach").TargetEvidence);
        Assert.Equal(zelt, field.MeantMember);
        Assert.StartsWith("Ein Turm mit vier Flächen", field.ForThisContext);
        Assert.Null(field.AskTheClient);
        Assert.Equal([1, 2, 3], field.Sources.Select(s => s.Number));
        Assert.Contains("Zeltdach: vier gleich geneigte Flächen; treffen sich in einer Spitze → pyramid roof (found in 2", model.Prompts[^1]);

        // im Dossier: das Begriffsfeld zuerst, seine Entsprechung geht als Kandidat in den Begriffsvergleich
        var dossier = await new DocumentResearch(ctx).ResearchAsync(
            new ResearchItem { Expression = "Spitzdach", Context = "Der Turm trägt ein steiles Spitzdach mit vier gleichen Flächen.", Category = "term" },
            "de-DE", "en-GB", "Bauwesen", ct: TestContext.Current.CancellationToken);
        Assert.Equal("Dachform", dossier.ConceptField?.Umbrella);
        Assert.Contains(model.Prompts, p => p.Contains("Translator's candidate equivalents: pyramid roof, tented roof"));
        Assert.Equal("pyramid roof", dossier.Recommended);
    }

    [Fact]
    public void IdiomsAndAbbreviationsHaveNoField()
    {
        Assert.False(DocumentResearch.HasConceptField("idiom"));
        Assert.False(DocumentResearch.HasConceptField("abbreviation"));
        Assert.True(DocumentResearch.HasConceptField("term"));
        Assert.True(DocumentResearch.HasConceptField("legal"));
    }
}
