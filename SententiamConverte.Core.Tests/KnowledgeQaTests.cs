using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;

namespace SententiamConverte.Core.Tests;

/// <summary>Fragen an das Wissen: passende Einträge auswählen (über Sprachen hinweg), nur daraus antworten, Quellen prüfen.</summary>
public sealed class KnowledgeQaTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Model(string answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            return Task.FromResult(p.Contains("most important search terms")
                ? """{"terms": {"de": ["MIG", "Schweißgerät", "Drahtvorschub"], "en": ["MIG welding", "wire feeder"]}}"""
                : answer);
        }
    }

    private KnowledgeBase Welding()
    {
        var kb = new KnowledgeBase(_dir.File("schweissen.sckb"), "Schweißen");
        kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Schweißen", Title = "Löten", Content = "Beim Löten schmilzt nur das Lot, nicht das Grundmaterial." });
        kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Schweißen", Title = "MIG/MAG-Schweißgerät", Content = "Ein MIG-Schweißgerät besteht aus Stromquelle, Drahtvorschub, Schlauchpaket und Brenner.", SourceUrl = "https://example.de/mig" });
        kb.Add(new KnowledgeEntry { Language = "en-GB", Topic = "welding", Title = "Wire feeder", Content = "In MIG welding the wire feeder pushes the electrode wire through the hose package to the torch.", Quote = "the wire feeder pushes the electrode wire" });
        kb.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Schweißen", Title = "Schutzgas", Content = "Bei MAG wird ein aktives Gas wie CO2 verwendet, bei MIG ein inertes wie Argon." });
        return kb;
    }

    [Fact]
    public async Task AnswersOnlyFromMatchingNotesWithCheckedCitations()
    {
        using var kb = Welding();
        var model = new Model("""{"answer": "Ein MIG-Schweißgerät besteht aus Stromquelle, Drahtvorschub, Schlauchpaket und Brenner [1]; der Drahtvorschub schiebt den Draht zum Brenner [2]. Siehe auch [9].", "found": true, "missing": ""}""");
        var answer = await new KnowledgeQa(new AgentContext { Model = model }).AskAsync("Wie ist ein MIG-Schweißgerät aufgebaut?", [kb], "Schweißen",
            ct: TestContext.Current.CancellationToken);

        Assert.True(answer.Found);
        Assert.Equal([1, 2], answer.Cited);                                                  // [9] gibt es nicht
        Assert.Equal(["MIG/MAG-Schweißgerät", "Wire feeder"], answer.Notes.Take(2).Select(n => n.Title).Order()); // die passendsten zuerst, auch englische
        Assert.DoesNotContain(answer.Notes, n => n.Title == "Löten" && n.Number <= 2);
        var prompt = model.Prompts[^1];
        Assert.Contains("(Wissen, de-DE) MIG/MAG-Schweißgerät:", prompt);
        Assert.Contains("„the wire feeder pushes the electrode wire“", prompt);
        Assert.Contains("Use ONLY facts from the notes", prompt);
        Assert.Contains("languages: de, en", model.Prompts[0]);                              // Suchwörter in den Sprachen des Wissens
    }

    [Fact]
    public async Task SaysWhatIsMissing()
    {
        using var kb = Welding();
        var model = new Model("""{"answer": "Über Laserschweißen steht in den Notizen nichts.", "found": false, "missing": "Laserschweißen: Aufbau und Einsatz"}""");
        var answer = await new KnowledgeQa(new AgentContext { Model = model }).AskAsync("Wie funktioniert Laserschweißen?", [kb],
            ct: TestContext.Current.CancellationToken);
        Assert.False(answer.Found);
        Assert.Equal("Laserschweißen: Aufbau und Einsatz", answer.Missing);

        // ganz leeres Wissen: keine Antwort der KI nötig
        using var empty = new KnowledgeBase(_dir.File("leer.sckb"), "Leer");
        var none = await new KnowledgeQa(new AgentContext { Model = model }).AskAsync("Was ist WIG?", [empty], ct: TestContext.Current.CancellationToken);
        Assert.Equal((false, "Dazu habe ich noch keine Informationen.", 0), (none.Found, none.Answer, none.Notes.Count));
        Assert.Equal("Dazu habe ich noch keine Informationen: Laserschweißen: Aufbau und Einsatz.", answer.NotYetKnown);
        Assert.Equal(3, model.Prompts.Count);                                                // 2 × Suchwörter, 1 × Antwort
    }

    [Fact]
    public async Task PlainTextAnswersStillWork()
    {
        using var kb = Welding();
        var answer = await new KnowledgeQa(new AgentContext { Model = new Model("Bei MAG wird ein aktives Gas wie CO2 verwendet [1].") })
            .AskAsync("Welches Gas nimmt man bei MAG?", [kb], ct: TestContext.Current.CancellationToken);
        Assert.True(answer.Found);
        Assert.StartsWith("Bei MAG wird", answer.Answer);
        Assert.Equal([1], answer.Cited);
    }
}
