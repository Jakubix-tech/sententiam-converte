using System.Globalization;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Resources;

/// <summary>Art eines Hilfsmittels.</summary>
public enum ToolKind { Termbase, Dictionary, Corpus, Law, Style, Reference }

/// <summary>Wie man es nutzt.</summary>
public enum ToolAccess
{
    /// <summary>Im Programm eingebaut (Agenten, Prüfung, Datenbanken).</summary>
    Integrated,
    /// <summary>Herunterladen und als Datenbank anbinden („TM &amp; Terminologie“ → Freie Sprachdaten).</summary>
    Download,
    /// <summary>Im Browser öffnen (auch mit dem markierten Wort).</summary>
    Browser,
}

/// <summary>
/// Ein Hilfsmittel für Übersetzer. <see cref="SearchUrl"/> schlägt ein Wort direkt nach: {q} = Suchbegriff,
/// {lang} = Sprache des Begriffs (de), {lang3} = dreistellig (deu), {src3}/{tgt3} = Sprachpaar dreistellig.
/// </summary>
public sealed record TranslatorTool(string Id, string Name, string Description, ToolKind Kind, ToolAccess Access, string Url,
    string? SearchUrl, string[] Languages, string[] Topics, string Provider, string License)
{
    public bool ForAllLanguages => Languages.Contains("*");

    public bool Covers(string language) => ForAllLanguages || Languages.Contains(Model.Languages.Primary(language));

    /// <summary>Adresse zum Nachschlagen eines Begriffs (ohne Suchadresse: die Startseite).</summary>
    public string LookupUrl(string term, string termLanguage, string? otherLanguage = null)
    {
        if (SearchUrl == null || string.IsNullOrWhiteSpace(term)) return Url;
        var lang = Model.Languages.Primary(termLanguage);
        return SearchUrl
            .Replace("{q}", Uri.EscapeDataString(term.Trim()))
            .Replace("{lang}", lang)
            .Replace("{lang3}", Three(termLanguage))
            .Replace("{src3}", Three(termLanguage))
            .Replace("{tgt3}", otherLanguage == null ? "" : Three(otherLanguage));
    }

    private static string Three(string language)
    {
        try { return CultureInfo.GetCultureInfo(Model.Languages.Primary(language)).ThreeLetterISOLanguageName; }
        catch (CultureNotFoundException) { return Model.Languages.Primary(language); }
    }
}

/// <summary>Ein Vorschlag für ein Projekt: Hilfsmittel, für welche Projektsprachen (leer = alle Sprachen) und welches Fachgebiet.</summary>
public sealed record ToolSuggestion(TranslatorTool Tool, IReadOnlyList<string> Languages, string? Topic, int Score);

/// <summary>
/// Katalog der Hilfsmittel: Termbanken, Wörterbücher, Korpora, Rechtsquellen und Stilratgeber öffentlicher und
/// gemeinnütziger Herausgeber. Dienste kommerzieller Anbieter erscheinen nur, wenn das Programm sie über ihre offizielle
/// Schnittstelle anbindet. Alles, was nicht eingebaut ist, öffnet der Browser – das Programm liest diese Seiten nicht aus.
/// </summary>
public static class TranslatorTools
{
    public const string Law = "Recht und Verwaltung", Medicine = "Medizin und Gesundheit", Tech = "Technik und Software",
        Economy = "Wirtschaft und Finanzen", General = "Allgemein und Alltag";

    private static readonly string[] All_ = ["*"];

