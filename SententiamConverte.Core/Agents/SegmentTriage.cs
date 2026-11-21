using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Agents;

/// <summary>Was mit einem Ausdruck geschehen soll: recherchieren, unverändert lassen (Eigenname), oder nicht nötig.</summary>
public enum TriageAction { Research, Keep, Skip }

/// <summary>Einschätzung eines Ausdrucks: was tun, wie viele Minuten Recherche, warum.</summary>
public sealed record TriageItem(string Expression, TriageAction Action, double Minutes, string Why);

/// <summary>
/// Einschätzung eines Segments vor der Recherche: wie schwierig, warum, wie viele Nachbarsegmente davor und danach als
/// Zusammenhang nötig sind und was mit jedem Ausdruck geschehen soll.
/// </summary>
public sealed record SegmentAssessment(string Difficulty, string Reason, int ContextBefore, int ContextAfter, IReadOnlyList<TriageItem> Items)
{
    public bool NeedsResearch => Items.Any(i => i.Action == TriageAction.Research);

    public TriageItem? For(string expression) => Items.FirstOrDefault(i => i.Expression.Equals(expression.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Intelligente Bewertung: Die KI schätzt ein Segment ein, bevor recherchiert wird – wie ein erfahrener Übersetzer, der
/// erst liest und dann entscheidet, wo sich Recherche lohnt. „Hallo“ ist einfach und braucht nichts; ein Rechtsbegriff
/// ohne Entsprechung braucht viel Zeit; ein Eigenname, eine Marke oder ein Produkt, das in beiden Sprachen gleich bleibt,
/// braucht keine Recherche. Dazu: wie viele Nachbarsegmente die Recherche als Zusammenhang braucht (Pronomen, Ellipsen,
/// Überschriften, Aufzählungen).
/// </summary>
public sealed class SegmentTriage(AgentContext ctx)
{
    public const double MinMinutes = 0.5;
    public const double MaxMinutes = 15;
    public const int MaxContext = 3;

    private sealed record Answer(string? Difficulty, string? Reason, int? ContextBefore, int? ContextAfter, List<AnswerItem>? Expressions);
    private sealed record AnswerItem(string? Expression, string? Action, double? Minutes, string? Why);

    /// <summary>Zeit nach der Schwierigkeit, die die Ausdruckssuche schon geschätzt hat (wenn die KI nichts sagt).</summary>
    public static double DefaultMinutes(ResearchDifficulty difficulty) => difficulty switch
    {
        ResearchDifficulty.Easy => 1,
        ResearchDifficulty.Hard => 6,
        _ => 3,
    };

    /// <param name="measured">Was auf diesem Rechner gemessen wurde (<see cref="ResearchStats.PromptNote"/>).</param>
    /// <param name="learned">Gelernte Minuten je Stufe (<see cref="ResearchStats.LearnedMinutes"/>) – gelten statt der festen Annahme.</param>
    public async Task<SegmentAssessment> AssessAsync(string segment, IReadOnlyList<string> before, IReadOnlyList<string> after,
        IReadOnlyList<ResearchItem> items, string sourceLanguage, string targetLanguage, string? topic = null,
        IReadOnlyList<TermHit>? terms = null, CancellationToken ct = default, string? measured = null, Func<ResearchDifficulty, double?>? learned = null)
    {
        var src = Languages.DisplayName(sourceLanguage);
        var tgt = Languages.DisplayName(targetLanguage);
        static string Clip(string s) => s.Length > 300 ? s[..300] + " …" : s;
        var termbase = terms is { Count: > 0 }
            ? "Termbase entries already known: " + string.Join("; ", terms.Take(8).Select(h => $"{h.SourceTerm.Text} = {string.Join(" / ", h.Translations.Select(t => t.Text))}"))
            : "";
        Answer answer;
        try
        {
            answer = JsonExtract.Parse<Answer>(await ctx.Model.AskAsync(
                "You are an experienced translator deciding where research is worth the time. Output only JSON.",
                $$"""
                Translation from {{src}} into {{tgt}}.{{(string.IsNullOrWhiteSpace(topic) ? "" : " Subject field: " + topic)}}
                {{(before.Count == 0 ? "" : "Segments before:\n" + string.Join("\n", before.Select((b, i) => $"[-{before.Count - i}] {Clip(b)}")))}}
                SEGMENT: {{segment}}
                {{(after.Count == 0 ? "" : "Segments after:\n" + string.Join("\n", after.Select((a, i) => $"[+{i + 1}] {Clip(a)}")))}}
                {{termbase}}
                {{measured}}
                Expressions found in the segment: {{string.Join("; ", items.Select(i => $"{i.Expression} ({i.Category}, {i.Difficulty})"))}}

                Decide like a translator who reads first and then researches only where it pays off:
                1. difficulty of the segment for translation: "easy" (greetings, plain everyday wording, only names or numbers),
                   "medium", "hard" or "very_hard" (legal or technical concepts without a clear equivalent, culture-bound content,
                   ambiguity that depends on context). reason: one short sentence in {{ctx.ExplanationLanguage}}.
                2. For EACH expression: action "research", "keep" (a proper name, brand, product, company, person or place that
                   stays unchanged in {{tgt}} – no research) or "skip" (common word, clear from context or termbase);
                   minutes of research for "research": 0.5 to 15 – more for hard concepts, less for well-known terms;
                   why: a few words in {{ctx.ExplanationLanguage}}.
                3. contextBefore / contextAfter: how many neighbouring segments (0 to 3 each) are needed to understand the segment
                   (pronouns, ellipsis, headings, list items, sentences split across segments); 0 if it stands on its own.
                Return: {"difficulty": "...", "reason": "...", "contextBefore": 0, "contextAfter": 0,
                         "expressions": [{"expression": "...", "action": "research", "minutes": 3, "why": "..."}]}
                """, ct));
        }
        catch (AiException ex)
        {
            ctx.Say("Bewertung nicht möglich: " + ex.Message);
            answer = new Answer(null, null, null, null, null);
        }

        // jeder gefundene Ausdruck bekommt eine Entscheidung – fehlt sie, gilt die Schwierigkeit aus der Ausdruckssuche
        var decided = items.Select(i =>
        {
            var a = answer.Expressions?.FirstOrDefault(x => string.Equals(x.Expression?.Trim(), i.Expression.Trim(), StringComparison.OrdinalIgnoreCase));
            var action = a?.Action?.Trim().ToLowerInvariant() switch
            {
                "keep" => TriageAction.Keep,
                "skip" => TriageAction.Skip,
                _ => TriageAction.Research,
            };
            var minutes = action == TriageAction.Research
                ? Math.Clamp(a?.Minutes is > 0 and var m ? m : learned?.Invoke(i.Difficulty) ?? DefaultMinutes(i.Difficulty), MinMinutes, MaxMinutes)
                : 0;
            return new TriageItem(i.Expression, action, minutes, a?.Why?.Trim() ?? "");
        }).ToList();
        var difficulty = answer.Difficulty?.Trim().ToLowerInvariant() is { Length: > 0 } d ? d.Replace(' ', '_') : "medium";
        return new SegmentAssessment(difficulty, answer.Reason?.Trim() ?? "",
            Math.Clamp(answer.ContextBefore ?? 1, 0, Math.Min(MaxContext, before.Count)),
            Math.Clamp(answer.ContextAfter ?? 1, 0, Math.Min(MaxContext, after.Count)), decided);
    }
}
