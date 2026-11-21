using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

/// <summary>Spracherkennung fürs Diktat: ein Dienst mit OpenAI-kompatibler Schnittstelle /audio/transcriptions (Whisper).</summary>
public sealed class TranscriptionConfig
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    public bool IsConfigured => BaseUrl.Length > 0 && Model.Length > 0;

    public bool IsLocal => new AiProviderConfig { Kind = AiProviderKind.OpenAiCompatible, BaseUrl = BaseUrl }.IsLocal;

    public string ResolveApiKey() =>
        ApiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? Environment.GetEnvironmentVariable(ApiKey[4..]) ?? "" : ApiKey;
}

/// <summary>Voreinstellung für einen Spracherkennungsdienst.</summary>
public sealed record TranscriptionPreset(string Name, string BaseUrl, string Model, string KeyUrl, string Note);

public static class Transcriber
{
    public static readonly IReadOnlyList<TranscriptionPreset> Presets =
    [
        new("Groq (kostenloser Schlüssel)", "https://api.groq.com/openai/v1", "whisper-large-v3-turbo", "https://console.groq.com/keys",
            "Sehr schnell; kostenlose Stufe mit Tageskontingent. Die Aufnahme geht an Groq (USA)."),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini-transcribe", "https://platform.openai.com/api-keys",
            "Kostenpflichtig; die Aufnahme geht an OpenAI (USA)."),
        new("Lokal (Whisper auf diesem Rechner)", "http://localhost:8000/v1", "Systran/faster-whisper-small", "https://speaches.ai/",
            "Freie Software (z. B. speaches mit faster-whisper) auf dem eigenen Rechner – die Aufnahme verlässt das Haus nicht."),
    ];

    /// <summary>16-Bit-PCM in eine WAV-Datei packen.</summary>
    public static byte[] Wav(byte[] pcm, int sampleRate = 16000, short channels = 1)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Aufnahme (WAV) in Text umwandeln; <paramref name="language"/> z. B. „en-GB“ (die Erkennung nutzt die Hauptsprache).</summary>
    public static async Task<string> TranscribeAsync(byte[] wav, string language, TranscriptionConfig config, CancellationToken ct = default)
    {
        if (!config.IsConfigured) throw new AiException("Für das Diktat ist noch keine Spracherkennung eingerichtet (Seite „KI einrichten“ → Diktat).");
        using var http = new HttpClient(new Privacy.OutboundHandler(Privacy.OutboundKind.Dictation)) { Timeout = TimeSpan.FromSeconds(120) };
        var key = config.ResolveApiKey();
        if (key.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "diktat.wav");
        form.Add(new StringContent(config.Model), "model");
        form.Add(new StringContent(Languages.Primary(language)), "language");
        form.Add(new StringContent("json"), "response_format");
        using var response = await http.PostAsync(config.BaseUrl.TrimEnd('/') + "/audio/transcriptions", form, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiException($"Spracherkennung: HTTP {(int)response.StatusCode} – {(body.Length > 300 ? body[..300] : body)}");
        var text = body.TrimStart().StartsWith('{') ? (string?)JsonNode.Parse(body)?["text"] : body;
        return (text ?? "").Trim();
    }
}
