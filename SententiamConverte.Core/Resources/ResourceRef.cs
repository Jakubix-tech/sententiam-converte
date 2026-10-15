using System.Text.Json.Serialization;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Resources;

/// <summary>
/// Eine an ein Projekt angebundene Ressource: lokale Datei oder Ressource auf einem Teamserver,
/// wahlweise nur für bestimmte Sprachen.
/// </summary>
public sealed class ResourceRef
{
    public ResourceKind Kind { get; set; }

    /// <summary>Name des Serverzugangs (Einstellungen → Teamserver); leer = lokale Datei.</summary>
    public string? Server { get; set; }

    /// <summary>Lokal: Dateipfad. Server: Name der Ressource auf dem Server.</summary>
    public string Location { get; set; } = "";

    /// <summary>Für welche Sprachen die Ressource gilt; leer = alle Sprachen des Projekts.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Bestätigte Segmente (TM) bzw. neue Begriffe (Terminologie) auch hierhin schreiben – so wächst das gemeinsame Wissen.</summary>
    public bool WriteBack { get; set; }

    [JsonIgnore] public bool IsRemote => !string.IsNullOrEmpty(Server);

    public bool AppliesTo(string language) =>
        Languages.Count == 0 || Languages.Any(l => Model.Languages.Matches(l, language));

    public string DisplayName => IsRemote ? $"{Server}: {Location}" : Path.GetFileName(Location);

    public string LanguagesText => Languages.Count == 0 ? "alle Sprachen" : string.Join(", ", Languages);
}

/// <summary>Öffnet angebundene Ressourcen – lokal oder über den passenden Serverzugang.</summary>
public static class ResourceFactory
{
    private static readonly Dictionary<string, RemoteApi> Apis = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wird gesetzt, wenn sich die Serverzugänge ändern (neuer Schlüssel usw.).</summary>
    public static void ResetConnections() => Apis.Clear();

    public static RemoteApi Api(ServerAccount account)
    {
        if (!Apis.TryGetValue(account.Name, out var api) || api.Account.Url != account.Url || api.Account.Token != account.Token)
            Apis[account.Name] = api = new RemoteApi(account);
        return api;
    }

    public static ILanguageResource Open(ResourceRef r, ServerSettings servers)
    {
        if (r.IsRemote)
        {
            var account = servers.Get(r.Server!) ?? throw new RemoteException($"Serverzugang „{r.Server}“ ist nicht eingerichtet.");
            var api = Api(account);
            return r.Kind switch
            {
                ResourceKind.Tm => new RemoteTranslationMemory(api, r.Location),
                ResourceKind.Termbase => new RemoteTermbase(api, r.Location),
                _ => new RemoteKnowledgeBase(api, r.Location),
            };
        }
        if (!File.Exists(r.Location)) throw new FileNotFoundException("Datei nicht gefunden", r.Location);
        return r.Kind switch
        {
            ResourceKind.Tm => new TranslationMemory(r.Location),
            ResourceKind.Termbase => new Termbase(r.Location),
            _ => new KnowledgeBase(r.Location),
        };
    }

    public static string Extension(ResourceKind kind) => kind switch
    {
        ResourceKind.Tm => ".sctm",
        ResourceKind.Termbase => ".sctb",
        _ => ".sckb",
    };

    public static string KindName(ResourceKind kind) => kind switch
    {
        ResourceKind.Tm => "Translation Memory",
        ResourceKind.Termbase => "Terminologie",
        _ => "Wissensdatenbank",
    };
}
