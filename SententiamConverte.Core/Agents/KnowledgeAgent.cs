using System.Globalization;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Agents;

/// <summary>Wie gut ein Unterthema in einer Sprache abgedeckt ist (neue Einträge dieses Laufs).</summary>
public sealed record AspectCoverage(string Aspect, string Language, int Entries);

public sealed record KnowledgeRunResult(int Added, int Rejected, int Duplicates, List<KnowledgeEntry> Entries, List<SourceRef> Sources,
    IReadOnlyList<AspectCoverage>? Coverage = null, int Rounds = 1);

/// <summary>
/// Füllt eine Wissensdatenbank aus Online-Quellen – wie ein Mensch, der sich in ein Thema einarbeitet: Erst eine
/// Gliederung (beim Schweißen: was es ist, Verfahren, Maschinen und ihr Aufbau, Werkstoffe …), dann jedes Unterthema in
/// jeder gewählten Sprache suchen und lesen (robots.txt und Nutzungsvorbehalte werden beachtet), danach prüfen, was noch
/// fehlt, und dort mit neuen Anfragen weitersuchen. Die KI zieht Wissenseinträge mit wörtlichem Beleg heraus; Einträge,
/// deren Beleg nicht wirklich auf der Seite steht, werden verworfen. Fachseiten, die Belege liefern, merkt sich das Programm.
/// </summary>
public sealed class KnowledgeAgent(AgentContext ctx)
{
    private sealed record Plan(Dictionary<string, List<string>> Queries);
    private sealed record AspectDto(string? Title, Dictionary<string, List<string>>? Queries);
    private sealed record Outline(List<AspectDto>? Aspects);
    private sealed record RetryDto(string? Aspect, string? Language, List<string>? Queries);
    private sealed record Retry(List<RetryDto>? Queries);
    private sealed record Item(string Kind, string Title, string Content, string Quote, string Url, double Confidence);
    private sealed record Extraction(List<Item> Items);

    /// <summary>Höchstzahl der Suchrunden: Nach der ersten Runde wird nur dort weitergesucht, wo noch Einträge fehlen.</summary>
    public int MaxRounds { get; init; } = 3;

