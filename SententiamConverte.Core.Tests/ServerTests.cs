using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Server;

namespace SententiamConverte.Core.Tests;

/// <summary>Echter Teamserver auf einem freien Port; mehrere Benutzer arbeiten gleichzeitig.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private WebApplication? _app;
    private string _url = "";
    private string _editorA = "", _editorB = "", _reader = "", _admin = "";

    public async ValueTask InitializeAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _url = $"http://127.0.0.1:{port}";

        var users = new UserStore(_dir.Path);
        _admin = users.Add("admin", Role.Admin);
        _editorA = users.Add("Anna", Role.Editor);
        _editorB = users.Add("Bartek", Role.Editor);
        _reader = users.Add("Gast", Role.Reader);
        _app = ServerApp.Build(["--urls", _url], _dir.Path);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
        _dir.Dispose();
    }

    private RemoteApi Api(string token, string name = "Team") => new(new ServerAccount { Name = name, Url = _url, Token = token });

    [Fact]
    public async Task TeamSharesTmTermsAndKnowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = Api(_admin);
        await admin.CreateResourceAsync(ResourceKind.Tm, "Maschinenbau", "gemeinsames TM", ct);
        await admin.CreateResourceAsync(ResourceKind.Termbase, "Fachbegriffe", null, ct);
        await admin.CreateResourceAsync(ResourceKind.Knowledge, "Pumpenwissen", null, ct);
        Assert.Equal(3, (await admin.ResourcesAsync(ct)).Count);
        Assert.Equal("Anna", (await Api(_editorA).WhoAmIAsync(ct)).User);

        // Zwei Übersetzer schreiben gleichzeitig ins selbe TM
        var tmA = new RemoteTranslationMemory(Api(_editorA), "Maschinenbau");
        var tmB = new RemoteTranslationMemory(Api(_editorB), "Maschinenbau");
        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 40; i++) tmA.Add("de-DE", "en-GB", $"Satz A Nummer {i}.", $"Sentence A number {i}."); }, ct),
            Task.Run(() => { for (var i = 0; i < 40; i++) tmB.Add("de-DE", "en-GB", $"Satz B Nummer {i}.", $"Sentence B number {i}."); }, ct));
        Assert.Equal(80, tmA.Count());

        // Was Anna bestätigt hat, findet Bartek sofort
        tmA.Add("de-DE", "en-GB", "Die Pumpe ausschalten.", "Switch off the pump.");
        var match = tmB.Search("de-DE", "en-GB", "Die Pumpe ausschalten.").First();
        Assert.Equal(100, match.Score);
        Assert.Equal("Anna", match.Unit.Origin);                         // wer es beigetragen hat
        Assert.NotEmpty(tmB.Concordance("de-DE", "en-GB", "Pumpe"));
        Assert.Single(tmB.Browse("Pumpe", 0, 10));

        // Terminologie
        var tbA = new RemoteTermbase(Api(_editorA), "Fachbegriffe");
        tbA.AddConcept([("de-DE", "Gleitringdichtung", TermStatus.Preferred), ("en-GB", "mechanical seal", TermStatus.Preferred)]);
        var hit = new RemoteTermbase(Api(_editorB), "Fachbegriffe").Recognize("Die Gleitringdichtungen prüfen.", "de-DE", "en-GB").Single();
        Assert.Equal("mechanical seal", hit.Translations.Single().Text);

        // Wissen
        var kbB = new RemoteKnowledgeBase(Api(_editorB), "Pumpenwissen");
        kbB.Add(new KnowledgeEntry { Language = "de-DE", Topic = "Pumpen", Kind = KnowledgeKind.Style, Title = "Anrede", Content = "Betriebsanleitungen siezen den Leser." });
        var found = new RemoteKnowledgeBase(Api(_editorA), "Pumpenwissen").Search("de-DE", "Anrede Betriebsanleitungen").Single();
        Assert.Equal("Bartek", found.Author);

        // Aktivität sichtbar für alle
        var activity = await Api(_reader).ActivityAsync(200, ct);
        Assert.Contains(activity, a => a.User == "Anna" && a.Kind == ResourceKind.Termbase);
        Assert.Contains(activity, a => a.User == "Bartek" && a.Text.Contains("Anrede"));

        // Leser darf lesen, aber nicht schreiben; falscher Schlüssel wird abgewiesen
        var readerTm = new RemoteTranslationMemory(Api(_reader), "Maschinenbau");
        Assert.Equal(81, readerTm.Count());
        Assert.Contains("keine Berechtigung", Assert.Throws<RemoteException>(() => readerTm.Add("de-DE", "en-GB", "x y", "x y")).Message);
        Assert.Contains("ungültig", Assert.Throws<RemoteException>(() => new RemoteTranslationMemory(Api("falsch"), "Maschinenbau").Count()).Message);
        Assert.Throws<RemoteException>(() => new RemoteTranslationMemory(Api(_editorA), "../../geheim").Count());
    }

    [Fact]
    public async Task ProjectUsesSharedResourcesPerLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        await Api(_admin).CreateResourceAsync(ResourceKind.Tm, "Polnisch", null, ct);
        var servers = new ServerSettings { Servers = [new ServerAccount { Name = "Büro", Url = _url, Token = _editorA }] };
        TranslationProject.Servers = () => servers;
        try
        {
            using var project = TranslationProject.Create(_dir.File("p"), "Team", "de-DE", ["en-GB", "pl-PL"]);
            project.Attach(new ResourceRef { Kind = ResourceKind.Tm, Server = "Büro", Location = "Polnisch", Languages = ["pl-PL"], WriteBack = true });
            Assert.Empty(project.ResourceErrors);
            Assert.Equal(2, project.TmsFor("pl-PL").Count);
            Assert.Single(project.TmsFor("en-GB"));                     // nur für Polnisch angebunden

            var file = project.AddFile(new MemoryStream("Guten Tag.\n"u8.ToArray()), "a.txt");
            var doc = project.LoadDocument(file, "pl-PL");
            var seg = doc.Segments.Single();
            seg.Target = "Dzień dobry.";
            Workflow.Confirm(project, doc, seg);

            // Bestätigtes Segment ist im gemeinsamen TM angekommen
            var shared = new RemoteTranslationMemory(Api(_editorB), "Polnisch");
            Assert.Equal("Dzień dobry.", shared.Search("de-DE", "pl-PL", "Guten Tag.").Single().Unit.Target);

            // Ist der Server weg, öffnet das Projekt trotzdem und meldet den Fehler
            servers.Servers[0].Url = "http://127.0.0.1:1";
            ResourceFactory.ResetConnections();
            using var offline = TranslationProject.Open(_dir.File("p"));
            Assert.Single(offline.ResourceErrors.Concat(offline.Tms.Skip(1).Select(t => t.Name)));
            Assert.Empty(Workflow.Lookup(offline, offline.LoadDocument(offline.Files[0], "pl-PL"), new Segment { Source = "Unbekannt hier." }));
        }
        finally
        {
            TranslationProject.Servers = ServerSettings.Load;
            ResourceFactory.ResetConnections();
        }
    }

    [Fact]
    public void TwoTranslatorsEditTheSameFile()
    {
        var servers = new ServerSettings { Servers = [new ServerAccount { Name = "Büro", Url = _url, Token = _editorA }] };
        var serversB = new ServerSettings { Servers = [new ServerAccount { Name = "Büro", Url = _url, Token = _editorB }] };
        // Projekt auf dem Server anlegen und Datei hochladen
        var admin = new SharedProjectClient(Api(_admin));
        admin.Create(new SharedProjectCreate("Vertrag", "de-DE", ["en-GB"], "Recht"));
        var file = admin.Upload("Vertrag", "vertrag.txt", "Erster Satz.\nZweiter Satz.\nDritter Satz.\n"u8.ToArray());

        try
        {
            TranslationProject.Servers = () => servers;
            ResourceFactory.ResetConnections();
            using var anna = TranslationProject.OpenShared(_dir.File("anna"), "Büro", "Vertrag");
            TranslationProject.Servers = () => serversB;
            ResourceFactory.ResetConnections();
            using var bartek = TranslationProject.OpenShared(_dir.File("bartek"), "Büro", "Vertrag");

            Assert.True(anna.IsShared);
            var fa = anna.Files.Single();
            var fb = bartek.Files.Single();
            var docA = anna.LoadDocument(fa, "en-GB");
            var docB = bartek.LoadDocument(fb, "en-GB");
            var a = docA.Segments.ToList();
            var b = docB.Segments.ToList();

            // Anna übersetzt Segment 1 → Bartek sieht es beim nächsten Abgleich
            a[0].Target = "First sentence.";
            Workflow.Confirm(anna, docA, a[0]);
            anna.SaveDocument(fa, docA);
            var sync = bartek.Sync(fb, docB);
            Assert.Contains(1, sync.ChangedSegments);
            Assert.Equal("First sentence.", b[0].Target);
            Assert.Equal(SegmentStatus.Translated, b[0].Status);

            // Beide ändern Segment 2 gleichzeitig: der Zweite bekommt einen Konflikt, seine Fassung landet im Verlauf
            a[1].Target = "Second sentence (Anna).";
            b[1].Target = "Second sentence (Bartek).";
            anna.SaveDocument(fa, docA);
            bartek.SaveDocument(fb, docB);
            Assert.Single(bartek.LastConflicts);
            Assert.Equal("Second sentence (Anna).", b[1].Target);
            Assert.Contains(b[1].History, h => h.Target == "Second sentence (Bartek).");

            // Sperre: Anna arbeitet an Segment 3, Bartek darf es nicht überschreiben
            Assert.True(anna.LockSegment(fa, "en-GB", 3));
            Assert.False(bartek.LockSegment(fb, "en-GB", 3));
            var locks = bartek.Sync(fb, docB).Locks;
            Assert.Contains(locks, l => l.SegmentId == 3 && l.User == "Anna");
            b[2].Target = "Third (Bartek).";
            bartek.SaveDocument(fb, docB);
            Assert.Contains(bartek.LastConflicts, c => c.StartsWith("Segment 3"));           // gesperrt – nicht übernommen
            Assert.NotEqual("Third (Bartek).", new SharedProjectClient(Api(_admin)).Document("Vertrag", file.Id, "en-GB").Document.Segments.ToList()[2].Target);
            anna.UnlockSegment(fa, "en-GB", 3);

            // Zieldatei kommt vom Server und enthält die Arbeit beider
            var ms = new MemoryStream();
            bartek.ExportTarget(fb, docB, ms);
            Assert.StartsWith("First sentence.\nSecond sentence (Anna).", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        finally
        {
            TranslationProject.Servers = ServerSettings.Load;
            ResourceFactory.ResetConnections();
        }
    }

    private sealed class Model : IChatModel
    {
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult(messages[^1].Text.Contains("write 3 web search queries")
                ? """{"queries": {"de-DE": ["Pumpen Wartung"]}}"""
                : "{\"items\": [" +
                  "{\"kind\": \"fact\", \"title\": \"Trockenlauf\", \"content\": \"Kreiselpumpen dürfen nicht trocken laufen.\", \"quote\": \"Eine Kreiselpumpe darf nie trocken laufen.\", \"url\": \"u\", \"confidence\": 0.9}," +
                  "{\"kind\": \"fact\", \"title\": \"Erfunden\", \"content\": \"Pumpen sind blau.\", \"quote\": \"Alle Pumpen sind blau.\", \"url\": \"u\", \"confidence\": 0.9}]}");
    }

    private sealed class Search : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) =>
            Task.FromResult(new List<SearchResult> { new("Pumpenratgeber", "https://pumpen.example/ratgeber", "") });
    }

    private sealed class Fetcher : IPageFetcher
    {
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) =>
            Task.FromResult<string?>("Ratgeber für Pumpen. " + new string('.', 300) + " Eine Kreiselpumpe darf nie trocken laufen, sonst wird die Dichtung zerstört.");
    }

    [Fact]
    public async Task KnowledgeAgentFillsSharedBaseOnlyWithVerifiedItems()
    {
        var ct = TestContext.Current.CancellationToken;
        await Api(_admin).CreateResourceAsync(ResourceKind.Knowledge, "Team-Wissen", null, ct);
        var kb = new RemoteKnowledgeBase(Api(_editorA), "Team-Wissen");
        var ctx = new AgentContext { Model = new Model(), Search = new Search(), Fetcher = new Fetcher() };

        var result = await new KnowledgeAgent(ctx).RunAsync("Kreiselpumpen", ["de-DE"], kb, pagesPerLanguage: 1, author: "Anna", ct: ct);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Rejected);                                 // erfundenes Zitat verworfen
        var entry = kb.Search("de-DE", "trocken Kreiselpumpe").Single();
        Assert.Equal("https://pumpen.example/ratgeber", entry.SourceUrl);
        Assert.Contains("Anna", entry.Author);

        // zweiter Lauf: keine Dubletten
        var again = await new KnowledgeAgent(ctx).RunAsync("Kreiselpumpen", ["de-DE"], kb, pagesPerLanguage: 1, ct: ct);
        Assert.Equal(0, again.Added);
        Assert.Equal(1, again.Duplicates);
    }
}
