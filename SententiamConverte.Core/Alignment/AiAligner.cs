using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Alignment;

/// <summary>
/// KI-Unterstützung beim Alignment: prüft, ob ein Paar wirklich Satz und Übersetzung ist, und richtet unsichere
/// Abschnitte nach dem Sinn neu aus. Die Längen-Zuordnung bleibt die Grundlage; die KI greift nur dort ein, wo sie
/// unsicher ist, und bestätigte Paare rührt sie nicht an. Ein Verbund prüft mehrere Abschnitte gleichzeitig.
/// </summary>
public sealed class AiAligner(IChatModel model, IProgress<string>? log = null)
{
    /// <summary>Höchstens so viele Sätze je Seite gibt die KI auf einmal zur Zuordnung.</summary>
    public const int MaxSentencesPerRun = 40;

    private int Parallelism => Math.Max(1, (model as IParallelChatModel)?.Parallelism ?? 1);

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + " …" : s;

    /// <summary>Bewertet alle vollständigen, nicht bestätigten Paare (0..1) und wählt sie danach aus oder ab.</summary>
    public async Task<int> CheckAsync(AlignmentSession session, CancellationToken ct = default)
    {
        var todo = session.Pairs.Where(p => p.IsComplete && !p.Confirmed).ToList();
        var batches = todo.Chunk(15).ToList();
        using var gate = new SemaphoreSlim(Parallelism);
        var done = 0;
        await Task.WhenAll(batches.Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await CheckBatchAsync(batch, session.SourceLanguage, session.TargetLanguage, ct);
                log?.Report($"KI prüft Paare … {Interlocked.Add(ref done, batch.Length)}/{todo.Count}");
            }
            finally { gate.Release(); }
        }));
        return todo.Count(p => p.AiScore is < 0.6);
    }

    private async Task CheckBatchAsync(AlignedPair[] batch, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < batch.Length; i++)
            sb.AppendLine($"{i + 1}. [{sourceLanguage}] {Shorten(TagText.StripTags(batch[i].SourceText))}\n   [{targetLanguage}] {Shorten(TagText.StripTags(batch[i].TargetText))}");
        var reply = await model.AskAsync(
            "You check sentence alignments for a translation memory. Output only JSON.",
            $$"""
            For each numbered pair: is the second line a translation of the first (same content, nothing missing, nothing extra)?
            score 1 = exact translation, 0.5 = partly (content missing or from a neighbouring sentence), 0 = different content.
            note: a few words why, only when score < 1.
            {{sb}}
            Return: {"items": [{"i": 1, "score": 0.9, "note": "…"}]}
            """, ct);
        if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["items"] is not JsonArray items) return;
        foreach (var item in items)
        {
            if (item?["i"] is not JsonValue iv || !iv.TryGetValue<int>(out var i) || i < 1 || i > batch.Length) continue;
            var score = item["score"] is JsonValue sv && sv.TryGetValue<double>(out var s) ? Math.Clamp(s, 0, 1) : (double?)null;
            if (score == null) continue;
            var p = batch[i - 1];
            p.AiScore = score;
            p.AiNote = (string?)item["note"] is { Length: > 0 } note ? note : null;
            p.Accepted = score >= 0.7;
        }
    }

    /// <summary>
    /// Sätze nach dem Sinn zuordnen (in der Reihenfolge der Texte). Liefert null, wenn die Antwort nicht stimmig ist –
    /// dann bleibt die bisherige Zuordnung.
    /// </summary>
    public async Task<List<AlignedPair>?> AlignAsync(IReadOnlyList<string> source, IReadOnlyList<string> target,
        string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (source.Count == 0 || target.Count == 0 || source.Count > MaxSentencesPerRun || target.Count > MaxSentencesPerRun) return null;
        var sb = new StringBuilder();
        sb.AppendLine($"Source sentences ({sourceLanguage}):");
        for (var i = 0; i < source.Count; i++) sb.AppendLine($"S{i + 1}: {Shorten(TagText.StripTags(source[i]))}");
        sb.AppendLine($"Target sentences ({targetLanguage}):");
        for (var i = 0; i < target.Count; i++) sb.AppendLine($"T{i + 1}: {Shorten(TagText.StripTags(target[i]))}");
        var reply = await model.AskAsync(
            "You align a text with its translation for a translation memory. Output only JSON.",
            $$"""
            {{sb}}
            Group the sentences into pairs that have the same content. Keep the order of both texts; every sentence
            belongs to exactly one pair; a pair may join several neighbouring sentences on one side (e.g. S3+S4 ↔ T3);
            a sentence without counterpart gets an empty list on the other side.
            Return: {"pairs": [{"s": [1], "t": [1]}, {"s": [2, 3], "t": [2]}, {"s": [4], "t": []}]}
            """, ct);
        if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["pairs"] is not JsonArray arr) return null;
        var pairs = new List<AlignedPair>();
        int nextS = 1, nextT = 1;
        foreach (var node in arr)
        {
            static List<int> Ids(JsonNode? n) => n is JsonArray a
                ? a.Select(x => x is JsonValue v && v.TryGetValue<int>(out var k) ? k : -1).ToList() : [];
            var s = Ids(node?["s"]);
            var t = Ids(node?["t"]);
            // stimmig: lückenlos und in der Reihenfolge
            if (s.Any(k => k != nextS++) || t.Any(k => k != nextT++) || s.Count + t.Count == 0) return null;
            pairs.Add(new AlignedPair
            {
                Source = s.Select(k => source[k - 1]).ToList(),
                Target = t.Select(k => target[k - 1]).ToList(),
            });
        }
        if (nextS != source.Count + 1 || nextT != target.Count + 1) return null;
        foreach (var p in pairs)
        {
            p.Confidence = p.IsComplete ? 0.8 : 0;
            p.Accepted = p.IsComplete;
        }
        return pairs;
    }

    /// <summary>Unsichere Abschnitte (zwischen sicheren Paaren) von der KI neu ausrichten lassen. Ergebnis: neu ausgerichtete Abschnitte.</summary>
    public async Task<int> FixUncertainAsync(AlignmentSession session, CancellationToken ct = default)
    {
        var runs = session.UncertainRuns();
        var fixedRuns = 0;
        // von hinten nach vorn, damit die Indizes stimmen bleiben
        foreach (var (from, to) in runs.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var slice = session.Pairs.Skip(from).Take(to - from + 1).ToList();
            var src = slice.SelectMany(p => p.Source).ToList();
            var tgt = slice.SelectMany(p => p.Target).ToList();
            log?.Report($"KI richtet Paare {from + 1}–{to + 1} neu aus …");
            if (await AlignAsync(src, tgt, session.SourceLanguage, session.TargetLanguage, ct) is not { } aligned) continue;
            session.Replace(from, to, aligned);
            fixedRuns++;
        }
        return fixedRuns;
    }

    /// <summary>Alles automatisch: KI prüft, richtet Unsicheres neu aus und prüft das Ergebnis noch einmal.</summary>
    public async Task<(int Fixed, int Doubtful)> AutoAsync(AlignmentSession session, CancellationToken ct = default)
    {
        await CheckAsync(session, ct);
        var fixedRuns = await FixUncertainAsync(session, ct);
        var doubtful = await CheckAsync(session, ct);
        return (fixedRuns, doubtful);
    }
}