    public static readonly IReadOnlyList<TranslatorTool> All =
    [
        // ---------- Termbanken ----------
        new("iate", "IATE", "Terminologiedatenbank der EU-Institutionen, alle Amtssprachen der EU; als Datenbank ladbar und online durchsuchbar.",
            ToolKind.Termbase, ToolAccess.Download, "https://iate.europa.eu/home", null, All_, [Law, Economy, Medicine, Tech], "Europäische Union", "EU-Wiederverwendung, Quelle angeben"),
        new("termium", "TERMIUM Plus", "Termbank der kanadischen Regierung: Englisch und Französisch, teils Spanisch und Portugiesisch, 23 Fachgebiete; als Datenbank ladbar.",
            ToolKind.Termbase, ToolAccess.Download, "https://www.btb.termiumplus.gc.ca/", null, ["en", "fr"], [Law, Medicine, Tech, Economy], "Regierung von Kanada", "Open Government Licence – Canada"),
        new("termdat", "TERMDAT", "Terminologie der Schweizer Bundesverwaltung: Recht und Verwaltung auf Deutsch, Französisch, Italienisch, Rätoromanisch und Englisch.",
            ToolKind.Termbase, ToolAccess.Browser, "https://www.termdat.bk.admin.ch/", null, ["de", "fr", "it", "rm", "en"], [Law, Economy], "Schweizerische Bundeskanzlei", "Quelle angeben"),
        new("franceterme", "FranceTerme", "Amtlich empfohlene französische Fachbegriffe mit englischer Entsprechung – statt Anglizismen.",
            ToolKind.Termbase, ToolAccess.Browser, "https://www.culture.fr/franceterme", null, ["fr", "en"], [Tech, Economy, Medicine, General], "Kulturministerium Frankreich", "Licence Ouverte"),
        new("gdt", "Vitrine linguistique (OQLF)", "Großes terminologisches Wörterbuch und Sprachberatung des Office québécois de la langue française (Französisch–Englisch).",
            ToolKind.Termbase, ToolAccess.Browser, "https://vitrinelinguistique.oqlf.gouv.qc.ca/", null, ["fr", "en"], [Tech, Law, Medicine, Economy, General], "Office québécois de la langue française", "Nutzungsbedingungen der Seite"),
        new("unterm", "UNTERM", "Terminologie der Vereinten Nationen: Arabisch, Chinesisch, Englisch, Französisch, Russisch, Spanisch und weitere.",
            ToolKind.Termbase, ToolAccess.Browser, "https://unterm.un.org/unterm2/en/", "https://unterm.un.org/unterm2/en/search?searchTerm={q}",
            ["en", "fr", "es", "ru", "ar", "zh", "de", "pt"], [Law, Economy], "Vereinte Nationen", "Nutzungsbedingungen der Seite"),
        new("eurovoc", "EuroVoc", "Mehrsprachiger Thesaurus der EU für Politik, Recht und Verwaltung, alle Amtssprachen.",
            ToolKind.Termbase, ToolAccess.Browser, "https://eur-lex.europa.eu/browse/eurovoc.html", null, All_, [Law, Economy], "Amt für Veröffentlichungen der EU", "EU-Wiederverwendung"),

        // ---------- Wörterbücher ----------
        new("dwds", "DWDS", "Digitales Wörterbuch der deutschen Sprache: Bedeutungen, Beispiele, Häufigkeit; im Programm für die Agenten eingebaut.",
            ToolKind.Dictionary, ToolAccess.Integrated, "https://www.dwds.de/", "https://www.dwds.de/wb/{q}", ["de"], [General], "Berlin-Brandenburgische Akademie der Wissenschaften", "Nutzungsbedingungen des DWDS"),
        new("wiktionary", "Wiktionary", "Freies Wörterbuch für alle Sprachen mit Übersetzungen, Beugung und Aussprache.",
            ToolKind.Dictionary, ToolAccess.Browser, "https://www.wiktionary.org/", "https://{lang}.wiktionary.org/wiki/{q}", All_, [General], "Wikimedia", "CC BY-SA"),
        new("cnrtl", "CNRTL – TLFi", "Trésor de la langue française: großes französisches Wörterbuch mit Definitionen, Synonymen und Herkunft.",
            ToolKind.Dictionary, ToolAccess.Browser, "https://www.cnrtl.fr/", "https://www.cnrtl.fr/definition/{q}", ["fr"], [General], "CNRS / ATILF", "Nutzungsbedingungen der Seite"),
        new("wsjp", "Wielki słownik języka polskiego", "Großes Wörterbuch der polnischen Sprache der Polnischen Akademie der Wissenschaften.",
            ToolKind.Dictionary, ToolAccess.Browser, "https://wsjp.pl/", null, ["pl"], [General], "Polnische Akademie der Wissenschaften", "Nutzungsbedingungen der Seite"),
        new("rae", "Diccionario de la lengua española", "Wörterbuch der Real Academia Española.",
            ToolKind.Dictionary, ToolAccess.Browser, "https://dle.rae.es/", "https://dle.rae.es/{q}", ["es"], [General], "Real Academia Española", "Nutzungsbedingungen der Seite"),
        new("pledari", "Pledari Grond", "Wörterbuch Deutsch–Rätoromanisch (Rumantsch Grischun und die Idiome) der Lia Rumantscha.",
            ToolKind.Dictionary, ToolAccess.Browser, "https://www.pledarigrond.ch/", null, ["rm", "de"], [General], "Lia Rumantscha", "Nutzungsbedingungen der Seite"),

        // ---------- Korpora und Konkordanz ----------
        new("opus", "OPUS (freie Sprachdaten)", "Parallele Texte für über 1000 Sprachpaare; im Programm als TM ladbar – die Konkordanz läuft dann auf diesem Rechner.",
            ToolKind.Corpus, ToolAccess.Download, "https://opus.nlpl.eu/", null, All_, [Law, Medicine, Tech, Economy, General], "Universität Helsinki", "je Datensatz verschieden"),
        new("tatoeba", "Tatoeba", "Sammlung übersetzter Beispielsätze (eher Alltagssprache) in vielen Sprachen.",
            ToolKind.Corpus, ToolAccess.Browser, "https://tatoeba.org/", "https://tatoeba.org/de/sentences/search?query={q}&from={src3}&to={tgt3}", All_, [General], "Tatoeba", "CC BY 2.0 FR"),
        new("dwds-korpora", "DWDS-Korpora", "Deutsche Textkorpora: wie ein Wort oder eine Wendung tatsächlich gebraucht wird.",
            ToolKind.Corpus, ToolAccess.Browser, "https://www.dwds.de/", "https://www.dwds.de/r/?q={q}", ["de"], [General], "Berlin-Brandenburgische Akademie der Wissenschaften", "Nutzungsbedingungen des DWDS"),
        new("nkjp", "NKJP", "Nationalkorpus der polnischen Sprache: Wendungen im Gebrauch finden.",
            ToolKind.Corpus, ToolAccess.Browser, "https://pelcra-nkjp.clarin-pl.eu/", null, ["pl"], [General], "Narodowy Korpus Języka Polskiego", "Nutzungsbedingungen der Seite"),

        // ---------- Rechtsquellen ----------
        new("eurlex", "EUR-Lex", "EU-Recht und Rechtsprechung in allen Amtssprachen, Texte nebeneinander vergleichbar; im Programm für die Agenten eingebaut.",
            ToolKind.Law, ToolAccess.Integrated, "https://eur-lex.europa.eu/", "https://eur-lex.europa.eu/search.html?scope=EURLEX&text={q}&lang={lang}&type=quick", All_, [Law, Economy], "Europäische Union", "EU-Wiederverwendung"),
        new("curia", "CURIA", "Rechtsprechung des Gerichtshofs der EU.",
            ToolKind.Law, ToolAccess.Browser, "https://curia.europa.eu/", null, All_, [Law], "Gerichtshof der EU", "EU-Wiederverwendung"),
        new("gii", "Gesetze im Internet", "Bundesrecht Deutschland; im Programm für die Agenten eingebaut.",
            ToolKind.Law, ToolAccess.Integrated, "https://www.gesetze-im-internet.de/", null, ["de"], [Law], "Bundesministerium der Justiz", "amtliche Werke"),
        new("fedlex", "Fedlex", "Bundesrecht der Schweiz auf Deutsch, Französisch, Italienisch, wichtige Erlasse auch Rätoromanisch.",
            ToolKind.Law, ToolAccess.Browser, "https://www.fedlex.admin.ch/", null, ["de", "fr", "it", "rm"], [Law], "Schweizerische Eidgenossenschaft", "amtliche Werke"),
        new("ris", "RIS", "Rechtsinformationssystem des Bundes Österreich.",
            ToolKind.Law, ToolAccess.Browser, "https://www.ris.bka.gv.at/", null, ["de"], [Law], "Bundeskanzleramt Österreich", "amtliche Werke"),
        new("isap", "ISAP", "Internetowy System Aktów Prawnych: polnisches Recht; im Programm für die Agenten eingebaut.",
            ToolKind.Law, ToolAccess.Integrated, "https://isap.sejm.gov.pl/", null, ["pl"], [Law], "Sejm der Republik Polen", "amtliche Werke"),
        new("legifrance", "Légifrance", "Französisches Recht und Rechtsprechung.",
            ToolKind.Law, ToolAccess.Browser, "https://www.legifrance.gouv.fr/", null, ["fr"], [Law], "Französische Republik", "Licence Ouverte"),
        new("legislation-uk", "legislation.gov.uk", "Recht des Vereinigten Königreichs; im Programm für die Agenten eingebaut.",
            ToolKind.Law, ToolAccess.Integrated, "https://www.legislation.gov.uk/", null, ["en"], [Law], "The National Archives", "Open Government Licence"),
        new("normattiva", "Normattiva", "Italienisches Recht.",
            ToolKind.Law, ToolAccess.Browser, "https://www.normattiva.it/", null, ["it"], [Law], "Italienische Republik", "amtliche Werke"),
        new("boe", "BOE", "Boletín Oficial del Estado: spanisches Recht.",
            ToolKind.Law, ToolAccess.Browser, "https://www.boe.es/", null, ["es"], [Law], "Königreich Spanien", "amtliche Werke"),

        // ---------- Stil und Sprachberatung ----------
        new("eu-style", "Interinstitutioneller Stilleitfaden", "Schreibregeln der EU-Institutionen für alle Amtssprachen (Zahlen, Daten, Abkürzungen, Ländernamen).",
            ToolKind.Style, ToolAccess.Browser, "https://style-guide.europa.eu/", null, All_, [Law, Economy, General], "Amt für Veröffentlichungen der EU", "EU-Wiederverwendung"),
        new("rechtschreibrat", "Rat für deutsche Rechtschreibung", "Amtliches Regelwerk der deutschen Rechtschreibung.",
            ToolKind.Style, ToolAccess.Browser, "https://www.rechtschreibrat.com/", null, ["de"], [General], "Rat für deutsche Rechtschreibung", "amtliches Regelwerk"),
        new("academie", "Académie française – Dire, ne pas dire", "Sprachberatung der Académie française: Zweifelsfälle und Anglizismen.",
            ToolKind.Style, ToolAccess.Browser, "https://www.academie-francaise.fr/dire-ne-pas-dire", null, ["fr"], [General], "Académie française", "Nutzungsbedingungen der Seite"),
        new("rjp", "Rada Języka Polskiego", "Rat für die polnische Sprache: Stellungnahmen und Sprachberatung.",
            ToolKind.Style, ToolAccess.Browser, "https://rjp.pan.pl/", null, ["pl"], [General], "Polnische Akademie der Wissenschaften", "Nutzungsbedingungen der Seite"),
        new("fundeu", "FundéuRAE", "Spanische Sprachberatung zu Zweifelsfällen und aktuellem Sprachgebrauch.",
            ToolKind.Style, ToolAccess.Browser, "https://www.fundeu.es/", null, ["es"], [General], "Fundación del Español Urgente", "Nutzungsbedingungen der Seite"),
        new("lia-rumantscha", "Lia Rumantscha", "Dachorganisation des Rätoromanischen: Sprachberatung, Rechtschreibung und Wörterbücher.",
            ToolKind.Style, ToolAccess.Browser, "https://www.liarumantscha.ch/", null, ["rm"], [General], "Lia Rumantscha", "Nutzungsbedingungen der Seite"),

        // ---------- Nachschlagen allgemein ----------
        new("wikipedia", "Wikipedia", "Sachwissen und Fachbegriffe; die Sprachversionen eines Artikels führen oft zum passenden Begriff in der Zielsprache.",
            ToolKind.Reference, ToolAccess.Integrated, "https://www.wikipedia.org/", "https://{lang}.wikipedia.org/w/index.php?search={q}", All_, [General, Tech, Medicine, Law, Economy], "Wikimedia", "CC BY-SA"),
    ];

