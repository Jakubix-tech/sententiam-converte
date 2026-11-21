using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Die Bewertung lernt aus der Recherche-Statistik dieses Rechners, wie viel Zeit ein Ausdruck wirklich braucht.</summary>
public sealed class ResearchStatsTests
{
    private sealed class Capture(string answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer);
        }
    }

    private static ResearchStat Stat(string model, ResearchDifficulty level, double minutes, bool evidenced) =>
        new(DateTime.UtcNow, model, level, 3, minutes, 2, evidenced);

    [Fact]
    public async Task TheAssessmentLearnsHowLongResearchReallyTakes()
    {
        using var dir = new TempDir();
        ResearchStats.UseFolderHere(dir.File("stat"));
        Assert.Null(ResearchStats.PromptNote("Gemma"));                                     // noch nichts gemessen
        foreach (var m in new[] { 4.0, 6.0, 8.0, 5.0 }) ResearchStats.Record(Stat("Gemma", ResearchDifficulty.Hard, m, true));
        ResearchStats.Record(Stat("Gemma", ResearchDifficulty.Hard, 9, false));
        foreach (var m in new[] { 0.5, 0.7 }) ResearchStats.Record(Stat("Gemma", ResearchDifficulty.Easy, m, true));
        ResearchStats.Record(Stat("Online", ResearchDifficulty.Hard, 1, true));             // andere KI: zählt nicht mit

        var hard = ResearchStats.Summary("Gemma").Single(l => l.Level == ResearchDifficulty.Hard);
        Assert.Equal((5, 4, 6.0), (hard.Count, hard.Evidenced, hard.TypicalMinutes!.Value)); // Median der belegten
        Assert.Equal(7.5, ResearchStats.LearnedMinutes("Gemma", ResearchDifficulty.Hard));    // mit Spielraum ×1,25
        Assert.Null(ResearchStats.LearnedMinutes("Gemma", ResearchDifficulty.Easy));         // zu wenige Fälle: feste Annahme
        Assert.Null(ResearchStats.LearnedMinutes("Online", ResearchDifficulty.Hard));
        var note = ResearchStats.PromptNote("Gemma")!;
        Assert.Contains("hard expressions: 4 of 5 evidenced, typically after 6 min", note);
        Assert.Contains("easy expressions: 2 of 2 evidenced", note);

        // die Bewertung bekommt den Hinweis, und ohne eigene Angabe der KI gilt das Gelernte statt der festen 6 min
        var model = new Capture("""{"difficulty": "hard", "expressions": [{"expression": "GLRD", "action": "research"}]}""");
        var a = await new SegmentTriage(new AgentContext { Model = model }).AssessAsync("Die GLRD.", [], [],
            [new ResearchItem { Expression = "GLRD", Difficulty = ResearchDifficulty.Hard }], "de-DE", "en-GB",
            ct: TestContext.Current.CancellationToken, measured: note, learned: level => ResearchStats.LearnedMinutes("Gemma", level));
        Assert.Equal(7.5, a.For("GLRD")!.Minutes);
        Assert.Contains("Measured on this computer with Gemma", model.Prompts[0]);
    }
}
