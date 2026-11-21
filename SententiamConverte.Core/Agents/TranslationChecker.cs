using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

/// <summary>Befund einer Prüfmethode: Bewertung 0..1, was auffiel, und Einzelheiten (z. B. die Rückübersetzung).</summary>
public sealed record CheckFinding(string Method, double Score, IReadOnlyList<string> Issues, string? Detail);

/// <summary>Gesamturteil über eine Übersetzung mit den Befunden der einzelnen Methoden.</summary>
public sealed record TranslationCheck(double Score, List<CheckFinding> Findings, List<string> Alternatives, bool SingleModel)
{
    public string Verdict => Score >= 0.85 ? "plausibel" : Score >= 0.6 ? "bitte prüfen" : "wahrscheinlich fehlerhaft";

    /// <summary>Unter 0,75: als Meldung aufnehmen.</summary>
    public bool Doubtful => Score < 0.75;

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Urteil: {Verdict} ({Score * 100:0} %)");
        foreach (var f in Findings)
        {
            sb.AppendLine();
            sb.AppendLine($"{f.Method}: {f.Score * 100:0} %");
            foreach (var issue in f.Issues) sb.AppendLine("• " + issue);
            if (f.Method == "Rückübersetzung" && f.Detail != null) sb.AppendLine($"Rückübersetzung: {f.Detail}");
        }
        if (SingleModel && Findings.Any(f => f.Method == "Abgleich mehrerer Modelle"))
            sb.AppendLine("\n(Nur ein Modell eingerichtet – für einen echten Abgleich den kostenlosen Verbund verwenden.)");
        return sb.ToString().Trim();
    }
}

/// <summary>
/// Prüft, ob eine Übersetzung den Sinn des Originals trifft – zwei Methoden, die über die formale QA hinausgehen:
/// <list type="bullet">
/// <item><b>Rückübersetzung</b>: Ein Modell übersetzt die Übersetzung zurück, ohne das Original zu kennen; dann wird
/// der Sinn mit dem Original verglichen (fehlt etwas, kam etwas hinzu, hat sich ein Fakt verändert?).</item>
/// <item><b>Abgleich mehrerer Modelle</b>: Mehrere Modelle übersetzen unabhängig; weicht die Übersetzung im Sinn von
/// ihnen ab, ist sie verdächtig. Im Verbund kommen die Alternativen von verschiedenen Modellen, gleichzeitig.</item>
/// </list>
/// Das Ergebnis ist ein Hinweis für die Prüfung durch einen Menschen, kein Ersatz dafür.
/// </summary>
public sealed class TranslationChecker(AgentContext ctx)
{
    public const string BackTranslation = "Rückübersetzung";
    public const string Consensus = "Abgleich mehrerer Modelle";

    private bool Parallel => ctx.Model is IParallelChatModel { Parallelism: > 1 };

