namespace SententiamConverte.Core.Model;

/// <summary>
/// Ein Absatz der Ausgangsdatei. Der Dateifilter merkt sich über <see cref="Locator"/>,
/// wo der Absatz im Original steht, und über <see cref="TagData"/>, wofür jedes Tag steht.
/// </summary>
public sealed class Paragraph
{
    public string Locator { get; set; } = "";
    public Dictionary<int, string> TagData { get; set; } = new();
    public List<Segment> Segments { get; set; } = new();

    /// <summary>Leerraum vor dem ersten Segment.</summary>
    public string LeadingWhitespace { get; set; } = "";

    /// <summary>Setzt die Zielfassung des Absatzes zusammen; unübersetzte Segmente bleiben im Original.</summary>
    public string JoinTarget() =>
        LeadingWhitespace + string.Concat(Segments.Select(s =>
            (string.IsNullOrEmpty(s.Target) ? s.Source : s.Target) + s.TrailingWhitespace));

    public string JoinSource() =>
        LeadingWhitespace + string.Concat(Segments.Select(s => s.Source + s.TrailingWhitespace));

    /// <summary>Optionaler Kontext für Übersetzer und KI (z. B. Tabellenzelle, Überschrift).</summary>
    public string? Context { get; set; }
}

/// <summary>Die zweisprachige Arbeitsfassung einer Datei für genau eine Zielsprache.</summary>
public sealed class BilingualDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string FilterId { get; set; } = "";
    public string SourceLanguage { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
    public List<Paragraph> Paragraphs { get; set; } = new();

    public IEnumerable<Segment> Segments => Paragraphs.SelectMany(p => p.Segments);

    /// <summary>Vergibt fortlaufende Segmentnummern ab 1.</summary>
    public void Renumber()
    {
        var n = 1;
        foreach (var s in Segments) s.Id = n++;
    }

    public Paragraph? ParagraphOf(Segment segment) =>
        Paragraphs.FirstOrDefault(p => p.Segments.Contains(segment));
}
