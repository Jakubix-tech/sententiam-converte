using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

public enum UsageVerdict { Common, Occasional, Rare, Unattested, Uncertain }

/// <summary>
/// Ein Beleg: Suchtreffer mit Ausschnitt; konnte die Seite gelesen werden, zusätzlich der Satz von der Seite selbst –
/// mit einem Link, der im Browser direkt zu diesem Satz springt.
/// </summary>
public sealed record UsageExample(string Url, string Title, string Snippet)
{
    /// <summary>Satz von der gelesenen Seite, der die Wortfolge wörtlich enthält; null = nur der Ausschnitt der Trefferliste.</summary>
    public string? Sentence { get; init; }

    /// <summary>Nummer, auf die Urteil und Erklärung verweisen ([1], [2] …).</summary>
    public int Number { get; init; }

    public bool Verified => Sentence != null;
    public string Quote => Sentence ?? Snippet;
    public string Link => SourceLink.WithTextFragment(Url, Sentence);
}

/// <summary>Belege für eine Formulierung: wie viele Treffer sie wörtlich enthalten, mit Beispielen und den Suchanfragen.</summary>
public sealed record UsageEvidence(string Variant, int ResultsChecked, int ExactHits, List<UsageExample> Examples)
{
    public List<string> Queries { get; init; } = [];
}

public sealed record UsageResult(
    string Expression, string Language, UsageVerdict Verdict, string? Recommended,
    string Explanation, List<UsageEvidence> Evidence)
{
    /// <summary>Klare Aussage aus den gezählten Belegen: üblich, selten, nicht üblich – oder dass es keine Belege gibt.</summary>
    public string Statement => UsageCheckAgent.Statement(this);
}

/// <summary>
/// Prüft, ob ein Ausdruck in einer Sprache tatsächlich so verwendet wird: sucht die genaue Wortfolge (und
/// gleichbedeutende Alternativen) im Netz, liest die Seiten und holt den Satz, in dem sie steht, zählt wörtliche
/// Belege und lässt die KI anhand der Belege urteilen. Reichen die Belege nicht, sucht der Agent weiter – erst mit dem
/// Fachgebiet, dann mit Anfragen, die sich die KI ausdenkt (typische Zusammenhänge, Textsorten wie Anleitung,
/// Datenblatt, Norm, Forum) – und sagt im Protokoll, dass er noch keine Belege hat und weitersucht.
/// </summary>
public sealed partial class UsageCheckAgent(AgentContext ctx)
{
    /// <summary>So viele wörtliche Belege je Formulierung reichen; darunter sucht der Agent weiter.</summary>
    public const int EnoughHits = 3;

    /// <summary>Höchstens so viele Seiten je Formulierung werden gelesen.</summary>
    public const int PagesPerVariant = 5;

    private sealed record Alternatives(List<string> Variants);
    private sealed record VerdictDto(string Verdict, string? Recommended, string Explanation);
    private sealed record QueriesDto(Dictionary<string, List<string>>? Queries);

