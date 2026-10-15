using System.Text;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Agents;

/// <summary>Wonach gefragt wird: ein Begriff in seinem Satzzusammenhang.</summary>
public sealed record TermQuery(
    string Term, string SourceLanguage, string TargetLanguage,
    string? Context = null, string? Topic = null, SourceProfile? Profile = null,
    IReadOnlyList<string>? CandidateHints = null, IReadOnlyList<TermHit>? KnownTerms = null);

/// <summary>Ein überprüfter Beleg von einer Fachseite.</summary>
public sealed record TermEvidenceItem(int Number, string Language, string Term, string Quote, string Url, string Title, string? Note);

public sealed class SourceConcept
{
    public string Term { get; set; } = "";
    public string MeaningInContext { get; set; } = "";
    public List<string> KeyFeatures { get; set; } = new();
    public string? System { get; set; }
}

public enum Equivalence { Full, Partial, None }

public sealed class CandidateAssessment
{
    public string Term { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Equivalence Equivalence { get; set; }
    public List<string> SharedFeatures { get; set; } = new();
    public List<string> Differences { get; set; } = new();
    public string? SuitableWhen { get; set; }
    public string? UnsuitableWhen { get; set; }
    public List<int> Evidence { get; set; } = new();
}

public sealed class TermRecommendation
{
    public string Term { get; set; } = "";
    /// <summary>„funktionale Entsprechung“, „wörtlich mit Erläuterung“, „Original mit Erklärung“, „Umschreibung“.</summary>
    public string Strategy { get; set; } = "";
    /// <summary>Vorschlag, wie die Stelle im Zieltext lauten könnte.</summary>
    public string? Rendering { get; set; }
    public string Reasoning { get; set; } = "";
}

public sealed record TermComparisonResult(
    SourceConcept Source, List<CandidateAssessment> Candidates, TermRecommendation Recommendation,
    List<string> Warnings, List<TermEvidenceItem> Evidence, bool UsedSpecialistSites);

/// <summary>
/// Terminologie-Assistent für kontextabhängige Fachbegriffe (z. B. Recht): klärt die Bedeutung im konkreten Satz,
/// sucht auf den Fachseiten beider Sprachen nach Definitionen und Verwendungen, vergleicht die Begriffsmerkmale
/// beider Systeme (z. B. Rechtsordnungen) und empfiehlt mit Begründung eine Entsprechung samt Übersetzungsstrategie.
/// Belegzitate, die nicht wörtlich auf der gelesenen Seite stehen, werden verworfen.
/// </summary>
public sealed class TermComparisonAgent(AgentContext ctx)
{
    private sealed record Candidate(string Term, string? Type, string? Why);
    private sealed record Analysis(string Meaning, List<string>? KeyFeatures, string? SourceSystem, string? TargetSystem,
        List<Candidate>? Candidates, Dictionary<string, List<string>>? Queries, List<string>? Warnings);
    private sealed record Found(string Term, string Quote, string? Note);
    private sealed record FoundList(List<Found> Items);
    private sealed record Comparison(SourceConcept Source, List<CandidateAssessment> Candidates, TermRecommendation Recommendation, List<string>? Warnings);

