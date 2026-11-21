using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Quellengüte: Seiten nach Art einordnen (auch unbekannte), gewichtet belegen – ein Forum zählt, aber allein reicht es
/// nicht –, Gütestufen je Dossier, Sättigung als Abbruch, Rückmeldung aus dem Bestätigen und Ruf je Seite.
/// </summary>
public sealed class SourceQualityTests
{
    [Fact]
    public void PagesAreClassifiedByWhatTheyAre()
    {
        Assert.Equal(SourceKinds.Forum, SourceKinds.Classify("https://dict.leo.org/forum/viewUnsolvedquery.php?idThread=1"));
        Assert.Equal(SourceKinds.Dictionary, SourceKinds.Classify("https://dict.leo.org/englisch-deutsch/Gleitringdichtung"));
        Assert.Equal(SourceKinds.Forum, SourceKinds.Classify("https://www.proz.com/kudoz/german-to-english/mechanics/123"));
        Assert.Equal(SourceKinds.Termbank, SourceKinds.Classify("https://iate.europa.eu/entry/result/123/de-en"));
        Assert.Equal(SourceKinds.Law, SourceKinds.Classify("https://eur-lex.europa.eu/legal-content/DE/TXT/?uri=CELEX:32016R0679"));
        Assert.Equal(SourceKinds.Authority, SourceKinds.Classify("https://www.bmwk.bund.de/Redaktion/DE/Artikel/x.html"));
        Assert.Equal(SourceKinds.Standard, SourceKinds.Classify("https://www.din.de/de/mitwirken/normenausschuesse"));
        Assert.Equal(SourceKinds.Encyclopedia, SourceKinds.Classify("https://en.wikipedia.org/wiki/Mechanical_seal"));
        Assert.Equal(SourceKinds.Academic, SourceKinds.Classify("https://www.uni-stuttgart.de/x"));
        Assert.Equal(SourceKinds.Own, SourceKinds.Classify(@"C:\Kunde\Handbuch.pdf"));
        // unbekannte Seite: die Einschätzung der KI beim Lesen, sonst „Webseite“
        Assert.Equal(SourceKinds.Manufacturer, SourceKinds.Classify("https://pumpenwerk-beispiel.de/produkte", "manufacturer"));
        Assert.Equal(SourceKinds.Web, SourceKinds.Classify("https://pumpenwerk-beispiel.de/produkte", "irgendwas"));
    }

    [Fact]
    public void AForumCountsButIsNotEnoughOnItsOwn()
    {
        static bool Enough(params (string Url, string Kind)[] s) => SourceKinds.IsConclusive(SourceKinds.Support(s.Select(x => (x.Url, (string?)x.Kind, false))));

        Assert.True(Enough(("https://a.example/1", SourceKinds.Web), ("https://b.example/2", SourceKinds.Web)));           // wie bisher: zwei Seiten reichen
        Assert.False(Enough(("https://f1.example/t/1", SourceKinds.Forum), ("https://b.example/2", SourceKinds.Web)));      // Forum + Webseite: nicht
        Assert.True(Enough(("https://f1.example/t/1", SourceKinds.Forum), ("https://amt.gv.at/2", SourceKinds.Authority))); // Forum + Behörde: ja
        Assert.False(Enough(("https://f1.example/t/1", SourceKinds.Forum), ("https://f2.example/t/1", SourceKinds.Forum), ("https://f3.example/t/1", SourceKinds.Forum)));
        Assert.True(Enough(("https://f1.example/t/1", SourceKinds.Forum), ("https://f2.example/t/1", SourceKinds.Forum),
            ("https://f3.example/t/1", SourceKinds.Forum), ("https://f4.example/t/1", SourceKinds.Forum)));                // vier unabhängige Foren: echter Sprachgebrauch
        Assert.False(Enough(("https://a.example/1", SourceKinds.Authority), ("https://a.example/2", SourceKinds.Authority))); // dieselbe Website ist eine Quelle
    }

    [Fact]
    public void ACompanysOwnSiteIsTheBestSourceForItsOwnNames()
    {
        Assert.True(SourceKinds.IsOwnSiteOf("https://www.raindancer.com/en/products", "Raindancer"));
        Assert.True(SourceKinds.IsOwnSiteOf("https://ab-raindancer.de/x", "Raindancer"));
        Assert.True(SourceKinds.IsOwnSiteOf("https://raindancer.com/x", "Rain Dancer"));
        Assert.False(SourceKinds.IsOwnSiteOf("https://pumpen-shop.de/x", "Pumpe"));                     // nur ein Teil eines Wortes
        Assert.False(SourceKinds.IsOwnSiteOf("https://raindancer.com/x", "Rai"));                       // zu kurz

        // eine Firmen-Webseite zählt wie ein Hersteller
        Assert.Equal(SourceKinds.Company, SourceKinds.Classify("https://pumpenwerk-beispiel.de/ueber-uns", "company"));
        Assert.Equal(0.85, SourceKinds.Weight(SourceKinds.Company));
        // die eigene Seite wie eine Behörde: mit nur einem Forum dazu schon eindeutig belegt, Güte A
        var d = new TermDossier
        {
            Expression = "Raindancer", TargetLanguage = "en-GB", Recommended = "Raindancer",
            Sources = [new SourceRef("en-GB", "Raindancer", "https://raindancer.com/en") { Quote = "Raindancer irrigation control.", Kind = SourceKinds.Company },
                       new SourceRef("en-GB", "Farmers", "https://forum.farmers.example/t/1") { Quote = "the Raindancer system works", Kind = SourceKinds.Forum }],
        };
        Assert.Equal("A", DossierStore.Grade(d));
        // dieselben Belege für einen anderen Ausdruck: die Firmen-Webseite zählt nur wie ein Hersteller – mit Forum nicht eindeutig
        d.Expression = "Bewässerungssteuerung";
        Assert.Equal("C", DossierStore.Grade(d));
    }

