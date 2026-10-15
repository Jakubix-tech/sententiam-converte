using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Services;

/// <summary>Eine vom Benutzer gewählte Datei; der Inhalt wird erst beim Öffnen gelesen (wichtig auf Android).</summary>
public sealed record PickedFile(string Name, Func<Task<Stream>> OpenRead);

/// <summary>Dateiauswahl und Speichern – wird von der Oberfläche bereitgestellt (Desktop und Android).</summary>
public interface IPlatformServices
{
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> patterns, bool multiple);
    Task<Stream?> SaveFileAsync(string title, string suggestedName);
    Task<string?> PickFolderAsync(string title);

    /// <summary>Öffnet eine Datei mit dem passenden Programm des Systems (Vorschau).</summary>
    Task<bool> OpenFileAsync(string path);
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
        RecentProjects.Remove(projectFolder);
        RecentProjects.Insert(0, projectFolder);
        if (RecentProjects.Count > 15) RecentProjects.RemoveRange(15, RecentProjects.Count - 15);
        Save();
    }
}

public static class AppServices
{
    public static AiSettings Ai { get; set; } = AiSettings.Load();
    public static UiSettings Ui { get; } = UiSettings.Load();
    public static IPlatformServices? Platform { get; set; }
    public static Core.Qa.SpellChecker Spell { get; } = new();
    public static Core.Remote.ServerSettings Servers { get; set; } = Core.Remote.ServerSettings.Load();
    public static ILocalApiHost? LocalApi { get; set; }

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
