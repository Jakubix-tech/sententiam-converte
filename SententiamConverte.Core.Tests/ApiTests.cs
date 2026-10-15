using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Remote;
using SententiamConverte.Server;

namespace SententiamConverte.Core.Tests;

/// <summary>REST-API v2 am echten Teamserver: alle Bereiche über HTTP, KI über einen lokalen Testserver.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TempDir _dir = new();
    private readonly HttpListener _ai = new();
    private readonly List<string> _aiPrompts = new();
    private WebApplication? _app;
    private string _url = "";
    private string _admin = "", _anna = "", _bartek = "", _reader = "";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        // KI: OpenAI-kompatibler Testserver, der jede Übersetzung mit demselben Satz beantwortet
        var aiPort = FreePort();
        _ai.Prefixes.Add($"http://localhost:{aiPort}/");
        _ai.Start();
        _ = Task.Run(async () =>
        {
            while (_ai.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _ai.GetContextAsync(); } catch (Exception) { return; }
                var request = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                lock (_aiPrompts) _aiPrompts.Add(request);
                var bytes = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"role":"assistant","content":"Pull out the plug."}}]}""");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        new AiSettings
        {
            Providers = [new AiProviderConfig { Name = "Test", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://localhost:{aiPort}/v1", Model = "test" }],
            Search = new SearchConfig { Kind = SearchProviderKind.None },
        }.Save(Path.Combine(_dir.Path, "ki-einstellungen.json"));

        var users = new UserStore(_dir.Path);
        _admin = users.Add("admin", Role.Admin);
        _anna = users.Add("Anna", Role.Editor);
        _bartek = users.Add("Bartek", Role.Editor);
        _reader = users.Add("Gast", Role.Reader);
        _url = $"http://127.0.0.1:{FreePort()}";
        _app = ServerApp.Build(["--urls", _url], _dir.Path);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
        _ai.Close();
        _dir.Dispose();
    }

    private HttpClient Client(string? token)
    {
        var c = new HttpClient { BaseAddress = new Uri(_url + "/api/v2/") };
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<JsonNode> Ok(Task<HttpResponseMessage> call)
    {
        var r = await call;
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task EverythingThroughTheApi()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("falsch").GetAsync("", ct)).StatusCode);
        var info = await Ok(Client(_reader).GetAsync("", ct));
        Assert.Equal("2.0", (string?)info["version"]);
        Assert.Contains(await Ok(Client(_reader).GetAsync("formats", ct)) as JsonArray ?? [], f => (string?)f!["id"] == "idml");

        // Leser dürfen nichts ändern
        var create = new { name = "Handbuch", sourceLanguage = "de-DE", targetLanguages = new[] { "en-GB" }, topic = "Technik" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(_reader).PostAsJsonAsync("projects", create, Json, ct)).StatusCode);

        var api = Client(_anna);
        var project = await Ok(api.PostAsJsonAsync("projects", create, Json, ct));
        Assert.Equal("Handbuch", (string?)project["name"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("projects", create, Json, ct)).StatusCode);   // gibt es schon
        Assert.Contains("Handbuch", (await Ok(api.GetAsync("projects", ct))).AsArray().Select(x => (string?)x));

        // Datei hinzufügen und Segmente lesen
        var file = await Ok(api.PostAsJsonAsync("projects/Handbuch/files",
            new { name = "anleitung.txt", base64 = B64("Das Gerät ausschalten.\n\nDen Stecker ziehen.\n\nDas Gerät ausschalten.\n") }, Json, ct));
        var fileId = (string)file["id"]!;
        var segments = (await Ok(api.GetAsync($"projects/Handbuch/files/{fileId}/en-GB", ct))).AsArray();
        Assert.Equal(3, segments.Count);
        int first = (int)segments[0]!["id"]!, second = (int)segments[1]!["id"]!, third = (int)segments[2]!["id"]!;
        var seg = $"projects/Handbuch/files/{fileId}/en-GB/segments/";

        // Übersetzen und bestätigen: TM mit Kontext, Übernahme ins gleiche Segment weiter unten
        var updated = await Ok(api.PutAsJsonAsync(seg + first, new { target = "Switch off the device." }, Json, ct));
        Assert.Equal("Draft", (string?)updated["status"]);
        var confirmed = await Ok(api.PostAsJsonAsync(seg + first + "/confirm", new { }, Json, ct));
        Assert.Equal(1, (int)confirmed["propagated"]!);
        Assert.Equal("Switch off the device.", (string?)(await Ok(api.GetAsync(seg + third, ct)))["target"]);
        var hits = (await Ok(api.GetAsync("projects/Handbuch/tm/search?source=" + Uri.EscapeDataString("Das Gerät ausschalten.") +
                                         "&sourceLanguage=de-DE&targetLanguage=en-GB", ct))).AsArray();
        Assert.True((int)hits[0]!["score"]! >= 100);
        var matches = (await Ok(api.GetAsync(seg + third + "/matches", ct))).AsArray();
        Assert.Equal("Switch off the device.", (string?)matches[0]!["unit"]!["target"]);

        // Terminologie und Wissen
        await Ok(api.PostAsJsonAsync("projects/Handbuch/terms", new
        {
            terms = new[] { new { lang = "de-DE", text = "Stecker", status = "Preferred" }, new { lang = "en-GB", text = "plug", status = "Preferred" } },
            definition = "Netzstecker", domain = "Technik",
        }, Json, ct));
        Assert.Contains((await Ok(api.GetAsync(seg + second + "/terms", ct))).AsArray(), t => t!.ToJsonString().Contains("plug"));
        await Ok(api.PostAsJsonAsync("projects/Handbuch/knowledge",
            new { language = "en-GB", topic = "Technik", kind = "Style", title = "Imperative", content = "Instructions use the imperative." }, Json, ct));
        var knowledge = (await Ok(api.GetAsync("projects/Handbuch/knowledge?query=imperative", ct))).AsArray();
        Assert.Equal("Anna", (string?)knowledge[0]!["author"]);

        // KI-Übersetzung eines Segments: TM, Terminologie und Wissen gehen in die Anfrage
        var ai = await Ok(api.PostAsJsonAsync("agents/translate", new { project = "Handbuch", file = fileId, language = "en-GB", segmentId = second }, Json, ct));
        Assert.Equal("Pull out the plug.", (string?)ai["translation"]);
        string prompt;
        lock (_aiPrompts) prompt = _aiPrompts.Last();
        Assert.Contains("plug", prompt);
        Assert.Contains("imperative", prompt);
        await Ok(api.PutAsJsonAsync(seg + second, new { target = (string?)ai["translation"], status = "Translated" }, Json, ct));

        // Stapelaufgaben
        var pre = (await Ok(api.PostAsJsonAsync("projects/Handbuch/pretranslate", new { confirmExact = true }, Json, ct))).AsArray();
        Assert.Single(pre);
        var analysis = (await Ok(api.GetAsync("projects/Handbuch/analysis", ct))).AsArray();
        Assert.NotEmpty(analysis);
        Assert.NotNull((await Ok(api.GetAsync("projects/Handbuch/qa", ct))).AsArray());

        // Ergebnisse
        var target = await Ok(api.GetAsync($"projects/Handbuch/files/{fileId}/en-GB/target", ct));
        var text = Encoding.UTF8.GetString(Convert.FromBase64String((string)target["base64"]!));
        Assert.Contains("Switch off the device.", text);
        Assert.Contains("Pull out the plug.", text);
        var tmx = await Ok(api.GetAsync("projects/Handbuch/tm/export", ct));
        Assert.Contains("Switch off the device.", Encoding.UTF8.GetString(Convert.FromBase64String((string)tmx["base64"]!)));
        var xliff = await Ok(api.GetAsync($"projects/Handbuch/files/{fileId}/en-GB/xliff", ct));
        Assert.Contains("<xliff", Encoding.UTF8.GetString(Convert.FromBase64String((string)xliff["base64"]!)));

        // Nur Administratoren löschen Dateien
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DeleteAsync($"projects/Handbuch/files/{fileId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("projects/Gibtsnicht", ct)).StatusCode);
        Assert.Empty((await Ok(api.GetAsync("extensions", ct))).AsArray());

        // Beschreibung der Schnittstelle
        var openApi = await new HttpClient().GetStringAsync(_url + "/openapi/v2.json", ct);
        Assert.Contains("/api/v2/projects/{project}/pretranslate", openApi);
        Assert.Contains("Vorübersetzung aus den TMs", openApi);
        Assert.DoesNotContain("/api/v1/", openApi);
    }

    [Theory]
    [InlineData("../../böse", "Projekt _.._böse")]
    [InlineData("..\\..\\Windows", "Projekt _.._Windows")]
    [InlineData("C:/temp/x", "C__temp_x")]
    [InlineData("Handbuch Pumpe 2026", "Handbuch Pumpe 2026")]
    [InlineData("...", "Projekt")]
    public void PackageNamesStayInsideTheProjectFolder(string name, string expected)
    {
        var safe = Core.Sdk.ProjectCache.SafeName(name);
        Assert.Equal(expected, safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.False(safe.StartsWith('.'));
    }

    [Fact]
    public async Task ApiChangesReachTeamMembersEditingTheSameFile()
    {
        var ct = TestContext.Current.CancellationToken;
        // Anna arbeitet im Programm an einem gemeinsamen Projekt (Abgleich über /api/v1)
        var anna = new SharedProjectClient(new RemoteApi(new ServerAccount { Name = "Büro", Url = _url, Token = _anna }));
        anna.Create(new SharedProjectCreate("Vertrag", "de-DE", ["en-GB"], "Recht"));
        var f = anna.Upload("Vertrag", "vertrag.txt", Encoding.UTF8.GetBytes("Der Vertrag endet am 31. Dezember und kann verlängert werden.\n\nDie Parteien vereinbaren Schriftform.\n"));
        var doc = anna.Document("Vertrag", f.Id, "en-GB");

        // Ein Skript ändert über die API (v2) ein Segment …
        var api = Client(_bartek);
        var seg = $"projects/Vertrag/files/{f.Id}/en-GB/segments/";
        var secondId = doc.Document.Segments.Last().Id;
        await Ok(api.PutAsJsonAsync(seg + secondId, new { target = "The parties agree on written form.", status = "Translated" }, Json, ct));

        // … und Anna bekommt es beim nächsten Abgleich wie jede andere Änderung
        var changes = anna.Changes("Vertrag", f.Id, "en-GB", doc.Version);
        var change = Assert.Single(changes.Changes);
        Assert.Equal(secondId, change.SegmentId);
        Assert.Equal("The parties agree on written form.", change.Target);
        Assert.Equal("API", change.User);
        Assert.False(changes.Reset);

        // Teilen verändert die Segmentfolge: Anna muss das Dokument neu laden
        var firstId = doc.Document.Segments.First().Id;
        var source = doc.Document.Segments.First().Source;
        var split = (await Ok(api.PostAsJsonAsync(seg + firstId + "/split", new { sourceIndex = source.IndexOf(" und", StringComparison.Ordinal) }, Json, ct))).AsArray();
        Assert.Equal(2, split.Count);
        Assert.True(anna.Changes("Vertrag", f.Id, "en-GB", changes.Version).Reset);
        var reloaded = anna.Document("Vertrag", f.Id, "en-GB");
        Assert.Equal(3, reloaded.Document.Segments.Count());
        Assert.Equal("The parties agree on written form.", reloaded.Document.Segments.Last().Target);
    }
}
