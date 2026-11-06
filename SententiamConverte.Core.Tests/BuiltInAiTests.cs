using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

[CollectionDefinition("BuiltInAi", DisableParallelization = true)]
public sealed class BuiltInAiCollection;

/// <summary>Eingebaute KI und OpenCode-Download gegen einen lokalen Prüfserver – ohne echte Downloads.</summary>
[Collection("BuiltInAi")]
public sealed class BuiltInAiTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentQueue<string> _ranges = new();
    private readonly string _base;
    private readonly string _oldReleaseApi = OpenCodeInstaller.ReleaseApi;

    public BuiltInAiTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
        BuiltInAi.Folder = _dir.Path;
        BuiltInAi.UrlOverride = u => u.Replace("https://huggingface.co/", _base + "hf/");
    }

    public void Dispose()
    {
        BuiltInAi.Folder = "";
        BuiltInAi.UrlOverride = null;
        BuiltInAi.RuntimeOverride = null;
        OpenCodeInstaller.ReleaseApi = _oldReleaseApi;
        _listener.Close();
        _dir.Dispose();
    }

    /// <summary>Liefert Dateien aus; mit „Range: bytes=n-“ nur den Rest (206), wie Hugging Face und GitHub.</summary>
    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
            if (!_files.TryGetValue(path, out var data))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }
            var from = 0;
            if (ctx.Request.Headers["Range"] is { } range)
            {
                _ranges.Enqueue(range);
                from = int.Parse(range.Replace("bytes=", "").TrimEnd('-'));
                ctx.Response.StatusCode = 206;
            }
            ctx.Response.ContentLength64 = data.Length - from;
            await ctx.Response.OutputStream.WriteAsync(data.AsMemory(from));
            ctx.Response.Close();
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Zip(string entry, string content = "programm")
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(content);
        }
        return ms.ToArray();
    }

    private static string ServerName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    /// <summary>Laufzeit (zip) und ein kleines „Modell“ auf dem Prüfserver bereitstellen.</summary>
    private BuiltInModel Offer(byte[] model, string? sha = null)
    {
        var runtime = Zip("build/bin/" + ServerName);
        _files["runtime.zip"] = runtime;
        BuiltInAi.RuntimeOverride = new BuiltInPackage(_base + "runtime.zip", runtime.Length, Sha(runtime));
        _files["hf/test/modell/resolve/abc/modell.gguf"] = model;
        return new BuiltInModel("test", "Testmodell", "klein", "test/modell", "abc", "modell.gguf", model.Length, sha ?? Sha(model));
    }

    [Fact]
    public async Task InstallsRuntimeAndModelAndChecksTheChecksum()
    {
        var data = RandomNumberGenerator.GetBytes(3_000_000);
        var model = Offer(data);
        Assert.False(BuiltInAi.IsInstalled(model));
        Assert.Equal(data.Length + _files["runtime.zip"].Length, BuiltInAi.MissingBytes(model));

        var reports = new List<DownloadProgress>();
        await BuiltInAi.InstallAsync(model, new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.True(BuiltInAi.IsInstalled(model));
        Assert.EndsWith(ServerName, BuiltInAi.ServerExecutable);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_dir.Path, "modelle", "modell.gguf")));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.part", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.zip"));                       // Archiv der Laufzeit aufgeräumt
        Assert.Contains(reports, r => r.What == "Testmodell" && r.Done == data.Length);
        Assert.Equal(0, BuiltInAi.MissingBytes(model));

        BuiltInAi.Remove(model);
        Assert.False(BuiltInAi.IsModelInstalled(model));
        Assert.True(BuiltInAi.IsRuntimeInstalled);
    }

    [Fact]
    public async Task InterruptedDownloadContinuesWhereItStopped()
    {
        var data = RandomNumberGenerator.GetBytes(2_000_000);
        var model = Offer(data);
        Directory.CreateDirectory(Path.Combine(_dir.Path, "modelle"));
        File.WriteAllBytes(Path.Combine(_dir.Path, "modelle", "modell.gguf.part"), data[..1_200_000]);
        Assert.Equal(800_000 + _files["runtime.zip"].Length, BuiltInAi.MissingBytes(model));

        await BuiltInAi.InstallAsync(model, null, TestContext.Current.CancellationToken);

        Assert.Contains("bytes=1200000-", _ranges);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_dir.Path, "modelle", "modell.gguf")));
    }

    [Fact]
    public async Task DamagedDownloadIsRejected()
    {
        var model = Offer(RandomNumberGenerator.GetBytes(500_000), sha: new string('0', 64));
        var ex = await Assert.ThrowsAsync<AiException>(() => BuiltInAi.InstallAsync(model, null, TestContext.Current.CancellationToken));
        Assert.Contains("beschädigt", ex.Message);
        Assert.False(BuiltInAi.IsModelInstalled(model));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.Path, "modelle"), "*.part"));   // beim nächsten Mal von vorn
    }

    [Fact]
    public async Task MissingModelGivesAClearMessage()
    {
        var config = AiSettings.BuiltInPreset();
        Assert.True(config.IsLocal);
        var ex = await Assert.ThrowsAsync<AiException>(() => ChatModelFactory.Create(config).AskAsync("system", "Hallo", TestContext.Current.CancellationToken));
        Assert.Contains("noch nicht heruntergeladen", ex.Message);
    }

    [Fact]
    public void NewInstallationsStartWithTheFreeBuiltInAi()
    {
        var path = Path.Combine(_dir.Path, "ki-einstellungen.json");
        var settings = AiSettings.Load(path);
        Assert.Equal(AiProviderKind.BuiltIn, settings.Providers[0].Kind);
        Assert.Equal("gemma-4-e2b", settings.Providers[0].Model);
        Assert.Same(settings.Providers[0], settings.Get(settings.TranslateProviderId));

        // Gewählter Ordner bleibt erhalten und gilt nach dem Laden
        settings.BuiltInFolder = Path.Combine(_dir.Path, "anderer Ordner");
        settings.Save(path);
        BuiltInAi.Folder = "";
        Assert.Equal(Path.Combine(_dir.Path, "anderer Ordner"), AiSettings.Load(path).BuiltInFolder);
        Assert.Equal(Path.Combine(_dir.Path, "anderer Ordner"), BuiltInAi.Folder);
    }

    [Fact]
    public async Task OpenCodeIsDownloadedFromTheReleaseAndChecked()
    {
        if (!OpenCodeInstaller.IsSupported) return;
        var exe = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
        var archive = Zip(exe);
        _files["dl/" + OpenCodeInstaller.AssetName] = archive;
        string Release(string digest) =>
            $$"""{"tag_name":"v9.9.9","assets":[{"name":"{{OpenCodeInstaller.AssetName}}","size":{{archive.Length}},"digest":"sha256:{{digest}}","browser_download_url":"{{_base}}dl/{{OpenCodeInstaller.AssetName}}"}]}""";

        _files["api/falsch"] = Encoding.UTF8.GetBytes(Release(new string('1', 64)));
        OpenCodeInstaller.ReleaseApi = _base + "api/falsch";
        await Assert.ThrowsAsync<AiException>(() => OpenCodeInstaller.InstallAsync(null, TestContext.Current.CancellationToken));
        Assert.Null(OpenCodeInstaller.Executable);

        _files["api/latest"] = Encoding.UTF8.GetBytes(Release(Sha(archive)));
        OpenCodeInstaller.ReleaseApi = _base + "api/latest";
        await OpenCodeInstaller.InstallAsync(null, TestContext.Current.CancellationToken);
        Assert.NotNull(OpenCodeInstaller.Executable);
        Assert.Contains("v9.9.9", OpenCodeInstaller.Executable);
        Assert.Empty(Directory.GetFiles(OpenCodeInstaller.Folder, "*.zip"));
    }

    /// <summary>Fortschritt ohne Umweg über den Synchronisationskontext (sonst kämen Meldungen nach dem Test an).</summary>
    private sealed class SyncProgress(List<DownloadProgress> list) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (list) list.Add(value);
        }
    }
}
