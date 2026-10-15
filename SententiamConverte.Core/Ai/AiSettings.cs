using System.Text.Json;
using System.Text.Json.Serialization;

namespace SententiamConverte.Core.Ai;

public enum AiProviderKind
{
    /// <summary>Jeder Server mit OpenAI-Schnittstelle (/v1/chat/completions): llama.cpp/Strata, Ollama, LM Studio, vLLM, OpenAI, OpenRouter, Mistral …</summary>
    OpenAiCompatible,
    /// <summary>Claude über die Anthropic-API (mit eingebauter Websuche).</summary>
    Anthropic,
}

/// <summary>Ein frei definierbarer KI-Zugang.</summary>
public sealed class AiProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public AiProviderKind Kind { get; set; }
    public string BaseUrl { get; set; } = "";

    /// <summary>API-Schlüssel. "env:NAME" liest ihn aus der Umgebungsvariablen NAME.</summary>
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public double? Temperature { get; set; }
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Wie viel Text pro Webseite an das Modell geht. Kleine lokale Modelle brauchen kleinere Werte.</summary>
    public int MaxCharsPerPage { get; set; } = 6000;
    public int TimeoutSeconds { get; set; } = 600;

    public string ResolveApiKey() =>
        ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable(ApiKey[4..]) ?? ""
            : ApiKey;

    public override string ToString() => Name;

    /// <summary>Läuft der Dienst auf diesem Rechner oder im eigenen Netz? Dann verlassen Texte das Haus nicht.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocal
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.EndsWith(".local") || host.EndsWith(".localhost")) return true;
            return System.Net.IPAddress.TryParse(host, out var ip) && IsPrivate(ip);
        }
    }

    private static bool IsPrivate(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }
}

public enum SearchProviderKind
{
    None,
    /// <summary>Eigene SearXNG-Instanz (selbst gehostet, kostenlos, empfohlen für lokale KI).</summary>
    SearXng,
    Brave,
    Tavily,
    /// <summary>Websuche über Claude (braucht einen Anthropic-Zugang).</summary>
    ClaudeWebSearch,
}

public sealed class SearchConfig
{
    public SearchProviderKind Kind { get; set; } = SearchProviderKind.None;
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int MaxResults { get; set; } = 6;

    /// <summary>Für <see cref="SearchProviderKind.ClaudeWebSearch"/>: welcher Anthropic-Zugang sucht.</summary>
    public string? ClaudeProviderId { get; set; }

    public string ResolveApiKey() =>
        ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable(ApiKey[4..]) ?? ""
            : ApiKey;
}

/// <summary>Wo das Programm seine Einstellungen ablegt. SENTENTIAM_HOME überschreibt den Ort (Tests, portable Installation).</summary>
public static class AppPaths
{
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("SENTENTIAM_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SententiamConverte");
}

/// <summary>Alle KI-Einstellungen; liegen als JSON im Benutzerordner.</summary>
public sealed class AiSettings
{
    public List<AiProviderConfig> Providers { get; set; } = new();

    /// <summary>Welcher Zugang für welche Aufgabe. Leer = erster Eintrag.</summary>
    public string? TranslateProviderId { get; set; }
    public string? ResearchProviderId { get; set; }
    public string? EvaluateProviderId { get; set; }

    public SearchConfig Search { get; set; } = new();

    /// <summary>In welcher Sprache die Agenten ihre Begründungen schreiben.</summary>
    public string ExplanationLanguage { get; set; } = "Deutsch";

    public AiProviderConfig? Get(string? id) =>
        Providers.FirstOrDefault(p => p.Id == id) ?? Providers.FirstOrDefault();

    /// <summary>Vorlagen für die Auswahl „Neuer Zugang“.</summary>
    public static IReadOnlyList<AiProviderConfig> Presets() =>
    [
        new() { Name = "Claude (Anthropic)", Kind = AiProviderKind.Anthropic, BaseUrl = "https://api.anthropic.com", ApiKey = "env:ANTHROPIC_API_KEY", Model = "claude-opus-5-5", MaxTokens = 16000, MaxCharsPerPage = 12000 },
        new() { Name = "Lokal: Strata / llama.cpp", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:8080/v1", Model = "local", Temperature = 0.2, MaxCharsPerPage = 3000 },
        new() { Name = "Lokal: Ollama", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1", Model = "qwen3:14b", Temperature = 0.2, MaxCharsPerPage = 3000 },
        new() { Name = "Lokal: LM Studio", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:1234/v1", Model = "local-model", Temperature = 0.2, MaxCharsPerPage = 3000 },
        new() { Name = "OpenAI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://api.openai.com/v1", ApiKey = "env:OPENAI_API_KEY", Model = "gpt-4.1" },
        new() { Name = "OpenRouter", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://openrouter.ai/api/v1", ApiKey = "env:OPENROUTER_API_KEY", Model = "" },
        new() { Name = "Mistral", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://api.mistral.ai/v1", ApiKey = "env:MISTRAL_API_KEY", Model = "mistral-large-latest" },
        new() { Name = "DeepSeek", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://api.deepseek.com/v1", ApiKey = "env:DEEPSEEK_API_KEY", Model = "deepseek-chat" },
        new() { Name = "Google Gemini", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", ApiKey = "env:GEMINI_API_KEY", Model = "gemini-2.5-pro" },
        new() { Name = "Eigener Server", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://", Model = "" },
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "ai-settings.json");

    public static AiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), Json) ?? Defaults();
            }
            catch (JsonException) { }      // ältere oder beschädigte Datei → Standardwerte
        }
        return Defaults();
    }

    private static AiSettings Defaults()
    {
        var presets = Presets();
        return new AiSettings
        {
            Providers = [presets[0], presets[1]],
            Search = new SearchConfig { Kind = SearchProviderKind.ClaudeWebSearch, ClaudeProviderId = presets[0].Id },
        };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
