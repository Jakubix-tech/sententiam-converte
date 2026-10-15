using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

public sealed class TermEvidence
{
    public string Language { get; set; } = "";
    public string Url { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>Ein gefundener Begriff mit seinen Entsprechungen in den gewählten Sprachen.</summary>
public sealed class TermCandidate
{
    public Dictionary<string, string> Terms { get; set; } = new();
    public string? Definition { get; set; }
    public double Confidence { get; set; }
    public List<TermEvidence> Evidence { get; set; } = new();
}

public sealed record ResearchResult(string Topic, List<TermCandidate> Candidates, List<SourceRef> Sources, string Summary);

/// <summary>
/// Recherche zu einem Thema: sucht in jeder gewählten Sprache Fachtexte, liest sie und stellt daraus
/// eine zweisprachige (mehrsprachige) Begriffsliste mit Belegstellen zusammen.
/// Optional mit vorgegebenen Begriffen, für die Entsprechungen gesucht werden.
/// </summary>
public sealed class TermResearchAgent(AgentContext ctx)
{
    private sealed record QueryPlan(Dictionary<string, List<string>> Queries);
    private sealed record ExtractedTerm(string Term, string? Context, string? Url);
    private sealed record Extraction(List<ExtractedTerm> Terms);
    private sealed record Alignment(List<TermCandidate> Concepts, string? Summary);

    public async Task<ResearchResult> RunAsync(
        string topic, string sourceLanguage, IReadOnlyList<string> targetLanguages,
        IReadOnlyList<string>? seedTerms = null, int pagesPerLanguage = 3, CancellationToken ct = default, SourceProfile? profile = null)
    {
        var search = ctx.RequireSearch();
        var languages = new[] { sourceLanguage }.Concat(targetLanguages).Distinct().ToList();
        var seeds = seedTerms?.Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToList() ?? [];

        // 1. Suchanfragen in jeder Sprache formulieren lassen
        ctx.Say("Plane Suchanfragen …");
        var langList = string.Join(", ", languages.Select(l => $"{l} ({Languages.DisplayName(l)})"));
        var plan = JsonExtract.Parse<QueryPlan>(await ctx.Model.AskAsync(
            "You are a research assistant for professional translators. Output only JSON.",
            $$"""
            Subject: {{topic}}
            {{(seeds.Count > 0 ? "Key terms (" + sourceLanguage + "): " + string.Join(", ", seeds) : "")}}
            For each language code in [{{langList}}], write 3 web search queries IN THAT LANGUAGE that would find
            authoritative native texts on this subject (standards, manufacturer documentation, glossaries, official
            bodies, specialist articles). Include the key terms where useful.
            Return: {"queries": {"<language code>": ["query 1", "query 2", "query 3"] } }
            """, ct));

        // 2. Suchen und Seiten lesen
        var sources = new List<SourceRef>();
        var pageTexts = new Dictionary<string, List<(string Url, string Text)>>();
        foreach (var lang in languages)
        {
            var queries = plan.Queries.FirstOrDefault(kv => Languages.Matches(kv.Key, lang)).Value ?? [topic];
            var urls = new List<SearchResult>();
            foreach (var q in queries.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                ctx.Say($"[{lang}] Suche: {q}");
                try { urls.AddRange((await SiteSearch.SearchAsync(search, q, lang, profile?.SitesFor(lang) ?? [], 6, ctx.Say, ct)).Results); }
                catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"[{lang}] Suche fehlgeschlagen: {ex.Message}"); }
            }
            var picked = urls.DistinctBy(u => u.Url).Where(u => !IsUnwanted(u.Url)).Take(pagesPerLanguage * 2).ToList();

            var texts = new List<(string, string)>();
            foreach (var r in picked)
            {
                if (texts.Count >= pagesPerLanguage) break;
                ctx.Say($"[{lang}] Lese {r.Url}");
                var text = await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct);
                if (text is not { Length: > 300 })
                {
                    ctx.Say($"[{lang}]   übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                    continue;
                }
                texts.Add((r.Url, text));
                sources.Add(new SourceRef(lang, r.Title, r.Url));
            }
            pageTexts[lang] = texts;
        }

        // 3. Fachbegriffe je Sprache aus den Texten ziehen
        var extracted = new Dictionary<string, List<ExtractedTerm>>();
        foreach (var lang in languages)
        {
            if (pageTexts[lang].Count == 0) { extracted[lang] = []; continue; }
            ctx.Say($"[{lang}] Ziehe Fachbegriffe aus {pageTexts[lang].Count} Texten …");
            var sb = new StringBuilder();
            foreach (var (url, text) in pageTexts[lang]) sb.AppendLine($"=== SOURCE {url}\n{text}\n");
            try
            {
                var ex = JsonExtract.Parse<Extraction>(await ctx.Model.AskAsync(
                    "You are a terminologist. Extract only terms that literally appear in the given texts. Output only JSON.",
                    $$"""
                    Subject: {{topic}}
                    Language of the texts: {{Languages.DisplayName(lang)}} ({{lang}})
                    {{(seeds.Count > 0 ? "Pay special attention to equivalents of: " + string.Join(", ", seeds) : "")}}

                    {{sb}}

                    List up to 40 subject-specific terms (nouns, noun phrases, fixed verb phrases) exactly as written
                    in the texts, each with a short verbatim context and the source URL.
                    Return: {"terms": [{"term": "...", "context": "...", "url": "..."}]}
                    """, ct));
                extracted[lang] = ex.Terms;
            }
            catch (AiException e)
            {
                ctx.Say($"[{lang}] Auswertung fehlgeschlagen: {e.Message}");
                extracted[lang] = [];
            }
        }

        // 4. Begriffe sprachübergreifend zuordnen
        ctx.Say("Ordne Begriffe den Sprachen zu …");
        var listing = new StringBuilder();
        foreach (var (lang, terms) in extracted)
        {
            listing.AppendLine($"--- {lang}");
            foreach (var t in terms) listing.AppendLine($"* {t.Term} | {t.Context} | {t.Url}");
        }
        var aligned = JsonExtract.Parse<Alignment>(await ctx.Model.AskAsync(
            "You are a senior terminologist building a multilingual termbase. Only pair terms when the contexts show they denote the same concept. Output only JSON.",
            $$"""
            Subject: {{topic}}
            Languages: {{langList}}
            {{(seeds.Count > 0 ? "Required concepts (source language " + sourceLanguage + "): " + string.Join(", ", seeds) : "")}}

            Terms found in native web texts per language (term | context | url):
            {{listing}}

            Build concept entries. Each entry: the term per language code (only terms from the lists above; leave a
            language out if no attested equivalent exists), a one-sentence definition in {{ctx.ExplanationLanguage}},
            a confidence 0..1 that the terms are true equivalents, and evidence (language, url, short quote).
            Required concepts come first, even if only partially found.
            Also give a 2-3 sentence summary in {{ctx.ExplanationLanguage}} of what you found and any doubtful pairs.
            Return: {"concepts": [{"terms": {"de-DE": "...", "pl-PL": "..."}, "definition": "...", "confidence": 0.8,
                     "evidence": [{"language": "...", "url": "...", "quote": "..."}]}], "summary": "..."}
            """, ct));

        // Sprachcodes der KI auf die Projektcodes abbilden
        foreach (var c in aligned.Concepts)
            c.Terms = c.Terms
                .Select(kv => (Lang: languages.FirstOrDefault(l => Languages.Matches(l, kv.Key)) ?? kv.Key, kv.Value))
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => x.Lang).ToDictionary(g => g.Key, g => g.First().Value);

        ctx.Say($"Fertig: {aligned.Concepts.Count} Begriffe aus {sources.Count} Quellen.");
        return new ResearchResult(topic, aligned.Concepts.OrderByDescending(c => c.Confidence).ToList(), sources, aligned.Summary ?? "");
    }

    private static bool IsUnwanted(string url) =>
        url.Contains("youtube.com") || url.Contains("facebook.com") || url.Contains("instagram.com") ||
        url.Contains("tiktok.com") || url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
