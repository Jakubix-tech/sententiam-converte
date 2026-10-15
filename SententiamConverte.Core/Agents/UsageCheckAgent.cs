using System.Globalization;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

public enum UsageVerdict { Common, Occasional, Rare, Unattested, Uncertain }

public sealed record UsageExample(string Url, string Title, string Snippet);

/// <summary>Belege für eine Formulierung: wie viele Suchtreffer sie wörtlich enthalten, mit Beispielen.</summary>
public sealed record UsageEvidence(string Variant, int ResultsChecked, int ExactHits, List<UsageExample> Examples);

public sealed record UsageResult(
    string Expression, string Language, UsageVerdict Verdict, string? Recommended,
    string Explanation, List<UsageEvidence> Evidence);

/// <summary>
/// Prüft, ob ein Ausdruck in einer Sprache tatsächlich so verwendet wird:
/// sucht die genaue Wortfolge (und gleichbedeutende Alternativen) im Netz, zählt wörtliche Belege
/// und lässt die KI anhand der Belege urteilen.
/// </summary>
public sealed class UsageCheckAgent(AgentContext ctx)
{
    private sealed record Alternatives(List<string> Variants);
    private sealed record VerdictDto(string Verdict, string? Recommended, string Explanation);

    public async Task<UsageResult> RunAsync(
        string expression, string language, string? topic = null, IReadOnlyList<string>? alternatives = null,
        bool suggestAlternatives = true, CancellationToken ct = default)
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

        var evidence = new List<UsageEvidence>();
        foreach (var v in variants)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Say($"Suche Belege für „{v}“ …");
            List<SearchResult> results;
            try { results = await search.SearchAsync($"\"{v}\"", language, 10, ct); }
            catch (Exception ex) when (ex is HttpRequestException or AiException)
            {
                ctx.Say($"Suche fehlgeschlagen: {ex.Message}");
                results = [];
            }
            var exact = results.Where(r => ContainsPhrase(r.Title + " " + r.Snippet, v)).ToList();
            evidence.Add(new UsageEvidence(v, results.Count, exact.Count,
                exact.Take(5).Select(r => new UsageExample(r.Url, r.Title, r.Snippet)).ToList()));
            ctx.Say($"  {exact.Count} von {results.Count} Treffern enthalten die Wortfolge wörtlich.");
        }

        ctx.Say("Bewerte die Belege …");
        var report = new StringBuilder();
        foreach (var e in evidence)
        {
            report.AppendLine($"Variant \"{e.Variant}\": {e.ExactHits} of {e.ResultsChecked} search results contain it verbatim.");
            foreach (var ex in e.Examples) report.AppendLine($"  - {ex.Url}: {ex.Snippet}");
        }
        var dto = JsonExtract.Parse<VerdictDto>(await ctx.Model.AskAsync(
            "You are an experienced terminologist and corpus linguist. Base your judgement on the evidence, not on guesses. Output only JSON.",
            $$"""
            Language: {{Languages.DisplayName(language)}} ({{language}})
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
            Expression under test: "{{expression}}"

            Web evidence (exact-phrase searches, top results):
            {{report}}

            Is the expression under test actually used by native speakers in this subject field?
            verdict: one of "common", "occasional", "rare", "unattested", "uncertain" (use "uncertain" if the search returned nothing usable).
            recommended: the variant you would recommend (may be the expression itself), or null.
            explanation: 2-4 sentences in {{ctx.ExplanationLanguage}}, citing the evidence.
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
        return new UsageResult(expression, language, verdict, dto.Recommended, dto.Explanation, evidence);
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
        UsageVerdict.Unattested => "nicht belegt",
        _ => "unklar",
    };
}