    public async Task<UsageResult> RunAsync(
        string expression, string language, string? topic = null, IReadOnlyList<string>? alternatives = null,
        bool suggestAlternatives = true, CancellationToken ct = default, bool readPages = true)
    {
        var search = ctx.RequireSearch();
        var variants = new List<string> { expression.Trim() };

        if (alternatives is { Count: > 0 })
            variants.AddRange(alternatives.Where(a => a.Trim().Length > 0).Select(a => a.Trim()));
        else if (suggestAlternatives)
        {
            ctx.Say($"Suche gleichbedeutende Alternativen zu „{expression}“ …");
            try
            {
                var alt = JsonExtract.Parse<Alternatives>(await ctx.Model.AskAsync(
                    "You are a terminologist. Output only JSON.",
                    $$"""
                    Language: {{Languages.DisplayName(language)}} ({{language}})
                    {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
                    Expression: "{{expression}}"
                    List up to 3 alternative expressions in the same language with the same meaning that a native
                    professional might use instead. Return: {"variants": ["...", "..."]}
                    """, ct));
                variants.AddRange(alt.Variants.Where(v => !string.IsNullOrWhiteSpace(v)).Take(3));
            }
            catch (AiException ex) { ctx.Say("Keine Alternativen: " + ex.Message); }
        }
        variants = variants.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Dictionary<string, List<string>>? creative = null;
        var evidence = new List<UsageEvidence>();
        foreach (var v in variants)
        {
            ct.ThrowIfCancellationRequested();
            var results = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
            var sentences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queries = new List<string>();
            bool Exact(SearchResult r) => ContainsPhrase(r.Title + " " + r.Snippet, v) || sentences.ContainsKey(r.Url);
            int Hits() => results.Values.Count(Exact);

            async Task SearchAsync(string query)
            {
                queries.Add(query);
                try
                {
                    foreach (var r in await search.SearchAsync(query, language, 10, ct)) results.TryAdd(r.Url, r);
                }
                catch (Exception ex) when (ex is HttpRequestException or AiException)
                {
                    ctx.Say($"Suche fehlgeschlagen: {ex.Message}");
                }
                if (readPages) await ReadPagesAsync(v, results.Values, sentences, tried, ct);
            }

            ctx.Say($"Suche Belege für „{v}“ …");
            await SearchAsync($"\"{v}\"");
            if (Hits() < EnoughHits && !string.IsNullOrWhiteSpace(topic))
            {
                ctx.Say($"Zu „{v}“ habe ich erst {Hits()} wörtliche Belege – ich suche im Fachgebiet weiter …");
                await SearchAsync($"\"{v}\" {topic}");
            }
            if (Hits() < EnoughHits)
            {
                creative ??= await CreativeQueriesAsync(variants, language, topic, ct);
                foreach (var q in creative.GetValueOrDefault(v) ?? [])
                {
                    if (Hits() >= EnoughHits) break;
                    ctx.Say($"Zu „{v}“ noch zu wenig Belege ({Hits()}) – ich suche anders: {q}");
                    await SearchAsync(q);
                }
            }
            var exact = results.Values.Where(Exact).OrderByDescending(r => sentences.ContainsKey(r.Url)).ToList();
            evidence.Add(new UsageEvidence(v, results.Count, exact.Count,
                exact.Take(5).Select(r => new UsageExample(r.Url, r.Title, r.Snippet) { Sentence = sentences.GetValueOrDefault(r.Url) }).ToList())
            {
                Queries = queries,
            });
            ctx.Say(exact.Count == 0
                ? $"  Für „{v}“ habe ich keinen wörtlichen Beleg gefunden ({results.Count} Treffer, {queries.Count} Suchen)."
                : $"  {exact.Count} von {results.Count} Treffern enthalten „{v}“ wörtlich, {sentences.Count} Sätze auf den Seiten nachgelesen.");
        }
        // Belege durchnummerieren, damit Urteil und Erklärung darauf verweisen können
        var number = 0;
        evidence = evidence.Select(e => e with { Examples = e.Examples.Select(x => x with { Number = ++number }).ToList() }).ToList();

        ctx.Say("Bewerte die Belege …");
        var report = new StringBuilder();
        foreach (var e in evidence)
        {
            report.AppendLine($"Variant \"{e.Variant}\": {e.ExactHits} of {e.ResultsChecked} search results contain it verbatim.");
            foreach (var ex in e.Examples) report.AppendLine($"  [{ex.Number}] {ex.Url}: {ex.Quote}");
        }
        var dto = JsonExtract.Parse<VerdictDto>(await ctx.Model.AskAsync(
            "You are an experienced terminologist and corpus linguist. Base your judgement on the evidence, not on guesses. Output only JSON.",
            $$"""
            Language: {{Languages.DisplayName(language)}} ({{language}})
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
            Expression under test: "{{expression}}"

            Web evidence (exact-phrase searches, top results, numbered):
            {{report}}

            Is the expression under test actually used by native speakers in this subject field?
            verdict: one of "common", "occasional", "rare", "unattested", "uncertain" (use "uncertain" if the search returned nothing usable).
            recommended: the variant you would recommend (may be the expression itself), or null.
            explanation: 2-4 sentences in {{ctx.ExplanationLanguage}}; support every statement with the evidence numbers like [1], [2].
            If the expression is not usual in this language, say so plainly.
            Return: {"verdict": "...", "recommended": "...", "explanation": "..."}
            """, ct));

        var verdict = dto.Verdict?.Trim().ToLowerInvariant() switch
        {
            "common" => UsageVerdict.Common,
            "occasional" => UsageVerdict.Occasional,
            "rare" => UsageVerdict.Rare,
            "unattested" => UsageVerdict.Unattested,
            _ => UsageVerdict.Uncertain,
        };
        // Das Urteil darf den gezählten Belegen nicht widersprechen
        var main = evidence[0];
        if (main.ExactHits == 0) verdict = evidence.All(e => e.ResultsChecked == 0) ? UsageVerdict.Uncertain : UsageVerdict.Unattested;
        else if (verdict == UsageVerdict.Unattested) verdict = UsageVerdict.Rare;
        var recommended = dto.Recommended is { Length: > 0 } rec && evidence.Any(e => e.Variant.Equals(rec, StringComparison.OrdinalIgnoreCase) && e.ExactHits > 0)
            ? rec : main.ExactHits > 0 ? dto.Recommended : evidence.Where(e => e.ExactHits > 0).MaxBy(e => e.ExactHits)?.Variant;
        return new UsageResult(expression, language, verdict, recommended, dto.Explanation, evidence);
    }

