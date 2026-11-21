using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Agents;

/// <summary>Wie schwierig ein Ausdruck zu finden bzw. zu übertragen ist.</summary>
public enum ResearchDifficulty { Easy, Medium, Hard }

/// <summary>Ein Fundort, den die KI für diese Art Information nennt: Seite, Sprache, was man dort findet.</summary>
public sealed record SiteHint(string Domain, string Language, string What);

/// <summary>Ein Ausdruck im Dokument, der Recherche braucht.</summary>
public sealed class ResearchItem
{
    public string Expression { get; set; } = "";
    /// <summary>Satz aus dem Dokument, in dem er steht.</summary>
    public string Context { get; set; } = "";
    /// <summary>Fachbegriff, Rechtsbegriff, Realie, Redewendung, Abkürzung, Name …</summary>
    public string Category { get; set; } = "";
    public string Reason { get; set; } = "";
    public ResearchDifficulty Difficulty { get; set; }
    public string DifficultyReason { get; set; } = "";
    public List<int> SegmentIds { get; set; } = new();
}

public sealed class DossierCandidate
{
    public string Term { get; set; } = "";
    public Equivalence Equivalence { get; set; }
    public string? SuitableWhen { get; set; }
    public string? UnsuitableWhen { get; set; }
    public List<string> Differences { get; set; } = new();
    /// <summary>Nummern der Belege (<see cref="SourceRef.Number"/>), die diese Entsprechung stützen.</summary>
    public List<int> Evidence { get; set; } = new();
}

