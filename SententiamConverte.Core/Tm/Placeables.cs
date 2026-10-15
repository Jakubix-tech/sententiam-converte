using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tm;

/// <summary>
/// Automatische Anpassung von Zahlen in TM-Treffern: unterscheiden sich Quelle und TM-Quelle nur in Zahlen,
/// werden die Zahlen der TM-Übersetzung entsprechend ersetzt (automatische Lokalisierung von Zahlen).
/// </summary>
public static partial class Placeables
{
    [GeneratedRegex(@"\d+(?:[.,  ]\d{3})*(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    public static string AdaptNumbers(string source, string tmSource, string tmTarget, string targetLanguage)
    {
        var now = NumberRegex().Matches(TagText.StripTags(source)).Select(m => m.Value).ToList();
        var then = NumberRegex().Matches(TagText.StripTags(tmSource)).Select(m => m.Value).ToList();
        if (now.Count == 0 || now.Count != then.Count) return tmTarget;

        var result = tmTarget;
        for (var i = 0; i < now.Count; i++)
        {
            if (now[i] == then[i]) continue;
            var inTarget = NumberRegex().Matches(result).Where(m => Normalize(m.Value) == Normalize(then[i])).ToList();
            if (inTarget.Count != 1) continue;             // nicht eindeutig → lieber nichts ändern
            var replacement = Localize(now[i], inTarget[0].Value, targetLanguage);
            result = result[..inTarget[0].Index] + replacement + result[(inTarget[0].Index + inTarget[0].Length)..];
        }
        return result;
    }

    private static string Normalize(string n) => Regex.Replace(n, @"[.,  ](?=\d{3}(\D|$))", "").Replace(',', '.');

    /// <summary>Neue Zahl im Zahlenformat, das die TM-Übersetzung benutzt (1.000,5 ↔ 1,000.5).</summary>
    private static string Localize(string number, string sample, string targetLanguage)
    {
        var value = Normalize(number);
        var commaDecimal = Regex.IsMatch(sample, @",\d{1,2}$") || (!Regex.IsMatch(sample, @"\.\d{1,2}$") && UsesCommaDecimal(targetLanguage));
        var parts = value.Split('.');
        var intPart = parts[0];
        var group = sample.Contains('.') && commaDecimal ? "." : sample.Contains(',') && !commaDecimal ? "," : sample.Contains(' ') ? " " : sample.Contains(' ') ? " " : "";
        // Hatte die neue Zahl Tausendertrennzeichen, die TM-Zahl aber keine: Konvention der Zielsprache
        if (group.Length == 0 && Regex.IsMatch(number, @"\d[.,  ]\d{3}(\D|$)")) group = GroupSeparator(targetLanguage);
        if (group.Length > 0 && intPart.Length > 3)
            intPart = Regex.Replace(intPart, @"\B(?=(\d{3})+(?!\d))", group);
        return parts.Length > 1 ? intPart + (commaDecimal ? "," : ".") + parts[1] : intPart;
    }

    private static string GroupSeparator(string lang) => Languages.Primary(lang) switch
    {
        "en" or "zh" or "ja" or "ko" => ",",
        "de" or "it" or "es" or "nl" or "pt" or "da" or "tr" or "el" or "ro" or "id" => ".",
        "fr" or "pl" or "cs" or "sk" or "ru" or "uk" or "sv" or "fi" or "nb" or "no" or "hu" => "\u00A0",
        _ => "",
    };

    private static bool UsesCommaDecimal(string lang) =>
        Languages.Primary(lang) is not ("en" or "zh" or "ja" or "ko");
}
