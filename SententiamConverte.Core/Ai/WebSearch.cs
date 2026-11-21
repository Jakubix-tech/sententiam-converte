using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>Eine Seite Suchergebnisse; <see cref="More"/>: es gibt weitere.</summary>
public sealed record SearchPage(List<SearchResult> Results, bool More);

/// <summary>Suche mit Länderfilter (ISO-Code wie „AT“) und Seiten – für die Verbreitung von Ausdrücken je Land.</summary>
public interface ICountrySearchProvider
{
    /// <summary>Höchstens so viele Seiten (je 20 Treffer) lassen sich abrufen.</summary>
    int MaxPages { get; }

    /// <summary><paramref name="phrase"/> wird als genaue Wortfolge gesucht.</summary>
    Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default);
}

public interface ISearchProvider
{
    string Name { get; }
    Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default);

    /// <summary>Suche nur auf bestimmten Seiten. Standard: „site:“-Operatoren, die Brave und SearXNG verstehen.</summary>
    Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default) =>
        SearchAsync($"{query} ({string.Join(" OR ", sites.Take(8).Select(x => "site:" + x))})", language, max, ct);
}

public static class SearchProviderFactory
{
    /// <summary>
    /// Die eingestellte Websuche. Ist sie nicht nutzbar (kein Schlüssel, keine Adresse, Claude ohne Schlüssel),
    /// sucht das Programm kostenlos in Wikipedia, damit Recherchen nie ins Leere laufen. „Keine“ bleibt keine.
    /// </summary>
    /// <remarks>
    /// Sind mehrere Dienste angekreuzt, fragt eine <see cref="MultiSearch"/> alle gleichzeitig. Nicht nutzbare (ohne
    /// Schlüssel oder Adresse) fallen weg; bleibt keiner übrig, sucht Wikipedia.
    /// </remarks>
    public static ISearchProvider? Create(AiSettings settings)
    {
        var sources = settings.Search.ActiveSources();
        if (sources.Count == 0) return null;
        var providers = sources.Select(s => CreateOne(s, settings)).OfType<ISearchProvider>().DistinctBy(p => p.Name).ToList();
        if (providers.Count == 0) return new WikipediaSearch();
        if (providers.Count == 1) return providers[0];
        return providers.Any(p => p is ICountrySearchProvider) ? new MultiCountrySearch(providers) : new MultiSearch(providers);
    }

    /// <summary>Ein einzelner Dienst; null, wenn er nicht nutzbar ist (kein Schlüssel, keine Adresse, kein Claude-Zugang).</summary>
    public static ISearchProvider? CreateOne(SearchSource s, AiSettings settings) => s.Kind switch
    {
        SearchProviderKind.SearXng when s.BaseUrl.Trim().Length > 0 => new SearXngSearch(s.BaseUrl),
        SearchProviderKind.Brave when s.ResolveApiKey().Length > 0 => new BraveSearch(s.ResolveApiKey()),
        SearchProviderKind.Tavily when s.ResolveApiKey().Length > 0 => new TavilySearch(s.ResolveApiKey()),
        SearchProviderKind.ClaudeWebSearch when settings.Get(s.ClaudeProviderId ?? settings.Search.ClaudeProviderId) is { Kind: AiProviderKind.Anthropic } p
                                                && p.ResolveApiKey().Length > 0
            => new ClaudeSearch(new ClaudeChatModel(p)),
        SearchProviderKind.Wikipedia => new WikipediaSearch(),
        _ => null,
    };

    /// <summary>Name der Suche, die <see cref="Create"/> liefern würde (ohne sie anzulegen) – für die Anzeige.</summary>
    public static string NameFor(AiSettings settings)
    {
        var sources = settings.Search.ActiveSources();
        if (sources.Count == 0) return "keine Websuche";
        var names = sources.Where(s => s.Kind == SearchProviderKind.Wikipedia || Usable(s, settings)).Select(s => NameOf(s.Kind)).Distinct().ToList();
        return names.Count == 0 ? "Wikipedia" : string.Join(" + ", names);
    }

