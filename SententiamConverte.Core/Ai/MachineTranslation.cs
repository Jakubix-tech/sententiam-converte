using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein Dienst für klassische maschinelle Übersetzung (über seine offizielle Schnittstelle, mit eigenem Schlüssel).</summary>
public sealed record MtService(string Id, string Name, string DefaultUrl, string KeyUrl, bool NeedsKey, bool NeedsUrl, bool HasRegion, string Note);

/// <summary>Übersetzt reinen Text samt Tags – ohne Anweisungen, ohne Kontext.</summary>
public interface IMachineTranslator
{
    Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}

/// <summary>
/// Klassische maschinelle Übersetzung: schnell und günstig, aber ohne Anweisungen. Der Zugang wird wie ein KI-Zugang
/// eingerichtet (Art „Maschinelle Übersetzung“, <see cref="AiProviderConfig.Model"/> = Dienst) und für das Übersetzen
/// gewählt; Recherche, Prüfung und Agenten brauchen weiterhin ein Sprachmodell. Formatierungs-Tags gehen als
/// XML-Elemente an den Dienst und kommen als ⟨1⟩ … ⟨/1⟩ zurück. Datenschutz gilt wie bei der KI (Platzhalter).
/// </summary>
public sealed partial class MachineTranslationModel : IChatModel, IMachineTranslator
{
    public static readonly IReadOnlyList<MtService> Services =
    [
        new("lokal", "Offline auf diesem Rechner (OPUS-MT)", "", "https://huggingface.co/Helsinki-NLP", false, false, false,
            "Kostenlos, ohne Schlüssel, auch auf Android: je Sprachrichtung ein Modell der Universität Helsinki (~300 MB). Beim Übersetzen verlässt kein Text den Rechner."),
        new("deepl", "DeepL", "", "https://www.deepl.com/pro-api", true, false, false,
            "Kostenlose Stufe mit 500 000 Zeichen im Monat (Schlüssel endet auf „:fx“)."),
        new("google", "Google Cloud Translation", "https://translation.googleapis.com", "https://console.cloud.google.com/apis/library/translate.googleapis.com", true, false, false,
            "500 000 Zeichen im Monat frei, danach kostenpflichtig; braucht ein Google-Cloud-Projekt."),
        new("microsoft", "Microsoft Translator", "https://api.cognitive.microsofttranslator.com", "https://portal.azure.com/#create/Microsoft.CognitiveServicesTextTranslation", true, false, true,
            "Stufe F0: 2 Mio. Zeichen im Monat frei; Region der Azure-Ressource angeben (z. B. westeurope)."),
        new("libretranslate", "LibreTranslate", "http://localhost:5000", "https://github.com/LibreTranslate/LibreTranslate", false, true, false,
            "Freie Software, auf dem eigenen Rechner oder Server – Texte verlassen das Haus nicht."),
    ];

    private readonly AiProviderConfig _config;
    private readonly HttpClient _http;

