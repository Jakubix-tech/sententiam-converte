using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Agents;

/// <summary>Ein Begriff des Begriffsfelds: Benennung, unterscheidende Merkmale, Entsprechung in der Zielsprache, Belege.</summary>
public sealed class ConceptFieldMember
{
    public string SourceTerm { get; set; } = "";
    /// <summary>Merkmale, die ihn von den anderen Begriffen des Felds unterscheiden (in der Sprache der Begründungen).</summary>
    public List<string> Features { get; set; } = new();
    public string? TargetTerm { get; set; }
    public List<string> TargetAlternatives { get; set; } = new();
    /// <summary>Der Ausdruck aus dem Dokument.</summary>
    public bool IsQueried { get; set; }
    /// <summary>In wie vielen Quellen der Zielsprache die Entsprechung wörtlich vorkommt (Wikipedia-Verknüpfung, Seiten, eigene Texte).</summary>
    public int TargetEvidence { get; set; }
    /// <summary>Die Wikipedia verknüpft den Artikel zum Begriff mit dem Artikel zur Entsprechung.</summary>
    public bool LinkedByWikipedia { get; set; }
}

/// <summary>
/// Begriffsfeld zu einem Ausdruck: Oberbegriff und Geschwisterbegriffe in beiden Sprachen (Dachformen: Satteldach,
/// Walmdach, Zeltdach …), woran man sie unterscheidet, was im konkreten Satz gemeint ist – und eine Rückfrage, wenn der
/// Kontext dafür nicht reicht. So berät die KI wie ein Fachübersetzer, der erst das Feld klärt und dann wählt.
/// </summary>
public sealed class ConceptField
{
    public string Umbrella { get; set; } = "";
    public string? UmbrellaTarget { get; set; }
    public List<ConceptFieldMember> Members { get; set; } = new();
    /// <summary>Prüffragen bzw. Kriterien, mit denen man den passenden Begriff findet.</summary>
    public List<string> HowToTell { get; set; } = new();
    /// <summary>Was im Satz gemeint ist und welche Benennung deshalb passt.</summary>
    public string? ForThisContext { get; set; }

    /// <summary>Der Begriff des Felds, den der Satz meint (kann vom Ausdruck abweichen, z. B. umgangssprachlich „Spitzdach“).</summary>
    public string? Meant { get; set; }

    public ConceptFieldMember? MeantMember =>
        Members.FirstOrDefault(m => Meant != null && m.SourceTerm.Equals(Meant, StringComparison.OrdinalIgnoreCase)) ?? Members.FirstOrDefault(m => m.IsQueried);
    /// <summary>Rückfrage an den Auftraggeber, wenn der Kontext nicht eindeutig ist.</summary>
    public string? AskTheClient { get; set; }
    public List<SourceRef> Sources { get; set; } = new();
}

/// <summary>Erarbeitet das Begriffsfeld: KI-Gliederung, dann Belege aus Wikipedia (Sprachverknüpfungen), dem Netz und den eigenen Fachtexten, dann Beratung.</summary>
public sealed class ConceptFieldAgent(AgentContext ctx)
{
    private sealed record MemberDto(string? Source, List<string>? Features, string? Target, List<string>? Alternatives);
    private sealed record FieldDto(string? Umbrella, string? UmbrellaTarget, List<MemberDto>? Members, List<string>? HowToTell);
    private sealed record AdviceDto(string? ForThisContext, string? Meant, string? AskTheClient);

    /// <summary>Höchstens so viele Seiten je Sprache werden gelesen (die Begriffe werden darauf wörtlich gesucht).</summary>
    public int PagesPerLanguage { get; init; } = 2;

