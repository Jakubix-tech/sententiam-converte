using System.Net;
using System.Text;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Tests;

[CollectionDefinition("Outbound", DisableParallelization = true)]
public sealed class OutboundCollection;

/// <summary>Datenausgang: Protokoll mit genauem Inhalt, Rückfrage vor dem Senden, gesperrte Dienste.</summary>
[Collection("Outbound")]
public sealed class OutboundTests : IDisposable
{
    private readonly OutboundSettings _old = Outbound.Settings;
    private readonly Func<OutboundRequest, CancellationToken, Task<OutboundAnswer>>? _oldApprover = Outbound.Approver;

    public OutboundTests()
    {
        Outbound.Settings = new OutboundSettings();
        Outbound.ResetSession();
        Outbound.Clear();
    }

    public void Dispose()
    {
        Outbound.Settings = _old;
        Outbound.Approver = _oldApprover;
        Outbound.ResetSession();
    }

    /// <summary>Statt ins Netz: zählt, was wirklich hinausginge.</summary>
    private sealed class Fake : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        }
    }

    private static Task<HttpResponseMessage> Chat(HttpClient http, string url, string text) =>
        http.PostAsync(url, new StringContent($$"""{"model":"m","messages":[{"role":"system","content":"Übersetze."},{"role":"user","content":"{{text}}"}]}""",
            Encoding.UTF8, "application/json"));

    [Fact]
    public async Task LogsTheExactContentReadably()
    {
        var fake = new Fake();
        using var http = new HttpClient(new OutboundHandler(OutboundKind.Ai, fake));
        await Chat(http, "https://api.example.com/v1/chat/completions", "Die Gleitringdichtung prüfen.");
        await http.GetAsync("https://translation.example.com/translate?q=Haus&key=GEHEIM123");
        await Chat(http, "http://localhost:8080/v1/chat/completions", "Lokal bleibt lokal.");

        var log = Outbound.Entries;
        Assert.Equal(3, log.Count);
        var chat = log[0];
        Assert.Equal(("api.example.com", OutboundDecision.Sent), (chat.Service, chat.Decision));
        Assert.Contains("[user]\nDie Gleitringdichtung prüfen.", chat.Content);       // lesbar, mit Umlauten
        Assert.Contains("[system]\nÜbersetze.", chat.Content);
        Assert.DoesNotContain("GEHEIM123", log[1].Content);                              // Schlüssel nicht im Protokoll
        Assert.Contains("q=Haus", log[1].Content);
        Assert.Equal(OutboundDecision.Local, log[2].Decision);
        Assert.Equal(3, fake.Calls);

        // Listen statt Objekte (Microsoft Translator): lesbar festgehalten, die Anfrage geht trotzdem hinaus
        await http.PostAsync("https://api.cognitive.example.com/translate", new StringContent("""[{"Text":"Zażółć gęślą jaźń"}]""", Encoding.UTF8, "application/json"));
        Assert.Contains("Zażółć gęślą jaźń", Outbound.Entries[3].Content);
        Assert.Equal(4, fake.Calls);
    }

    [Fact]
    public async Task AsksOncePerServiceAndRespectsTheAnswer()
    {
        var asked = new List<OutboundRequest>();
        var answers = new Queue<OutboundAnswer>([OutboundAnswer.Decline, OutboundAnswer.AlwaysThisService]);
        Outbound.Approver = (r, _) => { asked.Add(r); return Task.FromResult(answers.Dequeue()); };
        Outbound.Settings.Mode = OutboundMode.AskPerService;
        var fake = new Fake();
        using var http = new HttpClient(new OutboundHandler(OutboundKind.MachineTranslation, fake));

        // abgelehnt: nichts geht hinaus
        await Assert.ThrowsAsync<OutboundDeclinedException>(() => Chat(http, "https://mt.example.com/translate", "Vertraulich."));
        Assert.Equal(0, fake.Calls);
        Assert.Contains("Vertraulich.", asked[0].Content);                               // gezeigt wird der genaue Inhalt

        // erlaubt für die Sitzung: einmal fragen, danach nicht mehr
        await Chat(http, "https://mt.example.com/translate", "Erster Satz.");
        await Chat(http, "https://mt.example.com/translate", "Zweiter Satz.");
        Assert.Equal(2, asked.Count);
        Assert.Equal(2, fake.Calls);

        // lokal: nie fragen
        await Chat(http, "http://127.0.0.1:5000/translate", "Lokal.");
        Assert.Equal(2, asked.Count);
        Assert.Equal(new[] { OutboundDecision.Declined, OutboundDecision.Sent, OutboundDecision.Sent, OutboundDecision.Local },
            Outbound.Entries.Select(e => e.Decision));
    }

    [Fact]
    public async Task BlockedServicesNeverReceiveAnything()
    {
        var changed = 0;
        Action onChange = () => changed++;
        Outbound.SettingsChanged += onChange;
        try
        {
            Outbound.Approver = (_, _) => Task.FromResult(OutboundAnswer.BlockService);
            Outbound.Settings.Mode = OutboundMode.AskEveryTime;
            var fake = new Fake();
            using var http = new HttpClient(new OutboundHandler(OutboundKind.Web, fake));

            await Assert.ThrowsAsync<OutboundDeclinedException>(() => http.GetAsync("https://search.example.com/?q=Name"));
            Assert.Equal(["search.example.com"], Outbound.Settings.Blocked);
            Assert.Equal(1, changed);                                                    // Einstellungen werden gespeichert

            // auch ohne Rückfrage bleibt der Dienst gesperrt
            Outbound.Settings.Mode = OutboundMode.LogOnly;
            var ex = await Assert.ThrowsAsync<OutboundDeclinedException>(() => http.GetAsync("https://search.example.com/?q=Anderes"));
            Assert.Contains("gesperrt", ex.Message);
            Assert.Equal(0, fake.Calls);
            await http.GetAsync("https://other.example.com/robots.txt");                  // robots.txt: keine Nutzerdaten, kein Eintrag
            Assert.DoesNotContain(Outbound.Entries, e => e.Service == "other.example.com");
        }
        finally { Outbound.SettingsChanged -= onChange; }
    }

    [Fact]
    public async Task MachineTranslationGoesThroughTheCheck()
    {
        Outbound.Settings.Blocked.Add("mt.example.com");
        var mt = new Ai.MachineTranslationModel(new Ai.AiProviderConfig
        {
            Name = "Test-MÜ", Kind = Ai.AiProviderKind.MachineTranslation, Model = "libretranslate", BaseUrl = "https://mt.example.com",
        });
        var ex = await Assert.ThrowsAsync<OutboundDeclinedException>(() =>
            mt.TranslateAsync("Ein Satz.", "de-DE", "en-GB", TestContext.Current.CancellationToken));
        Assert.Contains("mt.example.com", ex.Message);
        Assert.Contains(Outbound.Entries, e => e is { Service: "mt.example.com", Kind: OutboundKind.MachineTranslation, Decision: OutboundDecision.Blocked }
                                                    && e.Content.Contains("Ein Satz."));
    }
}
