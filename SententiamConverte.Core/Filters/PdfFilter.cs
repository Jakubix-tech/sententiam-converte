using System.Text.Json;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// PDF mit Textebene. Beim Lesen bleiben Absätze, Aufzählungspunkte, Überschriften und fett/kursiv gesetzte Wörter
/// erhalten (als Tags wie bei Word-Dateien); Lage, Schriftgröße und Farbe jedes Absatzes werden vermerkt.
/// Beim Zurückschreiben entsteht wieder ein PDF im ursprünglichen Layout: Grafiken, Linien, Farbflächen und Bilder
/// bleiben unverändert, nur der Text wird an derselben Stelle durch die Übersetzung ersetzt (siehe <see cref="PdfRewriter"/>).
/// Gescannte PDFs ohne Textebene brauchen vorher eine Texterkennung.
/// </summary>
public sealed partial class PdfFilter : IFileFilter
{
    public string Id => "pdf";
    public string Name => "PDF";
    public IReadOnlyList<string> Extensions { get; } = [".pdf"];

    [GeneratedRegex(@"(\p{Ll})-\s*\n\s*(\p{Ll})")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex LineBreak();

    private static readonly HashSet<string> Bullets = ["•", "◦", "▪", "▫", "‣", "∙", "·", "-", "–", "—", "*", "■", "□", "►", "✓", "✔"];

    /// <summary>Nach einem Ergänzungsstrich am Zeilenende („Software-“ / „und …“) bleibt der Strich stehen.</summary>
    private static readonly HashSet<string> Conjunctions =
        ["und", "oder", "bzw.", "sowie", "bis", "als", "wie", "noch", "and", "or", "to", "et", "ou", "i", "lub", "oraz", "y", "o", "e"];

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var pdf = PdfDocument.Open(buffer);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        var anyText = false;
        var pages = new List<(Page Page, List<(PdfUnit Unit, List<TextLine> Lines)> Units)>();

        foreach (var page in pdf.GetPages())
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).Where(w => w.Text.Trim().Length > 0).ToList();
            if (words.Count == 0) continue;
            anyText = true;
            var units = new List<(PdfUnit Unit, List<TextLine> Lines)>();
            var b = 0;
            foreach (var block in DocstrumBoundingBoxes.Instance.GetBlocks(words))
            {
                b++;
                var u = 0;
                foreach (var lines in SplitIntoUnits(block))
                {
                    u++;
                    units.Add((PdfUnit.From(page, b, u, lines), lines));
                }
            }
            SetLimits(page, units.Select(x => x.Unit).ToList());
            pages.Add((page, units));
        }
        if (!anyText)
            throw new InvalidDataException("Das PDF enthält keinen Text (vermutlich gescannt). Bitte vorher eine Texterkennung (OCR) durchführen.");

        // Einzeilige Absätze kennen ihren Zeilenabstand nicht – den von mehrzeiligen gleicher Schriftgröße nehmen
        var spacing = pages.SelectMany(p => p.Units).Select(x => x.Unit).Where(u => u.Lines > 1)
            .GroupBy(u => u.Size).ToDictionary(g => g.Key, g => g.Select(u => u.LineHeight).Order().ElementAt(g.Count() / 2));
        foreach (var (page, units) in pages)
        {
            foreach (var (unit, lines) in units)
            {
                if (unit.Lines == 1 && spacing.TryGetValue(unit.Size, out var lh)) unit.LineHeight = lh;
                var para = new Paragraph { Locator = $"{page.Number}:{unit.Block}:{unit.Index}", Context = $"Seite {page.Number}" };
                var text = TaggedText(lines, unit, para.TagData);
                para.TagData[0] = "pdf:" + JsonSerializer.Serialize(unit);
                segmenter.Segment(para, text);
                if (para.Segments.Count > 0) doc.Paragraphs.Add(para);
            }
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output) => PdfRewriter.Write(document, original, output);

    // ---------- Absätze im Block erkennen ----------

    /// <summary>
    /// Ein Textblock der Layoutanalyse kann mehrere Absätze enthalten (Überschrift, Zeile darunter, Aufzählung).
    /// Neuer Absatz bei: Aufzählungszeichen, Wechsel von Schriftgröße oder -stärke, kurzer vorheriger Zeile und
    /// Zeilen, die mit einem fetten Stichwort beginnen („Material &amp; Einkauf: …“).
    /// </summary>
    private static List<List<TextLine>> SplitIntoUnits(TextBlock block)
    {
        var units = new List<List<TextLine>>();
        var lines = block.TextLines.Where(l => l.Words.Count > 0).ToList();
        var blockWidth = Math.Max(1, block.BoundingBox.Width);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var startNew = i == 0;
            if (!startNew)
            {
                var prev = lines[i - 1];
                var bullet = IsBullet(line.Words[0].Text) && line.Words.Count > 1;
                var styleChange = StyleKey(prev) != StyleKey(line);
                var shortPrev = prev.BoundingBox.Right < block.BoundingBox.Left + blockWidth * 0.72 && !prev.Text.TrimEnd().EndsWith('-');
                var label = IsBold(line.Words[0]) && !IsBold(prev.Words[^1]) && !StyleKey(line).Contains("|True|");
                startNew = bullet || styleChange || shortPrev || label;
            }
            if (startNew) units.Add([]);
            units[^1].Add(line);
        }
        return units;
    }

    internal static bool IsBullet(string word) => Bullets.Contains(word.Trim());

    private static string StyleKey(TextLine line)
    {
        var letters = line.Words.SelectMany(w => w.Letters).Where(l => !char.IsWhiteSpace(l.Value, 0)).ToList();
        if (letters.Count == 0) return "";
        var size = Math.Round(letters.GroupBy(l => Math.Round(l.PointSize * 2) / 2).MaxBy(g => g.Count())!.Key, 1);
        var bold = letters.Count(IsBold) * 2 > letters.Count;
        var italic = letters.Count(IsItalic) * 2 > letters.Count;
        return $"{size}|{bold}|{italic}";
    }

    private static bool IsBold(Word w)
    {
        var letters = w.Letters.Where(l => !char.IsWhiteSpace(l.Value, 0)).ToList();
        return letters.Count > 0 && letters.Count(IsBold) * 2 > letters.Count;
    }

    internal static bool IsBold(Letter l) =>
        l.FontDetails?.IsBold == true || Regex.IsMatch(l.FontName ?? "", "Bold|Black|Heavy|Semibold|SemiBold|Demi", RegexOptions.IgnoreCase);

    internal static bool IsItalic(Letter l) =>
        l.FontDetails?.IsItalic == true || Regex.IsMatch(l.FontName ?? "", "Italic|Oblique", RegexOptions.IgnoreCase);

    /// <summary>
    /// Wie weit ein Absatz seitlich wachsen darf. Stehen zwei Absätze nebeneinander (Überschrift links, Datum rechts),
    /// teilen sie sich den Zwischenraum, damit sie bei längerer Übersetzung nicht ineinanderlaufen.
    /// </summary>
    private static void SetLimits(Page page, List<PdfUnit> units)
    {
        var margin = units.Count == 0 ? 36 : Math.Max(18, units.Min(u => u.Left));
        static bool SameRow(PdfUnit a, PdfUnit b) => a.Bottom < b.Top && a.Top > b.Bottom;
        foreach (var u in units)
        {
            var right = units.Where(v => v != u && v.Left > u.Right && SameRow(u, v))
                .Select(v => u.Right + (v.Left - u.Right) * 0.7 - 4).DefaultIfEmpty(page.Width - margin).Min();
            u.RightLimit = Math.Max(u.Right, right);
            var left = units.Where(v => v != u && v.Right < u.Left && SameRow(u, v))
                .Select(v => v.Right + (u.Left - v.Right) * 0.7 + 4).DefaultIfEmpty(margin).Max();
            u.LeftLimit = Math.Min(u.Left, left);
        }
    }

    /// <summary>
    /// Text des Absatzes; fett/kursiv abweichende Wörter als Tags (wie bei Word-Dateien). Silbentrennung am Zeilenende
    /// wird aufgelöst, weite Abstände innerhalb einer Zeile (z. B. „Berlin  |  Telefon“) bleiben als mehrere Leerzeichen.
    /// </summary>
    private static string TaggedText(List<TextLine> lines, PdfUnit unit, Dictionary<int, string> tagData)
    {
        var parts = new List<(string Text, string Format, int Spaces)>();
        var space = unit.Size * 0.26;
        for (var li = 0; li < lines.Count; li++)
        {
            var words = lines[li].Words;
            if (li == 0 && unit.Bullet != null) words = words.Skip(1).ToList();
            var gaps = words.Skip(1).Select((w, k) => w.BoundingBox.Left - words[k].BoundingBox.Right).Order().ToList();
            var wide = Math.Max(space * 1.6, gaps.Count == 0 ? 0 : gaps[gaps.Count / 4] * 1.5);
            for (var wi = 0; wi < words.Count; wi++)
            {
                var w = words[wi];
                var letters = w.Letters.Where(l => !char.IsWhiteSpace(l.Value, 0)).ToList();
                var bold = letters.Count(IsBold) * 2 > letters.Count && !unit.Bold;
                var italic = letters.Count(IsItalic) * 2 > letters.Count && !unit.Italic;
                var format = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "");
                var text = w.Text;
                var spaces = 1;
                if (wi > 0)
                {
                    var gap = w.BoundingBox.Left - words[wi - 1].BoundingBox.Right;
                    if (gap > wide) spaces = (int)Math.Clamp(Math.Round(gap / space), 2, 8);
                }
                else if (parts.Count > 0 && parts[^1].Text.EndsWith('-') && parts[^1].Text.Length > 2 && char.IsLetter(parts[^1].Text[^2]))
                {
                    // Trennstrich am Zeilenende: „Konstruk-“ + „tion“ → „Konstruktion“, „ERP-“ + „System“ → „ERP-System“,
                    // „Software-“ + „und“ bleibt getrennt (Ergänzungsstrich)
                    var prev = parts[^1];
                    if (!Conjunctions.Contains(text.ToLowerInvariant()) && prev.Format == format && text.Length > 0)
                    {
                        var joined = char.IsLower(text[0]) && char.IsLower(prev.Text[^2]) ? prev.Text[..^1] + text : prev.Text + text;
                        parts[^1] = (joined, format, prev.Spaces);
                        continue;
                    }
                }
                parts.Add((text, format, spaces));
            }
        }
        var sb = new System.Text.StringBuilder();
        var nextId = 1;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(' ', parts[i].Spaces);
            if (parts[i].Format.Length == 0)
            {
                sb.Append(parts[i].Text);
                continue;
            }
            // gleich formatierte Nachbarwörter zu einem Tag-Paar zusammenfassen
            var j = i;
            while (j + 1 < parts.Count && parts[j + 1].Format == parts[i].Format) j++;
            var id = nextId++;
            tagData[id] = "f:<w:rPr>" + parts[i].Format + "</w:rPr>";
            sb.Append(TagText.Start(id));
            for (var k = i; k <= j; k++)
            {
                if (k > i) sb.Append(' ', parts[k].Spaces);
                sb.Append(parts[k].Text);
            }
            sb.Append(TagText.End(id));
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>Zeilenumbrüche im Block zu Leerzeichen, Silbentrennung am Zeilenende auflösen.</summary>
    internal static string CleanBlock(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HyphenBreak().Replace(text, "$1$2");
        return LineBreak().Replace(text, " ").Trim();
    }

    /// <summary>Erkannter Text (eine Zeile je Absatz, Seitenmarken „=== Seite n ===“) als Word-Datei zum Übersetzen.</summary>
    public static void OcrTextToDocx(string text, string language, Stream output)
    {
        var paragraphs = new List<(string, bool)>();
        var pageBreak = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("=== Seite"))
            {
                pageBreak = paragraphs.Count > 0;
                continue;
            }
            if (line.Length == 0) continue;
            paragraphs.Add((line, pageBreak));
            pageBreak = false;
        }
        DocxFilter.CreateSimple(output, paragraphs, language);
    }
}

