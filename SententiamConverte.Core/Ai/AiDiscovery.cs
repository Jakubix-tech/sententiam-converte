using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein KI-Server, der auf diesem Rechner läuft (Ollama, LM Studio, llama.cpp …).</summary>
public sealed record LocalAiServer(string Name, string BaseUrl, List<string> Models)
{
    public override string ToString() => $"{Name} – {Models.Count} Modell(e)";
}

/// <summary>
/// Ein Cloud-Dienst mit kostenloser Stufe. <paramref name="NeedsKey"/> = false: ohne Anmeldung nutzbar (ein Schlüssel
/// hebt nur die Grenzen an). <paramref name="Preferred"/>: das Modell, das sich bei Tests für Übersetzungen bewährt hat.
/// </summary>
public sealed record FreeAiService(string Id, string Name, string BaseUrl, string KeyUrl, string Note, Func<string, bool> IsFreeModel,
    bool NeedsKey = true, string? Preferred = null)
{
    public override string ToString() => Name;
}

/// <summary>Findet KI-Zugänge ohne Handarbeit: lokale Server, Modelllisten, kostenlose Cloud-Stufen.</summary>
public static class AiDiscovery
{
    /// <summary>Übliche Ports lokaler KI-Server.</summary>
    public static readonly IReadOnlyList<(string Name, int Port)> LocalPorts =
    [
        ("Ollama", 11434), ("LM Studio", 1234), ("llama.cpp / Strata", 8080), ("Jan", 1337), ("vLLM", 8000), ("KoboldCpp", 5001), ("GPT4All", 4891),
    ];

    /// <summary>
    /// Kostenlose Stufen bekannter Anbieter (Stand November 2026, die ersten beiden ohne Anmeldung live geprüft;
    /// Bedingungen ändern sich, die Hinweise im Programm nennen die Grenzen). Alle sprechen die OpenAI-kompatible Schnittstelle.
    /// </summary>
    public static readonly IReadOnlyList<FreeAiService> FreeServices =
    [
        new("ovhcloud", "OVHcloud AI Endpoints (EU, ohne Konto)", "https://oai.endpoints.kepler.ai.cloud.ovh.net/v1", "https://endpoints.ai.cloud.ovh.net/",
            "Ohne Anmeldung nutzbar, Rechenzentren in der EU. Ohne Schlüssel nur 2 Anfragen pro Minute und Modell – gut für einzelne Segmente, für ganze Dateien langsam; ein kostenloser Schlüssel hebt die Grenze an. Bedingungen von OVHcloud beachten.",
            _ => true, NeedsKey: false, Preferred: "gpt-oss-120b"),
        new("kilo", "Kilo Gateway (kostenlose Modelle, ohne Konto)", "https://api.kilo.ai/api/gateway", "https://app.kilo.ai",
            "Ohne Anmeldung etwa 200 Anfragen pro Stunde. Die Anfragen gehen an wechselnde Anbieter, die sie protokollieren und teils zum Training nutzen dürfen: nicht für vertrauliche Texte.",
            m => m.EndsWith(":free", StringComparison.OrdinalIgnoreCase) || m.EndsWith("-free", StringComparison.OrdinalIgnoreCase) || m == "kilo-auto/free",
            NeedsKey: false, Preferred: "kilo-auto/free"),
        new("openrouter", "OpenRouter (kostenlose Modelle)", "https://openrouter.ai/api/v1", "https://openrouter.ai/keys",
            "Modelle mit der Endung „:free“ kosten nichts – ohne Guthaben etwa 50 Anfragen am Tag. Anbieter kostenloser Modelle dürfen Eingaben teils speichern: nicht für vertrauliche Texte.",
            m => m.EndsWith(":free", StringComparison.OrdinalIgnoreCase)),
        new("gemini", "Google Gemini (kostenlose Stufe)", "https://generativelanguage.googleapis.com/v1beta/openai", "https://aistudio.google.com/apikey",
            "Schlüssel aus Google AI Studio, ohne Kreditkarte; Tageslimits je Modell. In der kostenlosen Stufe kann Google Eingaben zur Verbesserung seiner Dienste nutzen: nicht für vertrauliche Texte.",
            m => m.Contains("flash", StringComparison.OrdinalIgnoreCase) || m.Contains("gemma", StringComparison.OrdinalIgnoreCase)),
        new("groq", "Groq (kostenlose Stufe)", "https://api.groq.com/openai/v1", "https://console.groq.com/keys",
            "Sehr schnelle offene Modelle; kostenlose Stufe mit Minuten- und Tageslimits.",
            _ => true),
        new("mistral", "Mistral (kostenlose Stufe)", "https://api.mistral.ai/v1", "https://console.mistral.ai/api-keys",
            "Europäischer Anbieter, kostenloser Modus ohne Kreditkarte, etwa eine Anfrage pro Sekunde. Eingaben können zum Training genutzt werden, wenn das in der Konsole nicht abgeschaltet wird.",
            _ => true, Preferred: "mistral-small-latest"),
        new("nvidia", "NVIDIA NIM (kostenlos mit Entwicklerkonto)", "https://integrate.api.nvidia.com/v1", "https://build.nvidia.com",
            "Viele offene Modelle (u. a. Gemma 4, gpt-oss), kostenlos mit dem NVIDIA-Entwicklerprogramm, etwa 40 Anfragen pro Minute. NVIDIA protokolliert die Nutzung: nicht für vertrauliche Texte.",
            _ => true, Preferred: "gemma-4-31b"),
        new("cerebras", "Cerebras (kostenlose Stufe)", "https://api.cerebras.ai/v1", "https://cloud.cerebras.ai",
            "Sehr schnelle offene Modelle; kostenlose Stufe mit Minuten- und Tageslimits.",
            _ => true),
    ];

