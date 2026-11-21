using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

/// <summary>Reicht eine Recherche für die Übersetzung? Wenn nicht: was fehlt (für eine gezielte zweite Runde).</summary>
public sealed record SufficiencyVerdict(bool Sufficient, string Missing, string Why);

/// <summary>
/// Nach einer Recherche prüfen, ob sie für die Übersetzung reicht. Belegt (in einer Quelle der Zielsprache wörtlich) und
/// eindeutig (ein Kandidat in zwei unabhängigen Quellen): reicht – dafür braucht es keine KI. Sonst sagt die KI, was fehlt
/// (etwa die Definition im Zielland, die Verwendung in diesem Zusammenhang, die regionale Variante); danach sucht eine
/// zweite Runde gezielt danach.
/// </summary>
public sealed class ResearchCheck(AgentContext ctx)
{
    private sealed record Answer(bool? Sufficient, string? Missing, string? Why);

    /// <summary>Belegt und eindeutig – reicht ohne weitere Prüfung.</summary>
    public static bool ClearlySufficient(TermDossier d) => DossierStore.IsEvidenced(d) && d.Conclusive != false;

    public async Task<SufficiencyVerdict> CheckAsync(TermDossier d, string? context, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (ClearlySufficient(d)) return new SufficiencyVerdict(true, "", "belegt und eindeutig");
        var src = Languages.DisplayName(sourceLanguage);
        var tgt = Languages.DisplayName(targetLanguage);
        var evidenced = DossierStore.IsEvidenced(d);
        var answer = JsonExtract.Parse<Answer>(await ctx.Model.AskAsync(
            "You check terminology research like a careful reviewer. Output only JSON.",
            $$"""
            Expression: "{{d.Expression}}" ({{src}} → {{tgt}})
            {{(string.IsNullOrWhiteSpace(context) ? "" : "Context: " + context)}}
            Meaning found: {{AiTranslator.Short(d.Meaning, 300) ?? "-"}}
            Recommended: {{d.Recommended ?? "-"}}
            Candidates: {{string.Join(", ", d.Candidates.Select(c => $"{c.Term} ({c.Equivalence})"))}}
            Verified sources: {{d.Sources.Count}}; recommended term found verbatim in a {{tgt}} source: {{(evidenced ? "yes" : "NO")}};
            one candidate in two independent {{tgt}} sources: {{(d.Conclusive == true ? "yes" : "no")}}
            Warnings: {{string.Join(" | ", d.Warnings.Take(4))}}

            A recommendation without a verbatim {{tgt}} source is not used for the translation.
            Is this research sufficient to translate the expression in this context with confidence? If not, say precisely what
            is still missing (e.g. the definition in the target country's system, use in this kind of text, the regional variant,
            whether a candidate is a false friend) – in {{ctx.ExplanationLanguage}}, one sentence each.
            Return: {"sufficient": true|false, "missing": "...", "why": "..."}
            """, ct));
        // ohne Beleg reicht es nie – unbelegte Empfehlungen gehen nicht in die Übersetzung
        var sufficient = evidenced && answer.Sufficient == true;
        return new SufficiencyVerdict(sufficient, sufficient ? "" : answer.Missing?.Trim() is { Length: > 0 } m ? m : "ein Beleg in der Zielsprache",
            answer.Why?.Trim() ?? "");
    }
}
