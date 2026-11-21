using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Resources;

/// <summary>Woher ein Begriff kommt: Definitionssatz, Glossarliste, Abkürzung im Text oder von der KI gefunden.</summary>
public enum DocumentTermKind { Definition, Glossary, Abbreviation, Ai }

/// <summary>
/// Ein Begriff aus einem eigenen Dokument: Benennung, Definition (immer der wörtliche Satz aus dem Dokument),
/// Abkürzung, Fundstelle (Datei, Seite) und Sprache.
/// </summary>
public sealed record DocumentTerm(string Term, string Definition, string? Abbreviation, string Path, string Title, int? Page, string Language,
    DocumentTermKind Kind)
{
    /// <summary>Wie oft die Benennung in allen eigenen Fachtexten vorkommt.</summary>
    public int Count { get; init; }

    public string? Version { get; init; }

    public string Uri => new Uri(Path).AbsoluteUri + (Page is { } p ? "#page=" + p : "");

    public string Label => Page is { } p ? $"{Title}, S. {p}" : Title;
}

public sealed record DocumentStudyResult(int TermsSaved, int KnowledgeSaved, int OtherLanguage);

/// <summary>
/// Einarbeiten in eigene Dokumente (PDF, Word, Writer … aus den eigenen Fachtexten): Begriffe mit ihrer Definition
/// herausziehen – nach festen Regeln („Unter X versteht man …“, „X ist ein …“, „X means …“, „X to …“, Abkürzungen
/// wie „Gleitringdichtung (GLRD)“, Glossarlisten „Begriff: Erklärung“) und, wenn erlaubt, mit der KI. Als Definition
/// gilt immer der Satz aus dem Dokument; was die KI nennt, wird nur übernommen, wenn ihr Belegsatz wörtlich im
/// Abschnitt steht und den Begriff enthält. Gespeichert wird erst nach Auswahl (<see cref="Save"/>).
/// </summary>
public static partial class DocumentStudy
{
    public const int PassagesPerRequest = 6;
    public const int MaxAiPassagesPerFile = 36;

    // ---------- Regeln ----------

