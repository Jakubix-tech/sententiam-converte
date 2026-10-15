using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Maschinelle Übersetzung eines Segments mit Kontext: TM-Treffer, Terminologie,
/// Nachbarsegmente und Fachgebiet gehen in die Anfrage ein. Tags bleiben erhalten.
/// </summary>
public sealed class AiTranslator(AgentContext ctx)
{
    public async Task<string> TranslateAsync(
        string source, string sourceLanguage, string targetLanguage, string? topic,
        IReadOnlyList<TmMatch>? matches = null, IReadOnlyList<TermHit>? terms = null,
        string? previous = null, string? next = null, string? styleNotes = null, CancellationToken ct = default,
        IReadOnlyList<KnowledgeEntry>? knowledge = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Translate from {Languages.DisplayName(sourceLanguage)} ({sourceLanguage}) into {Languages.DisplayName(targetLanguage)} ({targetLanguage}).");
        if (!string.IsNullOrWhiteSpace(topic)) sb.AppendLine($"Subject / domain: {topic}");
        if (!string.IsNullOrWhiteSpace(styleNotes)) sb.AppendLine($"Style instructions: {styleNotes}");
        if (terms is { Count: > 0 })
        {
            sb.AppendLine("Mandatory terminology (use exactly; never use terms marked FORBIDDEN):");
            foreach (var t in terms.Where(t => t.Translations.Count > 0))
                sb.AppendLine($"- {t.SourceTerm.Text} → {string.Join(" / ", t.Translations.Select(x => x.Text + (x.Status == TermStatus.Forbidden ? " (FORBIDDEN)" : "")))}");
        }
        AppendKnowledge(sb, knowledge);
        if (matches is { Count: > 0 })
        {
            sb.AppendLine("Similar earlier translations from the translation memory (reuse wording where it fits):");
            foreach (var m in matches.Take(3))
                sb.AppendLine($"- [{m.Score}%] {m.Unit.Source}\n  → {m.Unit.Target}");
        }
        if (previous != null) sb.AppendLine($"Previous sentence (context only): {previous}");
        if (next != null) sb.AppendLine($"Next sentence (context only): {next}");
        sb.AppendLine();
        sb.AppendLine("Formatting tags such as ⟨1⟩, ⟨/1⟩ and ⟨2/⟩ must appear in the translation exactly once each, around the corresponding words.");
        sb.AppendLine("Reply with the translation only, no quotes, no explanation.");
        sb.AppendLine();
        sb.AppendLine("Text:");
        sb.AppendLine(source);

        var answer = await ctx.Model.AskAsync(
            "You are a professional translator. You produce publication-ready translations that read like native text.",
            sb.ToString(), ct);
        answer = answer.Trim().Trim('"', '„', '“').Trim();
        return TagText.EnsureTags(source, answer);
    }

    /// <summary>Wissen aus den Wissensdatenbanken (Fachwissen, Stilregeln, Sprachgebrauch) als Kontext.</summary>
    internal static void AppendKnowledge(StringBuilder sb, IReadOnlyList<KnowledgeEntry>? knowledge)
    {
        if (knowledge is not { Count: > 0 }) return;
        sb.AppendLine("Background knowledge and house rules from the team's knowledge base (follow style rules; use facts only if relevant):");
        foreach (var k in knowledge.Take(8))
            sb.AppendLine($"- [{k.Kind}, {k.Language}] {k.Title}: {k.Content}");
    }
}
