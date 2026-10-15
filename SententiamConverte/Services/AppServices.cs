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

/// <summary>Einstellungen der Oberfläche (zuletzt geöffnete Projekte, Projektordner).</summary>
public sealed class UiSettings
{
    public string ProjectsRoot { get; set; } = Path.Combine(AppPaths.DataRoot, "Projekte");

    public List<string> RecentProjects { get; set; } = new();

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
}
