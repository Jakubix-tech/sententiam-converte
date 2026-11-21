using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SententiamConverte.Core.Ai;

public enum DockerState { Unsupported, Missing, NotRunning, Ready }

public enum LocalSearchState { NotSetUp, Stopped, Running }

/// <summary>Ob Docker da ist und läuft; <see cref="Detail"/> ist eine Meldung für die Oberfläche.</summary>
public sealed record DockerStatus(DockerState State, string Detail, string? Version = null);

/// <summary>
/// Kostenlose Websuche auf diesem Rechner: SearXNG (AGPL-3.0) in einem Docker-Container, nur über 127.0.0.1
/// erreichbar. Das Programm prüft Docker, installiert es unter Windows auf Wunsch aus der offiziellen Quelle (für den
/// Benutzer, Programm und Daten auf einem Laufwerk nach Wahl), startet es, richtet den Container ein und startet ihn bei
/// Bedarf wieder. Docker selbst darf nicht mit dem Programm weitergegeben werden; geladen wird es erst auf Knopfdruck.
/// SearXNG fragt andere Suchmaschinen ab – deren Nutzungsbedingungen gelten, und sie können Anfragen ablehnen.
/// </summary>
public static partial class LocalSearXng
{
    public const string Container = "sententiam-searxng";
    public const string Image = "docker.io/searxng/searxng:latest";
    public const int Port = 8888;
    public const string DockerInstallerUrl = "https://desktop.docker.com/win/main/amd64/Docker%20Desktop%20Installer.exe";
    public const string DockerInfoUrl = "https://docs.docker.com/get-started/get-docker/";

    public static string BaseUrl => $"http://127.0.0.1:{Port}";

    public static string ConfigFolder => Path.Combine(AppPaths.DataRoot, "searxng");

    /// <summary>Ob die Websuche die Instanz dieses Programms meint (dann startet sie sie bei Bedarf selbst).</summary>
    public static bool IsLocal(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && u.IsLoopback && u.Port == Port;

    /// <summary>Für Tests: Befehle (Programm, Argumente) nicht ausführen, sondern hierüber beantworten.</summary>
    internal static Func<string, IReadOnlyList<string>, CancellationToken, Task<(int Exit, string Output)>>? RunOverride { get; set; }

    /// <summary>Für Tests: Antwortet die Suche schon?</summary>
    internal static Func<CancellationToken, Task<bool>>? ProbeOverride { get; set; }

    /// <summary>Für Tests: Docker-Installation stattdessen hierüber (Installationsprogramm, Argumente).</summary>
    internal static Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? InstallOverride { get; set; }

    /// <summary>Wie lange auf Docker bzw. die Suche gewartet wird (Tests kürzen das).</summary>
    internal static TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(3);

    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Platz, den Docker Desktop mit Daten braucht (Tests setzen 0).</summary>
    internal static long RequiredSpace { get; set; } = 6_000_000_000;

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Unter Windows lässt sich Docker aus der offiziellen Quelle nachinstallieren; sonst gibt es eine Anleitung.</summary>
    public static bool CanInstallDocker => OperatingSystem.IsWindows() || InstallOverride != null;

    private static string? _docker;

    // ---------- Docker ----------

    /// <summary>Wo das Docker-Programm stehen kann: im Suchpfad, sonst an den üblichen Orten von Docker Desktop.</summary>
    internal static IEnumerable<string> DockerCandidates()
    {
        if (_docker != null) yield return _docker;
        yield return "docker";
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "resources", "bin", "docker.exe");
            if (InstallFolder is { Length: > 0 } custom) yield return Path.Combine(custom, "resources", "bin", "docker.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/bin/docker";
            yield return "/Applications/Docker.app/Contents/Resources/bin/docker";
        }
        else yield return "/usr/bin/docker";
    }

    /// <summary>Wohin Docker Desktop installiert wurde (vom Programm, leer = üblicher Ort).</summary>
    public static string? InstallFolder { get; set; }

    public static async Task<DockerStatus> CheckDockerAsync(CancellationToken ct = default)
    {
        if (!IsSupported && RunOverride == null) return new(DockerState.Unsupported, "Docker gibt es für dieses System nicht.");
        foreach (var candidate in DockerCandidates().Distinct())
        {
            var (exit, output) = await RunAsync(candidate, ["version", "--format", "{{.Client.Version}}|{{.Server.Version}}"], ct);
            if (exit < 0) continue;                                              // nicht gefunden
            _docker = candidate;
            var parts = output.Trim().Split('|');
            if (exit == 0 && parts.Length == 2 && parts[1].Trim().Length > 0)
                return new(DockerState.Ready, $"Docker {parts[1].Trim()} läuft.", parts[1].Trim());
            return new(DockerState.NotRunning, "Docker ist installiert, läuft aber nicht.");
        }
        _docker = null;
        return new(DockerState.Missing, "Docker ist nicht installiert.");
    }

