using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace SententiamConverte.Core.Ai;

public sealed record ChatTurn(bool FromUser, string Text);

/// <summary>Ein Sprachmodell, egal ob lokal oder in der Cloud.</summary>
public interface IChatModel
{
    string DisplayName { get; }
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default);
}

/// <summary>Ein Werkzeug, das die KI aufrufen kann (Name, Zweck, Parameter als JSON-Schema).</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

/// <summary>Ein Werkzeugaufruf der KI.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>Eine Nachricht im Werkzeug-Dialog: Nutzer, Assistent (mit Aufrufen) oder Werkzeugergebnis.</summary>
public sealed record AgentMessage(string Role, string? Text, IReadOnlyList<ToolCall>? Calls = null, string? ToolCallId = null)
{
    public static AgentMessage User(string text) => new("user", text);
    public static AgentMessage Tool(string callId, string result) => new("tool", result, null, callId);
}

/// <summary>Antwort im Werkzeug-Dialog: Text und/oder Werkzeugaufrufe.</summary>
public sealed record ToolReply(string Text, IReadOnlyList<ToolCall> Calls);

/// <summary>Modelle mit echtem Function Calling (OpenAI-Schnittstelle, auch die eingebaute KI).</summary>
public interface IToolChatModel : IChatModel
{
    /// <summary>Wirft <see cref="NotSupportedException"/>, wenn der Dienst keine Werkzeuge annimmt.</summary>
    Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default);
}

public static class ChatModelExtensions
{
    public static Task<string> AskAsync(this IChatModel model, string system, string user, CancellationToken ct = default) =>
        model.CompleteAsync(system, [new ChatTurn(true, user)], ct);
}

public static class ChatModelFactory
{
    /// <summary>Das Modell eines Zugangs; gilt Datenschutz, nur mit Platzhaltern statt geschützter Angaben (siehe <see cref="Privacy.PrivacyChatModel"/>).</summary>
    public static IChatModel Create(AiProviderConfig config) => Privacy.PrivacyContext.Wrap(CreateRaw(config), config);

    /// <summary>Ohne Datenschutz-Hülle (für die Mitglieder eines Verbunds, den die Hülle als Ganzes umgibt).</summary>
    public static IChatModel CreateRaw(AiProviderConfig config) => config.Kind switch
    {
        AiProviderKind.Anthropic => new ClaudeChatModel(config),
        AiProviderKind.OpenCode => new OpenCodeChatModel(config),
        AiProviderKind.BuiltIn => new BuiltInChatModel(config),
        AiProviderKind.Pool => new PooledChatModel(config),
        AiProviderKind.MachineTranslation => new MachineTranslationModel(config),
        _ => new OpenAiCompatibleChatModel(config),
    };
}

/// <summary>OpenAI-kompatible Chat-Schnittstelle (lokale Server und viele Cloud-Anbieter).</summary>
public sealed partial class OpenAiCompatibleChatModel : IToolChatModel
{
    private readonly AiProviderConfig _config;
    private readonly HttpClient _http;

