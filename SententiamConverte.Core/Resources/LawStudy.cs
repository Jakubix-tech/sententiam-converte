using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Resources;

/// <summary>Ein Paragraph eines Gesetzes.</summary>
public sealed record LawSection(string Number, string Title, string Text, string Url);

/// <summary>Ein herausgezogener Begriff: Benennung, Definition (wörtlich aus dem Gesetz), Fundstelle, englische Benennung.</summary>
public sealed record StudiedTerm(string Term, string Definition, string Section, string Url, string? Target, bool LegalDefinition);

public sealed record LawStudyResult(string Abbreviation, string Title, string? Version, int Sections, List<StudiedTerm> Terms,
    int TermsSaved, int KnowledgeSaved, int TargetTerms);

/// <summary>
/// Einarbeiten in ein deutsches Bundesgesetz (z. B. „StGB“): die amtliche Fassung von gesetze-im-internet.de laden
/// (amtliches Werk, urheberrechtlich frei, robots.txt erlaubt es), Paragraph für Paragraph in die eigenen Fachtexte
/// aufnehmen und die Begriffe herausziehen – Überschriften mit dem ersten Satz als Definition und Legaldefinitionen
/// („Notwehr ist die Verteidigung, die …“) –, mit Fundstelle in Terminologie und Wissen. Bei Englisch als Zielsprache
/// kommen die Benennungen aus den Überschriften der englischen Übersetzung dazu (nicht amtlich und urheberrechtlich
/// geschützt: übernommen werden nur einzelne Benennungen, keine Sätze).
/// </summary>
public static partial class LawStudy
{
    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();

    private static string Base => LawSources.GiiBase;

