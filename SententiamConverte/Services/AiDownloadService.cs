using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Services;

/// <summary>
/// Downloads der eingebauten KI, von OpenCode und von freien Sprachdaten – einer für das ganze Programm, damit alle
/// Ansichten denselben Fortschritt zeigen und nichts doppelt geladen wird.
/// </summary>
public partial class AiDownloadService : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>Nach jedem erfolgreichen Download (Editor und Einstellungen aktualisieren ihre Anzeige).</summary>
    public event Action? Completed;

    public Task<bool> InstallAsync(BuiltInModel model) => Run(model.Name, (p, ct) => BuiltInAi.InstallAsync(model, p, ct));

    public Task<bool> InstallOpenCodeAsync() => Run("OpenCode", OpenCodeInstaller.InstallAsync);

    [RelayCommand]
    public void Cancel() => _cts?.Cancel();

    /// <summary>Beliebigen Download mit Fortschritt ausführen (auch freie Sprachdaten); false bei Abbruch oder Fehler.</summary>
    public async Task<bool> Run(string what, Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        if (IsRunning) return false;
        IsRunning = true;
        Fraction = 0;
        Text = $"Lade {what} …";
        _cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        string? current = null;
        long startBytes = 0;
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.What != current)
            {
                current = p.What;
                startBytes = p.Done;
                watch.Restart();
            }
            Fraction = p.Fraction;
            var speed = watch.Elapsed.TotalSeconds > 2 ? (p.Done - startBytes) / watch.Elapsed.TotalSeconds : 0;
            var rest = speed > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / speed) : (TimeSpan?)null;
            Text = $"{p.What}: {BuiltInAi.FormatSize(p.Done)} von {BuiltInAi.FormatSize(p.Total)} ({p.Fraction * 100:0} %)" +
                   (rest is { } r && p.Done < p.Total ? $" – noch etwa {(r.TotalMinutes >= 1 ? $"{Math.Ceiling(r.TotalMinutes):0} min" : $"{Math.Max(1, r.TotalSeconds):0} s")}" : "");
        });
        try
        {
            await Task.Run(() => work(progress, _cts.Token));
            Text = $"{what} ist heruntergeladen.";
            Completed?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            Text = "Download abgebrochen – beim nächsten Versuch geht es an derselben Stelle weiter.";
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Text = "Fehler: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
        return false;
    }
}
