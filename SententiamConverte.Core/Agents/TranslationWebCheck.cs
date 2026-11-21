using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

public enum PhraseVerdict { Fine, Questionable, Wrong, Unclear }

/// <summary>
/// Verbreitung einer Formulierung in der Zielsprache: Artikel der Wikipedia mit der genauen Wortfolge und – mit einem
/// Suchdienst – wie viele der ersten Treffer sie wörtlich enthalten; dazu Beispiele mit Adresse.
/// </summary>
public sealed record PhraseCount(string Variant, long? WikipediaArticles, int? WebHits, int? WebChecked, List<UsageExample> Examples, string? Country = null);

/// <summary>Eine geprüfte Wendung der Übersetzung mit ihren Varianten, dem Urteil und der Empfehlung.</summary>
public sealed record PhraseCheck(string Phrase, string SourcePart, List<PhraseCount> Variants, PhraseVerdict Verdict, string? Recommended, string Explanation);

public sealed record TranslationWebCheckResult(string Translation, List<PhraseCheck> Phrases, string? Improved, string Summary, string Model, string Search);

/// <summary>
/// Übersetzung im Netz der Zielsprache prüfen: Die KI wählt die Wendungen, an denen sich Richtigkeit und Natürlichkeit
/// zeigen (Fachbegriffe, Kollokationen, feste Wendungen), und nennt Alternativen. Jede Variante wird in der Zielsprache
/// gezählt; dann urteilt die KI im Zusammenhang – die Häufigkeit ist ein Hinweis, kein Beweis – und schlägt, wenn es
/// sich lohnt, eine bessere Fassung vor. Namen, Zahlen und Platzhalter werden nie gesucht.
/// </summary>
public sealed class TranslationWebCheck(AgentContext ctx)
{
    private sealed record PickDto(List<PickItem>? Items);
    private sealed record PickItem(string? Phrase, string? Source, List<string>? Alternatives);
    private sealed record JudgeDto(List<JudgeItem>? Phrases, string? Improved, string? Summary);
    private sealed record JudgeItem(string? Phrase, string? Verdict, string? Recommended, string? Explanation);

