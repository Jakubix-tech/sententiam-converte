using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Services;

/// <summary>Eine vom Benutzer gewählte Datei; der Inhalt wird erst beim Öffnen gelesen (wichtig auf Android).</summary>
public sealed record PickedFile(string Name, Func<Task<Stream>> OpenRead);

/// <summary>Mikrofonaufnahme fürs Diktat (WAV, 16 kHz mono) – vom Desktop-Programm bereitgestellt.</summary>
public interface IAudioRecorder
{
    bool IsAvailable { get; }

    /// <summary>Was fehlt, wenn keine Aufnahme möglich ist (z. B. ein Programm unter Linux).</summary>
    string Hint { get; }

    bool IsRecording { get; }
    void Start();
    Task<byte[]> StopAsync();
}

/// <summary>Dateiauswahl und Speichern – wird von der Oberfläche bereitgestellt (Desktop und Android).</summary>
public interface IPlatformServices
{
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> patterns, bool multiple);
    Task<Stream?> SaveFileAsync(string title, string suggestedName);
    Task<string?> PickFolderAsync(string title);

    /// <summary>Öffnet eine Datei mit dem passenden Programm des Systems (Vorschau).</summary>
    Task<bool> OpenFileAsync(string path);

    /// <summary>Öffnet eine Internetadresse im Browser.</summary>
    Task<bool> OpenUrlAsync(string url);
}

/// <summary>Lokale REST-API – nur in der Desktop-App (auf Android gibt es sie nicht).</summary>
public interface ILocalApiHost
{
    /// <summary>Adresse, solange die API läuft.</summary>
    string? Url { get; }
    Task StartAsync(int port, string token);
    Task StopAsync();
}

/// <summary>Einstellungen der Oberfläche (zuletzt geöffnete Projekte, Projektordner).</summary>
public sealed class UiSettings
{
    public string ProjectsRoot { get; set; } = Path.Combine(AppPaths.DataRoot, "Projekte");

    public List<string> RecentProjects { get; set; } = new();

    /// <summary>Editor: Quelle und Ziel untereinander (interlinear) statt nebeneinander.</summary>
    public bool EditorInterlinear { get; set; }

    /// <summary>Beim Öffnen eines Projekts die empfohlenen gemeinsamen Datenbanken ohne Rückfrage anlegen.</summary>
    public bool AutoCreateDatabases { get; set; }

    /// <summary>Editor: Änderungen an bestätigten Segmenten verfolgen (Lektorat).</summary>
    public bool EditorTrackChanges { get; set; }

    /// <summary>Zuletzt verwendete Preise (Vorlage für neue Projekte).</summary>
    public Core.Analysis.RateSettings? LastRates { get; set; }

    /// <summary>„Leere per KI“: Fuzzy-Treffer (70–99 %) gezielt anpassen statt frei übersetzen.</summary>
    public bool RepairFuzzyMatches { get; set; } = true;

    /// <summary>Aus Korrekturen lernen (Regeln vorschlagen und anwenden, Gewohnheiten je Kunde und Textsorte).</summary>
    public bool LearnFromCorrections { get; set; } = true;

    /// <summary>Vor der KI-Übersetzung das Dokument lesen lassen (Steckbrief: Thema, Anrede, Sprecher).</summary>
    public bool ReadDocumentFirst { get; set; } = true;

    /// <summary>„Erste Schritte“ im Reiter Projekt ausgeblendet (über die Hilfe wieder einblendbar).</summary>
    public bool HideGettingStarted { get; set; }

    /// <summary>Ordner für heruntergeladene freie Sprachdaten; leer = Standardordner.</summary>
    public string LanguageDataFolder { get; set; } = "";

    /// <summary>Sprache der Oberfläche: „de“, „en“ oder leer (= Systemsprache). Wirkt nach Neustart.</summary>
    public string UiLanguage { get; set; } = "";

    /// <summary>Lokale API: nur auf diesem Rechner (127.0.0.1) erreichbar, mit Zugangsschlüssel.</summary>
    public bool LocalApiEnabled { get; set; }
    public int LocalApiPort { get; set; } = 5290;
    public string LocalApiToken { get; set; } = "";

    public static string NewToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
        .Replace('+', '-').Replace('/', '_');

