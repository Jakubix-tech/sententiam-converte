using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein Treffer in einer amtlichen Rechtsdatenbank: Titel, Fundstelle und eine Adresse zum Lesen.</summary>
public sealed record LawHit(string Title, string Citation, string Url, string? Note = null);

/// <summary>
/// Amtliche Rechtsdatenbanken mit offener Schnittstelle, die automatische Abrufe erlauben (geprüft am 06.11.2026):
/// deutsches Bundesrecht (gesetze-im-internet.de: Inhaltsverzeichnis und Einzelnormen), polnisches Recht
/// (ELI-Schnittstelle des Sejm), britisches Recht (legislation.gov.uk, Atom-Feed) und EU-Recht (EUR-Lex; gesucht wird
/// über die SPARQL-Schnittstelle des Amts für Veröffentlichungen). Jede EU-Norm liegt in allen Amtssprachen vor –
/// dieselbe Stelle in zwei Sprachen ist die amtliche Entsprechung eines Begriffs.
/// Gesetzestexte sind amtliche Werke (§ 5 UrhG, Art. 4 des polnischen Urheberrechtsgesetzes, Open Government Licence,
/// Beschluss 2011/833/EU über die Weiterverwendung von Kommissionsdokumenten).
/// Nicht angebunden, weil ihre robots.txt Programme aussperrt: RIS (Österreich) und die Testphase des
/// Rechtsinformationsportals des Bundes.
/// </summary>
public static partial class LawSources
{
    internal static string GiiBase = "https://www.gesetze-im-internet.de";
    internal static string SejmBase = "https://api.sejm.gov.pl";
    internal static string UkBase = "https://www.legislation.gov.uk";
    internal static string EurLexBase = "https://eur-lex.europa.eu";
    internal static string CellarSparql = "https://publications.europa.eu/webapi/rdf/sparql";
    internal static string CellarResource = "https://publications.europa.eu/resource";

    private static readonly HttpClient Http = SearchProviderFactory.NewHttp();

    public static readonly IReadOnlyList<(string Code, string Name)> Jurisdictions =
    [
        ("de", "Germany – federal law (gesetze-im-internet.de)"),
        ("pl", "Poland – Dziennik Ustaw and Monitor Polski (Sejm database)"),
        ("uk", "United Kingdom (legislation.gov.uk)"),
        ("eu", "European Union (EUR-Lex, every official language)"),
    ];

    /// <summary>Die wichtigsten Gesetzbücher je Rechtsordnung – Hinweis für die KI, wenn eine Suche nichts findet.</summary>
    public static string MainActs(string code) => code switch
    {
        "de" => "BGB (civil law), HGB (commercial), ZPO (civil procedure), StGB (criminal), StPO, VwVfG (administrative procedure), VwGO, GG (constitution), AO (tax), SGB I–XII (social)",
        "pl" => "Kodeks cywilny, Kodeks postępowania cywilnego, Kodeks karny, Kodeks postępowania karnego, Kodeks postępowania administracyjnego, " +
                "Kodeks pracy, Kodeks spółek handlowych, Kodeks rodzinny i opiekuńczy, Konstytucja Rzeczypospolitej Polskiej, Ordynacja podatkowa",
        "uk" => "search by title, e.g. Companies Act 2006, Employment Rights Act 1996, Consumer Rights Act 2015, Tribunals, Courts and Enforcement Act 2007",
        "eu" => "search by number (Regulation 2016/679, Directive 2011/83) or by title words in the language of the query",
        _ => "",
    };

    /// <summary>„de-DE“, „Deutschland“, „german“ → „de“; „en-GB“, „England“ → „uk“; ohne Datenbank (z. B. „de-AT“) → null.</summary>
    public static string? Normalize(string jurisdiction)
    {
        var j = jurisdiction.Trim().ToLowerInvariant();
        if (j.Length == 0) return null;
        if (j is "eu" or "ue" or "eur-lex" || j.StartsWith("europ") || j.StartsWith("unia europ") || j.StartsWith("eu ")) return "eu";
        if (j is "uk" or "gb" or "en-gb" || j.Contains("england") || j.Contains("brit") || j.Contains("united kingdom") || j.Contains("königreich")) return "uk";
        if (j is "de" or "de-de" || j.StartsWith("deutsch") || j.StartsWith("german") || j.StartsWith("niemc")) return "de";
        if (j is "pl" or "pl-pl" || j.StartsWith("pol")) return "pl";
        return null;
    }