    /// <summary>
    /// <paramref name="research"/>: Dossiers zu Ausdrücken dieses Satzes – die Prüfung kennt dann Bedeutung und empfohlene
    /// Benennung jedes Teils (belegt oder nicht) und beurteilt die Wendungen damit.
    /// </summary>
    public async Task<TranslationWebCheckResult> RunAsync(string source, string target, string sourceLanguage, string targetLanguage, string? topic,
        CancellationToken ct = default, IReadOnlyList<TermDossier>? research = null)
    {
        var known = ResearchNotes(research);
        source = TagText.StripTags(source).Trim();
        target = TagText.StripTags(target).Trim();
        if (target.Length == 0) throw new AiException("Es gibt noch keine Übersetzung zum Prüfen.");

        // 1. Prüfwürdige Wendungen und Alternativen
        ctx.Say("Wähle die Wendungen, die sich im Netz prüfen lassen …");
        var picked = JsonExtract.Parse<PickDto>(await ctx.Model.AskAsync(
            "You are an experienced translator and corpus linguist. Output only JSON.",
            $$"""
            Source ({{Languages.DisplayName(sourceLanguage)}}, {{sourceLanguage}}): "{{source}}"
            Translation ({{Languages.DisplayName(targetLanguage)}}, {{targetLanguage}}): "{{target}}"
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
            {{known}}
            Pick up to 4 word sequences FROM THE TRANSLATION (copy them exactly, 2–6 words each) whose correctness or naturalness
            can be checked by how often native texts use them: technical terms, collocations, idioms, set phrases, unusual wording.
            Never pick names, numbers, dates, addresses or single common words. If the whole translation has 6 words or fewer, pick it as one item.
            For each: the source words it renders, and up to 3 alternative renderings in the target language that would fit this context
            (only genuine alternatives; none if the wording is the only sensible one).
            Return: {"items": [{"phrase": "...", "source": "...", "alternatives": ["..."]}]}
            """, ct));
        var items = (picked.Items ?? [])
            .Where(i => i.Phrase is { Length: > 1 } p && UsageCheckAgent.ContainsPhrase(target, p) && Searchable(p))
            .DistinctBy(i => i.Phrase!.Trim(), StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (items.Count == 0) throw new AiException("Die KI hat keine prüfbaren Wendungen gefunden.");

        // 2. Verbreitung in der Zielsprache
        var counted = new List<(PickItem Item, List<PhraseCount> Counts)>();
        foreach (var item in items)
        {
            var variants = new[] { item.Phrase!.Trim() }.Concat((item.Alternatives ?? []).Select(a => a.Trim()).Where(a => a.Length > 1 && Searchable(a)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            var counts = new List<PhraseCount>();
            foreach (var v in variants)
            {
                ct.ThrowIfCancellationRequested();
                counts.Add(await PhraseFrequency.CountAsync(ctx, v, targetLanguage, ct));
            }
            counted.Add((item, counts));
        }

        // 3. Urteil im Zusammenhang, bessere Fassung
        ctx.Say("Bewerte die Wendungen …");
        var report = new StringBuilder();
        foreach (var (item, counts) in counted)
        {
            report.AppendLine($"Phrase \"{item.Phrase}\" (renders \"{item.Source}\"):");
            foreach (var c in counts) report.AppendLine($"  - \"{c.Variant}\": {Describe(c)}");
        }
        var judged = JsonExtract.Parse<JudgeDto>(await ctx.Model.AskAsync(
            "You are an experienced translator and reviser. Base your judgement on the context and the evidence. Output only JSON.",
            $$"""
            Source ({{sourceLanguage}}): "{{source}}"
            Translation ({{targetLanguage}}): "{{target}}"
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}

            {{known}}
            How often native {{Languages.DisplayName(targetLanguage)}} texts use each wording (exact phrase):
            {{report}}
            Judge each phrase IN THIS CONTEXT: is the wording of the translation correct and natural? Frequencies are a hint,
            not proof – a rare wording can be right and a frequent one wrong here.
            verdict: "fine", "questionable" or "wrong"; recommended: the best variant (may be the phrase itself);
            explanation: 1–3 sentences in {{ctx.ExplanationLanguage}} that cite the numbers.
            improved: the whole translation with your recommended changes – only if a phrase is questionable or wrong, otherwise null;
            change nothing else.
            summary: one sentence in {{ctx.ExplanationLanguage}}.
            Return: {"phrases": [{"phrase": "...", "verdict": "...", "recommended": "...", "explanation": "..."}], "improved": null, "summary": "..."}
            """, ct));

        var checks = counted.Select(x =>
        {
            var j = judged.Phrases?.FirstOrDefault(p => string.Equals(p.Phrase?.Trim(), x.Item.Phrase!.Trim(), StringComparison.OrdinalIgnoreCase));
            var verdict = j?.Verdict?.Trim().ToLowerInvariant() switch
            {
                "fine" => PhraseVerdict.Fine,
                "questionable" => PhraseVerdict.Questionable,
                "wrong" => PhraseVerdict.Wrong,
                _ => PhraseVerdict.Unclear,
            };
            return new PhraseCheck(x.Item.Phrase!.Trim(), x.Item.Source?.Trim() ?? "", x.Counts, verdict, j?.Recommended?.Trim(), j?.Explanation?.Trim() ?? "");
        }).ToList();
        var improved = judged.Improved?.Trim() is { Length: > 0 } imp && imp != target && checks.Any(c => c.Verdict is PhraseVerdict.Questionable or PhraseVerdict.Wrong)
            ? imp : null;
        return new TranslationWebCheckResult(target, checks, improved, judged.Summary?.Trim() ?? "", ctx.Model.DisplayName, SearchName());
    }

    /// <summary>Recherche zu den Teilen des Satzes für die Prompts: Ausdruck → Empfehlung, belegt oder nicht, Bedeutung.</summary>
    internal static string ResearchNotes(IReadOnlyList<TermDossier>? research)
    {
        var usable = research?.Where(d => !string.IsNullOrWhiteSpace(d.Recommended)).Take(6).ToList();
        if (usable is not { Count: > 0 }) return "";
        var sb = new StringBuilder("Research on expressions in this sentence (evidenced = found verbatim in target-language sources):\n");
        foreach (var d in usable)
            sb.Append($"- {d.Expression} → {d.Recommended!.Trim()} ({(DossierStore.IsEvidenced(d) ? "evidenced" : "not verified")})")
              .Append(AiTranslator.Short(d.Meaning) is { } m ? $"; meaning: {m}" : "").Append('\n');
        sb.Append("Prefer evidenced renderings; judge unverified ones by the counts.\n");
        return sb.ToString();
    }

    private string SearchName() => ctx.Search is null or WikipediaSearch ? "Wikipedia" : "Wikipedia + " + ctx.Search.Name;

    /// <summary>Nie suchen: Platzhalter des Datenschutzes, Ziffern (Daten, Nummern, Beträge), E-Mail-Adressen.</summary>
    internal static bool Searchable(string phrase) =>
        !Privacy.SensitiveData.PlaceholderRegex().IsMatch(phrase) && !phrase.Any(char.IsDigit) && !phrase.Contains('@');

    /// <summary>„Wikipedia: 1 234 Artikel · Web: 7 von 10 Treffern“.</summary>
    public static string Describe(PhraseCount c) => string.Join(" · ", new[]
    {
        c.WikipediaArticles is { } a ? $"Wikipedia: {a:N0} Artikel" : null,
        c.WebHits is { } h ? $"Web{(c.Country != null ? $" ({c.Country})" : "")}: {h} von {c.WebChecked} Treffern" : null,
    }.Where(s => s != null)) is { Length: > 0 } s ? s : "nicht gezählt";

    public static string VerdictMark(PhraseVerdict v) => v switch
    {
        PhraseVerdict.Fine => "✔",
        PhraseVerdict.Questionable => "⚠",
        PhraseVerdict.Wrong => "✖",
        _ => "?",
    };
}