    public async Task<TermComparisonResult> RunAsync(TermQuery q, int pagesPerLanguage = 3, CancellationToken ct = default)
    {
        var search = ctx.RequireSearch();
        var srcName = Languages.DisplayName(q.SourceLanguage);
        var tgtName = Languages.DisplayName(q.TargetLanguage);
        var srcSystem = q.Profile?.SystemFor(q.SourceLanguage);
        var tgtSystem = q.Profile?.SystemFor(q.TargetLanguage);
        var known = q.KnownTerms is { Count: > 0 }
            ? "Termbase entries already known: " + string.Join("; ", q.KnownTerms.Select(h => $"{h.SourceTerm.Text} = {string.Join(" / ", h.Translations.Select(t => t.Text))}"))
            : "";

        // 1. Bedeutung im Kontext klären und Kandidaten/Suchanfragen ableiten
        ctx.Say($"Kläre „{q.Term}“ im Kontext …");
        var analysis = JsonExtract.Parse<Analysis>(await ctx.Model.AskAsync(
            "You are a senior terminologist and comparative lawyer/linguist. You know that terms are bound to their system " +
            "(e.g. a legal order) and that equivalents across systems are often only partial. Output only JSON.",
            $$"""
            Term: "{{q.Term}}" ({{srcName}}, {{q.SourceLanguage}})
            Target language: {{tgtName}} ({{q.TargetLanguage}})
            {{(string.IsNullOrWhiteSpace(q.Topic) ? "" : "Subject field: " + q.Topic)}}
            {{(srcSystem == null ? "" : "Source system: " + srcSystem)}}
            {{(tgtSystem == null ? "" : "Target system: " + tgtSystem)}}
            {{(string.IsNullOrWhiteSpace(q.Context) ? "" : "Context in which the term occurs:\n" + q.Context)}}
            {{(q.CandidateHints is { Count: > 0 } ? "Translator's candidate equivalents: " + string.Join(", ", q.CandidateHints) : "")}}
            {{known}}

            1. Explain what the term means IN THIS CONTEXT (in {{ctx.ExplanationLanguage}}), and list its essential conceptual features.
            2. Propose up to 4 candidate equivalents in {{tgtName}}: functional equivalents from the target system, formal/literal
               renderings, descriptive renderings, borrowings. type = "functional" | "formal" | "descriptive" | "borrowing".
            3. Write search queries that find authoritative definitions: 2 in {{srcName}} for the source term, and for each
               candidate 1 query in {{tgtName}}. Prefer wording found in statutes, official glossaries and specialist texts.
            4. Warnings: false friends or traps (in {{ctx.ExplanationLanguage}}).
            Return: {"meaning": "...", "keyFeatures": ["..."], "sourceSystem": "...", "targetSystem": "...",
                     "candidates": [{"term": "...", "type": "...", "why": "..."}],
                     "queries": {"{{q.SourceLanguage}}": ["..."], "{{q.TargetLanguage}}": ["..."]}, "warnings": ["..."]}
            """, ct));

        var candidates = (analysis.Candidates ?? []).Select(c => c.Term.Trim()).Where(t => t.Length > 0)
            .Concat(q.CandidateHints ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();

        // 2. Fachseiten durchsuchen und lesen, 3. Belege ziehen und prüfen
        var evidence = new List<TermEvidenceItem>();
        var usedSites = false;
        foreach (var (lang, terms) in new[] { (q.SourceLanguage, (IReadOnlyList<string>)[q.Term]), (q.TargetLanguage, (IReadOnlyList<string>)candidates) })
        {
            var queries = analysis.Queries?.FirstOrDefault(kv => Languages.Matches(kv.Key, lang)).Value
                          ?? terms.Select(t => $"\"{t}\" {q.Topic}").ToList();
            var sites = q.Profile?.SitesFor(lang) ?? [];
            var results = new List<SearchResult>();
            foreach (var query in queries.Take(5))
            {
                ct.ThrowIfCancellationRequested();
                ctx.Say($"[{lang}] Suche{(sites.Count > 0 ? " auf Fachseiten" : "")}: {query}");
                try
                {
                    var (found, restricted) = await SiteSearch.SearchAsync(search, query, lang, sites, 5, ctx.Say, ct);
                    usedSites |= restricted;
                    results.AddRange(found);
                }
                catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"[{lang}] Suche fehlgeschlagen: {ex.Message}"); }
            }

            var read = 0;
            foreach (var r in results.DistinctBy(r => r.Url))
            {
                if (read >= pagesPerLanguage) break;
                ctx.Say($"[{lang}] Lese {r.Url}");
                var text = await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct);
                if (text is not { Length: > 200 })
                {
                    ctx.Say($"[{lang}]   übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                    continue;
                }
                read++;
                FoundList found;
                try
                {
                    found = JsonExtract.Parse<FoundList>(await ctx.Model.AskAsync(
                        "You collect evidence for terminology work. Copy quotes exactly. Output only JSON.",
                        $$"""
                        Terms of interest ({{Languages.DisplayName(lang)}}): {{string.Join(", ", terms)}}
                        Subject: {{q.Topic}}

                        === TEXT ({{r.Url}})
                        {{text}}
                        === END

                        Find up to 4 passages that define one of the terms, state its legal/technical effect, or show how it is used.
                        For each: term, quote (copied EXACTLY, max. 40 words), note (what the passage shows, in {{ctx.ExplanationLanguage}}).
                        Return: {"items": [{"term": "...", "quote": "...", "note": "..."}]}  (empty list if nothing relevant)
                        """, ct));
                }
                catch (AiException ex)
                {
                    ctx.Say($"[{lang}]   Auswertung fehlgeschlagen: {ex.Message}");
                    continue;
                }
                var pageNorm = KnowledgeAgent.Normalize(text);
                foreach (var f in found.Items)
                {
                    if (string.IsNullOrWhiteSpace(f.Quote) || !pageNorm.Contains(KnowledgeAgent.Normalize(f.Quote)))
                    {
                        ctx.Say($"[{lang}]   Beleg verworfen (steht so nicht auf der Seite).");
                        continue;
                    }
                    evidence.Add(new TermEvidenceItem(evidence.Count + 1, lang, f.Term, f.Quote.Trim(), r.Url, r.Title, f.Note));
                }
            }
        }
        ctx.Say($"{evidence.Count} überprüfte Belege gesammelt.");