    private static readonly HashSet<string> NotTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        // de
        "Dies", "Das", "Es", "Er", "Sie", "Hier", "Dieser", "Diese", "Dieses", "Jeder", "Jede", "Jedes", "Man", "Was", "Wer", "Ziel",
        "Zweck", "Ergebnis", "Folge", "Grund", "Vorteil", "Nachteil", "Voraussetzung", "Aufgabe", "Beispiel", "Hinweis", "Achtung",
        "Warnung", "Vorsicht", "Gefahr", "Seite", "Datum", "Stand", "Version", "Quelle", "Autor", "Titel", "Ort", "Adresse", "Telefon",
        "Tel", "Fax", "E-Mail", "Email", "Inhalt", "Anhang", "Abbildung", "Tabelle", "Kapitel", "Bild", "Folgendes", "Wichtig",
        // en
        "It", "This", "That", "There", "These", "Those", "What", "Which", "Who", "One", "Each", "Every", "He", "She", "They", "Its",
        "Note", "Warning", "Caution", "Danger", "Example", "Page", "Date", "Source", "Author", "Title", "Address", "Phone", "Figure",
        "Table", "Chapter", "Result", "Aim", "Purpose", "Reason", "Advantage", "Disadvantage", "Important",
        // pl
        "To", "Ten", "Ta", "Co", "Czy", "Tak", "Nie", "Jest", "Dlatego", "Oto", "Uwaga", "Przykład", "Strona", "Na", "W", "Z", "Do",
        "Od", "Po", "Przy", "Dla", "Za", "O", "U", "Wynik", "Cel",
    };

    private const string Term = @"[„""»“]?(?<term>\p{L}[\p{L}\-]*(?:\s[\p{L}\-]+){0,3}?)[“""«”]?";

    [GeneratedRegex(@"^Unter\s+(?:(?:einer|einem|einen|der|dem|den|die|das)\s+)?(?:(?:Begriff|Bezeichnung|Ausdruck)\s+)?" + Term + @"\s+(?:versteht|verstehen)\s+(?:man|wir)\b")]
    private static partial Regex GermanUnder();

    [GeneratedRegex(@"^Als\s+" + Term + @"\s+(?:bezeichnet|definiert)\s+man\b|^Als\s+" + Term + @"\s+(?:wird|werden)\s.{5,300}\s(?:bezeichnet|definiert)\.?$")]
    private static partial Regex GermanAs();

    [GeneratedRegex(@"^(?:Der\s+(?:Begriff|Ausdruck)\s+|Die\s+Bezeichnung\s+)?" + Term + @"\s+(?:bezeichnet|meint)\s+(?:die|den|das|eine|einen|ein|alle)\b")]
    private static partial Regex GermanDenotes();

    [GeneratedRegex(@"^(?:(?:Der|Die|Das|Ein|Eine)\s+)?(?<term>\p{Lu}[\p{L}\-]+(?:\s\p{Lu}[\p{L}\-]+)?)\s+(?:ist\s+(?:ein|eine|einer)\s+\p{L}|sind\s+(?:\p{Ll}+e\s+)?\p{Lu}\p{L}+(?:,|\s+(?:die|zur|zum|für|mit|aus|zu|der|des)\b))")]
    private static partial Regex GermanIs();

    [GeneratedRegex(@"^(?:(?:A|An|The)\s+)?(?<term>\p{L}[\p{L}\-]*(?:\s[\p{L}\-]+){0,3}?)\s+is\s+(?:a|an)\s+\p{L}")]
    private static partial Regex EnglishIs();

    [GeneratedRegex(@"^(?:(?:The\s+)?(?:term|expression)\s+)?" + Term + @"\s+(?:means|refers to|is defined as|denotes|stands for)\s+\p{L}")]
    private static partial Regex EnglishMeans();

    [GeneratedRegex(@"^" + Term + @"\s+(?:to|jest to|oznacza)\s+\p{L}")]
    private static partial Regex PolishIs();

    // „Gleitringdichtung (GLRD)“, „Europäische Union (EU)“
    [GeneratedRegex(@"(?<long>(?:[\p{L}][\p{L}\-]*\s+){0,6}[\p{L}][\p{L}\-]*)\s+\((?<abbr>\p{Lu}[\p{Lu}\p{N}\-]{1,7})\)")]
    private static partial Regex Abbreviation();

    // Glossarzeile „Gleitringdichtung: Dichtung, bei der …“ (auch nummeriert, auch mit Gedankenstrich)
    [GeneratedRegex(@"^\s*(?:\d+(?:\.\d+)*\.?\s+)?(?<term>\p{L}[\p{L}\-/ ]{1,58}\p{L})\s*[:–—]\s+(?<def>\p{L}.{14,400})$")]
    private static partial Regex GlossaryLine();

    // Normstil „3.1 Gleitringdichtung“ und die Erklärung in der nächsten Zeile
    [GeneratedRegex(@"^\s*\d+(?:\.\d+)+\.?\s+(?<term>\p{L}[\p{L}\-/ ]{1,58}\p{L})\s*$")]
    private static partial Regex NumberedTerm();

    [GeneratedRegex(@"^\s*(?:\d+(?:\.\d+)*\.?\s+)?(?:Begriffe(?:\s+und\s+Definitionen)?|Definitionen|Begriffsbestimmungen|Glossar|Terminologie|Terms\s+and\s+definitions|Definitions|Glossary|Terminology|Słownik(?:\s+pojęć)?|Definicje|Terminy\s+i\s+definicje)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GlossaryHeading();

    private static bool IsTerm(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return term.Length >= 3 && words.Length <= 6 && !NotTerms.Contains(words[0]) && term.Any(char.IsLetter) && !term.All(char.IsUpper);
    }

    /// <summary>Begriffe nach festen Regeln aus den Abschnitten einer Datei (ohne KI).</summary>
    public static List<DocumentTerm> ExtractRules(IReadOnlyList<(int? Page, string Text)> passages, LibraryFile file)
    {
        var language = file.Language ?? "de-DE";
        var primary = Languages.Primary(language);
        var segmenter = new Segmenter(language);
        var result = new List<DocumentTerm>();
        DocumentTerm Make(string term, string definition, int? page, DocumentTermKind kind, string? abbreviation = null) =>
            new(term.Trim(), definition.Trim(), abbreviation, file.Path, file.Title, page, language, kind) { Version = file.Version };
        var quiet = 0;
        var inGlossary = false;
        foreach (var (page, text) in passages)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            // Glossarteil: nach einer Überschrift „Begriffe“, „Glossary“ … oder bei mindestens drei Zeilen „Begriff: Erklärung“
            if (lines.Any(l => l.Length < 60 && GlossaryHeading().IsMatch(l))) { inGlossary = true; quiet = 0; }
            var glossaryLines = lines.Select(l => GlossaryLine().Match(l)).Where(m => m.Success && IsTerm(m.Groups["term"].Value.Trim())).ToList();
            var found = 0;
            if (inGlossary || glossaryLines.Count >= 3)
            {
                foreach (var m in glossaryLines)
                {
                    result.Add(Make(m.Groups["term"].Value, m.Value.Trim(), page, DocumentTermKind.Glossary));
                    found++;
                }
                if (inGlossary)
                    for (var i = 0; i + 1 < lines.Count; i++)
                        if (NumberedTerm().Match(lines[i]) is { Success: true } n && IsTerm(n.Groups["term"].Value.Trim())
                            && lines[i + 1].Length >= 15 && char.IsLetter(lines[i + 1][0]) && !NumberedTerm().IsMatch(lines[i + 1]))
                        {
                            result.Add(Make(n.Groups["term"].Value, $"{n.Groups["term"].Value.Trim()}: {lines[i + 1]}", page, DocumentTermKind.Glossary));
                            found++;
                        }
                quiet = found > 0 ? 0 : quiet + 1;
                if (quiet >= 3) inGlossary = false;
            }

            foreach (var line in lines)
                foreach (var sentence in segmenter.Split(line).Pieces.Select(p => p.Text.Trim()).Where(s => s.Length >= 20 && s.Length <= 500))
                {
                    var match = primary switch
                    {
                        "de" => new[] { GermanUnder(), GermanAs(), GermanDenotes(), GermanIs() }.Select(r => r.Match(sentence)).FirstOrDefault(m => m.Success),
                        "en" => new[] { EnglishMeans(), EnglishIs() }.Select(r => r.Match(sentence)).FirstOrDefault(m => m.Success),
                        "pl" => PolishIs().Match(sentence) is { Success: true } p ? p : null,
                        _ => null,
                    };
                    if (match != null)
                    {
                        var term = match.Groups["term"].Captures.Select(c => c.Value).FirstOrDefault(v => v.Length > 0) ?? "";
                        if (IsTerm(term)) result.Add(Make(term, sentence, page, DocumentTermKind.Definition));
                    }
                    foreach (Match a in Abbreviation().Matches(sentence))
                        if (LongForm(a.Groups["long"].Value, a.Groups["abbr"].Value) is { } longForm && IsTerm(longForm))
                            result.Add(Make(longForm, sentence, page, DocumentTermKind.Abbreviation, a.Groups["abbr"].Value));
                }
        }
        var texts = passages.Select(p => p.Text).ToList();
        return result.Select(t => t with { Term = TermSuggester.NormalizeCase(t.Term, texts, language) }).ToList();
    }

    /// <summary>
    /// Die Langform zur Abkürzung: so wenige Wörter vor der Klammer wie nötig, beginnend mit dem ersten Buchstaben der
    /// Abkürzung, die übrigen Buchstaben in Reihenfolge enthalten („Gleitringdichtung“ → GLRD, „Europäische Union“ → EU).
    /// </summary>
    internal static string? LongForm(string text, string abbreviation)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = abbreviation.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray();
        if (letters.Length < 2) return null;
        for (var n = 1; n <= Math.Min(7, words.Length); n++)
        {
            var candidate = string.Join(' ', words[^n..]);
            if (char.ToLowerInvariant(candidate[0]) != letters[0] || !candidate.Any(char.IsLower)) continue;
            if (!InOrder(letters, candidate.ToLowerInvariant())) continue;
            // mehrere Wörter: ihre Anfangsbuchstaben (ohne Füllwörter) gehören zur Abkürzung – „Pumpe arbeitet mit Druck (PN)“ nicht
            if (n > 1)
            {
                var initials = words[^n..].Where(w => !FunctionWords.Contains(w)).Select(w => char.ToLowerInvariant(w[0])).ToArray();
                if (initials.Length < 2 || !InOrder(initials, new string(letters))) continue;
            }
            return candidate;
        }
        return null;
    }

    private static bool InOrder(IEnumerable<char> letters, string text)
    {
        var at = 0;
        foreach (var c in letters)
        {
            var i = text.IndexOf(c, at);
            if (i < 0) return false;
            at = i + 1;
        }
        return true;
    }

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
        { "für", "in", "im", "der", "die", "das", "des", "dem", "den", "von", "vom", "und", "zur", "zum", "of", "the", "and", "for", "on", "i", "w", "z", "na", "dla", "do", "oraz", "de", "du", "la", "le", "et" };

    // ---------- KI ----------

    [GeneratedRegex(@"versteht man|bezeichnet|Bezeichnung|definiert|Definition|Begriff|\bist eine?\b|\bsind\b|\bmeans\b|refers to|defined as|\bis an?\b|denotes|oznacza|rozumie się|nazywa|\bto\b|jest to|désigne|on entend|se entiende|si intende", RegexOptions.IgnoreCase)]
    private static partial Regex Cue();

    private static string Flat(string s) =>
        Regex.Replace(s, @"\s+", " ").Replace('„', '"').Replace('“', '"').Replace('”', '"').Replace('»', '"').Replace('«', '"').Replace('’', '\'').Trim();

    /// <summary>
    /// Die KI nennt Begriffe, die die Abschnitte erklären, mit Belegsatz. Übernommen wird nur, was wörtlich im
    /// Abschnitt steht und den Begriff enthält; als Definition dient der Belegsatz selbst.
    /// </summary>
    public static async Task<List<DocumentTerm>> ExtractWithAiAsync(IChatModel model, IReadOnlyList<(int? Page, string Text)> passages, LibraryFile file,
        string? topic = null, CancellationToken ct = default)
    {
        var language = file.Language ?? "de-DE";
        var chosen = passages.Select((p, i) => (p.Page, p.Text, Index: i))
            .OrderByDescending(p => Cue().IsMatch(p.Text)).ThenBy(p => p.Index).Take(MaxAiPassagesPerFile)
            .OrderBy(p => p.Index).ToList();
        var result = new List<DocumentTerm>();
        foreach (var batch in chosen.Chunk(PassagesPerRequest))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            for (var i = 0; i < batch.Length; i++) sb.AppendLine($"[{i + 1}] {batch[i].Text}").AppendLine();
            var reply = await model.AskAsync(
                "You are a terminologist. You build a glossary from a specialist document. Output only JSON.",
                $$"""
                Document: "{{file.Title}}" (language {{language}}){{(string.IsNullOrWhiteSpace(topic) ? "" : ", subject field: " + topic)}}.
                Passages:
                {{sb}}
                List the specialist terms that these passages define or explain (what something is, what it is used for,
                how it differs from something else) – not everyday words, not names of persons, companies or products.
                For each term give "term" in dictionary form (nominative singular; lower case unless the language
                capitalises nouns, as German does), "p" = the passage number, and "quote" = the one sentence from that
                passage that defines or explains it, copied exactly, character for character. Skip terms without such a
                sentence. At most 15.
                Return: {"terms": [{"term": "...", "p": 1, "quote": "..."}]}
                """, ct);
            if (JsonExtract.Find(reply) is not { } json) continue;
            JsonArray? items;
            try { items = JsonNode.Parse(json)?["terms"] as JsonArray; }
            catch (System.Text.Json.JsonException) { continue; }
            foreach (var node in items ?? [])
            {
                var term = ((string?)node?["term"])?.Trim();
                var quote = ((string?)node?["quote"])?.Trim();
                var p = node?["p"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
                if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(quote) || p < 1 || p > batch.Length || term.Length > 80 || quote.Length < 20) continue;
                var passage = Flat(batch[p - 1].Text);
                var at = passage.IndexOf(Flat(quote), StringComparison.Ordinal);
                if (at < 0 || !TermSuggester.ContainsInflected(quote, term) || !IsTerm(term)) continue;   // erfunden oder nicht belegt
                var original = Flat(quote);
                result.Add(new DocumentTerm(term, original, null, file.Path, file.Title, batch[p - 1].Page, language, DocumentTermKind.Ai) { Version = file.Version });
            }
        }
        return result;
    }

    // ---------- Zusammenführen ----------

    /// <summary>
    /// Begriffe aller gewählten Dateien: Regeln und (falls gegeben) KI, je Sprache und Benennung einmal – Definitionssätze
    /// vor Glossar, KI und Abkürzung; eine Abkürzung wird dem Begriff zugeordnet. Mit Häufigkeit in den eigenen Fachtexten.
    /// </summary>
    public static async Task<List<DocumentTerm>> ExtractAsync(ReferenceLibrary library, IReadOnlyList<LibraryFile> files, IChatModel? model,
        string? topic = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var all = new List<DocumentTerm>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var passages = library.Passages(file.Path);
            var rules = ExtractRules(passages, file);
            all.AddRange(rules);
            log?.Report($"{System.IO.Path.GetFileName(file.Path)}: {passages.Count} Abschnitte, {rules.Count} Begriffe nach Regeln");
            if (model == null) continue;
            log?.Report($"KI liest {System.IO.Path.GetFileName(file.Path)} …");
            try
            {
                var ai = await ExtractWithAiAsync(model, passages, file, topic, ct);
                all.AddRange(ai);
                log?.Report($"{System.IO.Path.GetFileName(file.Path)}: {ai.Count} Begriffe von der KI mit wörtlichem Beleg");
            }
            catch (AiException ex) { log?.Report("KI: " + ex.Message); }
        }
        var merged = new List<DocumentTerm>();
        foreach (var group in all.GroupBy(t => (Languages.Primary(t.Language), t.Term.ToLowerInvariant())))
        {
            var best = group.OrderBy(t => t.Kind switch
            {
                DocumentTermKind.Definition => 0, DocumentTermKind.Glossary => 1, DocumentTermKind.Ai => 2, _ => 3,
            }).First();
            var abbreviation = group.Select(t => t.Abbreviation).FirstOrDefault(a => a != null);
            merged.Add(best with { Abbreviation = abbreviation, Count = library.CountPhrase(best.Term, best.Language) });
        }
        return merged.OrderBy(t => t.Kind == DocumentTermKind.Abbreviation).ThenByDescending(t => t.Count)
            .ThenBy(t => t.Term, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- Speichern ----------

    /// <summary>
    /// Die gewählten Begriffe ablegen: in der Terminologie des Sprachpaars (Begriffe in einer der beiden Sprachen, mit
    /// Abkürzung als zugelassener Benennung) und im Wissen (alle Sprachen) – jeweils mit Fundstelle; nichts doppelt.
    /// </summary>
    public static DocumentStudyResult Save(IReadOnlyList<DocumentTerm> terms, string sourceLanguage, string targetLanguage, ITermbase? termbase,
        IKnowledgeBase? knowledge)
    {
        var concepts = new List<(IReadOnlyList<(string Lang, string Text, TermStatus Status)> Terms, string? Definition, string? Domain, string? Source)>();
        var entries = new List<KnowledgeEntry>();
        var otherLanguage = 0;
        foreach (var t in terms)
        {
            var lang = Languages.Matches(t.Language, sourceLanguage) ? sourceLanguage : Languages.Matches(t.Language, targetLanguage) ? targetLanguage : null;
            if (lang == null) otherLanguage++;
            else if (termbase != null && !termbase.Lookup(lang, t.Term, 5).Any(c => c.Terms.Any(x => x.Text.Equals(t.Term, StringComparison.OrdinalIgnoreCase)))
                     && !concepts.Any(c => c.Terms.Any(x => x.Lang == lang && x.Text.Equals(t.Term, StringComparison.OrdinalIgnoreCase))))
            {
                var names = new List<(string, string, TermStatus)> { (lang, t.Term, TermStatus.Preferred) };
                if (t.Abbreviation != null) names.Add((lang, t.Abbreviation, TermStatus.Admitted));
                concepts.Add((names, $"{t.Definition} ({t.Label})", null, t.Uri));
            }
            if (knowledge != null && !knowledge.Search(t.Language, t.Term, 10).Any(e => e.Title.Equals(t.Term, StringComparison.OrdinalIgnoreCase) && e.SourceUrl == t.Uri)
                && !entries.Any(e => e.Title.Equals(t.Term, StringComparison.OrdinalIgnoreCase) && e.Language == t.Language))
                entries.Add(new KnowledgeEntry
                {
                    Language = lang ?? t.Language,
                    Topic = t.Title,
                    Kind = KnowledgeKind.Definition,
                    Title = t.Term,
                    Content = t.Definition + (t.Abbreviation != null ? $" (Abkürzung: {t.Abbreviation})" : ""),
                    Quote = t.Definition,
                    SourceUrl = t.Uri,
                    SourceTitle = t.Label + (t.Version != null ? $" ({t.Version})" : ""),
                    Author = "Einarbeiten",
                    Confidence = t.Kind == DocumentTermKind.Ai ? 0.8 : 0.9,
                });
        }
        var termsSaved = concepts.Count == 0 ? 0 : termbase is Termbase tb ? tb.AddConcepts(concepts) : concepts.Count(c => termbase!.AddConcept(c.Terms, c.Definition, c.Domain, c.Source) > 0);
        var knowledgeSaved = entries.Count == 0 ? 0 : knowledge is KnowledgeBase kb ? kb.AddMany(entries) : entries.Count(e => knowledge!.Add(e) > 0);
        return new DocumentStudyResult(termsSaved, knowledgeSaved, otherLanguage);
    }
}
