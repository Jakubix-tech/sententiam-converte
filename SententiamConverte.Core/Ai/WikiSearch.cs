using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein Wikipedia- oder Wiktionary-Artikel mit seinen Sprachversionen (Sprachcode → Titel).</summary>
public sealed record WikiArticle(string Title, string Url, string Text, Dictionary<string, string> Versions);

/// <summary>
/// Wikipedia und Wiktionary über die offizielle MediaWiki-Schnittstelle (/w/api.php) – kostenlos, ohne Schlüssel,
/// in jeder Sprache. Für Übersetzer besonders nützlich: die Sprachversionen eines Artikels nennen den
/// Fachbegriff in den anderen Sprachen. Inhalte stehen unter CC BY-SA; zitiert werden nur kurze Belege.
/// </summary>
public static class WikiApi
{
    /// <summary>Für Tests: andere Adresse statt https://{Sprache}.{wikipedia|wiktionary}.org.</summary>
    internal static Func<string, string, string> Host { get; set; } = (site, lang) => $"https://{lang}.{site}.org";

    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();

    /// <summary>Wiki-Sprachcode: de-DE → de, nb → no, zh-TW → zh.</summary>
    public static string Code(string language) => Languages.Primary(language) switch
    {
        "nb" or "nn" => "no",
        var p => p,
    };

    private static string Site(string? source) => source?.Trim().ToLowerInvariant() is "wiktionary" ? "wiktionary" : "wikipedia";

    public static async Task<List<SearchResult>> SearchAsync(string source, string language, string query, int max, CancellationToken ct = default)
    {
        var site = Site(source);
        var host = Host(site, Code(language));
        var json = await Http.GetFromJsonAsync<JsonObject>(
            $"{host}/w/api.php?action=query&list=search&format=json&srlimit={Math.Clamp(max, 1, 20)}&srsearch={Uri.EscapeDataString(query)}", ct);
        return (json?["query"]?["search"]?.AsArray() ?? [])
            .Select(r => (string?)r?["title"] ?? "")
            .Zip((json?["query"]?["search"]?.AsArray() ?? []).Select(r => HtmlText.Strip((string?)r?["snippet"] ?? "")))
            .Where(x => x.First.Length > 0)
            .Select(x => new SearchResult(x.First, $"{host}/wiki/{Uri.EscapeDataString(x.First.Replace(' ', '_'))}", x.Second))
            .ToList();
    }

    /// <summary>
    /// Wie viele Artikel der Wikipedia einer Sprache die genaue Wortfolge enthalten (Gesamtzahl der Suche) und die ersten
    /// Treffer mit Ausschnitt – ein kostenloses Maß für die Verbreitung einer Formulierung.
    /// </summary>
    public static async Task<(long Total, List<SearchResult> Top)> PhraseHitsAsync(string language, string phrase, int top = 3, CancellationToken ct = default)
    {
        var host = Host("wikipedia", Code(language));
        var query = "\"" + phrase.Replace("\"", "").Trim() + "\"";
        var json = await Http.GetFromJsonAsync<JsonObject>(
            $"{host}/w/api.php?action=query&list=search&format=json&srinfo=totalhits&srprop=snippet&srlimit={Math.Clamp(top, 1, 10)}&srsearch={Uri.EscapeDataString(query)}", ct);
        var total = (long?)json?["query"]?["searchinfo"]?["totalhits"] ?? 0;
        var results = (json?["query"]?["search"]?.AsArray() ?? [])
            .Select(r => ((string?)r?["title"] ?? "", HtmlText.Strip((string?)r?["snippet"] ?? "")))
            .Where(x => x.Item1.Length > 0)
            .Select(x => new SearchResult(x.Item1, $"{host}/wiki/{Uri.EscapeDataString(x.Item1.Replace(' ', '_'))}", x.Item2))
            .ToList();
        return (total, results);
    }

    /// <summary>Sprachversionen mehrerer Artikel auf einmal (Titel → Sprachcode → Titel).</summary>
    public static async Task<Dictionary<string, Dictionary<string, string>>> VersionsAsync(string source, string language,
        IReadOnlyList<string> titles, CancellationToken ct = default)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        if (titles.Count == 0) return result;
        var host = Host(Site(source), Code(language));
        var json = await Http.GetFromJsonAsync<JsonObject>(
            $"{host}/w/api.php?action=query&format=json&prop=langlinks&lllimit=500&titles={Uri.EscapeDataString(string.Join("|", titles))}", ct);
        foreach (var page in (json?["query"]?["pages"] as JsonObject)?.Select(kv => kv.Value) ?? [])
        {
            var title = (string?)page?["title"];
            if (title == null) continue;
            result[title] = (page?["langlinks"]?.AsArray() ?? [])
                .Select(l => ((string?)l?["lang"] ?? "", (string?)l?["*"] ?? ""))
                .Where(x => x.Item1.Length > 0 && x.Item2.Length > 0)
                .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
        }
        return result;
    }

    /// <summary>Artikeltext (gekürzt) und die Titel der Sprachversionen.</summary>
    public static async Task<WikiArticle?> ArticleAsync(string source, string language, string title, int maxChars, CancellationToken ct = default)
    {
        var site = Site(source);
        var host = Host(site, Code(language));
        var json = await Http.GetFromJsonAsync<JsonObject>(
            $"{host}/w/api.php?action=query&format=json&redirects=1&prop=extracts|langlinks&explaintext=1&lllimit=500&titles={Uri.EscapeDataString(title)}", ct);
        var page = (json?["query"]?["pages"] as JsonObject)?.Select(kv => kv.Value).FirstOrDefault();
        if (page == null || page["missing"] != null) return null;
        var realTitle = (string?)page["title"] ?? title;
        var text = ((string?)page["extract"] ?? "").Trim();
        if (text.Length > maxChars) text = text[..maxChars];
        var versions = (page["langlinks"]?.AsArray() ?? [])
            .Select(l => ((string?)l?["lang"] ?? "", (string?)l?["*"] ?? ""))
            .Where(x => x.Item1.Length > 0 && x.Item2.Length > 0)
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
        return new WikiArticle(realTitle, $"{host}/wiki/{Uri.EscapeDataString(realTitle.Replace(' ', '_'))}", text, versions);
    }
}

/// <summary>Websuche ohne Schlüssel: durchsucht Wikipedia in der gewünschten Sprache.</summary>
public sealed class WikipediaSearch : ISearchProvider
{
    public string Name => "Wikipedia";

    public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default) =>
        WikiApi.SearchAsync("wikipedia", language, query.Replace("\"", ""), max, ct);

    /// <summary>„site:“ versteht die Wikipedia-Suche nicht – gesucht wird dort einfach nach dem Begriff.</summary>
    public Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default) =>
        SearchAsync(query, language, max, ct);
}
