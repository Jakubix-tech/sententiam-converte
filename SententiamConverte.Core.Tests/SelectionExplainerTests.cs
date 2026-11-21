using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Markierung erklären: Bedeutung, Grammatik, Sprachwissenschaft – die Gegenstelle nur, wenn sie wörtlich dasteht.</summary>
public sealed class SelectionExplainerTests
{
    private sealed class Model(string counterpart) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult($$"""{"meaning": "wieder einsetzbar", "grammar": "Infinitiv mit prädikativem Adjektiv", "linguistics": "feste Verbindung", "counterpart": "„{{counterpart}}“", "assessment": "trifft", "alternatives": ["operational again", "operational again", " "]}""");
        }
    }

    [Fact]
    public async Task ExplainsAndKeepsOnlyACounterpartThatReallyOccurs()
    {
        var research = new List<TermDossier> { new() { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Meaning = "Gleitringdichtung" } };
        var model = new Model("back in working order");
        var r = await new SelectionExplainer(new AgentContext { Model = model, ExplanationLanguage = "Deutsch" }).ExplainAsync(
            "wieder arbeitsfähig", false, "Die GLRD muss wieder arbeitsfähig werden.", "The mechanical seal must be back in working order.",
            "de-DE", "en-GB", "Pumpen", research, ct: TestContext.Current.CancellationToken);

        Assert.Equal("wieder einsetzbar", r.Meaning);
        Assert.Equal("Infinitiv mit prädikativem Adjektiv", r.Grammar);
        Assert.Equal("back in working order", r.Counterpart);                 // Anführungszeichen entfernt, steht wörtlich in der Übersetzung
        Assert.Equal(["operational again"], r.Alternatives);
        Assert.Contains("marked these words in the source text", model.Prompts[0]);
        Assert.Contains("GLRD → mechanical seal (not evidenced)", model.Prompts[0]);
        Assert.Contains("in Deutsch", model.Prompts[0]);

        // eine Gegenstelle, die so nicht in der anderen Sprache steht, wird nicht gezeigt
        var invented = await new SelectionExplainer(new AgentContext { Model = new Model("ready for work") }).ExplainAsync(
            "wieder arbeitsfähig", false, "Die GLRD muss wieder arbeitsfähig werden.", "The mechanical seal must be back in working order.",
            "de-DE", "en-GB", ct: TestContext.Current.CancellationToken);
        Assert.Null(invented.Counterpart);
    }
}
