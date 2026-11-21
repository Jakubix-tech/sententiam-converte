using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Kostenlose Websuche auf diesem Rechner: ein Knopf prüft Docker, installiert es bei Bedarf (Windows, aus der
/// offiziellen Quelle), richtet SearXNG ein und stellt die Websuche darauf um.
/// </summary>
public partial class SettingsViewModel
{
    private CancellationTokenSource? _localSearchCts;
    private string? _dockerFolder;

    [ObservableProperty] public partial string LocalSearchStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsLocalSearchBusy { get; set; }
    [ObservableProperty] public partial double LocalSearchProgress { get; set; }
    [ObservableProperty] public partial bool HasLocalSearchProgress { get; set; }
    [ObservableProperty] public partial bool DockerMissing { get; set; }
    [ObservableProperty] public partial bool LocalSearchSetUp { get; set; }
    [ObservableProperty] public partial bool LocalSearchRunning { get; set; }

    /// <summary>Docker ist installiert, startet aber nicht – dann hilft meist eine neue Installation.</summary>
    [ObservableProperty] public partial bool DockerNeedsRepair { get; set; }

    private bool _forceDockerInstall;

    public bool LocalSearchSupported => LocalSearXng.IsSupported;
    public bool CanInstallDocker => LocalSearXng.CanInstallDocker;

    /// <summary>
    /// Wohin Docker installiert wird (Programm und Daten, etwa 6 GB): neben den Ordner, den der Nutzer für die eingebaute
    /// KI gewählt hat, sonst auf das Laufwerk mit dem meisten freien Platz – das Systemlaufwerk nur, wenn kein anderes
    /// mindestens 10 GB frei hat.
    /// </summary>
    public string DockerFolder
    {
        get => _dockerFolder ??= DefaultDockerFolder(AppServices.Ai.BuiltInFolder);
        set
        {
            _dockerFolder = value;
            OnPropertyChanged();
        }
    }

    internal static string DefaultDockerFolder(string? builtInFolder)
    {
        if (builtInFolder is { Length: > 0 } ai)
            return Path.Combine(Path.GetDirectoryName(ai.TrimEnd('\\', '/')) is { Length: > 0 } parent ? parent : ai, "docker");
        if (OperatingSystem.IsWindows())
        {
            var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            try
            {
                var other = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase))
                    .Where(d => d.AvailableFreeSpace > 10_000_000_000)
                    .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
                if (other != null) return Path.Combine(other.Name, "SententiamConverte", "docker");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(Core.Ai.AppPaths.DataRoot, "docker");
    }

    public string SetUpLocalSearchText => DockerMissing && CanInstallDocker
        ? L.T("Docker installieren und Websuche einrichten")
        : LocalSearchSetUp ? L.T("Neu einrichten") : L.T("Websuche einrichten");

    partial void OnDockerMissingChanged(bool value) => OnPropertyChanged(nameof(SetUpLocalSearchText));
    partial void OnLocalSearchSetUpChanged(bool value) => OnPropertyChanged(nameof(SetUpLocalSearchText));

    /// <summary>Zustand von Docker und SearXNG anzeigen (beim Öffnen der Seite und nach jeder Aktion).</summary>
    [RelayCommand]
    public async Task RefreshLocalSearch()
    {
        if (!LocalSearchSupported) return;
        try
        {
            var docker = await LocalSearXng.CheckDockerAsync();
            DockerMissing = docker.State == DockerState.Missing;
            if (docker.State != DockerState.Ready)
            {
                LocalSearchSetUp = LocalSearchRunning = false;
                LocalSearchStatus = docker.Detail;
                return;
            }
            var state = await LocalSearXng.StateAsync();
            LocalSearchSetUp = state != LocalSearchState.NotSetUp;
            LocalSearchRunning = state == LocalSearchState.Running;
            LocalSearchStatus = state switch
            {
                LocalSearchState.Running => L.T($"SearXNG läuft auf {LocalSearXng.BaseUrl} ({docker.Detail})"),
                LocalSearchState.Stopped => L.T("SearXNG ist eingerichtet, aber angehalten – startet bei der nächsten Suche von selbst."),
                _ => L.T($"{docker.Detail} SearXNG ist noch nicht eingerichtet."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { LocalSearchStatus = "Fehler: " + ex.Message; }
    }

    /// <summary>
    /// Alles in einem: Docker prüfen, bei Bedarf installieren und starten, SearXNG einrichten, die Websuche darauf
    /// umstellen und testen. Mit dem Klick auf die Installation stimmt der Nutzer der Docker-Lizenz zu (der Text steht daneben).
    /// </summary>
    [RelayCommand]
    private async Task SetUpLocalSearch()
    {
        if (IsLocalSearchBusy) return;
        IsLocalSearchBusy = true;
        var cts = _localSearchCts = new CancellationTokenSource();
        var log = new Progress<string>(m => LocalSearchStatus = m);
        try
        {
            var docker = await LocalSearXng.CheckDockerAsync(cts.Token);
            if (docker.State == DockerState.Missing || _forceDockerInstall)
            {
                if (!CanInstallDocker)
                {
                    LocalSearchStatus = L.T("Docker ist nicht installiert. Bitte Docker installieren (die Anleitung öffnet sich) und danach noch einmal klicken.");
                    if (AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(LocalSearXng.DockerInfoUrl);
                    return;
                }
                HasLocalSearchProgress = true;
                await LocalSearXng.InstallDockerAsync(DockerFolder,
                    new Progress<DownloadProgress>(p =>
                    {
                        LocalSearchProgress = p.Fraction;
                        LocalSearchStatus = L.T($"Lade {p.What}: {BuiltInAi.FormatSize(p.Done)}") + (p.Total > 0 ? " / " + BuiltInAi.FormatSize(p.Total) : "");
                    }), log, cts.Token);
                HasLocalSearchProgress = false;
                Save();                                                  // Installationsort merken
            }
            docker = await LocalSearXng.StartDockerAsync(log, cts.Token);
            DockerNeedsRepair = docker.State == DockerState.NotRunning && CanInstallDocker;
            if (docker.State != DockerState.Ready)
            {
                LocalSearchStatus = docker.Detail + (DockerNeedsRepair ? " " + L.T("Hilft das nicht: „Docker neu installieren“.") : "");
                return;
            }
            await LocalSearXng.SetUpAsync(log, cts.Token);
            EnableSearchSource(SearchProviderKind.SearXng, LocalSearXng.BaseUrl);    // zusätzlich zu den anderen Diensten
            Save();
            await TestSearch();
            LocalSearchStatus = L.T("Fertig: Die Websuche läuft jetzt über SearXNG auf diesem Rechner.");
        }
        catch (OperationCanceledException) { LocalSearchStatus = L.T("Abgebrochen."); }
        catch (Exception ex) when (ex is AiException or IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            LocalSearchStatus = "Fehler: " + ex.Message;
        }
        finally
        {
            _forceDockerInstall = false;
            HasLocalSearchProgress = false;
            IsLocalSearchBusy = false;
            await RefreshLocalSearchStateOnly();
        }
    }

    /// <summary>Docker noch einmal aus der offiziellen Quelle installieren (über die vorhandene Installation) und weiter einrichten.</summary>
    [RelayCommand]
    private async Task ReinstallDocker()
    {
        _forceDockerInstall = true;
        DockerNeedsRepair = false;
        await SetUpLocalSearch();
    }

    private async Task RefreshLocalSearchStateOnly()
    {
        try
        {
            var state = await LocalSearXng.StateAsync();
            LocalSearchSetUp = state != LocalSearchState.NotSetUp;
            LocalSearchRunning = state == LocalSearchState.Running;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    [RelayCommand]
    private void CancelLocalSearch() => _localSearchCts?.Cancel();

    [RelayCommand]
    private async Task StopLocalSearch()
    {
        await LocalSearXng.StopAsync();
        await RefreshLocalSearch();
    }

    [RelayCommand]
    private async Task RemoveLocalSearch()
    {
        await LocalSearXng.RemoveAsync();
        if (SearchSources.FirstOrDefault(r => r.Kind == SearchProviderKind.SearXng) is { } row && LocalSearXng.IsLocal(row.BaseUrl))
        {
            DisableSearchSource(SearchProviderKind.SearXng);                     // die anderen Dienste suchen weiter
            Save();
        }
        await RefreshLocalSearch();
        LocalSearchStatus = L.T("SearXNG entfernt; die übrigen angekreuzten Suchdienste suchen weiter. Docker selbst bleibt installiert.");
    }

    [RelayCommand]
    private async Task ChooseDockerFolder()
    {
        if (AppServices.Platform == null) return;
        var folder = await AppServices.Platform.PickFolderAsync(L.T("Ordner für Docker (Programm und Daten, etwa 6 GB)"));
        if (folder != null) DockerFolder = folder;
    }

    [RelayCommand]
    private async Task OpenDockerInfo()
    {
        if (AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(LocalSearXng.DockerInfoUrl);
    }
}