    public static TranslatorTool? ById(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Fachgebiet eines Projekts (freier Text) einer Kategorie zuordnen.</summary>
    public static string? TopicCategory(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;
        var t = topic.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(t.Contains);
        if (Has("recht", "jur", "vertrag", "gesetz", "gericht", "verwaltung", "behörde", "law", "legal", "prawo", "droit", "notar")) return Law;
        if (Has("medizin", "pharma", "gesund", "klinik", "arzt", "medic", "health", "medyc")) return Medicine;
        if (Has("technik", "maschin", "software", "it", "elektr", "bau", "industrie", "tech", "engineer", "auto")) return Tech;
        if (Has("wirtschaft", "finanz", "bank", "steuer", "versicherung", "handel", "marketing", "econom", "financ", "ekonom")) return Economy;
        return General;
    }

    /// <summary>Passende Hilfsmittel für ein Sprachpaar und Fachgebiet, die besten zuerst.</summary>
    public static List<ToolSuggestion> Suggest(string sourceLanguage, IEnumerable<string> targetLanguages, string? topic, int max = 12)
    {
        var targets = targetLanguages.Select(Model.Languages.Primary).Distinct().ToList();
        var source = Model.Languages.Primary(sourceLanguage);
        var category = TopicCategory(topic);
        var list = new List<ToolSuggestion>();
        foreach (var tool in All)
        {
            var languages = tool.ForAllLanguages ? [] : tool.Languages.Where(l => l == source || targets.Contains(l)).ToList();
            if (!tool.ForAllLanguages && languages.Count == 0) continue;
            // Ladbare Daten für ein Sprachpaar (TERMIUM: Englisch–Französisch) nur, wenn das Projekt dieses Paar hat
            if (tool.Access == ToolAccess.Download && !tool.ForAllLanguages && !(languages.Contains(source) && languages.Any(targets.Contains))) continue;
            var score = tool.ForAllLanguages ? 2 : 3 + (languages.Contains(source) && languages.Any(targets.Contains) ? 3 : 0);
            var topicFits = category != null && tool.Topics.Contains(category);
            if (topicFits) score += category == General ? 1 : 3;
            if (tool.Kind == ToolKind.Law && category is not (Law or Economy)) score -= 3;      // Rechtsquellen nur, wenn es passt
            if (tool.Access != ToolAccess.Browser) score += 1;                                  // im Programm nutzbar
            list.Add(new ToolSuggestion(tool, languages, topicFits && category != General ? category : null, score));
        }
        var ranked = list.Where(s => s.Score > 1).OrderByDescending(s => s.Score).ThenBy(s => s.Tool.Name, StringComparer.OrdinalIgnoreCase).ToList();
        // Jede Projektsprache bekommt ihr eigenes Wörterbuch – sonst verdrängen Quellen für viele Sprachen die für seltene
        var chosen = new List<ToolSuggestion>();
        foreach (var language in targets.Prepend(source).Distinct())
            if (ranked.Where(s => s.Tool.Kind == ToolKind.Dictionary && !s.Tool.ForAllLanguages && s.Languages.Contains(language) && !chosen.Contains(s))
                    .OrderBy(s => s.Tool.Languages.Length).FirstOrDefault() is { } dictionary)       // das speziellste Wörterbuch der Sprache
                chosen.Add(dictionary);
        chosen.AddRange(ranked.Where(s => !chosen.Contains(s)).Take(Math.Max(0, max - chosen.Count)));
        return chosen.OrderByDescending(s => s.Score).ThenBy(s => s.Tool.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
