using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tm;

/// <summary>
/// Ähnlichkeit zweier Segmente in Prozent (unscharfer Treffer).
/// Wortweise Levenshtein-Distanz, bei der ein ähnliches Wort (Tippfehler, Endung) weniger kostet als ein fremdes.
/// </summary>
public static partial class FuzzyMatcher
{
    [GeneratedRegex(@"⟨/?\d+/?⟩|\p{L}[\p{L}\p{M}\p{Nd}'’\-]*|\p{Nd}+(?:[.,]\p{Nd}+)*|[^\s\p{L}\p{Nd}]")]
    private static partial Regex TokenRegex();

    public static List<string> Tokenize(string text) =>
        TokenRegex().Matches(text).Select(m => m.Value).ToList();

    /// <summary>Wörter (ohne Tags/Satzzeichen), klein, für die Volltextsuche.</summary>
    public static IEnumerable<string> Words(string text) =>
        Tokenize(TagText.StripTags(text)).Where(t => char.IsLetterOrDigit(t[0])).Select(t => t.ToLowerInvariant());

    /// <summary>Ähnlichkeit in Prozent. Mit <paramref name="minScore"/> wird früh abgebrochen, wenn er unerreichbar ist (gibt dann 0).</summary>
    public static int Score(string query, string candidate, int minScore = 0)
    {
        if (query == candidate) return 100;
        var qPlain = TagText.StripTags(query).Trim();
        var cPlain = TagText.StripTags(candidate).Trim();
        if (qPlain == cPlain) return 99;                                     // nur Tags verschieden
        if (string.Equals(qPlain, cPlain, StringComparison.OrdinalIgnoreCase)) return 99;

        var a = Tokenize(qPlain);
        var b = Tokenize(cPlain);
        if (a.Count == 0 || b.Count == 0) return 0;
        // Längenunterschied allein begrenzt die erreichbare Quote
        if (minScore > 0 && 100.0 * Math.Min(a.Count, b.Count) / Math.Max(a.Count, b.Count) < minScore) return 0;

        // Höchstens so viel „Abstand“, dass die Mindestquote noch erreichbar ist – sonst früh abbrechen
        var maxCost = minScore > 0 ? (1.0 - minScore / 100.0) * Math.Max(a.Count, b.Count) + 0.01 : double.MaxValue;
        var cost = WeightedEditDistance(a, b, maxCost);
        if (cost > maxCost) return 0;
        var score = (int)Math.Floor(100.0 * (1.0 - cost / Math.Max(a.Count, b.Count)));
        if (TagText.TagSignature(query) != TagText.TagSignature(candidate)) score -= 1;
        return Math.Clamp(score, 0, 98);
    }

    private static double WeightedEditDistance(List<string> a, List<string> b, double maxCost = double.MaxValue)
    {
        var prev = new double[b.Count + 1];
        var cur = new double[b.Count + 1];
        for (var j = 0; j <= b.Count; j++) prev[j] = j;
        for (var i = 1; i <= a.Count; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var sub = prev[j - 1] + SubstitutionCost(a[i - 1], b[j - 1]);
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), sub);
            }
            // Ist schon die ganze Zeile teurer als erlaubt, wird es nicht mehr besser
            var rowMin = double.MaxValue;
            for (var j = 0; j <= b.Count; j++) if (cur[j] < rowMin) rowMin = cur[j];
            if (rowMin > maxCost) return double.MaxValue;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Count];
    }

    private static double SubstitutionCost(string x, string y)
    {
        if (x == y) return 0;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return 0.1;
        // Zahlen werden beim Einsetzen automatisch angepasst – nur kleiner Abzug
        if (char.IsDigit(x[0]) && char.IsDigit(y[0])) return 0.15;
        if (!char.IsLetter(x[0]) || !char.IsLetter(y[0])) return 1;
        // Sehr verschieden lange Wörter können nicht ähnlich genug sein
        if (Math.Abs(x.Length - y.Length) > 0.4 * Math.Max(x.Length, y.Length)) return 1;
        var d = CharDistance(x.ToLowerInvariant(), y.ToLowerInvariant());
        var sim = 1.0 - (double)d / Math.Max(x.Length, y.Length);
        // erst ab deutlicher Ähnlichkeit (Flexion, Tippfehler) wird es billiger
        return sim >= 0.6 ? 1 - sim * 0.7 : 1;
    }

    public static int CharDistance(string s, string t)
    {
        var prev = new int[t.Length + 1];
        var cur = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++) prev[j] = j;
        for (var i = 1; i <= s.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= t.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[t.Length];
    }
}
