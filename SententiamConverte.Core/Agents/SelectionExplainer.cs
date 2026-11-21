using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Was die KI zu einer Markierung sagt: Bedeutung im Satz, Grammatik, sprachwissenschaftliche Einordnung, die
/// entsprechende Stelle in der anderen Sprache (nur, wenn sie dort wörtlich steht), ob sie trifft, und Alternativen.
/// </summary>
public sealed record SelectionExplanation(string Meaning, string Grammar, string Linguistics, string? Counterpart, string? Assessment,
    IReadOnlyList<string> Alternatives);

/// <summary>
/// Markierten Text erklären – wie ein Sprachwissenschaftler mit Übersetzungserfahrung: was die Wörter in diesem Satz
/// bedeuten, welche Form und Funktion sie haben (Wortart, Flexion, Satzglied, Konstruktion, Valenz), Register,
/// Kollokation, feste Wendung, Wortbildung – und wie sie in der anderen Sprache wiedergegeben sind. Was die Recherche zu
/// diesen Wörtern schon belegt hat und die Termbank-Einträge gehen ein. Eine Einschätzung der KI ohne eigene Quellen.
/// </summary>
public sealed class SelectionExplainer(AgentContext ctx)
{
    private sealed record Answer(string? Meaning, string? Grammar, string? Linguistics, string? Counterpart, string? Assessment, List<string>? Alternatives);

    /// <param name="inTarget">Markiert ist in der Übersetzung (sonst im Ausgangstext).</param>
    public async Task<SelectionExplanation> ExplainAsync(string selection, bool inTarget, string source, string target,
        string sourceLanguage, string targetLanguage, string? topic = null, IReadOnlyList<TermDossier>? research = null,
        IReadOnlyList<TermHit>? terms = null, CancellationToken ct = default)
    {
        var src = Languages.DisplayName(sourceLanguage);
        var tgt = Languages.DisplayName(targetLanguage);
        var source0 = TagText.StripTags(source).Trim();
        var target0 = TagText.StripTags(target).Trim();
        var side = inTarget ? $"translation ({tgt})" : $"source text ({src})";
        var other = inTarget ? $"source text ({src})" : $"translation ({tgt})";
        var known = research is { Count: > 0 }
            ? "Research already done on these words:\n" + string.Join("\n", research.Take(5).Select(d =>
                $"- {d.Expression} → {d.Recommended ?? "?"} ({(DossierStore.IsEvidenced(d) ? "evidenced in target-language sources" : "not evidenced")})" +
                (string.IsNullOrWhiteSpace(d.Meaning) ? "" : $"; meaning: {AiTranslator.Short(d.Meaning, 200)}")))
            : "";
        var termbase = terms is { Count: > 0 }
            ? "Termbase: " + string.Join("; ", terms.Take(8).Select(h => $"{h.SourceTerm.Text} = {string.Join(" / ", h.Translations.Select(t => t.Text))}"))
            : "";
        var answer = JsonExtract.Parse<Answer>(await ctx.Model.AskAsync(
            "You are a linguist and an experienced translator. You explain precisely and briefly, without padding. Output only JSON.",
            $$"""
            Source text ({{src}}): {{source0}}
            Translation ({{tgt}}): {{(target0.Length == 0 ? "(not yet translated)" : target0)}}
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
            {{known}}
            {{termbase}}

            The user marked these words in the {{side}}: "{{selection}}"

            Explain them in {{ctx.ExplanationLanguage}}:
            1. meaning: what they mean in THIS sentence (not a general dictionary entry).
            2. grammar: word class(es), form (case, number, gender, tense, mood, voice, degree …), syntactic function and the
               construction they belong to (e.g. an infinitive clause depending on a verb), valency if relevant.
            3. linguistics: register and style, collocation or fixed expression, word formation, connotation or ambiguity,
               false friends – only what applies.
            4. counterpart: the words in the {{other}} that correspond, copied EXACTLY (empty if there are none or the text is missing).
            5. assessment: does the translation render it correctly and idiomatically? Name concrete problems, or say it fits.
            6. alternatives: up to 3 better or equally good renderings in {{tgt}} (empty if the current one is best).
            Return: {"meaning": "...", "grammar": "...", "linguistics": "...", "counterpart": "...", "assessment": "...", "alternatives": ["..."]}
            """, ct));

        // die Gegenstelle nur, wenn sie wirklich so in der anderen Sprache steht
        var otherText = inTarget ? source0 : target0;
        var counterpart = answer.Counterpart?.Trim().Trim('"', '„', '“', '”', '«', '»') is { Length: > 0 } c
                          && otherText.Contains(c, StringComparison.OrdinalIgnoreCase) ? c : null;
        return new SelectionExplanation(answer.Meaning?.Trim() ?? "", answer.Grammar?.Trim() ?? "", answer.Linguistics?.Trim() ?? "",
            counterpart, string.IsNullOrWhiteSpace(answer.Assessment) ? null : answer.Assessment.Trim(),
            (answer.Alternatives ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList());
    }
}
