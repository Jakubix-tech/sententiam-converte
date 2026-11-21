using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Privacy;

/// <summary>Wozu Daten den Rechner verlassen.</summary>
public enum OutboundKind
{
    /// <summary>Sprachmodell (Übersetzen, Prüfen, Recherche, Agenten).</summary>
    Ai,
    /// <summary>Klassische maschinelle Übersetzung.</summary>
    MachineTranslation,
    /// <summary>Aufnahme für das Diktat.</summary>
    Dictation,
    /// <summary>Suchanfrage, Wörterbuch, Rechtsquelle oder Seitenabruf.</summary>
    Web,
}

/// <summary>Wann vor dem Senden gefragt wird.</summary>
public enum OutboundMode
{
    /// <summary>Nur festhalten, was gesendet wurde.</summary>
    LogOnly,
    /// <summary>Beim ersten Senden an einen Dienst fragen (bis zum Neustart).</summary>
    AskPerService,
    /// <summary>Vor jeder Anfrage fragen.</summary>
    AskEveryTime,
}

public enum OutboundDecision { Sent, Local, Declined, Blocked }

/// <summary>Einstellungen für den Datenausgang (gehören zu den KI-Einstellungen).</summary>
public sealed class OutboundSettings
{
    public OutboundMode Mode { get; set; } = OutboundMode.LogOnly;

    /// <summary>Dienste, an die nie etwas gesendet wird (Adresse, z. B. „api.openai.com“).</summary>
    public List<string> Blocked { get; set; } = new();
}

/// <summary>Eine Anfrage, die den Rechner verlassen soll (oder an einen lokalen Dienst geht).</summary>
public sealed record OutboundRequest(string Service, OutboundKind Kind, string Content, bool Local);

/// <summary>Ein Eintrag im Protokoll.</summary>
public sealed record OutboundEntry(DateTime When, string Service, OutboundKind Kind, bool Local, string Content, int Characters, OutboundDecision Decision);

/// <summary>Antwort auf die Rückfrage vor dem Senden.</summary>
public enum OutboundAnswer { Send, AlwaysThisService, Decline, BlockService }

/// <summary>Senden abgelehnt oder Dienst gesperrt.</summary>
public sealed class OutboundDeclinedException(string message) : AiException(message);

/// <summary>
/// Datenausgang: Alles, was das Programm an KI, maschinelle Übersetzung, Spracherkennung, Suche und Online-Quellen
/// schickt, kommt hier vorbei – mit dem genauen Inhalt (nach dem Datenschutz-Filter, also so, wie es tatsächlich
/// gesendet wird). Das Protokoll liegt nur im Arbeitsspeicher. Je nach Einstellung fragt das Programm vorher; gesperrte
/// Dienste bekommen nichts. Lokale Dienste (dieser Rechner, eigenes Netz) werden nur protokolliert.
/// </summary>
public static class Outbound
{
    private const int MaxEntries = 500;
    private const int MaxContent = 50_000;

    private static readonly ConcurrentQueue<OutboundEntry> Log = new();
    private static readonly ConcurrentDictionary<string, bool> AllowedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim AskLock = new(1, 1);

    public static OutboundSettings Settings { get; set; } = new();

    /// <summary>Rückfrage der Oberfläche; ohne sie wird nicht gefragt (Server, Tests, Kommandozeile).</summary>
    public static Func<OutboundRequest, CancellationToken, Task<OutboundAnswer>>? Approver { get; set; }

    /// <summary>Neuer Protokolleintrag (aus beliebigem Thread).</summary>
    public static event Action<OutboundEntry>? Logged;

    /// <summary>Wird gerufen, wenn ein Dienst gesperrt wurde (zum Speichern der Einstellungen).</summary>
    public static event Action? SettingsChanged;

    public static IReadOnlyList<OutboundEntry> Entries => Log.ToArray();

    public static void Clear()
    {
        while (Log.TryDequeue(out _)) { }
    }