    public OpenAiCompatibleChatModel(AiProviderConfig config)
    {
        _config = config;
        _http = new HttpClient(new Privacy.OutboundHandler(Privacy.OutboundKind.Ai)) { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
        var key = config.ResolveApiKey();
        if (key.Length > 0) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string DisplayName => $"{_config.Name} ({_config.Model})";

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
            msgs.Add(new JsonObject { ["role"] = m.FromUser ? "user" : "assistant", ["content"] = m.Text });
        var body = new JsonObject
        {
            ["model"] = _config.Model,
            ["messages"] = msgs,
            ["max_tokens"] = _config.MaxTokens,
        };
        if (_config.Temperature is { } t) body["temperature"] = t;

        var json = await SendAsync(body, ct);
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        // Denkmodelle (Qwen3, DeepSeek-R1 …) schreiben ihre Überlegungen in <think>-Blöcke.
        var answer = ThinkBlock().Replace(content, "").Trim();
        if (answer.Length == 0)
            throw new AiException($"{_config.Name}: Das Modell hat keine Antwort geliefert (bei Denkmodellen oft zu wenig Platz für die Antwort) – ein anderes Modell wählen.");
        return answer;
    }

    /// <summary>Modelle, die auf Werkzeug-Anfragen nicht rechtzeitig antworten (je Programmlauf gemerkt).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoToolAnswer = new();

    /// <summary>
    /// So lange darf eine Werkzeug-Anfrage dauern. Manche Dienste (beobachtet bei NVIDIA im November 2026) beantworten
    /// normale Anfragen, Anfragen mit Werkzeugen aber gar nicht – dann arbeitet der Agent mit dem einfachen Protokoll.
    /// </summary>
    internal static TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    public async Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var modelKey = _config.BaseUrl.TrimEnd('/') + "|" + _config.Model;
        if (NoToolAnswer.ContainsKey(modelKey))
            throw new NotSupportedException($"{_config.Name}: antwortet nicht auf Werkzeug-Anfragen.");
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Text ?? "" };
            if (m.Calls is { Count: > 0 } calls)
                o["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToJsonString() },
                }).ToArray());
            if (m.ToolCallId != null) o["tool_call_id"] = m.ToolCallId;
            msgs.Add(o);
        }
        var body = new JsonObject
        {
            ["model"] = _config.Model,
            ["messages"] = msgs,
            ["max_tokens"] = _config.MaxTokens,
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            }).ToArray()),
            ["tool_choice"] = "auto",
        };
        if (_config.Temperature is { } temp) body["temperature"] = temp;

        JsonNode? json;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ToolTimeout);
        try { json = await SendAsync(body, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            NoToolAnswer[modelKey] = true;
            throw new NotSupportedException($"{_config.Name}: keine Antwort auf Werkzeug-Anfragen in {ToolTimeout.TotalSeconds:0} s.");
        }
        catch (AiException ex) when (ex.Message.Contains("HTTP 400") && (ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("function", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException(ex.Message, ex);
        }
        var message = json?["choices"]?[0]?["message"];
        var text = ThinkBlock().Replace((string?)message?["content"] ?? "", "").Trim();
        var result = new List<ToolCall>();
        foreach (var call in message?["tool_calls"]?.AsArray() ?? [])
        {
            var name = (string?)call?["function"]?["name"] ?? "";
            var raw = call?["function"]?["arguments"];
            JsonObject args;
            try
            {
                args = raw is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s.Length > 0 ? s : "{}") as JsonObject ?? new JsonObject()
                     : raw as JsonObject is { } o2 ? (JsonObject)o2.DeepClone() : new JsonObject();
            }
            catch (JsonException) { args = new JsonObject(); }
            if (name.Length > 0) result.Add(new ToolCall((string?)call?["id"] ?? Guid.NewGuid().ToString("N")[..12], name, args));
        }
        return new ToolReply(text, result);
    }

    /// <summary>Anfrage senden; bei Mengenbegrenzung (kostenlose Stufen) kurz warten und erneut versuchen.</summary>
    private async Task<JsonNode?> SendAsync(JsonObject body, CancellationToken ct)
    {
        var url = _config.BaseUrl.TrimEnd('/') + "/chat/completions";
        string text;
        for (var attempt = 1; ; attempt++)
        {
            using var response = await _http.PostAsJsonAsync(url, body, ct);
            text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) break;
            // Mengenbegrenzung (kostenlose Stufen): kurz warten und erneut versuchen – im Verbund übernimmt sofort ein anderes Modell
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if ((int)response.StatusCode == 429 && attempt < 4 && !_config.FailFast)
            {
                var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Min(60, 15 * attempt));
                await Task.Delay(wait < TimeSpan.FromSeconds(90) ? wait : TimeSpan.FromSeconds(90), ct);
                continue;
            }
            if ((int)response.StatusCode == 429)
                throw new RateLimitException($"{_config.Name}: Die Mengenbegrenzung des Anbieters ist erreicht. Etwas warten oder ein anderes Modell bzw. einen eigenen Schlüssel verwenden.", retryAfter);
            if ((int)response.StatusCode == 404 && text.Contains("not found for account", StringComparison.OrdinalIgnoreCase))
                throw new AiException($"{_config.Name}: Dieses Modell ist für Ihr Konto beim Anbieter nicht freigeschaltet – ein anderes wählen.");
            throw new AiException($"{_config.Name}: HTTP {(int)response.StatusCode} – {Shorten(text)}");
        }
        return JsonNode.Parse(text);
    }

    internal static string Shorten(string s) => s.Length > 400 ? s[..400] + " …" : s;
}

