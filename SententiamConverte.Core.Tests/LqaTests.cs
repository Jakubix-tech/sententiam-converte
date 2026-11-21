using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Tests;

/// <summary>Lektorat: Fehlerbewertung und Änderungsverfolgung.</summary>
public class LqaTests
{
    private static BilingualDocument Doc(int words)
    {
        var doc = new BilingualDocument { FileName = "a.docx", SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        var p = new Paragraph();
        p.Segments.Add(new Segment { Id = 1, Source = string.Join(" ", Enumerable.Repeat("Wort", words)), Target = "word", Status = SegmentStatus.Translated });
        doc.Paragraphs.Add(p);
        return doc;
    }

    [Fact]
    public void ErrorsGivePenaltyPointsAndAScore()
    {
        var doc = Doc(200);
        var s = doc.Segments.First();
        s.Errors =
        [
            new LqaError("Genauigkeit: Auslassung", LqaSeverity.Major, "Satzteil fehlt", "Prüfer", DateTime.UtcNow),
            new LqaError("Sprache: Zeichensetzung", LqaSeverity.Minor, "", "Prüfer", DateTime.UtcNow),
            new LqaError("Stil: Register oder Ton", LqaSeverity.Neutral, "Geschmack", "Prüfer", DateTime.UtcNow),
        ];
        var r = Lqa.Evaluate([doc], new LqaSettings());
        Assert.Equal((3, 6), (r.Errors, r.Points));
        Assert.Equal(97.0, r.Score);                                 // 100 − 6 × 100 / 200
        Assert.Equal(30, r.PointsPerThousand);
        Assert.False(r.Passed);                                       // Grenze 98
        Assert.True(Lqa.Evaluate([doc], new LqaSettings { PassScore = 95 }).Passed);
        Assert.Equal(["Genauigkeit", "Sprache", "Stil"], r.Categories.Select(c => c.Category));

        s.Errors.Add(new LqaError("Genauigkeit: Fehlübersetzung", LqaSeverity.Critical, "", "Prüfer", DateTime.UtcNow));
        Assert.True(Lqa.Evaluate([Doc(100_000)], new LqaSettings()).Passed);                // ohne Fehler bestanden
        var big = Doc(100_000);
        big.Segments.First().Errors = [new LqaError("Genauigkeit: Fehlübersetzung", LqaSeverity.Critical, "", "P", DateTime.UtcNow)];
        Assert.False(Lqa.Evaluate([big], new LqaSettings()).Passed);  // kritisch: immer durchgefallen

        using var ms = new MemoryStream();
        Lqa.ExportDocx("a.docx (en-GB)", [doc], new LqaSettings(), ms);
        ms.Position = 0;
        using var word = WordprocessingDocument.Open(ms, false);
        var text = word.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("NICHT BESTANDEN", text);
        Assert.Contains("Satzteil fehlt", text);
    }

    [Fact]
    public void ChangesOfTheReviewerCanBeAcceptedOrRejected()
    {
        var s = new Segment { Source = "Schalten Sie das Gerät aus.", Target = "Switch the device off.", Status = SegmentStatus.Translated };
        TrackChanges.Remember(s);
        s.Target = "Switch off the device.";
        Assert.True(TrackChanges.HasChange(s));
        Assert.Equal(
            [(WordDiff.Kind.Same, "Switch "), (WordDiff.Kind.Inserted, "off "), (WordDiff.Kind.Same, "the "), (WordDiff.Kind.Deleted, "device off."), (WordDiff.Kind.Inserted, "device.")],
            WordDiff.Compare("Switch the device off.", "Switch off the device.").Select(p => (p.Kind, p.Text)));

        TrackChanges.Reject(s);
        Assert.Equal("Switch the device off.", s.Target);
        Assert.False(TrackChanges.HasChange(s));
        Assert.Equal("Switch off the device.", s.History[^1].Target);      // abgelehnte Fassung im Verlauf

        TrackChanges.Remember(s);
        s.Target = "Turn the device off.";
        TrackChanges.Accept(s);
        Assert.Null(s.ReviewBaseline);
        Assert.Equal("Turn the device off.", s.Target);

        var draft = new Segment { Source = "x", Target = "y", Status = SegmentStatus.Draft };
        TrackChanges.Remember(draft);
        Assert.Null(draft.ReviewBaseline);                                   // unbestätigte Entwürfe werden nicht verfolgt
    }
}
