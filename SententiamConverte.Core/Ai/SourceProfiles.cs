using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai;

/// <summary>Fachquellen einer Sprache: das zugrundeliegende System (z. B. Rechtsordnung) und bevorzugte Seiten.</summary>
public sealed class LanguageSources
{
    /// <summary>Sprachcode; "*" = gilt für alle Sprachen (z. B. EU-Quellen).</summary>
    public string Language { get; set; } = "*";

    /// <summary>Rechtsordnung, Normenwerk oder Fachsystem, z. B. „deutsches Recht (BGB, ZPO)“.</summary>
    public string? System { get; set; }

    /// <summary>Domains der Fachseiten, z. B. „gesetze-im-internet.de“.</summary>
    public List<string> Sites { get; set; } = new();
}

/// <summary>
/// Fachquellen-Profil für ein Thema (Recht, Medizin, Technik …): auf diese Seiten beschränken die Agenten ihre Suche,
/// und das jeweilige System (z. B. Rechtsordnung) geht in den Begriffsvergleich ein.
/// </summary>
public sealed class SourceProfile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<LanguageSources> Languages { get; set; } = new();

    public IReadOnlyList<string> SitesFor(string language) =>
        Languages.Where(l => l.Language == "*" || Model.Languages.Matches(l.Language, language))
            .OrderBy(l => l.Language == "*" ? 1 : 0)
            .SelectMany(l => l.Sites).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string? SystemFor(string language) =>
        Languages.FirstOrDefault(l => string.Equals(l.Language, language, StringComparison.OrdinalIgnoreCase))?.System
        ?? Languages.FirstOrDefault(l => l.Language != "*" && Model.Languages.Matches(l.Language, language))?.System;

    public override string ToString() => Name;

    /// <summary>Textform zum Bearbeiten: je Zeile „Sprache | System | domain1, domain2“.</summary>
    public string ToText() => string.Join("\n", Languages.Select(l => $"{l.Language} | {l.System} | {string.Join(", ", l.Sites)}"));

    public static List<LanguageSources> ParseText(string text) =>
        text.Replace("\r", "").Split('\n')
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length >= 3 && parts[0].Trim().Length > 0)
            .Select(parts => new LanguageSources
            {
                Language = parts[0].Trim(),
                System = parts[1].Trim().Length > 0 ? parts[1].Trim() : null,
                Sites = parts[2].Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(CleanDomain).Where(d => d.Length > 0).ToList(),
            }).ToList();

    /// <summary>„https://www.Beispiel.de/pfad“ → „beispiel.de“.</summary>
    public static string CleanDomain(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var uri)) s = uri.Host;
        return s.StartsWith("www.") ? s[4..] : s;
    }

    /// <summary>Profil zu einem Fachgebiet, auch unter anderen Namen („law“, „prawo“, „language“ …).</summary>
    public static SourceProfile? Find(IEnumerable<SourceProfile> profiles, string field)
    {
        var f = field.Trim().ToLowerInvariant();
        if (f.Length == 0) return null;
        var list = profiles.ToList();
        var name = f switch
        {
            _ when f.StartsWith("law") || f.StartsWith("legal") || f.StartsWith("recht") || f.StartsWith("praw") || f.StartsWith("droit")
                   || f.StartsWith("dirit") || f.StartsWith("derech") || f.StartsWith("jur") => "Recht",
            _ when f.StartsWith("medi") || f.StartsWith("health") || f.StartsWith("gesund") || f.StartsWith("pharm") => "Medizin",
            _ when f.StartsWith("tech") || f.StartsWith("engineer") || f.StartsWith("norm") || f.StartsWith("maschin") => "Technik",
            _ when f.StartsWith("langu") || f.StartsWith("sprach") || f.StartsWith("dict") || f.StartsWith("wörterb") || f.StartsWith("język")
                   || f.StartsWith("jezyk") || f.StartsWith("lingu") || f.StartsWith("usage") || f.StartsWith("grammar") => "Sprache",
            _ when f is "eu" || f.StartsWith("europ") || f.StartsWith("verwalt") || f.StartsWith("admin") => "EU und Verwaltung",
            _ => field.Trim(),
        };
        return list.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(p => p.Name.StartsWith(field.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Neue Profile seit der ersten Fassung, für ältere Einstellungsdateien.</summary>
    public const int DefaultsVersion = 2;

    /// <summary>Ältere Einstellungen um das ergänzen, was in neueren Standardprofilen dazugekommen ist (ohne Eigenes zu ändern).</summary>
    public static void Upgrade(List<SourceProfile> profiles, int fromVersion)
    {
        if (fromVersion >= 2) return;
        var defaults = Defaults();
        if (profiles.All(p => !string.Equals(p.Name, "Sprache", StringComparison.OrdinalIgnoreCase)))
            profiles.Add(defaults.First(p => p.Name == "Sprache"));
        if (profiles.FirstOrDefault(p => p.Name == "Recht")?.Languages.FirstOrDefault(l => l.Language == "*") is { } eu
            && !eu.Sites.Contains("eur-lex.europa.eu", StringComparer.OrdinalIgnoreCase))
            eu.Sites.Add("eur-lex.europa.eu");
    }

    /// <summary>
    /// Vorschläge zum Start; jederzeit in den Einstellungen änderbar. Aufgenommen sind Seiten, die das automatische
    /// Lesen erlauben (robots.txt) und lesbaren Text liefern (geprüft am 15.10.2026, EUR-Lex und Sprachseiten am
    /// 06.11.2026). Nicht enthalten, weil sie das Lesen durch Programme untersagen oder technisch verhindern: IATE,
    /// Légifrance, ISAP, sn.pl, ISO, IEC/Electropedia, AWMF, BAuA, OSHA. Im Profil „Sprache“ stehen auch Wörterbücher,
    /// die nur in Suchergebnissen erscheinen, deren Seiten das Programm aber nicht liest (DWDS und Larousse mit
    /// TDM-Vorbehalt, Duden mit „noindex“, die englischen Wörterbücher sperren Programme aus); das DWDS ist zusätzlich
    /// über seine offizielle Schnittstelle angebunden.
    /// </summary>
    public static List<SourceProfile> Defaults() =>
    [
        new()
        {
            Name = "Recht",
            Description = "Gesetze, Rechtsprechung und Behörden; Begriffe hängen von der Rechtsordnung ab.",
            Languages =
            [
                new() { Language = "*", System = "EU-Recht", Sites = ["curia.europa.eu", "eur-lex.europa.eu"] },
                new() { Language = "de-DE", System = "deutsches Recht (BGB, HGB, ZPO, StGB …)", Sites = ["gesetze-im-internet.de", "dejure.org", "bundesgerichtshof.de", "bundesjustizamt.de"] },
                new() { Language = "de-AT", System = "österreichisches Recht (ABGB, UGB, ZPO)", Sites = ["ris.bka.gv.at", "oesterreich.gv.at"] },
                new() { Language = "de-CH", System = "schweizerisches Recht (ZGB, OR)", Sites = ["fedlex.admin.ch"] },
                new() { Language = "en-GB", System = "Recht von England und Wales (Common Law)", Sites = ["legislation.gov.uk", "judiciary.uk", "gov.uk", "lawcom.gov.uk"] },
                new() { Language = "en-US", System = "US-amerikanisches Recht (Bundesrecht, Common Law)", Sites = ["law.cornell.edu", "uscode.house.gov", "supremecourt.gov", "uscourts.gov"] },
                new() { Language = "pl-PL", System = "polnisches Recht (Kodeks cywilny, k.p.c., k.k.)", Sites = ["lexlege.pl", "prawo.pl", "orzeczenia.ms.gov.pl", "trybunal.gov.pl", "saos.org.pl", "gov.pl"] },
                new() { Language = "fr-FR", System = "französisches Recht (Code civil …)", Sites = ["conseil-constitutionnel.fr", "service-public.fr"] },
                new() { Language = "it-IT", System = "italienisches Recht", Sites = ["normattiva.it", "giustizia.it"] },
                new() { Language = "es-ES", System = "spanisches Recht", Sites = ["boe.es", "poderjudicial.es"] },
            ],
        },
        new()
        {
            Name = "Medizin",
            Description = "Behörden, Leitlinien und Fachinformationen.",
            Languages =
            [
                new() { Language = "*", System = "EU-Arzneimittelrecht, WHO", Sites = ["ema.europa.eu", "who.int"] },
                new() { Language = "de-DE", System = "deutsches Gesundheitswesen", Sites = ["gesund.bund.de", "gesundheitsinformation.de", "bfarm.de", "msdmanuals.com"] },
                new() { Language = "en-GB", System = "britisches Gesundheitswesen (NHS)", Sites = ["nhs.uk", "nice.org.uk"] },
                new() { Language = "en-US", System = "US-Gesundheitswesen", Sites = ["medlineplus.gov", "cdc.gov", "fda.gov"] },
                new() { Language = "pl-PL", System = "polnisches Gesundheitswesen", Sites = ["mp.pl", "pzh.gov.pl"] },
            ],
        },
        new()
        {
            Name = "Technik",
            Description = "Normung, Arbeitsschutz, Elektrotechnik, Maschinenbau.",
            Languages =
            [
                new() { Language = "*", System = "internationaler Arbeitsschutz", Sites = ["ilo.org"] },
                new() { Language = "de-DE", System = "DIN/VDE/VDI, DGUV", Sites = ["din.de", "dke.de", "vdi.de", "dguv.de", "vde.com"] },
                new() { Language = "en-GB", System = "BSI-Normen, HSE", Sites = ["bsigroup.com", "hse.gov.uk"] },
                new() { Language = "en-US", System = "NIST", Sites = ["nist.gov"] },
                new() { Language = "pl-PL", System = "PKN-Normen, CIOP", Sites = ["pkn.pl", "ciop.pl"] },
            ],
        },
        new()
        {
            Name = "Sprache",
            Description = "Wörterbücher, Sprachberatung und Korpora: Schreibung, Bedeutung, Gebrauch.",
            Languages =
            [
                new() { Language = "*", System = "Wiktionary (alle Sprachen)", Sites = ["wiktionary.org"] },
                new() { Language = "de-DE", System = "Standarddeutsch (amtliches Regelwerk, DWDS, Duden)", Sites = ["dwds.de", "duden.de", "grammis.ids-mannheim.de", "owid.de", "rechtschreibrat.com"] },
                new() { Language = "pl-PL", System = "polnische Standardsprache (Rada Języka Polskiego, PWN)", Sites = ["sjp.pwn.pl", "wsjp.pl", "rjp.pan.pl", "nkjp.pl"] },
                new() { Language = "en-GB", System = "britisches Englisch", Sites = ["dictionary.cambridge.org", "oxfordlearnersdictionaries.com", "collinsdictionary.com"] },
                new() { Language = "en-US", System = "amerikanisches Englisch", Sites = ["merriam-webster.com", "dictionary.cambridge.org"] },
                new() { Language = "fr-FR", System = "Französisch (Académie française, CNRTL)", Sites = ["cnrtl.fr", "dictionnaire.lerobert.com", "larousse.fr"] },
                new() { Language = "it-IT", System = "Italienisch (Accademia della Crusca)", Sites = ["treccani.it", "accademiadellacrusca.it"] },
                new() { Language = "es-ES", System = "Spanisch (Real Academia Española)", Sites = ["dle.rae.es", "fundeu.es"] },
                new() { Language = "nl-NL", System = "Niederländisch (Taalunie)", Sites = ["vandale.nl", "taalunie.org"] },
            ],
        },
        new()
        {
            Name = "EU und Verwaltung",
            Description = "EU-Institutionen in allen Amtssprachen.",
            Languages = [new() { Language = "*", System = "EU-Institutionen", Sites = ["curia.europa.eu", "europa.eu", "publications.europa.eu"] }],
        },
    ];

    /// <summary>
    /// Prüft, ob die Seiten eines Profils das Lesen erlauben und Text liefern (Startseite jeder Domain).
    /// </summary>
    public async Task<List<(string Site, bool Ok, string Info)>> CheckSitesAsync(CancellationToken ct = default)
    {
        var sites = Languages.SelectMany(l => l.Sites).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<(string, bool, string)>();
        await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (site, token) =>
        {
            var fetcher = new PageFetcher();
            var text = await fetcher.FetchTextAsync($"https://{site}/", 20000, token);
            var ok = text is { Length: > 200 };
            var info = text == null ? fetcher.LastSkipReason ?? "nicht lesbar" : ok ? $"{text.Length} Zeichen" : "kaum Text (evtl. nur mit Browser lesbar)";
            lock (results) results.Add((site, ok, info));
        });
        return results.OrderBy(r => r.Item2).ThenBy(r => r.Item1).ToList();
    }
}

/// <summary>Suche, auf Fachseiten beschränkt – mit Ausweichen auf die allgemeine Suche, wenn dort nichts zu finden ist.</summary>
public static class SiteSearch
{
    public static async Task<(List<SearchResult> Results, bool Restricted)> SearchAsync(
        ISearchProvider search, string query, string language, IReadOnlyList<string> sites, int max,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (sites.Count == 0) return (await search.SearchAsync(query, language, max, ct), false);
        var restricted = (await search.SearchInSitesAsync(query, language, sites, max, ct))
            .Where(r => IsOnSite(r.Url, sites)).ToList();
        if (restricted.Count > 0) return (restricted, true);
        log?.Invoke($"  auf den Fachseiten nichts gefunden – allgemeine Suche: {query}");
        return (await search.SearchAsync(query, language, max, ct), false);
    }

    public static bool IsOnSite(string url, IReadOnlyList<string> sites) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        sites.Any(s => u.Host.Equals(s, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
}
