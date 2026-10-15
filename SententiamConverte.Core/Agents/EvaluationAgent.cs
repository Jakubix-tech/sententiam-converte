using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Agents;

public sealed record TranslationCandidate(string Label, string Text);

public sealed class CandidateScore
{
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Gesamtnote 0–100.</summary>
    public int Score { get; set; }
    public int Accuracy { get; set; }
    public int Fluency { get; set; }
    public int Terminology { get; set; }
    public int Style { get; set; }
    public string Comment { get; set; } = "";
    public List<string> Issues { get; set; } = new();
}

public sealed record EvaluationResult(List<CandidateScore> Scores, string? BestLabel, string? Suggestion, string Summary, List<UsageResult> UsageChecks);

/// <summary>
/// Bewertet mehrere Übersetzungsvarianten eines Segments (aus TM, KI oder vom Übersetzer)
/// nach Richtigkeit, Sprachfluss, Terminologie und Stil. Auf Wunsch werden fragliche Wendungen
/// vorher mit dem <see cref="UsageCheckAgent"/> im Netz überprüft.
/// </summary>
public sealed class EvaluationAgent(AgentContext ctx)
{
    private sealed record Doubts(List<string> Phrases);
    private sealed record EvalDto(List<CandidateScore> Scores, string? Best, string? Suggestion, string? Summary);

    public async Task<EvaluationResult> RunAsync(
        string source, string sourceLanguage, string targetLanguage, IReadOnlyList<TranslationCandidate> candidates,
        string? topic = null, IReadOnlyList<TermHit>? terms = null, bool checkUsage = false, CancellationToken ct = default,
        IReadOnlyList<KnowledgeEntry>? knowledge = null)
    {
        if (candidates.Count == 0) throw new AiException("Keine Übersetzungen zum Bewerten.");
        var candidateList = new StringBuilder();
        foreach (var c in candidates) candidateList.AppendLine($"[{c.Label}] {c.Text}");

        var termInfo = terms is { Count: > 0 }
            ? "Binding terminology: " + string.Join("; ", terms.Where(t => t.Translations.Count > 0)
                .Select(t => $"{t.SourceTerm.Text} = {string.Join(" / ", t.Translations.Select(x => x.Text + (x.Status == TermStatus.Forbidden ? " (FORBIDDEN)" : "")))}"))
            : "";

        var usageChecks = new List<UsageResult>();
        if (checkUsage && ctx.Search != null)
        {
            ctx.Say("Suche fragliche Wendungen …");
            try
            {
                var doubts = JsonExtract.Parse<Doubts>(await ctx.Model.AskAsync(
                    "You are a reviewer for professional translations. Output only JSON.",
                    $$"""
                    Target language: {{Languages.DisplayName(targetLanguage)}} ({{targetLanguage}})
                    {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject: " + topic)}}
                    Source: {{TagText.StripTags(source)}}
                    Candidate translations:
                    {{candidateList}}
                    Which word combinations in the candidates (max. 3, 2-5 words each) might be unidiomatic, calques or
                    non-standard terminology and should be verified against real usage? Copy them exactly.
                    Return: {"phrases": ["...", "..."]}  (empty list if none)
                    """, ct));
                var usage = new UsageCheckAgent(ctx);
                foreach (var phrase in doubts.Phrases.Where(p => p.Trim().Length > 0).Take(3))
                    usageChecks.Add(await usage.RunAsync(phrase, targetLanguage, topic, suggestAlternatives: true, ct: ct));
            }
            catch (AiException ex) { ctx.Say("Usus-Prüfung übersprungen: " + ex.Message); }
        }

        var usageInfo = new StringBuilder();
        foreach (var u in usageChecks)
        {
            usageInfo.AppendLine($"\"{u.Expression}\": verdict {u.Verdict}, recommended \"{u.Recommended}\". {u.Explanation}");
            foreach (var e in u.Evidence) usageInfo.AppendLine($"  \"{e.Variant}\": {e.ExactHits}/{e.ResultsChecked} verbatim web hits");
        }

        var knowledgeInfo = new StringBuilder();
        AiTranslator.AppendKnowledge(knowledgeInfo, knowledge);

        ctx.Say("Bewerte die Übersetzungen …");
        var dto = JsonExtract.Parse<EvalDto>(await ctx.Model.AskAsync(
            "You are a strict senior reviewer (ISO 17100) for professional translations. Judge meaning first, then language. Output only JSON.",
            $$"""
            Source language: {{Languages.DisplayName(sourceLanguage)}} ({{sourceLanguage}})
            Target language: {{Languages.DisplayName(targetLanguage)}} ({{targetLanguage}})
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject / domain: " + topic)}}
            {{termInfo}}
            {{knowledgeInfo}}
            Tags like ⟨1⟩…⟨/1⟩ and ⟨2/⟩ are formatting placeholders and must be kept.

            Source segment: {{source}}

            Candidates:
            {{candidateList}}
            {{(usageInfo.Length > 0 ? "Web usage evidence for doubtful phrases:\n" + usageInfo : "")}}

            Score every candidate 0-100 for accuracy, fluency, terminology, style and overall (overall is not an average:
            a meaning error caps it at 60). Comment and issues in {{ctx.ExplanationLanguage}}.
            If none is fully satisfactory, give an improved suggestion (keep the tags), else null.
            Return: {"scores": [{"label": "...", "score": 0, "accuracy": 0, "fluency": 0, "terminology": 0, "style": 0,
                     "comment": "...", "issues": ["..."]}], "best": "<label>", "suggestion": "...", "summary": "..."}
            """, ct));

        foreach (var s in dto.Scores)
            s.Text = candidates.FirstOrDefault(c => c.Label == s.Label)?.Text ?? s.Text;
        return new EvaluationResult(dto.Scores.OrderByDescending(s => s.Score).ToList(), dto.Best,
            string.IsNullOrWhiteSpace(dto.Suggestion) ? null : dto.Suggestion, dto.Summary ?? "", usageChecks);
    }
}
