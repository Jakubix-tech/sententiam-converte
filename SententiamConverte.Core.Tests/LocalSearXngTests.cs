using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Kostenlose Websuche auf diesem Rechner: Docker finden, installieren, starten, SearXNG einrichten und bei Bedarf
/// wieder starten – gegen ein nachgebildetes Docker (es läuft kein echter Befehl).
/// </summary>
[Collection("BuiltInAi")]
public sealed class LocalSearXngTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<string> _commands = new();
    private bool _installed, _running, _container, _containerRunning, _answers;

    public LocalSearXngTests()
    {
        LocalSearXng.RunOverride = Run;
        LocalSearXng.ProbeOverride = _ => Task.FromResult(_answers);
        LocalSearXng.InstallOverride = (_, args, _) =>
        {
            _commands.Add("install " + string.Join(" ", args));
            _installed = true;
            return Task.FromResult(0);
        };
        LocalSearXng.StartTimeout = TimeSpan.FromSeconds(2);
        LocalSearXng.PollInterval = TimeSpan.FromMilliseconds(5);
        LocalSearXng.RequiredSpace = 0;
    }

    public void Dispose()
    {
        LocalSearXng.RunOverride = null;
        LocalSearXng.ProbeOverride = null;
        LocalSearXng.InstallOverride = null;
        LocalSearXng.StartTimeout = TimeSpan.FromMinutes(3);
        LocalSearXng.PollInterval = TimeSpan.FromSeconds(2);
        LocalSearXng.RequiredSpace = 6_000_000_000;
        LocalSearXng.InstallFolder = null;
        _dir.Dispose();
    }

    private Task<(int, string)> Run(string program, IReadOnlyList<string> args, CancellationToken ct)
    {
        var line = string.Join(" ", args);
        _commands.Add($"{Path.GetFileName(program)} {line}");
        if (program == "docker-desktop") { _running = true; return Task.FromResult((0, "")); }
        if (!_installed) return Task.FromResult((-1, ""));                                   // Programm nicht gefunden
        return Task.FromResult(args[0] switch
        {
            "version" => _running ? (0, "27.3.1|27.3.1") : (1, "27.3.1|"),
            "inspect" => _container ? (0, _containerRunning ? "true" : "false") : (1, "Error: No such object"),
            "pull" => (0, "Status: Downloaded newer image"),
            "rm" => (_container ? 0 : 1, ""),
            "run" => Started(),
            "start" => Started(),
            "stop" => Stopped(),
            "rmi" => (0, ""),
            _ => (1, "unbekannt"),
        });

        (int, string) Started()
        {
            _container = _containerRunning = _answers = true;
            return (0, "abc123");
        }

        (int, string) Stopped()
        {
            _containerRunning = _answers = false;
            return (0, "");
        }
    }

    [Fact]
    public async Task InstallsDockerStartsItAndSetsUpSearXng()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(DockerState.Missing, (await LocalSearXng.CheckDockerAsync(ct)).State);

        // Installation: das Installationsprogramm liegt schon da (kein Download im Test), Programm und Daten unter dem Ordner
        var folder = _dir.File("docker");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Docker Desktop Installer.exe"), "x");
        await LocalSearXng.InstallDockerAsync(folder, ct: ct);
        var install = Assert.Single(_commands, c => c.StartsWith("install "));
        Assert.Contains("--user --quiet --accept-license", install);
        Assert.Contains($"--installation-dir={Path.Combine(folder, "programm")}", install);
        Assert.Contains($"--wsl-default-data-root={Path.Combine(folder, "daten")}", install);
        Assert.Equal(Path.Combine(folder, "programm"), LocalSearXng.InstallFolder);
        Assert.False(File.Exists(Path.Combine(folder, "Docker Desktop Installer.exe")));      // aufgeräumt

        Assert.Equal(DockerState.NotRunning, (await LocalSearXng.CheckDockerAsync(ct)).State);
        var started = await LocalSearXng.StartDockerAsync(ct: ct);
        Assert.Equal((DockerState.Ready, "27.3.1"), (started.State, started.Version));

        Assert.Equal(LocalSearchState.NotSetUp, await LocalSearXng.StateAsync(ct));
        await LocalSearXng.SetUpAsync(ct: ct);
        var run = Assert.Single(_commands, c => c.StartsWith("docker run "));
        Assert.Contains("--name sententiam-searxng --restart unless-stopped", run);
        Assert.Contains("-p 127.0.0.1:8888:8080", run);                                       // nur von diesem Rechner
        Assert.Contains($"-v {LocalSearXng.ConfigFolder}:/etc/searxng", run);
        Assert.EndsWith(LocalSearXng.Image, run);
        Assert.Equal(LocalSearchState.Running, await LocalSearXng.StateAsync(ct));

        // Einstellungen: JSON für das Programm, kein Bot-Schutz; der geheime Schlüssel bleibt beim neuen Einrichten
        var settings = File.ReadAllText(Path.Combine(LocalSearXng.ConfigFolder, "settings.yml"));
        Assert.Contains("    - json", settings);
        Assert.Contains("limiter: false", settings);
        LocalSearXng.WriteSettings();
        Assert.Equal(settings, File.ReadAllText(Path.Combine(LocalSearXng.ConfigFolder, "settings.yml")));
    }

    [Fact]
    public async Task StartsAStoppedInstanceBeforeSearching()
    {
        var ct = TestContext.Current.CancellationToken;
        (_installed, _running, _container, _containerRunning, _answers) = (true, false, true, false, false);
        Assert.True(await LocalSearXng.EnsureRunningAsync(ct: ct));
        Assert.Contains("docker-desktop start", _commands);
        Assert.Contains("docker start sententiam-searxng", _commands);

        // nicht eingerichtet: kein Start, die Suche meldet den Fehler weiter
        (_container, _answers) = (false, false);
        Assert.False(await LocalSearXng.EnsureRunningAsync(ct: ct));
    }

    [Fact]
    public async Task ReportsWhatIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("Docker ist nicht installiert.", (await LocalSearXng.CheckDockerAsync(ct)).Detail);
        _installed = true;
        Assert.Equal("Docker ist installiert, läuft aber nicht.", (await LocalSearXng.CheckDockerAsync(ct)).Detail);
        _running = true;
        Assert.Equal("Docker 27.3.1 läuft.", (await LocalSearXng.CheckDockerAsync(ct)).Detail);

        Assert.True(LocalSearXng.IsLocal("http://127.0.0.1:8888"));
        Assert.True(LocalSearXng.IsLocal("http://localhost:8888/"));
        Assert.False(LocalSearXng.IsLocal("http://192.168.1.5:8888"));
        Assert.False(LocalSearXng.IsLocal("http://127.0.0.1:9999"));
        Assert.False(LocalSearXng.IsLocal(""));
    }
}