    private static bool Usable(SearchSource s, AiSettings settings) => s.Kind switch
    {
        SearchProviderKind.SearXng => s.BaseUrl.Trim().Length > 0,
        SearchProviderKind.Brave or SearchProviderKind.Tavily => s.ResolveApiKey().Length > 0,
        SearchProviderKind.ClaudeWebSearch => settings.Get(s.ClaudeProviderId ?? settings.Search.ClaudeProviderId) is { Kind: AiProviderKind.Anthropic } p && p.ResolveApiKey().Length > 0,
        _ => true,
    };

    public static string NameOf(SearchProviderKind kind) => kind switch
    {
        SearchProviderKind.SearXng => "SearXNG",
        SearchProviderKind.Brave => "Brave Search",
        SearchProviderKind.Tavily => "Tavily",
        SearchProviderKind.ClaudeWebSearch => "Claude-Websuche",
        SearchProviderKind.Wikipedia => "Wikipedia",
        _ => "keine Websuche",
    };

    public const string RobotsName = "SententiamConverte";
    // Wikimedia verlangt eine Kennung mit Kontaktmöglichkeit; nur ASCII (HTTP-Kopfzeile)
    public const string UserAgent = "SententiamConverte/1.2 (+https://github.com/Jakubix-tech/sententiam-converte; Uebersetzungswerkzeug; beachtet robots.txt und TDM-Vorbehalte)";

    internal static HttpClient NewHttp()
    {
        // Datenausgang: Suchanfragen und Seitenabrufe erscheinen im Protokoll (und werden auf Wunsch vorher gezeigt)
        var http = new HttpClient(new Privacy.OutboundHandler(Privacy.OutboundKind.Web, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }))
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Ehrliche Kennung statt Browser-Tarnung; Seitenbetreiber können uns per robots.txt ausschließen.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}

/// <summary>
/// Mehrere Suchdienste gleichzeitig – wie ein Mensch, der in verschiedenen Quellen sucht: alle werden parallel gefragt,
/// die Treffer abwechselnd gemischt (jede Adresse nur einmal). Fällt ein Dienst aus (kein Netz, Docker angehalten,
/// Kontingent aufgebraucht), liefern die anderen; nur wenn alle scheitern, gilt die Suche als gescheitert.
/// </summary>
public class MultiSearch(IReadOnlyList<ISearchProvider> providers) : ISearchProvider
{
    public IReadOnlyList<ISearchProvider> Providers => providers;

    public string Name => string.Join(" + ", providers.Select(p => p.Name));

    /// <summary>Welche Dienste bei der letzten Suche scheiterten (für das Protokoll).</summary>
    public IReadOnlyList<string> LastFailures { get; private set; } = [];

    public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default) =>
        MergeAsync(p => p.SearchAsync(query, language, max, ct), max, ct);

    public Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default) =>
        MergeAsync(p => p.SearchInSitesAsync(query, language, sites, max, ct), max, ct);

    private async Task<List<SearchResult>> MergeAsync(Func<ISearchProvider, Task<List<SearchResult>>> query, int max, CancellationToken ct)
    {
        var failures = new List<string>();
        var lists = await Task.WhenAll(providers.Select(async p =>
        {
            try { return await query(p); }
            catch (Exception ex) when (ex is HttpRequestException or AiException or System.Text.Json.JsonException
                                           or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lock (failures) failures.Add($"{p.Name}: {ex.Message}");
                return null;
            }
        }));
        LastFailures = failures;
        if (lists.All(l => l == null)) throw new AiException("Keine Websuche hat geantwortet – " + string.Join("; ", failures));
        var merged = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var available = lists.Where(l => l != null).Select(l => l!).ToList();
        for (var i = 0; available.Any(l => i < l.Count); i++)
            foreach (var list in available.Where(l => i < l.Count))
                if (seen.Add(Key(list[i].Url))) merged.Add(list[i]);
        return merged.Take(max + providers.Count).ToList();
    }

    /// <summary>Gleiche Seite, auch mit http/https, „www.“ oder Schrägstrich am Ende.</summary>
    internal static string Key(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host) + u.PathAndQuery.TrimEnd('/')
            : url;
}

/// <summary>Mehrfachsuche mit einem Dienst, der nach Land suchen kann (Brave, Tavily): die Länderzählung macht dieser.</summary>
public sealed class MultiCountrySearch(IReadOnlyList<ISearchProvider> providers) : MultiSearch(providers), ICountrySearchProvider
{
    private ICountrySearchProvider Country => providers.OfType<ICountrySearchProvider>().First();

