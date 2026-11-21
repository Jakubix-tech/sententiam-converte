using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Wie verbreitet eine Formulierung ist: Artikel der Wikipedia der Sprache mit der genauen Wortfolge (kostenlos, immer)
/// und – mit einem Suchdienst – wie viele der ersten Treffer sie wörtlich enthalten; mit einem Suchdienst, der Länder
/// kennt (Brave, Tavily), nur Treffer aus dem Land der Sprache (pl-PL → Polen, es-MX → Mexiko).
/// </summary>
public static class PhraseFrequency
{
    /// <summary>Land aus dem Sprachcode („es-MX“ → „MX“); ohne Region das Hauptland der Sprache („pl“ → „PL“), sonst null.</summary>
    public static string? CountryOf(string language) =>
        language.Split('-', '_') is [_, var region, ..] && region.Length == 2 ? region.ToUpperInvariant()
            : WebPresence.CountriesFor(language).FirstOrDefault();

    /// <summary>Name des Landes auf Englisch (für die Anweisungen an die KI), z. B. „PL“ → „Poland“.</summary>
    public static string CountryName(string country)
    {
        try { return new System.Globalization.RegionInfo(country).EnglishName; }
        catch (ArgumentException) { return country; }
    }

    public static async Task<PhraseCount> CountAsync(AgentContext ctx, string variant, string language, CancellationToken ct = default)
    {
        ctx.Say($"[{language}] Wie oft: „{variant}“");
        long? articles = null;
        var examples = new List<UsageExample>();
        try
        {
            var (total, top) = await WikiApi.PhraseHitsAsync(language, variant, 3, ct);
            articles = total;
            examples.AddRange(top.Take(2).Select(r => new UsageExample(r.Url, r.Title, r.Snippet)));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            ctx.Say($"   Wikipedia nicht erreichbar: {ex.Message}");
        }

        int? hits = null, checkedResults = null;
        string? country = null;
        try
        {
            List<SearchResult>? results = null;
            if (ctx.Search is ICountrySearchProvider byCountry && CountryOf(language) is { } c)
            {
                results = (await byCountry.SearchPhraseAsync(variant, language, c, 0, ct)).Results;
                country = c;
            }
            else if (ctx.Search is { } search and not WikipediaSearch)
                results = await search.SearchAsync($"\"{variant}\"", language, 10, ct);
            if (results != null)
            {
                var exact = results.DistinctBy(r => r.Url).Where(r => UsageCheckAgent.ContainsPhrase(r.Title + " " + r.Snippet, variant)).ToList();
                (hits, checkedResults) = (exact.Count, results.DistinctBy(r => r.Url).Count());
                examples.AddRange(exact.Take(2).Select(r => new UsageExample(r.Url, r.Title, r.Snippet)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"   Suche fehlgeschlagen: {ex.Message}"); }

        var count = new PhraseCount(variant, articles, hits, checkedResults, examples, country);
        ctx.Say("   " + TranslationWebCheck.Describe(count));
        return count;
    }

    /// <summary>
    /// Welche Variante verbreiteter ist: zuerst die Treffer im Netz, sonst die Wikipedia-Artikel. Eine Variante gilt nur
    /// als klar vorn, wenn sie mindestens 3 Belege und doppelt so viele wie die nächste hat – sonst null (nicht aussagekräftig).
    /// </summary>
    public static PhraseCount? ClearlyMostCommon(IReadOnlyList<PhraseCount> counts)
    {
        static long Score(PhraseCount c) => c.WebHits ?? c.WikipediaArticles ?? 0;
        var ranked = counts.OrderByDescending(Score).ToList();
        if (ranked.Count == 0 || Score(ranked[0]) < 3) return null;
        return ranked.Count == 1 || Score(ranked[0]) >= 2 * Score(ranked[1]) ? ranked[0] : null;
    }
}
