using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Verbreitung im Netz: je Variante und Land die wörtlichen Treffer zählen.</summary>
public sealed class WebPresenceTests
{
    /// <summary>Nachgebildete Suche: „Jänner“ ist in Österreich üblich, in Deutschland kaum.</summary>
    private sealed class Search : ICountrySearchProvider
    {
        public readonly List<(string Phrase, string Country, int Page)> Calls = new();
        public int MaxPages => 10;

        public Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default)
        {
            Calls.Add((phrase, country, page));
            if (country == "CH") throw new HttpRequestException("Land nicht unterstützt");
            var results = (phrase, country) switch
            {
                ("Jänner", "AT") => Enumerable.Range(0, 20).Select(i => new SearchResult($"Termine im Jänner {i}", $"https://www.seite{i}.at/x", "Ab 10. Jänner geöffnet.")).ToList(),
                ("Jänner", "DE") =>
                [
                    new SearchResult("Österreichisch: Jänner", "https://www.woerter.de/j", "Jänner sagt man in Österreich."),
                    new SearchResult("Januar", "https://www.kalender.de/", "Im Januar ist es kalt."),            // ohne die Wortfolge
                ],
                _ => new List<SearchResult>(),
            };
            return Task.FromResult(new SearchPage(results, phrase == "Jänner" && country == "AT"));
        }
    }

    [Fact]
    public async Task PresenceIsCountedPerCountry()
    {
        var search = new Search();
        var cells = await WebPresence.CheckAsync(search, ["Jänner", "Hornung"], "de-AT", ["AT", "DE", "CH"], pages: 2, ct: TestContext.Current.CancellationToken);

        var at = cells.Single(c => c is { Variant: "Jänner", Country: "AT" });
        Assert.Equal((40, 20, 20), (at.Checked + 20, at.Exact, at.OnCountryDomains));     // gleiche Seiten zweimal: nur einmal gezählt
        Assert.Equal("≥ 20", at.CountText);
        Assert.Equal(PresenceLevel.Common, at.Level);

        var de = cells.Single(c => c is { Variant: "Jänner", Country: "DE" });
        Assert.Equal((2, 1, 1), (de.Checked, de.Exact, de.OnCountryDomains));
        Assert.Equal(PresenceLevel.Rare, de.Level);
        Assert.Single(de.Examples);

        Assert.Equal(PresenceLevel.None, cells.Single(c => c is { Variant: "Hornung", Country: "AT" }).Level);
        var ch = cells.Single(c => c is { Variant: "Jänner", Country: "CH" });
        Assert.Equal(("–", "Land nicht unterstützt"), (ch.CountText, ch.Error));

        // Seiten nur, solange es weitere gibt
        Assert.Equal(2, search.Calls.Count(c => c is ("Jänner", "AT", _)));
        Assert.Single(search.Calls, c => c is ("Jänner", "DE", _));
    }

    [Fact]
    public void CountriesAndCostAreSuggested()
    {
        Assert.Equal(["DE", "AT", "CH"], WebPresence.CountriesFor("de-DE"));
        Assert.Equal(["FR", "BE", "CH", "CA"], WebPresence.CountriesFor("fr"));
        Assert.Equal(["CH"], WebPresence.CountriesFor("rm-CH"));
        Assert.Equal(["PL"], WebPresence.CountriesFor("pl-PL"));
        Assert.Equal(12, WebPresence.Requests(2, 3, 2));
    }
}
