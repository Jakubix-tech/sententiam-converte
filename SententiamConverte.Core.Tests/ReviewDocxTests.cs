using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.Core.Tests;

/// <summary>Prüfdatei: Lektorat in Word, Rückimport mit Änderungsverfolgung, Anmerkungen und Word-Kommentaren.</summary>
public sealed class ReviewDocxTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static BilingualDocument Doc()
    {
        var doc = new BilingualDocument { FileName = "Anleitung.docx", SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        var p = new Paragraph();
        p.Segments.Add(new Segment { Source = "Schalten Sie das Gerät aus.", Target = "Switch the device off.", Status = SegmentStatus.Translated });
        p.Segments.Add(new Segment { Source = "Ziehen Sie die Schraube mit ⟨1⟩5 Nm⟨/1⟩ an.", Target = "Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", Status = SegmentStatus.Translated });
        p.Segments.Add(new Segment { Source = "Warten Sie.", Target = "Wait.", Status = SegmentStatus.Translated });
        p.Segments.Add(new Segment { Source = "12", Target = "12", Locked = true });
        doc.Paragraphs.Add(p);
        doc.Renumber();
        return doc;
    }

    /// <summary>Was ein Prüfer in Word tut: Zelle mit Änderungsverfolgung korrigieren, Anmerkung, Kommentar.</summary>
    private static MemoryStream Review(byte[] exported)
    {
        var ms = new MemoryStream();
        ms.Write(exported);
        using (var word = WordprocessingDocument.Open(ms, true))
        {
            var main = word.MainDocumentPart!;
            var rows = main.Document.Body!.Elements<W.Table>().First().Elements<W.TableRow>().ToList();
            // Segment 2: „50 Nm“ → „5 Nm“ mit Änderungsverfolgung (gelöscht/eingefügt)
            var cell = rows[2].Elements<W.TableCell>().ElementAt(2);
            var run = cell.Descendants<W.Run>().First(r => r.InnerText == "50 Nm");
            run.InsertBeforeSelf(new W.DeletedRun(new W.Run(new W.DeletedText("50 Nm"))) { Author = "Prüfer", Id = "1" });
            run.InsertBeforeSelf(new W.InsertedRun(new W.Run(new W.Text("5 Nm"))) { Author = "Prüfer", Id = "2" });
            run.Remove();
            // Segment 1: Anmerkung in der Spalte
            rows[1].Elements<W.TableCell>().ElementAt(4).Elements<W.Paragraph>().First().Append(new W.Run(new W.Text("Kundenterminologie: „power off“?")));
            // Segment 3: Word-Kommentar und eine Änderung, bei der ein Tag verloren geht
            var comments = main.AddNewPart<WordprocessingCommentsPart>();
            comments.Comments = new W.Comments(new W.Comment(new W.Paragraph(new W.Run(new W.Text("Bitte höflicher.")))) { Id = "7", Author = "Prüfer" });
            var target3 = rows[3].Elements<W.TableCell>().ElementAt(2).Elements<W.Paragraph>().First();
            target3.RemoveAllChildren<W.Run>();
            target3.Append(new W.CommentRangeStart { Id = "7" }, new W.Run(new W.Text("Please wait.")), new W.CommentRangeEnd { Id = "7" },
                new W.Run(new W.CommentReference { Id = "7" }));
            main.Document.Save();
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void AReviewerCorrectsInWordAndTheChangesComeBack()
    {
        var doc = Doc();
        var exported = new MemoryStream();
        ReviewDocx.Export(doc, exported);
        exported.Position = 0;
        using (var check = WordprocessingDocument.Open(exported, false))
        {
            var rows = check.MainDocumentPart!.Document.Body!.Elements<W.Table>().First().Elements<W.TableRow>().ToList();
            Assert.Equal(4, rows.Count);                                                  // Kopf + 3 Segmente, gesperrtes fehlt
            Assert.Contains("⟨1⟩", rows[2].InnerText);
        }
        exported.Position = 0;
        Assert.Equal(doc.Id, ReviewDocx.DocumentIdOf(exported));

        using var project = TranslationProject.Create(_dir.File("p"), "Prüfung", "de-DE", ["en-GB"]);
        var reviewed = Review(exported.ToArray());
        var result = ReviewDocx.Import(reviewed, doc, project);

        var s = doc.Segments.ToList();
        Assert.Equal(2, result.Changed);
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", s[1].Target);                    // Änderungsverfolgung angenommen
        Assert.Equal(SegmentStatus.Reviewed, s[1].Status);
        Assert.Equal("Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", s[1].History[^2].Target);        // vorige Fassung bleibt im Verlauf
        Assert.Equal("Tighten the screw to ⟨1⟩50 Nm⟨/1⟩.", s[1].ReviewBaseline);              // und in der Änderungsverfolgung
        Assert.Contains(project.ProjectTm.All(), u => u.Target == "Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.");
        Assert.Equal("Kundenterminologie: „power off“?", s[0].Comment);
        Assert.Equal(SegmentStatus.Translated, s[0].Status);                                 // nicht geändert
        Assert.Equal("Please wait.", s[2].Target);
        Assert.Equal("Bitte höflicher.", s[2].Comment);                                      // Word-Kommentar
        Assert.Empty(result.TagWarnings);
        Assert.Empty(result.Problems);

        // falsche Datei: Zuordnung nur über Nummern, mit Hinweis; verlorene Tags werden gemeldet
        var other = Doc();
        var again = Review(exported.ToArray());
        var r2 = ReviewDocx.Import(again, other, markUnchangedReviewed: true);
        Assert.NotEmpty(r2.Problems);
        Assert.Equal(SegmentStatus.Reviewed, other.Segments.First().Status);                // unverändert → lektoriert
    }

    [Fact]
    public void LostTagsAreReported()
    {
        var doc = Doc();
        var exported = new MemoryStream();
        ReviewDocx.Export(doc, exported, "en");
        using (var word = WordprocessingDocument.Open(exported, true))
        {
            var cell = word.MainDocumentPart!.Document.Body!.Elements<W.Table>().First().Elements<W.TableRow>().ElementAt(2).Elements<W.TableCell>().ElementAt(2);
            var p = cell.Elements<W.Paragraph>().First();
            p.RemoveAllChildren<W.Run>();
            p.Append(new W.Run(new W.Text("Tighten the screw to 5 Nm.")));
            Assert.Contains("Translation", word.MainDocumentPart.Document.Body.InnerText);  // englische Überschriften
        }
        exported.Position = 0;
        var result = ReviewDocx.Import(exported, doc);
        Assert.Equal([2], result.TagWarnings);
    }
}
