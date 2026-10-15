using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Server;

namespace SententiamConverte.Core.Tests;

/// <summary>Die Beispiel-Erweiterung (Ordner „Erweiterungen“) installieren, einschalten, benutzen und entfernen.</summary>
public class ExtensionTests
{
    private static string Package => Path.Combine(AppContext.BaseDirectory, "Beispiel.Erweiterung.scext");

    private static BilingualDocument Text(string text, string target = "en-GB") =>
        new TextFilter().Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test.txt", "de-DE", target, new Segmenter("de-DE"));

    [Fact]
    public async Task InstallEnableUseAndRemove()
    {
        using var dir = new TempDir();
        var manager = new ExtensionManager(dir.File("erweiterungen"));
        var ext = manager.Install(Package);

        // Nach der Installation ausgeschaltet: nichts geladen
        Assert.Equal("beispiel.erweiterung", ext.Manifest.Id);
        Assert.False(ext.Manifest.Enabled);
        Assert.False(ext.Loaded);
        Assert.Empty(manager.Commands);

        manager.SetEnabled(ext.Manifest.Id, true);
        Assert.True(ext.Loaded, ext.Error);
        Assert.Equal(4, manager.Commands.Count());
        Assert.Contains(ext.Contributions, c => c.StartsWith("Dateiformat: CSV"));
        Assert.Contains("QA-Prüfung: Ausrufezeichen", ext.Contributions);

        // Eingeschaltet bleibt eingeschaltet (nächster Programmstart)
        var again = new ExtensionManager(dir.File("erweiterungen"));
        again.LoadAll();
        Assert.True(Assert.Single(again.Extensions).Loaded);

        // Befehl für das Dokument: Anführungszeichen der Zielsprache
        var doc = Text("Auf Start drücken.\n\nDann warten.", "de-DE");
        doc.Segments.First().Target = "Auf \"Start\" drücken.";
        var quotes = manager.FindCommand("beispiel.anfuehrungszeichen")!;
        Assert.Equal(CommandScope.Document, quotes.Scope);
        var result = await quotes.RunAsync(new ExtensionContext { Document = doc }, TestContext.Current.CancellationToken);
        Assert.True(result.DocumentChanged);
        Assert.Equal("Auf „Start“ drücken.", doc.Segments.First().Target);

        // Befehl für ein Segment
        var stats = await manager.FindCommand("beispiel.statistik")!.RunAsync(new ExtensionContext { Document = doc, Segment = doc.Segments.First() },
            TestContext.Current.CancellationToken);
        Assert.Contains("Quelle 3 Wörter", stats.Message);

        // Ohne Segment eine verständliche Meldung statt Absturz
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.FindCommand("beispiel.statistik")!.RunAsync(new ExtensionContext(), TestContext.Current.CancellationToken));

        // QA-Prüfung der Erweiterung läuft in der normalen Qualitätsprüfung mit
        var qaDoc = Text("Das Gerät ausschalten.");
        qaDoc.Segments.First().Target = "Switch off the device!";
        qaDoc.Segments.First().Status = SegmentStatus.Translated;
        Assert.Contains(QaChecker.Check(qaDoc, new QaSettings()), i => i.Check == "Ausrufezeichen");

        // Dateiformat der Erweiterung: CSV, erste Spalte übersetzen, zweite füllen
        var csv = "Begriff;Übersetzung\r\nNetzstecker;\r\n\"Gerät; tragbar\";alt\r\n";
        var filter = FileFilters.ForFile("liste.csv")!;
        Assert.Equal("beispiel.csv", filter.Id);
        var csvDoc = filter.Read(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "liste.csv", "de-DE", "en-GB", new Segmenter("de-DE"));
        Assert.Equal(["Begriff", "Netzstecker", "Gerät; tragbar"], csvDoc.Segments.Select(s => s.Source));
        var targets = new[] { "Term", "Mains plug", "Device; portable" };
        foreach (var (s, t) in csvDoc.Segments.Zip(targets)) s.Target = t;
        var output = new MemoryStream();
        filter.Write(csvDoc, new MemoryStream(Encoding.UTF8.GetBytes(csv)), output);
        Assert.Equal("Begriff;Term\r\nNetzstecker;Mains plug\r\n\"Gerät; tragbar\";\"Device; portable\"\r\n", Encoding.UTF8.GetString(output.ToArray()));

