using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Prüft den echten HTTP-Weg zu einem OpenAI-kompatiblen Server (wie llama.cpp, Ollama, LM Studio).</summary>
public class AiClientTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task OpenAiCompatibleServerTranslatesWithContext()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        JsonNode? received = null;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            received = JsonNode.Parse(await new StreamReader(ctx.Request.InputStream).ReadToEndAsync());
            var answer = """{"choices":[{"message":{"role":"assistant","content":"<think>überlegen …</think>Ziehen Sie die Schraube mit ⟨1⟩5 Nm⟨/1⟩ an."}}]}""";
            var bytes = Encoding.UTF8.GetBytes(answer);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var config = new AiProviderConfig { Name = "Testserver", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://localhost:{port}/v1", Model = "test", Temperature = 0.1 };
        Assert.True(config.IsLocal);
        var agent = new AiTranslator(new AgentContext { Model = ChatModelFactory.Create(config) });
        var result = await agent.TranslateAsync("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", "en-GB", "de-DE", "Maschinenbau",
            ct: TestContext.Current.CancellationToken);
        await server;

        Assert.Equal("Ziehen Sie die Schraube mit ⟨1⟩5 Nm⟨/1⟩ an.", result);       // Denkblock entfernt
        Assert.Equal("test", (string?)received!["model"]);
        var user = (string?)received["messages"]![1]!["content"];
        Assert.Contains("Maschinenbau", user);
        Assert.Contains("Tighten the screw", user);
    }

    [Fact]
    public async Task ServerErrorsAreReadable()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 503;
            await ctx.Response.OutputStream.WriteAsync("Modell wird geladen"u8.ToArray());
            ctx.Response.Close();
        });
        var model = ChatModelFactory.Create(new AiProviderConfig { Name = "Lokal", BaseUrl = $"http://localhost:{port}/v1", Model = "x" });
        var ex = await Assert.ThrowsAsync<AiException>(() => model.AskAsync("s", "u", TestContext.Current.CancellationToken));
        await server;
        Assert.Contains("503", ex.Message);
        Assert.Contains("Modell wird geladen", ex.Message);
    }
}
