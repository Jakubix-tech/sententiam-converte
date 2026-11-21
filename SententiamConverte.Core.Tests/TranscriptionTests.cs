using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Diktat: Aufnahme als WAV an eine Whisper-kompatible Spracherkennung.</summary>
public class TranscriptionTests
{
    [Fact]
    public async Task TheRecordingIsSentAsMultipartAndTheTextComesBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        string? body = null, path = null, auth = null;
        _ = Task.Run(async () =>
        {
            var ctx = await server.GetContextAsync();
            path = ctx.Request.Url!.AbsolutePath;
            auth = ctx.Request.Headers["Authorization"];
            body = await new StreamReader(ctx.Request.InputStream, Encoding.Latin1).ReadToEndAsync();
            var bytes = Encoding.UTF8.GetBytes("""{"text": " Die Schraube festziehen. "}""");
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }, ct);

        var wav = Transcriber.Wav(new byte[32000]);                       // 1 s Stille
        Assert.Equal(44 + 32000, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));

        var config = new TranscriptionConfig { BaseUrl = $"http://127.0.0.1:{port}/v1", ApiKey = "geheim", Model = "whisper-large-v3-turbo" };
        Assert.True(config.IsLocal);
        var text = await Transcriber.TranscribeAsync(wav, "de-DE", config, ct);
        Assert.Equal("Die Schraube festziehen.", text);
        Assert.Equal("/v1/audio/transcriptions", path);
        Assert.Equal("Bearer geheim", auth);
        Assert.Contains("name=model", body);
        Assert.Contains("whisper-large-v3-turbo", body);
        Assert.Contains("name=language", body);
        Assert.Contains("\r\nde\r\n", body);
        Assert.Contains("filename=diktat.wav", body);

        await Assert.ThrowsAsync<AiException>(() => Transcriber.TranscribeAsync(wav, "de-DE", new TranscriptionConfig(), ct));
    }
}