    public async Task<ConceptField?> RunAsync(string expression, string? context, string sourceLanguage, string targetLanguage, string? topic,
        CancellationToken ct = default)
    {
        var srcName = Languages.DisplayName(sourceLanguage);
        var tgtName = Languages.DisplayName(targetLanguage);
        ctx.Say($"Kläre das Begriffsfeld zu „{expression}“ …");
        FieldDto dto;
        try
        {
            dto = JsonExtract.Parse<FieldDto>(await ctx.Model.AskAsync(
                "You are a senior terminologist. You work with concept systems: a concept, its superordinate concept and its " +
                "coordinate (sibling) concepts, each defined by distinguishing features. Output only JSON.",
                $$"""
                Expression: "{{expression}}" ({{srcName}})
                {{(string.IsNullOrWhiteSpace(context) ? "" : "Sentence: " + context)}}
                {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject: " + topic)}}
                Target language: {{tgtName}}

                1. Name the superordinate concept (umbrella) in {{srcName}} and {{tgtName}}.
                2. List the expression itself and its most important sibling concepts (at most 8) in {{srcName}}, each with the
                   features that distinguish it from the others (short, in {{ctx.ExplanationLanguage}}), the usual {{tgtName}}
                   term and other {{tgtName}} terms in use.
                3. Write 2–5 questions or criteria a translator can use to tell which concept is meant (in {{ctx.ExplanationLanguage}}).
                Return: {"umbrella": "...", "umbrellaTarget": "...",
                         "members": [{"source": "...", "features": ["..."], "target": "...", "alternatives": ["..."]}],
                         "howToTell": ["..."]}
                """, ct));
        }
        catch (AiException ex)
        {
            ctx.Say("Begriffsfeld nicht lesbar: " + ex.Message);
            return null;
        }
        var field = new ConceptField
        {
            Umbrella = (dto.Umbrella ?? "").Trim(),
            UmbrellaTarget = dto.UmbrellaTarget?.Trim(),
            HowToTell = (dto.HowToTell ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList(),
            Members = (dto.Members ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Source)).Take(9).Select(m => new ConceptFieldMember
            {
                SourceTerm = m.Source!.Trim(),
                Features = (m.Features ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList(),
                TargetTerm = m.Target?.Trim(),
                TargetAlternatives = (m.Alternatives ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
            }).ToList(),
        };
        if (field.Members.Count == 0) return null;
        var queried = field.Members.FirstOrDefault(m => DocumentResearch.Contains(expression, m.SourceTerm) || DocumentResearch.Contains(m.SourceTerm, expression));
        if (queried == null) field.Members.Insert(0, queried = new ConceptFieldMember { SourceTerm = expression });
        queried.IsQueried = true;

        // Belege: was die Wikipedia verknüpft, was auf Seiten beider Sprachen und in den eigenen Fachtexten wörtlich steht
        var texts = new List<(string Language, string Text, SourceRef Source)>();
        await WikipediaAsync(field, sourceLanguage, targetLanguage, texts, ct);
        await WebAsync(field, sourceLanguage, targetLanguage, texts, ct);
        OwnTexts(field, targetLanguage, texts);
        foreach (var m in field.Members)
        {
            var targets = new[] { m.TargetTerm }.Concat(m.TargetAlternatives).OfType<string>().Where(t => t.Length > 0).ToList();
            // die Benennung, die in den Quellen am häufigsten wörtlich vorkommt, wird zur Entsprechung
            var counts = targets.Select(t => (Term: t, Count: texts.Where(x => Languages.Matches(x.Language, targetLanguage) && ReferenceLibrary.Mentions(x.Text, t))
                .Select(x => SiteCatalog.DomainOf(x.Source.Url) ?? x.Source.Url).Distinct().Count())).ToList();
            if (counts.Count > 0 && counts.MaxBy(c => c.Count) is { Count: > 0 } best && best.Term != m.TargetTerm && !m.LinkedByWikipedia)
            {
                if (m.TargetTerm is { Length: > 0 } old) m.TargetAlternatives.Insert(0, old);
                m.TargetAlternatives.Remove(best.Term);
                m.TargetTerm = best.Term;
            }
            m.TargetEvidence = counts.FirstOrDefault(c => c.Term == m.TargetTerm).Count;
        }
        field.Sources = texts.Select(t => t.Source).DistinctBy(s => s.Url).Select((s, i) => s with { Number = i + 1 }).ToList();

        // Beratung: welcher Begriff ist im Satz gemeint – und eine Rückfrage, wenn der Kontext nicht reicht
        if (!string.IsNullOrWhiteSpace(context))
            try
            {
                var list = new StringBuilder();
                foreach (var m in field.Members)
                    list.AppendLine($"- {m.SourceTerm}{(m.IsQueried ? " (the expression in the text)" : "")}: {string.Join("; ", m.Features)} → {m.TargetTerm} (found in {m.TargetEvidence} {tgtName} sources)");
                var advice = JsonExtract.Parse<AdviceDto>(await ctx.Model.AskAsync(
                    "You are a senior technical translator advising a colleague. Output only JSON.",
                    $$"""
                    Sentence ({{srcName}}): {{context}}
                    Expression: "{{expression}}"
                    Concept field "{{field.Umbrella}}":
                    {{list}}
                    Criteria: {{string.Join(" | ", field.HowToTell)}}

                    Which concept of the field does the sentence really mean (the expression may be colloquial or imprecise)?
                    Explain in 1–3 sentences in {{ctx.ExplanationLanguage}} which {{tgtName}} term fits and why, based on the features.
                    If the sentence does not allow a safe decision, write one short question for the client in "askTheClient", else "".
                    Return: {"meant": "<source term of the field>", "forThisContext": "...", "askTheClient": ""}
                    """, ct));
                field.ForThisContext = advice.ForThisContext?.Trim();
                field.Meant = field.Members.FirstOrDefault(m => m.SourceTerm.Equals(advice.Meant?.Trim(), StringComparison.OrdinalIgnoreCase))?.SourceTerm;
                field.AskTheClient = string.IsNullOrWhiteSpace(advice.AskTheClient) ? null : advice.AskTheClient.Trim();
            }
            catch (AiException ex) { ctx.Say("Beratung nicht lesbar: " + ex.Message); }
        ctx.Say($"Begriffsfeld „{field.Umbrella}“: {field.Members.Count} Begriffe, {field.Members.Count(m => m.TargetEvidence > 0)} Entsprechungen belegt.");
        return field;
    }

    /// <summary>Sprachverknüpfung je Begriff und Artikel zum Oberbegriff in beiden Sprachen (dort stehen die Geschwister meist beisammen).</summary>
    private async Task WikipediaAsync(ConceptField field, string src, string tgt, List<(string, string, SourceRef)> texts, CancellationToken ct)
    {
        if (!TermComparisonAgent.WikipediaLinks) return;
        if (ctx.Model is Privacy.PrivacyChatModel privacy && privacy.MaskText(field.Umbrella) != field.Umbrella) return;
        try
        {
            foreach (var (lang, title) in new[] { (src, field.Umbrella), (tgt, field.UmbrellaTarget ?? "") }.Where(x => x.Item2.Length > 0))
                if (await WikiApi.ArticleAsync("wikipedia", lang, title, 20000, ct) is { Text.Length: > 0 } article)
                    texts.Add((lang, article.Text, new SourceRef(lang, "Wikipedia: " + article.Title, article.Url) { Retrieved = DateTime.UtcNow }));
            foreach (var m in field.Members)
            {
                ct.ThrowIfCancellationRequested();
                var article = await WikiApi.ArticleAsync("wikipedia", src, m.SourceTerm, 0, ct);
                if (article?.Versions.GetValueOrDefault(WikiApi.Code(tgt)) is not { Length: > 0 } title) continue;
                var target = System.Text.RegularExpressions.Regex.Replace(title, @"\s*\([^)]*\)$", "");
                // nur, wenn die Verknüpfung wirklich zu diesem Begriff führt (nicht zu einem allgemeinen Sammelartikel)
                if (!ReferenceLibrary.Mentions(article.Title, m.SourceTerm) && !ReferenceLibrary.Mentions(m.SourceTerm, article.Title)) continue;
                m.LinkedByWikipedia = true;
                if (!string.Equals(m.TargetTerm, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (m.TargetTerm is { Length: > 0 } old) m.TargetAlternatives.Insert(0, old);
                    m.TargetAlternatives.RemoveAll(a => a.Equals(target, StringComparison.OrdinalIgnoreCase));
                    m.TargetTerm = target;
                }
                texts.Add((tgt, title, new SourceRef(tgt, $"Wikipedia: {article.Title} → {title}", article.Url) { Term = target, Retrieved = DateTime.UtcNow }));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            ctx.Say("Wikipedia nicht erreichbar: " + ex.Message);
        }
    }

    /// <summary>Je Sprache eine Suche nach dem Oberbegriff mit den Begriffen; die Seiten werden gelesen und die Begriffe darauf wörtlich gesucht.</summary>
    private async Task WebAsync(ConceptField field, string src, string tgt, List<(string, string, SourceRef)> texts, CancellationToken ct)
    {
        if (ctx.Search is not { } search) return;
        foreach (var (lang, umbrella, terms) in new[]
                 {
                     (src, field.Umbrella, field.Members.Select(m => m.SourceTerm).Take(4)),
                     (tgt, field.UmbrellaTarget ?? field.Umbrella, field.Members.Select(m => m.TargetTerm).OfType<string>().Take(4)),
                 })
        {
            var query = $"{umbrella} {string.Join(" ", terms)}".Trim();
            if (query.Length == 0) continue;
            ctx.Say($"[{lang}] Suche: {query}");
            List<SearchResult> results;
            try { results = await search.SearchAsync(query, lang, 5, ct); }
            catch (Exception ex) when (ex is HttpRequestException or AiException) { ctx.Say($"[{lang}] Suche fehlgeschlagen: {ex.Message}"); continue; }
            var read = 0;
            foreach (var r in results.DistinctBy(r => r.Url))
            {
                if (read >= PagesPerLanguage) break;
                ctx.Say($"[{lang}] Lese {r.Url}");
                if (await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct) is not { Length: > 200 } text) continue;
                read++;
                texts.Add((lang, text, new SourceRef(lang, r.Title, r.Url) { Retrieved = DateTime.UtcNow }));
            }
        }
    }

    private void OwnTexts(ConceptField field, string tgt, List<(string, string, SourceRef)> texts)
    {
        if (ctx.Library is not { } library) return;
        try
        {
            foreach (var t in field.Members.SelectMany(m => new[] { m.TargetTerm }.Concat(m.TargetAlternatives)).OfType<string>().Distinct())
                foreach (var hit in library.FindPhrase(t, tgt, 1))
                    texts.Add((tgt, hit.Quote, new SourceRef(tgt, hit.Label, hit.Uri) { Version = hit.Version, Quote = hit.Quote }));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { ctx.Say("Eigene Fachtexte nicht lesbar: " + ex.Message); }
    }
}
