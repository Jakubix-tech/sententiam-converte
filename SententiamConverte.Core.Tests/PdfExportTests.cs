using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Writer;

namespace SententiamConverte.Core.Tests;

/// <summary>PDF wird als PDF im Layout des Originals zurückgeschrieben: Grafiken bleiben, Text steht an seiner Stelle.</summary>
public class PdfExportTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    /// <summary>A4: farbiges Kopfband mit weißem Titel, Überschrift mit Datum rechts, Fließtext mit fettem Wort, Linie, Aufzählung, Bild, Fußzeile.</summary>
    private static byte[] Sample()
    {
        using var b = new PdfDocumentBuilder();
        var regular = b.AddTrueTypeFont(Font("NotoSans-Regular"));
        var bold = b.AddTrueTypeFont(Font("NotoSans-Bold"));
        var page = b.AddPage(PageSize.A4);

        page.SetTextAndFillColor(31, 92, 153);
        page.DrawRectangle(new PdfPoint(0, 780), 595, 62, 0, true);
        page.SetTextAndFillColor(255, 255, 255);
        page.AddText("Bericht", 20, new PdfPoint(40, 800), bold);
        page.ResetColor();

        page.AddText("Übersicht", 14, new PdfPoint(40, 740), bold);
        page.AddText("01/2024 – 12/2025", 9, new PdfPoint(470, 740), regular);

        double Run(double x, double y, string text, PdfDocumentBuilder.AddedFont font) =>
            page.AddText(text, 10, new PdfPoint(x, y), font).Max(l => l.EndBaseLine.X);
        Run(40, 715, "Dieses Dokument beschreibt die Ergebnisse des Projekts im vergangenen Jahr und", regular);
        var x = Run(40, 702, "nennt die nächsten ", regular);
        x = Run(x, 702, "wichtigen", bold);
        Run(x, 702, " Schritte für das Team, die Kunden und die Partner im neuen", regular);
        Run(40, 689, "Jahr.", regular);

        page.DrawLine(new PdfPoint(40, 670), new PdfPoint(555, 670), 1);

        page.AddText("•", 10, new PdfPoint(40, 650), regular);
        page.AddText("Erster Punkt der Liste.", 10, new PdfPoint(52, 650), regular);
        page.AddText("•", 10, new PdfPoint(40, 634), regular);
        page.AddText("Zweiter Punkt der Liste.", 10, new PdfPoint(52, 634), regular);

        page.AddPng(new MemoryStream(Png), new PdfRectangle(40, 520, 100, 580));
        page.AddText("Seite 1", 8, new PdfPoint(520, 30), regular);
        return b.Build();
    }

    private static byte[] Font(string name)
    {
        using var stream = typeof(PdfFilter).Assembly.GetManifestResourceStream($"SententiamConverte.Core.Fonts.{name}.ttf")!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static readonly (string De, string En)[] Glossary =
    [
        ("Bericht", "Report"), ("Übersicht", "Overview"), ("Dieses Dokument beschreibt", "This document describes"),
        ("wichtigen", "important"), ("Erster Punkt der Liste.", "First item of the list."),
        ("Zweiter Punkt der Liste.", "Second item of the list."), ("Seite", "Page"),
    ];

    private static BilingualDocument Read(byte[] pdf) =>
        new PdfFilter().Read(new MemoryStream(pdf), "bericht.pdf", "de-DE", "en-GB", new Segmenter("de"));

    private static void Translate(BilingualDocument doc, Func<string, string>? extra = null)
    {
        foreach (var s in doc.Paragraphs.SelectMany(p => p.Segments))
        {
            var t = Glossary.Aggregate(s.Source, (a, g) => a.Replace(g.De, g.En));
            s.Target = extra?.Invoke(t) ?? t;
        }
    }

    private static Page Export(BilingualDocument doc, byte[] original, out PdfDocument result)
    {
        var output = new MemoryStream();
        new PdfFilter().Write(doc, new MemoryStream(original), output);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(output.ToArray(), 0, 4));
        result = PdfDocument.Open(output.ToArray());
        Assert.Equal(1, result.NumberOfPages);                          // keine Hilfsseite im Ergebnis
        return result.GetPage(1);
    }

    private static List<Word> WordsOf(Page page) => NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();

    private static string TextOf(Page page) => string.Join(" ", WordsOf(page).Select(w => w.Text));

    private static Word WordAt(Page page, string text) => WordsOf(page).First(w => w.Text == text);

    [Fact]
    public void ReadingKeepsParagraphsBulletsAndBoldWords()
    {
        var doc = Read(Sample());
        var sources = doc.Paragraphs.Select(p => p.JoinSource()).ToList();
        Assert.Contains("Übersicht", sources);
        Assert.Contains("01/2024 – 12/2025", sources);
        Assert.Contains("Erster Punkt der Liste.", sources);           // Aufzählungszeichen gehört nicht zum Text
        var prose = Assert.Single(sources, s => s.StartsWith("Dieses Dokument"));
        Assert.EndsWith("im neuen Jahr.", prose);                       // drei Zeilen, ein Absatz
        Assert.Contains(TagText.Start(1) + "wichtigen" + TagText.End(1), prose);
    }

    [Fact]
    public void ExportKeepsLayoutGraphicsAndFormatting()
    {
        var original = Sample();
        var doc = Read(original);
        Translate(doc);
        var page = Export(doc, original, out var result);
        using var _ = result;

        Assert.Equal(595, page.Width, 0);
        Assert.Equal(842, page.Height, 0);
        var text = TextOf(page);
        foreach (var expected in new[] { "Report", "Overview", "This document describes", "important", "First item of the list.", "Page 1", "01/2024 – 12/2025" })
            Assert.Contains(expected, text);
        foreach (var gone in new[] { "Bericht", "Übersicht", "Erster", "Seite" })
            Assert.DoesNotContain(gone, text);

        // Grafiken unverändert: Farbband, Linie, Bild
        Assert.Contains(page.Paths, p => p.IsFilled && p.GetBoundingRectangle() is { } r && r.Width > 590 && r.Bottom > 779);
        Assert.Contains(page.Paths, p => p.IsStroked && p.GetBoundingRectangle() is { } r && Math.Abs(r.Bottom - 670) < 1 && r.Width > 500);
        Assert.Single(page.GetImages());

        // Formatierung: Farbe, Stärke, Größe, Lage, rechtsbündiges Datum, Aufzählungszeichen
        var report = WordAt(page, "Report");
        Assert.Equal((1d, 1d, 1d), report.Letters[0].Color.ToRGBValues());
        Assert.Contains("Bold", report.Letters[0].FontName);
        Assert.Equal(20, report.Letters[0].PointSize, 1);
        Assert.Equal(800, report.Letters[0].StartBaseLine.Y, 0.5);
        Assert.Contains("Bold", WordAt(page, "important").Letters[0].FontName);
        Assert.DoesNotContain("Bold", WordAt(page, "describes").Letters[0].FontName);
        static double DateRight(Page p) => WordsOf(p).Where(w => w.BoundingBox.Bottom > 735 && w.BoundingBox.Left > 400).Max(w => w.BoundingBox.Right);
        using (var source = PdfDocument.Open(original))
            Assert.Equal(DateRight(source.GetPage(1)), DateRight(page), 1.0);
        Assert.Contains(page.Letters, l => l.Value == "•" && Math.Abs(l.StartBaseLine.X - 40) < 1 && Math.Abs(l.StartBaseLine.Y - 650) < 1);
        Assert.Equal(52, WordAt(page, "First").Letters[0].StartBaseLine.X, 0.5);
    }

    [Fact]
    public void LongerTranslationPushesTextBelowButNotPastGraphics()
    {
        var original = Sample();
        var doc = Read(original);
        Translate(doc, t => t == "First item of the list."
            ? "First item of the list, which is now considerably longer in the translation and therefore needs a second line on the page."
            : t);
        var page = Export(doc, original, out var result);
        using var _ = result;

        var second = WordAt(page, "Second").Letters[0];
        Assert.InRange(second.StartBaseLine.Y, 600, 625);               // eine Zeile tiefer, aber über dem Bild
        Assert.Equal(10, second.PointSize, 1);                          // nicht verkleinert
        Assert.All(WordsOf(page).Where(w => w.Text is "line."), w => Assert.True(w.BoundingBox.Bottom > 581));
    }

    [Fact]
    public void TextDoesNotRunAcrossALineBelow()
    {
        var original = Sample();
        var doc = Read(original);
        Translate(doc, t => t.StartsWith("This document")
            ? t + " It also explains in some detail how the results were measured, who was involved, what the budget looked like,"
                + " which risks were identified during the year and how the team intends to address them in the coming months."
            : t);
        var page = Export(doc, original, out var result);
        using var _ = result;

        // Die Linie bei 670 bleibt die Grenze: der Absatz wird kleiner gesetzt, die Aufzählung bleibt an ihrem Platz
        var prose = WordsOf(page).Where(w => w.BoundingBox.Bottom > 660 && w.BoundingBox.Top < 730 && w.BoundingBox.Left < 400).ToList();
        Assert.True(prose.Min(w => w.BoundingBox.Bottom) > 668);
        Assert.True(prose.Max(w => w.Letters[0].PointSize) < 10);
        Assert.Equal(650, WordAt(page, "First").Letters[0].StartBaseLine.Y, 0.5);
    }

    [Fact]
    public void ProjectsFromVersionOneAreExportedWithLayout()
    {
        // Version 1.0 kannte die Lage der Absätze nicht: die Übersetzungen werden über den Quelltext zugeordnet
        var original = Sample();
        var doc = Read(original);
        foreach (var p in doc.Paragraphs) p.TagData.Remove(0);
        Translate(doc);
        var page = Export(doc, original, out var result);
        using var _ = result;
        Assert.Contains("Overview", TextOf(page));
        Assert.Contains("First item of the list.", TextOf(page));
        Assert.Single(page.GetImages());
    }

    [Fact]
    public void UnknownCharactersDoNotStopTheExport()
    {
        var original = Sample();
        var doc = Read(original);
        Translate(doc, t => t == "Overview" ? "概要 Overview" : t);
        var page = Export(doc, original, out var result);
        using var _ = result;
        Assert.Contains("?? Overview", TextOf(page));
    }

    [Fact]
    public void ExportKeepsThePdfExtension() =>
        Assert.Equal("bericht_en-GB.pdf", ((IFileFilter)new PdfFilter()).TargetFileName("bericht.pdf", "en-GB"));
}