    public static bool IsBlocked(string service) => Settings.Blocked.Any(b => b.Equals(service, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Vor dem Senden aufrufen: protokolliert, fragt bei Bedarf und wirft, wenn nicht gesendet werden darf.
    /// <paramref name="id"/> verbindet den Eintrag im Ablaufprotokoll mit der Antwort.
    /// </summary>
    public static async Task CheckAsync(string service, OutboundKind kind, string content, bool local, CancellationToken ct = default, string? id = null)
    {
        // Suche und Seitenabruf (GET): keine geschützte Angabe in der Adresse – auch nicht lokal, SearXNG fragt weiter im Netz
        if (kind == OutboundKind.Web && content.StartsWith("GET ", StringComparison.Ordinal)
            && Uri.TryCreate(content[4..].Trim(), UriKind.Absolute, out var uri) && PrivacyGuard.Find(uri.PathAndQuery) is { } value)
        {
            Add(service, kind, local, content, OutboundDecision.Blocked, id);
            throw new OutboundDeclinedException($"Nicht gesendet: Die Anfrage an {service} enthielt die geschützte Angabe „{value}“.");
        }
        if (local)
        {
            Add(service, kind, true, content, OutboundDecision.Local, id);
            return;
        }
        if (IsBlocked(service))
        {
            Add(service, kind, false, content, OutboundDecision.Blocked, id);
            throw new OutboundDeclinedException($"Senden an {service} ist gesperrt (Seite „KI einrichten“ → Datenausgang).");
        }
        if (Approver is { } approver && Settings.Mode != OutboundMode.LogOnly)
        {
            // Nacheinander fragen – bei parallelen Anfragen (Verbund, Stapelübersetzung) erscheint nur eine Rückfrage zugleich
            await AskLock.WaitAsync(ct);
            try
            {
                if (IsBlocked(service))
                {
                    Add(service, kind, false, content, OutboundDecision.Blocked, id);
                    throw new OutboundDeclinedException($"Senden an {service} ist gesperrt (Seite „KI einrichten“ → Datenausgang).");
                }
                if (Settings.Mode == OutboundMode.AskEveryTime || !AllowedThisSession.ContainsKey(service))
                {
                    var answer = await approver(new OutboundRequest(service, kind, Shorten(content), false), ct);
                    switch (answer)
                    {
                        case OutboundAnswer.AlwaysThisService:
                            AllowedThisSession[service] = true;
                            break;
                        case OutboundAnswer.Decline:
                            Add(service, kind, false, content, OutboundDecision.Declined, id);
                            throw new OutboundDeclinedException($"Senden an {service} abgelehnt.");
                        case OutboundAnswer.BlockService:
                            lock (Settings) if (!IsBlocked(service)) Settings.Blocked.Add(service);
                            SettingsChanged?.Invoke();
                            Add(service, kind, false, content, OutboundDecision.Blocked, id);
                            throw new OutboundDeclinedException($"Senden an {service} ist jetzt gesperrt.");
                    }
                }
            }
            finally { AskLock.Release(); }
        }
        Add(service, kind, false, content, OutboundDecision.Sent, id);
    }

    /// <summary>Für Tests: Freigaben dieser Sitzung vergessen.</summary>
    public static void ResetSession() => AllowedThisSession.Clear();

    private static void Add(string service, OutboundKind kind, bool local, string content, OutboundDecision decision, string? id)
    {
        var entry = new OutboundEntry(DateTime.Now, service, kind, local, Shorten(content), content.Length, decision);
        Log.Enqueue(entry);
        while (Log.Count > MaxEntries && Log.TryDequeue(out _)) { }
        Logged?.Invoke(entry);
        Diagnostics.ActivityLog.Write("Datenausgang", $"Anfrage ({KindName(kind)}) – " + decision switch
        {
            OutboundDecision.Sent => "gesendet",
            OutboundDecision.Local => "an lokalen Dienst gesendet",
            OutboundDecision.Declined => "abgelehnt, nicht gesendet",
            _ => "gesperrt, nicht gesendet",
        }, service, content, id: id);
    }

    public static string KindName(OutboundKind kind) => kind switch
    {
        OutboundKind.Ai => "KI",
        OutboundKind.MachineTranslation => "Maschinelle Übersetzung",
        OutboundKind.Dictation => "Diktat",
        _ => "Web",
    };

    /// <summary>Antwort eines Dienstes ins Ablaufprotokoll (Gegenstück zur Anfrage mit derselben Kennung).</summary>
    public static void LogAnswer(string service, string? id, string answer, long milliseconds, string step = "Antwort") =>
        Diagnostics.ActivityLog.Write("Datenausgang", step, service, answer, milliseconds, id);

    private static string Shorten(string s) => s.Length > MaxContent ? s[..MaxContent] + $"\n… ({s.Length - MaxContent:N0} weitere Zeichen)" : s;

    private static readonly JsonSerializerOptions Readable = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Inhalt einer Anfrage lesbar machen: JSON eingerückt und mit echten Umlauten; bei Chat-Anfragen die Nachrichten
    /// als Text (Rolle und Inhalt), damit man sieht, was das Modell bekommt. Audio und andere Binärdaten nur mit Größe.
    /// </summary>
    public static string Describe(string? mediaType, string body)
    {
        if (body.Length == 0) return "";
        if (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(body);
                // Chat-Anfragen sind Objekte mit „messages“; andere Dienste schicken auch Listen (z. B. Microsoft Translator)
                if (node is JsonObject chat && chat["messages"] is JsonArray messages)
                {
                    var text = string.Join("\n\n", messages.Select(m => m is not JsonObject msg ? m?.ToJsonString(Readable) ?? "" :
                        $"[{(string?)msg["role"] ?? "?"}]\n{(msg["content"] is JsonValue v ? v.ToString() : msg["content"]?.ToJsonString(Readable) ?? "")}" +
                        (msg["tool_calls"] is JsonArray calls ? "\n" + calls.ToJsonString(Readable) : "")));
                    if (chat["tools"] is JsonArray tools)
                        text += "\n\n[Werkzeuge] " + string.Join(", ", tools.Select(t => t is JsonObject tool ? (string?)tool["function"]?["name"] ?? "?" : "?"));
                    return text;
                }
                return node?.ToJsonString(Readable) ?? body;
            }
            // die Beschreibung darf eine Anfrage nie verhindern: im Zweifel der Inhalt, wie er ist
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        return body;
    }

    /// <summary>Für HTTP-Anfragen: Adresse und Inhalt beschreiben (ohne Schlüssel – die stehen in Kopfzeilen, nicht hier).</summary>
    public static async Task<string> DescribeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = HideKeys(request.RequestUri?.ToString() ?? "");
        if (request.Content == null) return $"{request.Method} {url}";
        if (request.Content is MultipartFormDataContent multipart)
        {
            var parts = new List<string>();
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "?";
                var type = part.Headers.ContentType?.MediaType ?? "";
                parts.Add(type.StartsWith("text") || type.Length == 0 && part is StringContent
                    ? $"{name}: {await part.ReadAsStringAsync(ct)}"
                    : $"{name}: {type} ({(part.Headers.ContentLength ?? (await part.ReadAsByteArrayAsync(ct)).Length) / 1024.0:N0} KB)");
            }
            return $"{request.Method} {url}\n\n" + string.Join("\n", parts);
        }
        var mediaType = request.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !mediaType.StartsWith("text") && !mediaType.Contains("json") && !mediaType.Contains("xml") && !mediaType.Contains("form"))
            return $"{request.Method} {url}\n\n{mediaType} ({(await request.Content.ReadAsByteArrayAsync(ct)).Length / 1024.0:N0} KB)";
        return $"{request.Method} {url}\n\n{Describe(mediaType, await request.Content.ReadAsStringAsync(ct))}";
    }

    /// <summary>Schlüssel in Adressen (z. B. ?key=…) nicht ins Protokoll schreiben.</summary>
    private static string HideKeys(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url, @"([?&](?:key|api_key|apikey|token|access_token)=)[^&]*", "$1…",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>
/// HTTP-Zwischenstück für den Datenausgang: prüft jede Anfrage über <see cref="Outbound.CheckAsync"/> (Dienst = Adresse
/// des Servers). Lokale Adressen werden nur protokolliert.
/// </summary>
public sealed class OutboundHandler(OutboundKind kind, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    /// <summary>Antworten bis zu dieser Größe kommen mit Inhalt ins Ablaufprotokoll (größere nur mit Typ und Größe).</summary>
    private const long MaxLoggedBody = 2_000_000;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "?";
        if (request.RequestUri?.AbsolutePath == "/robots.txt") return await base.SendAsync(request, ct);   // keine Nutzerdaten
        if (request.Content != null) await request.Content.LoadIntoBufferAsync();
        var id = Diagnostics.ActivityLog.NextId();
        await Outbound.CheckAsync(host, kind, await Outbound.DescribeAsync(request, ct), AiProviderConfig.IsLocalHost(host), ct, id);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Outbound.LogAnswer(host, id, ex.Message, watch.ElapsedMilliseconds, "Fehler, keine Antwort");
            throw;
        }
        if (Diagnostics.ActivityLog.Enabled) await LogResponseAsync(host, id, response, watch.ElapsedMilliseconds, ct);
        return response;
    }

    /// <summary>Status und – bei Text, JSON oder XML bis 2 MB – den Inhalt der Antwort festhalten; Downloads bleiben ungelesen.</summary>
    private static async Task LogResponseAsync(string host, string id, HttpResponseMessage response, long milliseconds, CancellationToken ct)
    {
        var head = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var length = response.Content.Headers.ContentLength;
        var readable = type.StartsWith("text") || type.Contains("json") || type.Contains("xml");
        if (!readable || length > MaxLoggedBody || (length == null && !type.Contains("json")))
        {
            Outbound.LogAnswer(host, id, $"{head}\n\n{(type.Length > 0 ? type : "ohne Typ")}{(length is { } l ? $" ({l / 1024.0:N0} KB)" : "")} – Inhalt nicht protokolliert",
                milliseconds);
            return;
        }
        try
        {
            await response.Content.LoadIntoBufferAsync(ct);
            Outbound.LogAnswer(host, id, $"{head}\n\n{Outbound.Describe(type, await response.Content.ReadAsStringAsync(ct))}", milliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            Outbound.LogAnswer(host, id, $"{head}\n\n(Inhalt nicht lesbar: {ex.Message})", milliseconds);
        }
    }
}