    /// <summary>In der Datenbank der Rechtsordnung suchen: Titelwörter, Kurzbezeichnung oder Fundstelle („§ 35 VwVfG“, „2016/679“).</summary>
    public static Task<List<LawHit>> SearchAsync(string jurisdiction, string query, IReadOnlyList<string> languages, int max = 8,
        CancellationToken ct = default) =>
        Normalize(jurisdiction) switch
        {
            "de" => GermanAsync(query, max, ct),
            "pl" => PolishAsync(query, max, ct),
            "uk" => BritishAsync(query, max, ct),
            "eu" => EuropeanAsync(query, languages, max, ct),
            _ => Task.FromResult(new List<LawHit>()),
        };

    private static async Task<string> GetStringAsync(string url, CancellationToken ct, string? accept = null)
    {
        var uri = new Uri(url);
        if (await PageFetcher.PermitAsync(uri, ct) is { } reason) throw new HttpRequestException(reason);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (accept != null) request.Headers.Accept.ParseAdd(accept);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{uri.Host}: HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static List<string> Words(string query) =>
        Regex.Split(query, @"[^\p{L}\p{N}\-]+").Where(w => w.Length >= 3).Take(6).ToList();

    private static long Number(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) => p,
        _ => 0,
    };

    // ---------- Deutschland ----------

    private static List<(string Title, string Folder)>? _toc;
    private static readonly SemaphoreSlim TocLock = new(1, 1);

    internal static void ResetCache() => _toc = null;

    [GeneratedRegex(@"<item>\s*<title>(.*?)</title>\s*<link>[^<]*/([^/<]+)/xml\.zip</link>", RegexOptions.Singleline)]
    private static partial Regex TocItem();

