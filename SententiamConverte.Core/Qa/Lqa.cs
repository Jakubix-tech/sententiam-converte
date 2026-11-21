using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Model;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.Core.Qa;

/// <summary>Schweregrad eines Fehlers in der Fehlerbewertung (nach MQM: neutral, leicht, schwer, kritisch).</summary>
public enum LqaSeverity { Neutral, Minor, Major, Critical }

/// <summary>Ein vom Prüfer erfasster Fehler in einem Segment.</summary>
public sealed record LqaError(string Category, LqaSeverity Severity, string Note, string Reviewer, DateTime When);

/// <summary>Einstellungen der Fehlerbewertung: Strafpunkte je Schweregrad und Bestehensgrenze.</summary>
public sealed class LqaSettings
{
    public int MinorPoints { get; set; } = 1;
    public int MajorPoints { get; set; } = 5;
    public int CriticalPoints { get; set; } = 25;

    /// <summary>Bestanden ab dieser Punktzahl (100 = fehlerfrei).</summary>
    public double PassScore { get; set; } = 98;

    public int PointsOf(LqaSeverity s) => s switch
    {
        LqaSeverity.Minor => MinorPoints,
        LqaSeverity.Major => MajorPoints,
        LqaSeverity.Critical => CriticalPoints,
        _ => 0,
    };
}

public sealed record LqaCategoryResult(string Category, int Minor, int Major, int Critical, int Points);

/// <summary>Ergebnis der Fehlerbewertung eines oder mehrerer Dokumente.</summary>
public sealed record LqaReport(int Words, int Errors, int Points, double Score, bool Passed, double PassScore, List<LqaCategoryResult> Categories)
{
    /// <summary>Strafpunkte je 1000 Wörter.</summary>
    public double PointsPerThousand => Words == 0 ? 0 : Math.Round(Points * 1000.0 / Words, 1);
}