    private static TermDossier Dossier(params SourceRef[] sources) =>
        new() { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "mechanical seal", Sources = [.. sources] };

    private static SourceRef Src(string url, string? kind = null) => new("en-GB", "Seals", url) { Quote = "A mechanical seal keeps the pump tight.", Kind = kind };

    [Fact]
    public void EveryDossierGetsAGrade()
    {
        Assert.Equal("A", DossierStore.Grade(Dossier(Src("https://www.din.de/x"), Src("https://pumps.example/a"))));                  // Norm + Webseite
        Assert.Equal("B", DossierStore.Grade(Dossier(Src("https://pumps.example/a", SourceKinds.Web), Src("https://seals.example/b", SourceKinds.Web))));
        Assert.Equal("C", DossierStore.Grade(Dossier(Src("https://pumps.example/a"))));                                               // nur eine Quelle
        Assert.Equal("C", DossierStore.Grade(Dossier(Src("https://forum.example/t/1", SourceKinds.Forum), Src("https://pumps.example/a", SourceKinds.Web))));
        Assert.Equal("D", DossierStore.Grade(new TermDossier { Expression = "GLRD", TargetLanguage = "en-GB", Recommended = "seal" }));
        Assert.Equal("Güte A – eindeutig belegt, mit starker Quelle", DossierStore.GradeText(Dossier(Src("https://www.din.de/x"), Src("https://pumps.example/a"))));
    }

    private sealed class Model : IChatModel
    {
        private int _n;
        public string DisplayName => "Skript";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            return Task.FromResult(p.Contains("Explain what the term means IN THIS CONTEXT")
                ? """{"meaning": "Dichtung", "candidates": [{"term": "mechanical seal", "type": "functional"}], "queries": {"de-DE": [], "en-GB": ["nichts 0"]}}"""
                : p.Contains("is not conclusive yet") ? $$"""{"queries": {"en-GB": ["nichts {{++_n}}"]}, "candidates": []}"""
                : p.Contains("Find up to 4 passages") ? """{"items": []}"""
                : """{"source": {"term": "Gleitringdichtung"}, "candidates": [{"term": "mechanical seal", "equivalence": "Full"}], "recommendation": {"term": "mechanical seal"}}""");
        }
    }

    private sealed class Empty : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) => Task.FromResult(new List<SearchResult>());
    }

    [Fact]
    public async Task ResearchStopsWhenNothingNewTurnsUp()
    {
        var r = await new TermComparisonAgent(new AgentContext { Model = new Model(), Search = new Empty() })
            .RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB"), 3, TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Rounds);                                                          // nicht alle 6 Runden: zwei ohne Neues reichen
        Assert.False(r.Conclusive);
        Assert.Contains(r.Warnings, w => w.Contains("brachten zwei Runden nichts Neues"));
    }

    [Fact]
    public void ConfirmingTeachesGradesAndSites()
    {
        using var dir = new TempDir();
        ResearchFeedback.UseFolderHere(dir.File("rm"));
        SiteCatalog.UseFileHere(dir.File("quellen.json"));
        var d = Dossier(Src("https://pumps.example/a", SourceKinds.Manufacturer), Src("https://seals.example/b", SourceKinds.Web));
        d.Candidates = [new() { Term = "mechanical seal" }, new() { Term = "face seal" }];

        Assert.True(ResearchFeedback.Record("p|f|1|glrd", d, accepted: true));
        Assert.False(ResearchFeedback.Record("p|f|1|glrd", d, accepted: true));                        // dieselbe Rückmeldung zählt nur einmal
        Assert.True(ResearchFeedback.Record("p|f|2|glrd", d, accepted: true));
        var pumps = SiteCatalog.Load().Single(e => e.Domain == "pumps.example");
        Assert.Equal((2, 0), (pumps.Accepted, pumps.Rejected));

        // später ersetzt: die frühere Rückmeldung wird zurückgenommen
        Assert.True(ResearchFeedback.Record("p|f|2|glrd", d, accepted: false));
        pumps = SiteCatalog.Load().Single(e => e.Domain == "pumps.example");
        Assert.Equal((1, 1), (pumps.Accepted, pumps.Rejected));
        Assert.Equal([new GradeCalibration("A", 2, 1)], ResearchFeedback.Calibration());
        Assert.Equal("A 1/2", ResearchFeedback.Summary());

        // Ruf: eine Seite, deren Benennungen dauernd ersetzt werden, fällt aus der gezielten Suche
        SiteCatalog.RecordEvidence([Src("https://bad.example/x", SourceKinds.Forum) with { Language = "en-GB" }], ["Pumpen"]);
        Assert.Contains("bad.example", SiteCatalog.SitesFor("en-GB", ["Pumpen"]));
        SiteCatalog.RecordFeedback(["bad.example"], accepted: false);
        Assert.DoesNotContain("bad.example", SiteCatalog.SitesFor("en-GB", ["Pumpen"]));
    }
}
