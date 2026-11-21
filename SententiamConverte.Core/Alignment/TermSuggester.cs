using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Alignment;

/// <summary>Ein vorgeschlagenes Begriffspaar mit Häufigkeit und einem Beispielsatz.</summary>
public sealed record TermSuggestion(string Source, string Target, int Count, string Example);

/// <summary>
/// Fachbegriffe aus alten Übersetzungen: Die KI liest ausgerichtete Satzpaare und nennt Fachbegriffe samt der
/// Übersetzung, die dort tatsächlich verwendet wurde. Übernommen wird nur, was in den Sätzen wirklich steht (beide
/// Seiten, die Zielseite auch gebeugt) – erfundene Paare fallen heraus. Gezählt wird über alle Paare, Bekanntes
/// (schon in der Terminologie) wird ausgelassen. Ein Verbund bearbeitet mehrere Abschnitte gleichzeitig.
/// </summary>
public sealed class TermSuggester(IChatModel model, IProgress<string>? log = null)
{
    public const int PairsPerRequest = 40;

    /// <summary>Höchstens so viele Satzpaare werden der KI gezeigt (gezählt wird über alle).</summary>
    public int MaxPairs { get; init; } = 200;

    private int Parallelism => Math.Max(1, (model as IParallelChatModel)?.Parallelism ?? 1);

    public async Task<List<TermSuggestion>> SuggestAsync(IReadOnlyList<(string Source, string Target)> pairs, string sourceLanguage,
        string targetLanguage, string? topic = null, Func<string, bool>? known = null, CancellationToken ct = default)
    {
        var plain = pairs.Select(p => (Source: TagText.StripTags(p.Source).Trim(), Target: TagText.StripTags(p.Target).Trim()))
            .Where(p => p.Source.Length > 0 && p.Target.Length > 0).ToList();
        // aussagekräftige Sätze bevorzugen: mittlere Länge, keine Wiederholungen
        var sample = plain.Where(p => p.Source.Length is >= 25 and <= 400).DistinctBy(p => p.Source)
            .Take(MaxPairs).ToList();
        if (sample.Count < 3) sample = plain.DistinctBy(p => p.Source).Take(MaxPairs).ToList();
        var batches = sample.Chunk(PairsPerRequest).ToList();
        var found = new List<(string S, string T)>();
        using var gate = new SemaphoreSlim(Parallelism);
        var done = 0;
        await Task.WhenAll(batches.Select(async batch =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var terms = await AskAsync(batch, sourceLanguage, targetLanguage, topic, ct);
                lock (found) found.AddRange(terms);
                log?.Report($"KI sucht Fachbegriffe … {Interlocked.Add(ref done, batch.Length)}/{sample.Count} Satzpaare");
            }
            finally { gate.Release(); }
        }));

        var result = new List<TermSuggestion>();
        foreach (var g in found.GroupBy(t => (t.S.ToLowerInvariant(), t.T.ToLowerInvariant())))
        {
            var (s, t) = g.First();
            if (known?.Invoke(s) == true || result.Any(r => r.Source.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
            var hits = plain.Where(p => ContainsInflected(p.Source, s) && ContainsInflected(p.Target, t)).ToList();
            if (hits.Count == 0) continue;                                        // erfunden oder nicht belegt
            s = NormalizeCase(s, hits.Select(h => h.Source), sourceLanguage);
            t = NormalizeCase(t, hits.Select(h => h.Target), targetLanguage);
            if (known?.Invoke(s) == true || result.Any(r => r.Source.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new TermSuggestion(s, t, hits.Count, hits.MinBy(h => h.Source.Length).Source));
        }
        return result.OrderByDescending(r => r.Count).ThenBy(r => r.Source, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<List<(string, string)>> AskAsync((string Source, string Target)[] batch, string sourceLanguage, string targetLanguage,
        string? topic, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < batch.Length; i++)
            sb.AppendLine($"{i + 1}. [{sourceLanguage}] {batch[i].Source}\n   [{targetLanguage}] {batch[i].Target}");
        var reply = await model.AskAsync(
            "You are a terminologist. You extract terminology from existing translations for a termbase. Output only JSON.",
            $$"""
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic + "\n")}}Sentence pairs ({{sourceLanguage}} → {{targetLanguage}}):
            {{sb}}
            List the specialist terms in these pairs: domain-specific nouns and noun phrases, technical, legal or official
            terms, fixed expressions – not everyday words, not names of persons, not numbers, not placeholders like [NAME_1].
            For each term give the translation that is used in the target sentence of the same pair. Write both sides in
            their dictionary form – nominative singular, lower case unless the language capitalises nouns (German) or it is
            a proper name: e.g. "des Mietverhältnisses" → "Mietverhältnis", "stosunku najmu" → "stosunek najmu",
            "Kaucja" at the start of a sentence → "kaucja". Only pairs where both sides really occur in the sentences. At most 25.
            Return: {"terms": [{"s": "source term", "t": "target term"}]}
            """, ct);
        if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["terms"] is not JsonArray arr) return [];
        var list = new List<(string, string)>();
        foreach (var node in arr)
        {
            var s = ((string?)node?["s"])?.Trim();
            var t = ((string?)node?["t"])?.Trim();
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t) || s.Length > 80 || t.Length > 80) continue;
            if (SensitiveData.PlaceholderRegex().IsMatch(s) || SensitiveData.PlaceholderRegex().IsMatch(t)) continue;
            if (s.All(c => !char.IsLetter(c))) continue;
            list.Add((s, t));
        }
        return list;
    }

    private static bool Contains(string text, string term) => text.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Groß- oder Kleinschreibung wie im Text: Maßgeblich ist, wie der Begriff mitten im Satz steht („die Kaution“,
    /// „w Lipsku“). Steht er nur am Satzanfang („Kaucja wynosi …“), wird er klein geschrieben – außer im Deutschen,
    /// wo dann die Schreibung der KI bleibt. Abkürzungen („EU“) bleiben, wie sie sind.
    /// </summary>
    public static string NormalizeCase(string term, IEnumerable<string> texts, string language)
    {
        if (term.Length < 2 || char.IsUpper(term[0]) && char.IsUpper(term[1])) return term;
        var stem = term.Split(' ')[0];
        stem = stem.Length <= 4 ? stem : stem[..Math.Max(4, (int)Math.Ceiling(stem.Length * 0.7))];
        bool? upper = null;
        foreach (var text in texts)
            for (var i = text.IndexOf(stem, StringComparison.OrdinalIgnoreCase); i >= 0 && upper != true;
                 i = text.IndexOf(stem, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                var before = text[..i].TrimEnd();
                if (before.Length == 0 || ".!?:;\"„“»«(–-".Contains(before[^1])) continue;      // Satzanfang sagt nichts
                if (i > 0 && char.IsLetter(text[i - 1])) continue;                               // mitten im Wort
                upper = char.IsUpper(text[i]);
            }
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return upper switch
        {
            true => char.ToUpper(term[0], culture) + term[1..],
            false => char.ToLower(term[0], culture) + term[1..],
            null => Languages.Primary(language) == "de" ? term : char.ToLower(term[0], culture) + term[1..],
        };
    }

    /// <summary>Kommt der Begriff (auch gebeugt) im Text vor? Jedes Wort muss mit seinem Stamm (ohne Endung) dastehen.</summary>
    public static bool ContainsInflected(string text, string term)
    {
        if (Contains(text, term)) return true;
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w =>
        {
            var stem = w.Length <= 4 ? w : w[..Math.Max(4, (int)Math.Ceiling(w.Length * 0.7))];
            return Contains(text, stem);
        });
    }
}
