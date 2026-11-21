using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Tests;

/// <summary>Grammatikprüfung: Segmente bündeln, Treffer zuordnen – mit LanguageTool (eigener Server) und mit der KI.</summary>
public sealed class GrammarCheckerTests
{
    private sealed class Proofreader(string answer) : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public void SegmentsAreBundledWithTheirPositions()
    {
        var segments = new List<(int, string)> { (1, "Erster Satz."), (2, "Zweiter Satz."), (5, "Dritter.") };
        var (text, offsets) = GrammarChecker.Batches(segments).Single();
        Assert.Equal("Erster Satz.\n\nZweiter Satz.\n\nDritter.", text);
        Assert.Equal((2, 14, 13), offsets[1]);
        Assert.Equal("Dritter.", text.Substring(offsets[2].Start, offsets[2].Length));

        // große Dokumente: mehrere Anfragen unter 20 KB
        var many = Enumerable.Range(1, 400).Select(i => (i, new string('a', 100) + " Satz.")).ToList();
        var batches = GrammarChecker.Batches(many).ToList();
        Assert.True(batches.Count >= 2);
        Assert.All(batches, b => Assert.True(Encoding.UTF8.GetByteCount(b.Text) <= 18_000));
        Assert.Equal(400, batches.Sum(b => b.Offsets.Count));
        Assert.Equal("pl-PL", GrammarChecker.LanguageToolCode("pl"));
        Assert.Equal("fr", GrammarChecker.LanguageToolCode("fr-FR"));
        Assert.Equal("de-CH", GrammarChecker.LanguageToolCode("de-CH"));
    }

    [Fact]
    public async Task LanguageToolMatchesLandOnTheRightSegment()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        string? form = null;
        _ = Task.Run(async () =>
        {
            var ctx = await server.GetContextAsync();
            form = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            // „Les enfant“ im zweiten Segment (Position 15 im gebündelten Text)
            var body = Encoding.UTF8.GetBytes("""{"matches":[{"offset":15,"length":10,"message":"Le déterminant s’accorde avec le nom.","replacements":[{"value":"Les enfants"}]}]}""");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        });
        var issues = await GrammarChecker.CheckAsync([(7, "Tout va bien."), (9, "Les enfant sont là.")], "fr-FR",
            new GrammarSettings { Engine = GrammarEngine.LanguageToolServer, ServerUrl = $"http://127.0.0.1:{port}" }, ct: TestContext.Current.CancellationToken);
        var issue = Assert.Single(issues);
        Assert.Equal((9, "Les enfant", "Les enfants"), (issue.SegmentId, issue.Wrong, issue.Suggestion));
        Assert.Contains("language=fr", form);
        Assert.Contains("„Les enfant“ → „Les enfants“", issue.ToQa().Message);
    }

    [Fact]
    public async Task TheAiReportsOnlyWhatIsReallyThere()
    {
        var model = new Proofreader("""
            {"issues": [
              {"id": 2, "wrong": "Ja mają", "correct": "Ja mam", "note": "verb agreement"},
              {"id": 2, "wrong": "nicht im Text", "correct": "x", "note": "invented"},
              {"id": 1, "wrong": "dobrze", "correct": "dobrze", "note": "no change"}
            ]}
            """);
        var issues = await GrammarChecker.CheckAsync([(1, "Wszystko ⟨1⟩dobrze⟨/1⟩."), (2, "Ja mają nowy dom.")], "pl-PL",
            new GrammarSettings { Engine = GrammarEngine.Ai }, model, ct: TestContext.Current.CancellationToken);
        var issue = Assert.Single(issues);
        Assert.Equal((2, "Ja mają", "Ja mam"), (issue.SegmentId, issue.Wrong, issue.Suggestion));
        Assert.Contains("[2] Ja mają nowy dom.", model.Prompt);
        Assert.Contains("[1] Wszystko dobrze.", model.Prompt);                                // ohne Tags
    }
}
