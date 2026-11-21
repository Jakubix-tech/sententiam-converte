using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

/// <summary>Wie verbreitet eine Variante in einem Land ist.</summary>
public enum PresenceLevel { None, Rare, Present, Common }

/// <summary>
/// Ergebnis für eine Variante in einem Land: geprüfte Treffer, davon mit der genauen Wortfolge, davon auf Seiten des
/// Landes (Länderendung wie .at), und ob es weitere Treffer gäbe.
/// </summary>
public sealed record PresenceCell(string Variant, string Country, int Checked, int Exact, int OnCountryDomains, bool More,
    List<UsageExample> Examples, string? Error = null)
{
    /// <summary>„0“, „7“ oder „≥ 40“ (alle geprüften Treffer passen und es gäbe weitere).</summary>
    public string CountText => Error != null ? "–" : More && Exact == Checked && Checked > 0 ? $"≥ {Exact}" : Exact.ToString();

    public PresenceLevel Level => Error != null ? PresenceLevel.None : Exact switch
    {
        0 => PresenceLevel.None,
        <= 3 => PresenceLevel.Rare,
        _ when Exact < Math.Max(8, Checked / 2) => PresenceLevel.Present,
        _ => PresenceLevel.Common,
    };
}

/// <summary>
/// Verbreitung im Netz: Wird ein Ausdruck in bestimmten Ländern überhaupt so geschrieben – und wie oft? Je Variante und
/// Land eine Suche nach der genauen Wortfolge über eine offizielle Such-Schnittstelle mit Länderfilter (Brave, Tavily).
/// Gezählt werden nur Treffer, die die Wortfolge wörtlich enthalten; geschätzte Gesamtzahlen gibt es nicht.
/// </summary>
public static class WebPresence
{
    /// <summary>Übliche Länder einer Sprache (Vorschlag für die Auswahl).</summary>
    public static IReadOnlyList<string> CountriesFor(string language) => Languages.Primary(language) switch
    {
        "de" => ["DE", "AT", "CH"],
        "fr" => ["FR", "BE", "CH", "CA"],
        "it" => ["IT", "CH"],
        "en" => ["GB", "US", "IE", "CA", "AU"],
        "es" => ["ES", "MX", "AR"],
        "pt" => ["PT", "BR"],
        "nl" => ["NL", "BE"],
        "sv" => ["SE", "FI"],
        "rm" => ["CH"],
        _ when language.Contains('-') && language.Split('-')[1].Length == 2 => [language.Split('-')[1].ToUpperInvariant()],
        // sonst das Land aus der Sprachliste (cs → CZ, uk → UA, el → GR), nicht der Sprachcode selbst
        var p => [Languages.Common.Select(l => l.Code.Split('-')).FirstOrDefault(c => c.Length == 2 && c[1].Length == 2 && Languages.Primary(c[0]) == p)?[1]
                  ?? p.ToUpperInvariant()],
    };

    /// <summary>So viele Anfragen kostet eine Prüfung.</summary>
    public static int Requests(int variants, int countries, int pages) => variants * countries * pages;

    public static async Task<List<PresenceCell>> CheckAsync(ICountrySearchProvider search, IReadOnlyList<string> variants, string language,
        IReadOnlyList<string> countries, int pages, IProgress<string>? log = null, CancellationToken ct = default)
    {
        pages = Math.Clamp(pages, 1, search.MaxPages);
        var cells = new List<PresenceCell>();
        foreach (var variant in variants.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var country in countries.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length == 2).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                log?.Report($"Suche „{variant}“ in {country} …");
                var results = new List<SearchResult>();
                var more = false;
                string? error = null;
                for (var page = 0; page < pages; page++)
                {
                    try
                    {
                        var p = await search.SearchPhraseAsync(variant, language, country, page, ct);
                        results.AddRange(p.Results);
                        more = p.More;
                        if (!p.More || p.Results.Count == 0) break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or Privacy.OutboundDeclinedException)
                    {
                        error = ex.Message;
                        break;
                    }
                }
                var exact = results.DistinctBy(r => r.Url)
                    .Where(r => UsageCheckAgent.ContainsPhrase(r.Title + " " + r.Snippet, variant)).ToList();
                var tld = "." + country.ToLowerInvariant() + (country == "GB" ? "|.uk" : "");
                var onCountry = exact.Count(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var u)
                                                 && tld.Split('|').Any(t => u.Host.EndsWith(t, StringComparison.OrdinalIgnoreCase)));
                cells.Add(new PresenceCell(variant, country, results.DistinctBy(r => r.Url).Count(), exact.Count, onCountry, more,
                    exact.Take(5).Select(r => new UsageExample(r.Url, r.Title, r.Snippet)).ToList(), error));
            }
        return cells;
    }
}
