using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tm;

/// <summary>Wie oft eine Wendung in den eigenen Daten vorkommt – insgesamt und je Datenbank.</summary>
public sealed record PhraseFrequency(string Phrase, long Units, IReadOnlyList<(string Tm, long Units)> PerTm);

/// <summary>
/// Wendungen in den eigenen Daten zählen (TMs des Projekts, gemeinsame und geladene freie Sprachdaten): genau,
/// privat und für jede Sprache – statt der geschätzten Trefferzahlen von Suchmaschinen. Mehrere Varianten mit „|“
/// trennen, dann lassen sie sich vergleichen („Benutzer | Nutzer“).
/// </summary>
public static class PhraseCounter
{
    public static IReadOnlyList<string> Variants(string query) =>
        query.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    public static PhraseFrequency Count(IEnumerable<ITranslationMemory> tms, string phrase, string language)
    {
        var per = new List<(string, long)>();
        foreach (var tm in tms)
        {
            long n;
            try
            {
                // eigene TMs zählen genau; andere (Teamserver) über die Konkordanz, höchstens 200
                n = tm is TranslationMemory local
                    ? local.PhraseCount(language, phrase)
                    : tm.Concordance(language, language, phrase, inTarget: false, max: 200).Count;
            }
            catch (Exception) { continue; }
            if (n > 0) per.Add((tm.Name, n));
        }
        return new PhraseFrequency(phrase, per.Sum(p => p.Item2), per.OrderByDescending(p => p.Item2).ToList());
    }
}
