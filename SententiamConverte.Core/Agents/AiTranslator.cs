using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Maschinelle Übersetzung eines Segments mit Kontext: TM-Treffer, Terminologie (mit Definition), Wissen, Recherche zu
/// den Ausdrücken des Satzes, Nachbarsegmente und Fachgebiet gehen in die Anfrage ein. Das Programm sucht das Passende
/// heraus – auch kleine lokale Modelle bekommen so nur, was zu diesem Satz gehört. Tags bleiben erhalten.
/// </summary>
public sealed class AiTranslator(AgentContext ctx)
{
    public async Task<string> TranslateAsync(
        string source, string sourceLanguage, string targetLanguage, string? topic,
        IReadOnlyList<TmMatch>? matches = null, IReadOnlyList<TermHit>? terms = null,
        string? previous = null, string? next = null, string? styleNotes = null, CancellationToken ct = default,
        IReadOnlyList<KnowledgeEntry>? knowledge = null, Projects.DocumentBrief? brief = null, IReadOnlyList<TermDossier>? research = null)
    {
        // Klassische maschinelle Übersetzung: nur der Text geht hin (Kontext, Terminologie, Anweisungen versteht sie nicht)
        if (Privacy.PrivacyContext.Unwrap(ctx.Model) is IMachineTranslator mt)
        {
            var privacy = ctx.Model as Privacy.PrivacyChatModel;
            var raw = await mt.TranslateAsync(privacy?.MaskText(source) ?? source, sourceLanguage, targetLanguage, ct);
            var translated = Clean(source, privacy?.UnmaskText(raw) ?? raw);
            return privacy != null ? Privacy.DateLocalizer.Localize(translated, source, targetLanguage) : translated;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Translate from {Languages.DisplayName(sourceLanguage)} ({sourceLanguage}) into {Languages.DisplayName(targetLanguage)} ({targetLanguage}).");
        if (!string.IsNullOrWhiteSpace(topic)) sb.AppendLine($"Subject / domain: {topic}");
        if (!string.IsNullOrWhiteSpace(styleNotes)) sb.AppendLine($"Style instructions: {styleNotes}");
        if (Projects.CorrectionLearning.Instructions(targetLanguage) is { } learned)
        {
            sb.AppendLine("House style learned from the translator's corrections (follow it):");
            sb.AppendLine(learned);
        }
        if (brief?.Instructions(targetLanguage) is { Length: > 0 } about)
        {
            sb.AppendLine("About the whole document (follow this consistently in every sentence):");
            sb.Append(about);
        }
        if (terms is { Count: > 0 })
        {
            sb.AppendLine("Mandatory terminology (use exactly; never use terms marked FORBIDDEN):");
            foreach (var t in terms.Where(t => t.Translations.Count > 0))
                sb.AppendLine($"- {t.SourceTerm.Text} → {string.Join(" / ", t.Translations.Select(x => x.Text + (x.Status == TermStatus.Forbidden ? " (FORBIDDEN)" : "")))}" +
                              (Short(t.Concept?.Definition) is { } definition ? $" (meaning: {definition})" : ""));
        }
        AppendResearch(sb, research);
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
        // Die Tag-Regel nur, wenn der Satz Tags hat: sonst erfinden kleine Modelle welche (bis hin zu Endlosschleifen)
        if (TagText.Tags(source).Any())
            sb.AppendLine("Formatting tags such as ⟨1⟩, ⟨/1⟩ and ⟨2/⟩ must appear in the translation exactly once each, around the corresponding words.");
        sb.AppendLine("Reply with the translation only, no quotes, no explanation.");
        sb.AppendLine();
        sb.AppendLine("Text:");
        sb.AppendLine(source);

        const string system = "You are a professional translator. You produce publication-ready translations that read like native text.";
        var result = Clean(source, await ctx.Model.AskAsync(system, sb.ToString(), ct));
        // Datenschutz: die KI sah Daten nur als Platzhalter – in die Schreibweise der Zielsprache bringen
        if (ctx.Model is Privacy.PrivacyChatModel) result = Privacy.DateLocalizer.Localize(result, source, targetLanguage);
        // Kostenlose Modelle hängen gern eigene Sätze an. Im Verbund übersetzen bei einer auffälligen Antwort bis zu zwei
        // andere Modelle noch einmal; es gilt die erste unauffällige Fassung, sonst die knappste.
        if (ctx.Model is IParallelChatModel { Parallelism: > 1 } pool)
            for (var retry = 0; retry < Math.Min(2, pool.Parallelism - 1) && Inflated(source, result); retry++)
            {
                var again = Clean(source, await ctx.Model.AskAsync(system, sb.ToString(), ct));
                if (!Inflated(source, again) || TagText.StripTags(again).Length < TagText.StripTags(result).Length) result = again;
            }
        return result;
    }

    /// <summary>Mehr Sätze als das Original oder deutlich länger – meist hat das Modell etwas hinzugedichtet.</summary>
    internal static bool Inflated(string source, string translation) =>
        TooLong(source, translation) || Sentences(translation) > Sentences(source);

    private static int Sentences(string text) =>
        System.Text.RegularExpressions.Regex.Matches(TagText.StripTags(text).Trim(), @"\p{L}[.!?…]+(?=\s|$)").Count;

    private static string Clean(string source, string answer) => TagText.Repair(source, answer.Trim().Trim('"', '„', '“').Trim());

    /// <summary>Deutlich länger als der Ausgangstext – meist hat das Modell etwas hinzugedichtet.</summary>
    internal static bool TooLong(string source, string translation)
    {
        var s = TagText.StripTags(source).Length;
        return s >= 25 && TagText.StripTags(translation).Length > s * 1.6 + 5;
    }

    /// <summary>
    /// Recherche zum Dokument: Empfehlung, Bedeutung und was nicht passt, für die Ausdrücke dieses Satzes. Ohne Empfehlung
    /// (Recherche ohne Ergebnis) bleibt ein Dossier draußen.
    /// </summary>
    internal static void AppendResearch(StringBuilder sb, IReadOnlyList<TermDossier>? research)
    {
        var usable = research?.Where(d => !string.IsNullOrWhiteSpace(d.Recommended)).Take(5).ToList();
        if (usable is not { Count: > 0 }) return;
        sb.AppendLine("Researched expressions in this sentence (use the recommended rendering unless the sentence clearly needs another):");
        foreach (var d in usable)
        {
            var line = new StringBuilder($"- {d.Expression} → {d.Recommended!.Trim()}");
            if (Short(d.Rendering, 120) is { } rendering && !rendering.Equals(d.Recommended.Trim(), StringComparison.OrdinalIgnoreCase))
                line.Append($" (in context: {rendering})");
            if (Short(d.Meaning) is { } meaning) line.Append($"; meaning: {meaning}");
            if (Short(d.Strategy, 100) is { } strategy) line.Append($"; strategy: {strategy}");
            var avoid = d.Candidates.Where(c => c.Equivalence == Equivalence.None && c.Term.Trim().Length > 0).Select(c => c.Term.Trim()).Take(3).ToList();
            if (avoid.Count > 0) line.Append($"; avoid: {string.Join(", ", avoid)}");
            sb.AppendLine(line.ToString());
        }
    }

    /// <summary>Eine Zeile, höchstens <paramref name="max"/> Zeichen, an einer Wortgrenze gekürzt.</summary>
    public static string? Short(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (line.Length <= max) return line;
        var cut = line.LastIndexOf(' ', max);
        return line[..(cut > max / 2 ? cut : max)].TrimEnd(',', ';', ':', ' ') + " …";
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
