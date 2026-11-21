using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein überwachter Ordner in den Einstellungen (bearbeitbar).</summary>
public partial class WatchRuleViewModel(WatchRule rule, Action changed) : ObservableObject
{
    public WatchRule Rule { get; } = rule;
    public string Folder { get => Rule.Folder; set { Rule.Folder = value.Trim(); changed(); OnPropertyChanged(); } }
    public string ProjectFolder { get => Rule.ProjectFolder; set { Rule.ProjectFolder = value.Trim(); changed(); OnPropertyChanged(); } }
    public string OutputFolder { get => Rule.OutputFolder; set { Rule.OutputFolder = value.Trim(); changed(); OnPropertyChanged(); } }
    public bool Pretranslate { get => Rule.Pretranslate; set { Rule.Pretranslate = value; changed(); OnPropertyChanged(); } }
    public bool AiTranslate { get => Rule.AiTranslate; set { Rule.AiTranslate = value; changed(); OnPropertyChanged(); } }
    public bool ExportTargets { get => Rule.ExportTargets; set { Rule.ExportTargets = value; changed(); OnPropertyChanged(); } }
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; changed(); OnPropertyChanged(); } }
}

/// <summary>
/// Automatisierung: überwachte Eingangsordner. Alle paar Sekunden schaut das Programm nach neuen Dateien und
/// verarbeitet sie (siehe <see cref="WatchProcessor"/>); das Protokoll steht in den Einstellungen und in der Statusleiste.
/// </summary>
public partial class AutomationViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Avalonia.Threading.DispatcherTimer _timer;
    private bool _running;

    public AutomationViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var r in WatchRules.Load()) Rules.Add(new WatchRuleViewModel(r, Save));
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await CheckAsync();
        if (Rules.Any(r => r.Enabled)) _timer.Start();
    }

    public ObservableCollection<WatchRuleViewModel> Rules { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<string> ProjectFolders => _main.RecentProjects;

    private void Save()
    {
        WatchRules.Save(Rules.Select(r => r.Rule));
        if (Rules.Any(r => r.Enabled)) _timer.Start();
        else _timer.Stop();
    }

    [RelayCommand]
    private void AddRule()
    {
        var rule = new WatchRule { ProjectFolder = _main.Project?.Folder ?? _main.RecentProjects.FirstOrDefault() ?? "", Enabled = false };
        Rules.Add(new WatchRuleViewModel(rule, Save));
        Save();
    }

    [RelayCommand]
    private async Task PickFolder(WatchRuleViewModel? row)
    {
        if (row == null || AppServices.Platform == null) return;
        if (await AppServices.Platform.PickFolderAsync("Eingangsordner wählen") is { } folder) row.Folder = folder;
    }

    [RelayCommand]
    private async Task PickOutput(WatchRuleViewModel? row)
    {
        if (row == null || AppServices.Platform == null) return;
        if (await AppServices.Platform.PickFolderAsync("Ausgangsordner wählen") is { } folder) row.OutputFolder = folder;
    }

    [RelayCommand]
    private void RemoveRule(WatchRuleViewModel? row)
    {
        if (row == null) return;
        Rules.Remove(row);
        Save();
    }

    private void Write(string message)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        _main.Status = message;
    }

    /// <summary>Alle aktiven Ordner einmal prüfen und neue Dateien verarbeiten (auch von Hand: „Jetzt prüfen“).</summary>
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            foreach (var row in Rules.Where(r => r.Enabled && r.Folder.Length > 0 && r.ProjectFolder.Length > 0).ToList())
            {
                var pending = WatchProcessor.PendingFiles(row.Rule);
                if (pending.Count == 0) continue;
                if (!Directory.Exists(row.ProjectFolder)) { Write($"Projekt nicht gefunden: {row.ProjectFolder}"); continue; }
                // ist das Projekt gerade offen, wird es mitbenutzt (die Dateiliste aktualisiert sich)
                var open = _main.Project is { } p && string.Equals(Path.GetFullPath(p.Folder), Path.GetFullPath(row.ProjectFolder), StringComparison.OrdinalIgnoreCase)
                    ? p : null;
                var project = open ?? await Task.Run(() => TranslationProject.Open(row.ProjectFolder));
                try
                {
                    AgentContext? ai = null;
                    if (row.AiTranslate && AppServices.Ai.Get(AppServices.Ai.TranslateProviderId) != null)
                        ai = AgentContext.From(AppServices.Ai, AppServices.Ai.TranslateProviderId);
                    foreach (var file in pending)
                    {
                        Write($"Verarbeite {Path.GetFileName(file)} → {project.Name} …");
                        try
                        {
                            var r = await WatchProcessor.ProcessAsync(row.Rule, file, project, ai, new Progress<string>(m => _main.Status = m));
                            Write($"{r.File}: {r.Segments} Segmente, {r.FromTm} aus dem TM, {r.ByAi} per KI" +
                                  (r.Outputs.Count > 0 ? $" – Zieldateien in {Path.GetDirectoryName(Path.GetDirectoryName(r.Outputs[0]))}" : "") + ".");
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or AiException
                                                   or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
                        {
                            Write($"{Path.GetFileName(file)}: Fehler – {ex.Message}");
                        }
                    }
                }
                finally
                {
                    if (open == null) project.Dispose();
                    else _main.RefreshFiles();
                }
            }
        }
        finally { _running = false; }
    }
}
