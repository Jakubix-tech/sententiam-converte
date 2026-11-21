using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>Anschriften erkennen; Geschütztes und bloße Datumsangaben werden nicht recherchiert und gehen in keine Suchanfrage.</summary>
[Collection("BuiltInAi")]
public sealed class PrivacyGuardTests : IDisposable
{
    private readonly PrivacySettings _old = PrivacyContext.Current;

    public void Dispose()
    {
        PrivacyContext.Current = _old;
        PrivacyGuard.Values = [];
    }

    private static List<string> Addresses(string text) =>
        SensitiveData.Find(text, new PrivacySettings()).Where(f => f.Kind == SensitiveKind.Address).Select(f => f.Value).ToList();

    private static List<Segment> Segments(string text)
    {
        var paragraph = new Paragraph { Locator = "p0" };
        new Segmenter("de-DE").Segment(paragraph, text);
        return paragraph.Segments;
    }

    [Fact]
    public void AddressesAreFound()
    {
        Assert.Equal(["Bahnhofstraße 3a, 12345 Musterstadt"], Addresses("Anschrift: Bahnhofstraße 3a, 12345 Musterstadt"));
        Assert.Equal(["Hauptstr. 12"], Addresses("Wir sitzen in der Hauptstr. 12 im Erdgeschoss."));
        Assert.Equal(["Lange Straße 5"], Addresses("Lange Straße 5"));
        Assert.Equal(["ul. Długa 5/3, 00-950 Warszawa"], Addresses("Adres: ul. Długa 5/3, 00-950 Warszawa"));
        Assert.Equal(["221B Baker Street"], Addresses("He lives at 221B Baker Street."));
        Assert.Equal(["12345 Musterstadt"], Addresses("Max Beispiel\n12345 Musterstadt"));

        // keine Anschrift
        Assert.Empty(Addresses("Im Jahr 2020 wurde der Spaltring 2 getauscht."));
        Assert.Empty(Addresses("Die Verordnung (EU) 2016/679 vom 27. April 2016 gilt seit 2018."));
        Assert.Empty(Addresses("Arbeitsschritt 3: die Straße sperren."));

        // Platzhalter wie bei den anderen Arten, im Tag erkennbar
        var map = new PrivacyMap(new PrivacySettings { Enabled = true });
        Assert.Equal("Wohnort: [ADDRESS_1].", map.Mask("Wohnort: Bahnhofstraße 3a."));
        Assert.Equal("Wohnort: Bahnhofstraße 3a.", map.Unmask("Wohnort: [Address 1]."));
    }

    [Fact]
    public void ProtectedDataAndPlainDatesAreNotResearched()
    {
        PrivacyContext.Current = new PrivacySettings();                  // auch ohne „vertraulich“
        var segments = Segments("Max Beispiel, Bahnhofstraße 3a, 12345 Musterstadt, max@example.de. Tätig seit 03/2019 als Gleitringdichtungsprüfer.");
        Assert.Equal("geschützte Angabe", DocumentResearch.SkipReason("Bahnhofstraße 3a", segments));
        Assert.Equal("geschützte Angabe", DocumentResearch.SkipReason("Bahnhofstraße", segments));       // Teil der Anschrift
        Assert.Equal("geschützte Angabe", DocumentResearch.SkipReason("max@example.de", segments));
        Assert.Equal("nur ein Datum oder eine Zahl", DocumentResearch.SkipReason("03/2019", segments));
        Assert.Equal("nur ein Datum oder eine Zahl", DocumentResearch.SkipReason("27. April 2016", segments));
        Assert.Equal("nur ein Datum oder eine Zahl", DocumentResearch.SkipReason("seit 2019", segments));
        Assert.Equal("nur ein Datum oder eine Zahl", DocumentResearch.SkipReason("03/2019 – 05/2021", segments));
        Assert.Null(DocumentResearch.SkipReason("Gleitringdichtungsprüfer", segments));
        Assert.Null(DocumentResearch.SkipReason("Verordnung (EU) 2016/679", segments));

        // „Nicht schützen“ gibt einen Wert frei
        PrivacyContext.Current = new PrivacySettings { NotSensitive = ["Bahnhofstraße 3a, 12345 Musterstadt"] };
        Assert.Null(DocumentResearch.SkipReason("Bahnhofstraße 3a", segments));
    }

    [Fact]
    public async Task NoSearchQueryCarriesProtectedData()
    {
        PrivacyContext.Current = new PrivacySettings();
        PrivacyGuard.Values = PrivacyGuard.Collect(["Herr Beispiel wohnt in der Bahnhofstraße 3a. Tel. +49 30 1234567"], null);
        Assert.Contains("Bahnhofstraße 3a", PrivacyGuard.Values);
        Assert.Contains("Beispiel", PrivacyGuard.Values);

        Assert.Equal("Bahnhofstraße 3a", PrivacyGuard.Find("\"Bahnhofstraße 3a\" Lebenslauf"));
        Assert.Equal("Bahnhofstraße 3a", PrivacyGuard.Find("/w/api.php?search=%22Bahnhofstra%C3%9Fe+3a%22"));     // aus der Adresse dekodiert
        Assert.Equal("+49 30 1234567", PrivacyGuard.Find("+49 30 1234567"));
        Assert.Equal("jemand@example.org", PrivacyGuard.Find("Kontakt jemand@example.org"));                    // auch ohne Dokument erkannt
        Assert.Null(PrivacyGuard.Find("\"Gleitringdichtung\" Pumpen"));
        Assert.Null(PrivacyGuard.Find("Verordnung 2016/679 vom 27. April 2016"));                               // Datum bleibt frei

        // die Suche selbst: Anfrage mit Anschrift wird nicht gestellt
        var inner = new Search();
        var search = PrivacyGuard.Wrap(inner)!;
        await Assert.ThrowsAsync<OutboundDeclinedException>(() => search.SearchAsync("\"Bahnhofstraße 3a\"", "de", 5, TestContext.Current.CancellationToken));
        await search.SearchAsync("Gleitringdichtung", "de", 5, TestContext.Current.CancellationToken);
        Assert.Equal(["Gleitringdichtung"], inner.Queries);

        // der Datenausgang: auch an eine Suchmaschine auf diesem Rechner (SearXNG fragt weiter im Netz) geht nichts
        await Assert.ThrowsAsync<OutboundDeclinedException>(() => Outbound.CheckAsync("127.0.0.1", OutboundKind.Web,
            "GET http://127.0.0.1:8888/search?q=Bahnhofstra%C3%9Fe%203a", local: true, TestContext.Current.CancellationToken));
        await Outbound.CheckAsync("127.0.0.1", OutboundKind.Web, "GET http://127.0.0.1:8888/search?q=Gleitringdichtung", local: true, TestContext.Current.CancellationToken);
    }

    private sealed class Search : ISearchProvider
    {
        public List<string> Queries { get; } = new();
        public string Name => "Test";

        public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(new List<SearchResult>());
        }
    }
}
