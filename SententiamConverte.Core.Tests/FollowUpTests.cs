using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>Woran man unsichere Ausdrücke erkennt; Vorschläge des Lektorats übernehmen.</summary>
public sealed class FollowUpTests
{
    private static List<Segment> Segments(params string[] texts)
    {
        var paragraph = new Paragraph { Locator = "p0" };
        new Segmenter("de-DE").Segment(paragraph, string.Join(" ", texts));
        for (var i = 0; i < paragraph.Segments.Count; i++) paragraph.Segments[i].Id = i + 1;      // nummeriert sonst das Dokument
        return paragraph.Segments;
    }

    private static readonly SourceRef Evidence = new("en-GB", "Pumps", "https://pumps.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." };

    [Fact]
    public void UncertainExpressionsAreFoundWithTheirReasons()
    {
        var segments = Segments("Die GLRD ist verschlissen.", "Die GLRD wird getauscht.", "Die GLRD tropft.", "Der Spaltring sitzt.", "Das Laufrad dreht.");
        segments[3].Errors = [new LqaError("Terminologie: Falscher Fachbegriff", LqaSeverity.Major, "„gap ring“ → „wear ring“: Fachbegriff", "KI", DateTime.UtcNow)];
        var dossiers = new List<TermDossier>
        {
            new() { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "seal", Rounds = 6, Conclusive = false,
                    Warnings = ["Zeitbudget erreicht: nach 2 von höchstens 6 Suchrunden abgeschlossen"] },
            new() { Expression = "Spaltring", TargetLanguage = "en-GB", Recommended = "gap ring", Sources = [Evidence with { Quote = "The gap ring is worn." }] },
            new() { Expression = "Laufrad", TargetLanguage = "en-GB", Recommended = "impeller", Sources = [Evidence with { Quote = "The impeller turns." }] },
            new() { Expression = "Gehäuse", TargetLanguage = "en-GB", Recommended = "housing" },                       // steht nicht im Dokument
        };

        var found = ResearchFollowUp.Candidates(dossiers, segments, "en-GB", [(5, "the impeller turns")]);

        Assert.Equal(["GLRD", "Spaltring", "Laufrad"], found.Select(c => c.Expression));
        var glrd = found[0];
        Assert.Contains("nicht belegt: keine Quelle der Zielsprache bestätigt die Übersetzung", glrd.Reasons);
        Assert.Contains("nicht eindeutig: kein Kandidat in zwei unabhängigen Quellen", glrd.Reasons);
        Assert.Contains("Zeitbudget erreicht – nicht zu Ende gesucht", glrd.Reasons);
        Assert.Contains("kommt in 3 Segmenten vor – eine falsche Wahl wirkt sich mehrfach aus", glrd.Reasons);
        Assert.Equal([1, 2, 3], glrd.SegmentIds);
        // belegt, aber vom Lektorat bemängelt – die Anmerkung nennt die gewählte Übersetzung
        Assert.Equal(["Lektorat: Terminologie: Falscher Fachbegriff in Segment 4 – betrifft diesen Ausdruck"], found[1].Reasons);
        Assert.Equal(["Netzprüfung: „the impeller turns“ in Segment 5 fraglich"], found[2].Reasons);

        // ohne Zeichen kein Kandidat: belegt, eindeutig, nicht bemängelt
        Assert.Empty(ResearchFollowUp.Candidates([dossiers[2]], segments, "en-GB"));

        // die Nachrecherche ändert die Übersetzung, wenn sie jetzt belegt ist oder etwas anderes belegt
        var better = new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Sources = [Evidence] };
        Assert.True(ResearchFollowUp.ChangesTranslation(dossiers[0], better));
        Assert.False(ResearchFollowUp.ChangesTranslation(better, better));
        Assert.False(ResearchFollowUp.ChangesTranslation(better, dossiers[0]));
    }

    [Fact]
    public void ASuggestionOfTheReviewReplacesTheCriticisedWording()
    {
        var error = new LqaError("Terminologie: Falscher Fachbegriff", LqaSeverity.Major, "„gap ring“ → „wear ring“: Fachbegriff im Pumpenbau", "KI", DateTime.UtcNow);
        Assert.Equal(("gap ring", "wear ring"), Lqa.Suggestion(error));
        Assert.Equal("The wear ring sits on the <b>impeller</b>.", Lqa.ApplySuggestion("The gap ring sits on the <b>impeller</b>.", error));
        Assert.Equal("The wear ring sits.", Lqa.ApplySuggestion("The Gap ring sits.", error));             // Groß-/Kleinschreibung egal
        Assert.Null(Lqa.ApplySuggestion("The ring sits.", error));                                        // Stelle steht nicht da

        // ohne Verbesserung (nur Stelle und Begründung) oder von Hand ohne Anmerkung: nichts einzusetzen
        Assert.Null(Lqa.Suggestion(error with { Note = "„gap ring“: falscher Begriff" }));
        Assert.Null(Lqa.ApplySuggestion("The gap ring sits.", error with { Note = "falscher Begriff" }));
    }
}