        // 4. Vergleich und begründete Empfehlung
        var ev = new StringBuilder();
        foreach (var e in evidence) ev.AppendLine($"[{e.Number}] ({e.Language}) {e.Term}: \"{e.Quote}\" – {e.Url}{(e.Note != null ? " – " + e.Note : "")}");
        ctx.Say("Vergleiche die Begriffe …");
        var cmp = JsonExtract.Parse<Comparison>(await ctx.Model.AskAsync(
            "You are a senior terminologist and comparative lawyer/linguist. Compare concepts, not words. Base every claim about " +
            "the systems on the numbered evidence where possible and cite it as [n]; say clearly when something is your general " +
            "knowledge rather than evidenced. Output only JSON.",
            $$"""
            Source term: "{{q.Term}}" ({{srcName}}); target language {{tgtName}}.
            {{(string.IsNullOrWhiteSpace(q.Topic) ? "" : "Subject field: " + q.Topic)}}
            Source system: {{analysis.SourceSystem ?? srcSystem ?? "unknown"}}; target system: {{analysis.TargetSystem ?? tgtSystem ?? "unknown"}}
            {{(string.IsNullOrWhiteSpace(q.Context) ? "" : "Context:\n" + q.Context)}}
            Meaning in context (preliminary): {{analysis.Meaning}}
            Candidates: {{string.Join(", ", candidates)}}

            Evidence from specialist sources:
            {{(ev.Length > 0 ? ev.ToString() : "(none found – rely on general knowledge and say so)")}}

            Compare the source concept with each candidate: equivalence "Full" | "Partial" | "None", shared features, differences
            (legal effect, scope, procedure, institution …), when the candidate is suitable / unsuitable.
            Recommend the best rendering FOR THIS CONTEXT with a strategy ("funktionale Entsprechung", "wörtlich mit Erläuterung",
            "Original mit Erklärung", "Umschreibung"), a proposed wording for the passage, and reasoning that cites [n].
            All explanations in {{ctx.ExplanationLanguage}}.
            Return: {"source": {"term": "...", "meaningInContext": "...", "keyFeatures": ["..."], "system": "..."},
                     "candidates": [{"term": "...", "equivalence": "Partial", "sharedFeatures": ["..."], "differences": ["..."],
                                     "suitableWhen": "...", "unsuitableWhen": "...", "evidence": [1, 2]}],
                     "recommendation": {"term": "...", "strategy": "...", "rendering": "...", "reasoning": "..."},
                     "warnings": ["..."]}
            """, ct));

        var warnings = (analysis.Warnings ?? []).Concat(cmp.Warnings ?? []).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        if (evidence.Count == 0) warnings.Add("Keine überprüften Belege gefunden – die Empfehlung beruht auf dem Allgemeinwissen des Modells und sollte geprüft werden.");
        // Nur Belegnummern behalten, die es wirklich gibt
        foreach (var c in cmp.Candidates) c.Evidence = c.Evidence.Where(n => n >= 1 && n <= evidence.Count).ToList();
        return new TermComparisonResult(cmp.Source, cmp.Candidates, cmp.Recommendation, warnings, evidence, usedSites);
    }

