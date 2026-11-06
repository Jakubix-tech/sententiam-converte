using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SententiamConverte.Core.Ai;

/// <summary>Ein Modell, das die eigene OpenCode-Installation anbietet.</summary>
public sealed record OpenCodeModel(string ProviderId, string ModelId, string Name, bool Free, string? DataNote)
{
    public string Id => ProviderId + "/" + ModelId;
    public override string ToString() => Free ? $"{Name} – kostenlos" : Name;
}

/// <summary>
/// Startet und verwaltet einen OpenCode-Server (<c>opencode serve</c>) auf diesem Rechner. Die kostenlosen Modelle von
/// OpenCode dürfen laut OpenCode nur über OpenCode selbst genutzt werden – deshalb laufen alle Anfragen über die
/// eigene OpenCode-Installation des Benutzers und deren dokumentierte Server-Schnittstelle.
/// </summary>
public static class OpenCodeServer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static string? _url;
    private static string? _password;

    /// <summary>Hinweise zur Datennutzung der kostenlosen Modelle (Stand Oktober 2026, laut OpenCode-Dokumentation).</summary>
    private static readonly Dictionary<string, string> DataNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.3-flash-free"] = "keine Speicherung, kein Training",
        ["deepseek-v4.1-flash-free"] = "keine Speicherung, kein Training",
        ["beam-free"] = "kein Training, 30 Tage gespeichert",
        ["big-pickle"] = "Eingaben können zum Training genutzt werden",
        ["mimo-v2.6-flash-free"] = "Eingaben können zum Training genutzt werden",
        ["flint-40b-free"] = "Training möglich, 30 Tage gespeichert",
        ["nemotron-3-ultra-free"] = "Testnutzung, keine vertraulichen Daten",
        ["nemotron-3.5-lightning-free"] = "Testnutzung, keine vertraulichen Daten",
        ["nemotron-3.5-super-vl-free"] = "Testnutzung, keine vertraulichen Daten",
    };

    /// <summary>Sucht das OpenCode-Programm (PATH, npm-Installation, Installationsskript, vom Programm geladene Fassung).</summary>
    public static string? FindExecutable() => FindInstalled() ?? OpenCodeInstaller.Executable;

    private static string? FindInstalled()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "opencode.exe", "opencode.cmd" } : new[] { "opencode" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".opencode", "bin"));
        dirs.Add(Path.Combine(home, ".local", "bin"));
        if (OperatingSystem.IsWindows()) dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var dir in dirs)
            foreach (var name in names)
            {
                var path = Path.Combine(dir.Trim('"'), name);
                if (!File.Exists(path)) continue;
                if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return path;
                // npm-Starter: das eigentliche Programm liegt im Paketordner
                var exe = Path.Combine(dir.Trim('"'), "node_modules", "opencode-ai", "bin", "opencode.exe");
                return File.Exists(exe) ? exe : path;
            }
        return null;
    }

    public static bool IsInstalled => FindExecutable() != null;

    /// <summary>Adresse und Kennwort eines laufenden Servers; startet bei Bedarf einen eigenen.</summary>
    public static async Task<(string Url, string? Password)> EnsureAsync(AiProviderConfig config, CancellationToken ct = default)
    {
        if (config.BaseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (config.BaseUrl.TrimEnd('/'), config.ResolveApiKey() is { Length: > 0 } pw ? pw : null);
        await Gate.WaitAsync(ct);
        try
        {
            if (_process is { HasExited: false } && _url != null) return (_url, _password);
            var exe = FindExecutable() ?? throw new AiException("OpenCode ist auf diesem Rechner nicht installiert (https://opencode.ai).");
            var port = FreePort();
            _password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(exe);
            }
            foreach (var a in new[] { "serve", "--port", port.ToString(), "--hostname", "127.0.0.1" }) psi.ArgumentList.Add(a);
            psi.Environment["OPENCODE_SERVER_PASSWORD"] = _password;
            _process = Process.Start(psi) ?? throw new AiException("OpenCode ließ sich nicht starten.");
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            _url = $"http://127.0.0.1:{port}";

            using var http = Client(_url, _password, TimeSpan.FromSeconds(3));
            for (var i = 0; i < 60; i++)
            {
                if (_process.HasExited) throw new AiException("OpenCode wurde beim Start beendet.");
                try
                {
                    if ((await http.GetAsync("config/providers", ct)).IsSuccessStatusCode) return (_url, _password);
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(500, ct);
            }
            Stop();
            throw new AiException("OpenCode antwortet nicht.");
        }
        finally { Gate.Release(); }
    }

    public static void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process = null;
        _url = null;
    }

    internal static HttpClient Client(string url, string? password, TimeSpan timeout)
    {
        var http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = timeout };
        if (password != null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        return http;
    }

    /// <summary>Alle Modelle der OpenCode-Installation; kostenlose zuerst.</summary>
    public static async Task<List<OpenCodeModel>> ModelsAsync(AiProviderConfig config, CancellationToken ct = default)
    {
        var (url, pw) = await EnsureAsync(config, ct);
        using var http = Client(url, pw, TimeSpan.FromSeconds(30));
        var root = JsonNode.Parse(await http.GetStringAsync("config/providers", ct))!;
        var list = new List<OpenCodeModel>();
        foreach (var p in root["providers"]?.AsArray() ?? [])
        {
            var providerId = (string?)p!["id"] ?? "";
            foreach (var (modelId, m) in p["models"]?.AsObject() ?? [])
            {
                var cost = m?["cost"];
                var free = cost != null && (double?)cost["input"] == 0 && (double?)cost["output"] == 0;
                var name = (string?)m?["name"] ?? modelId;
                list.Add(new OpenCodeModel(providerId, modelId, $"{name} ({providerId})", free, DataNotes.GetValueOrDefault(modelId)));
            }
        }
        // Kostenlose OpenCode-Modelle mit „keine Speicherung“ zuerst, dann übrige kostenlose, dann der Rest
        return list.OrderByDescending(m => m.Free && m.ProviderId == "opencode" && m.DataNote?.StartsWith("keine") == true)
            .ThenByDescending(m => m.Free && m.ProviderId == "opencode")
            .ThenByDescending(m => m.Free).ThenBy(m => m.Name).ToList();
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

/// <summary>
/// Sprachmodell über OpenCode: legt eine Sitzung an, schickt die Anfrage (ohne Werkzeuge), wartet auf die Antwort
/// und löscht die Sitzung wieder. Ist das kostenlose Kontingent aufgebraucht, kommt eine klare Meldung statt Warten.
/// </summary>
public sealed class OpenCodeChatModel(AiProviderConfig config) : IChatModel
{
    private static Dictionary<string, bool>? _noTools;

    public string DisplayName => $"{config.Name} ({config.Model})";

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var slash = config.Model.IndexOf('/');
        if (slash <= 0) throw new AiException("Modell als „Anbieter/Modell“ angeben, z. B. opencode/glm-5.3-flash-free.");
        var (url, pw) = await OpenCodeServer.EnsureAsync(config, ct);
        using var http = OpenCodeServer.Client(url, pw, TimeSpan.FromSeconds(60));

        _noTools ??= (await http.GetFromJsonAsync<List<string>>("experimental/tool/ids", ct) ?? []).ToDictionary(t => t, _ => false);
        var text = messages.Count == 1
            ? messages[0].Text
            : string.Join("\n\n", messages.Select(m => (m.FromUser ? "Nutzer: " : "Assistent: ") + m.Text));

        var session = JsonNode.Parse(await (await Check(await http.PostAsJsonAsync("session", new { title = "Sententiam converte" }, ct)))
            .Content.ReadAsStringAsync(ct))!;
        var id = (string)session["id"]!;
        try
        {
            var body = new JsonObject
            {
                ["model"] = new JsonObject { ["providerID"] = config.Model[..slash], ["modelID"] = config.Model[(slash + 1)..] },
                ["system"] = system,
                ["tools"] = JsonSerializerNode(_noTools),
                ["parts"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            };
            await Check(await http.PostAsync($"session/{id}/prompt_async", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct));

            var deadline = DateTime.UtcNow.AddSeconds(config.TimeoutSeconds);
            while (true)
            {
                await Task.Delay(700, ct);
                var status = JsonNode.Parse(await http.GetStringAsync("session/status", ct))?[id];
                if (status == null) break;                                    // fertig
                if ((string?)status["type"] == "retry")
                {
                    var reason = (string?)status["action"]?["reason"];
                    var message = (string?)status["message"] ?? "";
                    if (reason == "free_tier_limit" || message.Contains("Free usage", StringComparison.OrdinalIgnoreCase))
                    {
                        var next = (long?)status["next"] is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : (DateTime?)null;
                        throw new AiException("Das kostenlose Kontingent von OpenCode ist aufgebraucht" +
                                              (next != null ? $" – wieder verfügbar ab {next:g}" : "") + ". Bis dahin ein anderes Modell wählen.");
                    }
                    if ((int?)status["attempt"] >= 3) throw new AiException("OpenCode: " + message);
                }
                if (DateTime.UtcNow > deadline) throw new AiException("OpenCode antwortet nicht rechtzeitig.");
            }

            var all = JsonNode.Parse(await http.GetStringAsync($"session/{id}/message", ct))!.AsArray();
            var answer = all.LastOrDefault(m => (string?)m?["info"]?["role"] == "assistant")
                         ?? throw new AiException("OpenCode hat keine Antwort geliefert.");
            if (answer["info"]?["error"] is { } error)
            {
                var message = (string?)error["data"]?["message"] ?? (string?)error["name"] ?? error.ToJsonString();
                if (message.Contains("not available in your country", StringComparison.OrdinalIgnoreCase))
                    throw new AiException($"OpenCode: Das Modell {config.Model} ist in diesem Land nicht verfügbar – ein anderes Modell wählen.");
                throw new AiException("OpenCode: " + message);
            }
            var result = string.Concat((answer["parts"]?.AsArray() ?? [])
                .Where(p => (string?)p?["type"] == "text" && (bool?)p?["synthetic"] != true)
                .Select(p => (string?)p!["text"])).Trim();
            if (result.Length == 0) throw new AiException("OpenCode hat keine Antwort geliefert.");
            return result;
        }
        catch (OperationCanceledException)
        {
            await Quietly(() => http.PostAsync($"session/{id}/abort", null));
            throw;
        }
        catch (AiException)
        {
            await Quietly(() => http.PostAsync($"session/{id}/abort", null));
            throw;
        }
        finally
        {
            await Quietly(() => http.DeleteAsync($"session/{id}"));          // die Sitzung soll nicht in OpenCode liegen bleiben
        }
    }

    private static JsonObject JsonSerializerNode(Dictionary<string, bool> tools)
    {
        var o = new JsonObject();
        foreach (var (k, v) in tools) o[k] = v;
        return o;
    }

    private static async Task<HttpResponseMessage> Check(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return r;
        var text = await r.Content.ReadAsStringAsync();
        throw new AiException($"OpenCode: {(int)r.StatusCode} {text}".Trim());
    }

    private static async Task Quietly(Func<Task<HttpResponseMessage>> call)
    {
        try { (await call()).Dispose(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }
}

/// <summary>
/// Lädt OpenCode auf Wunsch aus dem offiziellen GitHub-Release (MIT-Lizenz) in den Ordner der eingebauten KI –
/// für alle, die OpenCode nicht selbst installiert haben. Die Prüfsumme aus dem Release wird verglichen.
/// </summary>
public static class OpenCodeInstaller
{
    /// <summary>Für Tests: andere Release-Abfrage.</summary>
    internal static string ReleaseApi { get; set; } = "https://api.github.com/repos/anomalyco/opencode/releases/latest";

    public static string Folder => Path.Combine(BuiltInAi.Folder, "opencode");

    /// <summary>Name des Release-Pakets für dieses System; null, wenn es keins gibt (Android).</summary>
    public static string? AssetName
    {
        get
        {
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return null;
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => null,
            };
            if (arch == null) return null;
            if (OperatingSystem.IsWindows()) return $"opencode-windows-{arch}.zip";
            if (OperatingSystem.IsMacOS()) return $"opencode-darwin-{arch}.zip";
            if (OperatingSystem.IsLinux()) return $"opencode-linux-{arch}.tar.gz";
            return null;
        }
    }

    public static bool IsSupported => AssetName != null;

    public static string? Executable
    {
        get
        {
            if (!Directory.Exists(Folder)) return null;
            var name = OperatingSystem.IsWindows() ? "opencode.exe" : "opencode";
            return Directory.EnumerateFiles(Folder, name, SearchOption.AllDirectories).FirstOrDefault();
        }
    }

    /// <summary>Neueste Fassung laden, prüfen und entpacken.</summary>
    public static async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var assetName = AssetName ?? throw new AiException("OpenCode gibt es für dieses System nicht.");
        using var http = BuiltInAi.Http();
        var release = JsonNode.Parse(await http.GetStringAsync(ReleaseApi, ct))!;
        var asset = (release["assets"]?.AsArray() ?? []).FirstOrDefault(a => (string?)a?["name"] == assetName)
                    ?? throw new AiException($"Im aktuellen OpenCode-Release fehlt das Paket {assetName}.");
        var digest = (string?)asset["digest"] ?? "";
        if (!digest.StartsWith("sha256:")) throw new AiException("Das OpenCode-Release enthält keine Prüfsumme – Download abgebrochen.");
        var size = (long?)asset["size"] ?? 0;
        if (BuiltInAi.FreeSpace(BuiltInAi.Folder) is { } free && free < size * 4)
            throw new AiException($"Zu wenig Speicherplatz für OpenCode ({BuiltInAi.FormatSize(size * 3)} nötig).");

        Directory.CreateDirectory(Folder);
        var archive = Path.Combine(Folder, assetName);
        await BuiltInAi.DownloadAsync(http, (string)asset["browser_download_url"]!, archive, size, digest[7..], "OpenCode", progress, ct);
        var version = ((string?)release["tag_name"] ?? "neu").Trim();
        var target = Path.Combine(Folder, version);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        if (archive.EndsWith(".zip")) System.IO.Compression.ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzip, target, overwriteFiles: true, ct);
        }
        File.Delete(archive);
        foreach (var old in Directory.EnumerateDirectories(Folder).Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
            Directory.Delete(old, true);
        var exe = Executable ?? throw new AiException("Das OpenCode-Paket enthält kein ausführbares Programm.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