    public async Task<KnowledgeRunResult> RunAsync(
        string topic, IReadOnlyList<string> languages, IKnowledgeBase target, string? focus = null,
        int pagesPerLanguage = 3, string? author = null, CancellationToken ct = default, SourceProfile? profile = null)
    {
        var search = ctx.RequireSearch();
        var langList = string.Join(", ", languages.Select(l => $"{l} ({Languages.DisplayName(l)})"));
        // Fachseiten, die bei früheren Recherchen zu diesem Thema Belege lieferten, gezielt mit durchsuchen
        var topics = SiteCatalog.Topics(topic, profile);
        if (languages.Count > 0)
            profile = SiteCatalog.Augment(profile, topics, languages[0], languages.Count > 1 ? languages[1] : languages[0]);

        // 1. Gliederung: Unterthemen mit Suchanfragen je Sprache; geht das nicht, ein Durchgang zum ganzen Thema wie bisher
        ctx.Say("Gliedere das Thema …");
        var aspects = await OutlineAsync(topic, focus, languages, langList, ct);
        var single = aspects == null;
        if (single)
        {
            ctx.Say("Plane Suchanfragen …");
            var plan = JsonExtract.Parse<Plan>(await ctx.WaitAsync(ctx.Model.AskAsync(
                "You are a research assistant building a knowledge base for professional translators. Output only JSON.",
                $$"""
                Subject: {{topic}}
                {{(string.IsNullOrWhiteSpace(focus) ? "" : "Focus / open questions: " + focus)}}
                For each language code in [{{langList}}], write 3 web search queries IN THAT LANGUAGE that find authoritative
                native sources explaining this subject: standards bodies, authorities, manufacturers' documentation,
                specialist encyclopedias, style guides. Return: {"queries": {"<language code>": ["…", "…", "…"]} }
                """, ct), ct));
            aspects = [(topic, plan.Queries)];
        }
        else ctx.Say("Gliederung: " + string.Join(" · ", aspects!.Select(a => a.Title)));

        var added = new List<KnowledgeEntry>();
        var sources = new List<SourceRef>();
        var readUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverage = aspects!.SelectMany(a => languages.Select(l => (a.Title, l))).ToDictionary(k => k, _ => 0);
        int rejected = 0, duplicates = 0, rounds = 0;
        var pages = single ? pagesPerLanguage : Math.Max(1, pagesPerLanguage / 2);
        var queriesFor = aspects!.SelectMany(a => languages.Select(l => (Key: (a.Title, l), Queries: a.Queries.FirstOrDefault(kv => Languages.Matches(kv.Key, l)).Value ?? [])))
            .ToDictionary(x => x.Key, x => x.Queries.Count > 0 ? x.Queries : [single ? topic : $"{topic} {x.Key.Title}"]);

        for (var round = 1; round <= (single ? 1 : MaxRounds); round++)
        {
            var gaps = coverage.Where(c => c.Value == 0).Select(c => c.Key).ToList();
            if (gaps.Count == 0) break;
            rounds = round;
            if (round > 1)
            {
                ctx.Say($"Runde {round}: noch keine Einträge zu " + string.Join(", ", gaps.Select(g => $"{g.Item1} ({g.Item2})")) + " – suche anders weiter …");
                foreach (var (key, list) in await RetryQueriesAsync(topic, gaps, ct)) queriesFor[key] = list;
            }
            foreach (var (aspect, lang) in gaps)
            {
                ct.ThrowIfCancellationRequested();
                var results = new List<SearchResult>();
                foreach (var q in queriesFor[(aspect, lang)].Take(single ? 3 : 2))
                {
                    ct.ThrowIfCancellationRequested();
                    ctx.Say($"[{lang}] Suche{(single ? "" : $" ({aspect})")}: {q}");
                    try { results.AddRange((await SiteSearch.SearchAsync(search, q, lang, profile?.SitesFor(lang) ?? [], 6, ctx.Say, ct)).Results); }
                    catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"[{lang}] Suche fehlgeschlagen: {ex.Message}"); }
                }

                var read = 0;
                foreach (var r in results.DistinctBy(r => r.Url))
                {
                    if (read >= pages) break;
                    if (!readUrls.Add(r.Url)) continue;                                     // schon gelesen
                    ct.ThrowIfCancellationRequested();
                    ctx.Say($"[{lang}] Lese {r.Url}");
                    var text = await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct);
                    if (text is not { Length: > 300 })
                    {
                        ctx.Say($"[{lang}]   übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                        continue;
                    }
                    read++;
                    sources.Add(new SourceRef(lang, r.Title, r.Url) { Retrieved = DateTime.UtcNow });
                    var before = added.Count;
                    (var rej, var dup) = await ExtractAsync(topic, single ? null : aspect, focus, lang, r, text, target, author, added, ct);
                    rejected += rej;
                    duplicates += dup;
                    coverage[(aspect, lang)] += added.Count - before;
                    ctx.Say($"[{lang}]   bisher {added.Count} Einträge übernommen, {rejected} ohne Beleg verworfen.");
                }
            }
        }

        // Seiten, die Einträge geliefert haben, als Fachseiten zum Thema merken
        try { SiteCatalog.RecordEvidence(sources.Where(s => added.Any(e => e.SourceUrl == s.Url)), topics); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ctx.Say("Quellen-Datenbank nicht gespeichert: " + ex.Message); }

        var result = coverage.Select(c => new AspectCoverage(c.Key.Item1, c.Key.Item2, c.Value)).ToList();
        if (!single)
        {
            var missing = result.Where(c => c.Entries == 0).Select(c => $"{c.Aspect} ({c.Language})").ToList();
            ctx.Say("Abdeckung: " + string.Join(" · ", result.GroupBy(c => c.Aspect).Select(g => $"{g.Key}: " + string.Join(", ", g.Select(c => $"{c.Language} {c.Entries}")))));
            if (missing.Count > 0) ctx.Say("Nicht gefunden: " + string.Join(", ", missing) + " – eigene Fachtexte oder weitere Suchdienste helfen.");
        }
        ctx.Say($"Fertig: {added.Count} neue Einträge, {rejected} ohne passenden Beleg verworfen, {duplicates} Dubletten.");
        return new KnowledgeRunResult(added.Count, rejected, duplicates, added, sources, single ? null : result, Math.Max(1, rounds));
    }

    /// <summary>Unterthemen (4–8) mit 2 Suchanfragen je Sprache; null, wenn die Antwort nicht taugt.</summary>
    private async Task<List<(string Title, Dictionary<string, List<string>> Queries)>?> OutlineAsync(string topic, string? focus,
        IReadOnlyList<string> languages, string langList, CancellationToken ct)
    {
        try
        {
            var outline = JsonExtract.Parse<Outline>(await ctx.WaitAsync(ctx.Model.AskAsync(
                "You are a research assistant who prepares a translator for a subject, like a human expert would. Output only JSON.",
                $$"""
                Subject: {{topic}}
                {{(string.IsNullOrWhiteSpace(focus) ? "" : "Focus / open questions: " + focus)}}
                Break the subject into the 4–8 aspects a translator must understand (e.g. for welding: what it is, processes,
                machines and how they are built, materials and consumables, standards and safety). Title each aspect briefly in
                {{ctx.ExplanationLanguage}}. For each aspect and each language code in [{{langList}}], write 2 web search queries
                IN THAT LANGUAGE that find authoritative native sources (standards bodies, authorities, manufacturers'
                documentation, textbooks, specialist encyclopedias, glossaries).
                Return: {"aspects": [{"title": "...", "queries": {"<language code>": ["…", "…"]} }]}
                """, ct), ct));
            var list = (outline.Aspects ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Title))
                .Select(a => (a.Title!.Trim(), a.Queries ?? new Dictionary<string, List<string>>())).Take(8).ToList();
            return list.Count >= 2 ? list : null;
        }
        catch (AiException) { return null; }
    }

    /// <summary>Neue Suchanfragen für Unterthemen, zu denen noch nichts gefunden wurde.</summary>
    private async Task<Dictionary<(string, string), List<string>>> RetryQueriesAsync(string topic, IReadOnlyList<(string Aspect, string Language)> gaps,
        CancellationToken ct)
    {
        var result = new Dictionary<(string, string), List<string>>();
        try
        {
            var retry = JsonExtract.Parse<Retry>(await ctx.WaitAsync(ctx.Model.AskAsync(
                "You are a research assistant. Output only JSON.",
                $$"""
                Subject: {{topic}}
                Nothing reliable was found yet for these aspects and languages: {{string.Join("; ", gaps.Select(g => $"{g.Aspect} [{g.Language}]"))}}.
                Search on like a human expert: use other words, the specialist terms of that language, glossaries, standards,
                manufacturers, textbooks. Write 2 new queries IN THE GIVEN LANGUAGE for each.
                Return: {"queries": [{"aspect": "...", "language": "...", "queries": ["…", "…"]}]}
                """, ct), ct));
            foreach (var q in retry.Queries ?? [])
                if (gaps.FirstOrDefault(g => g.Aspect.Equals(q.Aspect?.Trim(), StringComparison.OrdinalIgnoreCase) && Languages.Matches(g.Language, q.Language ?? "")) is { Aspect: not null } gap
                    && q.Queries is { Count: > 0 } list)
                    result[gap] = list;
        }
        catch (AiException ex) { ctx.Say("Neue Suchanfragen nicht lesbar: " + ex.Message); }
        foreach (var g in gaps.Where(g => !result.ContainsKey(g)))
            result[g] = [$"{topic} {g.Aspect}"];
        return result;
    }

    /// <summary>Einträge aus einer Seite: nur mit wörtlichem Beleg, ohne Dubletten; Thema „Thema – Unterthema“.</summary>
    private async Task<(int Rejected, int Duplicates)> ExtractAsync(string topic, string? aspect, string? focus, string lang, SearchResult r,
        string text, IKnowledgeBase target, string? author, List<KnowledgeEntry> added, CancellationToken ct)
    {
        int rejected = 0, duplicates = 0;
        Extraction ex;
        try
        {
            ex = JsonExtract.Parse<Extraction>(await ctx.WaitAsync(ctx.Model.AskAsync(
                "You extract reliable knowledge for translators. Never invent anything: every item must be supported by a verbatim quote from the text. Output only JSON.",
                $$"""
                Subject: {{topic}}
                {{(aspect == null ? "" : "Aspect: " + aspect)}}
                {{(string.IsNullOrWhiteSpace(focus) ? "" : "Focus: " + focus)}}
                Language of the text: {{Languages.DisplayName(lang)}} ({{lang}})
                Source URL: {{r.Url}}

                === TEXT
                {{text}}
                === END

                Extract up to 8 knowledge items that help a translator working on this subject{{(aspect == null ? "" : " – especially on the aspect")}}:
                kind = "fact" (subject-matter fact), "definition" (what a term means – name the term as used in this language),
                "usage" (how something is customarily phrased in this language: collocations, fixed expressions), or "style"
                (conventions such as units, number formats, forms of address).
                title: short, content: 1-3 sentences in {{Languages.DisplayName(lang)}} in your own words,
                quote: one sentence copied EXACTLY from the text (max. 30 words) that proves the item,
                confidence 0..1.
                Return: {"items": [{"kind": "...", "title": "...", "content": "...", "quote": "...", "url": "{{r.Url}}", "confidence": 0.8}]}
                """, ct), ct));
        }
        catch (AiException e)
        {
            ctx.Say($"[{lang}]   Auswertung fehlgeschlagen: {e.Message}");
            return (0, 0);
        }

        foreach (var item in ex.Items)
        {
            // Beleg prüfen: das Zitat muss wirklich im gelesenen Text stehen
            if (string.IsNullOrWhiteSpace(item.Quote) || !Normalize(text).Contains(Normalize(item.Quote)))
            {
                rejected++;
                continue;
            }
            if (IsDuplicate(target, lang, item.Title, added))
            {
                duplicates++;
                continue;
            }
            var entry = new KnowledgeEntry
            {
                Language = lang,
                Topic = aspect == null ? topic : $"{topic} – {aspect}",
                Kind = item.Kind?.ToLowerInvariant() switch
                {
                    "definition" => KnowledgeKind.Definition,
                    "usage" => KnowledgeKind.Usage,
                    "style" => KnowledgeKind.Style,
                    _ => KnowledgeKind.Fact,
                },
                Title = item.Title.Trim(),
                Content = item.Content.Trim(),
                Quote = Shorten(item.Quote.Trim(), 300),
                SourceUrl = r.Url,
                SourceTitle = r.Title,
                Author = author is null ? "Wissens-Agent" : $"Wissens-Agent ({author})",
                Confidence = Math.Clamp(item.Confidence, 0, 1),
            };
            entry = entry with { Id = target.Add(entry) };
            added.Add(entry);
        }
        return (rejected, duplicates);
    }

    private static bool IsDuplicate(IKnowledgeBase kb, string lang, string title, List<KnowledgeEntry> justAdded)
    {
        if (justAdded.Any(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            return kb.Search(lang, title, 5).Any(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase));
        }
        catch (Remote.RemoteException) { return false; }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    /// <summary>Für den Belegvergleich: Kleinschreibung, ohne Akzente und Satzzeichen, Leerraum vereinheitlicht.</summary>
    internal static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            // Satzzeichen zählen nicht – ein Zitat endet oft anders als der Satz im Text
            if (char.IsPunctuation(c)) continue;
            sb.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