/// <summary>
/// Fehlerbewertung im Lektorat (in Anlehnung an das offene MQM-Modell): Der Prüfer ordnet jedem Fehler eine Kategorie
/// und einen Schweregrad zu; daraus ergeben sich Strafpunkte und eine Punktzahl von 100 abwärts
/// (100 − Strafpunkte × 100 / Wörter). Ab der Bestehensgrenze gilt die Übersetzung als bestanden.
/// </summary>
public static class Lqa
{
    /// <summary>Kategorien mit Unterarten; gespeichert wird „Kategorie: Unterart“.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "Genauigkeit: Fehlübersetzung", "Genauigkeit: Auslassung", "Genauigkeit: Hinzufügung", "Genauigkeit: Unübersetzt",
        "Sprache: Grammatik", "Sprache: Rechtschreibung", "Sprache: Zeichensetzung", "Sprache: Unidiomatisch",
        "Terminologie: Falscher Fachbegriff", "Terminologie: Uneinheitlich",
        "Stil: Register oder Ton", "Stil: Kundenvorgaben",
        "Lokalisierung: Zahlen, Datum, Einheiten", "Lokalisierung: Kulturbezug",
        "Formatierung: Tags oder Layout",
    ];

    public static string MainCategory(string category) => category.Split(':')[0].Trim();

    /// <summary>
    /// Stelle und Verbesserung aus der Anmerkung eines Fehlers („„falsche Wörter“ → „bessere Fassung“: Begründung“, so legt
    /// die KI-Bewertung sie an); null, wenn die Anmerkung keinen Vorschlag enthält.
    /// </summary>
    public static (string Span, string Better)? Suggestion(LqaError error)
    {
        var m = System.Text.RegularExpressions.Regex.Match(error.Note, @"^„(?<span>.+?)“ → „(?<better>.*?)“(?::|$)");
        return m.Success ? (m.Groups["span"].Value, m.Groups["better"].Value) : null;
    }

    /// <summary>
    /// Den Vorschlag eines Fehlers in die Übersetzung einsetzen: die bemängelte Stelle wird durch die bessere Fassung ersetzt.
    /// null, wenn es keinen Vorschlag gibt oder die Stelle so nicht in der Übersetzung steht (etwa bei einer Auslassung).
    /// </summary>
    public static string? ApplySuggestion(string target, LqaError error)
    {
        if (Suggestion(error) is not { } s || s.Span.Length == 0) return null;
        var at = target.IndexOf(s.Span, StringComparison.Ordinal);
        if (at < 0) at = target.IndexOf(s.Span, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : target[..at] + s.Better + target[(at + s.Span.Length)..];
    }

    public static string SeverityName(LqaSeverity s, bool en = false) => (s, en) switch
    {
        (LqaSeverity.Neutral, false) => "neutral",
        (LqaSeverity.Minor, false) => "leicht",
        (LqaSeverity.Major, false) => "schwer",
        (LqaSeverity.Critical, false) => "kritisch",
        (LqaSeverity.Neutral, true) => "neutral",
        (LqaSeverity.Minor, true) => "minor",
        (LqaSeverity.Major, true) => "major",
        _ => "critical",
    };

    public static LqaReport Evaluate(IEnumerable<BilingualDocument> documents, LqaSettings settings)
    {
        var segments = documents.SelectMany(d => d.Segments).Where(s => !s.Locked).ToList();
        var words = segments.Sum(s => Analysis.Analyzer.CountWords(s.Source));
        var errors = segments.SelectMany(s => s.Errors ?? []).ToList();
        var points = errors.Sum(e => settings.PointsOf(e.Severity));
        var score = words == 0 ? 100 : Math.Max(0, Math.Round(100 - points * 100.0 / words, 1));
        var categories = errors.GroupBy(e => MainCategory(e.Category)).OrderBy(g => g.Key)
            .Select(g => new LqaCategoryResult(g.Key, g.Count(e => e.Severity == LqaSeverity.Minor), g.Count(e => e.Severity == LqaSeverity.Major),
                g.Count(e => e.Severity == LqaSeverity.Critical), g.Sum(e => settings.PointsOf(e.Severity))))
            .ToList();
        // ein kritischer Fehler lässt die Übersetzung immer durchfallen
        var passed = score >= settings.PassScore && errors.All(e => e.Severity != LqaSeverity.Critical);
        return new LqaReport(words, errors.Count, points, score, passed, settings.PassScore, categories);
    }

    /// <summary>Bewertungsbericht als Word-Datei: Ergebnis, Kategorien, jeder Fehler mit Segment und Anmerkung.</summary>
    public static void ExportDocx(string title, IReadOnlyList<BilingualDocument> documents, LqaSettings settings, Stream output, string uiLanguage = "de")
    {
        var en = uiLanguage == "en";
        var report = Evaluate(documents, settings);
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(Para(en ? "Quality evaluation" : "Qualitätsbewertung", true, 32));
            body.Append(Para($"{title} · {DateTime.Now:yyyy-MM-dd}", false, 20, "666666"));
            body.Append(Para(en
                ? $"Score {report.Score:0.0} of 100 (pass from {report.PassScore:0.#}) – {(report.Passed ? "PASSED" : "FAILED")}"
                : $"Punktzahl {report.Score:0.0} von 100 (bestanden ab {report.PassScore:0.#}) – {(report.Passed ? "BESTANDEN" : "NICHT BESTANDEN")}", true, 26,
                report.Passed ? "2F855A" : "C53030"));
            body.Append(Para(en
                ? $"{report.Errors} errors, {report.Points} penalty points, {report.Words} words ({report.PointsPerThousand:0.#} points per 1000 words). Points: minor {settings.MinorPoints}, major {settings.MajorPoints}, critical {settings.CriticalPoints}."
                : $"{report.Errors} Fehler, {report.Points} Strafpunkte, {report.Words} Wörter ({report.PointsPerThousand:0.#} Punkte je 1000 Wörter). Punkte: leicht {settings.MinorPoints}, schwer {settings.MajorPoints}, kritisch {settings.CriticalPoints}.",
                false, 20));
            if (report.Categories.Count > 0)
            {
                var t = Table();
                t.Append(Row([en ? "Category" : "Kategorie", en ? "minor" : "leicht", en ? "major" : "schwer", en ? "critical" : "kritisch", en ? "Points" : "Punkte"], true));
                foreach (var c in report.Categories)
                    t.Append(Row([c.Category, c.Minor.ToString(), c.Major.ToString(), c.Critical.ToString(), c.Points.ToString()]));
                body.Append(t);
            }
            body.Append(Para(en ? "Errors" : "Fehler im Einzelnen", true, 24));
            var list = Table();
            list.Append(Row([en ? "No." : "Nr.", en ? "Source / translation" : "Ausgangstext / Übersetzung", en ? "Category" : "Kategorie",
                en ? "Severity" : "Schwere", en ? "Comment" : "Anmerkung"], true));
            foreach (var doc in documents)
                foreach (var s in doc.Segments.Where(s => s.Errors is { Count: > 0 }))
                    foreach (var e in s.Errors!)
                        list.Append(Row([$"{(documents.Count > 1 ? Path.GetFileName(doc.FileName) + " " : "")}{s.Id}",
                            TagText.StripTags(s.Source) + "\n→ " + TagText.StripTags(s.Target), e.Category, SeverityName(e.Severity, en), e.Note]));
            body.Append(list);
            main.Document = new W.Document(body);
            main.Document.Save();
        }
        ms.Position = 0;
        ms.CopyTo(output);
    }

    private static W.Table Table() => new(new W.TableProperties(new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
        new W.TableBorders(new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" },
            new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" })));

    private static W.Paragraph Para(string text, bool bold, int size, string? color = null)
    {
        var rp = new W.RunProperties();
        if (bold) rp.Append(new W.Bold());
        if (color != null) rp.Append(new W.Color { Val = color });
        rp.Append(new W.FontSize { Val = size.ToString() });
        var run = new W.Run(rp);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new W.Break());
            run.Append(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "80" }), run);
    }

    private static W.TableRow Row(string[] cells, bool bold = false) =>
        new(cells.Select(c => new W.TableCell(Para(c, bold, 18))));
}