/// <summary>Claude über das offizielle Anthropic-SDK, optional mit serverseitiger Websuche.</summary>
public sealed class ClaudeChatModel : IChatModel
{
    private readonly AiProviderConfig _config;
    private readonly AnthropicClient _client;

    public ClaudeChatModel(AiProviderConfig config)
    {
        _config = config;
        _client = new AnthropicClient
        {
            ApiKey = config.ResolveApiKey(),
            BaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.anthropic.com" : config.BaseUrl.TrimEnd('/'),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds),
        };
    }

    public string DisplayName => $"{_config.Name} ({_config.Model})";

    private string Host => new Uri(_client.BaseUrl).Host;

    public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
        SendAsync(system, messages, webSearch: false, ct);

    /// <summary>Anfrage mit eingeschalteter Websuche; Claude sucht selbst und antwortet mit Text.</summary>
    public Task<string> CompleteWithWebSearchAsync(string system, string user, CancellationToken ct = default, IReadOnlyList<string>? allowedDomains = null) =>
        SendAsync(system, [new ChatTurn(true, user)], webSearch: true, ct, allowedDomains);

    /// <summary>
    /// Texterkennung für gescannte PDFs: Claude liest das PDF (auch Bilder) und gibt die Absätze zurück,
    /// je Seite getrennt durch eine Zeile „=== Seite n ===“.
    /// </summary>
    public async Task<string> ExtractPdfTextAsync(byte[] pdf, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_client.ApiKey))
            throw new AiException($"{_config.Name}: kein API-Schlüssel hinterlegt.");
        var logId = Diagnostics.ActivityLog.NextId();
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Privacy.Outbound.CheckAsync(Host, Privacy.OutboundKind.Ai, $"PDF-Dokument zur Texterkennung ({pdf.Length / 1024.0:N0} KB)", AiProviderConfig.IsLocalHost(Host), ct, logId);
        var parameters = new MessageCreateParams
        {
            Model = _config.Model,
            MaxTokens = Math.Max(_config.MaxTokens, 32000),
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new DocumentBlockParam { Source = new Base64PdfSource { Data = Convert.ToBase64String(pdf) } },
                        new TextBlockParam
                        {
                            Text = "Transcribe the complete text of this document exactly as written, in its original language. " +
                                   "One paragraph per line; keep headings, list items and table cells as separate lines. " +
                                   "Start every page with a line '=== Seite N ==='. Output only the text.",
                        },
                    },
                },
            ],
        };
        try
        {
            var response = await _client.Messages.Create(parameters, ct);
            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
            Privacy.Outbound.LogAnswer(Host, logId, text, started.ElapsedMilliseconds);
            return text;
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            Privacy.Outbound.LogAnswer(Host, logId, ex.Message, started.ElapsedMilliseconds, "Fehler");
            throw new AiException($"{_config.Name}: {ex.Message}", ex);
        }
    }

    private async Task<string> SendAsync(string system, IReadOnlyList<ChatTurn> messages, bool webSearch, CancellationToken ct,
        IReadOnlyList<string>? allowedDomains = null)
    {
        if (string.IsNullOrEmpty(_client.ApiKey))
            throw new AiException($"{_config.Name}: kein API-Schlüssel hinterlegt.");
        var logId = Diagnostics.ActivityLog.NextId();
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Privacy.Outbound.CheckAsync(Host, Privacy.OutboundKind.Ai,
            $"[system]\n{system}\n\n" + string.Join("\n\n", messages.Select(m => (m.FromUser ? "[user]\n" : "[assistant]\n") + m.Text)) +
            (webSearch ? "\n\n[Websuche beim Anbieter eingeschaltet]" : ""), AiProviderConfig.IsLocalHost(Host), ct, logId);
        var parameters = new MessageCreateParams
        {
            Model = _config.Model,
            MaxTokens = _config.MaxTokens,
            System = system,
            Messages = messages.Select(m => new MessageParam
            {
                Role = m.FromUser ? Role.User : Role.Assistant,
                Content = m.Text,
            }).ToList(),
        };
        if (webSearch)
            parameters = parameters with
            {
                Tools = [new ToolUnion(allowedDomains is { Count: > 0 }
                    ? new WebSearchTool20260209 { MaxUses = 6, AllowedDomains = allowedDomains.ToList() }
                    : new WebSearchTool20260209 { MaxUses = 6 })],
            };

        try
        {
            var response = await _client.Messages.Create(parameters, ct);
            if (response.StopReason == "refusal")
                throw new AiException($"{_config.Name}: Anfrage wurde abgelehnt.");
            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
            Privacy.Outbound.LogAnswer(Host, logId, text, started.ElapsedMilliseconds);
            return text;
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            Privacy.Outbound.LogAnswer(Host, logId, ex.Message, started.ElapsedMilliseconds, "Fehler");
            throw new AiException($"{_config.Name}: {ex.Message}", ex);
        }
    }
}

