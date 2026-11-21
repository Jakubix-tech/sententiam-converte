using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Qa;

/// <summary>Womit die Grammatik geprüft wird.</summary>
public enum GrammarEngine
{
    /// <summary>Sprachmodell (KI): Fall, Kongruenz, Rechtschreibung, Zeichensetzung – auch für Polnisch gut.</summary>
    Ai,
    /// <summary>Eigener LanguageTool-Server (freie Software) – Texte bleiben im Haus.</summary>
    LanguageToolServer,
    /// <summary>Öffentliche LanguageTool-Schnittstelle (kostenlos, begrenzt: 20 Anfragen und 75 KB je Minute, nur auf Knopfdruck).</summary>
    LanguageToolPublic,
}

public sealed class GrammarSettings
{
    public GrammarEngine Engine { get; set; } = GrammarEngine.Ai;
    public string ServerUrl { get; set; } = "http://localhost:8081";
}

/// <summary>Ein Grammatikfehler in einem Segment.</summary>
public sealed record GrammarIssue(int SegmentId, string Wrong, string? Suggestion, string Message)
{
    public QaIssue ToQa() => new(SegmentId, QaSeverity.Warning, "Grammatik",
        Suggestion is { Length: > 0 } s ? $"„{Wrong}“ → „{s}“: {Message}" : $"„{Wrong}“: {Message}");
}

/// <summary>
/// Grammatikprüfung der Übersetzungen eines Dokuments. Mit LanguageTool (eigener Server oder öffentliche Schnittstelle)
/// oder mit der KI. Segmente werden gebündelt, damit wenige Anfragen genügen; die öffentliche Schnittstelle wird nur auf
/// ausdrücklichen Wunsch genutzt und innerhalb ihrer Grenzen gehalten.
/// </summary>
public static class GrammarChecker
{
    public const string PublicApi = "https://api.languagetool.org";
    private const int MaxRequestBytes = 18_000;          // öffentlich: 20 KB je Anfrage
    private const int MaxBytesPerMinute = 70_000;        // öffentlich: 75 KB je Minute
    private const int MaxRequestsPerMinute = 18;         // öffentlich: 20 je Minute
    private const int AiBatch = 25;

    /// <summary>Für Tests: Wartezeit zwischen Anfragen an die öffentliche Schnittstelle abschalten.</summary>
    internal static bool Throttle { get; set; } = true;

    private static readonly HttpClient Http = NewHttp();

    private static HttpClient NewHttp()
    {
        var http = new HttpClient(new Privacy.OutboundHandler(Privacy.OutboundKind.Web)) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(SearchProviderFactory.UserAgent);
        return http;
    }

    /// <summary>LanguageTool kennt nicht jede Sprache in jeder Variante: „de-DE“, „pl-PL“, „en-GB“, „fr“ …</summary>
    public static string LanguageToolCode(string language)
    {
        var primary = Languages.Primary(language);
        return primary is "de" or "en" or "pt" ? (language.Contains('-') ? language : primary) : primary == "pl" ? "pl-PL" : primary;
    }

    public static async Task<List<GrammarIssue>> CheckAsync(IReadOnlyList<(int Id, string Text)> segments, string language, GrammarSettings settings,
        IChatModel? model = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var todo = segments.Select(s => (s.Id, Text: TagText.StripTags(s.Text).Trim())).Where(s => s.Text.Length > 0).ToList();
        if (todo.Count == 0) return [];
        return settings.Engine == GrammarEngine.Ai
            ? await WithAiAsync(todo, language, model ?? throw new AiException("Für die Grammatikprüfung mit KI ist kein Sprachmodell eingerichtet."), log, ct)
            : await WithLanguageToolAsync(todo, language, settings.Engine == GrammarEngine.LanguageToolPublic ? PublicApi : settings.ServerUrl,
                settings.Engine == GrammarEngine.LanguageToolPublic, log, ct);
    }

    // ---------- LanguageTool ----------