    public int MaxPages => Country.MaxPages;

    public Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default) =>
        Country.SearchPhraseAsync(phrase, language, country, page, ct);
}

public sealed class SearXngSearch(string baseUrl) : ISearchProvider
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    public string Name => "SearXNG";

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        try
        {
            return await QueryAsync(query, language, max, ct);
        }
        catch (HttpRequestException) when (LocalSearXng.IsLocal(baseUrl))
        {
            // Die eigene Instanz dieses Programms: Docker bzw. Container ruhen gerade – starten und noch einmal fragen
            if (!await LocalSearXng.EnsureRunningAsync(ct: ct)) throw;
            return await QueryAsync(query, language, max, ct);
        }
    }

    private async Task<List<SearchResult>> QueryAsync(string query, string language, int max, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}&language={Uri.EscapeDataString(language)}";
        var json = await Http.GetFromJsonAsync<JsonObject>(url, ct);
        return (json?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", (string?)r?["content"] ?? ""))
            .Take(max).ToList();
    }
}

public sealed class BraveSearch(string apiKey) : ISearchProvider, ICountrySearchProvider
{
    public int MaxPages => 10;

    /// <summary>Genaue Wortfolge in einem Land (country, search_lang), 20 Treffer je Seite.</summary>
    public async Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default)
    {
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString("\"" + phrase + "\"")}&count=20&offset={page}" +
                  $"&country={Uri.EscapeDataString(country.ToUpperInvariant())}&search_lang={Languages.Primary(language)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Subscription-Token", apiKey);
        req.Headers.Add("Accept", "application/json");
        using var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var results = (json?["web"]?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", HtmlText.Strip((string?)r?["description"] ?? ""))).ToList();
        var more = json?["query"]?["more_results_available"] is JsonValue m && m.TryGetValue<bool>(out var b) && b;
        return new SearchPage(results, more);
    }

    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    public string Name => "Brave Search";

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={max}&search_lang={Languages.Primary(language)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Subscription-Token", apiKey);
        req.Headers.Add("Accept", "application/json");
        using var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return (json?["web"]?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", HtmlText.Strip((string?)r?["description"] ?? "")))
            .Take(max).ToList();
    }
}

public sealed class TavilySearch(string apiKey) : ISearchProvider, ICountrySearchProvider
{
    public int MaxPages => 1;

    /// <summary>Genaue Wortfolge (exact_match) in einem Land (englischer Ländername), höchstens 20 Treffer.</summary>
    public async Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default)
    {
        string? name = null;
        try { name = new System.Globalization.RegionInfo(country).EnglishName.ToLowerInvariant(); }
        catch (ArgumentException) { }
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query = "\"" + phrase + "\"", max_results = 20, search_depth = "basic", topic = "general", country = name, exact_match = true }),
        };
        req.Headers.Add("Authorization", "Bearer " + apiKey);
        using var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var results = (json?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", (string?)r?["content"] ?? "")).ToList();
        return new SearchPage(results, false);
    }

    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    private string[]? _sites;
    public string Name => "Tavily";

    public async Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default)
    {
        _sites = sites.ToArray();
        try { return await SearchAsync(query, language, max, ct); }
        finally { _sites = null; }
    }

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query, max_results = max, search_depth = "basic", include_domains = _sites }),
        };
        req.Headers.Add("Authorization", "Bearer " + apiKey);
        using var response = await Http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return (json?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", (string?)r?["content"] ?? ""))
            .Take(max).ToList();
    }
}

/// <summary>Lässt Claude mit seiner eingebauten Websuche suchen und die Treffer als JSON-Liste zurückgeben.</summary>
public sealed class ClaudeSearch(ClaudeChatModel claude) : ISearchProvider
{
    public string Name => "Claude-Websuche";

