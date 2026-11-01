using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.TextShowing;
using UglyToad.PdfPig.Writer;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Schreibt die Übersetzung in ein PDF mit dem Layout des Originals: Jede Seite wird übernommen, nur die
/// Textbefehle werden entfernt – Grafiken, Linien, Farbflächen, Bilder und Seitenformat bleiben unverändert.
/// Dann wird jeder Absatz an seiner ursprünglichen Stelle neu gesetzt: gleiche Schriftgröße, Stärke, Farbe und
/// Ausrichtung, fett/kursiv aus den Tags, Aufzählungszeichen mit hängendem Einzug. Braucht die Übersetzung mehr
/// Zeilen, rückt der Text darunter nach unten, soweit bis zur nächsten Linie/Grafik bzw. zum Seitenende Platz ist;
/// reicht das nicht, wird die Schrift etwas verkleinert.
/// Schriften: Noto Sans / Noto Serif (SIL Open Font License), es werden nur die benutzten Zeichen eingebettet.
/// </summary>
public static class PdfRewriter
{
    private static readonly FieldInfo? StreamsField = typeof(PdfPageBuilder).GetField("contentStreams", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? CurrentField = typeof(PdfPageBuilder).GetField("currentStream", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Type? DefaultStreamType = typeof(PdfPageBuilder).GetNestedType("DefaultContentStream", BindingFlags.NonPublic);

    /// <summary>Kleinste Schrift im Verhältnis zum Original, bevor Text über seinen Platz hinausläuft.</summary>
    private const double MinScale = 0.72;

    public static void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var buffer = FileFilters.Buffer(original);
        document = WithLayout(document, buffer);
        using var source = PdfDocument.Open(buffer);
        using var builder = new PdfDocumentBuilder();
        using var fonts = new Fonts(builder);

        var byPage = document.Paragraphs
            .Select(p => (Paragraph: p, Unit: UnitOf(p)))
            .Where(x => x.Unit != null)
            .GroupBy(x => x.Unit!.Page)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Paragraph, Unit: x.Unit!)).ToList());

        foreach (var page in source.GetPages())
        {
            var pb = builder.AddPage(source, page.Number);
            var items = byPage.GetValueOrDefault(page.Number) ?? [];
            var removed = RemoveText(pb, page);
            var flow = new PageFlow(page, items.Select(x => x.Unit).ToList());
            foreach (var (paragraph, unit) in items.OrderByDescending(x => x.Unit.Top).ThenBy(x => x.Unit.Left))
                Render(pb, fonts, paragraph, unit, flow, cover: unit.Cover || !removed);
        }
        output.Write(builder.Build());
    }

    /// <summary>
    /// Projekte aus Version 1.0 kennen die Lage der Absätze noch nicht: Original neu einlesen und die vorhandenen
    /// Übersetzungen über den Quelltext zuordnen.
    /// </summary>
    private static BilingualDocument WithLayout(BilingualDocument document, MemoryStream pdf)
    {
        if (document.Paragraphs.Any(p => UnitOf(p) != null)) return document;
        var fresh = new PdfFilter().Read(pdf, document.FileName, document.SourceLanguage, document.TargetLanguage, new Segmenter(document.SourceLanguage));
        pdf.Position = 0;
        static string Key(string s) => Regex.Replace(TagText.StripTags(s), @"\s+", " ").Trim();
        var targets = new Dictionary<string, string>();
        foreach (var s in document.Paragraphs.SelectMany(p => p.Segments))
            if (!string.IsNullOrEmpty(s.Target)) targets.TryAdd(Key(s.Source), s.Target);
        foreach (var s in fresh.Paragraphs.SelectMany(p => p.Segments))
            if (targets.TryGetValue(Key(s.Source), out var t)) s.Target = t;
        return fresh;
    }

    private static PdfUnit? UnitOf(Paragraph p) =>
        p.TagData.TryGetValue(0, out var d) && d.StartsWith("pdf:") ? JsonSerializer.Deserialize<PdfUnit>(d[4..]) : null;

    /// <summary>Den übernommenen Seiteninhalt durch dieselben Befehle ohne Textausgabe ersetzen.</summary>
    private static bool RemoveText(PdfPageBuilder pb, Page page)
    {
        if (StreamsField?.GetValue(pb) is not IList streams || DefaultStreamType == null || CurrentField == null) return false;
        try
        {
            var ops = page.Operations
                .Where(o => o is not (ShowText or ShowTextsWithPositioning or MoveToNextLineShowText or MoveToNextLineShowTextWithSpacing))
                .ToList();
            var stream = Activator.CreateInstance(DefaultStreamType, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null,
                [new List<IGraphicsStateOperation>(ops)], null);
            if (stream == null) return false;
            streams.Clear();
            streams.Add(stream);
            CurrentField.SetValue(pb, stream);
            return true;
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ---------- Absatz setzen ----------

    /// <summary>Ein Stück Text in einer Schrift; ein Wort besteht aus einem oder mehreren Stücken (z. B. fett + Doppelpunkt).</summary>
    private sealed record Piece(string Text, bool Bold, bool Italic);

    /// <summary>Ein Wort mit der Zahl der Leerzeichen davor (weite Abstände aus dem Original bleiben erhalten).</summary>
    private sealed record WordItem(List<Piece> Pieces, int Spaces);

    private sealed record WordBox(WordItem Word, double Width);

    private static void Render(PdfPageBuilder pb, Fonts fonts, Paragraph paragraph, PdfUnit u, PageFlow flow, bool cover)
    {
        var words = Words(paragraph.JoinTarget(), paragraph.TagData, u, fonts);
        if (words.Count == 0) return;
        var maxWidth = Math.Max(20, u.RightLimit - u.TextX) + 0.5;
        if (u.Align == "R") maxWidth = Math.Max(20, u.Right - u.LeftLimit) + 0.5;
        if (u.Align == "C") maxWidth = Math.Max(20, 2 * Math.Min(u.RightLimit - (u.Left + u.Right) / 2, (u.Left + u.Right) / 2 - u.LeftLimit));

        // Mehr Zeilen als im Original: zuerst Platz darunter nutzen bzw. den Text darunter verschieben,
        // erst wenn das nicht reicht, die Schrift schrittweise verkleinern
        var size = u.Size;
        var lines = Wrap(words, size, maxWidth, fonts, u);
        var room = flow.Room(u);
        while (Extra(lines.Count, size, u) > room && size > u.Size * MinScale)
        {
            size = Math.Max(u.Size * MinScale, size - 0.25);
            lines = Wrap(words, size, maxWidth, fonts, u);
        }
        flow.Grow(u, Extra(lines.Count, size, u));

        var step = u.LineHeight * size / u.Size;
        var baseline = u.Baseline - flow.Shift(u);
        var space = fonts.SpaceWidth(u.Serif, u.Bold, u.Italic, size);
        var widths = lines.Select(line => line.Sum(w => w.Width) + line.Skip(1).Sum(w => w.Word.Spaces) * space).ToList();
        double X(int i) => u.Align switch
        {
            "R" => u.Right - widths[i],
            "C" => (u.Left + u.Right) / 2 - widths[i] / 2,
            _ => u.TextX,
        };
        if (cover)
        {
            // Originaltext liegt noch im Bild bzw. konnte nicht entfernt werden: weiß abdecken
            var left = Math.Min(u.Left, Enumerable.Range(0, lines.Count).Min(X)) - 1;
            var right = Math.Max(u.Right, Enumerable.Range(0, lines.Count).Max(i => X(i) + widths[i])) + 1;
            var top = Math.Max(u.Top - flow.Shift(u), baseline + size * 0.9) + 1;
            var bottom = Math.Min(u.Bottom - flow.Shift(u), baseline - (lines.Count - 1) * step - size * 0.25) - 1;
            pb.SetStrokeColor(255, 255, 255);
            pb.SetTextAndFillColor(255, 255, 255);
            pb.DrawRectangle(new PdfPoint(left, bottom), right - left, top - bottom, 0, true);
            pb.ResetColor();
        }

        if (u.Bullet != null)
        {
            SetColor(pb, u.BulletColor ?? u.Color);
            pb.AddText(u.Bullet, size, new PdfPoint(u.Left, baseline), fonts.Get(u.Serif, u.Bold, u.Italic));
        }
        SetColor(pb, u.Color);
        for (var i = 0; i < lines.Count; i++)
        {
            var x = X(i);
            var y = baseline - i * step;
            for (var k = 0; k < lines[i].Count; k++)
            {
                var word = lines[i][k];
                if (k > 0) x += space * word.Word.Spaces;
                foreach (var piece in word.Word.Pieces)
                {
                    pb.AddText(piece.Text, size, new PdfPoint(x, y), fonts.Get(u.Serif, piece.Bold, piece.Italic));
                    x += fonts.Width(piece.Text, u.Serif, piece.Bold, piece.Italic, size);
                }
            }
        }
        pb.ResetColor();
    }

    /// <summary>Zusätzliche Höhe gegenüber dem Original (in Punkt).</summary>
    private static double Extra(int lines, double size, PdfUnit u) =>
        Math.Max(0, (lines - 1) * u.LineHeight * size / u.Size - (u.Lines - 1) * u.LineHeight);

    private static void SetColor(PdfPageBuilder pb, double[] c) =>
        pb.SetTextAndFillColor((byte)Math.Round(c[0] * 255), (byte)Math.Round(c[1] * 255), (byte)Math.Round(c[2] * 255));

    /// <summary>Text mit Tags in Wörter zerlegen; jedes Wort aus Stücken mit ihrer Formatierung (fett/kursiv aus den Tag-Daten).</summary>
    private static List<WordItem> Words(string tagged, Dictionary<int, string> tagData, PdfUnit u, Fonts fonts)
    {
        var words = new List<WordItem>();
        var pieces = new List<Piece>();
        var current = new System.Text.StringBuilder();
        var stack = new List<TextFormat>();
        var (bold, italic) = (u.Bold, u.Italic);
        var spaces = 0;
        void FlushPiece()
        {
            if (current.Length > 0) pieces.Add(new Piece(fonts.Printable(current.ToString(), u.Serif, bold, italic), bold, italic));
            current.Clear();
        }
        void FlushWord()
        {
            FlushPiece();
            if (pieces.Count > 0) words.Add(new WordItem(pieces, Math.Max(1, spaces)));
            pieces = new List<Piece>();
            spaces = 0;
        }
        foreach (var part in TagText.Split(tagged))
        {
            if (part.Tag is { } tag)
            {
                FlushPiece();                     // Formatwechsel mitten im Wort: neues Stück, gleiches Wort
                if (tag.Kind == TagText.TagKind.Start) stack.Add(TagFormat.Of(tagData, tag.Id));
                else if (tag.Kind == TagText.TagKind.End && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                var f = stack.Aggregate(TextFormat.None, (a, x) => a | x);
                bold = u.Bold || f.HasFlag(TextFormat.Bold);
                italic = u.Italic || f.HasFlag(TextFormat.Italic);
                continue;
            }
            foreach (var ch in part.Text ?? "")
            {
                if (char.IsWhiteSpace(ch) && ch is not (' ' or ' '))   // geschützte Leerzeichen bleiben im Wort
                {
                    if (current.Length > 0 || pieces.Count > 0) FlushWord();
                    spaces++;
                }
                else current.Append(ch);
            }
        }
        FlushWord();
        return words;
    }

    /// <summary>Wörter auf Zeilen verteilen (Umbruch an Leerzeichen).</summary>
    private static List<List<WordBox>> Wrap(List<WordItem> words, double size, double maxWidth, Fonts fonts, PdfUnit u)
    {
        var space = fonts.SpaceWidth(u.Serif, u.Bold, u.Italic, size);
        var lines = new List<List<WordBox>> { new() };
        double used = 0;
        foreach (var word in words)
        {
            var width = word.Pieces.Sum(p => fonts.Width(p.Text, u.Serif, p.Bold, p.Italic, size));
            var extra = lines[^1].Count == 0 ? width : space * word.Spaces + width;
            if (lines[^1].Count > 0 && used + extra > maxWidth)
            {
                lines.Add([]);
                used = 0;
                extra = width;
            }
            lines[^1].Add(new WordBox(word, width));
            used += extra;
        }
        return lines;
    }

    /// <summary>
    /// Verschiebt Absätze nach unten, wenn ein Absatz darüber mehr Zeilen braucht. Mitbewegt wird alles, was darunter
    /// in derselben Spalte steht – bis zur nächsten Linie, Fläche oder Grafik bzw. zur Fußzeile; diese bleiben fest.
    /// </summary>
    private sealed class PageFlow
    {
        private readonly List<PdfUnit> _units;
        private readonly List<PdfRectangle> _graphics = [];
        private readonly Dictionary<PdfUnit, double> _shift = new();
        private readonly double _height;

        public PageFlow(Page page, List<PdfUnit> units)
        {
            _units = units;
            _height = page.Height;
            foreach (var path in page.Paths)
                if (!path.IsClipping && (path.IsFilled || path.IsStroked) && path.GetBoundingRectangle() is { } r)
                    _graphics.Add(r);
            try
            {
                foreach (var image in page.GetImages()) _graphics.Add(image.BoundingBox);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // Bild nicht lesbar: dann eben ohne
            }
        }

        public double Shift(PdfUnit u) => _shift.GetValueOrDefault(u);

        /// <summary>Kopf- und Fußzeilen bleiben, wo sie sind.</summary>
        private bool Fixed(PdfUnit u) => u.Top < _height * 0.07 || u.Bottom > _height * 0.93;

        private static bool OverlapsX(double left, double right, PdfUnit u) => u.Left < right && u.Right > left;

        private static bool SameRow(PdfUnit a, PdfUnit b) => a.Bottom < b.Top && a.Top > b.Bottom;

        /// <summary>
        /// Was unter <paramref name="u"/> mitwandert, und die Grenze, an der es haltmacht. Mit wandert alles, was in
        /// derselben Spalte darunter steht, und alles, worauf ein mitwandernder Absatz sonst stoßen würde
        /// (z. B. das Datum neben einer Überschrift).
        /// </summary>
        private (List<PdfUnit> Movers, double Barrier) Below(PdfUnit u)
        {
            var (left, right) = (Math.Min(u.Left, u.LeftLimit), Math.Max(u.Right, u.RightLimit));
            var candidates = _units.Where(v => v != u && !Fixed(v) && v.Top < u.Bottom + 0.5).ToList();
            var movers = candidates.Where(v => OverlapsX(left, right, v)).ToHashSet();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var v in candidates)
                    if (!movers.Contains(v) && movers.Any(m => (OverlapsX(m.Left, m.Right, v) && v.Top < m.Bottom + 0.5) || SameRow(m, v)))
                        changed |= movers.Add(v);
            }

            bool Relevant(double l, double r) => (l < right && r > left) || movers.Any(m => m.Left < r && m.Right > l);
            var barrier = Math.Min(u.Bottom - Shift(u), 24.0);
            foreach (var r in _graphics)
            {
                if (r.Top < u.Bottom - 0.5 && Relevant(r.Left, r.Right)) barrier = Math.Max(barrier, r.Top);   // Linie/Grafik darunter
                else if (r.Bottom < u.Bottom - 0.5 && r.Top >= u.Top - 1 && r.Left <= u.Left + 1 && r.Right >= u.Right - 1)
                    barrier = Math.Max(barrier, r.Bottom + 3);                                                     // Kasten um den Text
            }
            foreach (var v in _units)
                if (v != u && Fixed(v) && v.Top < u.Bottom && Relevant(v.Left, v.Right)) barrier = Math.Max(barrier, v.Top - Shift(v));
            return (movers.Where(v => v.Bottom - Shift(v) > barrier).ToList(), barrier);
        }

        /// <summary>Freier Platz direkt unter dem Absatz; ein Teil des gewohnten Abstands zum nächsten Absatz bleibt.</summary>
        private double Free(PdfUnit u, List<PdfUnit> movers, double barrier)
        {
            var bottom = u.Bottom - Shift(u);
            var near = movers.Where(v => OverlapsX(Math.Min(u.Left, u.LeftLimit), Math.Max(u.Right, u.RightLimit), v)).ToList();
            var next = near.Select(v => v.Top - Shift(v)).DefaultIfEmpty(barrier).Max();
            var gap = Math.Max(0, bottom - next);
            var keep = near.Count > 0 ? Math.Max(u.Size, gap * 0.6) : Math.Max(3, Math.Min(gap * 0.5, u.Size));
            return Math.Max(0, gap - keep);
        }

        /// <summary>Wie weit sich der Text darunter verschieben lässt; vor der Grenze bleibt etwas Luft.</summary>
        private double Capacity(PdfUnit u, List<PdfUnit> movers, double barrier) =>
            movers.Count == 0 ? 0 : Math.Max(0, movers.Min(v => v.Bottom - Shift(v)) - barrier - Math.Max(6, u.Size * 1.5));

        /// <summary>Wie viel zusätzliche Höhe <paramref name="u"/> bekommen kann.</summary>
        public double Room(PdfUnit u)
        {
            var (movers, barrier) = Below(u);
            return Free(u, movers, barrier) + Capacity(u, movers, barrier);
        }

        /// <summary>
        /// Platz für <paramref name="extra"/> Punkt zusätzliche Höhe schaffen. Fließtext schiebt den Text darunter weiter,
        /// damit die Abstände zwischen den Absätzen bleiben; rechtsbündige und zentrierte Zeilen (Datum neben einer
        /// Überschrift) nutzen zuerst den freien Raum unter sich.
        /// </summary>
        public void Grow(PdfUnit u, double extra)
        {
            if (extra <= 0) return;
            var (movers, barrier) = Below(u);
            var push = Math.Min(u.Align == "L" ? extra : extra - Free(u, movers, barrier), Capacity(u, movers, barrier));
            if (push <= 0) return;
            foreach (var v in movers) _shift[v] = Shift(v) + push;
        }
    }

    /// <summary>
    /// Eingebettete Noto-Schriften, je Familie und Schnitt nur einmal im Dokument. Gemessen wird mit einem zweiten,
    /// nie gespeicherten Dokument, damit keine Hilfsseite im Ergebnis landet.
    /// </summary>
    private sealed class Fonts(PdfDocumentBuilder builder) : IDisposable
    {
        private readonly Dictionary<string, PdfDocumentBuilder.AddedFont> _added = new();
        private readonly Dictionary<string, PdfDocumentBuilder.AddedFont> _measureFonts = new();
        private readonly PdfDocumentBuilder _measureBuilder = new();
        private PdfPageBuilder? _measure;
        private readonly Dictionary<(string, string, double), double> _widths = new();

        private static string Name(bool serif, bool bold, bool italic) =>
            (serif ? "NotoSerif-" : "NotoSans-") + (bold && italic ? "BoldItalic" : bold ? "Bold" : italic ? "Italic" : "Regular");

        private static byte[] Load(string name)
        {
            using var stream = typeof(PdfRewriter).Assembly.GetManifestResourceStream($"SententiamConverte.Core.Fonts.{name}.ttf")
                               ?? throw new InvalidOperationException($"Schrift {name} fehlt.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public PdfDocumentBuilder.AddedFont Get(bool serif, bool bold, bool italic)
        {
            var name = Name(serif, bold, italic);
            if (!_added.TryGetValue(name, out var f)) _added[name] = f = builder.AddTrueTypeFont(Load(name));
            return f;
        }

        public double Width(string text, bool serif, bool bold, bool italic, double size)
        {
            if (text.Length == 0) return 0;
            var name = Name(serif, bold, italic);
            if (_widths.TryGetValue((name, text, size), out var cached)) return cached;
            if (!_measureFonts.TryGetValue(name, out var f)) _measureFonts[name] = f = _measureBuilder.AddTrueTypeFont(Load(name));
            _measure ??= _measureBuilder.AddPage(10, 10);
            var letters = _measure.MeasureText(text, size, new PdfPoint(0, 0), f);
            var width = letters.Count == 0 ? 0 : letters.Max(l => l.EndBaseLine.X);
            _widths[(name, text, size)] = width;
            return width;
        }

        private readonly Dictionary<(string, char), bool> _supported = new();

        /// <summary>Zeichen, die die Schrift nicht kennt, durch „?“ ersetzen statt den Export abzubrechen.</summary>
        public string Printable(string text, bool serif, bool bold, bool italic)
        {
            var name = Name(serif, bold, italic);
            if (!_measureFonts.TryGetValue(name, out var f)) _measureFonts[name] = f = _measureBuilder.AddTrueTypeFont(Load(name));
            _measure ??= _measureBuilder.AddPage(10, 10);
            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!_supported.TryGetValue((name, chars[i]), out var ok))
                {
                    try
                    {
                        _measure.MeasureText(chars[i].ToString(), 10, new PdfPoint(0, 0), f);
                        ok = !char.IsSurrogate(chars[i]);
                    }
                    catch (InvalidOperationException)
                    {
                        ok = false;
                    }
                    _supported[(name, chars[i])] = ok;
                }
                if (!ok) chars[i] = '?';
            }
            return new string(chars);
        }

        public double SpaceWidth(bool serif, bool bold, bool italic, double size) =>
            Width("a a", serif, bold, italic, size) - Width("aa", serif, bold, italic, size);

        public void Dispose() => _measureBuilder.Dispose();
    }
}
