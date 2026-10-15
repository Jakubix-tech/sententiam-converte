using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Tests gegen echte Dienste im Netz. Laufen nur auf Anforderung: dotnet test -- --explicit only</summary>
public class LiveTests
{
    [Fact(Explicit = true)]
    public async Task FetcherReadsPage()
    {
        var text = await new PageFetcher().FetchTextAsync("https://de.wikipedia.org/wiki/Drehmoment", 4000, TestContext.Current.CancellationToken);
        Assert.NotNull(text);
        Assert.Contains("Drehmoment", text);
    }

    /// <summary>
    /// Prüft die mitgelieferten Fachquellen: erlaubt robots.txt das Lesen, und kommt lesbarer Text an?
    /// Gibt eine Tabelle aus; schlägt nicht fehl, damit alle Seiten geprüft werden.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task SpecialistSitesAreReadable()
    {
        var samples = Environment.GetEnvironmentVariable("SC_SITES") is { Length: > 0 } list
            ? list.Split(';').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1])
            : new Dictionary<string, string>
        {
            ["gesetze-im-internet.de"] = "https://www.gesetze-im-internet.de/bgb/__433.html",
            ["dejure.org"] = "https://dejure.org/gesetze/BGB/433.html",
            ["bundesgerichtshof.de"] = "https://www.bundesgerichtshof.de/DE/DasGericht/dasGericht_node.html",
            ["bundesjustizamt.de"] = "https://www.bundesjustizamt.de/DE/Home/Home_node.html",
            ["ris.bka.gv.at"] = "https://www.ris.bka.gv.at/GeltendeFassung.wxe?Abfrage=Bundesnormen&Gesetzesnummer=10001622",
            ["oesterreich.gv.at"] = "https://www.oesterreich.gv.at/themen/dokumente_und_recht.html",
            ["fedlex.admin.ch"] = "https://www.fedlex.admin.ch/eli/cc/24/233_245_233/de",
            ["bger.ch"] = "https://www.bger.ch/de/index.htm",
            ["legislation.gov.uk"] = "https://www.legislation.gov.uk/ukpga/1979/54/section/14",
            ["judiciary.uk"] = "https://www.judiciary.uk/about-the-judiciary/",
            ["gov.uk"] = "https://www.gov.uk/make-court-claim-for-money",
            ["lawcom.gov.uk"] = "https://lawcom.gov.uk/about-us/",
            ["law.cornell.edu"] = "https://www.law.cornell.edu/wex/consideration",
            ["uscode.house.gov"] = "https://uscode.house.gov/browse.xhtml",
            ["supremecourt.gov"] = "https://www.supremecourt.gov/about/about.aspx",
            ["uscourts.gov"] = "https://www.uscourts.gov/about-federal-courts",
            ["isap.sejm.gov.pl"] = "https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU19640160093",
            ["sn.pl"] = "https://www.sn.pl/osadzie/Strony/default.aspx",
            ["gov.pl"] = "https://www.gov.pl/web/sprawiedliwosc",
            ["trybunal.gov.pl"] = "https://trybunal.gov.pl/o-trybunale",
            ["legifrance.gouv.fr"] = "https://www.legifrance.gouv.fr/codes/article_lc/LEGIARTI000006441370",
            ["courdecassation.fr"] = "https://www.courdecassation.fr/la-cour-de-cassation",
            ["service-public.fr"] = "https://www.service-public.fr/particuliers/vosdroits/F1426",
            ["normattiva.it"] = "https://www.normattiva.it/",
            ["giustizia.it"] = "https://www.giustizia.it/giustizia/",
            ["boe.es"] = "https://www.boe.es/buscar/act.php?id=BOE-A-1889-4763",
            ["poderjudicial.es"] = "https://www.poderjudicial.es/cgpj/es/Poder-Judicial/",
            ["eur-lex.europa.eu"] = "https://eur-lex.europa.eu/legal-content/DE/TXT/?uri=CELEX:32016R0679",
            ["iate.europa.eu"] = "https://iate.europa.eu/home",
            ["curia.europa.eu"] = "https://curia.europa.eu/jcms/jcms/Jo2_6999/de/",
            ["awmf.org"] = "https://register.awmf.org/de/leitlinien/aktuelle-leitlinien",
            ["rki.de"] = "https://www.rki.de/DE/Themen/Infektionskrankheiten/infektionskrankheiten-node.html",
            ["gesund.bund.de"] = "https://gesund.bund.de/bluthochdruck",
            ["gesundheitsinformation.de"] = "https://www.gesundheitsinformation.de/bluthochdruck.html",
            ["bfarm.de"] = "https://www.bfarm.de/DE/Arzneimittel/_node.html",
            ["nhs.uk"] = "https://www.nhs.uk/conditions/high-blood-pressure-hypertension/",
            ["nice.org.uk"] = "https://www.nice.org.uk/guidance/ng136",
            ["ncbi.nlm.nih.gov"] = "https://www.ncbi.nlm.nih.gov/books/NBK539859/",
            ["medlineplus.gov"] = "https://medlineplus.gov/highbloodpressure.html",
            ["cdc.gov"] = "https://www.cdc.gov/high-blood-pressure/about/index.html",
            ["fda.gov"] = "https://www.fda.gov/drugs",
            ["pzh.gov.pl"] = "https://www.pzh.gov.pl/",
            ["mp.pl"] = "https://www.mp.pl/pacjent/nadcisnienie/",
            ["ema.europa.eu"] = "https://www.ema.europa.eu/en/about-us",
            ["who.int"] = "https://www.who.int/news-room/fact-sheets/detail/hypertension",
            ["electropedia.org"] = "https://www.electropedia.org/iev/iev.nsf/display?openform&ievref=151-11-01",
            ["iso.org"] = "https://www.iso.org/about",
            ["iec.ch"] = "https://www.iec.ch/who-we-are",
            ["din.de"] = "https://www.din.de/de/ueber-normen-und-standards",
            ["dke.de"] = "https://www.dke.de/de/ueber-uns",
            ["vdi.de"] = "https://www.vdi.de/ueber-uns",
            ["baua.de"] = "https://www.baua.de/DE/Themen/Arbeitsgestaltung/Arbeitsgestaltung_node.html",
            ["bsigroup.com"] = "https://www.bsigroup.com/en-GB/about-bsi/",
            ["hse.gov.uk"] = "https://www.hse.gov.uk/work-equipment-machinery/",
            ["ansi.org"] = "https://www.ansi.org/about/introduction",
            ["osha.gov"] = "https://www.osha.gov/machine-guarding",
            ["nist.gov"] = "https://www.nist.gov/about-nist",
            ["pkn.pl"] = "https://www.pkn.pl/",
            ["ciop.pl"] = "https://www.ciop.pl/",
            ["europa.eu"] = "https://european-union.europa.eu/institutions-law-budget/law_de",
            ["publications.europa.eu"] = "https://op.europa.eu/de/home",
        };
        var fetcher = new PageFetcher();
        var lines = new List<string>();
        await Parallel.ForEachAsync(samples, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken }, async (kv, ct) =>
        {
            var f = new PageFetcher();
            var text = await f.FetchTextAsync(kv.Value, 20000, ct);
            lock (lines) lines.Add($"{kv.Key}\t{(text == null ? "NEIN: " + f.LastSkipReason : $"ok {text.Length} Zeichen")}");
        });
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fachseiten-pruefung.txt"), lines.OrderBy(l => l));
        foreach (var l in lines.OrderBy(l => l)) TestContext.Current.TestOutputHelper?.WriteLine(l);
    }
}
