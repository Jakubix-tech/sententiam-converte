using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai.LocalMt;

/// <summary>
/// Anführungszeichen der Zielsprache: Die Modelle geben „…“ meist als gerade Zeichen ("…") aus. Hat die Quelle
/// typografische Anführungszeichen, bekommt die Übersetzung die der Zielsprache.
/// </summary>
internal static partial class Typography
{
    private static (string Open, string Close) For(string language) => LocalTranslation.Code(language) switch
    {
        "de" or "cs" or "sk" or "sl" or "hr" or "bg" or "lt" or "et" or "is" or "da" => ("„", "“"),
        "pl" or "ro" or "hu" or "sr" or "bs" or "mk" => ("„", "”"),
        "fr" => ("«\u00A0", "\u00A0»"),
        "es" or "it" or "ru" or "uk" or "no" or "ca" or "el" or "lv" => ("«", "»"),
        "pt" => language.EndsWith("-BR", StringComparison.OrdinalIgnoreCase) ? ("“", "”") : ("«", "»"),
        "sv" or "fi" => ("”", "”"),
        _ => ("“", "”"),
    };

    [GeneratedRegex("[„“”«»]")]
    private static partial Regex Typographic();

    [GeneratedRegex("\"([^\"\\n]+)\"")]
    private static partial Regex StraightPair();

    public static string Quotes(string source, string target, string targetLanguage)
    {
        if (!Typographic().IsMatch(source) || !target.Contains('"')) return target;
        var (open, close) = For(targetLanguage);
        return StraightPair().Replace(target, m => open + m.Groups[1].Value.Trim() + close);
    }
}