/// <summary>Lage und Gestaltung eines PDF-Absatzes (in den Tag-Daten des Absatzes abgelegt, Schlüssel 0, „pdf:“).</summary>
public sealed class PdfUnit
{
    public int Page { get; set; }
    public int Block { get; set; }
    public int Index { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }
    public double Top { get; set; }
    public double Bottom { get; set; }

    /// <summary>Wo der Text beginnt (nach einem Aufzählungszeichen).</summary>
    public double TextX { get; set; }

    /// <summary>Grundlinie der ersten Zeile.</summary>
    public double Baseline { get; set; }
    public double LineHeight { get; set; }
    public double Size { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Serif { get; set; }
    public double[] Color { get; set; } = [0, 0, 0];
    public string? Bullet { get; set; }
    public double[]? BulletColor { get; set; }

    /// <summary>L, R oder C.</summary>
    public string Align { get; set; } = "L";
    public int Lines { get; set; }
    public double RightLimit { get; set; }
    public double LeftLimit { get; set; }

    /// <summary>Unsichtbarer Text über einem Bild (Texterkennung eines Scans): Übersetzung auf weißem Grund setzen.</summary>
    public bool Cover { get; set; }

    internal static PdfUnit From(Page page, int block, int index, List<TextLine> lines)
    {
        var letters = lines.SelectMany(l => l.Words).SelectMany(w => w.Letters).Where(l => !char.IsWhiteSpace(l.Value, 0)).ToList();
        var size = letters.GroupBy(l => Math.Round(l.PointSize * 2) / 2).MaxBy(g => g.Count())!.Key;
        var bold = letters.Count(PdfFilter.IsBold) * 10 >= letters.Count * 9;
        var italic = letters.Count(PdfFilter.IsItalic) * 10 >= letters.Count * 9;
        var baselines = lines.Select(l => l.Words.SelectMany(w => w.Letters).Select(x => x.StartBaseLine.Y).DefaultIfEmpty(l.BoundingBox.Bottom).Average()).ToList();
        var lineHeight = baselines.Count > 1 ? Math.Abs(baselines[0] - baselines[^1]) / (baselines.Count - 1) : size * 1.25;
        var fontName = letters.GroupBy(l => l.FontName ?? "").MaxBy(g => g.Count())!.Key;
        var firstWord = lines[0].Words[0];
        var bullet = PdfFilter.IsBullet(firstWord.Text) && lines[0].Words.Count > 1 ? firstWord.Text.Trim() : null;
        var textWord = bullet != null ? lines[0].Words[1] : firstWord;
        var first = textWord.Letters.FirstOrDefault(l => !char.IsWhiteSpace(l.Value, 0)) ?? letters[0];
        var rgb = first.Color?.ToRGBValues() ?? (0, 0, 0);
        var unit = new PdfUnit
        {
            Page = page.Number, Block = block, Index = index,
            Left = lines.Min(l => l.BoundingBox.Left), Right = lines.Max(l => l.BoundingBox.Right),
            Top = lines.Max(l => l.BoundingBox.Top), Bottom = lines.Min(l => l.BoundingBox.Bottom),
            TextX = textWord.BoundingBox.Left,
            Baseline = baselines[0], LineHeight = Math.Clamp(lineHeight, size * 0.9, size * 2.5), Size = size,
            Bold = bold, Italic = italic, Serif = IsSerif(fontName), Color = [rgb.r, rgb.g, rgb.b], Bullet = bullet, Lines = lines.Count,
            Cover = letters.Count(l => l.RenderingMode == TextRenderingMode.Neither) * 2 > letters.Count,
        };
        if (bullet != null && firstWord.Letters.FirstOrDefault()?.Color?.ToRGBValues() is { } bc && bc != rgb)
            unit.BulletColor = [bc.r, bc.g, bc.b];
        // Ausrichtung: rechtsbündig (z. B. Datumsangaben rechts), zentriert (Titel) oder links
        var center = (unit.Left + unit.Right) / 2;
        if (lines.Count == 1 && unit.Left > page.Width * 0.55 && unit.Right > page.Width * 0.7) unit.Align = "R";
        else if (lines.Count > 1 && lines.Select(l => l.BoundingBox.Right).Max() - lines.Select(l => l.BoundingBox.Right).Min() < 2
                 && lines.Select(l => l.BoundingBox.Left).Max() - lines.Select(l => l.BoundingBox.Left).Min() > 10) unit.Align = "R";
        else if (Math.Abs(center - page.Width / 2) < page.Width * 0.03 && unit.Right - unit.Left < page.Width * 0.7 && unit.Left > page.Width * 0.12) unit.Align = "C";
        return unit;
    }

    internal static bool IsSerif(string fontName)
    {
        var n = fontName.Contains('+') ? fontName[(fontName.IndexOf('+') + 1)..] : fontName;
        if (Regex.IsMatch(n, "Sans|Arial|Helvetica|Calibri|Verdana|Segoe|Tahoma|Roboto|Inter|Open|Lato|Futura|Gill|Frutiger|Myriad", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(n, "Serif|Times|Georgia|Garamond|Cambria|Book|Palatino|Minion|Caslon|Baskerville|Charter|Constantia|Century|Bodoni|Didot", RegexOptions.IgnoreCase);
    }
}
