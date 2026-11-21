using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>Was bei der Recherche mit kleinen lokalen Modellen schiefging: Belegnummern als Text, Personennamen als Ausdruck.</summary>
public sealed class ResearchRobustnessTests
{
    [Fact]
    public void EvidenceNumbersWrittenAsTextStillCount()
    {
        var c = JsonExtract.Parse<CandidateAssessment>("""{"term": "x", "equivalence": "Full", "evidence": [1, "2", "[3]", "Beleg 4", null, {"n": 5}]}""");
        Assert.Equal([1, 2, 3, 4], c.Evidence);
        Assert.Empty(JsonExtract.Parse<CandidateAssessment>("""{"term": "x", "evidence": "keine"}""").Evidence);
    }

    private sealed record AnalysisLike(string Meaning, List<string>? KeyFeatures, Dictionary<string, List<string>>? Queries, string? Note);

    [Fact]
    public void AnswersThatBendTheRequestedShapeAreStillRead()
    {
        // so kam es von der KI: unter „queries“ je Sprache ein einzelner Text bzw. ein Objekt statt einer Liste
        var a = JsonExtract.Parse<AnalysisLike>("""
            {"meaning": "physical components", "keyFeatures": "Tangibility",
             "queries": {"fr-FR": "matériel informatique", "de-DE": ["Hardware Begriff", 2024], "en": {"1": "hardware", "2": "  "}},
             "note": ["erstens", "zweitens"]}
            """);
        Assert.Equal("physical components", a.Meaning);
        Assert.Equal(["Tangibility"], a.KeyFeatures);
        Assert.Equal(["matériel informatique"], a.Queries!["fr-FR"]);
        Assert.Equal(["Hardware Begriff", "2024"], a.Queries["de-DE"]);
        Assert.Equal(["hardware"], a.Queries["en"]);
        Assert.Equal("erstens; zweitens", a.Note);
        // wie verlangt geht weiter, Zahlen als Text auch
        var b = JsonExtract.Parse<AnalysisLike>("""{"meaning": 42, "keyFeatures": ["a", "b"], "queries": {"en": ["x"]}, "note": null}""");
        Assert.Equal(("42", 2, (string?)null), (b.Meaning, b.KeyFeatures!.Count, b.Note));
    }

    [Fact]
    public void OnlyEvidencedRecommendationsCount()
    {
        TermDossier D(string? recommended, params SourceRef[] sources) =>
            new() { Expression = "Gleitringdichtung", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = recommended, Sources = [.. sources] };
        var en = new SourceRef("en", "Seals", "https://example.org/a") { Quote = "Replace the Mechanical-Seal before start-up." };
        var de = new SourceRef("de-DE", "Dichtungen", "https://example.de/a") { Quote = "Die mechanical seal heißt hier Gleitringdichtung." };
        Assert.True(DossierStore.IsEvidenced(D("mechanical seal", en)));                 // Groß/klein, Bindestrich egal
        Assert.True(DossierStore.IsEvidenced(D("Mechanical seal", new SourceRef("en-US", "Mechanical seal", "https://en.wikipedia.org/wiki/Mechanical_seal"))));
        Assert.False(DossierStore.IsEvidenced(D("mechanical seal", de)));                // nur in einer Quelle der Ausgangssprache
        Assert.False(DossierStore.IsEvidenced(D("sliding ring seal", en)));
        Assert.False(DossierStore.IsEvidenced(D("seal", new SourceRef("en", "x", "https://x.org") { Quote = "Sealing rings" })));   // nur ganze Wörter
        Assert.False(DossierStore.IsEvidenced(D(null, en)));
    }

    [Fact]
    public void PersonNamesAreNotResearched()
    {
        var paragraph = new Paragraph { Locator = "p0" };
        new Segmenter("de-DE").Segment(paragraph, "Sehr geehrte Frau Kowalska, vielen Dank für Ihre Anfrage vom 12. März.");
        var segments = paragraph.Segments;
        Assert.True(DocumentResearch.IsProtected("Frau Kowalska", segments));
        Assert.True(DocumentResearch.IsProtected("Kowalska", segments));
        Assert.False(DocumentResearch.IsProtected("Anfrage", segments));
    }
}