    private static (double Score, List<string> Issues) ParseJudge(string reply)
    {
        try
        {
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json) is not JsonObject o) return (0.5, ["Die KI hat keine auswertbare Bewertung geliefert."]);
            var score = o["score"] is JsonValue v && v.TryGetValue<double>(out var s) ? Math.Clamp(s, 0, 1) : 0.5;
            var issues = (o["issues"] as JsonArray ?? [])
                .Select(i => (string?)(i as JsonValue)).OfType<string>().Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            return (score, issues);
        }
        catch (System.Text.Json.JsonException) { return (0.5, ["Die KI hat keine auswertbare Bewertung geliefert."]); }
    }

    public async Task<CheckFinding> BackTranslationAsync(string source, string target, string sourceLanguage, string targetLanguage,
        CancellationToken ct = default)
    {
        var back = (await ctx.WaitAsync(ctx.Model.AskAsync(
            "You are a translator. Translate as literally as possible: keep every detail, add nothing, explain nothing. Reply with the translation only.",
            $"Translate from {Languages.DisplayName(targetLanguage)} into {Languages.DisplayName(sourceLanguage)}:\n{TagText.StripTags(target)}", ct), ct))
            .Trim().Trim('"', '„', '“').Trim();
        var judge = await ctx.WaitAsync(ctx.Model.AskAsync(
            "You compare meanings precisely. Output only JSON.",
            $$"""
            Original ({{sourceLanguage}}): {{TagText.StripTags(source)}}
            Back-translation of a translation ({{sourceLanguage}}): {{back}}
            Does the back-translation say the same as the original? Ignore wording, word order and style; look only at the
            meaning: missing information, added information, changed facts (numbers, units, negation, who does what), wrong terms.
            score: 1 = same meaning, 0.7 = only a small nuance differs, 0.4 = something important is missing or changed, 0 = different meaning.
            issues: short notes in {{ctx.ExplanationLanguage}} naming what differs; empty list if nothing.
            Return: {"score": 0.9, "issues": ["…"]}
            """, ct), ct);
        var (score, issues) = ParseJudge(judge);
        return new CheckFinding(BackTranslation, score, issues, back);
    }

    /// <summary>Unabhängige Übersetzungen holen (im Verbund von verschiedenen Modellen) und die Übersetzung im Sinn daran messen.</summary>
    public async Task<(CheckFinding Finding, List<string> Alternatives)> ConsensusAsync(string source, string target, string sourceLanguage,
        string targetLanguage, string? topic, int count = 3, CancellationToken ct = default)
    {
        var translator = new AiTranslator(ctx);
        var alternatives = (await Task.WhenAll(Enumerable.Range(0, Math.Max(1, count))
                .Select(_ => translator.TranslateAsync(source, sourceLanguage, targetLanguage, topic, ct: ct))))
            .Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList();
        if (alternatives.Count == 0) return (new CheckFinding(Consensus, 0.5, ["Keine unabhängige Übersetzung erhalten."], null), alternatives);
        if (alternatives.Any(a => string.Equals(TagText.StripTags(a).Trim(), TagText.StripTags(target).Trim(), StringComparison.OrdinalIgnoreCase)))
            return (new CheckFinding(Consensus, 1, [], string.Join("\n", alternatives)), alternatives);     // wörtlich gleich: kein Urteil nötig

        var list = new StringBuilder();
        for (var i = 0; i < alternatives.Count; i++) list.AppendLine($"{i + 1}. {TagText.StripTags(alternatives[i])}");
        var judge = await ctx.WaitAsync(ctx.Model.AskAsync(
            "You review translations. Output only JSON.",
            $$"""
            Original ({{sourceLanguage}}): {{TagText.StripTags(source)}}
            Translation under review ({{targetLanguage}}): {{TagText.StripTags(target)}}
            Independent translations by other translators:
            {{list}}
            Does the translation under review convey the same meaning as the independent translations agree on? Different
            but correct wording is fine. Flag meaning differences, terms where it disagrees with most others, missing or added content.
            score: 1 = same meaning as the others, 0.7 = acceptable variant, 0.4 = deviates in an important point, 0 = different meaning.
            issues: short notes in {{ctx.ExplanationLanguage}}; empty list if nothing.
            Return: {"score": 0.9, "issues": ["…"]}
            """, ct), ct);
        var (score, issues) = ParseJudge(judge);
        return (new CheckFinding(Consensus, score, issues, string.Join("\n", alternatives)), alternatives);
    }

    /// <summary>Beide Methoden (gleichzeitig) und ein Gesamturteil; maßgeblich ist der schlechtere Befund.</summary>
    public async Task<TranslationCheck> CheckAsync(string source, string target, string sourceLanguage, string targetLanguage, string? topic,
        bool backTranslation = true, bool consensus = true, CancellationToken ct = default)
    {
        var count = Parallel ? Math.Min(3, ((IParallelChatModel)ctx.Model).Parallelism) : 2;
        var back = backTranslation ? BackTranslationAsync(source, target, sourceLanguage, targetLanguage, ct) : null;
        var cons = consensus ? ConsensusAsync(source, target, sourceLanguage, targetLanguage, topic, count, ct) : null;
        var findings = new List<CheckFinding>();
        var alternatives = new List<string>();
        if (back != null) findings.Add(await back);
        if (cons != null)
        {
            var (finding, alts) = await cons;
            findings.Add(finding);
            alternatives = alts;
        }
        var score = findings.Count == 0 ? 1 : findings.Min(f => f.Score);
        return new TranslationCheck(score, findings, alternatives, !Parallel);
    }
}
