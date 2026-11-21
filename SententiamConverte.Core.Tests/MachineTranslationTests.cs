using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Tests;

/// <summary>Klassische maschinelle Übersetzung: Anfragen je Dienst, Tags hin und zurück, Datenschutz.</summary>
[Collection("BuiltInAi")]
public sealed class MachineTranslationTests
{
    private sealed record Seen(string Path, string Query, Dictionary<string, string> Headers, string Body);

    /// <summary>Ein nachgebauter Dienst: ersetzt deutsche Wörter durch englische und lässt das Markup stehen.</summary>
    private static HttpListener Fake(string service, ConcurrentQueue<Seen> seen, out string url)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        url = $"http://127.0.0.1:{port}";
        var server = new HttpListener();
        server.Prefixes.Add(url + "/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                seen.Enqueue(new Seen(ctx.Request.Url!.AbsolutePath, ctx.Request.Url.Query,
                    ctx.Request.Headers.AllKeys.ToDictionary(k => k!, k => ctx.Request.Headers[k]!), body));
                var json = JsonNode.Parse(body)!;
                string text = service switch
                {
                    "deepl" => (string)json["text"]![0]!,
                    "google" => (string)json["q"]![0]!,
                    "microsoft" => (string)json[0]!["Text"]!,
                    _ => (string)json["q"]!,
                };
                var translated = text.Replace("Ziehen Sie die Schraube mit", "Tighten the screw to").Replace(" an.", ".")
                    .Replace("Herr", "Mr").Replace("unterschreibt", "signs").Replace("&amp;", "&amp;");
                if (service == "google") translated = translated.Replace("'", "&#39;");     // HTML-Entitäten wie im echten Dienst
                JsonNode answer = service switch
                {
                    "deepl" => new JsonObject { ["translations"] = new JsonArray(new JsonObject { ["text"] = translated }) },
                    "google" => new JsonObject { ["data"] = new JsonObject { ["translations"] = new JsonArray(new JsonObject { ["translatedText"] = translated }) } },
                    "microsoft" => new JsonArray(new JsonObject { ["translations"] = new JsonArray(new JsonObject { ["text"] = translated, ["to"] = "en" }) }),
                    _ => new JsonObject { ["translatedText"] = translated },
                };
                var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        return server;
    }

    [Fact]
    public void TagsTravelAsXmlAndComeBack()
    {
        var markup = MachineTranslationModel.ToMarkup("Mit ⟨1⟩5 <Nm>⟨/1⟩ & ⟨2⟩fest⟨/2⟩⟨3/⟩ anziehen.");
        Assert.Equal("Mit <g id=\"1\">5 &lt;Nm&gt;</g> &amp; <g id=\"2\">fest</g><x id=\"3\"/> anziehen.", markup);
        Assert.Equal("Tighten ⟨2⟩firmly⟨/2⟩ to ⟨1⟩5 <Nm>⟨/1⟩ & ⟨3/⟩.",
            MachineTranslationModel.FromMarkup("Tighten <g id=\"2\">firmly</g> to <g id=\"1\">5 &lt;Nm&gt;</g> &amp; <x id=\"3\"></x>."));
        Assert.Equal("⟨1⟩a ⟨2⟩b⟨/2⟩⟨/1⟩", MachineTranslationModel.FromMarkup("<g id='1'>a <g id='2'>b</g></g>"));   // verschachtelt

        Assert.Equal("EN-GB", MachineTranslationModel.Code("deepl", "en-GB", true));
        Assert.Equal("EN-US", MachineTranslationModel.Code("deepl", "en-US", true));
        Assert.Equal("DE", MachineTranslationModel.Code("deepl", "de-AT", false));
        Assert.Equal("PT-BR", MachineTranslationModel.Code("deepl", "pt-BR", true));
        Assert.Equal("zh-CN", MachineTranslationModel.Code("google", "zh-CN", true));
        Assert.Equal("zh-Hans", MachineTranslationModel.Code("microsoft", "zh-CN", true));
        Assert.Equal("pl", MachineTranslationModel.Code("libretranslate", "pl-PL", true));
    }

    [Theory]
    [InlineData("deepl")]
    [InlineData("google")]
    [InlineData("microsoft")]
    [InlineData("libretranslate")]
    public async Task EachServiceIsCalledThroughItsOfficialInterface(string service)
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new ConcurrentQueue<Seen>();
        using var server = Fake(service, seen, out var url);
        var config = new AiProviderConfig
        {
            Name = "MÜ", Kind = AiProviderKind.MachineTranslation, Model = service, ApiKey = "geheim:fx", BaseUrl = url, Region = "westeurope",
        };
        var mt = Assert.IsType<MachineTranslationModel>(ChatModelFactory.CreateRaw(config));
        var result = await mt.TranslateAsync("Ziehen Sie die Schraube mit ⟨1⟩5 Nm⟨/1⟩ an.", "de-DE", "en-GB", ct);
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", result);

        var request = Assert.Single(seen);
        var body = JsonNode.Parse(request.Body)!;
        switch (service)
        {
            case "deepl":
                Assert.Equal("/v2/translate", request.Path);
                Assert.Equal("DeepL-Auth-Key geheim:fx", request.Headers["Authorization"]);
                Assert.Equal(("DE", "EN-GB", "xml"), ((string)body["source_lang"]!, (string)body["target_lang"]!, (string)body["tag_handling"]!));
                break;
            case "google":
                Assert.Equal("/language/translate/v2", request.Path);
                Assert.Contains("key=geheim", request.Query);
                Assert.Equal(("de", "en", "html"), ((string)body["source"]!, (string)body["target"]!, (string)body["format"]!));
                break;
            case "microsoft":
                Assert.Equal("/translate", request.Path);
                Assert.Contains("from=de", request.Query);
                Assert.Contains("to=en", request.Query);
                Assert.Contains("textType=html", request.Query);
                Assert.Equal("geheim:fx", request.Headers["Ocp-Apim-Subscription-Key"]);
                Assert.Equal("westeurope", request.Headers["Ocp-Apim-Subscription-Region"]);
                break;
            default:
                Assert.Equal("/translate", request.Path);
                Assert.Equal("html", (string)body["format"]!);
                break;
        }
        // für Chat-Aufgaben (Recherche, Prüfung) klare Meldung statt Absturz
        var ex = await Assert.ThrowsAsync<AiException>(() => mt.AskAsync("s", "u", ct));
        Assert.Contains("Sprachmodell", ex.Message);
    }

    [Fact]
    public async Task TheTranslatorUsesTheServiceAndKeepsPrivacy()
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new ConcurrentQueue<Seen>();
        using var server = Fake("deepl", seen, out var url);
        var before = PrivacyContext.Current;
        try
        {
            // ein öffentlicher Dienst: Datenschutz greift (Adresse des Prüfservers ist lokal, daher auch an lokale)
            PrivacyContext.Current = new PrivacySettings { Enabled = true, AlsoLocalAi = true };
            var config = new AiProviderConfig { Name = "MÜ", Kind = AiProviderKind.MachineTranslation, Model = "deepl", ApiKey = "k", BaseUrl = url };
            var model = ChatModelFactory.Create(config);
            Assert.IsType<PrivacyChatModel>(model);
            var result = await new AiTranslator(new AgentContext { Model = model }).TranslateAsync("Herr Müller unterschreibt.", "de-DE", "en-GB", "Recht", ct: ct);
            Assert.Equal("Mr Müller signs.", result);
            Assert.DoesNotContain(seen, s => s.Body.Contains("Müller"));                  // nur [NAME_1] ging hinaus
        }
        finally { PrivacyContext.Current = before; }
    }
}
