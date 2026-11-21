using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Gespeicherte Recherche: Was die KI in einem Satz fand, steht beim Projekt und gilt für jeden gleichen Satz; ein
/// anders erfasster Ausdruck ist derselbe; Meldungen je Segment bleiben beim Dokument.
/// </summary>
public sealed class SegmentScanTests
{
    [Fact]
    public void TheSameExpressionIsRecognisedHoweverItWasCaptured()
    {
        Assert.True(DocumentResearch.SameExpression("der Spaltring", "Spaltring"));
        Assert.True(DocumentResearch.SameExpression("Spaltringe", "Spaltring"));
        Assert.True(DocumentResearch.SameExpression("„Laufrad“", "laufräder"));
        Assert.True(DocumentResearch.SameExpression("uszczelnienia mechanicznego", "uszczelnienie mechaniczne"));
        Assert.True(DocumentResearch.SameExpression("GLRD", "glrd"));
        Assert.True(DocumentResearch.SameExpression("the mechanical seal", "mechanical seal"));
        Assert.False(DocumentResearch.SameExpression("Druck", "Drucker"));                  // kurz: nur gleich
        Assert.False(DocumentResearch.SameExpression("Pumpe", "Kreiselpumpe"));
        Assert.False(DocumentResearch.SameExpression("Spaltring", "Spaltring am Laufrad"));
        Assert.False(DocumentResearch.SameExpression("Dichtung", "Dichtfläche"));
    }

    private static TranslationProject Project(TempDir dir) => TranslationProject.Create(dir.File("p"), "Satzspeicher", "de-DE", ["en-GB"]);

    private static Segment Seg(int id, string source) => new() { Id = id, Source = source };

    [Fact]
    public void FoundExpressionsAreStoredPerSentenceAndServeEverySameSentence()
    {
        using var dir = new TempDir();
        using var p = Project(dir);
        SegmentScanStore.Save(p, "Der  Spaltring sitzt am Laufrad.", "en-GB",
            [new ResearchItem { Expression = "Spaltring", Category = "term", Difficulty = ResearchDifficulty.Medium, SegmentIds = [7] }]);
        SegmentScanStore.SaveMany(p, [("Die Pumpe läuft.", []), ("Neue Spaltringe einbauen.", [new ResearchItem { Expression = "Spaltringe" }])], "en-GB");
        SegmentScanStore.Save(p, "Der Spaltring sitzt am Laufrad.", "en-GB", null, "hard", "Segment 7: schwierig");     // nur die Bewertung

        // ein anderes Dokument (andere Nummern), Tags und Leerraum anders
        var index = SegmentScanStore.Index(SegmentScanStore.Load(p), "en-US");
        var (items, scanned) = SegmentScanStore.ForDocument(index,
            [Seg(1, "Der Spaltring sitzt ⟨1⟩am⟨/1⟩ Laufrad."), Seg(2, "Die Pumpe läuft."), Seg(3, "Ganz neu."), Seg(4, "Neue Spaltringe einbauen.")]);
        Assert.Equal([1, 2, 4], scanned.Order());                                            // „Ganz neu.“ muss die KI noch lesen
        var item = Assert.Single(items);                                                    // „Spaltringe“ ist derselbe Ausdruck
        Assert.Equal(("Spaltring", ResearchDifficulty.Medium), (item.Expression, item.Difficulty));
        Assert.Equal([1, 4], item.SegmentIds);
        var scan = index[SegmentScanStore.KeyOf("Der Spaltring sitzt am Laufrad.")];
        Assert.Equal(("hard", "Segment 7: schwierig", 1), (scan.AssessmentLevel, scan.AssessmentInfo, scan.Items.Count));   // Bewertung ohne die Ausdrücke zu verlieren
        Assert.Empty(scan.Items[0].SegmentIds);                                             // Nummern gehören zum Dokument, nicht zum Satz
        // andere Zielsprache: nichts
        Assert.Empty(SegmentScanStore.Index(SegmentScanStore.Load(p), "pl-PL"));
    }

    [Fact]
    public void SegmentNotesStayWithTheDocument()
    {
        using var dir = new TempDir();
        using var p = Project(dir);
        var t = DateTime.UtcNow;
        SegmentNotes.Add(p, "datei1", new SegmentNote(3, "Recherche", "Warnung", "„GLRD“: reicht noch nicht", t));
        SegmentNotes.Add(p, "datei1", new SegmentNote(3, "Recherche", "Warnung", "„GLRD“: reicht noch nicht", t.AddMinutes(1)));   // dieselbe: ersetzt
        SegmentNotes.Add(p, "datei1", new SegmentNote(3, "KI-Prüfung", "Warnung", "zweifelhaft (55 %)", t));
        SegmentNotes.Add(p, "datei2", new SegmentNote(3, "Rückmeldung", "Hinweis", "anderes Dokument", t));
        var notes = SegmentNotes.Load(p, "datei1");
        Assert.Equal(2, notes.Count);
        Assert.Equal(t.AddMinutes(1), notes.Single(n => n.Kind == "Recherche").When);

        // eine neue KI-Prüfung ersetzt die frühere; ohne Befund verschwindet sie
        SegmentNotes.Replace(p, "datei1", 3, "KI-Prüfung", new SegmentNote(3, "KI-Prüfung", "Fehler", "falsch (30 %)", t));
        Assert.Equal("falsch (30 %)", SegmentNotes.Load(p, "datei1").Single(n => n.Kind == "KI-Prüfung").Text);
        SegmentNotes.Replace(p, "datei1", 3, "KI-Prüfung", null);
        Assert.DoesNotContain(SegmentNotes.Load(p, "datei1"), n => n.Kind == "KI-Prüfung");

        // je Segment die letzten 30
        for (var i = 0; i < 40; i++) SegmentNotes.Add(p, "datei1", new SegmentNote(5, "Übersetzung", "Fehler", $"Fehler {i}", t.AddSeconds(i)));
        var five = SegmentNotes.Load(p, "datei1").Where(n => n.SegmentId == 5).ToList();
        Assert.Equal(30, five.Count);
        Assert.Equal("Fehler 39", five[^1].Text);
        Assert.Single(SegmentNotes.Load(p, "datei2"));
    }
}
