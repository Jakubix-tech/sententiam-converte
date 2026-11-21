using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Tests;

/// <summary>KI-Bewertung fertiger Übersetzungen: Fehler nach dem Schema der Fehlerbewertung, Punktzahl daraus.</summary>
public sealed class QualityEvaluatorTests
{
    private sealed class Reviewer(string answer) : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task SuggestedErrorsFollowTheEvaluationScheme()
    {
        var model = new Reviewer("""
            {"errors": [
              {"id": 1, "category": "accuracy/mistranslation", "severity": "major", "span": "Wartung", "suggestion": "Reinigung", "explanation": "source says cleaning"},
              {"id": 2, "category": "fluency/grammar", "severity": "minor", "span": "die Gerät", "suggestion": "das Gerät", "explanation": "gender"},
              {"id": 2, "category": "style/register", "severity": "minor", "span": "steht nicht im Text", "explanation": "invented"},
              {"id": 9, "category": "fluency/spelling", "severity": "minor", "span": "x", "explanation": "unknown segment"}
            ]}
            """);
        var brief = new DocumentBrief { Address = AddressForm.Informal, Confirmed = true };
        var found = await new QualityEvaluator(new AgentContext { Model = model }).EvaluateAsync(
            [(1, "Switch off the pump before cleaning.", "Vor der Wartung die Pumpe ausschalten."), (2, "Clean the device.", "Die Gerät reinigen.")],
            "en-GB", "de-DE", brief, TestContext.Current.CancellationToken);

        var first = Assert.Single(found[1]);
        Assert.Equal(("Genauigkeit: Fehlübersetzung", LqaSeverity.Major, QualityEvaluator.Reviewer), (first.Category, first.Severity, first.Reviewer));
        Assert.Equal("„Wartung“ → „Reinigung“: source says cleaning", first.Note);
        var second = Assert.Single(found[2]);                                                // erfundene Stelle fällt weg
        Assert.Equal("Sprache: Grammatik", second.Category);
        Assert.False(found.ContainsKey(9));
        Assert.Contains("informal", model.Prompt);                                            // Steckbrief als Maßstab
        Assert.All(found.Values.SelectMany(e => e), e => Assert.Contains(e.Category, Lqa.Categories));

        // daraus die Punktzahl wie bei menschlichen Prüfern
        var doc = new BilingualDocument { SourceLanguage = "en-GB", TargetLanguage = "de-DE" };
        var p = new Paragraph { Locator = "p" };
        p.Segments.Add(new Segment { Id = 1, Source = "Switch off the pump before cleaning.", Target = "x", Errors = found[1] });
        p.Segments.Add(new Segment { Id = 2, Source = "Clean the device.", Target = "x", Errors = found[2] });
        doc.Paragraphs.Add(p);
        var report = Lqa.Evaluate([doc], new LqaSettings());
        Assert.Equal((2, 6), (report.Errors, report.Points));
    }
}