    /// <summary>Fragt alle üblichen lokalen Ports gleichzeitig ab (kurzes Zeitlimit).</summary>
    public static async Task<List<LocalAiServer>> DetectLocalAsync(CancellationToken ct = default)
    {
        var tasks = LocalPorts.Select(async p =>
        {
            var url = $"http://127.0.0.1:{p.Port}/v1";
            try
            {
                var models = await ModelsAsync(url, null, TimeSpan.FromMilliseconds(1500), ct);
                return new LocalAiServer(p.Name, url, models);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiException or System.Text.Json.JsonException) { return null; }
        });
        return (await Task.WhenAll(tasks)).OfType<LocalAiServer>().ToList();
    }

    /// <summary>Modelle eines OpenAI-kompatiblen Servers (<c>GET /models</c>).</summary>
    public static async Task<List<string>> ModelsAsync(string baseUrl, string? apiKey, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrEmpty(apiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var r = await http.GetAsync(baseUrl.TrimEnd('/') + "/models", ct);
        if (r.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new AiException("Der Schlüssel wurde abgelehnt.");
        if (!r.IsSuccessStatusCode) throw new AiException($"Der Dienst antwortet mit {(int)r.StatusCode}.");
        var root = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        var items = root?["data"]?.AsArray() ?? root?["models"]?.AsArray() ?? [];
        return items.Select(m => (string?)m?["id"] ?? (string?)m?["name"])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.StartsWith("models/") ? id[7..] : id)        // Gemini nennt sie „models/…“
            .Where(id => !new[] { "embed", "whisper", "tts", "image", "bge-", "diffusion", "guard", "rerank", "moderation" }
                .Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .Distinct().OrderBy(id => id).ToList();
    }

    /// <summary>Ein naheliegendes Modell für Übersetzung: bevorzugt das bewährte des Dienstes, sonst große, aktuelle Allround-Modelle.</summary>
    public static string? SuggestModel(IReadOnlyList<string> models, string? preferredByService = null)
    {
        if (preferredByService != null && models.FirstOrDefault(m => m.Contains(preferredByService, StringComparison.OrdinalIgnoreCase)) is { } best)
            return best;
        string[] preferred = ["qwen3", "gemini-2.5-flash", "gemini", "llama-3.3-70b", "deepseek", "gpt-oss-120b", "mistral", "llama", "gemma", "qwen"];
        foreach (var p in preferred)
            if (models.FirstOrDefault(m => m.Contains(p, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return models.FirstOrDefault();
    }
}
