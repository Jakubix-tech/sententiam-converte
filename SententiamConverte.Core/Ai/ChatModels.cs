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

public static class ChatModelExtensions
{
    public static Task<string> AskAsync(this IChatModel model, string system, string user, CancellationToken ct = default) =>
        model.CompleteAsync(system, [new ChatTurn(true, user)], ct);
}

public static class ChatModelFactory
{
    public static IChatModel Create(AiProviderConfig config) => config.Kind switch
    {
        AiProviderKind.Anthropic => new ClaudeChatModel(config),
        _ => new OpenAiCompatibleChatModel(config),
    };
}

/// <summary>OpenAI-kompatible Chat-Schnittstelle (lokale Server und viele Cloud-Anbieter).</summary>
public sealed partial class OpenAiCompatibleChatModel : IChatModel
{
    private readonly AiProviderConfig _config;
    private readonly HttpClient _http;

    public OpenAiCompatibleChatModel(AiProviderConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
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

        var url = _config.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var response = await _http.PostAsJsonAsync(url, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiException($"{_config.Name}: HTTP {(int)response.StatusCode} – {Shorten(text)}");
        var json = JsonNode.Parse(text);
        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        // Denkmodelle (Qwen3, DeepSeek-R1 …) schreiben ihre Überlegungen in <think>-Blöcke.
        return ThinkBlock().Replace(content, "").Trim();
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
            return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            throw new AiException($"{_config.Name}: {ex.Message}", ex);
        }
    }

    private async Task<string> SendAsync(string system, IReadOnlyList<ChatTurn> messages, bool webSearch, CancellationToken ct,
        IReadOnlyList<string>? allowedDomains = null)
    {
        if (string.IsNullOrEmpty(_client.ApiKey))
            throw new AiException($"{_config.Name}: kein API-Schlüssel hinterlegt.");
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
            return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            throw new AiException($"{_config.Name}: {ex.Message}", ex);
        }
    }
}

public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Holt JSON aus einer Modellantwort, auch wenn das Modell Text oder ```json-Blöcke drumherum schreibt.</summary>
public static class JsonExtract
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
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