    public MachineTranslationModel(AiProviderConfig config)
    {
        _config = config;
        _http = new HttpClient(new Privacy.OutboundHandler(Privacy.OutboundKind.MachineTranslation)) { Timeout = TimeSpan.FromSeconds(Math.Min(config.TimeoutSeconds, 120)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SententiamConverte/1.0");
    }

    // unbekannt → DeepL wie in früheren Fassungen (damals der erste Eintrag)
    public MtService Service => Services.FirstOrDefault(s => s.Id == _config.Model) ?? Services.First(s => s.Id == "deepl");
    public string DisplayName => $"{_config.Name} ({Service.Name})";

    public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
        throw new AiException($"„{_config.Name}“ ist ein Dienst für maschinelle Übersetzung und kann nur übersetzen. Für Recherche, Prüfung, " +
                              "Alignment und Agenten unter „KI einrichten“ ein Sprachmodell wählen.");

    // ---------- Tags ⟨1⟩ ↔ XML ----------

    /// <summary>Segmenttext für den Dienst: Text maskiert, ⟨1⟩…⟨/1⟩ → &lt;g id="1"&gt;…&lt;/g&gt;, ⟨2/⟩ → &lt;x id="2"/&gt;.</summary>
    public static string ToMarkup(string text)
    {
        var sb = new StringBuilder();
        foreach (var part in TagText.Split(text))
        {
            if (part.Tag is { } t)
                sb.Append(t.Kind switch
                {
                    TagText.TagKind.Start => $"<g id=\"{t.Id}\">",
                    TagText.TagKind.End => "</g>",
                    _ => $"<x id=\"{t.Id}\"/>",
                });
            else sb.Append(WebUtility.HtmlEncode(part.Text!).Replace("&#39;", "'").Replace("&quot;", "\""));
        }
        return sb.ToString();
    }

    [GeneratedRegex("""<g\s+id\s*=\s*["']?(\d+)["']?\s*>|</g\s*>|<x\s+id\s*=\s*["']?(\d+)["']?\s*/?>(?:\s*</x>)?|</x\s*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupTag();

    /// <summary>Antwort des Dienstes zurück in Segmenttext.</summary>
    public static string FromMarkup(string markup)
    {
        var sb = new StringBuilder();
        var open = new Stack<int>();
        var pos = 0;
        foreach (Match m in MarkupTag().Matches(markup))
        {
            sb.Append(WebUtility.HtmlDecode(markup[pos..m.Index]));
            pos = m.Index + m.Length;
            if (m.Groups[1].Success)
            {
                var id = int.Parse(m.Groups[1].Value);
                open.Push(id);
                sb.Append(TagText.Start(id));
            }
            else if (m.Groups[2].Success) sb.Append(TagText.Placeholder(int.Parse(m.Groups[2].Value)));
            else if (m.Value.StartsWith("</g", StringComparison.OrdinalIgnoreCase) && open.Count > 0) sb.Append(TagText.End(open.Pop()));
        }
        sb.Append(WebUtility.HtmlDecode(markup[pos..]));
        return sb.ToString();
    }

    // ---------- Sprachcodes je Dienst ----------

    public static string Code(string serviceId, string language, bool target)
    {
        var primary = Languages.Primary(language);
        var region = language.Contains('-') ? language.Split('-')[1].ToUpperInvariant() : "";
        return serviceId switch
        {
            "deepl" when !target => primary.ToUpperInvariant(),
            "deepl" => primary switch
            {
                "en" => region == "US" ? "EN-US" : "EN-GB",
                "pt" => region == "BR" ? "PT-BR" : "PT-PT",
                "zh" => "ZH-HANS",
                "nb" or "no" => "NB",
                _ => primary.ToUpperInvariant(),
            },
            "google" => primary switch { "zh" => region == "TW" ? "zh-TW" : "zh-CN", "nb" => "no", _ => primary },
            "microsoft" => primary switch { "zh" => region == "TW" ? "zh-Hant" : "zh-Hans", "pt" when region == "PT" => "pt-pt", "no" => "nb", _ => primary },
            _ => primary switch { "nb" => "nb", _ => primary },
        };
    }

    // ---------- Anfrage ----------

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (text.Trim().Length == 0) return text;
        if (Service.Id == "lokal") return await LocalMt.LocalTranslation.TranslateAsync(text, sourceLanguage, targetLanguage, ct);
        var key = _config.ResolveApiKey();
        if (Service.NeedsKey && key.Length == 0) throw new AiException($"Für {Service.Name} fehlt der Schlüssel (Seite „KI einrichten“).");
        var markup = ToMarkup(text);
        var id = Service.Id;
        var src = Code(id, sourceLanguage, false);
        var tgt = Code(id, targetLanguage, true);
        var baseUrl = (_config.BaseUrl.Length > 0 ? _config.BaseUrl : Service.DefaultUrl).TrimEnd('/');
        using var request = id switch
        {
            "deepl" => Post($"{(baseUrl.Length > 0 ? baseUrl : key.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com")}/v2/translate",
                new JsonObject { ["text"] = new JsonArray(markup), ["source_lang"] = src, ["target_lang"] = tgt, ["tag_handling"] = "xml" },
                ("Authorization", "DeepL-Auth-Key " + key)),
            "google" => Post($"{baseUrl}/language/translate/v2?key={Uri.EscapeDataString(key)}",
                new JsonObject { ["q"] = new JsonArray(markup), ["source"] = src, ["target"] = tgt, ["format"] = "html" }),
            "microsoft" => Post($"{baseUrl}/translate?api-version=3.0&from={src}&to={tgt}&textType=html",
                new JsonArray(new JsonObject { ["Text"] = markup }),
                ("Ocp-Apim-Subscription-Key", key), ("Ocp-Apim-Subscription-Region", _config.Region)),
            _ => Post($"{baseUrl}/translate",
                new JsonObject { ["q"] = markup, ["source"] = src, ["target"] = tgt, ["format"] = "html", ["api_key"] = key }),
        };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw (int)response.StatusCode switch
            {
                401 or 403 => new AiException($"{Service.Name}: Schlüssel ungültig oder ohne Berechtigung (HTTP {(int)response.StatusCode})."),
                429 => new RateLimitException($"{Service.Name}: zu viele Anfragen – kurz warten.", TimeSpan.FromSeconds(5)),
                456 => new AiException($"{Service.Name}: Zeichenkontingent für diesen Monat aufgebraucht."),
                _ => new AiException($"{Service.Name}: HTTP {(int)response.StatusCode} – {Shorten(body)}"),
            };
        var json = JsonNode.Parse(body);
        var translated = id switch
        {
            "deepl" => (string?)json?["translations"]?[0]?["text"],
            "google" => (string?)json?["data"]?["translations"]?[0]?["translatedText"],
            "microsoft" => (string?)json?[0]?["translations"]?[0]?["text"],
            _ => (string?)json?["translatedText"],
        };
        return translated == null ? throw new AiException($"{Service.Name}: unerwartete Antwort – {Shorten(body)}") : FromMarkup(translated);
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + " …" : s;

    private static HttpRequestMessage Post(string url, JsonNode body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        foreach (var (name, value) in headers.Where(h => h.Value.Length > 0)) request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }
}
