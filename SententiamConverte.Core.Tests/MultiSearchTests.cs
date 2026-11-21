using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Mehrere Suchdienste gleichzeitig: mischen ohne Doppelte, Ausfälle überstehen, alte Einstellungen weiter lesen.</summary>
public sealed class MultiSearchTests
{
    private sealed class Fake(string name, params string[] urls) : ISearchProvider
    {
        public string Name => name;
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) =>
            urls.Length == 1 && urls[0] == "!" ? throw new HttpRequestException("keine Verbindung")
                : Task.FromResult(urls.Select(u => new SearchResult(name + " " + u, u, "")).ToList());
    }

    [Fact]
    public async Task MixesResultsSkipsDuplicatesAndSurvivesFailures()
    {
        var multi = new MultiSearch([
            new Fake("A", "https://a.example/1", "https://a.example/2", "https://a.example/3"),
            new Fake("B", "https://b.example/1", "http://www.a.example/1/"),
            new Fake("C", "!"),
        ]);
        var results = await multi.SearchAsync("x", "de-DE", 5, TestContext.Current.CancellationToken);
        Assert.Equal(["https://a.example/1", "https://b.example/1", "https://a.example/2", "https://a.example/3"], results.Select(r => r.Url));
        Assert.Equal("C: keine Verbindung", Assert.Single(multi.LastFailures));
        Assert.Equal("A + B + C", multi.Name);

        var none = new MultiSearch([new Fake("C", "!"), new Fake("D", "!")]);
        var ex = await Assert.ThrowsAsync<AiException>(() => none.SearchAsync("x", "de-DE", 5, TestContext.Current.CancellationToken));
        Assert.Contains("C: keine Verbindung", ex.Message);
    }

    [Fact]
    public void BuildsFromTheTickedSourcesAndReadsOldSettings()
    {
        // ältere Einstellungen: ein Dienst
        var old = new AiSettings { Search = new SearchConfig { Kind = SearchProviderKind.Brave, ApiKey = "schluessel" } };
        Assert.IsType<BraveSearch>(SearchProviderFactory.Create(old));
        Assert.Equal("Brave Search", SearchProviderFactory.NameFor(old));

        // mehrere angekreuzt; Brave ohne Schlüssel fällt weg, ausgeschaltete zählen nicht
        var s = new AiSettings
        {
            Search = new SearchConfig
            {
                Sources =
                [
                    new() { Kind = SearchProviderKind.Wikipedia },
                    new() { Kind = SearchProviderKind.SearXng, BaseUrl = "http://127.0.0.1:8888" },
                    new() { Kind = SearchProviderKind.Brave },
                    new() { Kind = SearchProviderKind.Tavily, ApiKey = "tvly-x", Enabled = false },
                ],
            },
        };
        var multi = Assert.IsType<MultiSearch>(SearchProviderFactory.Create(s));
        Assert.Equal("Wikipedia + SearXNG", multi.Name);
        Assert.Equal("Wikipedia + SearXNG", SearchProviderFactory.NameFor(s));
        Assert.IsNotAssignableFrom<ICountrySearchProvider>(multi);

        // mit einem Dienst, der nach Land suchen kann, zählt die Mehrfachsuche auch nach Land
        s.Search.Sources[3].Enabled = true;
        Assert.IsAssignableFrom<ICountrySearchProvider>(SearchProviderFactory.Create(s));

        // nur Unbrauchbares angekreuzt: Wikipedia springt ein; nichts angekreuzt: keine Websuche
        var unusable = new AiSettings { Search = new SearchConfig { Sources = [new() { Kind = SearchProviderKind.Brave }] } };
        Assert.IsType<WikipediaSearch>(SearchProviderFactory.Create(unusable));
        var off = new AiSettings { Search = new SearchConfig { Sources = [new() { Kind = SearchProviderKind.Wikipedia, Enabled = false }] } };
        Assert.Null(SearchProviderFactory.Create(off));
        Assert.Equal("keine Websuche", SearchProviderFactory.NameFor(off));
    }
}
