using System.Text.Json;
using System.Text.Json.Serialization;

namespace SententiamConverte.Core.Ai;

public enum AiProviderKind
{
    /// <summary>Jeder Server mit OpenAI-Schnittstelle (/v1/chat/completions): llama.cpp/Strata, Ollama, LM Studio, vLLM, OpenAI, OpenRouter, Mistral …</summary>
    OpenAiCompatible,
    /// <summary>Claude über die Anthropic-API (mit eingebauter Websuche).</summary>
    Anthropic,
    /// <summary>Über die eigene OpenCode-Installation (auch deren kostenlose Modelle). Modell als „Anbieter/Modell“.</summary>
    OpenCode,
    /// <summary>Eingebaute KI: offenes Modell auf diesem Rechner (siehe <see cref="BuiltInAi"/>). Modell = Kennung, z. B. gemma-4-e2b.</summary>
    BuiltIn,
    /// <summary>Verbund mehrerer Modelle, die gleichzeitig arbeiten und füreinander einspringen (siehe <see cref="PooledChatModel"/>).</summary>
    Pool,
    /// <summary>Klassische maschinelle Übersetzung (DeepL, Google, Microsoft, LibreTranslate); Modell = Dienst, siehe <see cref="MachineTranslationModel"/>.</summary>
    MachineTranslation,
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

    /// <summary>Nur bei manchen Diensten nötig (Microsoft Translator: Region der Azure-Ressource).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Region { get; set; } = "";
    public double? Temperature { get; set; }
    public int MaxTokens { get; set; } = 4096;

    /// <summary>Wie viel Text pro Webseite an das Modell geht. Kleine lokale Modelle brauchen kleinere Werte.</summary>
    public int MaxCharsPerPage { get; set; } = 6000;
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Nur beim Verbund: die Modelle, die zusammenarbeiten.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AiProviderConfig>? Members { get; set; }

    /// <summary>Im Verbund: bei Mengenbegrenzung nicht warten, sondern sofort melden, damit ein anderes Modell übernimmt.</summary>
    [JsonIgnore]
    public bool FailFast { get; set; }

    public AiProviderConfig Clone() => (AiProviderConfig)MemberwiseClone();

    /// <summary>
    /// Eine ungültige Art (z. B. -1, von einem Auswahlfeld ohne Auswahl zurückgeschrieben) aus den übrigen Angaben
    /// wiederherstellen.
    /// </summary>
    public void RepairKind()
    {
        if (Enum.IsDefined(Kind)) return;
        Kind = Members is { Count: > 0 } ? AiProviderKind.Pool
            : BuiltInAi.Models.Any(m => m.Id == Model) && BaseUrl.Length == 0 ? AiProviderKind.BuiltIn
            : Name.StartsWith("OpenCode", StringComparison.OrdinalIgnoreCase) || (BaseUrl.Length == 0 && Model.Contains('/')) ? AiProviderKind.OpenCode
            : BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase) ? AiProviderKind.Anthropic
            : AiProviderKind.OpenAiCompatible;
    }

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
            if (Kind == AiProviderKind.BuiltIn) return true;
            if (Kind == AiProviderKind.Pool) return Members is { Count: > 0 } m && m.All(x => x.IsLocal);
            if (Kind == AiProviderKind.MachineTranslation && Model == "lokal") return true;                         // OPUS-MT auf diesem Rechner
            if (Kind == AiProviderKind.MachineTranslation && BaseUrl.Length == 0) return Model == "libretranslate";   // Standard: localhost
            return Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && IsLocalHost(uri.Host);
        }
    }

    /// <summary>Dieser Rechner oder das eigene Netz (localhost, .local, private IPv4-Adressen).</summary>
    public static bool IsLocalHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.EndsWith(".local") || host.EndsWith(".localhost")) return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && IsPrivate(ip);
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
    /// <summary>Wikipedia (kostenlos, ohne Schlüssel, alle Sprachen).</summary>
    Wikipedia,
}

public sealed class SearchConfig
{
    public SearchProviderKind Kind { get; set; } = SearchProviderKind.None;
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int MaxResults { get; set; } = 6;

