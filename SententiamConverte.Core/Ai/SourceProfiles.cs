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

    /// <summary>Vorschläge zum Start; jederzeit in den Einstellungen änderbar.</summary>
    public static List<SourceProfile> Defaults() =>
    [
        new()
        {
            Name = "Recht",
            Description = "Gesetze, Rechtsprechung und EU-Recht; Begriffe hängen von der Rechtsordnung ab.",
            Languages =
            [
                new() { Language = "*", System = "EU-Recht", Sites = ["eur-lex.europa.eu", "iate.europa.eu", "curia.europa.eu"] },
                new() { Language = "de-DE", System = "deutsches Recht (BGB, HGB, ZPO, StGB …)", Sites = ["gesetze-im-internet.de", "dejure.org", "bundesgerichtshof.de", "bundesjustizamt.de"] },
                new() { Language = "de-AT", System = "österreichisches Recht (ABGB, UGB, ZPO)", Sites = ["ris.bka.gv.at", "oesterreich.gv.at"] },
                new() { Language = "de-CH", System = "schweizerisches Recht (ZGB, OR)", Sites = ["fedlex.admin.ch", "bger.ch"] },
                new() { Language = "en-GB", System = "Recht von England und Wales (Common Law)", Sites = ["legislation.gov.uk", "judiciary.uk", "gov.uk", "lawcom.gov.uk"] },
                new() { Language = "en-US", System = "US-amerikanisches Recht (Bundesrecht, Common Law)", Sites = ["law.cornell.edu", "uscode.house.gov", "supremecourt.gov", "uscourts.gov"] },
                new() { Language = "pl-PL", System = "polnisches Recht (Kodeks cywilny, k.p.c., k.k.)", Sites = ["isap.sejm.gov.pl", "sn.pl", "gov.pl", "trybunal.gov.pl"] },
                new() { Language = "fr-FR", System = "französisches Recht (Code civil …)", Sites = ["legifrance.gouv.fr", "courdecassation.fr", "service-public.fr"] },
                new() { Language = "it-IT", System = "italienisches Recht", Sites = ["normattiva.it", "giustizia.it"] },
                new() { Language = "es-ES", System = "spanisches Recht", Sites = ["boe.es", "poderjudicial.es"] },
            ],
        },
        new()
        {
            Name = "Medizin",
            Description = "Leitlinien, Behörden und Fachinformationen.",
            Languages =
            [
                new() { Language = "*", System = "EU-Arzneimittelrecht", Sites = ["ema.europa.eu", "who.int", "iate.europa.eu"] },
                new() { Language = "de-DE", System = "deutsches Gesundheitswesen", Sites = ["awmf.org", "rki.de", "gesund.bund.de", "gesundheitsinformation.de", "bfarm.de"] },
                new() { Language = "en-GB", System = "britisches Gesundheitswesen (NHS)", Sites = ["nhs.uk", "nice.org.uk", "gov.uk"] },
                new() { Language = "en-US", System = "US-Gesundheitswesen", Sites = ["ncbi.nlm.nih.gov", "medlineplus.gov", "cdc.gov", "fda.gov"] },
                new() { Language = "pl-PL", System = "polnisches Gesundheitswesen", Sites = ["pzh.gov.pl", "gov.pl", "mp.pl"] },
            ],
        },
        new()
        {
            Name = "Technik",
            Description = "Normung, Elektrotechnik, Maschinenbau.",
            Languages =
            [
                new() { Language = "*", System = "internationale Normung (IEC/ISO)", Sites = ["electropedia.org", "iso.org", "iec.ch", "iate.europa.eu"] },
                new() { Language = "de-DE", System = "DIN/VDE/VDI", Sites = ["din.de", "dke.de", "vdi.de", "baua.de"] },
                new() { Language = "en-GB", System = "BSI-Normen", Sites = ["bsigroup.com", "hse.gov.uk"] },
                new() { Language = "en-US", System = "ANSI/OSHA", Sites = ["ansi.org", "osha.gov", "nist.gov"] },
                new() { Language = "pl-PL", System = "PKN-Normen", Sites = ["pkn.pl", "ciop.pl"] },
            ],
        },
        new()
        {
            Name = "EU und Verwaltung",
            Description = "Amtliche EU-Terminologie in allen Amtssprachen.",
            Languages = [new() { Language = "*", System = "EU-Institutionen", Sites = ["eur-lex.europa.eu", "iate.europa.eu", "europa.eu", "publications.europa.eu"] }],
        },
    ];
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
