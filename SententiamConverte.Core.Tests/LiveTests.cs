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
}