    /// <summary>Docker Desktop starten (Windows, macOS) und warten, bis es antwortet.</summary>
    public static async Task<DockerStatus> StartDockerAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var status = await CheckDockerAsync(ct);
        if (status.State != DockerState.NotRunning) return status;
        log?.Report("Starte Docker …");
        if (RunOverride == null)
        {
            if (OperatingSystem.IsWindows() && DesktopExecutable() is { } exe)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", ["-a", "Docker"]) { UseShellExecute = false });
            else
                return new(DockerState.NotRunning, "Docker läuft nicht – bitte den Docker-Dienst starten (z. B. „sudo systemctl start docker“).");
        }
        else await RunAsync("docker-desktop", ["start"], ct);
        var until = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(PollInterval, ct);
            status = await CheckDockerAsync(ct);
            if (status.State == DockerState.Ready) return status;
        }
        return new(DockerState.NotRunning, "Docker ist nach dem Start nicht bereit geworden – Docker Desktop öffnen und den Hinweisen folgen.");
    }

    private static string? DesktopExecutable() => new[]
    {
        InstallFolder is { Length: > 0 } f ? Path.Combine(f, "Docker Desktop.exe") : null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "Docker Desktop.exe"),
    }.FirstOrDefault(p => p != null && File.Exists(p));

    /// <summary>
    /// Docker Desktop aus der offiziellen Quelle laden und für den angemeldeten Benutzer installieren (ohne
    /// Administratorrechte, sofern WSL 2 schon eingerichtet ist). Programm und Daten kommen unter <paramref name="folder"/>,
    /// damit das Systemlaufwerk nicht vollläuft. Wer das aufruft, stimmt der Docker-Lizenz zu (in der Oberfläche
    /// genannt: kostenlos für Privatleute, Ausbildung und kleine Firmen).
    /// </summary>
    public static async Task InstallDockerAsync(string folder, IProgress<DownloadProgress>? progress = null, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        if (!CanInstallDocker) throw new AiException("Docker lässt sich hier nicht automatisch installieren – siehe " + DockerInfoUrl);
        var program = Path.Combine(folder, "programm");
        var data = Path.Combine(folder, "daten");
        Directory.CreateDirectory(folder);
        if (BuiltInAi.FreeSpace(folder) is { } free && free < RequiredSpace)
            throw new AiException($"Zu wenig Speicherplatz für Docker: etwa 6 GB nötig, frei {BuiltInAi.FormatSize(free)} auf {Path.GetPathRoot(Path.GetFullPath(folder))}. Einen anderen Ordner wählen.");
        var installer = Path.Combine(folder, "Docker Desktop Installer.exe");
        if (!File.Exists(installer))
        {
            using var http = BuiltInAi.Http();
            await BuiltInAi.DownloadAsync(http, DockerInstallerUrl, installer, 0, null, "Docker Desktop", progress, ct);
        }
        string[] args = ["install", "--user", "--quiet", "--accept-license", $"--installation-dir={program}", $"--wsl-default-data-root={data}"];
        log?.Report("Installiere Docker Desktop (das kann einige Minuten dauern) …");
        var exit = InstallOverride != null ? await InstallOverride(installer, args, ct) : await RunInstallerAsync(installer, args, ct);
        if (exit != 0)
        {
            // Manche Fassungen erlauben für die Installation je Benutzer keinen eigenen Programmordner: dann am üblichen Ort
            log?.Report("Zweiter Versuch am üblichen Installationsort …");
            args = [.. args.Where(a => !a.StartsWith("--installation-dir="))];
            program = "";
            exit = InstallOverride != null ? await InstallOverride(installer, args, ct) : await RunInstallerAsync(installer, args, ct);
        }
        if (exit != 0)
            throw new AiException($"Die Docker-Installation ist mit Code {exit} beendet worden. Häufigster Grund: WSL 2 ist noch nicht eingerichtet " +
                                  "(einmalig mit Administratorrechten „wsl --install“ ausführen und neu starten).");
        InstallFolder = program.Length > 0 ? program : null;
        try { File.Delete(installer); }
        catch (IOException) { }
    }

    private static async Task<int> RunInstallerAsync(string installer, IReadOnlyList<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new AiException("Das Docker-Installationsprogramm ließ sich nicht starten.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    // ---------- SearXNG ----------

    public static async Task<LocalSearchState> StateAsync(CancellationToken ct = default)
    {
        var (exit, output) = await DockerAsync(["inspect", "-f", "{{.State.Running}}", Container], ct);
        if (exit != 0) return LocalSearchState.NotSetUp;
        return output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? LocalSearchState.Running : LocalSearchState.Stopped;
    }

    /// <summary>Container einrichten (Abbild laden, Einstellungen schreiben, starten) und warten, bis die Suche antwortet.</summary>
    public static async Task SetUpAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        WriteSettings();
        log?.Report("Lade SearXNG (einmalig, etwa 200 MB) …");
        var (pull, pullOutput) = await DockerAsync(["pull", Image], ct);
        if (pull != 0) throw new AiException("SearXNG ließ sich nicht laden: " + Last(pullOutput));
        await DockerAsync(["rm", "-f", Container], ct);                         // alte Fassung ersetzen
        log?.Report("Starte SearXNG …");
        var (run, runOutput) = await DockerAsync(
        [
            "run", "-d", "--name", Container, "--restart", "unless-stopped",
            "-p", $"127.0.0.1:{Port}:8080",                                           // nur von diesem Rechner erreichbar
            "-v", $"{ConfigFolder}:/etc/searxng",
            "-e", $"SEARXNG_BASE_URL={BaseUrl}/",
            Image,
        ], ct);
        if (run != 0) throw new AiException("SearXNG ließ sich nicht starten: " + Last(runOutput));
        if (!await WaitUntilAnswersAsync(ct)) throw new AiException("SearXNG läuft, antwortet aber nicht – „Websuche testen“ später noch einmal versuchen.");
        log?.Report("SearXNG antwortet.");
    }

    /// <summary>Vor einer Suche: läuft die eigene Instanz nicht, Docker und Container starten (falls eingerichtet).</summary>
    public static async Task<bool> EnsureRunningAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (await ProbeAsync(ct)) return true;
        var docker = await StartDockerAsync(log, ct);
        if (docker.State != DockerState.Ready) return false;
        var state = await StateAsync(ct);
        if (state == LocalSearchState.NotSetUp) return false;
        if (state == LocalSearchState.Stopped)
        {
            log?.Report("Starte SearXNG …");
            await DockerAsync(["start", Container], ct);
        }
        return await WaitUntilAnswersAsync(ct);
    }

    public static Task StopAsync(CancellationToken ct = default) => DockerAsync(["stop", Container], ct);

    /// <summary>Container und Abbild entfernen (die Einstellungen bleiben für ein neues Einrichten).</summary>
    public static async Task RemoveAsync(CancellationToken ct = default)
    {
        await DockerAsync(["rm", "-f", Container], ct);
        await DockerAsync(["rmi", Image], ct);
    }

    private static async Task<bool> WaitUntilAnswersAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < until)
        {
            if (await ProbeAsync(ct)) return true;
            await Task.Delay(PollInterval, ct);
        }
        return false;
    }

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Antwortet die Suche im JSON-Format?</summary>
    public static async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        if (ProbeOverride != null) return await ProbeOverride(ct);
        try
        {
            using var response = await ProbeHttp.GetAsync($"{BaseUrl}/search?format=json&q=test", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ---------- Einstellungen ----------

    [GeneratedRegex(@"secret_key:\s*""([0-9a-f]{32,})""")]
    private static partial Regex SecretRegex();

    /// <summary>settings.yml: JSON-Ausgabe für das Programm, kein Bot-Schutz (nur dieser Rechner), Geheimnis bleibt erhalten.</summary>
    internal static string SettingsYaml(string secret) => $$"""
        # Von Sententiam converte angelegt – Änderungen bleiben erhalten, solange der geheime Schlüssel bleibt.
        use_default_settings: true
        general:
          instance_name: "Sententiam converte"
        server:
          secret_key: "{{secret}}"
          limiter: false
          public_instance: false
          image_proxy: false
        search:
          safe_search: 0
          formats:
            - html
            - json
        """;

    internal static void WriteSettings()
    {
        Directory.CreateDirectory(ConfigFolder);
        var path = Path.Combine(ConfigFolder, "settings.yml");
        if (File.Exists(path) && SecretRegex().IsMatch(File.ReadAllText(path))) return;     // vorhandene (evtl. angepasste) Einstellungen behalten
        File.WriteAllText(path, SettingsYaml(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()));
    }

    // ---------- Befehle ----------

    private static Task<(int Exit, string Output)> DockerAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        RunAsync(_docker ?? "docker", args, ct);

    /// <summary>Befehl ausführen; Exit −1, wenn das Programm nicht gefunden wurde.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string program, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (RunOverride != null) return await RunOverride(program, args, ct);
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start);
            if (process == null) return (-1, "");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }      // Programm nicht gefunden
    }

    private static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "unbekannter Fehler";
}
