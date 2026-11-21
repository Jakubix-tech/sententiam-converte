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
    IReadOnlyList<string>? CandidateHints = null, IReadOnlyList<TermHit>? KnownTerms = null, PriorResearch? Prior = null,
    string? Focus = null);

/// <summary>
/// Was eine frühere Recherche zum selben Ausdruck schon erbracht hat – die neue setzt dort an: ihre Kandidaten sind der
/// Startpunkt, ihre Anfragen werden nicht wiederholt, ihre Seiten nicht noch einmal gelesen, ihre geprüften Belege zählen weiter.
/// </summary>
public sealed record PriorResearch(DateTime Created, string? Recommended, IReadOnlyList<string> Candidates, IReadOnlyList<string> Queries,
    IReadOnlyList<TermEvidenceItem> Evidence)
{
    public static PriorResearch From(TermDossier d) => new(d.Created, d.Recommended,
        new[] { d.Recommended }.Concat(d.Candidates.Select(c => c.Term)).OfType<string>().Select(t => t.Trim()).Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        d.Queries,
        d.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Quote))
            .Select((s, i) => new TermEvidenceItem(i + 1, s.Language, s.Term ?? d.Recommended ?? "", s.Quote!, s.Url, s.Title, "aus der früheren Recherche",
                s.Version, !s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase), s.Kind ?? SourceKinds.Classify(s.Url)))
            .ToList());
}

