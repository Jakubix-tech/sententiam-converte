using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Writer;

namespace SententiamConverte.Core.Tests;

/// <summary>PDF wird als PDF im Layout des Originals zurückgeschrieben: Grafiken bleiben, Text steht an seiner Stelle.</summary>
public class PdfExportTests
{
    /// <summary>
    /// Wie „Microsoft: Print To PDF“: am Anfang eine gespiegelte Umrechnung ohne q/Q, der Text mit Gegen-Spiegelung im Tm.
    /// Von Hand geschrieben, weil der PDF-Baukasten solche Seiten nicht erzeugt.
    /// </summary>
    private static byte[] FlippedSample()
    {
        const string content = "0.75 0 0 -0.75 0 841.92 cm\nq\n0 0 1 rg\n10 10 100 20 re f\nQ\nBT\n/F1 16 Tf\n1 0 0 -1 60 100 Tm\n(Guten Tag) Tj\nET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.32 841.92] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void TextStaysUprightWhenThePageStartsWithAFlippedTransform()
    {
        var pdf = FlippedSample();
        var doc = new PdfFilter().Read(new MemoryStream(pdf), "flip.pdf", "de-DE", "en-GB", new Segmenter("de-DE"));
        var seg = doc.Paragraphs.SelectMany(p => p.Segments).Single(s => s.Source.Contains("Guten Tag"));
        seg.Target = "Good day";
        using var output = new MemoryStream();
        PdfRewriter.Write(doc, new MemoryStream(pdf), output);

        using var result = PdfDocument.Open(output.ToArray());
        var page = result.GetPage(1);
        var letters = page.Letters.Where(l => "Good day".Contains(l.Value) && l.Value != " ").ToList();
        Assert.Equal("Goodday", string.Concat(letters.Select(l => l.Value)));
        Assert.All(letters, l => Assert.Equal(TextOrientation.Horizontal, l.TextOrientation));       // nicht auf dem Kopf
        Assert.InRange(letters[0].StartBaseLine.Y, 750, 780);                                         // oben, wie das Original (842 − 0,75·100)
        Assert.DoesNotContain(page.Letters, l => l.Value == "T");                                      // „Guten Tag“ entfernt
        Assert.NotEmpty(page.ExperimentalAccess.Paths);                                                // blaue Fläche bleibt
    }

    [Fact]
    public void CopiedContentIsIsolatedAndBalanced()
    {
        IGraphicsStateOperation cm = new UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState.ModifyCurrentTransformationMatrix([0.75, 0, 0, -0.75, 0, 842]);
        string Kinds(IEnumerable<IGraphicsStateOperation> ops) => string.Join(" ", ops.Select(o => o switch
        {
            UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState.Push => "q",
            UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState.Pop => "Q",
            _ => "cm",
        }));
        var push = UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState.Push.Value;
        var pop = UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState.Pop.Value;
        Assert.Equal("q cm q Q Q", Kinds(PdfRewriter.Isolated([cm, push, pop])));
        Assert.Equal("q q cm Q Q", Kinds(PdfRewriter.Isolated([push, cm])));                          // offen gebliebenes q
        Assert.Equal("q cm Q", Kinds(PdfRewriter.Isolated([pop, cm])));                               // überzähliges Q
    }

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

    /// <summary>
    /// Die Ersatzschrift (Noto) läuft breiter als z. B. Helvetica/Arial: Ohne Abgleich wirkte die Übersetzung bei gleicher
    /// Punktgröße größer und bräche anders um. Unveränderter Text muss etwa so breit stehen wie im Original.
    /// </summary>
    [Fact]
    public void TheReplacementFontLooksAsLargeAsTheOriginal()
    {
        var b = new PdfDocumentBuilder();
        var pb = b.AddPage(595, 842);
        var helvetica = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        string[] lines =
        [
            "Softwareentwicklung und Architektur mit C# und .NET",
            "Datenbanken, Schnittstellen und Automatisierung",
            "Konstruktion und Produktionsplanung im Betrieb",
            "Projektarbeit mit Verantwortung und Terminen",
            "Webanwendungen mit JavaScript und TypeScript",
        ];
        for (var i = 0; i < lines.Length; i++) pb.AddText("• " + lines[i], 10, new PdfPoint(50, 760 - i * 40), helvetica);   // Aufzählung: je ein Absatz
        var original = b.Build();
        var doc = Read(original);
        foreach (var s in doc.Segments) s.Target = s.Source;                          // Text bleibt, nur die Schrift wechselt
        var page = Export(doc, original, out var result);
        using var _ = result;
        using var source = PdfDocument.Open(original);

        static double Width(Page p, string line)
        {
            var key = line.Replace(" ", "");
            var letters = p.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).GroupBy(l => Math.Round(l.StartBaseLine.Y))
                .Select(g => g.OrderBy(l => l.StartBaseLine.X).SkipWhile(l => l.Value == "•").ToList())             // ohne Aufzählungszeichen
                .First(g => string.Concat(g.Select(l => l.Value)).StartsWith(key));
            return letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
        }
        foreach (var line in lines)
        {
            var ratio = Width(page, line) / Width(source.GetPage(1), line);
            Assert.True(ratio is >= 0.97 and <= 1.03, $"{line}: {Width(page, line):0.0} statt {Width(source.GetPage(1), line):0.0} | "
                + string.Join(" / ", page.Letters.GroupBy(l => Math.Round(l.StartBaseLine.Y)).Select(g => $"{g.Key}:{g.First().PointSize:0.0}:{string.Concat(g.Select(l => l.Value))}")));
        }
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