        // Entfernen: Bibliotheken sind nicht gesperrt, der Ordner verschwindet sofort
        manager.Remove(ext.Manifest.Id);
        Assert.Empty(manager.Extensions);
        Assert.False(Directory.Exists(ext.Folder));
    }

    [Fact]
    public void BrokenPackagesAreRejected()
    {
        using var dir = new TempDir();
        var manager = new ExtensionManager(dir.File("erweiterungen"));
        var zip = dir.File("kaputt.scext");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            archive.CreateEntry("liesmich.txt");
        Assert.Throws<InvalidDataException>(() => manager.Install(zip));

        var badId = dir.File("boese.scext");
        using (var archive = System.IO.Compression.ZipFile.Open(badId, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("extension.json").Open()))
            w.Write("""{"id":"../../ausbrechen","name":"x","assembly":"x.dll"}""");
        Assert.Throws<InvalidDataException>(() => manager.Install(badId));
        Assert.Empty(Directory.GetDirectories(manager.Folder));
    }

    [Fact]
    public async Task ExtensionCommandsRunThroughTheApi()
    {
        using var dir = new TempDir();
        var users = new UserStore(dir.Path);
        var token = users.Add("Anna", Role.Editor);
        var setup = new ExtensionManager(Path.Combine(dir.Path, "erweiterungen"));
        setup.SetEnabledAfterInstall(Package);

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        l.Stop();
        await using var app = ServerApp.Build(["--urls", url], dir.Path);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var http = new HttpClient { BaseAddress = new Uri(url + "/api/v2/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var list = JsonNode.Parse(await http.GetStringAsync("extensions", ct))!.AsArray();
        var ext = Assert.Single(list);
        Assert.True((bool)ext!["loaded"]!);
        Assert.Contains(ext["commands"]!.AsArray(), c => (string?)c!["id"] == "beispiel.anfuehrungszeichen");

        await http.PostAsJsonAsync("projects", new { name = "Erw", sourceLanguage = "de-DE", targetLanguages = new[] { "en-GB" } }, ct);
        var file = JsonNode.Parse(await (await http.PostAsJsonAsync("projects/Erw/files",
            new { name = "a.txt", base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Auf Start drücken.")) }, ct)).Content.ReadAsStringAsync(ct))!;
        var seg = $"projects/Erw/files/{file["id"]}/en-GB/segments/1";
        (await http.PutAsJsonAsync(seg, new { target = "Press \"Start\"." }, ct)).EnsureSuccessStatusCode();

        var run = await http.PostAsJsonAsync("extensions/commands/beispiel.anfuehrungszeichen", new { project = "Erw", file = (string?)file["id"], language = "en-GB" }, ct);
        var result = JsonNode.Parse(await run.Content.ReadAsStringAsync(ct))!;
        Assert.True(run.IsSuccessStatusCode, result.ToJsonString());
        Assert.True((bool)result["documentChanged"]!);
        Assert.Equal("Press “Start”.", (string?)JsonNode.Parse(await http.GetStringAsync(seg, ct))!["target"]);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("extensions/commands/gibtsnicht", new { }, ct)).StatusCode);

        // Ein Befehl, der selbst die interne Programmierschnittstelle (context.Api) benutzt – hier auf dem Server
        var report = JsonNode.Parse(await (await http.PostAsJsonAsync("extensions/commands/beispiel.projektbericht", new { project = "Erw" }, ct))
            .Content.ReadAsStringAsync(ct))!;
        var message = (string)report["message"]!;
        Assert.Contains("Projekt „Erw“: de-DE → en-GB, 1 Datei(en)", message);
        Assert.Contains("a.txt (en-GB): 0/1 Segmente fertig", message);
        Assert.Contains("QA:", message);
    }
}

internal static class ExtensionTestHelpers
{
    /// <summary>Installieren und gleich einschalten (wie ein Admin auf dem Server).</summary>
    public static void SetEnabledAfterInstall(this ExtensionManager manager, string package)
    {
        var e = manager.Install(package);
        manager.SetEnabled(e.Manifest.Id, true);
    }
}