    private static string FilePath => Path.Combine(AppPaths.DataRoot, "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch (JsonException) { }
        return new UiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Remember(string projectFolder)
    {
        RecentProjects.RemoveAll(p => SamePath(p, projectFolder));
        RecentProjects.Insert(0, projectFolder);
        if (RecentProjects.Count > 15) RecentProjects.RemoveRange(15, RecentProjects.Count - 15);
        Save();
    }

    /// <summary>Aus der Liste „Zuletzt geöffnet“ nehmen – das Projekt selbst bleibt unberührt.</summary>
    public void Forget(string projectFolder)
    {
        RecentProjects.RemoveAll(p => SamePath(p, projectFolder));
        LastPlaces.Remove(LastPlaces.Keys.FirstOrDefault(k => SamePath(k, projectFolder)) ?? "");
        Save();
    }

    /// <summary>
    /// Wo man in einem Projekt zuletzt stand (Datei, Zielsprache, Segment) – beim Zurückschalten geht es dort weiter.
    /// Schlüssel ist der Projektordner. (Neu; ältere Einstellungsdateien haben das Feld nicht und bleiben lesbar.)
    /// </summary>
    public Dictionary<string, ProjectPlace> LastPlaces { get; set; } = new();

    public ProjectPlace? PlaceOf(string projectFolder) =>
        LastPlaces.FirstOrDefault(kv => SamePath(kv.Key, projectFolder)).Value;

    public void SetPlace(string projectFolder, ProjectPlace place)
    {
        LastPlaces.Remove(LastPlaces.Keys.FirstOrDefault(k => SamePath(k, projectFolder)) ?? "");
        LastPlaces[projectFolder] = place;
        // nur für Projekte der Liste merken, sonst wächst die Datei unbegrenzt
        foreach (var gone in LastPlaces.Keys.Where(k => !RecentProjects.Any(p => SamePath(p, k)) && !SamePath(k, projectFolder)).ToList())
            LastPlaces.Remove(gone);
        Save();
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Stelle in einem Projekt: Datei (Kennung), Zielsprache und gewähltes Segment.</summary>
public sealed record ProjectPlace(string FileId, string Language, int? SegmentId);

public static class AppServices
{
    // das Recherche-Gedächtnis übernimmt und zeigt nur, was in der eingestellten Erklärungssprache geschrieben ist
    static AppServices() => Core.Agents.ResearchMemory.CurrentExplanationLanguage = () => Ai.ExplanationLanguage;

    public static AiSettings Ai { get; set; } = AiSettings.Load();
    public static UiSettings Ui { get; } = UiSettings.Load();
    public static IPlatformServices? Platform { get; set; }
    public static Core.Qa.SpellChecker Spell { get; } = new();

    /// <summary>Download der eingebauten KI bzw. von OpenCode (einer für das ganze Programm).</summary>
    public static AiDownloadService AiDownload { get; } = new();
    public static Core.Remote.ServerSettings Servers { get; set; } = Core.Remote.ServerSettings.Load();
    public static ILocalApiHost? LocalApi { get; set; }

    /// <summary>Mikrofonaufnahme fürs Diktat; null = auf dieser Plattform nicht vorhanden.</summary>
    public static IAudioRecorder? Recorder { get; set; }

    /// <summary>Das Hauptfenster – die lokale API arbeitet mit dessen geöffnetem Projekt.</summary>
    public static ViewModels.MainViewModel? Main { get; set; }

    private static Core.Extensions.ExtensionManager? _extensions;
    public static Core.Extensions.ExtensionManager Extensions => _extensions ??= new Core.Extensions.ExtensionManager();

    /// <summary>Beim Programmstart: Erweiterungen laden und ggf. die lokale API starten.</summary>
    public static async Task StartupAsync()
    {
        try
        {
            Extensions.CleanUp();
            Extensions.LoadAll();
        }
        catch (IOException) { }
        if (LocalApi != null && Ui.LocalApiEnabled && Ui.LocalApiToken.Length > 0)
        {
            try { await LocalApi.StartAsync(Ui.LocalApiPort, Ui.LocalApiToken); }
            catch (Exception) { /* Port belegt – in den Einstellungen sichtbar, wenn man neu startet */ }
        }
    }
}
