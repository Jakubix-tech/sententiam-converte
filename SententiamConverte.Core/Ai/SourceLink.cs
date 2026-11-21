namespace SententiamConverte.Core.Ai;

/// <summary>Links zum Selbstprüfen: Adresse, die im Browser direkt zur zitierten Stelle springt.</summary>
public static class SourceLink
{
    /// <summary>
    /// Adresse mit Textfragment („#:~:text=Anfang,Ende“): Der Browser springt zur zitierten Stelle und hebt sie hervor.
    /// Anfang und Ende aus je vier Wörtern, damit kleine Abweichungen im Zitat (Leerraum, Satzzeichen dazwischen) nicht stören.
    /// </summary>
    public static string WithTextFragment(string url, string? quote)
    {
        var words = (quote ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('„', '“', '”', '"', '«', '»', '(', ')', '[', ']')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || url.Contains(":~:")) return url;
        static string Part(IEnumerable<string> w) => Uri.EscapeDataString(string.Join(' ', w)).Replace("-", "%2D");
        var text = words.Count <= 8 ? Part(words) : Part(words.Take(4)) + "," + Part(words.TakeLast(4));
        return url + (url.Contains('#') ? ":~:text=" : "#:~:text=") + text;
    }
}
