using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Alignment;

/// <summary>Ein Rechtsakt der EU zum Thema: CELEX-Nummer, Titel in Ausgangs- und Zielsprache, Art (R, L, D …).</summary>
public sealed record EuAct(string Celex, string Title, string TargetTitle)
{
    /// <summary>R = Verordnung, L = Richtlinie, D = Beschluss (Buchstabe in der CELEX-Nummer).</summary>
    public char Type => Celex.Length > 5 ? Celex[5] : '?';

    public int Year => Celex.Length >= 5 && int.TryParse(Celex[1..5], CultureInfo.InvariantCulture, out var y) ? y : 0;

    public string Url(string language) =>
        $"{LawSources.EurLexBase}/legal-content/{Languages.Primary(language).ToUpperInvariant()}/TXT/HTML/?uri=CELEX:{Celex}";
}

/// <summary>Was aus einem Rechtsakt in die TM kam (oder warum nichts).</summary>
public sealed record EuActResult(EuAct Act, int Pairs, int Added, string? Problem);

public sealed record EuTmResult(List<EuActResult> Acts)
{
    public int Pairs => Acts.Sum(a => a.Pairs);
    public int Added => Acts.Sum(a => a.Added);
}

/// <summary>
/// Thema → TM: Rechtsakte der EU zu einem Thema finden (Titelsuche über die SPARQL-Schnittstelle des Amts für
/// Veröffentlichungen), beide Sprachfassungen aus CELLAR laden und Satz für Satz paaren. Jede Sprachfassung ist
/// amtlich und gleich gegliedert – derselbe Absatz an derselben Stelle ist dieselbe Aussage; deshalb wird nur Absatz
/// für Absatz gepaart, und nur, wenn beide Fassungen gleich viele Absätze gleicher Art haben. Innerhalb eines Absatzes
/// werden Sätze gepaart, wenn beide gleich viele haben, sonst der ganze Absatz (wenn er kurz genug ist). Paare, deren
/// Zahlen nicht übereinstimmen oder deren Länge zu verschieden ist, fallen weg.
/// Weiterverwendung: Beschluss 2011/833/EU; Quelle: EUR-Lex, © Europäische Union. Verbindlich ist nur die im
/// Amtsblatt veröffentlichte Fassung.
/// </summary>
public static partial class EuLawTm
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = SearchProviderFactory.NewHttp();
        http.Timeout = TimeSpan.FromMinutes(2);                         // die SPARQL-Schnittstelle braucht manchmal über eine Minute
        return http;
    }

    private const int MaxDocumentBytes = 25_000_000;
    private const int MaxBlockAsUnit = 600;
    private const int MaxUnit = 1000;

    /// <summary>Dreibuchstabiger Sprachcode der EU („de-DE“ → „DEU“); null, wenn die Sprache keine Amtssprache ist.</summary>
    public static string? EuLanguage(string language) =>
        LawSources.EuLanguages.TryGetValue(Languages.Primary(language), out var code) ? code : null;

    // ---------- Suche ----------

    /// <summary>
    /// Suchausdruck für die Titelsuche: Kommas trennen Alternativen, Wörter einer Alternative müssen alle vorkommen;
    /// Wörter werden auf ihren Stamm gekürzt („Schweißen“ → „Schweiß*“), damit auch Zusammensetzungen gefunden werden.
    /// </summary>
    internal static string? TitleQuery(string topic)
    {
        var alternatives = topic.Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Regex.Split(part, @"[^\p{L}\p{N}\-]+").Where(w => w.Length >= 4).Select(Stem).Distinct().ToList())
            .Where(words => words.Count > 0)
            .Select(words => words.Count == 1 ? words[0] : "(" + string.Join(" AND ", words) + ")")
            .Distinct().ToList();
        return alternatives.Count == 0 ? null : string.Join(" OR ", alternatives);
    }

    private static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        foreach (var ending in new[] { "ungen", "ing", "en", "er", "e", "n", "s" })
            if (w.EndsWith(ending, StringComparison.Ordinal) && w.Length - ending.Length >= 4)
            {
                w = w[..^ending.Length];
                break;
            }
        return $"\"{w}*\"";
    }

    /// <summary>
    /// Rechtsakte, deren Titel in der Ausgangssprache zum Thema passt und die es auch in der Zielsprache gibt –
    /// Verordnungen und Richtlinien zuerst, ändernde und kurze Durchführungsakte weiter hinten.
    /// </summary>
    public static async Task<List<EuAct>> FindAsync(string topic, string sourceLanguage, string targetLanguage, int max = 20, CancellationToken ct = default)
    {
        var src = EuLanguage(sourceLanguage) ?? throw new AiException($"{sourceLanguage} ist keine Amtssprache der EU – EU-Recht gibt es darin nicht.");
        var tgt = EuLanguage(targetLanguage) ?? throw new AiException($"{targetLanguage} ist keine Amtssprache der EU – EU-Recht gibt es darin nicht.");
        var celexes = Regex.Matches(topic, @"\b3\d{4}[RLDF]\d{4}\b").Select(m => m.Value).Distinct().ToList();
        string sparql;
        if (celexes.Count > 0)
            sparql = $$"""
                PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
                PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
                SELECT DISTINCT ?celex ?title ?ttitle WHERE {
                  VALUES ?celex { {{string.Join(" ", celexes.Select(c => $"\"{c}\"^^xsd:string"))}} }
                  ?w cdm:resource_legal_id_celex ?celex .
                  ?e cdm:expression_belongs_to_work ?w ; cdm:expression_title ?title ;
                     cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{src}}> .
                  ?t cdm:expression_belongs_to_work ?w ; cdm:expression_title ?ttitle ;
                     cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{tgt}}> .
                } LIMIT {{max}}
                """;
        else
        {
            var query = TitleQuery(topic) ?? throw new AiException("Bitte ein Thema mit mindestens einem Wort aus vier Buchstaben angeben.");
            sparql = $$"""
                PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
                SELECT DISTINCT ?celex ?title ?ttitle WHERE {
                  ?e cdm:expression_title ?title . ?title bif:contains '{{query.Replace("'", " ")}}' .
                  ?e cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{src}}> ;
                     cdm:expression_belongs_to_work ?w .
                  ?w cdm:resource_legal_id_celex ?celex .
                  FILTER(STRSTARTS(STR(?celex), "3") && !CONTAINS(STR(?celex), "("))
                  ?t cdm:expression_belongs_to_work ?w ; cdm:expression_title ?ttitle ;
                     cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{tgt}}> .
                } ORDER BY DESC(?celex) LIMIT {{Math.Max(max * 3, 30)}}
                """;
        }
        var url = $"{LawSources.CellarSparql}?query={Uri.EscapeDataString(sparql)}&format={Uri.EscapeDataString("application/sparql-results+json")}";
        if (await PageFetcher.PermitAsync(new Uri(url), ct) is { } refused) throw new AiException("EU-Recht: " + refused);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/sparql-results+json");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new AiException($"EU-Recht: Suche fehlgeschlagen (HTTP {(int)response.StatusCode}).");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var acts = new Dictionary<string, EuAct>();
        foreach (var b in json?["results"]?["bindings"] as JsonArray ?? [])
        {
            var celex = (string?)b?["celex"]?["value"];
            var title = (string?)b?["title"]?["value"];
            var target = (string?)b?["ttitle"]?["value"];
            if (celex == null || title == null || target == null) continue;
            acts.TryAdd(celex, new EuAct(celex, Clean(title), Clean(target)));
        }
        return acts.Values.OrderByDescending(Score).ThenByDescending(a => a.Year).Take(max).ToList();
    }

    /// <summary>Hauptrechtsakte vor Änderungen und Durchführungsbeschlüssen; kurze Titel sind meist Stammakte.</summary>
    private static double Score(EuAct a)
    {
        var score = a.Type switch { 'R' => 2.0, 'L' => 2.0, 'D' => 0.5, _ => 0 };
        if (Regex.IsMatch(a.TargetTitle, @"\b(amending|correcting|corrigendum|implementing|delegated|discharge|derogat)", RegexOptions.IgnoreCase)
            || Regex.IsMatch(a.Title, @"(Änderung|Berichtigung|Durchführungs|Delegierte|Entlastung|zmieniając|wykonawcz|modifiant|d'exécution)", RegexOptions.IgnoreCase))
            score -= 1.5;
        // neuere vor älteren (eine alte Richtlinie ist oft schon ersetzt)
        return score - Math.Min(a.Title.Length, 600) / 300.0 + Math.Clamp(a.Year - 1990, 0, 40) / 25.0;
    }

    // ---------- Laden ----------

    /// <summary>Amtliche Sprachfassung als XHTML aus CELLAR (dieselben Texte wie EUR-Lex, für Programme freigegeben).</summary>
    internal static async Task<string> XhtmlAsync(string celex, string language, CancellationToken ct)
    {
        var code = (EuLanguage(language) ?? throw new AiException($"{language} ist keine Amtssprache der EU.")).ToLowerInvariant();
        var uri = new Uri($"{LawSources.CellarResource}/celex/{celex}");
        if (await PageFetcher.PermitAsync(uri, ct) is { } refused) throw new AiException(refused);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/xhtml+xml");
            request.Headers.Accept.ParseAdd("text/html;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd(code);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < 5)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme == Uri.UriSchemeHttp && uri.Scheme == Uri.UriSchemeHttps) next = new UriBuilder(next) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                uri = next;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) throw new AiException("keine Textfassung in dieser Sprache");
            if (!response.IsSuccessStatusCode) throw new AiException($"HTTP {(int)response.StatusCode}");
            if (response.Headers.TryGetValues("tdm-reservation", out var tdm) && tdm.Any(v => v.Trim() == "1"))
                throw new AiException("Nutzungsvorbehalt für Text- und Data-Mining");
            if (response.Content.Headers.ContentLength > MaxDocumentBytes) throw new AiException("Dokument zu groß");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new AiException($"keine HTML-Fassung ({type})");
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    // ---------- Paaren ----------

    /// <summary>Absätze in Dokumentreihenfolge: Art (CSS-Klasse) und bereinigter Text.</summary>
    internal static List<(string Kind, string Text)> Blocks(string html) =>
        Paragraph().Matches(html).Select(m =>
        {
            var kind = Regex.Match(m.Groups["attr"].Value, @"class=""([^""]*)""") is { Success: true } c ? c.Groups[1].Value : "";
            var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups["body"].Value, "<[^>]+>", " "));
            return (kind, Clean(text));
        }).ToList();

    [GeneratedRegex(@"<p\b(?<attr>[^>]*)>(?<body>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Paragraph();

    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    /// <summary>
    /// Zwei Sprachfassungen paaren. null, wenn die Gliederung abweicht (dann wäre jede Paarung geraten).
    /// </summary>
    internal static List<(string Source, string Target)>? Align(string sourceHtml, string targetHtml, string sourceLanguage, string targetLanguage)
    {
        var a = Blocks(sourceHtml);
        var b = Blocks(targetHtml);
        if (a.Count == 0 || a.Count != b.Count) return null;
        var sameKind = a.Zip(b).Count(x => x.First.Kind == x.Second.Kind);
        if (sameKind < a.Count * 0.95) return null;
        var srcSegmenter = new Segmenter(sourceLanguage);
        var tgtSegmenter = new Segmenter(targetLanguage);
        var pairs = new List<(string, string)>();
        foreach (var ((kind, s), (_, t)) in a.Zip(b))
        {
            if (kind.Contains("note", StringComparison.OrdinalIgnoreCase)) continue;      // Fußnoten: Amtsblatt-Fundstellen
            if (!Words().IsMatch(s) || !Words().IsMatch(t) || s == t) continue;
            var ss = srcSegmenter.Split(s).Pieces.Select(p => p.Text.Trim()).Where(x => x.Length > 0).ToList();
            var ts = tgtSegmenter.Split(t).Pieces.Select(p => p.Text.Trim()).Where(x => x.Length > 0).ToList();
            if (ss.Count == ts.Count)
                pairs.AddRange(ss.Zip(ts).Where(p => Plausible(p.First, p.Second)));
            else if (s.Length <= MaxBlockAsUnit && t.Length <= MaxBlockAsUnit && Plausible(s, t))
                pairs.Add((s, t));
        }
        return pairs;
    }

    [GeneratedRegex(@"\p{L}{3}")]
    private static partial Regex Words();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    // Überschriften ohne Inhalt („Artikel 46“, „KAPITEL VII“, „ANHANG I“) bringen der TM nichts
    [GeneratedRegex(@"^\p{L}+\s+([\dIVXLC]+[a-z]?|\d+(\.\d+)*)\.?$")]
    private static partial Regex BareHeading();

    /// <summary>Gleiche Zahlen, ähnliche Länge, keine bloße Überschrift.</summary>
    internal static bool Plausible(string source, string target)
    {
        if (source.Length > MaxUnit || target.Length > MaxUnit) return false;
        if (BareHeading().IsMatch(source) && BareHeading().IsMatch(target)) return false;
        if (!Words().IsMatch(source) || !Words().IsMatch(target)) return false;
        var ratio = (double)Math.Max(source.Length, target.Length) / Math.Max(1, Math.Min(source.Length, target.Length));
        if (ratio > (Math.Min(source.Length, target.Length) < 30 ? 3.0 : 2.2)) return false;
        var sn = Digits().Matches(source).Select(m => m.Value.TrimStart('0')).Order().ToList();
        var tn = Digits().Matches(target).Select(m => m.Value.TrimStart('0')).Order().ToList();
        return sn.SequenceEqual(tn);
    }

    // ---------- Aufbauen ----------

    /// <summary>
    /// Die gewählten Rechtsakte laden, paaren und in die TM schreiben – mit Herkunft (CELEX) und Nachbarsätzen als
    /// Kontext. Ein Rechtsakt, der sich nicht laden oder paaren lässt, wird übersprungen und im Ergebnis vermerkt.
    /// </summary>
    public static async Task<EuTmResult> BuildAsync(IReadOnlyList<EuAct> acts, string sourceLanguage, string targetLanguage, TranslationMemory tm,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var results = new List<EuActResult>();
        foreach (var act in acts)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Lade {act.Celex} in beiden Sprachen …");
            List<(string Source, string Target)>? pairs;
            try
            {
                var source = await XhtmlAsync(act.Celex, sourceLanguage, ct);
                var target = await XhtmlAsync(act.Celex, targetLanguage, ct);
                pairs = Align(source, target, sourceLanguage, targetLanguage);
            }
            catch (Exception ex) when (ex is AiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log?.Report($"{act.Celex}: übersprungen – {ex.Message}");
                results.Add(new EuActResult(act, 0, 0, ex.Message));
                continue;
            }
            if (pairs == null)
            {
                log?.Report($"{act.Celex}: übersprungen – die Sprachfassungen sind unterschiedlich gegliedert.");
                results.Add(new EuActResult(act, 0, 0, "unterschiedlich gegliedert"));
                continue;
            }
            var unique = pairs.Distinct().ToList();
            var origin = $"EUR-Lex CELEX {act.Celex}";
            var entries = unique.Select((p, i) => new TmEntry(sourceLanguage, targetLanguage, p.Source, p.Target, Origin: origin,
                Previous: i > 0 ? unique[i - 1].Source : "", Next: i + 1 < unique.Count ? unique[i + 1].Source : "")).ToList();
            var before = tm.Count();
            tm.AddMany(entries);
            var added = (int)(tm.Count() - before);
            log?.Report($"{act.Celex}: {unique.Count} Satzpaare, {added} neu in der TM.");
            results.Add(new EuActResult(act, unique.Count, added, null));
        }
        return new EuTmResult(results);
    }
}