/// <summary>
/// Änderungsverfolgung im Lektorat: Ändert der Prüfer eine bestätigte Übersetzung, bleibt die vorige Fassung als
/// <see cref="Segment.ReviewBaseline"/> stehen. Der Übersetzer sieht die Änderung wortweise und nimmt sie an
/// (die Basis entfällt) oder lehnt sie ab (die vorige Fassung gilt wieder).
/// </summary>
public static class TrackChanges
{
    public static bool HasChange(Segment s) => s.ReviewBaseline != null && s.ReviewBaseline != s.Target;

    /// <summary>Vor einer Änderung durch den Prüfer aufrufen: merkt sich die bestätigte Fassung.</summary>
    public static void Remember(Segment s)
    {
        if (s.ReviewBaseline == null && s.IsConfirmed && s.Target.Length > 0) s.ReviewBaseline = s.Target;
    }

    public static void Accept(Segment s) => s.ReviewBaseline = null;

    public static void Reject(Segment s)
    {
        if (s.ReviewBaseline == null) return;
        if (s.Target != s.ReviewBaseline) s.History.Add(new SegmentRevision(DateTime.UtcNow, s.Target, s.Status, s.Origin));
        s.Target = s.ReviewBaseline;
        s.ReviewBaseline = null;
        s.Status = SegmentStatus.Translated;
        s.Modified = DateTime.UtcNow;
    }
}

/// <summary>Wortweiser Vergleich zweier Fassungen.</summary>
public static class WordDiff
{
    public enum Kind { Same, Deleted, Inserted }

    public readonly record struct Part(Kind Kind, string Text);

    public static List<Part> Compare(string before, string after)
    {
        var a = Tokens(before);
        var b = Tokens(after);
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var parts = new List<Part>();
        void Add(Kind k, string t)
        {
            if (parts.Count > 0 && parts[^1].Kind == k) parts[^1] = new Part(k, parts[^1].Text + t);
            else parts.Add(new Part(k, t));
        }
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y]) { Add(Kind.Same, a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) Add(Kind.Deleted, a[x++]);
            else Add(Kind.Inserted, b[y++]);
        }
        while (x < a.Count) Add(Kind.Deleted, a[x++]);
        while (y < b.Count) Add(Kind.Inserted, b[y++]);
        return parts;
    }

    /// <summary>Wörter samt folgendem Leerraum, damit der Text beim Zusammensetzen gleich bleibt.</summary>
    private static List<string> Tokens(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\S+\s*|\s+").Select(m => m.Value).ToList();
}
