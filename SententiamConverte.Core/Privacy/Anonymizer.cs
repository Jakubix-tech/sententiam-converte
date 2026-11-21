using System.Text.Json;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Privacy;

/// <summary>
/// Anonymisierte Fassung eines Dokuments zum Weitergeben: geschützte Angaben (Namen, Daten, Aktenzeichen, E-Mail,
/// Telefon, IBAN, Schutzliste – wie in den Datenschutz-Einstellungen des Projekts) werden im ganzen Dokument einheitlich
/// durch Platzhalter ersetzt ([NAME_1], [DATE_1] …): derselbe Wert bekommt überall denselben Platzhalter. Das Layout
/// der Datei bleibt, weil der Dateifilter sie wie eine Übersetzung schreibt.
/// </summary>
public static class Anonymizer
{
    /// <summary>
    /// Kopie des Dokuments, deren Zieltexte anonymisiert sind – aus dem Ausgangstext (<paramref name="source"/>) oder
    /// aus der Übersetzung (leere Segmente: Ausgangstext). Ergebnis: Dokument und Zahl der verschiedenen ersetzten Angaben.
    /// </summary>
    public static (BilingualDocument Document, int Replaced) Anonymize(BilingualDocument doc, PrivacySettings settings, bool source)
    {
        var copy = JsonSerializer.Deserialize<BilingualDocument>(JsonSerializer.Serialize(doc))!;
        var map = new PrivacyMap(settings);
        foreach (var s in copy.Segments)
        {
            var text = source || s.Target.Trim().Length == 0 ? s.Source : s.Target;
            s.Target = map.Mask(text);
        }
        if (!source) copy.TargetLanguage = doc.TargetLanguage;
        return (copy, map.Count);
    }
}
