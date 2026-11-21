using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

/// <summary>Fachbegriffe aus den Ausgangstexten gewinnen.</summary>
public class TermExtractorTests
{
    private static readonly string[] German =
    [
        "Der Mieter zahlt die monatliche Miete bis zum dritten Werktag.",
        "Die Kaution beträgt drei Monatsmieten.",
        "Der Vermieter legt die Kaution auf einem Sparkonto an.",
        "Die Betriebskostenabrechnung erfolgt jährlich.",
        "Der Mieter kann der Betriebskostenabrechnung innerhalb von zwölf Monaten widersprechen.",
        "Die ordentliche Kündigung des Mietvertrags ist schriftlich zu erklären.",
        "Eine ordentliche Kündigung ist mit einer Frist von drei Monaten möglich.",
        "Der Mietvertrag endet mit Ablauf der Kündigungsfrist.",
    ];

    [Fact]
    public void RepeatedNounsAndPhrasesBecomeCandidates()
    {
        var terms = TermExtractor.Candidates(German, "de-DE").Select(t => t.Term).ToList();
        Assert.Contains("Mieter", terms);
        Assert.Contains("Kaution", terms);
        Assert.Contains("ordentliche Kündigung", terms);
        Assert.Contains("Betriebskostenabrechnung", terms);
        Assert.Contains("Kündigungsfrist", terms);                    // langes Kompositum, auch einmal
        Assert.DoesNotContain("der", terms, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Kündigung", terms);                    // nur als Teil von „ordentliche Kündigung“
        Assert.DoesNotContain(terms, t => t.StartsWith("Die ", StringComparison.Ordinal));

        var known = TermExtractor.Candidates(German, "de-DE", known: t => t == "Kaution").Select(t => t.Term);
        Assert.DoesNotContain("Kaution", known);

        var en = TermExtractor.Candidates(
            ["The tenant pays the security deposit.", "The security deposit is held in a savings account.", "The tenant may terminate the lease."],
            "en-GB").Select(t => t.Term).ToList();
        Assert.Contains("security deposit", en);
        Assert.Contains("tenant", en);
        Assert.DoesNotContain("the", en, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Scripted(string reply) : IChatModel
    {
        public string DisplayName => "Drehbuch";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) => Task.FromResult(reply);
    }

    [Fact]
    public async Task TheAiPicksTermsAndSuggestsTranslations()
    {
        var candidates = TermExtractor.Candidates(German, "de-DE");
        var model = new Scripted("""{"terms": [{"s": "Kaution", "t": "kaucja"}, {"s": "ordentliche Kündigung", "t": "wypowiedzenie umowy"}, {"s": "Rückgabeprotokoll", "t": "protokół zdawczy"}]}""");
        var terms = await TermExtractor.RefineAsync(model, candidates, German, "de-DE", "pl-PL", "Mietrecht", TestContext.Current.CancellationToken);
        Assert.Equal(["Kaution", "ordentliche Kündigung"], terms.Select(t => t.Source).Order());   // erfundener Begriff fällt weg
        Assert.Equal(2, terms.First(t => t.Source == "Kaution").Count);
        Assert.Equal("wypowiedzenie umowy", terms.First(t => t.Source == "ordentliche Kündigung").Target);
    }
}