    /// <summary>Für <see cref="SearchProviderKind.ClaudeWebSearch"/>: welcher Anthropic-Zugang sucht.</summary>
    public string? ClaudeProviderId { get; set; }

    /// <summary>Wohin das Programm Docker Desktop installiert hat (für die eigene SearXNG-Instanz); leer = üblicher Ort.</summary>
    public string? DockerFolder { get; set; }

    /// <summary>
    /// Mehrere Suchdienste gleichzeitig – wie ein Mensch, der in verschiedenen Quellen sucht; jeder mit eigener Adresse
    /// bzw. eigenem Schlüssel. Leer (ältere Einstellungen): nur der Dienst aus <see cref="Kind"/>, <see cref="BaseUrl"/>
    /// und <see cref="ApiKey"/>. Diese Felder schreibt das Programm weiter (erster gewählter Dienst), damit ältere
    /// Fassungen die Einstellungen lesen können.
    /// </summary>
    public List<SearchSource> Sources { get; set; } = new();

    public string ResolveApiKey() =>
        ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable(ApiKey[4..]) ?? ""
            : ApiKey;

    /// <summary>Die Dienste, die suchen sollen: die angekreuzten bzw. bei älteren Einstellungen der eine gewählte.</summary>
    public List<SearchSource> ActiveSources() => Sources.Count > 0
        ? Sources.Where(s => s.Enabled && s.Kind != SearchProviderKind.None).ToList()
        : Kind == SearchProviderKind.None ? []
        : [new SearchSource { Kind = Kind, BaseUrl = BaseUrl, ApiKey = ApiKey, ClaudeProviderId = ClaudeProviderId }];

    /// <summary>
    /// Ein lokales SearXNG läuft, gesucht wird aber nur in Wikipedia (oder gar nicht): SearXNG dazunehmen, damit die
    /// Recherche im ganzen Netz der Sprache sucht – Fachseiten zuerst, dann allgemein. Wikipedia bleibt dabei. Hat der
    /// Nutzer SearXNG schon einmal eingetragen (auch ausgeschaltet) oder sucht er schon im Netz, bleibt alles, wie es ist.
    /// </summary>
    public bool AdoptLocalSearXng(string baseUrl)
    {
        if (Sources.Any(s => s.Kind == SearchProviderKind.SearXng)) return false;
        if (ActiveSources().Any(s => s.Kind is not (SearchProviderKind.Wikipedia or SearchProviderKind.None))) return false;
        if (Sources.Count == 0 && Kind != SearchProviderKind.None)
            Sources.Add(new SearchSource { Kind = Kind, BaseUrl = BaseUrl, ApiKey = ApiKey, ClaudeProviderId = ClaudeProviderId });
        if (!Sources.Any(s => s.Kind == SearchProviderKind.Wikipedia)) Sources.Add(new SearchSource { Kind = SearchProviderKind.Wikipedia });
        Sources.Insert(0, new SearchSource { Kind = SearchProviderKind.SearXng, BaseUrl = baseUrl });
        // ältere Programmfassungen kennen nur einen Dienst: dort gilt SearXNG
        Kind = SearchProviderKind.SearXng;
        BaseUrl = baseUrl;
        return true;
    }
}

/// <summary>Ein Suchdienst der Mehrfachsuche.</summary>
public sealed class SearchSource
{
    public SearchProviderKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
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

    /// <summary>Spracherkennung für das Diktat im Editor.</summary>
    public TranscriptionConfig Transcription { get; set; } = new();

    /// <summary>Fachquellen je Thema und Sprache (Recht, Medizin, Technik, Sprache …).</summary>
    public List<SourceProfile> SourceProfiles { get; set; } = SourceProfile.Defaults();

    /// <summary>Datenschutz ohne Projekt (Recherche, Alignment) und Vorlage für neue Projekte.</summary>
    public Privacy.PrivacySettings Privacy { get; set; } = new();

    /// <summary>Grammatikprüfung: KI oder LanguageTool (eigener Server, öffentliche Schnittstelle).</summary>
    public Qa.GrammarSettings Grammar { get; set; } = new();

