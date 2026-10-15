using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Segmentation;

/// <summary>
/// Satzsegmentierung nach festen Regeln (ähnlich SRX): Trennung nach . ! ? … wenn danach Leerraum
/// und ein Großbuchstabe, eine Ziffer, ein Anführungszeichen oder ein Tag folgt.
/// Abkürzungen der Ausgangssprache und Ordnungszahlen trennen nicht. Tag-Paare werden nie zerrissen.
/// </summary>
public sealed partial class Segmenter
{
    private static readonly Dictionary<string, string[]> Abbreviations = new()
    {
        ["de"] = ["z.B", "z. B", "bzw", "ca", "Dr", "Prof", "Nr", "usw", "etc", "vgl", "ggf", "inkl", "evtl", "d.h", "d. h", "u.a", "u. a", "Abs", "Art", "Hr", "Fr", "Str", "Tel", "bspw", "max", "min", "zzgl", "S", "Kap", "Abb", "Tab", "Jh", "geb", "gest", "Mio", "Mrd", "St", "sog", "z.T", "o.Ä", "u.U"],
        ["en"] = ["Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "e.g", "i.e", "etc", "vs", "Inc", "Ltd", "Co", "Corp", "No", "Fig", "approx", "Dept", "St", "Mt", "Jan", "Feb", "Mar", "Apr", "Jun", "Jul", "Aug", "Sep", "Sept", "Oct", "Nov", "Dec"],
        ["pl"] = ["np", "tzn", "itd", "itp", "tj", "dr", "prof", "nr", "ul", "godz", "tys", "mln", "mld", "r", "w", "ok", "ds", "m.in", "pkt", "str", "tel", "zob", "por", "wg"],
        ["fr"] = ["M", "Mme", "Mlle", "Dr", "etc", "p.ex", "cf", "env", "St", "Ste", "n°"],
        ["es"] = ["Sr", "Sra", "Srta", "Dr", "Dra", "etc", "p.ej", "pág", "núm", "Ud", "Uds"],
        ["it"] = ["Sig", "Sig.ra", "Dott", "Prof", "ecc", "pag", "es", "n"],
    };

    private readonly HashSet<string> _abbrev;

    public Segmenter(string sourceLanguage)
    {
        Abbreviations.TryGetValue(Languages.Primary(sourceLanguage), out var list);
        _abbrev = new HashSet<string>(list ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public readonly record struct Piece(string Text, string TrailingWhitespace);

    [GeneratedRegex("[.!?…。！？]+[\"'”’»«)\\]]*(?:⟨/\\d+⟩)*(?=\\s)")]
    private static partial Regex EndRegex();

    /// <summary>Teilt einen Absatz in Sätze. Liefert führenden Leerraum separat.</summary>
    public (string Leading, List<Piece> Pieces) Split(string text)
    {
        var leadLen = text.Length - text.TrimStart().Length;
        var leading = text[..leadLen];
        var pieces = new List<Piece>();
        var depth = TagText.OpenDepth(text);
        var start = leadLen;

        foreach (Match m in EndRegex().Matches(text, leadLen))
        {
            var end = m.Index + m.Length;
            if (end <= start) continue;
            if (depth[end] != 0) continue;
            if (!NextStartsSentence(text, end)) continue;
            if (m.Value[0] == '.' && TagText.StripTags(m.Value).Length == 1 && IsAbbreviationOrOrdinal(text, start, m.Index)) continue;

            var wsEnd = end;
            while (wsEnd < text.Length && char.IsWhiteSpace(text[wsEnd])) wsEnd++;
            pieces.Add(new Piece(text[start..end], text[end..wsEnd]));
            start = wsEnd;
        }

        if (start < text.Length)
        {
            var rest = text[start..];
            var trimmed = rest.TrimEnd();
            pieces.Add(new Piece(trimmed, rest[trimmed.Length..]));
        }
        return (leading, pieces);
    }

    private static bool NextStartsSentence(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length) return false;
        var c = text[pos];
        return char.IsUpper(c) || char.IsDigit(c) || c is '"' or '„' or '“' or '«' or '»' or '\'' or '(' or '[' or '¿' or '¡' or TagText.Open
               || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter;
    }

    private bool IsAbbreviationOrOrdinal(string text, int segStart, int dotIndex)
    {
        // Wort vor dem Punkt bestimmen (darf selbst Punkte enthalten: "z.B", "d.h").
        var i = dotIndex;
        while (i > segStart && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] != TagText.Close) i--;
        var word = TagText.StripTags(text[i..dotIndex]).TrimStart('(', '"', '„', '«');
        if (word.Length == 0) return false;
        if (_abbrev.Contains(word)) return true;

        // "z. B." – zwei Wörter
        var j = i - 1;
        while (j > segStart && char.IsWhiteSpace(text[j])) j--;
        var k = j;
        while (k > segStart && !char.IsWhiteSpace(text[k - 1])) k--;
        if (k < j && _abbrev.Contains(text[k..(j + 1)] + " " + word)) return true;

        // Ordnungszahl ("am 3. Mai") und einzelne Initialen ("J. Smith")
        if (word.All(char.IsDigit) && word.Length <= 3) return true;
        if (word.Length == 1 && char.IsLetter(word[0])) return true;
        return false;
    }

    /// <summary>Baut die Segmente eines Absatzes auf.</summary>
    public void Segment(Paragraph paragraph, string text)
    {
        var (leading, pieces) = Split(text);
        paragraph.LeadingWhitespace = leading;
        paragraph.Segments = pieces
            .Where(p => p.Text.Length > 0)
            .Select(p => new Segment
            {
                Source = p.Text,
                TrailingWhitespace = p.TrailingWhitespace,
                Locked = !HasTranslatableText(p.Text),
            })
            .ToList();
        if (paragraph.Segments.Count == 0)
            paragraph.LeadingWhitespace = text;
    }

    /// <summary>Segmente ohne Buchstaben (nur Zahlen, Tags, Satzzeichen) gelten als nicht zu übersetzen.</summary>
    public static bool HasTranslatableText(string text) => TagText.StripTags(text).Any(char.IsLetter);
}