    [GeneratedRegex(@"^(§§?|Art\.?|Artikel)\s*(\d+[a-z]?)\s*(?:(?:Abs\.|Absatz)\s*\d+\s*)?(?:(?:S\.|Satz|Nr\.)\s*\d+\s*)?(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Citation();

    /// <summary>Inhaltsverzeichnis aller Bundesgesetze und -verordnungen (Titel und Ordnername), einmal je Programmlauf.</summary>
    private static async Task<List<(string Title, string Folder)>> TocAsync(CancellationToken ct)
    {
        await TocLock.WaitAsync(ct);
        try
        {
            if (_toc != null) return _toc;
            var xml = await GetStringAsync(GiiBase + "/gii-toc.xml", ct);
            return _toc = TocItem().Matches(xml).Select(m => (WebUtility.HtmlDecode(m.Groups[1].Value).Trim(), m.Groups[2].Value)).ToList();
        }
        finally { TocLock.Release(); }
    }

    /// <summary>Ordner eines Bundesgesetzes laut Inhaltsverzeichnis; null, wenn es dort fehlt oder nicht erreichbar ist.</summary>
    internal static async Task<string?> GermanFolderAsync(string abbreviation, CancellationToken ct)
    {
        try { return FolderFor(await TocAsync(ct), abbreviation); }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Kurzbezeichnung → Ordner: „VwVfG“ → „vwvfg“, „SGB X“ → „sgb_10“, „StVO“ → „stvo_2013“.</summary>
    private static string? FolderFor(List<(string Title, string Folder)> toc, string abbreviation)
    {
        var parts = abbreviation.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Roman(p) is { } r ? r.ToString(CultureInfo.InvariantCulture) : p);
        var key = string.Join("_", parts).Replace('ä', '_').Replace('ö', '_').Replace('ü', '_').Replace('ß', '_');
        if (key.Length == 0) return null;
        return toc.FirstOrDefault(t => t.Folder == key).Folder
               ?? toc.Where(t => t.Folder.StartsWith(key + "_") && t.Folder[(key.Length + 1)..].All(char.IsDigit))
                   .Select(t => t.Folder).OrderBy(f => f.Length).FirstOrDefault();
    }

    private static int? Roman(string s)
    {
        if (s.Length == 0 || s.Any(c => c is not ('i' or 'v' or 'x'))) return null;
        var values = s.Select(c => c switch { 'i' => 1, 'v' => 5, _ => 10 }).ToList();
        return values.Select((v, i) => i + 1 < values.Count && v < values[i + 1] ? -v : v).Sum();
    }

    private static async Task<List<LawHit>> GermanAsync(string query, int max, CancellationToken ct)
    {
        var toc = await TocAsync(ct);
        var hits = new List<LawHit>();
        var q = query.Trim();
        // „§ 35 VwVfG“, „§ 31 SGB X“, auch mit angehängtem Begriff („§ 35 VwVfG Verwaltungsakt“)
        var cited = Citation().Match(q);
        var words3 = cited.Success ? cited.Groups[3].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        var abbreviation = new[] { string.Join(' ', words3), string.Join(' ', words3.Take(2)), string.Join(' ', words3.Take(1)) }
            .FirstOrDefault(a => a.Length > 0 && FolderFor(toc, a) != null);
        if (cited.Success && abbreviation != null && FolderFor(toc, abbreviation) is { } lawFolder)
        {
            var m = cited;
            var number = m.Groups[2].Value.ToLowerInvariant();
            var article = m.Groups[1].Value.StartsWith("Art", StringComparison.OrdinalIgnoreCase);
            hits.Add(new LawHit(toc.First(t => t.Folder == lawFolder).Title, $"{(article ? "Art." : "§")} {number} {abbreviation}",
                $"{GiiBase}/{lawFolder}/{(article ? "art_" : "__")}{number}.html", "the provision itself"));
            q = abbreviation;
        }
        var folder = FolderFor(toc, q);
        var words = Words(q);
        var matches = toc.Where(t => t.Folder == folder)
            .Concat(toc.Where(t => words.Count > 0 && words.All(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Title.Length));
        foreach (var t in matches)
        {
            if (hits.Count >= max) break;
            var url = $"{GiiBase}/{t.Folder}/index.html";
            if (hits.Any(h => h.Url == url)) continue;
            hits.Add(new LawHit(t.Title, t.Folder, url, $"table of contents; a single section: {GiiBase}/{t.Folder}/__<number>.html"));
        }
        return hits;
    }

    // ---------- Polen ----------

    private static async Task<List<LawHit>> PolishAsync(string query, int max, CancellationToken ct)
    {
        var hits = new List<(LawHit Hit, bool Consolidated, bool Amendment, long Year, long Pos)>();
        foreach (var inForce in new[] { true, false })
        {
            var url = $"{SejmBase}/eli/acts/search?title={Uri.EscapeDataString(query.Trim())}&limit={Math.Max(max * 3, 20)}" + (inForce ? "&inForce=1" : "");
            var json = JsonNode.Parse(await GetStringAsync(url, ct, "application/json"));
            foreach (var item in json?["items"] as JsonArray ?? [])
            {
                if (item == null) continue;
                var title = (string?)item["title"] ?? "";
                var publisher = (string?)item["publisher"] ?? "DU";
                long year = Number(item["year"]), pos = Number(item["pos"]);
                var html = item["textHTML"] is JsonValue h && h.TryGetValue<bool>(out var hb) && hb;
                var pdf = item["textPDF"] is JsonValue p && p.TryGetValue<bool>(out var pb) && pb;
                var textUrl = html || pdf
                    ? $"{SejmBase}/eli/acts/{publisher}/{year}/{pos}/text.{(html ? "html" : "pdf")}"
                    : $"https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id={(string?)item["address"]}";
                var consolidated = title.Contains("jednolitego tekstu", StringComparison.OrdinalIgnoreCase);
                var note = $"{(string?)item["type"]}, {(string?)item["status"]}" + (consolidated ? " – consolidated text (tekst jednolity)" : "");
                hits.Add((new LawHit(title, (string?)item["displayAddress"] ?? $"{publisher} {year} poz. {pos}", textUrl, note),
                    consolidated, title.Contains("o zmianie", StringComparison.OrdinalIgnoreCase), year, pos));
            }
            if (hits.Count > 0) break;
        }
        return hits.OrderByDescending(h => h.Consolidated).ThenBy(h => h.Amendment).ThenByDescending(h => h.Year).ThenByDescending(h => h.Pos)
            .Select(h => h.Hit).Take(max).ToList();
    }

    // ---------- Vereinigtes Königreich ----------

    private static async Task<List<LawHit>> BritishAsync(string query, int max, CancellationToken ct)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var hits = new List<LawHit>();
        foreach (var field in new[] { "title", "text" })
        {
            var xml = await GetStringAsync($"{UkBase}/all/data.feed?{field}={Uri.EscapeDataString(query.Trim())}&results-count={max}", ct);
            foreach (var entry in XDocument.Parse(xml).Root?.Elements(atom + "entry") ?? [])
            {
                var id = (string?)entry.Element(atom + "id") ?? "";
                var path = Regex.Match(id, @"legislation\.gov\.uk/id/(.+)$") is { Success: true } m ? m.Groups[1].Value : null;
                if (path == null) continue;
                var url = $"{UkBase}/{path}";
                if (hits.Any(h => h.Url == url)) continue;
                hits.Add(new LawHit(((string?)entry.Element(atom + "title") ?? "").Trim(), path, url,
                    $"a single section: {url}/section/<number>"));
                if (hits.Count >= max) break;
            }
            if (hits.Count > 0) break;
        }
        return hits;
    }

    // ---------- Europäische Union ----------

    internal static readonly Dictionary<string, string> EuLanguages = new()
    {
        ["bg"] = "BUL", ["cs"] = "CES", ["da"] = "DAN", ["de"] = "DEU", ["el"] = "ELL", ["en"] = "ENG", ["es"] = "SPA",
        ["et"] = "EST", ["fi"] = "FIN", ["fr"] = "FRA", ["ga"] = "GLE", ["hr"] = "HRV", ["hu"] = "HUN", ["it"] = "ITA",
        ["lt"] = "LIT", ["lv"] = "LAV", ["mt"] = "MLT", ["nl"] = "NLD", ["pl"] = "POL", ["pt"] = "POR", ["ro"] = "RON",
        ["sk"] = "SLK", ["sl"] = "SLV", ["sv"] = "SWE",
    };

    [GeneratedRegex(@"\b3\d{4}[RLDF]\d{4}\b")]
    private static partial Regex Celex();

    [GeneratedRegex(@"(?<y>(?:19|20)\d{2})/(?<n>\d{1,4})(?!\d)|(?<n2>\d{1,4})/(?<y2>(?:19|20)\d{2})(?!\d)|(?<!\d)(?<y3>\d{2})/(?<n3>\d{1,4})/(?:EWG|EG|EU|EEC|EC|EGKS|EWR|UE|WE|CE)")]
    private static partial Regex ActNumber();

    /// <summary>Art der Norm aus dem Wortlaut: R = Verordnung, L = Richtlinie, D = Beschluss.</summary>
    private static string? ActType(string query)
    {
        var q = query.ToLowerInvariant();
        if (Regex.IsMatch(q, "verordnung|regulation|rozporządz|règlement|reglement|regolamento|reglamento")) return "R";
        if (Regex.IsMatch(q, "richtlinie|directive|dyrektyw|direttiva|directiva|richtlijn")) return "L";
        if (Regex.IsMatch(q, "beschluss|decision|decyzj|décision|decisione|decisión|besluit")) return "D";
        return null;
    }

    /// <summary>
    /// EUR-Lex-Seiten verlangen einen Browser (Schutz vor Programmen); dieselben amtlichen Texte gibt die
    /// Dokumentenschnittstelle des Amts für Veröffentlichungen (CELLAR) an Programme heraus – Sprache per Accept-Language.
    /// </summary>
    internal static (Uri Uri, string Language)? CellarFor(Uri uri)
    {
        if (!uri.Host.Equals(new Uri(EurLexBase).Host, StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith("eur-lex.europa.eu", StringComparison.OrdinalIgnoreCase))
            return null;
        var language = Regex.Match(uri.AbsolutePath, "^/legal-content/([A-Za-z]{2})/");
        var celex = Regex.Match(WebUtility.UrlDecode(uri.Query), @"CELEX:([0-9A-Za-z()]+)");
        if (!language.Success || !celex.Success) return null;
        var code = EuLanguages.GetValueOrDefault(language.Groups[1].Value.ToLowerInvariant(), "ENG");
        return (new Uri($"{CellarResource}/celex/{celex.Groups[1].Value.ToUpperInvariant()}"), code.ToLowerInvariant());
    }

    private static string Language2(string language) => (language.Split('-')[0].ToLowerInvariant()) is var l && EuLanguages.ContainsKey(l) ? l : "en";

    private static string EurLexUrl(string celex, string language) =>
        $"{EurLexBase}/legal-content/{Language2(language).ToUpperInvariant()}/TXT/HTML/?uri=CELEX:{celex}";

    private static async Task<List<LawHit>> EuropeanAsync(string query, IReadOnlyList<string> languages, int max, CancellationToken ct)
    {
        var langs = (languages.Count > 0 ? languages : ["en-GB"]).Select(Language2).Distinct().ToList();
        var main = EuLanguages[langs[0]];
        string sparql;
        var celexes = Celex().Matches(query).Select(m => m.Value).ToList();
        if (celexes.Count == 0 && ActNumber().Match(query) is { Success: true } n)
        {
            var year = n.Groups["y"].Success ? n.Groups["y"].Value : n.Groups["y2"].Success ? n.Groups["y2"].Value
                : (int.Parse(n.Groups["y3"].Value, CultureInfo.InvariantCulture) > 50 ? "19" : "20") + n.Groups["y3"].Value;
            var number = (n.Groups["n"].Success ? n.Groups["n"] : n.Groups["n2"].Success ? n.Groups["n2"] : n.Groups["n3"]).Value.PadLeft(4, '0');
            var types = ActType(query) is { } type ? new[] { type } : new[] { "R", "L", "D" };
            celexes = types.Select(t => $"3{year}{t}{number}").ToList();
        }
        if (celexes.Count > 0)
        {
            sparql = $$"""
                PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
                PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
                SELECT ?celex ?title WHERE {
                  VALUES ?celex { {{string.Join(" ", celexes.Select(c => $"\"{c}\"^^xsd:string"))}} }
                  ?w cdm:resource_legal_id_celex ?celex .
                  ?e cdm:expression_belongs_to_work ?w ; cdm:expression_title ?title ;
                     cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{main}}> .
                } LIMIT {{max}}
                """;
        }
        else
        {
            var words = Words(query.Replace("\"", " ").Replace("'", " "));
            if (words.Count == 0) return [];
            var contains = string.Join(" AND ", words.Select(w => $"\"{w}\""));
            sparql = $$"""
                PREFIX cdm: <http://publications.europa.eu/ontology/cdm#>
                SELECT DISTINCT ?celex ?title WHERE {
                  ?e cdm:expression_title ?title . ?title bif:contains '{{contains}}' .
                  ?e cdm:expression_uses_language <http://publications.europa.eu/resource/authority/language/{{main}}> ;
                     cdm:expression_belongs_to_work ?w .
                  ?w cdm:resource_legal_id_celex ?celex .
                  FILTER(STRSTARTS(STR(?celex), "3") && !CONTAINS(STR(?celex), "("))
                } LIMIT {{max * 2}}
                """;
        }
        var url = $"{CellarSparql}?query={Uri.EscapeDataString(sparql)}&format={Uri.EscapeDataString("application/sparql-results+json")}";
        var json = JsonNode.Parse(await GetStringAsync(url, ct, "application/sparql-results+json"));
        var hits = new List<LawHit>();
        foreach (var b in json?["results"]?["bindings"] as JsonArray ?? [])
        {
            var celex = (string?)b?["celex"]?["value"];
            var title = (string?)b?["title"]?["value"];
            if (celex == null || title == null || hits.Any(h => h.Citation == "CELEX " + celex)) continue;
            var others = langs.Skip(1).Select(l => $"{l.ToUpperInvariant()}: {EurLexUrl(celex, l)}").ToList();
            hits.Add(new LawHit(title.Trim(), "CELEX " + celex, EurLexUrl(celex, langs[0]),
                others.Count > 0 ? "the same act in " + string.Join("; ", others) : null));
            if (hits.Count >= max) break;
        }
        return hits;
    }
}