    private static async Task<List<GrammarIssue>> WithLanguageToolAsync(List<(int Id, string Text)> segments, string language, string baseUrl,
        bool isPublic, IProgress<string>? log, CancellationToken ct)
    {
        var issues = new List<GrammarIssue>();
        var url = baseUrl.TrimEnd('/') + "/v2/check";
        var sent = new Queue<(DateTime When, int Bytes)>();
        var batches = Batches(segments).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            var (text, offsets) = batches[b];
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (isPublic && Throttle) await WaitForQuotaAsync(sent, bytes, ct);
            log?.Report($"Grammatik: Teil {b + 1} von {batches.Count} …");
            using var content = new FormUrlEncodedContent([new("text", text), new("language", LanguageToolCode(language))]);
            using var response = await Http.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new AiException($"LanguageTool: HTTP {(int)response.StatusCode} – {(body.Length > 200 ? body[..200] : body)}");
            sent.Enqueue((DateTime.UtcNow, bytes));
            foreach (var m in JsonNode.Parse(body)?["matches"]?.AsArray() ?? [])
            {
                var offset = (int?)m?["offset"] ?? -1;
                var length = (int?)m?["length"] ?? 0;
                var hit = offsets.LastOrDefault(o => o.Start <= offset);
                if (hit.Id == 0 || offset + length > hit.Start + hit.Length) continue;
                var wrong = text.Substring(offset, length);
                var suggestion = (string?)m?["replacements"]?.AsArray().FirstOrDefault()?["value"];
                issues.Add(new GrammarIssue(hit.Id, wrong, suggestion, (string?)m?["message"] ?? ""));
            }
        }
        return issues;
    }

    /// <summary>Segmente zu Anfragen bündeln (Absätze getrennt), mit der Lage jedes Segments im Text.</summary>
    internal static IEnumerable<(string Text, List<(int Id, int Start, int Length)> Offsets)> Batches(IReadOnlyList<(int Id, string Text)> segments)
    {
        var sb = new StringBuilder();
        var offsets = new List<(int, int, int)>();
        foreach (var (id, text) in segments)
        {
            if (sb.Length > 0 && Encoding.UTF8.GetByteCount(sb.ToString()) + Encoding.UTF8.GetByteCount(text) + 2 > MaxRequestBytes)
            {
                yield return (sb.ToString(), offsets);
                sb.Clear();
                offsets = new();
            }
            if (sb.Length > 0) sb.Append("\n\n");
            offsets.Add((id, sb.Length, text.Length));
            sb.Append(text);
        }
        if (sb.Length > 0) yield return (sb.ToString(), offsets);
    }

    private static async Task WaitForQuotaAsync(Queue<(DateTime When, int Bytes)> sent, int bytes, CancellationToken ct)
    {
        while (true)
        {
            while (sent.Count > 0 && DateTime.UtcNow - sent.Peek().When > TimeSpan.FromMinutes(1)) sent.Dequeue();
            if (sent.Count < MaxRequestsPerMinute && sent.Sum(s => s.Bytes) + bytes <= MaxBytesPerMinute) return;
            var wait = sent.Peek().When.AddMinutes(1) - DateTime.UtcNow;
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), ct);
        }
    }

    // ---------- KI ----------

    private static async Task<List<GrammarIssue>> WithAiAsync(List<(int Id, string Text)> segments, string language, IChatModel model,
        IProgress<string>? log, CancellationToken ct)
    {
        var issues = new List<GrammarIssue>();
        var batches = segments.Chunk(AiBatch).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            log?.Report($"Grammatik (KI): Teil {b + 1} von {batches.Count} …");
            var prompt = new StringBuilder();
            prompt.AppendLine($"Check these {Languages.DisplayName(language)} ({language}) sentences for real errors only: grammar, case and gender " +
                              "agreement, verb forms, spelling and punctuation. Ignore style preferences and anything that is correct.");
            prompt.AppendLine("""Reply with JSON only: {"issues": [{"id": 3, "wrong": "exact wrong words", "correct": "corrected words", "note": "short reason in English"}]} – an empty list if all is correct.""");
            prompt.AppendLine();
            foreach (var (id, text) in batches[b]) prompt.AppendLine($"[{id}] {text}");
            var reply = await model.AskAsync("You are a meticulous proofreader.", prompt.ToString(), ct);
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["issues"] is not JsonArray arr) continue;
            var ids = batches[b].ToDictionary(s => s.Id, s => s.Text);
            foreach (var i in arr)
            {
                var id = (int?)i?["id"] ?? 0;
                var wrong = ((string?)i?["wrong"] ?? "").Trim();
                if (!ids.TryGetValue(id, out var text) || wrong.Length == 0 || !text.Contains(wrong)) continue;   // nur, was wirklich dasteht
                var correct = ((string?)i?["correct"])?.Trim();
                if (correct == wrong) continue;
                issues.Add(new GrammarIssue(id, wrong, correct, ((string?)i?["note"] ?? "").Trim()));
            }
        }
        return issues;
    }

}