/// <summary>
/// Dossier zu einem Ausdruck: was er in Ausgangssprache und -land bedeutet, was ihm in Zielsprache und -land entspricht
/// (Varianten, wann und warum welche), Hintergrund, Empfehlung mit Strategie, Warnungen, Quellen – und wie schwierig er
/// zu finden ist.
/// </summary>
public sealed class TermDossier
{
    public string Expression { get; set; } = "";
    public string? Context { get; set; }
    public string? FileId { get; set; }
    public List<int> SegmentIds { get; set; } = new();
    /// <summary>Sprache mit Land, z. B. „es-ES“.</summary>
    public string SourceLanguage { get; set; } = "";
    /// <summary>Sprache mit Land, z. B. „es-MX“.</summary>
    public string TargetLanguage { get; set; } = "";
    public string Category { get; set; } = "";
    public ResearchDifficulty Difficulty { get; set; }
    /// <summary>Einschätzung der KI (in der Sprache der Begründungen).</summary>
    public string DifficultyReason { get; set; } = "";
    /// <summary>Feste Einordnung nach der Recherche (Programmtext, wird übersetzt angezeigt).</summary>
    public string DifficultyNote { get; set; } = "";
    public string Meaning { get; set; } = "";
    public List<string> KeyFeatures { get; set; } = new();
    public string? SourceSystem { get; set; }
    public string? TargetSystem { get; set; }
    public List<DossierCandidate> Candidates { get; set; } = new();
    public string? Recommended { get; set; }
    public string? Strategy { get; set; }
    public string? Rendering { get; set; }
    public string? Reasoning { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<SourceRef> Sources { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Welche KI das Dossier erstellt hat (im Verbund: die Modelle, die geantwortet haben); ältere Dossiers: leer.</summary>
    public string? Model { get; set; }
    /// <summary>Womit im Netz gesucht wurde (Wikipedia, Brave …) oder „keine Websuche“; ältere Dossiers: leer.</summary>
    public string? Search { get; set; }
    /// <summary>Die Fassung vor der letzten erneuten Recherche (eine Stufe), damit sich nichts Gutes verliert.</summary>
    public TermDossier? Previous { get; set; }
    /// <summary>Wo man diese Art Information findet (Vorschläge der KI, landen in der Quellen-Datenbank).</summary>
    public List<SiteHint> WhereToLook { get; set; } = new();

    /// <summary>Begriffsfeld: Oberbegriff, Geschwisterbegriffe, Unterscheidung, was im Satz gemeint ist; ältere Dossiers: leer.</summary>
    public ConceptField? ConceptField { get; set; }

    /// <summary>Wie viele Suchrunden die Recherche brauchte und wie viele Seiten sie las; ältere Dossiers: 0.</summary>
    public int Rounds { get; set; }

    public int PagesRead { get; set; }

    /// <summary>Ein Kandidat steht in zwei unabhängigen Quellen der Zielsprache; ältere Dossiers: leer.</summary>
    public bool? Conclusive { get; set; }

    /// <summary>
    /// Alle bisher gestellten Suchanfragen (auch die früherer Recherchen zu diesem Ausdruck) – eine erneute Recherche
    /// wiederholt sie nicht; ältere Dossiers: leer.
    /// </summary>
    public List<string> Queries { get; set; } = new();

    /// <summary>Wie lange die Recherche dauerte; ältere Dossiers: leer.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Aus dem Recherche-Gedächtnis übernommen: aus welchem Projekt, wann („Projekt X (03.11.2026)“); sonst leer.</summary>
    public string? Origin { get; set; }

    /// <summary>In welcher Sprache Bedeutung, Begründung und Hinweise geschrieben sind (Einstellung „Erklärungen“); ältere Dossiers: leer = Deutsch.</summary>
    public string? ExplanationLanguage { get; set; }
}

/// <summary>
/// Recherche zum Dokument: Die KI sucht im Dokument die Ausdrücke, die Recherche brauchen (Fachbegriffe, Rechtsbegriffe,
/// Realien wie Tiere oder Speisen, Redewendungen, Abkürzungen) und schätzt ein, wie schwierig sie sind. Zu jedem erstellt
/// der Begriffsvergleich ein Dossier – mit Sprache und Land auf beiden Seiten (Spanisch in Spanien ist nicht Spanisch in
/// Mexiko; ein Gesetz aus Land A hat in Land B ein anderes Gegenstück).
/// </summary>
public sealed partial class DocumentResearch(AgentContext ctx)
{
    /// <summary>
    /// Zu Fachbegriffen, Rechtsbegriffen und Realien auch das Begriffsfeld klären (Geschwisterbegriffe, Unterscheidung,
    /// Beratung); Redewendungen und Abkürzungen haben keins.
    /// </summary>
    public bool ConceptFields { get; init; } = true;

    /// <summary>
    /// Zeitbudget je Ausdruck (null = keins): danach keine neue Suchrunde mehr, das Dossier entsteht aus dem Gefundenen –
    /// so bleibt z. B. „Leere per KI“ mit Recherche nicht an einem schwierigen Ausdruck stehen.
    /// </summary>
    public TimeSpan? TimeBudget { get; init; }

    internal static bool HasConceptField(string category) =>
        category.Trim().ToLowerInvariant() is not ("idiom" or "abbreviation" or "name" or "redewendung" or "abkürzung");

    /// <summary>So viele Zeichen des Dokuments liest die KI auf einmal; ein längeres Dokument wird in Abschnitten gelesen.</summary>
    public const int ChunkChars = 4000;

    /// <summary>
    /// Alle Ausdrücke des Dokuments, die Recherche brauchen: Die KI liest das ganze Dokument in Abschnitten (nicht nur einen
    /// Ausschnitt) und nennt je Abschnitt bis zu <paramref name="max"/> Ausdrücke; dazu findet das Programm selbst
    /// Abkürzungen, Paragraphenverweise und EU-Rechtsakte. Übernommen wird nur, was wörtlich im Dokument steht.
    /// </summary>
    public async Task<List<ResearchItem>> FindAsync(BilingualDocument doc, string sourceLanguage, string targetLanguage, DocumentBrief? brief,
        string? topic, int max = 40, CancellationToken ct = default) =>
        await FindInAsync(doc, doc.Segments.Where(s => !s.Locked).ToList(), sourceLanguage, targetLanguage, brief, topic, max, ct);

    /// <summary>Wie <see cref="FindAsync"/>, aber nur in den angegebenen Segmenten (z. B. dem gewählten).</summary>
    public async Task<List<ResearchItem>> FindInAsync(BilingualDocument doc, IReadOnlyList<Segment> scope, string sourceLanguage, string targetLanguage,
        DocumentBrief? brief, string? topic, int max = 40, CancellationToken ct = default)
    {
        ctx.Say("Suche Ausdrücke, die Recherche brauchen …");
        var sentences = scope.Select(s => TagText.StripTags(s.Source)).Where(s => s.Trim().Length > 0).ToList();
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var s in sentences)
        {
            if (current.Length > 0 && current.Length + s.Length > ChunkChars) { chunks.Add(current.ToString()); current.Clear(); }
            current.AppendLine(s);
        }
        if (current.Length > 0) chunks.Add(current.ToString());

        var replies = new string?[chunks.Count];
        var parallel = Math.Max(1, (ctx.Model as IParallelChatModel)?.Parallelism ?? 1);
        using var gate = new SemaphoreSlim(parallel);
        var done = 0;
        await Task.WhenAll(chunks.Select(async (chunk, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var prompt = new StringBuilder();
                prompt.AppendLine($"A translator will translate this document from {Place(sourceLanguage)} into {Place(targetLanguage)}.");
                if (!string.IsNullOrWhiteSpace(topic)) prompt.AppendLine($"Subject field: {topic}");
                if (brief?.Instructions(targetLanguage) is { Length: > 0 } about) prompt.Append(about);
                var part = chunks.Count > 1 ? $"part ({index + 1} of {chunks.Count}) of the " : "";
                prompt.AppendLine($$"""
                    List ALL expressions in this {{part}}document that need research before translating (up to {{max}}):
                    technical terms, legal concepts and institutions (which may have no or a different counterpart in the target
                    country), culture-bound realia (animals, plants, food, customs …), idioms, abbreviations, names of organisations
                    or laws. Be thorough – rather one too many than one missing. Skip everyday words and names of people.
                    Use the expression exactly as it is written in the document.
                    For each: category (term|legal|realia|idiom|abbreviation|institution|other), why it needs research, and how difficult
                    it is to find and translate for the target country: easy|medium|hard with a short reason.
                    Reply with JSON only: {"items": [{"expression": "...", "category": "...", "reason": "...", "difficulty": "...", "difficulty_reason": "..."}]}
                    Write reason and difficulty_reason in {{ctx.ExplanationLanguage}}.
                    """);
                prompt.AppendLine();
                prompt.AppendLine("Document:");
                prompt.AppendLine(chunk);
                replies[index] = await ctx.Model.AskAsync("You prepare research for professional translators.", prompt.ToString(), ct);
            }
            catch (AiException ex) { ctx.Say($"Abschnitt {index + 1}: {ex.Message}"); }
            finally
            {
                gate.Release();
                if (chunks.Count > 1) ctx.Say($"Dokument durchgesehen: {Interlocked.Increment(ref done)} von {chunks.Count} Abschnitten …");
            }
        }));

        var items = new List<ResearchItem>();
        void Add(string expression, string category, string reason, ResearchDifficulty difficulty, string difficultyReason)
        {
            expression = expression.Trim();
            if (expression.Length == 0) return;
            if (items.FirstOrDefault(x => x.Expression.Equals(expression, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                if (difficulty > known.Difficulty) (known.Difficulty, known.DifficultyReason) = (difficulty, difficultyReason);
                return;
            }
            var segments = doc.Segments.Where(s => Contains(TagText.StripTags(s.Source), expression)).ToList();
            if (segments.Count == 0) return;                                            // nur, was wirklich im Dokument steht
            if (SkipReason(expression, segments) != null) return;                       // „Frau Kowalska“, Anschrift, Datum: kein Rechercheziel
            items.Add(new ResearchItem
            {
                Expression = expression,
                Context = TagText.StripTags(segments[0].Source),
                Category = category,
                Reason = reason,
                Difficulty = difficulty,
                DifficultyReason = difficultyReason,
                SegmentIds = segments.Select(s => s.Id).ToList(),
            });
        }
        foreach (var reply in replies)
        {
            if (reply == null || JsonExtract.Find(reply) is not { } json) continue;
            JsonArray? arr;
            try { arr = JsonNode.Parse(json)?["items"] as JsonArray; }
            catch (JsonException) { continue; }
            foreach (var i in arr ?? [])
                Add((string?)i?["expression"] ?? "", ((string?)i?["category"] ?? "").Trim(), ((string?)i?["reason"] ?? "").Trim(),
                    ParseDifficulty((string?)i?["difficulty"]), ((string?)i?["difficulty_reason"] ?? "").Trim());
        }
        // was das Programm selbst sicher erkennt
        foreach (var (expression, category, reason) in RuleItems(sentences))
            Add(expression, category, reason, ResearchDifficulty.Medium, "");
        return items;
    }

    /// <summary>Abkürzungen, Paragraphenverweise und EU-Rechtsakte, die im Text stehen – ohne KI erkannt.</summary>
    internal static IEnumerable<(string Expression, string Category, string Reason)> RuleItems(IEnumerable<string> sentences)
    {
        foreach (var s in sentences)
        {
            foreach (System.Text.RegularExpressions.Match m in EuActRegex().Matches(s))
                yield return (m.Value.Trim(), "legal", "EU-Rechtsakt – amtliche Bezeichnung in der Zielsprache klären");
            foreach (System.Text.RegularExpressions.Match m in CitationRegex().Matches(s))
                yield return (m.Value.Trim(), "legal", "Rechtsvorschrift – Gegenstück bzw. übliche Zitierweise in der Zielsprache klären");
            foreach (System.Text.RegularExpressions.Match m in AbbreviationRegex().Matches(s))
                if (!CommonAbbreviations.Contains(m.Value))
                    yield return (m.Value, "abbreviation", "Abkürzung – Bedeutung und Entsprechung in der Zielsprache klären");
        }
    }

    /// <summary>Römische Zahlen und Wörter, die nur großgeschrieben sind, aber keine Recherche brauchen.</summary>
    private static readonly HashSet<string> CommonAbbreviations = new(StringComparer.Ordinal)
        { "II", "III", "IV", "VI", "VII", "VIII", "IX", "XI", "XII", "OK", "PS", "CEO", "USA", "EU", "UK", "PDF", "URL", "FAQ" };

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:Verordnung|Richtlinie|Beschluss|Durchführungsverordnung|Regulation|Directive|Decision|Rozporządzenie|Dyrektywa|Decyzja)\s+\((?:EU|EG|EWG|EC|EEC|UE|WE)\)\s+(?:Nr\.\s*|No\s*)?\d{2,4}/\d{1,4}|\b(?:Richtlinie|Directive|Dyrektywa)\s+\d{2,4}/\d{1,4}/(?:EU|EG|EWG|EC|EEC|UE|WE)\b")]
    private static partial System.Text.RegularExpressions.Regex EuActRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:§§?|Art\.|Artikel|art\.)\s*\d+[a-z]?(?:\s*(?:Abs\.|Absatz|ust\.)\s*\d+)?(?:\s*(?:S\.|Satz|Nr\.|pkt)\s*\d+)?\s+(?:[A-ZÄÖÜ][a-zäöüß]*){1,3}[A-ZÄÖÜ][A-Za-zäöüß]*\b")]
    private static partial System.Text.RegularExpressions.Regex CitationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\p{L}\p{N}-])(?:[A-ZÄÖÜ]{2,6}|[A-ZÄÖÜ][a-z]?[A-ZÄÖÜ]{1,4}[a-z]?[A-ZÄÖÜ]?)(?![\p{L}\p{N}-])")]
    private static partial System.Text.RegularExpressions.Regex AbbreviationRegex();

    /// <summary>Dossier zu einem Ausdruck über den Begriffsvergleich (Fachseiten beider Länder, Belege).</summary>
    /// <param name="previous">Ein früheres Dossier zum selben Ausdruck: die neue Recherche setzt dort an (siehe <see cref="PriorResearch"/>).</param>
    /// <param name="focus">Was nach einer früheren Recherche noch fehlt – die neue sucht gezielt danach.</param>
    public async Task<TermDossier> ResearchAsync(ResearchItem item, string sourceLanguage, string targetLanguage, string? topic,
        SourceProfile? profile = null, IReadOnlyList<TermHit>? knownTerms = null, string? fileId = null, CancellationToken ct = default,
        TermDossier? previous = null, string? focus = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        ctx.Say($"Recherchiere „{item.Expression}“ ({Place(sourceLanguage)} → {Place(targetLanguage)}) …");
        var pool = Privacy.PrivacyContext.Unwrap(ctx.Model) as Ai.PooledChatModel;
        var before = pool?.Usage.ToDictionary(u => u.Name, u => u.Done);
        // Seiten aus der Quellen-Datenbank, die bei früheren Recherchen zu diesem Thema Belege lieferten, gezielt mit durchsuchen
        var topics = Resources.SiteCatalog.Topics(topic, profile);
        var learned = Resources.SiteCatalog.Augment(profile, topics, sourceLanguage, targetLanguage);
        if (!ReferenceEquals(learned, profile))
            ctx.Say("Gelernte Fachseiten: " + string.Join(", ", new[] { sourceLanguage, targetLanguage }.SelectMany(l => learned!.SitesFor(l)).Distinct()));
        // Erst das Begriffsfeld (welcher Begriff der Familie ist gemeint?), dann der Begriffsvergleich mit dessen Entsprechung als Kandidat
        var field = ConceptFields && HasConceptField(item.Category)
            ? await new ConceptFieldAgent(ctx).RunAsync(item.Expression, item.Context, sourceLanguage, targetLanguage, topic, ct)
            : null;
        var hints = field?.MeantMember is { } meant
            ? new[] { meant.TargetTerm }.Concat(meant.TargetAlternatives.Take(1)).OfType<string>().Where(t => t.Length > 0).ToList()
            : null;
        var result = await new TermComparisonAgent(ctx) { Deadline = DateTime.UtcNow + TimeBudget }.RunAsync(new TermQuery(item.Expression, sourceLanguage, targetLanguage,
            item.Context, topic, learned, hints, knownTerms, previous == null ? null : PriorResearch.From(previous), focus), ct: ct);
        var whereToLook = await WhereToLookAsync(item.Expression, sourceLanguage, targetLanguage, topic, ct);
        // Im Verbund die Modelle, die für dieses Dossier tatsächlich geantwortet haben
        var model = pool == null ? ctx.Model.DisplayName
            : string.Join(", ", pool.Usage.Where(u => u.Done > before!.GetValueOrDefault(u.Name)).Select(u => u.Name)) is { Length: > 0 } used
                ? $"{ctx.Model.DisplayName}: {used}" : ctx.Model.DisplayName;
        var dossier = new TermDossier
        {
            Expression = item.Expression,
            Context = item.Context,
            FileId = fileId,
            SegmentIds = item.SegmentIds,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Category = item.Category,
            Meaning = result.Source.MeaningInContext,
            KeyFeatures = result.Source.KeyFeatures,
            SourceSystem = result.Source.System,
            Candidates = result.Candidates.Select(c => new DossierCandidate
            {
                Term = c.Term, Equivalence = c.Equivalence, SuitableWhen = c.SuitableWhen, UnsuitableWhen = c.UnsuitableWhen, Differences = c.Differences,
                Evidence = c.Evidence,
            }).ToList(),
            Recommended = result.Recommendation.Term,
            Strategy = result.Recommendation.Strategy,
            Rendering = result.Recommendation.Rendering,
            Reasoning = result.Recommendation.Reasoning,
            Warnings = result.Warnings,
            // jeder Beleg mit Nummer und wörtlichem Zitat (auf derselben Seite können mehrere stehen)
            Sources = result.Evidence.Select(e => new SourceRef(e.Language, e.Title, e.Url)
            {
                Number = e.Number, Term = e.Term, Quote = e.Quote, Version = e.Version, Retrieved = e.Local ? null : DateTime.UtcNow,
                Kind = e.Local ? SourceKinds.Own : e.Kind,
            }).ToList(),
            Model = model,
            Search = ctx.Search?.Name ?? "keine Websuche",
            WhereToLook = whereToLook,
            ConceptField = field,
            Rounds = result.Rounds,
            PagesRead = result.PagesRead,
            Conclusive = result.Conclusive,
            Queries = result.Queries?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
            ExplanationLanguage = ctx.ExplanationLanguage,
        };
        (dossier.Difficulty, dossier.DifficultyNote) = Assess(item, dossier, result.Evidence.Count);
        dossier.DifficultyReason = item.DifficultyReason;
        try
        {
            Resources.SiteCatalog.RecordEvidence(dossier.Sources, topics);
            Resources.SiteCatalog.RecordSuggestions(whereToLook, topics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ctx.Say("Quellen-Datenbank nicht gespeichert: " + ex.Message); }
        dossier.Duration = started.Elapsed;
        return dossier;
    }

    private sealed record SiteDto(string? Domain, string? Language, string? What);
    private sealed record SitesDto(List<SiteDto>? Sites);

    /// <summary>
    /// Die KI nennt Fundorte für diese Art Information in beiden Ländern (Behörden, amtliche Termbanken, Normungsstellen,
    /// Fachverbände, Wörterbücher). Ungeprüft – sie kommen als „vorgeschlagen“ in die Quellen-Datenbank. Ein Fehler hier
    /// hält die Recherche nicht auf.
    /// </summary>
    internal async Task<List<SiteHint>> WhereToLookAsync(string expression, string sourceLanguage, string targetLanguage, string? topic, CancellationToken ct)
    {
        ctx.Say("Wo findet man solche Angaben? …");
        try
        {
            var dto = JsonExtract.Parse<SitesDto>(await ctx.Model.AskAsync(
                "You are an experienced translator and documentation specialist. Output only JSON.",
                $$"""
                A translator researches "{{expression}}" ({{Place(sourceLanguage)}} → {{Place(targetLanguage)}}).
                {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic)}}
                Name up to 6 websites (domain names only) where this kind of information can be found and verified reliably –
                authorities, official terminology databases, standards bodies, professional associations, reference dictionaries –
                for BOTH countries. Only domains you are certain exist; no search engines, no Wikipedia.
                For each: language code ("{{sourceLanguage}}" or "{{targetLanguage}}") and what can be found there (a short phrase in {{ctx.ExplanationLanguage}}).
                Return: {"sites": [{"domain": "...", "language": "...", "what": "..."}]}
                """, ct));
            return (dto.Sites ?? [])
                .Select(s => (Domain: Resources.SiteCatalog.DomainOf(s.Domain ?? ""), s.Language, s.What))
                .Where(s => s.Domain != null && !s.Domain.Contains("wikipedia") && !s.Domain.Contains("google."))
                .Select(s => new SiteHint(s.Domain!, Languages.Matches(s.Language ?? "", sourceLanguage) ? sourceLanguage : targetLanguage, (s.What ?? "").Trim()))
                .DistinctBy(s => s.Domain).Take(6).ToList();
        }
        catch (Exception ex) when (ex is AiException or JsonException or InvalidOperationException)
        {
            ctx.Say("Keine Fundorte: " + ex.Message);
            return [];
        }
    }

    /// <summary>Schwierigkeit nach der Recherche: keine volle Entsprechung oder kaum Belege machen es schwer.</summary>
    internal static (ResearchDifficulty, string) Assess(ResearchItem item, TermDossier d, int evidence)
    {
        var best = d.Candidates.Count == 0 ? Equivalence.None : d.Candidates.Min(c => c.Equivalence);   // Full < Partial < None
        var level = best switch
        {
            Equivalence.None => ResearchDifficulty.Hard,
            Equivalence.Partial => (ResearchDifficulty)Math.Max((int)ResearchDifficulty.Medium, (int)item.Difficulty),
            _ => evidence >= 2 ? (ResearchDifficulty)Math.Min((int)item.Difficulty, (int)ResearchDifficulty.Medium) : ResearchDifficulty.Medium,
        };
        var why = best switch
        {
            Equivalence.None => "Keine Entsprechung im Zielland gefunden – umschreiben oder das Original mit Erklärung verwenden.",
            Equivalence.Partial => "Nur teilweise Entsprechung – Unterschiede beachten.",
            _ => evidence >= 2 ? $"Volle Entsprechung, {evidence} Belege." : "Volle Entsprechung, aber kaum Belege.",
        };
        // Wie lange gesucht werden musste, sagt auch etwas über die Schwierigkeit
        if (d.Conclusive == false && level < ResearchDifficulty.Hard)
        {
            level = (ResearchDifficulty)Math.Max((int)level, (int)ResearchDifficulty.Medium);
            why += "\n" + $"Nach {d.Rounds} Suchrunden nicht eindeutig belegt.";
        }
        else if (d.Rounds >= 3 && level == ResearchDifficulty.Easy)
        {
            level = ResearchDifficulty.Medium;
            why += "\n" + $"Erst nach {d.Rounds} Suchrunden gefunden.";
        }
        return (level, why);
    }

    internal static ResearchDifficulty ParseDifficulty(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "easy" => ResearchDifficulty.Easy,
        "hard" => ResearchDifficulty.Hard,
        _ => ResearchDifficulty.Medium,
    };

    /// <summary>„Spanisch (Mexiko)“ für den Prompt (englisch).</summary>
    internal static string Place(string language)
    {
        try
        {
            var c = CultureInfo.GetCultureInfo(language);
            return language.Contains('-') ? $"{c.Parent.EnglishName} as used in {new RegionInfo(language.Split('-')[1]).EnglishName} ({language})" : $"{c.EnglishName} ({language})";
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentException) { return language; }
    }

    /// <summary>Wörtliches Vorkommen, unabhängig von Groß-/Kleinschreibung und Akzenten.</summary>
    public static bool Contains(string text, string expression) => UsageCheckAgent.ContainsPhrase(text, expression);

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "eines",
        "the", "a", "an", "le", "la", "les", "l", "un", "une", "du", "el", "los", "las", "una", "unos", "unas",
        "il", "lo", "gli", "uno", "het", "een", "o", "os", "as", "um", "uma",
    };

    /// <summary>
    /// Derselbe Ausdruck, auch wenn die KI ihn diesmal etwas anders erfasst hat: ohne Groß-/Kleinschreibung, Akzente,
    /// Satzzeichen und Artikel am Anfang („der Spaltring“ = „Spaltring“), und gebeugt („Spaltringe“, „uszczelnienia
    /// mechanicznego“). Dieselben Wörter in derselben Reihenfolge; Beugung erst ab sechs Buchstaben und mit höchstens zwei
    /// (ab acht Buchstaben drei) Buchstaben Unterschied am Ende – „Druck“ und „Drucker“ bleiben verschieden.
    /// </summary>
    public static bool SameExpression(string a, string b)
    {
        if (a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        static List<string> Core(string s)
        {
            var w = RecommendationUse.Words(s);
            while (w.Count > 1 && Articles.Contains(w[0])) w.RemoveAt(0);
            return w;
        }
        var x = Core(a);
        var y = Core(b);
        if (x.Count == 0 || x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
        {
            if (x[i] == y[i]) continue;
            var min = Math.Min(x[i].Length, y[i].Length);
            var p = 0;
            while (p < min && x[i][p] == y[i][p]) p++;
            if (min < 6 || p < min - 2 || Math.Max(x[i].Length, y[i].Length) - p > (min >= 8 ? 3 : 2)) return false;
        }
        return true;
    }

    /// <summary>
    /// Warum ein Ausdruck kein Rechercheziel ist, oder null. Eine geschützte Angabe (Name nach Anrede, Anschrift, E-Mail,
    /// Telefon, IBAN, Aktenzeichen, Schutzliste) steckt darin oder er ist Teil einer – auch wenn der Datenschutz für das
    /// Projekt nicht eingeschaltet ist, denn so etwas gehört nicht ins Netz. Oder er ist nur ein Datum bzw. eine Zahl: das
    /// überträgt sich nach festen Regeln, eine Recherche kostet nur Zeit.
    /// </summary>
    public static string? SkipReason(string expression, IEnumerable<Segment> segments)
    {
        var e = expression.Trim();
        if (IsJustDateOrNumber(e)) return "nur ein Datum oder eine Zahl";
        var settings = Privacy.PrivacyGuard.ForWeb(Privacy.PrivacyContext.Current);
        // was unter „Nicht schützen“ steht (auch als Teil eines längeren Eintrags), bleibt frei
        if (Privacy.SensitiveData.Find(e, settings).Any(f => f.Kind == Privacy.SensitiveKind.Term || !Privacy.PrivacyGuard.IsFreed(f.Value, settings)))
            return "geschützte Angabe";
        foreach (var f in segments.SelectMany(s => Privacy.SensitiveData.Find(TagText.StripTags(s.Source), settings)))
            if (Contains(e, f.Value) || e.Length >= 4 && Contains(f.Value, e)) return "geschützte Angabe";
        return null;
    }

    /// <summary>Ein Personenname, eine Anschrift oder ein anderer geschützter Wert steckt im Ausdruck (siehe <see cref="SkipReason"/>).</summary>
    public static bool IsProtected(string expression, IEnumerable<Segment> segments) => SkipReason(expression, segments) == "geschützte Angabe";

    [GeneratedRegex(@"\b(?:" + Privacy.SensitiveData.Months + @"|styczeń|luty|marzec|kwiecień|maj|czerwiec|lipiec|sierpień|wrzesień|październik|listopad|grudzień|" +
                    @"Jan|Feb|Mär|Apr|Jun|Jul|Aug|Sep|Sept|Okt|Oct|Nov|Dez|Dec|am|vom|von|bis|zum|ab|seit|ca|heute|on|from|to|since|until|dnia|od|do|r)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex DateWordRegex();

    /// <summary>„27. April 2016“, „03/2019 – 05/2021“, „seit 2019“, „12.03.2020“, „2016“: nur Datum oder Zahl, kein Wort.</summary>
    public static bool IsJustDateOrNumber(string expression) =>
        expression.Any(char.IsDigit) && !DateWordRegex().Replace(expression, " ").Any(char.IsLetter);
}

/// <summary>Dossiers eines Projekts (Datei „recherche.json“ im Projektordner).</summary>
public static class DossierStore
{
    private static string PathOf(TranslationProject p) => System.IO.Path.Combine(p.Folder, "recherche.json");

    public static List<TermDossier> Load(TranslationProject p)
    {
        try { return File.Exists(PathOf(p)) ? JsonSerializer.Deserialize<List<TermDossier>>(File.ReadAllText(PathOf(p))) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Neues Dossier speichern; ein älteres zum selben Ausdruck und Sprachpaar wird ersetzt.</summary>
    private static bool SameKey(TermDossier a, TermDossier b) =>
        a.Expression.Equals(b.Expression, StringComparison.OrdinalIgnoreCase) && a.SourceLanguage == b.SourceLanguage && a.TargetLanguage == b.TargetLanguage;

    /// <summary>Dossier speichern; ein vorhandenes zum selben Ausdruck wird ersetzt und bleibt als <see cref="TermDossier.Previous"/> erhalten.</summary>
    public static void Add(TranslationProject p, TermDossier d)
    {
        var all = Load(p);
        if (all.FirstOrDefault(x => SameKey(x, d)) is { } old && old.Created != d.Created)
        {
            old.Previous = null;                                                 // nur eine Stufe aufheben
            d.Previous = old;
        }
        all.RemoveAll(x => SameKey(x, d));
        all.Add(d);
        Save(p, all);
        // auch ins Recherche-Gedächtnis (alle Projekte) – ohne Satzzusammenhang; aus vertraulichen Projekten nur, wenn eingeschaltet
        if (ResearchMemory.MayRemember(p))
            try { ResearchMemory.Remember(d, p.Name, p.Topic); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Frühere Fassung wiederherstellen; die aktuelle wird dafür zur früheren (hin und zurück möglich).</summary>
    public static TermDossier? RestorePrevious(TranslationProject p, TermDossier d)
    {
        var all = Load(p);
        var current = all.FirstOrDefault(x => SameKey(x, d));
        if (current?.Previous is not { } previous) return null;
        current.Previous = null;
        previous.Previous = current;
        all.Remove(current);
        all.Add(previous);
        Save(p, all);
        return previous;
    }

    private static void Save(TranslationProject p, List<TermDossier> all) =>
        File.WriteAllText(PathOf(p), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

    public static void Remove(TranslationProject p, TermDossier d)
    {
        var all = Load(p);
        all.RemoveAll(x => SameKey(x, d));
        Save(p, all);
    }

    /// <summary>Empfehlung und Varianten als Begriff in die Termbank (Varianten ohne Entsprechung als „nicht verwenden“).</summary>
    public static long ToTermbase(TermDossier d, Resources.ITermbase tb, string? topic)
    {
        if (string.IsNullOrWhiteSpace(d.Recommended)) throw new InvalidOperationException("Das Dossier hat keine Empfehlung.");
        var terms = new List<(string, string, TermStatus)> { (d.SourceLanguage, d.Expression, TermStatus.Preferred), (d.TargetLanguage, d.Recommended, TermStatus.Preferred) };
        foreach (var c in d.Candidates.Where(c => !c.Term.Equals(d.Recommended, StringComparison.OrdinalIgnoreCase)))
            terms.Add((d.TargetLanguage, c.Term, c.Equivalence == Equivalence.None ? TermStatus.Forbidden : TermStatus.Admitted));
        var definition = d.Meaning + (d.Strategy is { Length: > 0 } s ? " | Empfehlung: " + s : "");
        var source = string.Join(" ", d.Sources.Select(x => x.Url).Take(3));
        return tb.AddConcept(terms, definition, topic, source.Length > 0 ? source : null);
    }

    /// <summary>Dossier als Wissenseintrag (Bedeutung, Hintergrund, Empfehlung) für das Team.</summary>
    public static long ToKnowledge(TermDossier d, Resources.IKnowledgeBase kb, string? topic, string? author)
    {
        var first = d.Sources.FirstOrDefault(s => Languages.Matches(s.Language, d.TargetLanguage)) ?? d.Sources.FirstOrDefault();
        return kb.Add(new Knowledge.KnowledgeEntry
        {
            Language = d.TargetLanguage,
            Topic = topic ?? "",
            Kind = Knowledge.KnowledgeKind.Usage,
            Title = $"{d.Expression} ({d.SourceLanguage}) → {d.Recommended} ({d.TargetLanguage})",
            Content = string.Join(" ", new[] { d.Meaning, d.Strategy is { Length: > 0 } s ? s + ":" : null, d.Reasoning }.Where(x => !string.IsNullOrWhiteSpace(x))) +
                      (d.Sources.Count == 0 ? "\nOhne Beleg (aus dem Wissen des Modells)." :
                          "\nBelege: " + string.Join(" ", d.Sources.Take(4).Select(q => $"[{q.Number}] {q.Title} ({q.Url})" + (q.Quote is { Length: > 0 } quote ? $" „{quote}“" : "")))),
            SourceUrl = first?.Url,
            SourceTitle = first?.Title,
            Author = author is null ? "Recherche zum Dokument" : $"Recherche zum Dokument ({author})",
            Confidence = d.Sources.Count > 0 ? 0.8 : 0.5,
        });
    }

    /// <summary>
    /// Die Empfehlung steht wörtlich in mindestens einer Quelle der Zielsprache (Zitat oder Titel). Nur solche Dossiers
    /// gehen von selbst in die Übersetzung: Im Test mit 16 Sätzen machten unbelegte Empfehlungen kleiner Modelle
    /// („sliding ring seal“, „moje piwo“) die Übersetzungen schlechter, mit dieser Prüfung blieb der Schaden aus.
    /// </summary>
    /// <summary>
    /// Gütestufe eines Dossiers: A – eindeutig belegt, mit einer starken Quelle (Termbank, Gesetz, Norm, Behörde,
    /// Fachverband, Hersteller, Hochschule, Wörterbuch, eigene Fachtexte); B – eindeutig belegt aus gewöhnlichen Quellen;
    /// C – belegt, aber nicht eindeutig (eine Quelle, oder nur schwache wie Forum und Blog); D – nicht belegt.
    /// Ältere Dossiers ohne Angabe der Quellenart werden an der Adresse eingeordnet.
    /// </summary>
    public static string Grade(TermDossier d)
    {
        if (!IsEvidenced(d)) return "D";
        var recommended = Normalize(d.Recommended!);
        var supporting = d.Sources.Where(s => Languages.Matches(s.Language, d.TargetLanguage)
                                              && Normalize((s.Quote ?? "") + " " + s.Title).Contains(recommended, StringComparison.Ordinal)).ToList();
        string KindOf(SourceRef s) => s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Kind ?? SourceKinds.Classify(s.Url) : SourceKinds.Own;
        var support = SourceKinds.Support(supporting.Select(s => (s.Url, (string?)KindOf(s), !s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))), d.Expression);
        if (!SourceKinds.IsConclusive(support)) return "C";
        return supporting.Any(s => SourceKinds.Tier(KindOf(s)) <= 2 || SourceKinds.IsOwnSiteOf(s.Url, d.Expression)) ? "A" : "B";
    }

    /// <summary>„Güte A – eindeutig belegt, starke Quelle“ usw. (Programmtext, wird übersetzt angezeigt).</summary>
    public static string GradeText(TermDossier d) => Grade(d) switch
    {
        "A" => "Güte A – eindeutig belegt, mit starker Quelle",
        "B" => "Güte B – eindeutig belegt",
        "C" => "Güte C – belegt, aber nicht eindeutig",
        _ => "Güte D – nicht belegt",
    };

    public static bool IsEvidenced(TermDossier d)
    {
        var recommended = Normalize(d.Recommended ?? "");
        return recommended.Length > 0 && d.Sources.Any(s => Languages.Matches(s.Language, d.TargetLanguage)
            && Normalize((s.Quote ?? "") + " " + s.Title).Contains(recommended, StringComparison.Ordinal));
    }

    private static string Normalize(string text) =>
        " " + System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim() + " ";

    /// <summary>Dossiers, die die Übersetzung eines Textes mitbekommt: Ausdruck kommt vor, Zielsprache passt, Empfehlung belegt.</summary>
    public static List<TermDossier> ForTranslation(IEnumerable<TermDossier> dossiers, string text, string targetLanguage) =>
        ForText(dossiers, text, targetLanguage).Where(IsEvidenced).ToList();

    /// <summary>Dossiers zu einem Text (Wort, Markierung, Satz, Segment): alle, deren Ausdruck darin vorkommt.</summary>
    public static List<TermDossier> ForText(IEnumerable<TermDossier> dossiers, string text, string? targetLanguage = null)
    {
        var plain = TagText.StripTags(text);
        return dossiers.Where(d => (targetLanguage == null || Languages.Matches(d.TargetLanguage, targetLanguage))
                                   && (DocumentResearch.Contains(plain, d.Expression) || DocumentResearch.Contains(d.Expression, plain.Trim())))
            .OrderByDescending(d => d.Difficulty).ToList();
    }
}