/// <summary>Ein überprüfter Beleg von einer Fachseite.</summary>
public sealed record TermEvidenceItem(int Number, string Language, string Term, string Quote, string Url, string Title, string? Note,
    string? Version = null, bool Local = false, string? Kind = null);

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
    [JsonConverter(typeof(Ai.LenientIntListConverter))]
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
    List<string> Warnings, List<TermEvidenceItem> Evidence, bool UsedSpecialistSites,
    int Rounds = 1, int PagesRead = 0, bool Conclusive = false, IReadOnlyList<string>? Queries = null);

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
    private sealed record FoundList(List<Found> Items, string? Kind = null);
    private sealed record Comparison(SourceConcept Source, List<CandidateAssessment> Candidates, TermRecommendation Recommendation, List<string>? Warnings);
    private sealed record ParallelFind(string? Term, string? Quote);
    private sealed record NextStrategy(Dictionary<string, List<string>>? Queries, List<string>? Candidates);

    /// <summary>
    /// Höchstzahl der Suchrunden: Steht nach einer Runde kein Kandidat in zwei unabhängigen Quellen der Zielsprache,
    /// sucht die Recherche anders weiter (beschreibend, Glossare, Herstellerunterlagen, Normen, Behörden, Synonyme).
    /// </summary>
    public int MaxRounds { get; init; } = 6;

    /// <summary>Ab hier keine neue Suchrunde mehr – mit dem Gefundenen abschließen (null = kein Zeitlimit).</summary>
    public DateTime? Deadline { get; init; }

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
        // Sprachverknüpfung der Wikipedia: von Menschen gepflegt und oft genau die übliche Benennung im Zielland
        // (Gleitringdichtung → Mechanical seal) – ein belegter Kandidat, den auch ein kleines Modell nicht erraten muss
        var linked = await LinkedArticleAsync(q, ct);
        var linkNote = linked == null ? "" :
            $"Wikipedia links the {srcName} article \"{linked.SourceTitle}\" to the {tgtName} article \"{linked.TargetTitle}\" (interlanguage link) – check that it is the same concept.";
        // Frühere Recherche: dort ansetzen, nicht von vorn anfangen
        var prior = q.Prior;
        var priorNote = prior == null ? "" :
            $"An earlier research ({prior.Created:yyyy-MM-dd}) recommended \"{prior.Recommended ?? "?"}\" without sufficient evidence; candidates then: " +
            $"{string.Join(", ", prior.Candidates)}. Already searched (do NOT repeat these, find different ways): {string.Join(" | ", prior.Queries.Take(30))}";
        if (prior != null)
            ctx.Say($"Baue auf der Recherche vom {prior.Created.ToLocalTime():dd.MM.yyyy} auf: {prior.Candidates.Count} Kandidaten, " +
                    $"{prior.Queries.Count} Anfragen schon gestellt, {prior.Evidence.Count} geprüfte Belege.");

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
            {{linkNote}}
            {{priorNote}}
            {{(string.IsNullOrWhiteSpace(q.Focus) ? "" : "Still missing after the earlier research – search specifically for this: " + q.Focus)}}

            1. Explain what the term means IN THIS CONTEXT (in {{ctx.ExplanationLanguage}}), and list its essential conceptual features.
            2. Propose up to 4 candidate equivalents in {{tgtName}}: functional equivalents from the target system, formal/literal
               renderings, descriptive renderings, borrowings. type = "functional" | "formal" | "descriptive" | "borrowing".
            3. Write search queries that find authoritative definitions: 2 in {{srcName}} for the source term, and for each
               candidate 1 query in {{tgtName}}. Prefer wording found in statutes, official glossaries and specialist texts, and aim
               the queries at the strongest source types of this field (termbanks or glossaries, legislation or standards,
               authorities, professional bodies, manufacturer documentation) – e.g. add "glossary", "standard" or the authority's name.
            4. Warnings: false friends or traps (in {{ctx.ExplanationLanguage}}).
            Return: {"meaning": "...", "keyFeatures": ["..."], "sourceSystem": "...", "targetSystem": "...",
                     "candidates": [{"term": "...", "type": "...", "why": "..."}],
                     "queries": {"{{q.SourceLanguage}}": ["..."], "{{q.TargetLanguage}}": ["..."]}, "warnings": ["..."]}
            """, ct));

        // die Kandidaten der früheren Recherche zuerst – sie sind der Startpunkt
        var candidates = (prior?.Candidates.Take(4) ?? []).Concat((analysis.Candidates ?? []).Select(c => c.Term.Trim()).Where(t => t.Length > 0))
            .Concat(q.CandidateHints ?? []).Concat(linked == null ? Array.Empty<string>() : [linked.Candidate])
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(prior == null ? 6 : 8).ToList();

        // 2. Fachseiten durchsuchen und lesen, 3. Belege ziehen und prüfen – die geprüften Belege der früheren Recherche zählen weiter
        var evidence = new List<TermEvidenceItem>();
        foreach (var e in prior?.Evidence ?? [])
            evidence.Add(e with { Number = evidence.Count + 1 });
        if (linked?.Lead is { } lead)
            evidence.Add(new TermEvidenceItem(1, q.TargetLanguage, linked.Candidate, lead, linked.Url, linked.TargetTitle,
                $"Wikipedia-Sprachverknüpfung von „{linked.SourceTitle}“", Kind: SourceKinds.Encyclopedia));
        var usedSites = false;
        // Eigene Fachtexte zuerst: lokal, mit Datei, Seite und Stand – und wie oft jede Benennung darin vorkommt
        var ownCounts = "";
        if (ctx.Library is { } library)
            try
            {
                foreach (var (lang, terms) in new[] { (q.SourceLanguage, (IReadOnlyList<string>)[q.Term]), (q.TargetLanguage, (IReadOnlyList<string>)candidates) })
                    foreach (var term in terms)
                        foreach (var hit in library.FindPhrase(term, lang, 2))
                            evidence.Add(new TermEvidenceItem(evidence.Count + 1, lang, term, hit.Quote, hit.Uri, hit.Label, "Eigene Fachtexte", hit.Version, true, SourceKinds.Own));
                var counts = candidates.Select(c => (Term: c, Count: library.CountPhrase(c, q.TargetLanguage))).ToList();
                if (counts.Any(c => c.Count > 0))
                    ownCounts = $"How often each candidate occurs in the translator's own reference texts ({tgtName}): " +
                                string.Join(", ", counts.Select(c => $"{c.Term}: {c.Count}"));
                ctx.Say($"Eigene Fachtexte: {evidence.Count(e => e.Local)} Belegstellen" + (ownCounts.Length > 0 ? "; " + string.Join(", ", counts.Select(c => $"{c.Term} {c.Count}×")) : ""));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { ctx.Say("Eigene Fachtexte nicht lesbar: " + ex.Message); }

        // Wie ein Mensch: suchen, lesen, prüfen – und anders weitersuchen, solange kein Kandidat in zwei unabhängigen
        // Quellen der Zielsprache steht. Reichen schon die eigenen Fachtexte, bleibt die Websuche aus.
        var readUrls = new HashSet<string>(prior?.Evidence.Select(e => e.Url) ?? [], StringComparer.OrdinalIgnoreCase);
        var tried = new List<string>(prior?.Queries ?? []);
        var pagesRead = 0;
        var rounds = 0;
        var support = Support(candidates, evidence, q.TargetLanguage);
        var conclusive = evidence.Any(e => e.Local) && Weighted(candidates, evidence, q.TargetLanguage, q.Term).Values.Any(SourceKinds.IsConclusive);
        if (conclusive) ctx.Say("Die eigenen Fachtexte belegen es schon – keine Websuche nötig.");
        var nextQueries = analysis.Queries;
        var outOfTime = false;
        var idle = 0;                                                    // Runden in Folge ohne neue Belege oder Kandidaten
        var saturated = false;
        for (var round = 1; round <= MaxRounds && !conclusive; round++)
        {
            var evidenceBefore = evidence.Count(e => Languages.Matches(e.Language, q.TargetLanguage));
            var candidatesBefore = candidates.Count;
            if (round > 1 && Deadline is { } deadline && DateTime.UtcNow > deadline)
            {
                ctx.Say("Zeitbudget erreicht – ich schließe mit dem Gefundenen ab.");
                outOfTime = true;
                break;
            }
            rounds = round;
            if (round > 1) ctx.Say($"Runde {round}: noch nicht eindeutig belegt – suche anders weiter …");
            var sourcePages = new List<(string Url, string Text, IReadOnlyDictionary<string, string> Versions)>();
            foreach (var (lang, terms) in new[] { (q.SourceLanguage, (IReadOnlyList<string>)[q.Term]), (q.TargetLanguage, (IReadOnlyList<string>)candidates) })
            {
                var queries = (nextQueries?.FirstOrDefault(kv => Languages.Matches(kv.Key, lang)).Value
                               ?? (round == 1 ? terms.Select(t => $"\"{t}\" {q.Topic}".Trim()).ToList() : []))
                    .Select(x => x.Trim()).Where(x => x.Length > 0 && !tried.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct().Take(5).ToList();
                var sites = q.Profile?.SitesFor(lang) ?? [];
                var results = new List<SearchResult>();
                foreach (var query in queries)
                {
                    ct.ThrowIfCancellationRequested();
                    tried.Add(query);
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
                    if (!readUrls.Add(r.Url)) continue;                                     // schon gelesen: nicht noch einmal
                    ctx.Say($"[{lang}] Lese {r.Url}");
                    var text = await ctx.Fetcher.FetchTextAsync(r.Url, ctx.MaxCharsPerPage, ct);
                    var versions = ctx.Fetcher.LastLanguageVersions;
                    if (text is not { Length: > 200 })
                    {
                        ctx.Say($"[{lang}]   übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                        continue;
                    }
                    read++;
                    pagesRead++;
                    await ExtractAsync(lang, terms, r.Url, r.Title, text);
                    if (Languages.Matches(lang, q.SourceLanguage) && Resources.ReferenceLibrary.Mentions(text, q.Term) && versions.Count > 0)
                        sourcePages.Add((r.Url, text, versions));
                    // reicht es schon (ein Kandidat in zwei unabhängigen Quellen der Zielsprache)? Dann keine weitere Seite lesen
                    if (Languages.Matches(lang, q.TargetLanguage) && Weighted(candidates, evidence, q.TargetLanguage, q.Term).Values.Any(SourceKinds.IsConclusive))
                    {
                        ctx.Say($"[{lang}] reicht schon – keine weitere Seite nötig.");
                        break;
                    }
                }
            }

            // Sprachversion derselben Seite: was die Seite in der Zielsprache für denselben Begriff schreibt
            foreach (var page in sourcePages.Take(2))
                await ParallelPageAsync(page.Text, page.Versions);

            support = Support(candidates, evidence, q.TargetLanguage);
            // gewichtet: ein Forum allein reicht nicht, eine Behörde, Norm oder Termbank mit einer zweiten Quelle schon
            var best = Weighted(candidates, evidence, q.TargetLanguage, q.Term).Where(w => SourceKinds.IsConclusive(w.Value))
                .OrderByDescending(w => w.Value.Weight).FirstOrDefault();
            if (best.Key != null)
            {
                conclusive = true;
                ctx.Say($"Belegt: „{best.Key}“ in {best.Value.Sites} unabhängigen Quellen der Zielsprache (Gewicht {best.Value.Weight:0.##}).");
                break;
            }
            // Sättigung: zwei Runden ohne neue Belege und ohne neue Kandidaten – weiter suchen bringt kaum noch etwas
            var nothingNew = evidence.Count(e => Languages.Matches(e.Language, q.TargetLanguage)) == evidenceBefore && candidates.Count == candidatesBefore;
            idle = nothingNew ? idle + 1 : 0;
            if (idle >= 2)
            {
                ctx.Say("Sättigung: zwei Runden ohne neue Belege oder Kandidaten – ich schließe mit dem Gefundenen ab.");
                saturated = true;
                break;
            }
            if (round == MaxRounds)
            {
                ctx.Say($"Nach {round} Runden nicht eindeutig belegt.");
                break;
            }
            var next = await NextStrategyAsync(q, analysis.Meaning, srcName, tgtName, candidates, SupportText(candidates, evidence, q.TargetLanguage), tried, evidence, ct);
            foreach (var c in next?.Candidates ?? [])
                if (c.Trim().Length > 0 && candidates.Count < 8 && !candidates.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase))
                    candidates.Add(c.Trim());
            nextQueries = next?.Queries;
            if (nextQueries == null || nextQueries.Values.All(v => v == null || v.Count == 0))
            {
                // Der KI fällt nichts mehr ein – noch nicht aufgeben: eigene, einfache Suchwege, die noch nicht versucht sind
                nextQueries = FallbackQueries(q, tgtName, candidates, tried);
                if (nextQueries.Values.All(v => v.Count == 0))
                {
                    ctx.Say("Keine weiteren Suchwege – alle naheliegenden Anfragen sind versucht.");
                    break;
                }
                ctx.Say("Die KI schlägt keine neuen Suchwege vor – ich suche mit einfachen Anfragen weiter.");
            }
        }
        support = Support(candidates, evidence, q.TargetLanguage);
        ctx.Say($"{evidence.Count} überprüfte Belege gesammelt ({pagesRead} Seiten, {rounds} Runde(n)).");

        // Belege aus einer gelesenen Seite: Zitate des Modells (nur wörtliche) und die Fundstellen der Begriffe selbst
        async Task ExtractAsync(string lang, IReadOnlyList<string> terms, string url, string title, string text)
        {
            FoundList found;
            try
            {
                found = JsonExtract.Parse<FoundList>(await ctx.Model.AskAsync(
                    "You collect evidence for terminology work. Copy quotes exactly. Output only JSON.",
                    $$"""
                    Terms of interest ({{Languages.DisplayName(lang)}}): {{string.Join(", ", terms)}}
                    Subject: {{q.Topic}}

                    === TEXT ({{url}})
                    {{text}}
                    === END

                    Find up to 4 passages that define one of the terms, state its legal/technical effect, or show how it is used.
                    For each: term, quote (copied EXACTLY, max. 40 words), note (what the passage shows, in {{ctx.ExplanationLanguage}}).
                    Also say what kind of page this is: termbank (glossary, terminology database), law, standard, authority,
                    association (professional body), manufacturer, company (a company's own website), academic, dictionary,
                    encyclopedia, news, forum, blog or web.
                    Return: {"kind": "...", "items": [{"term": "...", "quote": "...", "note": "..."}]}  (empty list if nothing relevant)
                    """, ct));
            }
            catch (AiException ex)
            {
                ctx.Say($"[{lang}]   Auswertung fehlgeschlagen: {ex.Message}");
                found = new FoundList([]);
            }
            var pageNorm = KnowledgeAgent.Normalize(text);
            foreach (var f in found.Items)
            {
                if (string.IsNullOrWhiteSpace(f.Quote) || !pageNorm.Contains(KnowledgeAgent.Normalize(f.Quote)))
                {
                    ctx.Say($"[{lang}]   Beleg verworfen (steht so nicht auf der Seite).");
                    continue;
                }
                evidence.Add(new TermEvidenceItem(evidence.Count + 1, lang, f.Term, f.Quote.Trim(), url, title, f.Note, Kind: SourceKinds.Classify(url, found.Kind)));
            }
            // Den Begriff selbst auf der Seite suchen – kleine Modelle übersehen ihn oft; der Satz drumherum ist der Beleg
            foreach (var term in terms)
            {
                if (evidence.Any(e => e.Url == url && Resources.ReferenceLibrary.Mentions(e.Quote, term))) continue;   // schon belegt von dieser Seite
                if (Resources.ReferenceLibrary.Sentence(text, term) is { } sentence)
                    evidence.Add(new TermEvidenceItem(evidence.Count + 1, lang, term, sentence, url, title, "Fundstelle auf der Seite", Kind: SourceKinds.Classify(url, found.Kind)));
            }
        }

        // Ein Mensch schaltet auf einer Herstellerseite die Sprache um: dieselbe Seite in der Zielsprache lesen
        async Task ParallelPageAsync(string sourceText, IReadOnlyDictionary<string, string> versions)
        {
            if (PageFetcher.VersionFor(versions, q.TargetLanguage) is not { } url || !readUrls.Add(url)) return;
            ctx.Say($"Sprachversion derselben Seite: {url}");
            var text = await ctx.Fetcher.FetchTextAsync(url, ctx.MaxCharsPerPage, ct);
            if (text is not { Length: > 200 })
            {
                ctx.Say($"  übersprungen: {ctx.Fetcher.LastSkipReason ?? "zu wenig Text"}");
                return;
            }
            pagesRead++;
            var context = Resources.ReferenceLibrary.Sentence(sourceText, q.Term) ?? q.Term;
            ParallelFind? found;
            try
            {
                found = JsonExtract.Parse<ParallelFind>(await ctx.Model.AskAsync(
                    "You align parallel texts for terminology work. Copy quotes exactly. Output only JSON.",
                    $$"""
                    The following {{tgtName}} page is the {{tgtName}} version of a {{srcName}} page. The {{srcName}} page says:
                    "{{context}}"
                    Which {{tgtName}} term does this page use for "{{q.Term}}"? Copy the sentence that contains it EXACTLY.

                    === TEXT ({{url}})
                    {{text}}
                    === END

                    Return: {"term": "...", "quote": "..."}  ({"term": ""} if the page does not contain it)
                    """, ct));
            }
            catch (AiException ex)
            {
                ctx.Say($"  Auswertung fehlgeschlagen: {ex.Message}");
                return;
            }
            if (found?.Term is not { Length: > 0 } term || found.Quote is not { Length: > 0 } quote) return;
            if (!KnowledgeAgent.Normalize(text).Contains(KnowledgeAgent.Normalize(quote)) || !Resources.ReferenceLibrary.Mentions(quote, term))
            {
                ctx.Say("  Beleg verworfen (steht so nicht auf der Seite).");
                return;
            }
            term = term.Trim();
            evidence.Add(new TermEvidenceItem(evidence.Count + 1, q.TargetLanguage, term, quote.Trim(), url,
                $"{new Uri(url).Host} ({q.TargetLanguage})", $"Sprachversion derselben Seite – dort: „{context}“", Kind: SourceKinds.Classify(url)));
            if (!candidates.Contains(term, StringComparer.OrdinalIgnoreCase)) candidates.Add(term);
            ctx.Say($"  Die Seite verwendet „{term}“.");
        }

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
            {{ownCounts}}
            Research: {{rounds}} search round(s), {{pagesRead}} pages read; independent {{tgtName}} sources that use each candidate (with the kind of source): {{SupportText(candidates, evidence, q.TargetLanguage)}}{{(conclusive ? "" : " – not conclusive, say so if you recommend a candidate without such sources")}}

            Evidence from specialist sources and the translator's own reference texts:
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
        if (!conclusive && evidence.Count > 0)
            warnings.Add($"Nach {rounds} Suchrunden ist kein Kandidat in zwei unabhängigen Quellen der Zielsprache belegt – die Empfehlung prüfen.");
        if (saturated && !conclusive)
            warnings.Add($"Nach {rounds} Suchrunden brachten zwei Runden nichts Neues – weiter zu suchen lohnt sich kaum; die Empfehlung prüfen.");
        if (outOfTime)
            warnings.Add($"Zeitbudget erreicht: nach {rounds} von höchstens {MaxRounds} Suchrunden abgeschlossen – mit „Recherchieren“ im Reiter Recherche lässt sich gründlicher suchen.");
        return new TermComparisonResult(cmp.Source, cmp.Candidates, cmp.Recommendation, warnings, evidence, usedSites, rounds, pagesRead, conclusive, tried);
    }

    /// <summary>
    /// In wie vielen unabhängigen Quellen der Zielsprache jeder Kandidat wörtlich steht: je Website (bzw. je eigener
    /// Datei) einmal – zehn Seiten desselben Herstellers sind eine Quelle.
    /// </summary>
    /// <summary>
    /// Einfache Suchwege, wenn der KI keine neuen mehr einfallen: jeder Kandidat allein und mit Fachgebiet bzw. „Glossar“ in
    /// der Zielsprache, der Ausgangsbegriff zusammen mit dem Namen der Zielsprache – nur, was noch nicht versucht ist.
    /// </summary>
    internal static Dictionary<string, List<string>> FallbackQueries(TermQuery q, string targetName, IReadOnlyList<string> candidates, IReadOnlyList<string> tried)
    {
        bool New(string x) => !tried.Contains(x, StringComparer.OrdinalIgnoreCase);
        var glossary = Languages.Primary(q.TargetLanguage) switch { "de" => "Glossar", "pl" => "słownik", "fr" => "glossaire", "es" => "glosario", "it" => "glossario", _ => "glossary" };
        var target = candidates.Take(4).SelectMany(c => new[] { $"\"{c}\"", $"\"{c}\" {q.Topic}".Trim(), $"\"{c}\" {glossary}" })
            .Distinct().Where(New).Take(5).ToList();
        var source = new[] { $"\"{q.Term}\" {targetName}", $"\"{q.Term}\" {q.Topic}".Trim() }.Distinct().Where(New).Take(2).ToList();
        return new Dictionary<string, List<string>> { [q.TargetLanguage] = target, [q.SourceLanguage] = source };
    }

    /// <summary>Gewichteter Beleg je Kandidat: unabhängige Websites und ihr Gewicht nach Art der Quelle (siehe <see cref="SourceKinds"/>).</summary>
    internal static Dictionary<string, (int Sites, double Weight)> Weighted(IEnumerable<string> candidates, IReadOnlyList<TermEvidenceItem> evidence,
        string targetLanguage, string? subject = null) =>
        candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(c => c, c => SourceKinds.Support(evidence
            .Where(e => Languages.Matches(e.Language, targetLanguage) && Resources.ReferenceLibrary.Mentions(e.Quote + " " + e.Title, c))
            .Select(e => (e.Url, e.Kind, e.Local)), subject), StringComparer.OrdinalIgnoreCase);

    /// <summary>Für die Prompts: „mechanical seal: 2 (Behörde, Forum)“ – Zahl der Quellen und ihre Art.</summary>
    internal static string SupportText(IEnumerable<string> candidates, IReadOnlyList<TermEvidenceItem> evidence, string targetLanguage) =>
        string.Join(", ", candidates.Distinct(StringComparer.OrdinalIgnoreCase).Select(c =>
        {
            var kinds = evidence.Where(e => Languages.Matches(e.Language, targetLanguage) && Resources.ReferenceLibrary.Mentions(e.Quote + " " + e.Title, c))
                .GroupBy(e => e.Local ? e.Url : Resources.SiteCatalog.DomainOf(e.Url) ?? e.Url).Select(g => g.First().Local ? SourceKinds.Own : g.First().Kind ?? SourceKinds.Web).ToList();
            return $"{c}: {kinds.Count}" + (kinds.Count > 0 ? $" ({string.Join(", ", kinds)})" : "");
        }));

    internal static Dictionary<string, int> Support(IEnumerable<string> candidates, IReadOnlyList<TermEvidenceItem> evidence, string targetLanguage) =>
        candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(c => c, c => evidence
            .Where(e => Languages.Matches(e.Language, targetLanguage) && Resources.ReferenceLibrary.Mentions(e.Quote + " " + e.Title, c))
            .Select(e => e.Local ? e.Url.Split('#')[0] : Resources.SiteCatalog.DomainOf(e.Url) ?? e.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Nächster Suchweg, wie ihn ein erfahrener Übersetzer wählen würde – mit dem, was schon versucht und gefunden ist.</summary>
    private async Task<NextStrategy?> NextStrategyAsync(TermQuery q, string meaning, string srcName, string tgtName, IReadOnlyList<string> candidates,
        string support, IReadOnlyList<string> tried, IReadOnlyList<TermEvidenceItem> evidence, CancellationToken ct)
    {
        var found = evidence.TakeLast(6).Select(e => $"- ({e.Language}, {(e.Local ? SourceKinds.Own : e.Kind ?? SourceKinds.Web)}) {e.Term}: \"{Resources.ReferenceLibrary.Sentence(e.Quote, e.Term) ?? e.Quote}\"").ToList();
        try
        {
            return JsonExtract.Parse<NextStrategy>(await ctx.Model.AskAsync(
                "You are an experienced human translator doing terminology research online. Output only JSON.",
                $$"""
                Research on "{{q.Term}}" ({{srcName}} → {{tgtName}}) is not conclusive yet.
                Meaning in context: {{meaning}}
                {{(string.IsNullOrWhiteSpace(q.Context) ? "" : "Context: " + q.Context)}}
                Candidates and in how many independent {{tgtName}} sources each was found (with the kind of source): {{support}}
                A candidate found only in forums or blogs is a lead, not proof: look for it in termbanks or glossaries, standards,
                authorities, professional bodies or manufacturer documentation.
                Searches already tried (do not repeat them): {{string.Join(" | ", tried)}}
                {{(found.Count > 0 ? "Evidence so far:\n" + string.Join("\n", found) : "No evidence found so far.")}}
                {{(string.IsNullOrWhiteSpace(q.Focus) ? "" : "Still missing – search specifically for this: " + q.Focus)}}

                Search on like an experienced human translator: describe the concept in {{tgtName}} by its key features; look for
                bilingual glossaries and dictionaries, manufacturer or product documentation, standards, authorities and professional
                bodies of the target country; try synonyms, broader and narrower terms, and the source term together with likely
                {{tgtName}} words. Write 2–4 new search queries per language. If what you read suggests better {{tgtName}} terms, list them.
                Return: {"queries": {"{{q.SourceLanguage}}": ["..."], "{{q.TargetLanguage}}": ["..."]}, "candidates": ["..."]}
                """, ct));
        }
        catch (AiException ex)
        {
            ctx.Say("Nächster Suchweg nicht lesbar: " + ex.Message);
            return null;
        }
    }

    private sealed record LinkedArticle(string SourceTitle, string TargetTitle, string Candidate, string Url, string? Lead);

    /// <summary>Die Sprachverknüpfung der Wikipedia abfragen (Tests ohne Netz schalten das ab).</summary>
    internal static bool WikipediaLinks { get; set; } = true;

    /// <summary>
    /// Wikipedia-Artikel zum Begriff in der Ausgangssprache und sein Gegenstück in der Zielsprache (über die
    /// Sprachverknüpfung), mit dem ersten Satz als Beleg. Geschützte Angaben (Namen …) gehen nicht an Wikipedia.
    /// </summary>
    private async Task<LinkedArticle?> LinkedArticleAsync(TermQuery q, CancellationToken ct)
    {
        if (!WikipediaLinks) return null;
        if (ctx.Model is Privacy.PrivacyChatModel privacy && privacy.MaskText(q.Term) != q.Term) return null;
        try
        {
            var article = await WikiApi.ArticleAsync("wikipedia", q.SourceLanguage, q.Term, 0, ct);
            if (article?.Versions.GetValueOrDefault(WikiApi.Code(q.TargetLanguage)) is not { Length: > 0 } title) return null;
            var target = await WikiApi.ArticleAsync("wikipedia", q.TargetLanguage, title, 1500, ct);
            if (target == null) return null;
            ctx.Say($"Wikipedia verknüpft „{article.Title}“ mit „{target.Title}“.");
            // „Dichtung (Technik)“ → „Dichtung“: die Klammer unterscheidet nur gleichnamige Artikel
            var candidate = System.Text.RegularExpressions.Regex.Replace(target.Title, @"\s*\([^)]*\)$", "");
            return new LinkedArticle(article.Title, target.Title, candidate, target.Url, FirstSentence(target.Text));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Erster Satz eines Artikeltexts (höchstens 300 Zeichen), wörtlich, damit er als Beleg taugt.</summary>
    internal static string? FirstSentence(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        var end = System.Text.RegularExpressions.Regex.Match(t, @"(?<=\p{Ll}{2}|\))[.!?](?=\s|$)");
        var sentence = end.Success && end.Index < 300 ? t[..(end.Index + 1)] : t[..Math.Min(t.Length, 300)];
        return sentence.Split('\n')[0].Trim();
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
