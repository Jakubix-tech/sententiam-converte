namespace SententiamConverte.Core.Agents;

/// <summary>
/// Welche Art Quelle eine Seite ist – erkannt an ihr selbst (Adresse, Aufbau, Herausgeber), nicht an einer Liste bekannter
/// Seiten; so lässt sich auch eine nie gesehene Seite in einem neuen Fachgebiet einordnen. Die Rangfolge der Arten ist in
/// fast jedem Fachgebiet dieselbe: Termbank, Gesetz, Norm, Behörde vor Fachverband, Hersteller, Hochschule, Wörterbuch;
/// diese vor Lexikon, Presse und allgemeinen Seiten; Blog und Forum zuletzt. Ein Forum zählt trotzdem – manchmal steht dort
/// genau die Auskunft (Umgangssprache, neue Technik, Übersetzerforen) –, nur schwächer: allein reicht es nicht, mit einer
/// starken Quelle oder als einer von mehreren unabhängigen Foren schon. Eine Firmen-Webseite zählt wie ein Hersteller; für
/// die eigenen Namen, Produkte und Begriffe der Firma ist ihre eigene Seite (der Name steckt in der Adresse) die stärkste
/// Quelle überhaupt – so stark wie eine Behörde.
/// </summary>
public static class SourceKinds
{
    public const string Termbank = "termbank";
    public const string Law = "law";
    public const string Standard = "standard";
    public const string Authority = "authority";
    public const string Association = "association";
    public const string Manufacturer = "manufacturer";
    public const string Company = "company";
    public const string Academic = "academic";
    public const string Dictionary = "dictionary";
    public const string Encyclopedia = "encyclopedia";
    public const string News = "news";
    public const string Web = "web";
    public const string Blog = "blog";
    public const string Forum = "forum";
    public const string Own = "own";

    /// <summary>Ab diesem Gewicht (aus mindestens zwei unabhängigen Websites) gilt ein Kandidat als eindeutig belegt.</summary>
    public const double Conclusive = 1.4;

    /// <summary>
    /// Gewicht einer Quelle: Termbank, Gesetz, Norm, Behörde 1,1 · Fachverband, Hersteller, Hochschule, Wörterbuch 0,85 ·
    /// eigene Fachtexte 1,0 · Lexikon, Presse, nicht eingeordnet 0,7 · Blog 0,45 · Forum 0,35. Zwei gewöhnliche Seiten
    /// reichen also wie bisher; ein Forum braucht eine starke Quelle dazu oder drei weitere Foren.
    /// </summary>
    public static double Weight(string? kind) => kind switch
    {
        Termbank or Law or Standard or Authority => 1.1,
        Own => 1.0,
        Association or Manufacturer or Company or Academic or Dictionary => 0.85,
        Blog => 0.45,
        Forum => 0.35,
        _ => 0.7,
    };

    /// <summary>Stufe 1 (stark) bis 4 (Forum, Blog) – für die Gütestufe des Dossiers.</summary>
    public static int Tier(string? kind) => Weight(kind) switch
    {
        >= 1.0 => 1,
        >= 0.85 => 2,
        >= 0.7 => 3,
        _ => 4,
    };

    /// <summary>Für Anzeige und Prompt: „Behörde“, „Forum“ …</summary>
    public static string Name(string? kind) => kind switch
    {
        Termbank => "Termbank/Glossar",
        Law => "Gesetz/Recht",
        Standard => "Norm",
        Authority => "Behörde",
        Association => "Fachverband",
        Manufacturer => "Hersteller",
        Company => "Firmen-Webseite",
        Academic => "Hochschule/Fachzeitschrift",
        Dictionary => "Wörterbuch",
        Encyclopedia => "Lexikon",
        News => "Presse",
        Blog => "Blog",
        Forum => "Forum",
        Own => "eigene Fachtexte",
        _ => "Webseite",
    };

    private static readonly string[] Kinds = [Termbank, Law, Standard, Authority, Association, Manufacturer, Company, Academic, Dictionary, Encyclopedia, News, Web, Blog, Forum];

