using System.Text.RegularExpressions;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Themenrecherche wie ein Mensch: Gliederung, jedes Unterthema in beiden Sprachen, Lücken in weiteren Runden mit neuen
/// Anfragen schließen, gelesene Seiten nicht doppelt lesen, ergiebige Seiten als Fachseiten merken.
/// </summary>
public sealed class TopicResearchTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Model : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("Break the subject into"))
                return Task.FromResult("""
                    {"aspects": [
                      {"title": "Verfahren", "queries": {"de-DE": ["Schweißverfahren MIG MAG"], "en-GB": ["welding processes MIG MAG"]}},
                      {"title": "Werkstoffe", "queries": {"de-DE": ["Schweißzusatzwerkstoffe"], "en-GB": ["welding consumables"]}}]}
                    """);
            if (p.Contains("Nothing reliable was found yet"))
                return Task.FromResult("""{"queries": [{"aspect": "Werkstoffe", "language": "en-GB", "queries": ["filler metal welding wire"]}]}""");
            // Auswertung: der erste Satz der Seite als Beleg – außer auf der Seite ohne Fachinhalt (Zitat steht dort nicht)
            var text = Regex.Match(p, @"=== TEXT\s*(.*?)\s*=== END", RegexOptions.Singleline).Groups[1].Value.Trim('.', ' ', '\n', '\r');
            var first = text.Split(". ")[0].Trim() + ".";
            var quote = text.Contains("Werbung") ? "Ein Satz, der nicht dasteht." : first;
            return Task.FromResult($$"""{"items": [{"kind": "fact", "title": "{{first[..Math.Min(30, first.Length)]}}", "content": "{{first}}", "quote": "{{quote}}", "url": "u", "confidence": 0.9}]}""");
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
                "Schweißverfahren MIG MAG" => [new SearchResult("Verfahren", "https://schweissen.example/verfahren", "")],
                "welding processes MIG MAG" => [new SearchResult("Processes", "https://welding.example/processes", "")],
                "Schweißzusatzwerkstoffe" => [new SearchResult("Werkstoffe", "https://schweissen.example/werkstoffe", "")],
                "welding consumables" => [new SearchResult("Shop", "https://shop.example/angebot", "")],
                "filler metal welding wire" => [new SearchResult("Filler metals", "https://welding.example/filler", "")],
                _ => new List<SearchResult>(),
            });
        }
    }

    private sealed class Fetcher : IPageFetcher
    {
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) => Task.FromResult<string?>(new string('.', 310) + " " + url switch
        {
            "https://schweissen.example/verfahren" => "Beim MAG-Schweißen schützt ein aktives Gas das Schmelzbad. Mehr dazu unten.",
            "https://welding.example/processes" => "MIG welding uses an inert shielding gas such as argon. See below.",
            "https://schweissen.example/werkstoffe" => "Schweißzusätze müssen zum Grundwerkstoff passen. Mehr dazu unten.",
            "https://shop.example/angebot" => "Werbung: heute alles billiger. Kaufen Sie jetzt.",
            "https://welding.example/filler" => "The filler wire must match the base metal. See below.",
            _ => "",
        });
    }

    [Fact]
    public async Task OutlineBothLanguagesAndGapsClosedInAFurtherRound()
    {
        var model = new Model();
        var search = new Search();
        using var kb = new KnowledgeBase(_dir.File("schweissen.sckb"), "Schweißen");
        var result = await new KnowledgeAgent(new AgentContext { Model = model, Search = search, Fetcher = new Fetcher() })
            .RunAsync("Schweißen", ["de-DE", "en-GB"], kb, "Verfahren, Werkstoffe", pagesPerLanguage: 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Rounds);
        Assert.Equal(4, result.Added);
        Assert.Equal(1, result.Rejected);                                                // Seite ohne passenden Beleg
        Assert.All(result.Coverage!, c => Assert.True(c.Entries > 0, $"{c.Aspect} {c.Language}"));
        Assert.Contains(kb.All(), e => e.Topic == "Schweißen – Werkstoffe" && e.Language == "en-GB" && e.SourceUrl == "https://welding.example/filler");
        var retry = Assert.Single(model.Prompts, p => p.Contains("Nothing reliable was found yet"));
        Assert.Contains("Werkstoffe [en-GB]", retry);
        Assert.DoesNotContain("Verfahren [", retry);                                     // nur, was noch fehlt
        Assert.Equal(search.Queries.Count, search.Queries.Distinct().Count());
        // ergiebige Seiten sind jetzt Fachseiten zum Thema; die Werbeseite nicht
        var sites = Core.Resources.SiteCatalog.Load().Select(s => s.Domain).ToList();
        Assert.Contains("schweissen.example", sites);
        Assert.Contains("welding.example", sites);
        Assert.DoesNotContain("shop.example", sites);
    }
}
