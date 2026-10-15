using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>Was ein Agent zum Arbeiten braucht: ein Modell, eine Websuche und ein Protokoll für die Oberfläche.</summary>
public sealed class AgentContext
{
    public required IChatModel Model { get; init; }
    public ISearchProvider? Search { get; init; }
    public IPageFetcher Fetcher { get; init; } = new PageFetcher();
    public IProgress<string>? Log { get; init; }
    public int MaxCharsPerPage { get; init; } = 6000;
    public string ExplanationLanguage { get; init; } = "Deutsch";

    public void Say(string message) => Log?.Report(message);

    public ISearchProvider RequireSearch() =>
        Search ?? throw new AiException("Keine Websuche eingerichtet (Einstellungen → KI → Websuche).");

    public static AgentContext From(AiSettings settings, string? providerId, IProgress<string>? log = null)
    {
        var provider = settings.Get(providerId) ?? throw new AiException("Kein KI-Zugang eingerichtet (Einstellungen → KI).");
        return new AgentContext
        {
            Model = ChatModelFactory.Create(provider),
            Search = SearchProviderFactory.Create(settings),
            Log = log,
            MaxCharsPerPage = provider.MaxCharsPerPage,
            ExplanationLanguage = settings.ExplanationLanguage,
        };
    }
}

public sealed record SourceRef(string Language, string Title, string Url);
