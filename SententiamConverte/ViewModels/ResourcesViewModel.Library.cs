using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine eingelesene Datei der eigenen Fachtexte (Zeile in der Liste).</summary>
public sealed class LibraryFileRow(LibraryFile file)
{
    public LibraryFile File { get; } = file;
    public string Name => IsSource ? File.Title : Path.GetFileName(File.Path);
    public string Folder => IsSource ? File.Path : Path.GetDirectoryName(File.Path) ?? "";

    /// <summary>Eingearbeitete Quelle (Gesetz …) statt Datei aus einem Ordner.</summary>
    public bool IsSource => File.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    public bool HasProblem => File.Problem != null;

    public string Info => File.Problem != null
        ? L.T("nicht lesbar: ") + L.T(File.Problem)
        : string.Join(" · ", new[]
        {
            File.Language is { } l ? Languages.DisplayName(l) : L.T("Sprache unbekannt"),
            File.Pages > 0 ? L.T($"{File.Pages} Seiten") : L.T($"{File.Passages} Abschnitte"),
            File.Version is { } v ? L.T(v) : null,
        }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>
/// Eigene Fachtexte: Ordner mit PDFs und Dokumenten, die das Programm lokal durchsuchbar macht – die Recherche findet
/// darin Belegstellen (Datei, Seite, Stand) und zählt, wie oft eine Benennung vorkommt.
/// </summary>
public partial class ResourcesViewModel
{
    private ReferenceLibrarySettings _library = ReferenceLibrarySettings.Load();
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<string> LibraryFolders { get; } = new();
    public ObservableCollection<LibraryFileRow> LibraryFiles { get; } = new();
    public ObservableCollection<LibraryHit> LibraryHits { get; } = new();

    [ObservableProperty] public partial string LibraryStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsScanningLibrary { get; set; }
    [ObservableProperty] public partial string LibraryQuery { get; set; } = "";

    public string LibraryIndexFolder => Path.GetDirectoryName(_library.IndexPath) ?? "";

    public string LibrarySummary
    {
        get
        {
            var files = LibraryFiles.Count(f => !f.HasProblem);
            var free = Core.Ai.BuiltInAi.FreeSpace(LibraryIndexFolder) is { } f ? Core.Ai.BuiltInAi.FormatSize(f) : "?";
            var size = File.Exists(_library.IndexPath) ? Core.Ai.BuiltInAi.FormatSize(new FileInfo(_library.IndexPath).Length) : "0 MB";
            return L.T($"{files} Dateien eingelesen, Suchindex {size} (frei auf dem Laufwerk: {free}).");
        }
    }

    public bool LibraryAllowOnline
    {
        get => _library.AllowOnlineAi;
        set
        {
            if (_library.AllowOnlineAi == value) return;
            _library.AllowOnlineAi = value;
            _library.Save();
            OnPropertyChanged();
        }
    }

    public bool HasLibraryFolders => LibraryFolders.Count > 0;

    /// <summary>Ordner und eingelesene Dateien anzeigen (beim Öffnen des Reiters).</summary>
    public void RefreshLibrary()
    {
        _library = ReferenceLibrarySettings.Load();
        LibraryFolders.Clear();
        foreach (var f in _library.Folders) LibraryFolders.Add(f);
        LibraryFiles.Clear();
        if (File.Exists(_library.IndexPath))
            try
            {
                foreach (var f in ReferenceLibrary.Shared(_library.IndexPath).Files()) LibraryFiles.Add(new LibraryFileRow(f));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { LibraryStatus = "Suchindex nicht lesbar: " + ex.Message; }
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(LibraryIndexFolder));
        OnPropertyChanged(nameof(HasLibraryFolders));
        OnPropertyChanged(nameof(LibraryAllowOnline));
    }

    [RelayCommand]
    private async Task AddLibraryFolder()
    {
        if (AppServices.Platform == null) return;
        var folder = await AppServices.Platform.PickFolderAsync(L.T("Ordner mit eigenen Fachtexten (PDF, Word, Writer, Text …)"));
        if (folder == null) return;
        if (!_library.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _library.Folders.Add(folder);
        _library.Save();
        RefreshLibrary();
        await ScanLibrary();
    }

    [RelayCommand]
    private async Task RemoveLibraryFolder(string? folder)
    {
        if (folder == null) return;
        _library.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        _library.Save();
        RefreshLibrary();
        await ScanLibrary();                                             // nimmt die Dateien des Ordners aus dem Index
    }

    [RelayCommand]
    private async Task ChooseLibraryIndexFolder()
    {
        if (AppServices.Platform == null) return;
        var folder = await AppServices.Platform.PickFolderAsync(L.T("Ordner für den Suchindex der eigenen Fachtexte (ein Laufwerk mit Platz)"));
        if (folder == null) return;
        var old = _library.IndexPath;
        _library.IndexFolder = folder;
        try
        {
            ReferenceLibrary.MoveIndex(old, _library.IndexPath);
            _library.Save();
            LibraryStatus = L.T($"Suchindex liegt jetzt in {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _library = ReferenceLibrarySettings.Load();
            LibraryStatus = "Suchindex nicht verschoben: " + ex.Message;
        }
        RefreshLibrary();
    }

    /// <summary>Ordner einlesen: neue und geänderte Dateien lesen, verschwundene entfernen.</summary>
    [RelayCommand]
    private async Task ScanLibrary()
    {
        if (IsScanningLibrary) return;
        IsScanningLibrary = true;
        var cts = _scanCts = new CancellationTokenSource();
        try
        {
            var library = ReferenceLibrary.Shared(_library.IndexPath);
            var scan = await library.ScanAsync(_library.Folders, new Progress<string>(m => LibraryStatus = m), cts.Token);
            LibraryStatus = L.T($"Eingelesen: {scan.Added} neu, {scan.Updated} geändert, {scan.Removed} entfernt, {scan.Unchanged} unverändert.")
                            + (scan.Problems.Count > 0 ? " " + L.T($"{scan.Problems.Count} Dateien ohne lesbaren Text (z. B. gescannt – dann vorher Texterkennung).") : "");
        }
        catch (OperationCanceledException) { LibraryStatus = L.T("Einlesen abgebrochen – das bisher Gelesene bleibt im Index."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LibraryStatus = "Einlesen fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsScanningLibrary = false;
            RefreshLibrary();
        }
    }

    [RelayCommand]
    private void CancelLibraryScan() => _scanCts?.Cancel();

    /// <summary>Probesuche: Stellen mit der Wortfolge in allen eingelesenen Texten.</summary>
    [RelayCommand]
    private void SearchLibrary()
    {
        LibraryHits.Clear();
        if (LibraryQuery.Trim().Length == 0 || !File.Exists(_library.IndexPath)) return;
        var library = ReferenceLibrary.Shared(_library.IndexPath);
        foreach (var hit in library.FindPhrase(LibraryQuery.Trim(), null, 20)) LibraryHits.Add(hit);
        LibraryStatus = LibraryHits.Count == 0 ? L.T("Nichts gefunden.")
            : L.T($"„{LibraryQuery.Trim()}“ kommt {library.CountPhrase(LibraryQuery.Trim())}-mal vor.");
    }

    [RelayCommand]
    private async Task OpenLibraryHit(LibraryHit? hit)
    {
        if (hit != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(hit.Uri);
    }
}
