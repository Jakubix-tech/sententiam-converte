using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Remote;

public enum ResourceKind { Tm, Termbase, Knowledge }

/// <summary>Eine Ressource auf dem Teamserver.</summary>
public sealed record ResourceInfo(ResourceKind Kind, string Name, string? Description, long Count);

/// <summary>Was zuletzt im Team passiert ist (neue TM-Einheiten, Begriffe, Wissenseinträge).</summary>
public sealed record ActivityEntry(DateTime When, string User, ResourceKind Kind, string Resource, string Text);

public sealed record WhoAmI(string User, string Role);

// Anfragen
public sealed record TmSearchRequest(string SrcLang, string TgtLang, string Text, int MinScore, int Max, bool InTarget = false);
public sealed record TmAddRequest(string SrcLang, string TgtLang, string Source, string Target, string? Origin, string? Context, DateTime? Created);
public sealed record TmUpdateRequest(string Source, string Target);
public sealed record TermRecognizeRequest(string Text, string SrcLang, string TgtLang);
public sealed record ConceptAddRequest(List<TermDto> Terms, string? Definition, string? Domain, string? Source);
public sealed record TermDto(string Lang, string Text, Terminology.TermStatus Status);
public sealed record KnowledgeSearchRequest(string? Language, string Query, int Max);

/// <summary>Zugang zu einem Teamserver.</summary>
public sealed class ServerAccount
{
    public string Name { get; set; } = "Team";
    public string Url { get; set; } = "http://";
    /// <summary>Zugangsschlüssel vom Server-Admin; "env:NAME" liest ihn aus einer Umgebungsvariablen.</summary>
    public string Token { get; set; } = "";

    public string ResolveToken() =>
        Token.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? Environment.GetEnvironmentVariable(Token[4..]) ?? "" : Token;
}

public sealed class ServerSettings
{
    public List<ServerAccount> Servers { get; set; } = new();

    private static string FilePath => Path.Combine(AppPaths.DataRoot, "servers.json");

    public static ServerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FilePath), RemoteApi.Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, RemoteApi.Json));
    }

    public ServerAccount? Get(string name) => Servers.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class RemoteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// HTTP-Zugriff auf den Teamserver. Die Ressourcen-Schnittstellen sind synchron; die Anfragen laufen deshalb
/// auf einem Hintergrund-Thread (auf Android darf der Oberflächen-Thread nicht selbst ins Netz).
/// </summary>
public sealed class RemoteApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    public ServerAccount Account { get; }

    public RemoteApi(ServerAccount account, HttpMessageHandler? handler = null)
    {
        Account = account;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(account.Url.TrimEnd('/') + "/api/v1/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        var token = account.ResolveToken();
        if (token.Length > 0) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SearchProviderFactory.UserAgent.Split(' ')[0]);
    }

    public T Send<T>(HttpMethod method, string path, object? body = null) =>
        Task.Run(() => SendAsync<T>(method, path, body)).GetAwaiter().GetResult();

    public void Send(HttpMethod method, string path, object? body = null) =>
        Task.Run(() => SendAsync<JsonElement?>(method, path, body)).GetAwaiter().GetResult();

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new RemoteException($"Server „{Account.Name}“ nicht erreichbar: {ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new RemoteException($"Server „{Account.Name}“ antwortet nicht.", ex); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                throw new RemoteException((int)response.StatusCode switch
                {
                    401 => $"Server „{Account.Name}“: Zugangsschlüssel ungültig.",
                    403 => $"Server „{Account.Name}“: keine Berechtigung für diese Aktion.",
                    404 => $"Server „{Account.Name}“: Ressource nicht gefunden.",
                    _ => $"Server „{Account.Name}“: HTTP {(int)response.StatusCode} {text}",
                });
            }
            if (typeof(T) == typeof(JsonElement?) || response.Content.Headers.ContentLength == 0) return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    public Task<WhoAmI> WhoAmIAsync(CancellationToken ct = default) => SendAsync<WhoAmI>(HttpMethod.Get, "me", ct: ct);
    public Task<List<ResourceInfo>> ResourcesAsync(CancellationToken ct = default) => SendAsync<List<ResourceInfo>>(HttpMethod.Get, "resources", ct: ct);
    public Task<List<ActivityEntry>> ActivityAsync(int max = 50, CancellationToken ct = default) => SendAsync<List<ActivityEntry>>(HttpMethod.Get, $"activity?max={max}", ct: ct);

    public Task CreateResourceAsync(ResourceKind kind, string name, string? description, CancellationToken ct = default) =>
        SendAsync<JsonElement?>(HttpMethod.Post, "resources", new ResourceInfo(kind, name, description, 0), ct);

    internal static string Seg(string name) => Uri.EscapeDataString(name);
}
