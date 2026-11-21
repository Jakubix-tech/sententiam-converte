using System.Text.Json.Nodes;

namespace SententiamConverte.Core.Ai;

/// <summary>Was das DWDS über ein deutsches Wort sagt: Einträge, Häufigkeitsklasse und Aussprache.</summary>
public sealed record DwdsInfo(List<(string Lemma, string? PartOfSpeech, string Url)> Entries, int? FrequencyClass, long? Hits, string? Pronunciation)
{
    public bool Found => Entries.Count > 0 || Hits is > 0;

    /// <summary>Für die KI: knapp und in einer Zeile je Angabe.</summary>
    public string Describe(string word)
    {
        var lines = new List<string>();
        if (Entries.Count == 0) lines.Add($"DWDS: no dictionary entry for \"{word}\".");
        foreach (var e in Entries)
            lines.Add($"DWDS entry: {e.Lemma}" + (e.PartOfSpeech != null ? $" ({e.PartOfSpeech})" : "") + $" – {e.Url}" +
                      (!string.Equals(e.Lemma, word, StringComparison.Ordinal) ? $" (valid spelling of \"{word}\")" : ""));
        if (FrequencyClass is { } f)
            lines.Add($"Frequency class {f} of 6 in the DWDS corpora ({Hits:N0} hits; 0 = rare or unattested, 1 = rare, 2 = occasional, 3 = common, 4–6 = very common).");
        if (Pronunciation != null) lines.Add($"Pronunciation: [{Pronunciation}]");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Amtliche bzw. wissenschaftliche Wörterbücher mit offener Schnittstelle. Das DWDS (Berlin-Brandenburgische Akademie)
/// bietet unter /api Eintrag, Wortart, gültige Schreibung, Häufigkeitsklasse und Aussprache an; die Artikelseiten selbst
/// tragen einen Text-und-Data-Mining-Vorbehalt und werden deshalb nicht gelesen, nur verlinkt (geprüft am 06.11.2026).
/// </summary>
public static class Dictionaries
{
    internal static string DwdsBase = "https://www.dwds.de";

    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();

    private static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (await PageFetcher.PermitAsync(uri, ct) is { } reason) throw new HttpRequestException(reason);
        using var response = await Http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{uri.Host}: HTTP {(int)response.StatusCode}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Nur die Häufigkeit eines deutschen Wortes im DWDS (Treffer in den Korpora, Klasse 0–6); null, wenn unbekannt.</summary>
    public static async Task<(long Hits, int Class)?> DwdsFrequencyAsync(string word, CancellationToken ct = default)
    {
        if (await GetJsonAsync($"{DwdsBase}/api/frequency/?q={Uri.EscapeDataString(word.Trim())}", ct) is not JsonObject f) return null;
        return f["hits"] is JsonValue hv && hv.TryGetValue<long>(out var hits) && f["frequency"] is JsonValue fv && fv.TryGetValue<int>(out var cls)
            ? (hits, cls) : null;
    }

    public static async Task<DwdsInfo> DwdsAsync(string word, CancellationToken ct = default)
    {
        var q = Uri.EscapeDataString(word.Trim());
        var entries = new List<(string, string?, string)>();
        foreach (var e in await GetJsonAsync($"{DwdsBase}/api/wb/snippet/?q={q}", ct) as JsonArray ?? [])
            if ((string?)e?["lemma"] is { } lemma && (string?)e["url"] is { } url)
                entries.Add((lemma, (string?)e["wortart"], url));

        int? frequency = null;
        long? hits = null;
        if (await GetJsonAsync($"{DwdsBase}/api/frequency/?q={q}", ct) is JsonObject f)
        {
            if (f["frequency"] is JsonValue fv && fv.TryGetValue<int>(out var fi)) frequency = fi;
            if (f["hits"] is JsonValue hv && hv.TryGetValue<long>(out var hl)) hits = hl;
        }
        string? ipa = null;
        try
        {
            if (await GetJsonAsync($"{DwdsBase}/api/ipa/?q={q}", ct) is JsonArray { Count: > 0 } arr) ipa = (string?)arr[0]?["ipa"];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { }   // Aussprache ist Beiwerk
        return new DwdsInfo(entries, frequency, hits, ipa);
    }
}
