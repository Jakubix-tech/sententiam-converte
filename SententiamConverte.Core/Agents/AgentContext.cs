using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>Was ein Agent zum Arbeiten braucht: ein Modell, eine Websuche und ein Protokoll für die Oberfläche.</summary>
public sealed class AgentContext
{
    public required IChatModel Model { get; init; }
    public ISearchProvider? Search { get; init; }

    /// <summary>Eigene Fachtexte (lokaler Index); null = keine eingestellt oder für diese KI nicht freigegeben.</summary>
    public Resources.ReferenceLibrary? Library { get; init; }
    public IPageFetcher Fetcher { get; init; } = new PageFetcher();
    public IProgress<string>? Log { get; init; }
    public int MaxCharsPerPage { get; init; } = 6000;
    public string ExplanationLanguage { get; init; } = "Deutsch";

    /// <summary>Fachquellen je Thema und Sprache; die Recherche-KI kann neue amtliche Seiten ergänzen.</summary>
    public List<SourceProfile> Profiles { get; init; } = SourceProfile.Defaults();

    /// <summary>Speichert geänderte Fachquellen (z. B. in den KI-Einstellungen); null = nur für diese Sitzung.</summary>
    public Action? SaveProfiles { get; init; }

    public void Say(string message) => Log?.Report(message);

    /// <summary>Wie oft sich das Protokoll meldet, solange das Modell noch nicht geantwortet hat.</summary>
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Auf das Modell warten und sich dabei regelmäßig im Protokoll melden – lange Wartezeiten sollen sichtbar sein.</summary>
    public async Task<T> WaitAsync<T>(Task<T> call, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var hinted = false;
        while (true)
        {
            if (await Task.WhenAny(call, Task.Delay(HeartbeatInterval, ct)) == call) return await call;
            ct.ThrowIfCancellationRequested();
            var seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
            Say($"⏳ Warte auf {Model.DisplayName} … {seconds} s");
            if (hinted || seconds < 40) continue;
            hinted = true;
            Say("   Kostenlose Online-Dienste sind manchmal überlastet. Mit „Abbrechen“ aufhören und ein anderes Modell oder den kostenlosen Verbund wählen.");
        }
    }


    public ISearchProvider RequireSearch() =>
        Search ?? throw new AiException("Keine Websuche eingerichtet (Seite „KI einrichten“ → Websuche).");

    public static AgentContext From(AiSettings settings, string? providerId, IProgress<string>? log = null)
    {
        var provider = settings.Get(providerId) ?? throw new AiException("Kein KI-Zugang eingerichtet (Seite „KI einrichten“).");
        var model = ChatModelFactory.Create(provider);
        if (model is Privacy.PrivacyChatModel privacy)
            privacy.Masked += n => log?.Report($"🔒 Datenschutz: {n} geschützte Angaben gehen nur als Platzhalter an die KI");
        return new AgentContext
        {
            Model = model,
            Search = Privacy.PrivacyGuard.Wrap(SearchProviderFactory.Create(settings)),
            Library = Resources.ReferenceLibrary.For(provider),
            Log = log,
            MaxCharsPerPage = provider.MaxCharsPerPage,
            ExplanationLanguage = settings.ExplanationLanguage,
            Profiles = settings.SourceProfiles,
            SaveProfiles = () => settings.Save(),
        };
    }
}

/// <summary>
/// Eine Quelle: Seite mit Sprache, Titel und Adresse; bei Belegen zusätzlich die Nummer, auf die Aussagen verweisen, der
/// belegte Begriff und das wörtliche Zitat – damit sich die Stelle auf der Seite wiederfinden lässt (ältere Dateien: leer).
/// </summary>
public sealed record SourceRef(string Language, string Title, string Url)
{
    public int Number { get; init; }
    public string? Term { get; init; }
    public string? Quote { get; init; }

    /// <summary>Wann die Seite gelesen wurde (Inhalte im Netz ändern sich); ältere Dateien und eigene Fachtexte: leer.</summary>
    public DateTime? Retrieved { get; init; }

    /// <summary>Art der Quelle (<see cref="SourceKinds"/>): Behörde, Norm, Forum …; ältere Belege: leer (dann an der Adresse eingeordnet).</summary>
    public string? Kind { get; init; }

    /// <summary>Stand bzw. Fassung, soweit die Quelle ihn nennt („Stand: 01.03.2021“, „ISO 12100:2010“, „PDF vom …“).</summary>
    public string? Version { get; init; }
}
