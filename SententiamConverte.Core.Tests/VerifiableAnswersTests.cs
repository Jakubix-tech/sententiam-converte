using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Jede Angabe nachprüfbar: Verwendungsbeispiele mit dem Satz von der Seite und einem Link dorthin, ein klares
/// „nicht üblich“, Weitersuchen mit eigenen Ideen, und die klare Aussage, wozu es noch keine Informationen gibt.
/// </summary>
public sealed class VerifiableAnswersTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Model(Func<string, string> answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer(messages[^1].Text));
        }
    }

    private sealed class Search : ISearchProvider
    {
        public List<string> Queries { get; } = new();
        public string Name => "fake";
        public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(query switch
            {
                "\"Drehkraft anziehen\"" => [new SearchResult("Forum", "https://forum.example/1", "Wie fest anziehen?")],
                "\"Drehkraft anziehen\" Anleitung" => [new SearchResult("Anleitung", "https://manual.example/a", "Schrauben anziehen")],
                "\"Anzugsdrehmoment\"" =>
                [
                    new SearchResult("A", "https://a.example/1", "Anzugsdrehmoment der Schraube"),
                    new SearchResult("B", "https://b.example/2", "das Anzugsdrehmoment beträgt"),
                    new SearchResult("C", "https://c.example/3", "Anzugsdrehmoment"),
                ],
                _ => new List<SearchResult>(),
            });
        }
    }

    private sealed class Pages : IPageFetcher
    {
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) => Task.FromResult<string?>(url switch
        {
            "https://forum.example/1" => "Hallo zusammen. Wie fest muss ich die Radmuttern anziehen? Danke.",
            "https://manual.example/a" => "Kapitel 3. Die Schrauben gleichmäßig über Kreuz anziehen. Ende.",
            _ => "Montage. Das Anzugsdrehmoment beträgt 5 Nm. Danach prüfen.",
        });
    }

    private sealed class Log(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    [Fact]
    public async Task UsageCheckSearchesOnQuotesSentencesAndSaysWhatIsNotUsual()
    {
        var log = new List<string>();
        var search = new Search();
        var model = new Model(p =>
            p.Contains("web search queries that are likely")
                ? """{"queries": {"Drehkraft anziehen": ["\"Drehkraft anziehen\" Anleitung", "Drehkraft ohne Anführungszeichen"]} }"""
                : """{"verdict": "common", "recommended": "Anzugsdrehmoment", "explanation": "Für Drehkraft anziehen gibt es keinen Beleg, Anzugsdrehmoment ist üblich [1]."}""");
        var ctx = new AgentContext { Model = model, Search = search, Fetcher = new Pages(), Log = new Log(log) };

        var r = await new UsageCheckAgent(ctx).RunAsync("Drehkraft anziehen", "de-DE", "Maschinenbau", ["Anzugsdrehmoment"],
            ct: TestContext.Current.CancellationToken);

        // erst wörtlich, dann mit Fachgebiet, dann mit der eigenen Idee der KI (die ohne Anführungszeichen fällt weg)
        Assert.Equal(["\"Drehkraft anziehen\"", "\"Drehkraft anziehen\" Maschinenbau", "\"Drehkraft anziehen\" Anleitung"], r.Evidence[0].Queries);
        Assert.Contains("Zu „Drehkraft anziehen“ habe ich erst 0 wörtliche Belege – ich suche im Fachgebiet weiter …", log);
        Assert.Contains("Zu „Drehkraft anziehen“ noch zu wenig Belege (0) – ich suche anders: \"Drehkraft anziehen\" Anleitung", log);
        Assert.Contains("  Für „Drehkraft anziehen“ habe ich keinen wörtlichen Beleg gefunden (2 Treffer, 3 Suchen).", log);

        // die KI sagt „üblich“, aber es gibt keinen einzigen Beleg: nicht üblich
        Assert.Equal(UsageVerdict.Unattested, r.Verdict);
        var language = Languages.DisplayName("de-DE");
        Assert.Equal($"„Drehkraft anziehen“ ist in {language} nicht üblich: kein wörtlicher Beleg in 2 Treffern. "
                     + "Üblich ist „Anzugsdrehmoment“: 3 wörtliche Belege ([1], [2], [3]).", r.Statement);

        // Belege: der Satz von der Seite, mit Link direkt zur Stelle
        var example = r.Evidence[1].Examples[0];
        Assert.Equal(("Das Anzugsdrehmoment beträgt 5 Nm.", true, 1), (example.Sentence, example.Verified, example.Number));
        Assert.Equal("https://a.example/1#:~:text=Das%20Anzugsdrehmoment%20betr%C3%A4gt%205%20Nm.", example.Link);
        Assert.Contains("[1] https://a.example/1: Das Anzugsdrehmoment beträgt 5 Nm.", model.Prompts[^1]);
        Assert.Contains("support every statement with the evidence numbers", model.Prompts[^1]);
    }

    [Fact]
    public void LongSentencesAreCutAroundThePhrase()
    {
        var text = "Einleitung. " + new string('a', 300) + " das Anzugsdrehmoment beträgt " + new string('b', 300) + ". Schluss.";
        var s = UsageCheckAgent.SentenceWith(text, "Anzugsdrehmoment")!;
        Assert.StartsWith("… ", s);
        Assert.EndsWith(" …", s);
        Assert.Contains("das Anzugsdrehmoment beträgt", s);
        Assert.Null(UsageCheckAgent.SentenceWith("Nichts davon.", "Anzugsdrehmoment"));
    }

    [Fact]
    public async Task AnswersSayWhatIsNotKnownYetAndFlagUnsourcedStatements()
    {
        using var kb = new KnowledgeBase(_dir.File("pumpen.sckb"), "Pumpen");
        kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Pumpen", Title = "Kreiselpumpe", Content = "Eine Kreiselpumpe fördert Flüssigkeiten mit einem Laufrad.", SourceUrl = "https://example.de/kreiselpumpe" });
        kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Pumpen", Title = "Laufrad", Content = "Das Laufrad einer Kreiselpumpe dreht sich und überträgt Energie." });
        var model = new Model(p => p.Contains("most important search terms")
            ? """{"terms": {"de": ["Gleitringdichtung", "Kreiselpumpe", "Dichtung"]}}"""
            : """{"answer": "Eine Kreiselpumpe fördert Flüssigkeiten mit einem Laufrad [1]. Das Laufrad überträgt Energie [2]. Sie ist in der Industrie weit verbreitet und sehr wichtig.", "found": false, "missing": "Gleitringdichtung"}""");

        var answer = await new KnowledgeQa(new AgentContext { Model = model })
            .AskAsync("Was ist eine Gleitringdichtung in einer Kreiselpumpe?", [kb], ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Gleitringdichtung"], answer.UnknownTerms);                         // „Dichtung“ steht nicht als Wort in der Frage
        Assert.Equal("Zu „Gleitringdichtung“ habe ich noch keine Informationen.", answer.NotYetKnown);
        Assert.Equal(1, answer.UncitedStatements);                                          // der dritte Satz hat keinen Beleg
        var laufrad = answer.Notes.Single(n => n.Title == "Laufrad").Number;
        Assert.Equal([laufrad], answer.CitedWithoutSource);                                // zitiert, aber ohne Link
        Assert.Contains(", no source) Laufrad:", model.Prompts[^1]);
        Assert.Contains("every sentence needs a number", model.Prompts[^1]);
    }
}
