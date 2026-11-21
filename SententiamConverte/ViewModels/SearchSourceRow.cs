using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Suchdienst zum Ankreuzen: an oder aus, dazu Adresse bzw. Schlüssel, wo er sie braucht.</summary>
public sealed partial class SearchSourceRow(SearchProviderKind kind) : ObservableObject
{
    /// <summary>Reihenfolge in der Liste: kostenlose zuerst.</summary>
    public static readonly SearchProviderKind[] Order =
        [SearchProviderKind.Wikipedia, SearchProviderKind.SearXng, SearchProviderKind.Tavily, SearchProviderKind.Brave, SearchProviderKind.ClaudeWebSearch];

    public SearchProviderKind Kind { get; } = kind;

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";

    public string Name => L.T(Kind switch
    {
        SearchProviderKind.Wikipedia => "Wikipedia (kostenlos, ohne Schlüssel)",
        SearchProviderKind.SearXng => "SearXNG – Metasuche (eigener Server oder auf diesem Rechner, kostenlos)",
        SearchProviderKind.Tavily => "Tavily (kostenlos mit Schlüssel: 1000 Suchen im Monat)",
        SearchProviderKind.Brave => "Brave Search (mit Schlüssel: 5 $ Guthaben im Monat)",
        _ => "Claude-Websuche (über den Claude-Zugang, kostenpflichtig)",
    });

    public bool NeedsUrl => Kind == SearchProviderKind.SearXng;
    public bool NeedsKey => Kind is SearchProviderKind.Brave or SearchProviderKind.Tavily;
    public bool IsClaude => Kind == SearchProviderKind.ClaudeWebSearch;
    public bool ShowUrl => Enabled && NeedsUrl;
    public bool ShowKey => Enabled && NeedsKey;
    public bool ShowClaude => Enabled && IsClaude;

    public string KeyHint => L.T(Kind == SearchProviderKind.Tavily
        ? "Kostenlos: 1000 Abfragen im Monat, ohne Kreditkarte. Nach der Anmeldung den Schlüssel (tvly-…) hier einfügen."
        : "Jeden Monat 5 $ Guthaben (etwa 1000 Suchen); Brave verlangt bei der Anmeldung eine Kreditkarte (gegen Missbrauch). Danach im Dashboard einen Schlüssel für die Search-API anlegen und hier einfügen.");

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUrl));
        OnPropertyChanged(nameof(ShowKey));
        OnPropertyChanged(nameof(ShowClaude));
    }
}
