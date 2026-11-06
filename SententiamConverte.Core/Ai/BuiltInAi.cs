using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein offenes Sprachmodell, das die eingebaute KI herunterladen kann.</summary>
public sealed record BuiltInModel(string Id, string Name, string Description, string Repo, string Revision, string File, long Size, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repo}/resolve/{Revision}/{File}";
    public string SizeText => BuiltInAi.FormatSize(Size);
    public override string ToString() => $"{Name} – {Description} ({SizeText})";
}

/// <summary>Ein Download mit Prüfsumme (Programmpaket der Laufzeit).</summary>
public sealed record BuiltInPackage(string Url, long Size, string Sha256);

/// <summary>Fortschritt eines Downloads.</summary>
public sealed record DownloadProgress(string What, long Done, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>
/// Eingebaute KI: ein offenes Sprachmodell (Gemma 4, Apache-2.0) läuft mit der Laufzeit llama.cpp (MIT) auf diesem
/// Rechner – kostenlos, ohne Anmeldung, ohne Tageslimit, und kein Text verlässt den Rechner. Laufzeit und Modell
/// werden erst auf Wunsch aus den offiziellen Quellen geladen (GitHub-Release von llama.cpp, Hugging Face) und
/// über ihre SHA-256-Prüfsumme geprüft. Grafikkarten werden genutzt, wenn vorhanden (Vulkan/Metal), sonst der Prozessor.
/// Auf Android nicht verfügbar.
/// </summary>
public static class BuiltInAi
{
    /// <summary>Getestete Fassung von llama.cpp.</summary>
    public const string RuntimeBuild = "b12138";

    public static readonly IReadOnlyList<BuiltInModel> Models =
    [
        new("gemma-4-e2b", "Gemma 4 E2B", "schnell, auch ohne Grafikkarte", "ggml-org/gemma-4-E2B-it-GGUF", "6938c58e698502037401a5f19f6905f2fb2f7dee",
            "gemma-4-E2B-it-Q4_0.gguf", 2841481184, "8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52"),
        new("gemma-4-e4b", "Gemma 4 E4B", "bessere Übersetzungen, braucht mehr Rechenleistung", "ggml-org/gemma-4-E4B-it-GGUF", "a69f018d7678a2b9f5e705f8903d6fb29b9b25dc",
            "gemma-4-E4B-it-Q4_0.gguf", 4590807392, "a555b900214b477d8880e7832e0b8925e139b0159640036b09fe472b6f2097f2"),
    ];

    public static BuiltInModel? Model(string? id) => Models.FirstOrDefault(m => m.Id == id);

    private const string Release = "https://github.com/ggml-org/llama.cpp/releases/download/" + RuntimeBuild + "/llama-" + RuntimeBuild + "-bin-";

    /// <summary>Das passende Laufzeit-Paket für dieses System; null, wenn es keins gibt (Android, exotische Systeme).</summary>
    public static BuiltInPackage? Runtime
    {
        get
        {
            if (RuntimeOverride != null) return RuntimeOverride;
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return null;
            var arch = RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsWindows())
                return arch switch
                {
                    Architecture.X64 => new(Release + "win-vulkan-x64.zip", 37677878, "5fd9523dc1d4ce634a697d760addf5722ec8f7f2dc722234c75124d506d3bb41"),
                    Architecture.Arm64 => new(Release + "win-cpu-arm64.zip", 13222742, "bb3ed4f64d39512948365b97c3163cddeeff6b35dac3fb858a4b6f12aeb12137"),
                    _ => null,
                };
            if (OperatingSystem.IsMacOS())
                return arch switch
                {
                    Architecture.Arm64 => new(Release + "macos-arm64.tar.gz", 16670361, "ef898f71f6f5d773565b84670eb0e6b9acf24a64d4c045e2e99fa24a9c9f7e8c"),
                    Architecture.X64 => new(Release + "macos-x64.tar.gz", 12408482, "16cd528378489276861dffd896862a57bf7523c9151a71909f566c36f348f55c"),
                    _ => null,
                };
            if (OperatingSystem.IsLinux())
                return arch switch
                {
                    Architecture.X64 => new(Release + "ubuntu-x64.tar.gz", 23077553, "25509d3d9721926394361a78f8f242ac5213a5ba46b13c96a704bc77358f5760"),
                    Architecture.Arm64 => new(Release + "ubuntu-arm64.tar.gz", 18754438, "18cc629e75e31509c1378645b391b34c8e1d2241274fd116c5fb01aca71ae8f0"),
                    _ => null,
                };
            return null;
        }
    }

    /// <summary>Für Tests: anderes Laufzeit-Paket.</summary>
    internal static BuiltInPackage? RuntimeOverride { get; set; }

    public static bool IsSupported => Runtime != null;

    private static string? _folder;

    /// <summary>Wo Laufzeit und Modelle liegen (einstellbar, z. B. auf ein Laufwerk mit mehr Platz).</summary>
    public static string Folder
    {
        get => _folder is { Length: > 0 } f ? f : DefaultFolder;
        set => _folder = value;
    }

    public static string DefaultFolder => Path.Combine(AppPaths.DataRoot, "ki");

    private static string RuntimeDir => Path.Combine(Folder, "llama.cpp-" + RuntimeBuild);
    private static string ModelPath(BuiltInModel m) => Path.Combine(Folder, "modelle", m.File);

    public static string? ServerExecutable
    {
        get
        {
            if (!Directory.Exists(RuntimeDir)) return null;
            var name = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
            return Directory.EnumerateFiles(RuntimeDir, name, SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    public static bool IsRuntimeInstalled => ServerExecutable != null;

    public static bool IsModelInstalled(BuiltInModel m) => new FileInfo(ModelPath(m)) is { Exists: true } f && f.Length == m.Size;

    public static bool IsInstalled(BuiltInModel m) => IsRuntimeInstalled && IsModelInstalled(m);

    /// <summary>Wie viel noch heruntergeladen werden muss.</summary>
    public static long MissingBytes(BuiltInModel m) =>
        (IsRuntimeInstalled ? 0 : Runtime?.Size ?? 0) + (IsModelInstalled(m) ? 0 : m.Size - PartialLength(ModelPath(m) + ".part"));

    /// <summary>Freier Platz auf dem Laufwerk des Ordners (null, wenn unbekannt).</summary>
    public static long? FreeSpace(string? folder = null)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder ?? Folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{Math.Max(1, bytes / 1_000_000)} MB";

    /// <summary>Für Tests: andere Downloadquelle (z. B. ein lokaler Prüfserver).</summary>
    internal static Func<string, string>? UrlOverride { get; set; }

    /// <summary>Laufzeit und Modell herunterladen (abgebrochene Downloads werden fortgesetzt) und prüfen.</summary>
    public static async Task InstallAsync(BuiltInModel model, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var runtime = Runtime ?? throw new AiException("Die eingebaute KI gibt es für dieses System nicht.");
        Directory.CreateDirectory(Folder);
        var missing = MissingBytes(model);
        if (FreeSpace() is { } free && free < missing + 500_000_000)
            throw new AiException($"Zu wenig Speicherplatz: gebraucht {FormatSize(missing)}, frei {FormatSize(free)} auf {Path.GetPathRoot(Path.GetFullPath(Folder))}. " +
                                  "Einen anderen Ordner wählen.");

        using var http = Http();
        if (!IsRuntimeInstalled)
        {
            var archive = Path.Combine(Folder, Path.GetFileName(new Uri(runtime.Url).LocalPath));
            await DownloadAsync(http, runtime.Url, archive, runtime.Size, runtime.Sha256, "Laufzeit (llama.cpp)", progress, ct);
            var target = RuntimeDir + ".neu";
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Extract(archive, target);
            if (Directory.Exists(RuntimeDir)) Directory.Delete(RuntimeDir, true);
            Directory.Move(target, RuntimeDir);
            File.Delete(archive);
            if (!OperatingSystem.IsWindows())
                foreach (var f in Directory.EnumerateFiles(RuntimeDir, "*", SearchOption.AllDirectories).Where(f => !Path.HasExtension(f) || f.EndsWith(".so") || f.Contains(".so.") || f.EndsWith(".dylib")))
                    File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            if (!IsRuntimeInstalled) throw new AiException("Die Laufzeit wurde heruntergeladen, enthält aber kein llama-server.");
        }
        if (!IsModelInstalled(model))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath(model))!);
            await DownloadAsync(http, model.Url, ModelPath(model), model.Size, model.Sha256, model.Name, progress, ct);
        }
    }

    /// <summary>Heruntergeladenes Modell wieder entfernen (Platz freigeben).</summary>
    public static void Remove(BuiltInModel model)
    {
        if (CurrentModel == model.Id) Stop();
        foreach (var f in new[] { ModelPath(model), ModelPath(model) + ".part" })
            if (File.Exists(f)) File.Delete(f);
    }

    internal static HttpClient Http()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SententiamConverte/" + (typeof(BuiltInAi).Assembly.GetName().Version?.ToString(3) ?? "1") +
                                                     " (+https://github.com/Jakubix-tech/sententiam-converte)");
        return http;
    }

    private static long PartialLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>
    /// Datei laden – in eine .part-Datei, die bei einem neuen Versuch fortgesetzt wird – und Größe und Prüfsumme vergleichen.
    /// <paramref name="size"/> 0 = unbekannt (dann gilt die Angabe des Servers), <paramref name="sha256"/> null = nicht prüfen.
    /// </summary>
    internal static async Task DownloadAsync(HttpClient http, string url, string target, long size, string? sha256, string what,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        url = UrlOverride?.Invoke(url) ?? url;
        var part = target + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        if (File.Exists(part))
        {
            if (size > 0 && new FileInfo(part).Length > size) File.Delete(part);
            else
            {
                await using var existing = File.OpenRead(part);
                var buffer = new byte[1 << 20];
                int n;
                while ((n = await existing.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, n);
                done = existing.Length;
            }
        }
        progress?.Report(new DownloadProgress(what, done, size));
        if (size <= 0 || done < size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (done > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && size <= 0)
            {
                // schon vollständig (Größe war unbekannt)
                File.Move(part, target, overwrite: true);
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new AiException($"Download von {what} fehlgeschlagen: HTTP {(int)response.StatusCode}.");
            if (done > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server setzt nicht fort: von vorn
                hash.GetHashAndReset();
                done = 0;
                File.Delete(part);
            }
            if (size <= 0 && response.Content.Headers.ContentLength is { } length) size = done + length;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 20];
            var lastReport = Stopwatch.StartNew();
            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                hash.AppendData(buffer, 0, n);
                done += n;
                if (lastReport.ElapsedMilliseconds > 250)
                {
                    progress?.Report(new DownloadProgress(what, done, size));
                    lastReport.Restart();
                }
            }
        }
        progress?.Report(new DownloadProgress(what, done, size));
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if ((size > 0 && done != size) || (sha256 != null && actual != sha256))
        {
            File.Delete(part);
            throw new AiException($"{what}: die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht). Bitte erneut versuchen.");
        }
        File.Move(part, target, overwrite: true);
    }

    private static void Extract(string archive, string target)
    {
        Directory.CreateDirectory(target);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
            return;
        }
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
    }

    // ---------- Server ----------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> LogLines = new();
    private static Process? _process;

    /// <summary>Die letzten Meldungen der Laufzeit (für die Fehlersuche).</summary>
    public static IReadOnlyList<string> RecentLog => LogLines.ToArray();
    private static string? _url;
    private static string? _key;

    /// <summary>Welches Modell der laufende Server geladen hat.</summary>
    public static string? CurrentModel { get; private set; }

    /// <summary>Adresse (OpenAI-Schnittstelle, …/v1) und Schlüssel des lokalen Servers; startet ihn bei Bedarf mit dem Modell.</summary>
    public static async Task<(string BaseUrl, string Key)> EnsureServerAsync(BuiltInModel model, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false } && CurrentModel == model.Id && _url != null) return (_url, _key!);
            StopUnlocked();
            if (!IsInstalled(model))
                throw new AiException($"Die eingebaute KI ({model.Name}) ist noch nicht heruntergeladen: Einstellungen → KI einrichten → „Kostenlos auf diesem Rechner“.");
            var port = FreePort();
            _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var psi = new ProcessStartInfo(ServerExecutable!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ServerExecutable!)!,
            };
            foreach (var a in new[]
                     {
                         "-m", ModelPath(model), "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", _key,
                         "-c", "16384", "-ngl", "999", "--jinja", "-np", "1", "--reasoning", "off", "--no-webui",
                     })
                psi.ArgumentList.Add(a);
            LogLines.Clear();
            void Keep(string? line)
            {
                if (line == null) return;
                LogLines.Enqueue(line);
                while (LogLines.Count > 200) LogLines.TryDequeue(out _);
            }
            _process = Process.Start(psi) ?? throw new AiException("Die eingebaute KI ließ sich nicht starten.");
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            var url = $"http://127.0.0.1:{port}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            // Ein Modell von mehreren GB von einer Festplatte zu laden kann dauern
            for (var i = 0; i < 600; i++)
            {
                if (_process.HasExited)
                    throw new AiException("Die eingebaute KI wurde beim Start beendet: " + string.Join(" ", LogLines.TakeLast(3)));
                try
                {
                    if ((await http.GetAsync(url + "/health", ct)).IsSuccessStatusCode)
                    {
                        _url = url + "/v1";
                        CurrentModel = model.Id;
                        return (_url, _key);
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(500, ct);
            }
            StopUnlocked();
            throw new AiException("Die eingebaute KI antwortet nicht.");
        }
        finally { Gate.Release(); }
    }

    public static void Stop()
    {
        Gate.Wait();
        try { StopUnlocked(); }
        finally { Gate.Release(); }
    }

    private static void StopUnlocked()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process = null;
        _url = null;
        CurrentModel = null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>Sprachmodell der eingebauten KI: startet den lokalen Server und spricht ihn über die OpenAI-Schnittstelle an.</summary>
public sealed class BuiltInChatModel(AiProviderConfig config) : IChatModel
{
    public string DisplayName => $"{config.Name} ({config.Model})";

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var model = BuiltInAi.Model(config.Model) ?? throw new AiException($"Unbekanntes Modell der eingebauten KI: {config.Model}");
        var (url, key) = await BuiltInAi.EnsureServerAsync(model, ct);
        var inner = new AiProviderConfig
        {
            Name = config.Name, Kind = AiProviderKind.OpenAiCompatible, BaseUrl = url, ApiKey = key, Model = model.Id,
            Temperature = config.Temperature ?? 0.2, MaxTokens = config.MaxTokens, TimeoutSeconds = config.TimeoutSeconds,
        };
        return await new OpenAiCompatibleChatModel(inner).CompleteAsync(system, messages, ct);
    }
}
