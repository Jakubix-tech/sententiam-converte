using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Diagnostics;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Tests;

internal static class TestEnvironment
{
    /// <summary>Alles, was die Tests anlegen, liegt hier; der Ordner wird beim Prozessende gelöscht.</summary>
    internal static readonly string Root = Path.Combine(Path.GetTempPath(), "sc-test-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// Die Tests lesen und ändern nie die echten Einstellungen, Sprachressourcen und Protokolle des Nutzers: eigener
    /// Datenordner (wer für einen Live-Test den echten braucht, setzt SENTENTIAM_HOME selbst) und eigenes Ablaufprotokoll.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        Testing.TempCleanup.Start(Root, "sc-test-");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SENTENTIAM_HOME")))
        {
            Directory.CreateDirectory(Path.Combine(Root, "home"));
            Environment.SetEnvironmentVariable("SENTENTIAM_HOME", Path.Combine(Root, "home"));
        }
        ActivityLog.FolderOverride = Path.Combine(Root, "protokoll");
        // keine echten Wikipedia-Abfragen aus den Agenten-Tests; wer sie braucht, schaltet sie mit nachgebildetem Wiki ein
        SententiamConverte.Core.Agents.TermComparisonAgent.WikipediaLinks = false;
        // kein gemeinsames Recherche-Gedächtnis zwischen den Tests; wer es prüft, nimmt sein eigenes (UseFolderHere)
        SententiamConverte.Core.Agents.ResearchMemory.Enabled = false;
    }
}

/// <summary>Ablaufprotokoll: Anfrage und Antwort jeder Schnittstelle mit derselben Kennung, gekürzt, ohne Schlüssel.</summary>
[Collection("Outbound")]
public sealed class ActivityLogTests : IDisposable
{
    public ActivityLogTests()
    {
        ActivityLog.Enabled = true;
        ActivityLog.KeepDays = 14;
        Outbound.Settings = new OutboundSettings();
    }

    public void Dispose() => ActivityLog.Enabled = true;

    private static List<JsonObject> Lines(string marker) =>
        File.ReadAllLines(ActivityLog.CurrentFile).Where(l => l.Contains(marker)).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    /// <summary>Antwortet je nach Adresse mit JSON, einem großen Download oder einem Fehler.</summary>
    private sealed class Fake(string marker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("chat/completions"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$$"""{"choices":[{"message":{"role":"assistant","content":"Check the seal. {{{marker}}}-antwort"}}]}""", Encoding.UTF8, "application/json"),
                });
            if (path.EndsWith("modell.bin"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[3_000_000]) });
            throw new HttpRequestException("Verbindung abgelehnt " + marker);
        }
    }

    [Fact]
    public async Task RequestAndAnswerAreLoggedWithTheSameId()
    {
        var marker = "m" + Guid.NewGuid().ToString("N")[..10];
        using var http = new HttpClient(new OutboundHandler(OutboundKind.Ai, new Fake(marker)));
        await http.PostAsync("https://api.example.com/v1/chat/completions",
            new StringContent($$"""{"model":"m","messages":[{"role":"user","content":"Prüfen Sie die Dichtung. {{marker}}"}]}""", Encoding.UTF8, "application/json"));

        var lines = Lines(marker);
        var request = Assert.Single(lines, l => (string?)l["schritt"] == "Anfrage (KI) – gesendet");
        Assert.Equal(("Datenausgang", "api.example.com"), ((string?)request["bereich"], (string?)request["ziel"]));
        Assert.Contains($"[user]\nPrüfen Sie die Dichtung. {marker}", (string)request["inhalt"]!);            // lesbar, mit Umlauten
        var answer = Assert.Single(lines, l => (string?)l["schritt"] == "Antwort");
        Assert.Equal((string?)request["id"], (string?)answer["id"]);                                           // Paar erkennbar
        Assert.StartsWith("HTTP 200", (string)answer["inhalt"]!);
        Assert.Contains($"{marker}-antwort", (string)answer["inhalt"]!);
        Assert.NotNull(answer["dauer_ms"]);

        // Downloads: nur Typ und Größe; Schlüssel in der Adresse nie; Fehler mit Meldung
        await http.GetAsync($"https://hub.example.com/modell.bin?key=GEHEIM{marker}");
        var download = File.ReadAllLines(ActivityLog.CurrentFile).Last(l => l.Contains("\"Antwort\""));
        Assert.Contains("Inhalt nicht protokolliert", download);
        Assert.DoesNotContain("GEHEIM" + marker, File.ReadAllText(ActivityLog.CurrentFile));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"https://tot.example.com/{marker}"));
        Assert.Contains(Lines(marker), l => (string?)l["schritt"] == "Fehler, keine Antwort" && (string?)l["ziel"] == "tot.example.com");
    }

    [Fact]
    public void LongContentIsShortenedAndSwitchedOffWritesNothing()
    {
        var marker = "m" + Guid.NewGuid().ToString("N")[..10];
        ActivityLog.Write("Test", marker, "ziel", new string('x', ActivityLog.MaxDetail + 500), 12);
        var line = Assert.Single(Lines(marker));
        Assert.EndsWith("(500 weitere Zeichen)", (string)line["inhalt"]!);
        Assert.Equal(12, (long)line["dauer_ms"]!);

        ActivityLog.Enabled = false;
        ActivityLog.Write("Test", marker + "-aus");
        ActivityLog.Enabled = true;
        Assert.Empty(Lines(marker + "-aus"));
    }

    [Fact]
    public void OldLogsAreDeleted()
    {
        Directory.CreateDirectory(ActivityLog.Folder);
        var old = Path.Combine(ActivityLog.Folder, $"protokoll-{DateTime.Now.AddDays(-20):yyyy-MM-dd}.jsonl");
        var recent = Path.Combine(ActivityLog.Folder, $"protokoll-{DateTime.Now.AddDays(-3):yyyy-MM-dd}.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(recent, "{}\n");
        ActivityLog.Cleanup();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        File.Delete(recent);
    }
}