    /// <summary>
    /// Die eigene Seite einer Firma oder Einrichtung zu einem Ausdruck: Der Name ist ein ganzer Teil der Adresse
    /// („Raindancer“ → raindancer.com, ab-raindancer.de, „Rain Dancer“ → raindancer.com). Für ihre eigenen Namen, Produkte und
    /// Begriffe die stärkste Quelle. Ein Wort, das nur in einem längeren Teil steckt, genügt nicht – „Pumpe“ macht
    /// pumpen-shop.de nicht zur offiziellen Seite.
    /// </summary>
    public static bool IsOwnSiteOf(string url, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        static string Letters(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var name = Letters(subject);
        if (name.Length < 4) return false;
        var host = Resources.SiteCatalog.DomainOf(url) ?? "";
        var label = host.Contains('.') ? host[..host.LastIndexOf('.')] : host;       // ohne Endung: raindancer.com → raindancer
        return Letters(label) == name || label.Split('-', '.', '_').Any(part => Letters(part) == name);
    }

    /// <summary>
    /// Art einer Seite: zuerst an der Adresse (sicher erkennbar: Foren, Wikipedia, Gesetzesportale, Normungsstellen,
    /// amtliche Adressen, Hochschulen, Wörterbücher), sonst die Einschätzung der KI beim Lesen, sonst „Webseite“.
    /// </summary>
    public static string Classify(string url, string? aiKind = null)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Own;
        var u = url.ToLowerInvariant();
        var host = Resources.SiteCatalog.DomainOf(url) ?? "";
        var full = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host.ToLowerInvariant() : host;
        bool HostIs(params string[] domains) => domains.Any(d => host == d || host.EndsWith("." + d));
        bool HostEnds(params string[] suffixes) => suffixes.Any(s => full.EndsWith(s));

        // Foren zuerst: leo.org/forum ist ein Forum, leo.org ein Wörterbuch
        if (full.StartsWith("forum.") || full.StartsWith("forums.") || full.StartsWith("community.") || full.StartsWith("discuss.")
            || u.Contains("/forum") || u.Contains("/thread") || u.Contains("/showthread") || u.Contains("/topic/") || u.Contains("/t/")
            || HostIs("proz.com", "reddit.com", "stackexchange.com", "stackoverflow.com", "quora.com", "gutefrage.net", "wer-weiss-was.de",
                "translatorscafe.com", "wordreference.com") && (u.Contains("forum") || u.Contains("/kudoz") || u.Contains("/r/") || u.Contains("/questions/") || HostIs("quora.com", "gutefrage.net", "wer-weiss-was.de")))
            return Forum;
        if (full.StartsWith("blog.") || u.Contains("/blog/") || HostIs("medium.com", "wordpress.com", "blogspot.com", "substack.com")) return Blog;
        if (HostIs("iate.europa.eu", "termiumplus.gc.ca", "unterm.un.org", "wipo.int") && (u.Contains("term") || HostIs("iate.europa.eu"))
            || u.Contains("termdat") || u.Contains("glossar") || u.Contains("glossary") || u.Contains("terminolog"))
            return Termbank;
        if (HostIs("eur-lex.europa.eu", "gesetze-im-internet.de", "ris.bka.gv.at", "fedlex.admin.ch", "legifrance.gouv.fr", "isap.sejm.gov.pl",
                "legislation.gov.uk", "boe.es", "normattiva.it", "dejure.org", "buzer.de", "jusline.at"))
            return Law;
        if (HostIs("iso.org", "din.de", "dinmedia.de", "beuth.de", "cen.eu", "cenelec.eu", "etsi.org", "iec.ch", "astm.org", "austrian-standards.at",
                "snv.ch", "afnor.org", "pkn.pl", "bsigroup.com", "vde.com", "vdi.de"))
            return Standard;
        if (HostIs("wikipedia.org", "britannica.com", "brockhaus.de")) return Encyclopedia;
        if (HostIs("wiktionary.org", "duden.de", "dwds.de", "pons.com", "dict.cc", "leo.org", "langenscheidt.com", "merriam-webster.com",
                "collinsdictionary.com", "larousse.fr", "cnrtl.fr", "sjp.pwn.pl", "dictionary.cambridge.org", "oed.com", "wordreference.com", "linguee.de"))
            return Dictionary;
        if (HostEnds(".gov", ".gov.uk", ".gv.at", ".admin.ch", ".bund.de", ".gouv.fr", ".gov.pl", ".gob.es", ".gc.ca", ".europa.eu", ".gov.au")
            || HostIs("bund.de", "europa.eu", "who.int", "un.org", "oecd.org"))
            return Authority;
        if (HostEnds(".edu", ".ac.uk", ".ac.at", ".edu.pl") || full.Contains("uni-") || full.Contains("univ") || full.Contains("universit")
            || HostIs("springer.com", "sciencedirect.com", "researchgate.net", "jstor.org", "ncbi.nlm.nih.gov"))
            return Academic;
        var said = aiKind?.Trim().ToLowerInvariant();
        return said != null && Kinds.Contains(said) ? said : Web;
    }

    /// <summary>
    /// Gewichteter Beleg eines Kandidaten: je unabhängige Website das stärkste Gewicht, zusammengezählt. Eindeutig, wenn
    /// mindestens zwei Websites und mindestens <see cref="Conclusive"/>.
    /// </summary>
    /// <param name="subject">Der gesuchte Ausdruck: Ist eine Seite dessen eigene (Name in der Adresse), zählt sie wie eine Behörde.</param>
    public static (int Sites, double Weight) Support(IEnumerable<(string Url, string? Kind, bool Local)> sources, string? subject = null)
    {
        var bySite = sources.GroupBy(s => s.Local ? s.Url.Split('#')[0] : Resources.SiteCatalog.DomainOf(s.Url) ?? s.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Max(s => s.Local ? Weight(Own) : IsOwnSiteOf(s.Url, subject) ? Math.Max(Weight(s.Kind), Weight(Authority)) : Weight(s.Kind))).ToList();
        return (bySite.Count, Math.Round(bySite.Sum(), 2));
    }

    public static bool IsConclusive((int Sites, double Weight) support) => support.Sites >= 2 && support.Weight >= Conclusive;
}
