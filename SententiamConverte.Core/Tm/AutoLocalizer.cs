using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tm;

/// <summary>
/// Zahlen und Datum in der Schreibweise der Zielsprache: Steht eine Zahl oder ein Datum unverändert aus dem
/// Ausgangstext in der Übersetzung, wird sie umgeschrieben – „1.234,5 kg“ → „1,234.5 kg“ (en), „0,75“ → „0.75“,
/// „12.03.2024“ → „12/03/2024“ (en-GB). Ganze Zahlen ohne Trennzeichen, Versionsnummern („1.2.3“) und
/// Uhrzeiten bleiben, wie sie sind; Maßeinheiten werden nicht umgerechnet.
/// </summary>
public static partial class AutoLocalizer
{
    // Zahl mit Tausendertrennern und/oder Dezimalstellen – nicht Teil eines Datums, einer Versionsnummer oder IP-Adresse
    [GeneratedRegex(@"(?<![\d.,])(?:\d{1,3}(?:[.,   ]\d{3})+(?:[.,]\d+)?|\d+[.,]\d+)(?![.,]?\d)")]
    private static partial Regex NumberRegex();

    /// <summary>Dezimalzeichen und Tausendertrenner einer Sprache.</summary>
    public static (char Decimal, string Group) Convention(string language) => Languages.Primary(language) switch
    {
        "en" or "zh" or "ja" or "ko" or "he" or "th" => ('.', ","),
        "de" or "it" or "es" or "nl" or "pt" or "da" or "tr" or "el" or "ro" or "id" or "sl" or "hr" or "sr" => (',', "."),
        "fr" or "pl" or "cs" or "sk" or "ru" or "uk" or "sv" or "fi" or "nb" or "no" or "hu" or "bg" or "lt" or "lv" or "et" => (',', " "),
        _ => (',', "."),
    };

    private static (char Decimal, string Group) ConventionOf(string language) =>
        language.Equals("de-CH", StringComparison.OrdinalIgnoreCase) ? ('.', "’") : Convention(language);

    /// <summary>Eine Zahl nach der Konvention lesen: Wert als Ziffernfolge, Dezimalstellen, ob gruppiert.</summary>
    private static bool TryParse(string s, char dec, out string integer, out string fraction, out bool grouped)
    {
        integer = fraction = "";
        grouped = false;
        var decIndex = s.LastIndexOf(dec);
        var intPart = decIndex >= 0 ? s[..decIndex] : s;
        fraction = decIndex >= 0 ? s[(decIndex + 1)..] : "";
        if (fraction.Any(c => !char.IsDigit(c))) return false;
        var digits = new StringBuilder();
        foreach (var c in intPart)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else grouped = true;
        }
        // Tausendergruppen müssen dreistellig sein, sonst ist es keine Zahl dieser Konvention
        if (grouped && !Regex.IsMatch(intPart, @"^\d{1,3}(?:[^\d]\d{3})+$")) return false;
        integer = digits.ToString();
        return integer.Length > 0;
    }

    private static string Format(string integer, string fraction, bool grouped, string language)
    {
        var (dec, group) = ConventionOf(language);
        // vierstellige Zahlen ohne Trenner, wo die Sprache Leerzeichen gruppiert (1234, aber 12 345)
        if (grouped && integer.Length > 3 && !(group == " " && integer.Length == 4))
            integer = Regex.Replace(integer, @"\B(?=(\d{3})+(?!\d))", group);
        return fraction.Length > 0 ? integer + dec + fraction : integer;
    }

    /// <summary>Zahlen der Quelle, die im Format der Ausgangssprache in der Übersetzung stehen, im Format der Zielsprache schreiben.</summary>
    public static string LocalizeNumbers(string source, string target, string sourceLanguage, string targetLanguage)
    {
        var from = ConventionOf(sourceLanguage);
        var to = ConventionOf(targetLanguage);
        if (from == to) return target;
        foreach (var value in NumberRegex().Matches(TagText.StripTags(source)).Select(m => m.Value).Distinct().OrderByDescending(v => v.Length))
        {
            if (!TryParse(value, from.Decimal, out var integer, out var fraction, out var grouped)) continue;
            var localized = Format(integer, fraction, grouped, targetLanguage);
            if (localized == value) continue;
            // nur ganze Zahlen ersetzen, nicht Teile längerer Zahlen
            target = Regex.Replace(target, $@"(?<![\d.,]){Regex.Escape(value)}(?![.,]?\d)", localized);
        }
        return target;
    }

    /// <summary>Zahlen und Datum in die Schreibweise der Zielsprache bringen.</summary>
    public static string Localize(string source, string target, string sourceLanguage, string targetLanguage) =>
        Privacy.DateLocalizer.Localize(LocalizeNumbers(source, target, sourceLanguage, targetLanguage), source, targetLanguage);
}

/// <summary>
/// Pseudoübersetzung zum Testen vor der eigentlichen Übersetzung: Buchstaben werden durch Akzentformen ersetzt
/// („Ŝćĥŕàübé“), der Text um einen Anteil verlängert und in Klammern gesetzt. Tags, Zahlen und Platzhalter bleiben.
/// So zeigt die Zieldatei, ob Sonderzeichen, längere Texte und alle Textstellen richtig ankommen.
/// </summary>
public static class PseudoTranslator
{
    private const string Plain = "abcdeghiklnorstuyzABCDEGHIKLNORSTUYZ";
    private const string Accented = "àƀçđéĝĥîķĺñöŕšţüýžÀƁÇĐÉĜĤÎĶĹÑÖŔŠŢÜÝŽ";

    public static string Translate(string source, int expansionPercent = 30)
    {
        var sb = new StringBuilder("[");
        var letters = 0;
        foreach (var part in TagText.Split(source))
        {
            if (part.Tag is { } tag) { sb.Append(source, tag.Index, tag.Length); continue; }
            foreach (var c in part.Text!)
            {
                var i = Plain.IndexOf(c);
                sb.Append(i >= 0 ? Accented[i] : c);
                if (char.IsLetter(c)) letters++;
            }
        }
        var padding = (int)Math.Ceiling(letters * expansionPercent / 100.0);
        if (padding > 0) sb.Append(' ').Append(new string('~', padding));
        return sb.Append(']').ToString();
    }
}
