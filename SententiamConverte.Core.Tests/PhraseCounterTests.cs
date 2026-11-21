using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Wendungen in den eigenen Daten zählen; DWDS-Häufigkeit für deutsche Einzelwörter.</summary>
public sealed class PhraseCounterTests
{
    [Fact]
    public void PhrasesAreCountedExactlyPerDatabase()
    {
        using var dir = new TempDir();
        using var project = new TranslationMemory(dir.File("projekt.sctm"), "Projekt-TM");
        using var opus = new TranslationMemory(dir.File("dgt.sctm"), "DGT de-en");
        project.Add("de-DE", "en-GB", "Die Pumpe ausschalten.", "Switch off the pump.");
        project.Add("de-DE", "en-GB", "Die Pumpe prüfen.", "Check the pump.");
        opus.Add("de-DE", "en-GB", "Vor dem Start die Pumpe füllen.", "Fill the pump before starting.");
        opus.Add("de-DE", "en-GB", "Pumpe die Anlage leer.", "Pump the system empty.");               // andere Wortfolge
        opus.Add("en-GB", "de-DE", "The user switches off the pump.", "Der Nutzer schaltet die Pumpe aus."); // Deutsch im Ziel
        opus.Add("de-DE", "en-GB", "Der Benutzer wartet.", "The user waits.");

        var f = PhraseCounter.Count([project, opus], "die Pumpe", "de-DE");
        Assert.Equal(4, f.Units);                                                         // „Pumpe die Anlage“ zählt nicht
        Assert.Equal([("Projekt-TM", 2L), ("DGT de-en", 2L)], f.PerTm);
        Assert.Equal(2, PhraseCounter.Count([project], "the pump", "en").Units);
        Assert.Equal(0, PhraseCounter.Count([project, opus], "Pumpe die Pumpe", "de-DE").Units);

        Assert.Equal(["Benutzer", "Nutzer"], PhraseCounter.Variants(" Benutzer | Nutzer |Benutzer"));
        Assert.Equal(1, PhraseCounter.Count([project, opus], "Benutzer", "de").Units);
        Assert.Equal(1, PhraseCounter.Count([project, opus], "Nutzer", "de").Units);
    }

    [Fact]
    public async Task DwdsFrequencyComesFromTheOfficialInterface()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        string? path = null;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                if (ctx.Request.Url!.AbsolutePath == "/robots.txt") { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                path = ctx.Request.Url.PathAndQuery;
                var body = Encoding.UTF8.GetBytes("""{"q":"Pumpe","lemma":["Pumpe"],"total":100000000,"hits":12345,"frequency":3}""");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
        var old = Dictionaries.DwdsBase;
        Dictionaries.DwdsBase = $"http://127.0.0.1:{port}";
        try
        {
            var f = await Dictionaries.DwdsFrequencyAsync("Pumpe", TestContext.Current.CancellationToken);
            Assert.Equal((12345L, 3), f);
            Assert.Equal("/api/frequency/?q=Pumpe", path);
        }
        finally { Dictionaries.DwdsBase = old; }
    }
}
