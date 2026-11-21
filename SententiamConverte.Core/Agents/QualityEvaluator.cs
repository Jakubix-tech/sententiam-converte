using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Fertige Übersetzung bewerten: Die KI geht die Segmente durch und schlägt Fehler im Schema der Fehlerbewertung vor
/// (Kategorie, Schwere, Stelle, Verbesserung, Begründung – angelehnt an MQM). Die Vorschläge tragen „KI“ als Prüfer;
/// der Mensch entscheidet. Gezählt wird nur, was wirklich im Text steht.
/// </summary>
public sealed class QualityEvaluator(AgentContext ctx)
{
    public const string Reviewer = "KI";
    private const int Batch = 15;

    /// <summary>Kategorien der Antwort (englisch, wie in MQM) → Kategorien der Fehlerbewertung.</summary>
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accuracy/mistranslation"] = "Genauigkeit: Fehlübersetzung",
        ["accuracy/omission"] = "Genauigkeit: Auslassung",
        ["accuracy/addition"] = "Genauigkeit: Hinzufügung",
        ["accuracy/untranslated"] = "Genauigkeit: Unübersetzt",
        ["fluency/grammar"] = "Sprache: Grammatik",
        ["fluency/spelling"] = "Sprache: Rechtschreibung",
        ["fluency/punctuation"] = "Sprache: Zeichensetzung",
        ["fluency/unidiomatic"] = "Sprache: Unidiomatisch",
        ["terminology/wrong_term"] = "Terminologie: Falscher Fachbegriff",
        ["terminology/inconsistent"] = "Terminologie: Uneinheitlich",
        ["style/register"] = "Stil: Register oder Ton",
        ["style/client"] = "Stil: Kundenvorgaben",
        ["locale/format"] = "Lokalisierung: Zahlen, Datum, Einheiten",
        ["locale/culture"] = "Lokalisierung: Kulturbezug",
        ["formatting/tags"] = "Formatierung: Tags oder Layout",
    };

    /// <summary>Vorgeschlagene Fehler je Segment.</summary>
    public async Task<Dictionary<int, List<LqaError>>> EvaluateAsync(IReadOnlyList<(int Id, string Source, string Target)> segments,
        string sourceLanguage, string targetLanguage, Projects.DocumentBrief? brief = null, CancellationToken ct = default)
    {
        var result = new Dictionary<int, List<LqaError>>();
        var todo = segments.Select(s => (s.Id, Source: TagText.StripTags(s.Source).Trim(), Target: TagText.StripTags(s.Target).Trim()))
            .Where(s => s.Target.Length > 0).ToList();
        var batches = todo.Chunk(Batch).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            ctx.Say($"Bewerte Teil {b + 1} von {batches.Count} …");
            var prompt = new StringBuilder();
            prompt.AppendLine($"You are a senior reviewer. Evaluate these translations from {Languages.DisplayName(sourceLanguage)} into {Languages.DisplayName(targetLanguage)} " +
                              "the way an MQM review does: mark only real errors – meaning (mistranslation, omission, addition, untranslated), " +
                              "language (grammar, spelling, punctuation, unidiomatic), terminology, style, locale conventions, formatting.");
            if (brief?.Instructions(targetLanguage) is { Length: > 0 } about)
            {
                prompt.AppendLine("Agreed for this document (deviations are style/client errors):");
                prompt.Append(about);
            }
            prompt.AppendLine("Severity: minor (does not hinder understanding), major (changes or obscures the meaning), critical (could cause harm, legal or safety problems).");
            prompt.AppendLine($$"""Reply with JSON only: {"errors": [{"id": 3, "category": "accuracy/mistranslation", "severity": "major", "span": "exact wrong words from the translation (for an omission: from the source)", "suggestion": "better wording (only the replacement for span)", "explanation": "short reason in {{ctx.ExplanationLanguage}}"}]}""");
            prompt.AppendLine("Categories: " + string.Join(", ", Categories.Keys));
            prompt.AppendLine("An empty list if everything is correct. Do not report preferences.");
            prompt.AppendLine();
            foreach (var (id, source, target) in batches[b])
                prompt.AppendLine($"[{id}] Source: {source}\n     Translation: {target}");
            var reply = await ctx.Model.AskAsync("You review translations strictly and fairly.", prompt.ToString(), ct);
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["errors"] is not JsonArray arr) continue;
            var byId = batches[b].ToDictionary(s => s.Id);
            foreach (var e in arr)
            {
                var id = (int?)e?["id"] ?? 0;
                if (!byId.TryGetValue(id, out var seg)) continue;
                var span = ((string?)e?["span"] ?? "").Trim();
                if (span.Length > 0 && !seg.Target.Contains(span, StringComparison.OrdinalIgnoreCase) && !seg.Source.Contains(span, StringComparison.OrdinalIgnoreCase))
                    continue;                                                                       // nur, was wirklich dasteht
                var category = Categories.GetValueOrDefault(((string?)e?["category"] ?? "").Trim()) ?? "Genauigkeit: Fehlübersetzung";
                var severity = ((string?)e?["severity"] ?? "").Trim().ToLowerInvariant() switch
                {
                    "critical" => LqaSeverity.Critical,
                    "major" => LqaSeverity.Major,
                    "neutral" => LqaSeverity.Neutral,
                    _ => LqaSeverity.Minor,
                };
                var suggestion = ((string?)e?["suggestion"])?.Trim();
                var explanation = ((string?)e?["explanation"] ?? "").Trim();
                var note = (span.Length > 0 ? $"„{span}“" + (suggestion is { Length: > 0 } s && s != span ? $" → „{s}“" : "") + ": " : "") + explanation;
                (result.TryGetValue(id, out var list) ? list : result[id] = new()).Add(new LqaError(category, severity, note, Reviewer, DateTime.UtcNow));
            }
        }
        return result;
    }
}