public class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Der Anbieter meldet seine Mengenbegrenzung (HTTP 429); <see cref="RetryAfter"/> sagt, wann es wieder geht.</summary>
public sealed class RateLimitException(string message, TimeSpan? retryAfter) : AiException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Nummernliste aus einer Modellantwort: kleine Modelle schreiben Belegnummern gern als Text („[1]“, „2“) – lesbare
/// Nummern zählen, der Rest wird übergangen, statt die ganze Antwort zu verwerfen.
/// </summary>
public sealed class LenientIntListConverter : System.Text.Json.Serialization.JsonConverter<List<int>>
{
    public override List<int> Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<int>();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return result;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n)) result.Add(n);
            else if (reader.TokenType == JsonTokenType.String)
                result.AddRange(Regex.Matches(reader.GetString() ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var v) ? v : -1).Where(v => v >= 0));
            else reader.Skip();
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<int> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}

/// <summary>
/// Textliste aus einer Modellantwort: Modelle schreiben statt einer Liste gern einen einzelnen Text
/// (<c>"fr-FR": "matériel informatique"</c>), ein Objekt (<c>{"1": "…", "2": "…"}</c>) oder mischen Zahlen hinein. Alles
/// Lesbare zählt, statt die ganze Antwort – und damit eine Recherche – zu verwerfen.
/// </summary>
public sealed class LenientStringListConverter : System.Text.Json.Serialization.JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<string>();
        Collect(ref reader, result);
        return result;
    }

    /// <summary>Alle Texte unter dem aktuellen Wert (auch verschachtelt), in der Reihenfolge der Antwort.</summary>
    internal static void Collect(ref Utf8JsonReader reader, List<string> into)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                if (reader.GetString()?.Trim() is { Length: > 0 } s) into.Add(s);
                break;
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
                into.Add(Scalar(ref reader));
                break;
            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                var depth = reader.CurrentDepth;
                while (reader.Read() && !(reader.CurrentDepth == depth && reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject))
                    if (reader.TokenType != JsonTokenType.PropertyName) Collect(ref reader, into);
                break;
        }
    }

    internal static string Scalar(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan),
    };

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Einzelner Text aus einer Modellantwort: Steht dort eine Zahl, ein Wahrheitswert oder eine Liste von Texten, wird
/// daraus Text („a; b“), statt die ganze Antwort zu verwerfen.
/// </summary>
public sealed class LenientStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False) return LenientStringListConverter.Scalar(ref reader);
        var parts = new List<string>();
        LenientStringListConverter.Collect(ref reader, parts);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    // Schlüssel von Wörterbüchern („fr-FR“) bleiben, wie sie sind
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
}

/// <summary>Holt JSON aus einer Modellantwort, auch wenn das Modell Text oder ```json-Blöcke drumherum schreibt.</summary>
public static class JsonExtract
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        // Modelle halten die verlangte Form nicht immer ein: Einzeltext statt Liste, Liste statt Text
        Converters = { new LenientStringListConverter(), new LenientStringConverter() },
    };

    public static T Parse<T>(string text)
    {
        var json = Find(text) ?? throw new AiException("Die KI hat kein gültiges JSON geliefert: " + OpenAiCompatibleChatModel.Shorten(text));
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new AiException("Leere KI-Antwort.");
        }
        catch (JsonException ex)
        {
            throw new AiException("KI-Antwort ließ sich nicht lesen: " + ex.Message + " – " + OpenAiCompatibleChatModel.Shorten(json), ex);
        }
    }

    internal static string? Find(string text)
    {
        var fence = Regex.Match(text, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline);
        if (fence.Success) text = fence.Groups[1].Value;
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        var open = text[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