    public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default) =>
        Run(query, language, max, null, ct);

    public Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default) =>
        Run(query, language, max, sites, ct);

    private async Task<List<SearchResult>> Run(string query, string language, int max, IReadOnlyList<string>? sites, CancellationToken ct)
    {
        var answer = await claude.CompleteWithWebSearchAsync(
            "You are a search backend. Use the web_search tool, then output only JSON.",
            $$"""
            Search the web for: {{query}}
            Prefer pages written in {{Languages.DisplayName(language)}} ({{language}}).
            Return up to {{max}} results as JSON: [{"title": "...", "url": "...", "snippet": "verbatim sentence from the page that contains the searched words"}]
            Only include results you actually found. No commentary.
            """, ct, sites);
        return JsonExtract.Parse<List<SearchResult>>(answer).Take(max).ToList();
    }
}

/// <summary>
/// Lädt Webseiten und macht daraus lesbaren Text – nur, wenn der Betreiber es erlaubt:
/// robots.txt wird beachtet, und Seiten mit Nutzungsvorbehalt für Text und Data Mining
/// (§ 44b Abs. 3 UrhG / Art. 4 DSM-Richtlinie: Header oder Meta „tdm-reservation“, „noai“) werden übersprungen.
/// </summary>
public interface IPageFetcher
{
    Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default);
    string? LastSkipReason { get; }

    /// <summary>
    /// Sprachversionen der zuletzt gelesenen Seite (hreflang → Adresse), wie sie die Seite selbst nennt – so wie ein
    /// Mensch auf einer Herstellerseite die Sprache umschaltet; leer, wenn die Seite keine nennt.
    /// </summary>
    IReadOnlyDictionary<string, string> LastLanguageVersions => new Dictionary<string, string>();
}

