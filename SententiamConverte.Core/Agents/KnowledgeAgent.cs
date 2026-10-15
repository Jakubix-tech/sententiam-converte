using System.Globalization;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Agents;

public sealed record KnowledgeRunResult(int Added, int Rejected, int Duplicates, List<KnowledgeEntry> Entries, List<SourceRef> Sources);

/// <summary>
/// Füllt eine Wissensdatenbank aus Online-Quellen: sucht zu einem Thema in jeder gewählten Sprache,
/// liest die Seiten (robots.txt und Nutzungsvorbehalte werden beachtet) und lässt die KI Wissenseinträge
/// mit wörtlichem Beleg herausziehen. Einträge, deren Beleg nicht wirklich auf der Seite steht, werden verworfen.
/// </summary>
public sealed class KnowledgeAgent(AgentContext ctx)
{
    private sealed record Plan(Dictionary<string, List<string>> Queries);
    private sealed record Item(string Kind, string Title, string Content, string Quote, string Url, double Confidence);
    private sealed record Extraction(List<Item> Items);

    public async Task<KnowledgeRunResult> RunAsync(
        string topic, IReadOnlyList<string> languages, IKnowledgeBase target, string? focus = null,
        int pagesPerLanguage = 3, string? author = null, CancellationToken ct = default)
    {
        var search = ctx.RequireSearch();
        var langList = string.Join(", ", languages.Select(l => $"{l} ({Languages.DisplayName(l)})"));

        ctx.Say("Plane Suchanfragen …");
        var plan = JsonExtract.Parse<Plan>(await ctx.Model.AskAsync(
            "You are a research assistant building a knowledge base for professional translators. Output only JSON.",
            $$"""
            Subject: {{topic}}
            {{(string.IsNullOrWhiteSpace(focus) ? "" : "Focus / open questions: " + focus)}}
            For each language code in [{{langList}}], write 3 web search queries IN THAT LANGUAGE that find authoritative
            native sources explaining this subject: standards bodies, authorities, manufacturers' documentation,
            specialist encyclopedias, style guides. Return: {"queries": {"<language code>": ["…", "…", "…"]} }
            """, ct));

        var added = new List<KnowledgeEntry>();
        var sources = new List<SourceRef>();
        int rejected = 0, duplicates = 0;

        foreach (var lang in languages)
        {
            var queries = plan.Queries.FirstOrDefault(kv => Languages.Matches(kv.Key, lang)).Value ?? [topic];
            var results = new List<SearchResult>();
            foreach (var q in queries.Take(3))
            {
                ct.ThrowIfCancellationRequested();
                ctx.Say($"[{lang}] Suche: {q}");
                try { results.AddRange(await search.SearchAsync(q, lang, 6, ct)); }
                catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"[{lang}] Suche fehlgeschlagen: {ex.Message}"); }
            }

            var read = 0;
            foreach (var r in results.DistinctBy(r => r.Url))
            {
                if (read >= pagesPerLanguage) break;
                ct.ThrowIfCancellationRequested();
                ctx.Say($"[{lang}] Lese {r.Url}");
                var text = await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct);
                if (text is not { Length: > 300 })
                {
                    ctx.Say($"[{lang}]   übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                    continue;
                }
                read++;
                sources.Add(new SourceRef(lang, r.Title, r.Url));

                Extraction ex;
                try
                {
                    ex = JsonExtract.Parse<Extraction>(await ctx.Model.AskAsync(
                        "You extract reliable knowledge for translators. Never invent anything: every item must be supported by a verbatim quote from the text. Output only JSON.",
                        $$"""
                        Subject: {{topic}}
                        {{(string.IsNullOrWhiteSpace(focus) ? "" : "Focus: " + focus)}}
                        Language of the text: {{Languages.DisplayName(lang)}} ({{lang}})
                        Source URL: {{r.Url}}

                        === TEXT
                        {{text}}
                        === END

                        Extract up to 8 knowledge items that help a translator working on this subject:
                        kind = "fact" (subject-matter fact), "definition" (what a term means), "usage" (how something is
                        customarily phrased in this language: collocations, fixed expressions), or "style" (conventions such
                        as units, number formats, forms of address).
                        title: short, content: 1-3 sentences in {{Languages.DisplayName(lang)}} in your own words,
                        quote: one sentence copied EXACTLY from the text (max. 30 words) that proves the item,
                        confidence 0..1.
                        Return: {"items": [{"kind": "...", "title": "...", "content": "...", "quote": "...", "url": "{{r.Url}}", "confidence": 0.8}]}
                        """, ct));
                }
                catch (AiException e)
                {
                    ctx.Say($"[{lang}]   Auswertung fehlgeschlagen: {e.Message}");
                    continue;
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
                        Topic = topic,
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
                ctx.Say($"[{lang}]   bisher {added.Count} Einträge übernommen, {rejected} ohne Beleg verworfen.");
            }
        }

        ctx.Say($"Fertig: {added.Count} neue Einträge, {rejected} ohne passenden Beleg verworfen, {duplicates} Dubletten.");
        return new KnowledgeRunResult(added.Count, rejected, duplicates, added, sources);
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