    /// <summary>Lesbare Darstellung für Editor und Agentenbereich.</summary>
    public static string Format(TermComparisonResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"„{r.Source.Term}“ – Bedeutung im Kontext:");
        sb.AppendLine("    " + r.Source.MeaningInContext);
        if (r.Source.System is { Length: > 0 }) sb.AppendLine($"    System: {r.Source.System}");
        if (r.Source.KeyFeatures.Count > 0) sb.AppendLine("    Merkmale: " + string.Join("; ", r.Source.KeyFeatures));
        sb.AppendLine();
        sb.AppendLine($"EMPFEHLUNG: „{r.Recommendation.Term}“ ({r.Recommendation.Strategy})");
        if (r.Recommendation.Rendering is { Length: > 0 }) sb.AppendLine($"    Vorschlag: {r.Recommendation.Rendering}");
        sb.AppendLine("    " + r.Recommendation.Reasoning);
        sb.AppendLine();
        foreach (var c in r.Candidates)
        {
            var eq = c.Equivalence switch { Equivalence.Full => "volle Entsprechung", Equivalence.Partial => "teilweise Entsprechung", _ => "keine Entsprechung" };
            sb.AppendLine($"• {c.Term} – {eq}{(c.Evidence.Count > 0 ? " [" + string.Join(", ", c.Evidence) + "]" : "")}");
            if (c.SharedFeatures.Count > 0) sb.AppendLine("    gemeinsam: " + string.Join("; ", c.SharedFeatures));
            if (c.Differences.Count > 0) sb.AppendLine("    Unterschiede: " + string.Join("; ", c.Differences));
            if (c.SuitableWhen is { Length: > 0 }) sb.AppendLine("    passt, wenn: " + c.SuitableWhen);
            if (c.UnsuitableWhen is { Length: > 0 }) sb.AppendLine("    nicht, wenn: " + c.UnsuitableWhen);
        }
        if (r.Warnings.Count > 0)
        {
            sb.AppendLine();
            foreach (var w in r.Warnings) sb.AppendLine("⚠ " + w);
        }
        if (r.Evidence.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(r.UsedSpecialistSites ? "Belege (Fachseiten):" : "Belege:");
            foreach (var e in r.Evidence) sb.AppendLine($"[{e.Number}] ({e.Language}) „{e.Quote}“\n    {e.Url}");
        }
        return sb.ToString();
    }

    /// <summary>Ergebnis in eine Terminologie übernehmen: empfohlene Benennung bevorzugt, teilweise Entsprechungen zugelassen.</summary>
    public static long SaveToTermbase(TermComparisonResult r, ITermbase tb, string sourceLanguage, string targetLanguage, string? topic)
    {
        var terms = new List<(string, string, TermStatus)> { (sourceLanguage, r.Source.Term, TermStatus.Preferred) };
        terms.Add((targetLanguage, r.Recommendation.Term, TermStatus.Preferred));
        foreach (var c in r.Candidates.Where(c => !string.Equals(c.Term, r.Recommendation.Term, StringComparison.OrdinalIgnoreCase)))
            terms.Add((targetLanguage, c.Term, c.Equivalence == Equivalence.None ? TermStatus.Forbidden : TermStatus.Admitted));
        var rec = r.Candidates.FirstOrDefault(c => string.Equals(c.Term, r.Recommendation.Term, StringComparison.OrdinalIgnoreCase));
        var definition = $"{r.Source.MeaningInContext} | Empfehlung: {r.Recommendation.Strategy}" +
                         (rec is { Differences.Count: > 0 } ? " | Unterschiede: " + string.Join("; ", rec.Differences) : "");
        var source = string.Join(" ", r.Evidence.Select(e => e.Url).Distinct().Take(3));
        return tb.AddConcept(terms, definition, topic, source.Length > 0 ? source : null);
    }

    /// <summary>Ergebnis als Wissenseintrag speichern (Begründung und Beleg für das Team).</summary>
    public static long SaveToKnowledge(TermComparisonResult r, IKnowledgeBase kb, string targetLanguage, string? topic, string? author)
    {
        var first = r.Evidence.FirstOrDefault(e => Languages.Matches(e.Language, targetLanguage)) ?? r.Evidence.FirstOrDefault();
        return kb.Add(new KnowledgeEntry
        {
            Language = targetLanguage,
            Topic = topic ?? "",
            Kind = KnowledgeKind.Usage,
            Title = $"{r.Source.Term} → {r.Recommendation.Term}",
            Content = $"{r.Recommendation.Strategy}: {r.Recommendation.Reasoning}",
            Quote = first?.Quote,
            SourceUrl = first?.Url,
            SourceTitle = first?.Title,
            Author = author is null ? "Terminologie-Assistent" : $"Terminologie-Assistent ({author})",
            Confidence = r.Evidence.Count > 0 ? 0.8 : 0.5,
        });
    }
}