public sealed partial class PageFetcher : IPageFetcher
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    private static readonly Dictionary<string, RobotsRules> Robots = new();
    private static readonly SemaphoreSlim RobotsLock = new(1, 1);
    private static readonly Dictionary<string, DateTime> NextVisit = new();

    /// <summary>Längste Wartezeit, die ein Crawl-delay auslösen darf; verlangt eine Seite mehr, wird später erneut gelesen.</summary>
    internal static double MaxWaitSeconds = 20;

    /// <summary>Größte PDF-Datei, die gelesen wird.</summary>
    private const long MaxPdfBytes = 40_000_000;

    /// <summary>Warum die letzte Seite nicht gelesen wurde (für das Protokoll).</summary>
    public string? LastSkipReason { get; private set; }

    public IReadOnlyDictionary<string, string> LastLanguageVersions { get; private set; } = new Dictionary<string, string>();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex(@"(?<![\w-])(rel|hreflang|href)\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRegex();

    /// <summary>&lt;link rel="alternate" hreflang="en" href="…"&gt; einer Seite (ohne „x-default“), Adressen absolut.</summary>
    internal static Dictionary<string, string> LanguageVersions(string html, Uri baseUri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in LinkTagRegex().Matches(html))
        {
            var attributes = AttributeRegex().Matches(tag.Value).GroupBy(a => a.Groups[1].Value.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Value.Trim()));
            if (!attributes.TryGetValue("rel", out var rel) || !rel.Contains("alternate", StringComparison.OrdinalIgnoreCase)) continue;
            if (!attributes.TryGetValue("hreflang", out var lang) || lang.Length == 0 || lang.Equals("x-default", StringComparison.OrdinalIgnoreCase)) continue;
            if (attributes.TryGetValue("href", out var href) && Uri.TryCreate(baseUri, href, out var abs) && abs.Scheme is "http" or "https")
                result.TryAdd(lang.ToLowerInvariant(), abs.AbsoluteUri);
        }
        return result;
    }

    /// <summary>Die Fassung einer Sprache: genau („en-gb“), sonst die Hauptsprache („en“), sonst eine Landesvariante davon.</summary>
    public static string? VersionFor(IReadOnlyDictionary<string, string> versions, string language)
    {
        var exact = language.ToLowerInvariant();
        var primary = Languages.Primary(language);
        return versions.GetValueOrDefault(exact) ?? versions.GetValueOrDefault(primary)
               ?? versions.FirstOrDefault(v => v.Key.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase)).Value;
    }

    [GeneratedRegex("<meta[^>]+name\\s*=\\s*[\"'](tdm-reservation|robots)[\"'][^>]*content\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    public async Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default)
    {
        LastSkipReason = null;
        LastLanguageVersions = new Dictionary<string, string>();
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Skip("keine Webadresse");
            var cellar = LawSources.CellarFor(uri);
            var target = cellar?.Uri ?? uri;
            if (await PermitAsync(target, ct) is { } refused) return Skip(refused);

            using var response = await SendAsync(target, cellar?.Language, ct);
            if (!response.IsSuccessStatusCode) return Skip($"HTTP {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.Accepted) return Skip("die Seite verlangt einen Browser (Schutz vor Programmen)");
            if (response.Headers.TryGetValues("tdm-reservation", out var tdm) && tdm.Any(v => v.Trim() == "1"))
                return Skip("Text-und-Data-Mining-Vorbehalt (Header)");
            if (response.Headers.TryGetValues("X-Robots-Tag", out var xr) && xr.Any(v => v.Contains("noai", StringComparison.OrdinalIgnoreCase)))
                return Skip("Vorbehalt „noai“ (Header)");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("pdf") || (type is "" or "application/octet-stream" && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                if (response.Content.Headers.ContentLength > MaxPdfBytes) return Skip("PDF zu groß");
                var pdf = PdfText(await response.Content.ReadAsByteArrayAsync(ct), maxChars);
                return pdf.Length > 0 ? pdf : Skip("PDF ohne lesbaren Text (Scan?)");
            }
            if (!type.Contains("html") && !type.Contains("text")) return Skip("kein Text");

            var html = await response.Content.ReadAsStringAsync(ct);
            foreach (Match m in MetaRegex().Matches(html))
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                var content = m.Groups[2].Value.ToLowerInvariant();
                if (name == "tdm-reservation" && content.Trim() == "1") return Skip("Text-und-Data-Mining-Vorbehalt (Meta)");
                if (name == "robots" && (content.Contains("noai") || content.Contains("noindex"))) return Skip($"Vorbehalt „{content}“ (Meta)");
            }
            LastLanguageVersions = LanguageVersions(html, response.RequestMessage?.RequestUri ?? target);
            var text = HtmlText.ToPlain(html);
            if (text.Length == 0) return Skip("kein lesbarer Text");
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Eine einzelne kaputte Seite (falscher Zeichensatz, Zeitüberschreitung …) darf den Agenten nicht abbrechen
            return Skip(ex.Message);
        }
    }

    private string? Skip(string reason)
    {
        LastSkipReason = reason;
        return null;
    }

    /// <summary>GET mit Weiterleitungen; eine Weiterleitung von HTTPS auf HTTP (der .NET nicht folgt) bleibt verschlüsselt.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Uri uri, string? cellarLanguage, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (cellarLanguage != null)
            {
                request.Headers.Accept.ParseAdd("application/xhtml+xml");
                request.Headers.AcceptLanguage.ParseAdd(cellarLanguage);
            }
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < 5)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme == Uri.UriSchemeHttp && uri.Scheme == Uri.UriSchemeHttps) next = new UriBuilder(next) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                response.Dispose();
                uri = next;
                continue;
            }
            return response;
        }
    }

    /// <summary>Text einer PDF-Datei in Leserichtung, Seite für Seite.</summary>
    internal static string PdfText(byte[] bytes, int maxChars)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
            if (sb.Length >= maxChars) break;
        }
        // am Zeilenende getrennte Wörter wieder zusammensetzen („indywidual-\nnych“)
        var text = Regex.Replace(sb.ToString(), @"(\p{Ll})-[ \t]*\r?\n[ \t]*(\p{Ll})", "$1$2").Trim();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    /// <summary>
    /// Darf das Programm diese Adresse abrufen? Prüft robots.txt und hält den dort gewünschten Abstand zwischen zwei
    /// Abrufen ein (Crawl-delay). Gilt auch für Schnittstellen. Null = erlaubt, sonst der Grund der Ablehnung.
    /// </summary>
    public static async Task<string?> PermitAsync(Uri uri, CancellationToken ct = default)
    {
        var rules = await RulesFor(uri, ct);
        if (!rules.Allows(uri.PathAndQuery)) return "robots.txt untersagt den Abruf";
        if (rules.CrawlDelay <= 0) return null;
        var key = uri.GetLeftPart(UriPartial.Authority);
        TimeSpan wait;
        lock (NextVisit)
        {
            var now = DateTime.UtcNow;
            var next = NextVisit.TryGetValue(key, out var n) && n > now ? n : now;
            wait = next - now;
            if (wait.TotalSeconds > MaxWaitSeconds)
                return $"robots.txt verlangt {rules.CrawlDelay:0} s Abstand zwischen zwei Abrufen – bitte später erneut";
            NextVisit[key] = next.AddSeconds(rules.CrawlDelay);
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        return null;
    }

    private static async Task<RobotsRules> RulesFor(Uri uri, CancellationToken ct)
    {
        var key = uri.GetLeftPart(UriPartial.Authority);
        await RobotsLock.WaitAsync(ct);
        try
        {
            if (Robots.TryGetValue(key, out var cached)) return cached;
            RobotsRules rules;
            try
            {
                using var response = await Http.GetAsync(key + "/robots.txt", ct);
                rules = response.IsSuccessStatusCode
                    ? RobotsRules.Parse(await response.Content.ReadAsStringAsync(ct), SearchProviderFactory.RobotsName)
                    // 401/403: Abruf nicht erwünscht; sonst (404 usw.) gibt es keine Einschränkung
                    : (int)response.StatusCode is 401 or 403 ? RobotsRules.DenyAll : RobotsRules.AllowAll;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { rules = RobotsRules.AllowAll; }
            Robots[key] = rules;
            return rules;
        }
        finally { RobotsLock.Release(); }
    }
}

/// <summary>Minimaler robots.txt-Leser (RFC 9309): passende User-agent-Gruppe, längste Regel gewinnt.</summary>
public sealed class RobotsRules
{
    private readonly List<(bool Allow, string Path)> _rules;

    private RobotsRules(List<(bool, string)> rules, double crawlDelay = 0)
    {
        _rules = rules;
        CrawlDelay = crawlDelay;
    }

    public static RobotsRules AllowAll { get; } = new([]);
    public static RobotsRules DenyAll { get; } = new([(false, "/")]);

    /// <summary>Gewünschter Abstand zwischen zwei Abrufen in Sekunden (Crawl-delay; 0 = keiner).</summary>
    public double CrawlDelay { get; }

    private sealed class Group
    {
        public readonly List<string> Agents = new();
        public readonly List<(bool, string)> Rules = new();
        public double Delay;
    }

    public static RobotsRules Parse(string text, string agent)
    {
        var groups = new List<Group>();
        Group? group = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (!lastWasAgent) groups.Add(group = new Group());
                group!.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if ((field == "allow" || field == "disallow") && group != null)
            {
                lastWasAgent = false;
                if (value.Length > 0) group.Rules.Add((field == "allow", value));
            }
            else if (field == "crawl-delay" && group != null)
            {
                lastWasAgent = false;
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
                    group.Delay = d;
            }
            else lastWasAgent = false;
        }
        var own = groups.Where(g => g.Agents.Any(a => a != "*" && agent.ToLowerInvariant().Contains(a))).ToList();
        var chosen = own.Count > 0 ? own : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList(), chosen.Select(g => g.Delay).DefaultIfEmpty(0).Max());
    }

    public bool Allows(string path)
    {
        var best = _rules.Where(r => Matches(path, r.Path)).OrderByDescending(r => r.Path.Length).ThenByDescending(r => r.Allow).FirstOrDefault();
        return best.Path == null || best.Allow;
    }

    private static bool Matches(string path, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\$", "$");
        return Regex.IsMatch(path, regex);
    }
}

public static partial class HtmlText
{
    [GeneratedRegex("<(script|style|noscript|svg|nav|footer|aside|template)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBlocks();

    [GeneratedRegex("<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("[ \\t\\u00A0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex("\\n\\s*\\n+")]
    private static partial Regex BlankLines();

    public static string Strip(string html) => WebUtility.HtmlDecode(Tags().Replace(html, "")).Trim();

    public static string ToPlain(string html)
    {
        var body = Regex.Match(html, "<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (body.Success) html = body.Groups[1].Value;
        html = NoiseBlocks().Replace(html, " ");
        html = BlockEnds().Replace(html, "\n");
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n");
        // Kurze Menüzeilen weglassen, Fließtext behalten
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length >= 40 || l.EndsWith('.'));
        return string.Join("\n", lines);
    }
}
