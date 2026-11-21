using System.Globalization;
using System.Text.RegularExpressions;

namespace SententiamConverte.Core.Privacy;

/// <summary>
/// Bringt Datumsangaben, die unverändert aus dem Ausgangstext stammen, in die Schreibweise der Zielsprache –
/// nötig, wenn die KI das Datum nur als Platzhalter gesehen hat: „3. April 2024“ → „3 April 2024“ (en-GB),
/// „3 kwietnia 2024“ (pl-PL); „12.03.2024“ → „12/03/2024“ (en-GB), „3/12/2024“ (en-US).
/// </summary>
public static partial class DateLocalizer
{
    private static readonly string[] Languages = ["de", "en", "pl", "fr", "it", "es", "nl", "pt", "cs"];

    /// <summary>Monatsnamen (auch Genitiv) aller unterstützten Sprachen → Monatszahl.</summary>
    private static readonly Dictionary<string, int> MonthNumbers = BuildMonths();

    private static Dictionary<string, int> BuildMonths()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in Languages)
        {
            var f = CultureInfo.GetCultureInfo(lang).DateTimeFormat;
            for (var m = 0; m < 12; m++)
            {
                d.TryAdd(f.MonthNames[m], m + 1);
                d.TryAdd(f.MonthGenitiveNames[m], m + 1);
            }
        }
        d.TryAdd("Jänner", 1);
        return d;
    }

    [GeneratedRegex(@"^(?<d>\d{1,2})\.\s?(?<m>\d{1,2})\.\s?(?<y>\d{4}|\d{2})$|^(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})$|^(?<d>\d{1,2})/(?<m>\d{1,2})/(?<y>\d{4}|\d{2})$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^(?<d>\d{1,2})\.?\s(?<mn>\p{L}+)\s(?<y>\d{4})$|^(?<mn>\p{L}+)\s(?<d>\d{1,2}),?\s(?<y>\d{4})$")]
    private static partial Regex Textual();

    /// <summary>Ein Datum lesen; <paramref name="numeric"/> sagt, ob es in Ziffern geschrieben war.</summary>
    public static bool TryParse(string value, out DateTime date, out bool numeric)
    {
        date = default;
        numeric = false;
        var v = value.Trim();
        if (Numeric().Match(v) is { Success: true } n)
        {
            numeric = true;
            var y = int.Parse(n.Groups["y"].Value);
            if (y < 100) y += y > 50 ? 1900 : 2000;
            return TryDate(y, int.Parse(n.Groups["m"].Value), int.Parse(n.Groups["d"].Value), out date);
        }
        if (Textual().Match(v) is { Success: true } t && MonthNumbers.TryGetValue(t.Groups["mn"].Value, out var month))
            return TryDate(int.Parse(t.Groups["y"].Value), month, int.Parse(t.Groups["d"].Value), out date);
        return false;
    }

    private static bool TryDate(int y, int m, int d, out DateTime date)
    {
        date = default;
        if (m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), m)) return false;
        date = new DateTime(y, m, d);
        return true;
    }

    public static string Format(DateTime date, bool numeric, string language)
    {
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return date.ToString(numeric ? "dd.MM.yyyy" : "d MMMM yyyy", CultureInfo.InvariantCulture); }
        var f = culture.DateTimeFormat;
        if (numeric) return date.ToString(f.ShortDatePattern.Replace("yy", "yyyy").Replace("yyyyyyyy", "yyyy"), culture);
        // langes Format ohne Wochentag und ohne führende Null: „3 April 2024“, „3 kwietnia 2024“, „April 3, 2024“
        var pattern = Regex.Replace(f.LongDatePattern, @"dddd,?\s*", "").Trim().Replace("dd", "d");
        return date.ToString(pattern, culture);
    }

    /// <summary>Daten aus dem Ausgangstext, die unverändert in der Übersetzung stehen, in die Zielsprache bringen.</summary>
    public static string Localize(string translation, string source, string targetLanguage)
    {
        var settings = new PrivacySettings { Names = false, CaseNumbers = false, Emails = false, Phones = false, Iban = false };
        foreach (var span in SensitiveData.Find(source, settings).Where(s => s.Kind == SensitiveKind.Date).OrderByDescending(s => s.Length))
        {
            if (!translation.Contains(span.Value, StringComparison.Ordinal) || !TryParse(span.Value, out var date, out var numeric)) continue;
            var localized = Format(date, numeric, targetLanguage);
            if (localized != span.Value) translation = translation.Replace(span.Value, localized, StringComparison.Ordinal);
        }
        return translation;
    }
}
