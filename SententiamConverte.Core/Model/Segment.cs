namespace SententiamConverte.Core.Model;

/// <summary>Bearbeitungsstand eines Segments (Übersetzung, Lektorat, Freigabe).</summary>
public enum SegmentStatus
{
    NotTranslated,
    Draft,
    Translated,
    TranslationRejected,
    Reviewed,
    ReviewRejected,
    Signed,
}

/// <summary>Woher die aktuelle Übersetzung stammt.</summary>
public enum TranslationOrigin
{
    None,
    Manual,
    TranslationMemory,
    AutoPropagated,
    Ai,
    Source,
}

/// <summary>Eine frühere Fassung eines Segments.</summary>
public sealed record SegmentRevision(DateTime When, string Target, SegmentStatus Status, TranslationOrigin Origin);

/// <summary>
/// Ein Satz (oder kurzer Absatz) Quelltext mit seiner Übersetzung.
/// Formatierungen stehen als Tags im Text, siehe <see cref="TagText"/>.
/// </summary>
public sealed class Segment
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public SegmentStatus Status { get; set; }
    public TranslationOrigin Origin { get; set; }

    /// <summary>Trefferquote des TM-Treffers, aus dem die Übersetzung stammt (null = keiner).</summary>
    public int? MatchPercent { get; set; }

    /// <summary>Leerraum, der im Original nach diesem Segment stand (beim Zurückschreiben nötig).</summary>
    public string TrailingWhitespace { get; set; } = "";

    /// <summary>Gesperrte Segmente werden weder übersetzt noch geprüft (z. B. reine Zahlen).</summary>
    public bool Locked { get; set; }

    /// <summary>Kennung des Segments in der Ausgangsdatei, falls der Filter eine braucht (z. B. mrk mid in SDLXLIFF).</summary>
    public string? Key { get; set; }

    public string? Comment { get; set; }
    public DateTime? Modified { get; set; }

    /// <summary>Frühere bestätigte Fassungen (Änderungsverlauf für das Lektorat).</summary>
    public List<SegmentRevision> History { get; set; } = new();

    public bool IsConfirmed => Status is SegmentStatus.Translated or SegmentStatus.Reviewed or SegmentStatus.Signed;
}