    /// <summary>Suchanfragen, die die KI sich ausdenkt, um echte Verwendungen zu finden (die Wortfolge immer in Anführungszeichen).</summary>
    private async Task<Dictionary<string, List<string>>> CreativeQueriesAsync(IReadOnlyList<string> variants, string language, string? topic,
        CancellationToken ct)
    {
        try
        {
            var dto = JsonExtract.Parse<QueriesDto>(await ctx.Model.AskAsync(
                "You are a resourceful researcher. Output only JSON.",
                $$"""
                Language: {{Languages.DisplayName(language)}} ({{language}}){{(string.IsNullOrWhiteSpace(topic) ? "" : ", subject field: " + topic)}}
                Expressions: {{string.Join("; ", variants.Select(v => $"\"{v}\""))}}
                For each expression write 2 different web search queries that are likely to find real texts by native
                speakers that use it verbatim – combine it with typical context words, or with kinds of texts where it would
                occur (manual, data sheet, standard, specialist article, forum, dictionary example). Every query must contain
                the expression exactly, in double quotes.
                Return: {"queries": {"expression": ["query 1", "query 2"]} }
                """, ct));
            return (dto.Queries ?? []).ToDictionary(
                kv => kv.Key, kv => kv.Value.Where(q => q.Contains('"' + kv.Key + '"', StringComparison.OrdinalIgnoreCase)).Take(2).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (AiException ex)
        {
            ctx.Say("Keine weiteren Suchideen: " + ex.Message);
            return [];
        }
    }

    /// <summary>Seiten lesen (Treffer mit der Wortfolge im Ausschnitt zuerst) und den Satz holen, in dem sie steht.</summary>
    private async Task ReadPagesAsync(string phrase, IEnumerable<SearchResult> results, Dictionary<string, string> sentences, HashSet<string> tried,
        CancellationToken ct)
    {
        foreach (var r in results.OrderByDescending(r => ContainsPhrase(r.Title + " " + r.Snippet, phrase)).ToList())
        {
            if (tried.Count >= PagesPerVariant || sentences.Count >= EnoughHits) return;
            if (!tried.Add(r.Url)) continue;
            string? text;
            try { text = await ctx.Fetcher.FetchTextAsync(r.Url, Math.Max(ctx.MaxCharsPerPage, 20000), ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiException && !ct.IsCancellationRequested) { continue; }
            if (text != null && SentenceWith(text, phrase) is { } sentence) sentences[r.Url] = sentence;
        }
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+(?=[\p{Lu}\p{N}„""«(])")]
    private static partial Regex SentenceEnd();

    /// <summary>Der Satz, der die Wortfolge wörtlich enthält; zu lange Sätze werden um die Stelle herum gekürzt.</summary>
    internal static string? SentenceWith(string text, string phrase)
    {
        foreach (var raw in SentenceEnd().Split(Regex.Replace(text, @"\s+", " ")))
        {
            var s = raw.Trim();
            if (!ContainsPhrase(s, phrase)) continue;
            if (s.Length <= 400) return s;
            var at = s.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = s.Length / 2;
            var start = Math.Max(0, at - 160);
            var end = Math.Min(s.Length, at + phrase.Length + 160);
            return (start > 0 ? "… " : "") + s[start..end].Trim() + (end < s.Length ? " …" : "");
        }
        return null;
    }

    /// <summary>Wörtliches Vorkommen, unabhängig von Groß-/Kleinschreibung und Akzenten.</summary>
    internal static bool ContainsPhrase(string text, string phrase) =>
        Normalize(text).Contains(Normalize(phrase), StringComparison.Ordinal);

    private static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string VerdictName(UsageVerdict v) => v switch
    {
        UsageVerdict.Common => "üblich",
        UsageVerdict.Occasional => "gelegentlich",
        UsageVerdict.Rare => "selten",
        UsageVerdict.Unattested => "nicht üblich",
        _ => "unklar",
    };

    /// <summary>Klare Aussage aus den gezählten Belegen, mit Verweis auf die Belegnummern.</summary>
    public static string Statement(UsageResult r)
    {
        var main = r.Evidence.FirstOrDefault();
        var hits = main?.ExactHits ?? 0;
        var found = main?.ResultsChecked ?? 0;
        var cites = string.Join(", ", (main?.Examples ?? []).Take(3).Select(e => $"[{e.Number}]"));
        var language = Languages.DisplayName(r.Language);
        var statement = r.Verdict switch
        {
            UsageVerdict.Common => $"„{r.Expression}“ ist in {language} üblich: {hits} wörtliche Belege, z. B. {cites}.",
            UsageVerdict.Occasional => $"„{r.Expression}“ kommt in {language} vor, aber nicht häufig: {hits} wörtliche Belege ({cites}).",
            UsageVerdict.Rare => $"„{r.Expression}“ ist in {language} selten: nur {hits} wörtliche Belege ({cites}).",
            UsageVerdict.Unattested => $"„{r.Expression}“ ist in {language} nicht üblich: kein wörtlicher Beleg in {found} Treffern.",
            _ => $"Zu „{r.Expression}“ habe ich keine verwertbaren Belege gefunden – ob es in {language} üblich ist, bleibt offen.",
        };
        if (r.Recommended is { Length: > 0 } rec && !rec.Equals(r.Expression, StringComparison.OrdinalIgnoreCase)
            && r.Evidence.FirstOrDefault(e => e.Variant.Equals(rec, StringComparison.OrdinalIgnoreCase)) is { ExactHits: > 0 } alt)
            statement += " " + $"Üblich ist „{alt.Variant}“: {alt.ExactHits} wörtliche Belege ({string.Join(", ", alt.Examples.Take(3).Select(e => $"[{e.Number}]"))}).";
        return statement;
    }
}
