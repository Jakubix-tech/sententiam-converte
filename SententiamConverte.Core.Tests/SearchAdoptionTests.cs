using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tests;

/// <summary>Eigene Websuche wird erkannt und genutzt; der Übersetzungsbericht zeigt Sätze, Begriffe und Belege.</summary>
public sealed class SearchAdoptionTests
{
    [Fact]
    public void ARunningLocalSearXngIsAddedWhenOnlyWikipediaSearches()
    {
        var only = new SearchConfig { Kind = SearchProviderKind.Wikipedia };
        Assert.True(only.AdoptLocalSearXng("http://127.0.0.1:8888"));
        Assert.Equal([SearchProviderKind.SearXng, SearchProviderKind.Wikipedia], only.ActiveSources().Select(s => s.Kind));
        Assert.Equal((SearchProviderKind.SearXng, "http://127.0.0.1:8888"), (only.Kind, only.BaseUrl));   // für ältere Fassungen
        Assert.False(only.AdoptLocalSearXng("http://127.0.0.1:8888"));                                     // nur einmal

        // bewusst ausgeschaltet oder schon eine echte Websuche: nichts ändern
        var off = new SearchConfig { Sources = [new SearchSource { Kind = SearchProviderKind.SearXng, Enabled = false }, new SearchSource { Kind = SearchProviderKind.Wikipedia }] };
        Assert.False(off.AdoptLocalSearXng("http://127.0.0.1:8888"));
        var brave = new SearchConfig { Sources = [new SearchSource { Kind = SearchProviderKind.Brave, ApiKey = "x" }] };
        Assert.False(brave.AdoptLocalSearXng("http://127.0.0.1:8888"));
    }

    [Fact]
    public void TheReportExplainsSentencesAndTermsWithEvidence()
    {
        var glrd = new TermDossier
        {
            Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Meaning = "Gleitringdichtung", Reasoning = "Fachbegriff <Pumpen>.",
            Sources = [new SourceRef("en-GB", "Seals", "https://seals.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." }],
        };
        var check = new TranslationWebCheckResult("The gap ring sits.",
            [new PhraseCheck("gap ring", "Spaltring", [new PhraseCount("wear ring", 120, 7, 10, [new UsageExample("https://pumps.example/w", "Pumps", "s") { Sentence = "The wear ring protects it." }])],
                PhraseVerdict.Wrong, "wear ring", "Nicht üblich.")], "The wear ring sits.", "Eine Wendung war falsch.", "KI", "Wikipedia");
        var html = TranslationReport.Html("Übersetzungsbericht – Pumpe", "de-DE", "en-GB",
        [
            new ReportSegment(1, "Die GLRD ist verschlissen.", "The mechanical seal is worn.", TranslationOrigin.Ai, null, [glrd]),
            new ReportSegment(2, "Der Spaltring sitzt.", "The wear ring sits.", TranslationOrigin.Ai, null, [], check, "The gap ring sits."),
        ]);
        Assert.Contains("<h1>Übersetzungsbericht – Pumpe</h1>", html);                       // Umlaute lesbar
        Assert.Contains("Fachbegriff &lt;Pumpen&gt;.", html);                                   // maskiert
        Assert.Contains("2 Segmente, 1 recherchierte Ausdrücke, 1 Übersetzungen im Netz geprüft, 1 verbessert.", html);
        Assert.Contains("https://seals.example/#:~:text=A%20mechanical%20seal%20keeps%20the%20pump%20tight.", html);
        Assert.Contains("<del>The gap ring sits.</del> → <ins>The wear ring sits.</ins>", html);
        Assert.Contains("„gap ring“: falsch", html);
        Assert.Contains("https://pumps.example/w#:~:text=The%20wear%20ring%20protects%20it.", html);
    }

    [Fact]
    public void TheWebCheckKnowsTheResearchOfEachPart()
    {
        var notes = TranslationWebCheck.ResearchNotes(
        [
            new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Meaning = "Gleitringdichtung",
                Sources = [new SourceRef("en-GB", "Seals", "https://s/") { Quote = "a mechanical seal" }] },
            new TermDossier { Expression = "Spaltring", TargetLanguage = "en-GB", Recommended = "wear ring" },
        ]);
        Assert.Contains("- GLRD → mechanical seal (evidenced); meaning: Gleitringdichtung", notes);
        Assert.Contains("- Spaltring → wear ring (not verified)", notes);
        Assert.Equal("", TranslationWebCheck.ResearchNotes(null));
    }
}
