using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

public sealed record SearchResult(string Title, string Url, string Snippet);

public interface ISearchProvider
{
    string Name { get; }
    Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default);
}

public static class SearchProviderFactory
{
    public static ISearchProvider? Create(AiSettings settings) => settings.Search.Kind switch
    {
        SearchProviderKind.SearXng => new SearXngSearch(settings.Search.BaseUrl),
        SearchProviderKind.Brave => new BraveSearch(settings.Search.ResolveApiKey()),
        SearchProviderKind.Tavily => new TavilySearch(settings.Search.ResolveApiKey()),
        SearchProviderKind.ClaudeWebSearch when settings.Get(settings.Search.ClaudeProviderId) is { Kind: AiProviderKind.Anthropic } p
            => new ClaudeSearch(new ClaudeChatModel(p)),
        _ => null,
    };

    public const string RobotsName = "SententiamConverte";
    public const string UserAgent = "SententiamConverte/0.1 (Uebersetzungswerkzeug; beachtet robots.txt und TDM-Vorbehalte)";

    internal static HttpClient NewHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Ehrliche Kennung statt Browser-Tarnung; Seitenbetreiber können uns per robots.txt ausschließen.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }
}

public sealed class SearXngSearch(string baseUrl) : ISearchProvider
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    public string Name => "SearXNG";

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?format=json&q={Uri.EscapeDataString(query)}&language={Uri.EscapeDataString(language)}";
        var json = await Http.GetFromJsonAsync<JsonObject>(url, ct);
        return (json?["results"]?.AsArray() ?? [])
            .Select(r => new SearchResult((string?)r?["title"] ?? "", (string?)r?["url"] ?? "", (string?)r?["content"] ?? ""))
            .Take(max).ToList();
    }
}

public sealed class BraveSearch(string apiKey) : ISearchProvider
{
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

public sealed class TavilySearch(string apiKey) : ISearchProvider
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    public string Name => "Tavily";

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query, max_results = max, search_depth = "basic" }),
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

    public async Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        var answer = await claude.CompleteWithWebSearchAsync(
            "You are a search backend. Use the web_search tool, then output only JSON.",
            $$"""
            Search the web for: {{query}}
            Prefer pages written in {{Languages.DisplayName(language)}} ({{language}}).
            Return up to {{max}} results as JSON: [{"title": "...", "url": "...", "snippet": "verbatim sentence from the page that contains the searched words"}]
            Only include results you actually found. No commentary.
            """, ct);
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
}

public sealed partial class PageFetcher : IPageFetcher
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();
    private static readonly Dictionary<string, RobotsRules> Robots = new();
    private static readonly SemaphoreSlim RobotsLock = new(1, 1);

    /// <summary>Warum die letzte Seite nicht gelesen wurde (für das Protokoll).</summary>
    public string? LastSkipReason { get; private set; }

    [GeneratedRegex("<meta[^>]+name\\s*=\\s*[\"'](tdm-reservation|robots)[\"'][^>]*content\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    public async Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default)
    {
        LastSkipReason = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Skip("keine Webadresse");
            var rules = await RulesFor(uri, ct);
            if (!rules.Allows(uri.PathAndQuery)) return Skip("robots.txt untersagt den Abruf");

            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return Skip($"HTTP {(int)response.StatusCode}");
            if (response.Headers.TryGetValues("tdm-reservation", out var tdm) && tdm.Any(v => v.Trim() == "1"))
                return Skip("Text-und-Data-Mining-Vorbehalt (Header)");
            if (response.Headers.TryGetValues("X-Robots-Tag", out var xr) && xr.Any(v => v.Contains("noai", StringComparison.OrdinalIgnoreCase)))
                return Skip("Vorbehalt „noai“ (Header)");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("html") && !type.Contains("text")) return Skip("kein Text");

            var html = await response.Content.ReadAsStringAsync(ct);
            foreach (Match m in MetaRegex().Matches(html))
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                var content = m.Groups[2].Value.ToLowerInvariant();
                if (name == "tdm-reservation" && content.Trim() == "1") return Skip("Text-und-Data-Mining-Vorbehalt (Meta)");
                if (name == "robots" && (content.Contains("noai") || content.Contains("noindex"))) return Skip($"Vorbehalt „{content}“ (Meta)");
            }
            var text = HtmlText.ToPlain(html);
            return text.Length > maxChars ? text[..maxChars] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            return Skip(ex.Message);
        }
    }

    private string? Skip(string reason)
    {
        LastSkipReason = reason;
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
            catch (HttpRequestException) { rules = RobotsRules.AllowAll; }
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
    private RobotsRules(List<(bool, string)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);
    public static RobotsRules DenyAll { get; } = new([(false, "/")]);

    public static RobotsRules Parse(string text, string agent)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? agents = null;
        List<(bool, string)>? rules = null;
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
                if (!lastWasAgent)
                {
                    agents = new List<string>();
                    rules = new List<(bool, string)>();
                    groups.Add((agents, rules));
                }
                agents!.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if ((field == "allow" || field == "disallow") && rules != null)
            {
                lastWasAgent = false;
                if (value.Length > 0) rules.Add((field == "allow", value));
            }
            else lastWasAgent = false;
        }
        var own = groups.Where(g => g.Agents.Any(a => a != "*" && agent.ToLowerInvariant().Contains(a))).ToList();
        var chosen = own.Count > 0 ? own : groups.Where(g => g.Agents.Contains("*")).ToList();
        return new RobotsRules(chosen.SelectMany(g => g.Rules).ToList());
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
    [GeneratedRegex("<(script|style|noscript|svg|nav|footer|header|aside|form)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
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