    /// <summary>Amtliche Fassung laden: amtliche Abkürzung („StGB“), Titel, Stand und alle Paragraphen.</summary>
    public static async Task<(string Abbreviation, string Title, string? Version, DateTime? Date, List<LawSection> Sections)> LoadAsync(string abbreviation, CancellationToken ct = default)
    {
        var abbr = await FolderAsync(abbreviation, ct);
        var url = $"{Base}/{abbr}/xml.zip";
        if (await PageFetcher.PermitAsync(new Uri(url), ct) is { } refused) throw new AiException($"{abbreviation}: {refused}");
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new AiException($"Ein Gesetz „{abbreviation}“ gibt es auf gesetze-im-internet.de nicht (Abkürzung prüfen, z. B. StGB, BGB, ZPO).");
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)));
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new AiException("Die amtliche Fassung enthält keine XML-Datei.");
        using var stream = entry.Open();
        return Parse(XDocument.Load(stream), abbr);
    }

    internal static (string Abbreviation, string Title, string? Version, DateTime? Date, List<LawSection> Sections) Parse(XDocument xml, string abbr)
    {
        var norms = xml.Descendants("norm").ToList();
        var head = norms.FirstOrDefault()?.Element("metadaten");
        var official = Clean(head?.Element("jurabk")?.Value ?? "") is { Length: > 0 } j ? j : abbr.ToUpperInvariant();
        var title = Clean(head?.Element("langue")?.Value ?? head?.Element("kurzue")?.Value ?? abbr.ToUpperInvariant());
        var version = string.Join("; ", head?.Elements("standangabe").Select(s => Clean(s.Element("standkommentar")?.Value ?? "").TrimEnd(';', ' ')).Where(s => s.Length > 0) ?? []);
        DateTime? date = DateTime.TryParseExact(norms.FirstOrDefault()?.Attribute("builddate")?.Value?[..Math.Min(8, norms.FirstOrDefault()?.Attribute("builddate")?.Value?.Length ?? 0)],
            "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var sections = new List<LawSection>();
        foreach (var n in norms)
        {
            var number = Clean(n.Element("metadaten")?.Element("enbez")?.Value ?? "");
            if (!number.StartsWith('§') && !number.StartsWith("Art", StringComparison.Ordinal)) continue;
            var text = Clean(string.Join(" ", n.Descendants("Content").SelectMany(c => c.DescendantNodes().OfType<XText>()).Select(t => t.Value)));
            var heading = Clean(n.Element("metadaten")?.Element("titel")?.Value ?? "");
            if (text.Length == 0 && heading.Length == 0) continue;
            sections.Add(new LawSection(number, heading, text, SectionUrl(abbr, number)));
        }
        return (official, title, version.Length > 0 ? version : null, date, sections);
    }

    private static string SectionUrl(string abbr, string number) =>
        SectionNumber().Match(number) is { Success: true } m
            ? $"{Base}/{abbr}/{(number.StartsWith('§') ? "__" : "art_")}{m.Value.ToLowerInvariant()}.html"
            : $"{Base}/{abbr}/";

    [GeneratedRegex(@"\d+[a-zA-Z]?")]
    private static partial Regex SectionNumber();

    /// <summary>Ordner auf gesetze-im-internet.de: „StGB“ → „stgb“, „StVO“ → „stvo_2013“, „SGB X“ → „sgb_10“.</summary>
    private static async Task<string> FolderAsync(string abbreviation, CancellationToken ct) =>
        await LawSources.GermanFolderAsync(abbreviation, ct) ?? Slug(abbreviation);

    private static string Slug(string abbreviation) => Regex.Replace(abbreviation.Trim().ToLowerInvariant(), @"[^a-z0-9äöüß_]", "");

    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    /// <summary>Überschriften der englischen Übersetzung je Paragraph („§ 242“ → „Theft“); leer, wenn es keine gibt.</summary>
    public static async Task<Dictionary<string, string>> EnglishHeadingsAsync(string abbreviation, CancellationToken ct = default)
    {
        var abbr = await FolderAsync(abbreviation, ct);
        var url = $"{Base}/englisch_{abbr}/englisch_{abbr}.html";
        if (await PageFetcher.PermitAsync(new Uri(url), ct) is not null) return [];
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return [];
        if (response.Headers.TryGetValues("tdm-reservation", out var tdm) && tdm.Any(v => v.Trim() == "1")) return [];
        return ParseEnglishHeadings(await response.Content.ReadAsStringAsync(ct));
    }

    internal static Dictionary<string, string> ParseEnglishHeadings(string html)
    {
        var result = new Dictionary<string, string>();
        // Volltext: „Section 242<br />Theft“; Inhaltsverzeichnis: Verweis „Section 242 Theft“
        var headings = EnglishHeadingText().Matches(html);
        foreach (var m in headings.Concat(EnglishHeading().Matches(html)))
        {
            var title = Clean(WebUtility.HtmlDecode(Regex.Replace(m.Groups["title"].Value, "<[^>]+>", " ")));
            if (title.Length > 0) result.TryAdd("§ " + m.Groups["n"].Value, title);
        }
        // Begriffslisten (§ 11: „9. ‘a consideration’ means …“) – nur die Benennung, nicht der Satz
        for (var i = 0; i < headings.Count; i++)
        {
            var end = i + 1 < headings.Count ? headings[i + 1].Index : html.Length;
            foreach (Match m in EnglishListTerm().Matches(html[headings[i].Index..end]))
            {
                var term = Regex.Replace(Clean(WebUtility.HtmlDecode(m.Groups["term"].Value)), @"^(?:a|an|the)\s+", "", RegexOptions.IgnoreCase);
                if (term.Length > 1) result.TryAdd($"§ {headings[i].Groups["n"].Value} Nr. {m.Groups["n"].Value}", term);
            }
        }
        return result;
    }

    [GeneratedRegex(@">(?<n>\d+[a-z]?)\.(?:&#160;|\s)+(?:&#8216;|‘|&#699;)(?<term>[^&<‘’]{2,80})(?:&#8217;|’|&#700;)[^<.;]{0,40}?\s(?:means|includes|refers)\b")]
    private static partial Regex EnglishListTerm();

    [GeneratedRegex(@"<a[^>]*href=""[^""]*#p\d+""[^>]*>\s*Section\s+(?<n>\d+[a-z]?)\s+(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EnglishHeading();

    [GeneratedRegex(@"Section(?:\s|<[^>]+>)+(?<n>\d+[a-z]?)\s*<br\s*/?>\s*(?<title>(?:[^<]|<(?!/p>)[^>]*>){2,300}?)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishHeadingText();

    // „Notwehr ist die Verteidigung, die …“, „Verbrechen sind rechtswidrige Taten, die …“ (am Satzanfang, auch nach „(2) “)
    [GeneratedRegex(@"(?:^|(?<=[.;:]\s)|(?<=\(\d+[a-z]?\)\s))(?<term>\p{Lu}[\p{L}\-]+(?:\s[\p{Ll}\-]+)?)\s(?:ist|sind)\s(?:die|der|das|ein|eine|jede|jeder|jedes|wer|alle|\p{Ll}+e)\b[^.;]{10,400}[.;]")]
    private static partial Regex LegalDefinition();

    // Begriffslisten wie § 11 StGB: „Im Sinne dieses Gesetzes ist 1. Angehöriger: wer …; 2. Amtsträger: wer …“
    [GeneratedRegex(@"(?<=^|\s)(?<n>\d+[a-z]?)\.\s(?<term>\p{L}[\p{L}\- ]{2,60}?):\s(?<def>.{5,1500}?)(?=;\s\d+[a-z]?\.\s|;?\s\(\d+\)|;?$)")]
    private static partial Regex DefinitionList();

    // Satzanfänge, die wie ein Begriff aussehen, aber keiner sind („Jedoch ist …“, „Ausgenommen ist …“, „Rechtswidrig ist …“)
    private static readonly HashSet<string> NotTerms = new(StringComparer.OrdinalIgnoreCase)
        { "Jedoch", "Dies", "Das", "Es", "Ebenso", "Gleiches", "Entsprechendes", "Dasselbe", "Hiervon", "Dabei", "Ferner", "Insoweit", "Auch", "Nicht" };

    [GeneratedRegex(@"^(?:(?:aus|ab|an|ein|vor|zu|über|unter|mit)?ge\p{Ll}+(?:en|t)|\p{L}+(?:ig|lich|bar|isch|sam|haft|los|end))$", RegexOptions.IgnoreCase)]
    private static partial Regex AdjectiveOrParticiple();

    private static bool IsTerm(string term) =>
        term.Length >= 4 && term.Split(' ').Length <= 3 && !NotTerms.Contains(term.Split(' ')[0])
        && !(term.Split(' ').Length == 1 && AdjectiveOrParticiple().IsMatch(term));

    /// <summary>Begriffe aus Überschriften (erster Satz als Definition) und Legaldefinitionen; englische Benennung, wenn bekannt.</summary>
    public static List<StudiedTerm> ExtractTerms(IReadOnlyList<LawSection> sections, IReadOnlyDictionary<string, string>? english = null)
    {
        var result = new Dictionary<string, StudiedTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
        {
            // Legaldefinitionen zuerst: sie sind genauer als die Überschrift
            foreach (Match m in LegalDefinition().Matches(s.Text))
            {
                var term = m.Groups["term"].Value.Trim();
                if (!IsTerm(term)) continue;
                result.TryAdd(term, new StudiedTerm(term, m.Value.Trim(), s.Number, s.Url, null, true));
            }
            if (s.Text.Contains("im Sinne dieses Gesetzes", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in DefinitionList().Matches(s.Text))
                {
                    var term = m.Groups["term"].Value.Trim();
                    if (term.Split(' ').Length > 6) continue;
                    var target = english?.GetValueOrDefault($"{s.Number} Nr. {m.Groups["n"].Value}");
                    result.TryAdd(term, new StudiedTerm(term, $"{term}: {m.Groups["def"].Value.Trim()}", s.Number, s.Url, target, true));
                }
            if (s.Title.Length == 0 || s.Title.Contains("weggefallen", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = s.Title.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var targets = english?.GetValueOrDefault(s.Number) is { } en ? en.Split(';').Select(p => p.Trim()).ToList() : null;
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].Split(' ').Length > 6) continue;
                var target = targets == null ? null : targets.Count == parts.Count ? targets[i] : parts.Count == 1 ? targets[0] : null;
                var definition = FirstSentence(s.Text);
                if (result.TryGetValue(parts[i], out var existing))
                {
                    if (existing.Target == null && target != null) result[parts[i]] = existing with { Target = target };
                    continue;
                }
                if (definition.Length > 0) result[parts[i]] = new StudiedTerm(parts[i], definition, s.Number, s.Url, target, false);
            }
        }
        return result.Values.ToList();
    }

    /// <summary>Erster Satz ohne Absatznummer, höchstens 400 Zeichen.</summary>
    internal static string FirstSentence(string text)
    {
        var t = Regex.Replace(text, @"^\(\d+[a-z]?\)\s*", "");
        var end = Regex.Match(t, @"(?<=\p{Ll}{2}|\))\.(?=\s|$)");
        var s = end.Success ? t[..(end.Index + 1)] : t;
        return s.Length > 400 ? s[..400].TrimEnd() + " …" : s;
    }

    /// <summary>
    /// Einarbeiten: laden, in die eigenen Fachtexte aufnehmen, Begriffe herausziehen und in Terminologie und Wissen
    /// ablegen (vorhandene Einträge bleiben, es wird nichts doppelt angelegt).
    /// </summary>
    public static async Task<LawStudyResult> StudyAsync(string abbreviation, string? targetLanguage, ReferenceLibrary? library, ITermbase? termbase,
        IKnowledgeBase? knowledge, IProgress<string>? log = null, CancellationToken ct = default)
    {
        log?.Report($"Lade {abbreviation.Trim()} (amtliche Fassung, gesetze-im-internet.de) …");
        var (abbr, title, version, date, sections) = await LoadAsync(abbreviation, ct);
        log?.Report($"{abbr}: {sections.Count} Paragraphen" + (version != null ? $" – {version}" : ""));
        library?.AddDocument($"{Base}/{await FolderAsync(abbreviation, ct)}/", $"{abbr} – {title}", "de-DE", date, version,
            sections.Select(s => ($"{s.Number} {s.Title}".Trim(), $"{s.Number} {s.Title}. {s.Text}".Trim())).ToList());

        Dictionary<string, string>? english = null;
        if (targetLanguage != null && Languages.Primary(targetLanguage) == "en")
            try
            {
                english = await EnglishHeadingsAsync(abbreviation, ct);
                log?.Report(english.Count > 0 ? $"Englische Benennungen aus der Übersetzung: {english.Count} Überschriften" : "Keine englische Übersetzung gefunden.");
            }
            catch (HttpRequestException ex) { log?.Report("Englische Übersetzung nicht erreichbar: " + ex.Message); }

        var terms = ExtractTerms(sections, english);
        log?.Report($"{terms.Count} Begriffe gefunden ({terms.Count(t => t.LegalDefinition)} Legaldefinitionen).");
        var concepts = new List<(IReadOnlyList<(string Lang, string Text, TermStatus Status)> Terms, string? Definition, string? Domain, string? Source)>();
        var entries = new List<KnowledgeEntry>();
        foreach (var t in terms)
        {
            ct.ThrowIfCancellationRequested();
            var source = $"{t.Section} {abbr}";
            var target = targetLanguage != null ? t.Target : null;
            if (termbase != null && !termbase.Lookup("de-DE", t.Term, 5).Any(c => c.Terms.Any(x => x.Text.Equals(t.Term, StringComparison.OrdinalIgnoreCase))))
            {
                var names = new List<(string, string, TermStatus)> { ("de-DE", t.Term, TermStatus.Preferred) };
                if (target != null) names.Add((targetLanguage!, target, TermStatus.Admitted));
                concepts.Add((names, $"{t.Definition} ({source})" + (target != null ? " – englische Benennung aus der nicht amtlichen Übersetzung" : ""),
                    "Recht", t.Url));
            }
            if (knowledge != null && !knowledge.Search("de-DE", t.Term, 10).Any(e => e.Title.Equals(t.Term, StringComparison.OrdinalIgnoreCase) && e.SourceUrl == t.Url))
            {
                entries.Add(new KnowledgeEntry
                {
                    Language = "de-DE",
                    Topic = $"{abbr} – {title}",
                    Kind = KnowledgeKind.Definition,
                    Title = t.Term,
                    Content = t.Definition + (t.Target != null ? $" (englisch: {t.Target})" : ""),
                    Quote = t.Definition.Length > 300 ? t.Definition[..300] + " …" : t.Definition,
                    SourceUrl = t.Url,
                    SourceTitle = source + (version != null ? $" (Stand: {version})" : ""),
                    Author = "Einarbeiten",
                    Confidence = t.LegalDefinition ? 1 : 0.8,
                });
            }
        }
        // in einem Zug schreiben (Hunderte Begriffe – einzeln wäre das auf einer Festplatte spürbar langsam)
        var termsSaved = termbase is Termbase tb ? tb.AddConcepts(concepts) : concepts.Count(c => termbase!.AddConcept(c.Terms, c.Definition, c.Domain, c.Source) > 0);
        var knowledgeSaved = knowledge is KnowledgeBase kb ? kb.AddMany(entries) : entries.Count(e => knowledge!.Add(e) > 0);
        log?.Report($"Fertig: {termsSaved} Begriffe in der Terminologie, {knowledgeSaved} Einträge im Wissen, {sections.Count} Paragraphen durchsuchbar.");
        return new LawStudyResult(abbr, title, version, sections.Count, terms, termsSaved, knowledgeSaved, terms.Count(t => t.Target != null));
    }
}