    /// <summary>Datenausgang: vor dem Senden fragen, gesperrte Dienste.</summary>
    public Privacy.OutboundSettings Outbound { get; set; } = new();

    /// <summary>Ablaufprotokoll: jeder Schritt und jede Anfrage samt Antwort, im Datenordner unter „protokoll“.</summary>
    public Diagnostics.ActivityLogSettings ActivityLog { get; set; } = new();

    /// <summary>Stand der Standardprofile, mit dem die Datei zuletzt ergänzt wurde (ältere Dateien: 0).</summary>
    public int SourceProfilesVersion { get; set; }

    public SourceProfile? Profile(string? name) =>
        string.IsNullOrEmpty(name) ? null : SourceProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>In welcher Sprache die Agenten ihre Begründungen schreiben.</summary>
    public string ExplanationLanguage { get; set; } = "Deutsch";

    /// <summary>Ordner für die eingebaute KI (Laufzeit und Modelle); leer = Standardordner im Benutzerprofil.</summary>
    public string? BuiltInFolder { get; set; }

    public AiProviderConfig? Get(string? id) =>
        Providers.FirstOrDefault(p => p.Id == id) ?? Providers.FirstOrDefault();

    /// <summary>Vorlagen für die Auswahl „Neuer Zugang“.</summary>
    public static IReadOnlyList<AiProviderConfig> Presets() =>
    [
        BuiltInPreset(),
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

    /// <summary>Die kostenlose eingebaute KI mit dem kleineren, schnellen Modell.</summary>
    public static AiProviderConfig BuiltInPreset(BuiltInModel? model = null)
    {
        model ??= BuiltInAi.Models[0];
        return new AiProviderConfig
        {
            Name = "Kostenlose KI auf diesem Rechner (" + model.Name + ")", Kind = AiProviderKind.BuiltIn, Model = model.Id,
            Temperature = 0.2, MaxCharsPerPage = 3000,
        };
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(AppPaths.DataRoot, "ai-settings.json");

    public static AiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        var settings = Defaults();
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), Json) ?? settings;
            }
            catch (JsonException) { }      // ältere oder beschädigte Datei → Standardwerte
        }
        foreach (var p in settings.Providers) p.RepairKind();
        if (!Enum.IsDefined(settings.Search.Kind)) settings.Search.Kind = SearchProviderKind.Wikipedia;
        SourceProfile.Upgrade(settings.SourceProfiles, settings.SourceProfilesVersion);
        settings.SourceProfilesVersion = SourceProfile.DefaultsVersion;
        BuiltInAi.Folder = settings.BuiltInFolder ?? "";
        LocalSearXng.InstallFolder = settings.Search.DockerFolder;
        Diagnostics.ActivityLog.Apply(settings.ActivityLog ??= new());
        return settings;
    }

    /// <summary>
    /// Neue Installation: zuerst die kostenlose eingebaute KI (wird beim ersten Gebrauch bzw. unter „KI einrichten“
    /// heruntergeladen), daneben Claude und ein lokaler Server als Vorlagen.
    /// </summary>
    private static AiSettings Defaults()
    {
        var claude = new AiProviderConfig
        {
            Name = "Claude (Anthropic)", Kind = AiProviderKind.Anthropic, BaseUrl = "https://api.anthropic.com", ApiKey = "env:ANTHROPIC_API_KEY",
            Model = "claude-opus-5-5", MaxTokens = 16000, MaxCharsPerPage = 12000,
        };
        var local = new AiProviderConfig
        {
            Name = "Lokal: Strata / llama.cpp", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:8080/v1", Model = "local",
            Temperature = 0.2, MaxCharsPerPage = 3000,
        };
        var providers = BuiltInAi.IsSupported ? new List<AiProviderConfig> { BuiltInPreset(), claude, local } : [claude, local];
        return new AiSettings
        {
            Providers = providers,
            Search = new SearchConfig { Kind = SearchProviderKind.Wikipedia, ClaudeProviderId = claude.Id },
        };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
