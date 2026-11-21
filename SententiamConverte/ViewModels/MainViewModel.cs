using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public partial class FileRowViewModel : ObservableObject
{
    public required ProjectFile File { get; init; }
    public required string TargetLanguage { get; init; }
    public string Name => File.OriginalName;
    public string Language => TargetLanguage;

    [ObservableProperty] public partial int Words { get; set; }
    [ObservableProperty] public partial int Percent { get; set; }
}

/// <summary>Ein Projekt der Liste zum Umschalten (Name, Sprachen, Ordner; das offene ist markiert).</summary>
public sealed record RecentProjectRow(MainViewModel Owner, ProjectSummary Info, bool IsCurrent)
{
    public string Folder => Info.Folder;
    public string Name => Info.Name;
    public string Details => Info.Languages + (Info.IsShared ? " · " + L.T("Team") : "") + " · " + Info.Folder;
    public string Mark => IsCurrent ? "✓" : "";
}

/// <summary>Arbeitsschritt einer Datei in einer Zielsprache (gemeinsames Projekt).</summary>
public partial class TeamStepRow(string fileId, string fileName, FileStep step, string me, bool admin) : ObservableObject
{
    public string FileId { get; } = fileId;
    public string FileName { get; } = fileName;
    public FileStep Step { get; } = step;
    public string Language => Step.Language;
    [ObservableProperty] public partial int StepIndex { get; set; } = (int)step.Step;
    [ObservableProperty] public partial string? Translator { get; set; } = step.Translator ?? "";
    [ObservableProperty] public partial string? Reviewer { get; set; } = step.Reviewer ?? "";
    [ObservableProperty] public partial string? Approver { get; set; } = step.Approver ?? "";

    public static string StepName(WorkflowStep s) => L.T(s switch
    {
        WorkflowStep.Translation => "Übersetzung",
        WorkflowStep.Review => "Lektorat",
        WorkflowStep.Approval => "Freigabe",
        _ => "fertig",
    });

    public string StepText => StepName(Step.Step) + (Step.Assignee is { } a ? " · " + a : "");
    public bool IsMine => Step.Assignee == me;
    public bool CanAdvance => Step.Step != WorkflowStep.Done && (admin || Step.Assignee == null || Step.Assignee == me);
    public bool IsAdmin => admin;
}

/// <summary>Anteil des Grundpreises für eine Trefferstufe (bearbeitbar).</summary>
public partial class WeightRow(MatchBand band, int weight, Action changed) : ObservableObject
{
    public MatchBand Band { get; } = band;
    public string Name => L.T(AnalysisResult.BandName(Band));

    public int Weight
    {
        get => weight;
        set
        {
            if (weight == value) return;
            weight = Math.Clamp(value, 0, 300);
            OnPropertyChanged();
            changed();
        }
    }
}

public sealed record AnalysisRow(string Band, int Segments, int Words, int Characters)
{
    public string BandText => L.T(Band);
    public string SegmentsText => L.T($"{Segments} Segm.");
    public string WordsText => L.T($"{Words} Wörter");
    public string CharactersText => L.T($"{Characters} Zeichen");
}

/// <summary>Bearbeitbare Kopie einer eigenen QA-Regel.</summary>
public partial class RegexRuleViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string SourcePattern { get; set; } = "";
    [ObservableProperty] public partial string TargetPattern { get; set; } = "";
    [ObservableProperty] public partial bool IsError { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    public static RegexRuleViewModel From(RegexRule r) => new()
    {
        Name = r.Name, SourcePattern = r.SourcePattern ?? "", TargetPattern = r.TargetPattern,
        IsError = r.Severity == QaSeverity.Error, Enabled = r.Enabled,
    };

    public RegexRule ToRule() => new()
    {
        Name = Name, SourcePattern = SourcePattern.Length > 0 ? SourcePattern : null, TargetPattern = TargetPattern,
        Severity = IsError ? QaSeverity.Error : QaSeverity.Warning, Enabled = Enabled,
    };
}

public partial class MainViewModel : ViewModelBase
{
    public EditorViewModel Editor { get; }
    public ResourcesViewModel Resources { get; }
    public AgentsViewModel Agents { get; }
    public SettingsViewModel Settings { get; }
    public KnowledgeViewModel Knowledge { get; }
    public ExtensionsViewModel Extensions { get; }
    public HelpViewModel Help { get; } = new();

    public const int KnowledgeTab = 3;
    public const int ToolsTab = 4;
    public const int AgentsTab = 5;
    public const int AiTab = 6;
    public const int SettingsTab = 7;
    public const int ExtensionsTab = 8;
    public const int HelpTab = 9;

    public MainViewModel()
    {
        Editor = new EditorViewModel(this);
        Resources = new ResourcesViewModel(this);
        Agents = new AgentsViewModel(this);
        Settings = new SettingsViewModel(this);
        Knowledge = new KnowledgeViewModel(this);
        Privacy = new PrivacyViewModel(this);
        Outbound = new OutboundViewModel();
        Tools = new ToolsViewModel(this);
        Learning = new LearningViewModel(this);
        Start = new GettingStartedViewModel(this);
        Automation = new AutomationViewModel(this);
        Extensions = new ExtensionsViewModel(this);
        Core.Projects.TranslationProject.Servers = () => AppServices.Servers;
        AppServices.Main = this;
        foreach (var p in AppServices.Ui.RecentProjects.Where(p => File.Exists(Path.Combine(p, TranslationProject.FileName))))
            RecentProjects.Add(p);
        RefreshRecentRows();
        LanguageCodes = new ObservableCollection<string>(Languages.Common.Select(l => l.Code));
        Knowledge.OnProjectChanged();          // allgemeines KI-Gedächtnis auch ohne Projekt
        Privacy.Refresh();                     // Datenschutz ohne Projekt gilt
        _ = AdoptLocalSearchAsync();           // läuft ein eigenes SearXNG, sucht die Recherche im ganzen Netz
    }

    /// <summary>
    /// Läuft die eigene Websuche (SearXNG auf diesem Rechner), die Einstellungen kennen aber nur Wikipedia: selbst
    /// umstellen – die Recherche sucht dann auf Fachseiten und im ganzen Netz der jeweiligen Sprache.
    /// </summary>
    private async Task AdoptLocalSearchAsync()
    {
        try
        {
            if (AppServices.Ai.Search.ActiveSources().Any(s => s.Kind is not (Core.Ai.SearchProviderKind.Wikipedia or Core.Ai.SearchProviderKind.None))) return;
            if (!await Core.Ai.LocalSearXng.ProbeAsync()) return;
            if (!AppServices.Ai.Search.AdoptLocalSearXng(Core.Ai.LocalSearXng.BaseUrl)) return;
            AppServices.Ai.Save();
            Editor.CheckAi();
            Status = L.T("Eigene Websuche (SearXNG) erkannt: Die Recherche sucht jetzt auf Fachseiten und im ganzen Netz, nicht nur in Wikipedia.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>Datenschutz des offenen Projekts (ohne Projekt: Voreinstellung).</summary>
    public PrivacyViewModel Privacy { get; }

    /// <summary>Datenausgang: was den Rechner verlässt, Rückfrage vor dem Senden.</summary>
    public OutboundViewModel Outbound { get; }

    /// <summary>Hilfsmittel für Übersetzer: Katalog, Vorschläge, Nachschlagen.</summary>
    public ToolsViewModel Tools { get; }

    [RelayCommand]
    private void ShowTools() => SelectedTab = ToolsTab;

    /// <summary>Aus Korrekturen lernen: Regeln, Hinweis im Editor.</summary>
    public LearningViewModel Learning { get; }

    // ---------- Zustand ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title), nameof(ProjectSwitchLabel), nameof(RecentTitle))]
    public partial TranslationProject? Project { get; set; }

    public bool HasProject => Project != null;
    public string Title => Project == null ? "Sententiam converte" : $"Sententiam converte – {Project.Name}";

    [ObservableProperty] public partial string Status { get; set; } = "Bereit.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial int SelectedTab { get; set; }

    /// <summary>„Erste Schritte“ für neue Benutzer im Reiter Projekt.</summary>
    public GettingStartedViewModel Start { get; }

    /// <summary>Überwachte Ordner (Einstellungen → Automatisierung).</summary>
    public AutomationViewModel Automation { get; }

    /// <summary>Meldung, wenn dem Projekt gemeinsame Datenbanken fehlen (leer = nichts fehlt).</summary>
    [ObservableProperty] public partial string DatabaseHint { get; set; } = "";

    [RelayCommand]
    private void CreateRecommendedDatabases() => Resources.CreateAllRecommendedCommand.Execute(null);

    [RelayCommand]
    private void AlwaysCreateDatabases()
    {
        Resources.AutoCreateDatabases = true;
        Resources.CreateAllRecommendedCommand.Execute(null);
    }

    /// <summary>Alte Übersetzungen (Original + Übersetzung) ins TM und in die Terminologie übernehmen.</summary>
    [RelayCommand]
    private void OpenImportTranslations()
    {
        Resources.Alignment.CloseCommand.Execute(null);
        Resources.ImportTranslations.Open();
        SelectedTab = 2;
    }

    [RelayCommand]
    private void ShowDatabases()
    {
        Resources.RefreshDatabasesCommand.Execute(null);
        SelectedTab = 2;
    }

    public ObservableCollection<string> RecentProjects { get; } = new();

    /// <summary>Zuletzt geöffnete Projekte mit Name und Sprachen – zum Umschalten in der Statusleiste.</summary>
    public ObservableCollection<RecentProjectRow> RecentRows { get; } = new();

    /// <summary>Die anderen davon (ohne das offene Projekt) – Liste im Reiter Projekt.</summary>
    public ObservableCollection<RecentProjectRow> OtherProjects { get; } = new();

    public bool HasOtherProjects => OtherProjects.Count > 0;
    public bool HasRecent => RecentRows.Count > 0;
    public string RecentTitle => HasProject ? "Andere Projekte" : "Zuletzt geöffnet";
    public string ProjectSwitchLabel => Project?.Name ?? L.T("Kein Projekt");
    public ObservableCollection<FileRowViewModel> Files { get; } = new();
    public ObservableCollection<AnalysisRow> AnalysisRows { get; } = new();
    public ObservableCollection<string> LanguageCodes { get; }
    public ObservableCollection<RegexRuleViewModel> RegexRules { get; } = new();

    [ObservableProperty] public partial QaSettings? Qa { get; set; }
    [ObservableProperty] public partial bool IsPackageProject { get; set; }
    [ObservableProperty] public partial string ForbiddenWords { get; set; } = "";

    [ObservableProperty] public partial FileRowViewModel? SelectedFile { get; set; }

    // Neues Projekt
    [ObservableProperty] public partial bool ShowNewProject { get; set; }
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string NewSource { get; set; } = "de-DE";
    [ObservableProperty] public partial string NewTargets { get; set; } = "en-GB";
    [ObservableProperty] public partial string NewTopic { get; set; } = "";

    // Projekteinstellungen, die man im Projektbereich ändern kann
    [ObservableProperty] public partial string ProjectTopic { get; set; } = "";
    [ObservableProperty] public partial int PretranslateMinScore { get; set; } = 100;
    [ObservableProperty] public partial bool ConfirmContextMatches { get; set; } = true;
    [ObservableProperty] public partial bool AutoLocalize { get; set; } = true;

    // Gemeinsame Projekte
    [ObservableProperty] public partial bool ShowShared { get; set; }
    [ObservableProperty] public partial string? SharedServer { get; set; }
    [ObservableProperty] public partial SharedProjectInfo? SelectedShared { get; set; }
    [ObservableProperty] public partial bool NewOnServer { get; set; }
    public ObservableCollection<SharedProjectInfo> SharedProjects { get; } = new();
    public IEnumerable<string> ServerNames => AppServices.Servers.Servers.Select(s => s.Name);
    public bool IsSharedProject => Project?.IsShared == true;
    [ObservableProperty] public partial string? ProjectProfile { get; set; }
    public IEnumerable<string> ProfileNames => new[] { "(keins)" }.Concat(AppServices.Ai.SourceProfiles.Select(p => p.Name));

    /// <summary>Das Fachquellen-Profil des offenen Projekts.</summary>
    public SourceProfile? CurrentProfile => AppServices.Ai.Profile(Project?.SourceProfile);

    public string ProjectsRoot
    {
        get => AppServices.Ui.ProjectsRoot;
        set { AppServices.Ui.ProjectsRoot = value; AppServices.Ui.Save(); OnPropertyChanged(); }
    }

    internal async Task RunBusy(string message, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = message;
        try { await action(); }
        catch (Exception ex) { Status = "Fehler: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---------- Projekt anlegen / öffnen ----------

    [RelayCommand]
    private void BeginNewProject()
    {
        NewName = "";
        ShowNewProject = true;
    }

    [RelayCommand]
    private void CancelNewProject() => ShowNewProject = false;

    [RelayCommand]
    private async Task BeginShared()
    {
        ShowShared = !ShowShared;
        OnPropertyChanged(nameof(ServerNames));
        SharedServer ??= ServerNames.FirstOrDefault();
        if (ShowShared) await LoadSharedProjects();
    }

    [RelayCommand]
    private async Task LoadSharedProjects()
    {
        SharedProjects.Clear();
        var account = SharedServer == null ? null : AppServices.Servers.Get(SharedServer);
        if (account == null) { Status = "Zuerst unter Einstellungen → Teamserver einen Zugang einrichten."; return; }
        try
        {
            foreach (var p in await Task.Run(() => new SharedProjectClient(Core.Resources.ResourceFactory.Api(account)).List()))
                SharedProjects.Add(p);
            Status = $"{SharedProjects.Count} gemeinsame Projekte auf „{account.Name}“.";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task OpenShared()
    {
        if (SelectedShared == null || SharedServer == null) return;
        var name = SelectedShared.Name;
        var server = SharedServer;
        await RunBusy("Öffne gemeinsames Projekt …", async () =>
        {
            var safe = string.Concat($"{name} ({server})".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (!await LeaveProjectAsync()) return;
            var project = await Task.Run(() => TranslationProject.OpenShared(Path.Combine(ProjectsRoot, "Team", safe), server, name));
            SetProject(project);
            ShowShared = false;
            Status = $"Gemeinsames Projekt „{name}“ geöffnet. Änderungen werden laufend mit dem Team abgeglichen.";
        });
    }

    [RelayCommand]
    private async Task CreateProject()
    {
        var name = NewName.Trim();
        if (name.Length == 0) { Status = "Bitte einen Projektnamen eingeben."; return; }
        var targets = NewTargets.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (targets.Count == 0) { Status = "Bitte mindestens eine Zielsprache angeben."; return; }
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var folder = Path.Combine(ProjectsRoot, safe);
        await RunBusy("Lege Projekt an …", async () =>
        {
            if (!await LeaveProjectAsync()) return;
            TranslationProject project;
            if (NewOnServer)
            {
                var server = SharedServer ?? ServerNames.FirstOrDefault() ?? throw new InvalidOperationException("Kein Teamserver eingerichtet.");
                var account = AppServices.Servers.Get(server)!;
                await Task.Run(() => new SharedProjectClient(Core.Resources.ResourceFactory.Api(account))
                    .Create(new SharedProjectCreate(name, NewSource.Trim(), targets, NewTopic.Trim())));
                project = await Task.Run(() => TranslationProject.OpenShared(Path.Combine(ProjectsRoot, "Team", $"{safe} ({server})"), server, name));
            }
            else project = await Task.Run(() => TranslationProject.Create(folder, name, NewSource.Trim(), targets, NewTopic.Trim()));
            if (AppServices.Ui.LastRates is { } rates)
            {
                project.Rates = rates.Clone();                          // zuletzt verwendete Preise
                project.Save();
            }
            if (NewTemplate is { } template && !NewOnServer) ProjectTemplates.Apply(template, project);
            if (AppServices.Ai.Privacy.Enabled && project.Privacy.Terms.Count == 0 && !project.Privacy.Enabled)
            {
                project.Privacy = AppServices.Ai.Privacy.Clone();      // Vorlage für neue Projekte
                project.Save();
            }
            SetProject(project);
            ShowNewProject = false;
            Status = $"Projekt „{name}“ angelegt. Jetzt Dateien hinzufügen.";
        });
    }

    [RelayCommand]
    private async Task OpenProjectFolder()
    {
        var folder = await AppServices.Platform!.PickFolderAsync("Projektordner öffnen");
        if (folder != null) await OpenProject(folder);
    }

    [RelayCommand]
    private async Task OpenProject(string folder)
    {
        var inEditor = SelectedTab == 1;
        await RunBusy("Öffne Projekt …", async () =>
        {
            if (!await LeaveProjectAsync()) return;
            var project = await Task.Run(() => TranslationProject.Open(folder));
            SetProject(project);
            Status = $"Projekt „{project.Name}“ geöffnet.";
            // im Editor geht es dort weiter, wo man in diesem Projekt zuletzt stand; ohne gemerkte Stelle: Datei wählen
            if (inEditor && !await RestorePlaceAsync()) SelectedTab = 0;
        });
    }

    /// <summary>Zu einem anderen Projekt umschalten (Statusleiste oder Liste im Reiter Projekt).</summary>
    [RelayCommand]
    private async Task SwitchProject(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (Project != null && UiSettings.SamePath(Project.Folder, folder)) return;
        if (IsBusy) { Status = "Erst wenn die laufende Arbeit fertig ist, lässt sich das Projekt wechseln."; return; }
        await OpenProject(folder);
    }

    /// <summary>Aus der Liste nehmen – der Projektordner bleibt, wie er ist.</summary>
    [RelayCommand]
    private void ForgetProject(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || Project != null && UiSettings.SamePath(Project.Folder, folder)) return;
        AppServices.Ui.Forget(folder);
        foreach (var gone in RecentProjects.Where(p => UiSettings.SamePath(p, folder)).ToList()) RecentProjects.Remove(gone);
        RefreshRecentRows();
        Status = "Aus der Liste genommen – der Projektordner bleibt, wie er ist.";
    }

    /// <summary>Aus der Statusleiste: in den Reiter Projekt und ein neues anlegen.</summary>
    [RelayCommand]
    private void NewProjectFromSwitcher()
    {
        SelectedTab = 0;
        BeginNewProject();
    }

    /// <summary>
    /// Vor dem Wechsel: Stelle im Editor merken und laufende KI-Arbeit anhalten – erst wenn sie steht, wird gewechselt;
    /// sonst schriebe sie Ergebnisse und Meldungen des alten Dokuments womöglich ins neue Projekt.
    /// </summary>
    private async Task<bool> LeaveProjectAsync()
    {
        if (Project == null) return true;
        RememberPlace();
        if (Editor.IsAiWorking) Status = "Halte die KI-Arbeit im Editor an …";
        if (await Editor.StopWorkAsync(TimeSpan.FromSeconds(15))) return true;
        Status = "Die KI-Arbeit im Editor hält noch nicht an – bitte gleich noch einmal wechseln.";
        return false;
    }

    /// <summary>Datei, Zielsprache und Segment des Editors für das offene Projekt merken.</summary>
    public void RememberPlace()
    {
        if (Project is { } p && Editor.CurrentFile is { } f && Editor.TargetLanguage is { Length: > 0 } lang)
            AppServices.Ui.SetPlace(p.Folder, new ProjectPlace(f.Id, lang, Editor.Selected?.Id));
    }

    /// <summary>Im Editor dort weitermachen, wo man in diesem Projekt zuletzt stand. False, wenn nichts gemerkt ist.</summary>
    private async Task<bool> RestorePlaceAsync()
    {
        if (Project is not { } p || AppServices.Ui.PlaceOf(p.Folder) is not { } place) return false;
        var row = Files.FirstOrDefault(r => r.File.Id == place.FileId && r.TargetLanguage == place.Language);
        if (row == null) return false;                                   // Datei inzwischen entfernt
        try { await Editor.OpenAsync(row.File, row.TargetLanguage); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status = $"{row.Name} ließ sich nicht öffnen: {ex.Message}";
            return false;
        }
        if (Project != p) return false;
        SelectedFile = row;
        if (place.SegmentId is int id && Editor.Segments.FirstOrDefault(s => s.Id == id) is { } seg) Editor.Selected = seg;
        return true;
    }

    /// <summary>Liste zum Umschalten neu aufbauen (Name und Sprachen aus project.json, ohne die Projekte zu öffnen).</summary>
    private void RefreshRecentRows()
    {
        RecentRows.Clear();
        OtherProjects.Clear();
        foreach (var folder in RecentProjects)
        {
            if (TranslationProject.Peek(folder) is not { } info) continue;
            var row = new RecentProjectRow(this, info, Project != null && UiSettings.SamePath(Project.Folder, folder));
            RecentRows.Add(row);
            if (!row.IsCurrent) OtherProjects.Add(row);
        }
        OnPropertyChanged(nameof(HasOtherProjects));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(RecentTitle));
    }

    private void SetProject(TranslationProject project)
    {
        Editor.Close();
        Project?.Dispose();
        Project = project;
        ProjectTopic = project.Topic;
        PretranslateMinScore = project.PretranslateMinScore;
        ConfirmContextMatches = project.ConfirmContextMatches;
        LoadRates();
        AutoLocalize = project.AutoLocalize;
        OnPropertyChanged(nameof(IsSharedProject));
        OnPropertyChanged(nameof(ProfileNames));
        ProjectProfile = project.SourceProfile ?? "(keins)";
        Qa = project.Qa;
        IsPackageProject = project.IsFromPackage;
        ForbiddenWords = string.Join(", ", project.Qa.ForbiddenWords);
        RegexRules.Clear();
        foreach (var r in project.Qa.RegexRules) RegexRules.Add(RegexRuleViewModel.From(r));
        AppServices.Ui.Remember(project.Folder);
        Tools.RefreshSuggestions();
        foreach (var same in RecentProjects.Where(f => UiSettings.SamePath(f, project.Folder)).ToList()) RecentProjects.Remove(same);
        RecentProjects.Insert(0, project.Folder);
        RefreshRecentRows();
        RefreshFiles();
        Resources.Refresh();
        Resources.Harvest.Refresh();
        Agents.OnProjectChanged();
        Knowledge.OnProjectChanged();
        Privacy.Refresh();                               // Datenschutz dieses Projekts gilt ab jetzt für alle KI-Anfragen
        _ = LoadTeamStepsAsync();                        // Team: Arbeitsschritte und Zuständige
        _ = Privacy.SuggestAsync();                      // persönliche Angaben in den Dateien? dann „Vertraulich“ vorschlagen
        Resources.CheckDatabases();                      // fehlende gemeinsame Datenbanken melden bzw. anlegen
        DatabaseHint = Resources.MissingText;
        if (project.ResourceErrors.Count > 0)
            Status = "Nicht erreichbar: " + string.Join("; ", project.ResourceErrors);
    }

    partial void OnSelectedTabChanged(int value)
    {
        Core.Diagnostics.ActivityLog.Write("Oberfläche", "Reiter gewechselt", value switch
        {
            0 => "Projekt", 1 => "Editor", 2 => "TM & Terminologie", 3 => "Wissen", ToolsTab => "Hilfsmittel", AgentsTab => "Agenten",
            AiTab => "KI einrichten", SettingsTab => "Einstellungen", ExtensionsTab => "Erweiterungen", HelpTab => "Hilfe", _ => value.ToString(),
        });
        // Beim Wechsel zurück zum Projekt den Fortschritt aus dem Editor übernehmen
        if (value == 0 && Project != null)
        {
            Editor.SaveIfOpen();
            RefreshFiles();
        }
        if (value == ExtensionsTab) Extensions.Refresh();      // Bezug (offenes Dokument, Segment) aktuell anzeigen
        if (value == 2) Resources.RefreshDatabases();          // Datenbank-Übersicht aktuell
        if (value == ToolsTab) Tools.RefreshSites();           // Quellen-Datenbank wächst mit jeder Recherche
        if (value == 1) Editor.CheckAi();                      // KI inzwischen eingerichtet oder gewechselt?
        // in den Editor ohne offenes Dokument (etwa nach dem Umschalten): dort weiter, wo man in diesem Projekt stand
        if (value == 1 && Project != null && Editor.CurrentFile == null && !IsBusy) _ = RestorePlaceAsync();
        if (value == AgentsTab) Agents.RefreshSkillsCommand.Execute(null);
        if (value == ToolsTab) Tools.RefreshSuggestions();
        if (value == AiTab) _ = Settings.RefreshLocalSearch();        // Docker und eigene Websuche: Zustand zeigen
    }

    partial void OnProjectTopicChanged(string value)
    {
        if (Project == null || Project.Topic == value) return;
        Project.Topic = value;
        Tools?.RefreshSuggestions();
        Project.Save();
    }

    partial void OnAutoLocalizeChanged(bool value)
    {
        if (Project == null || Project.AutoLocalize == value) return;
        Project.AutoLocalize = value;
        Project.Save();
    }

    partial void OnConfirmContextMatchesChanged(bool value)
    {
        if (Project == null || Project.ConfirmContextMatches == value) return;
        Project.ConfirmContextMatches = value;
        Project.Save();
    }

    partial void OnProjectProfileChanged(string? value)
    {
        if (Project == null) return;
        var name = value is null or "(keins)" ? null : value;
        if (Project.SourceProfile == name) return;
        Project.SourceProfile = name;
        Project.Save();
        Agents.OnProjectChanged();
    }

    partial void OnPretranslateMinScoreChanged(int value)
    {
        if (Project == null || Project.PretranslateMinScore == value) return;
        Project.PretranslateMinScore = Math.Clamp(value, 50, 100);
        Project.Save();
    }

    public void RefreshFiles()
    {
        Files.Clear();
        if (Project == null) return;
        foreach (var (f, lang) in Project.Documents())
            {
                var row = new FileRowViewModel { File = f, TargetLanguage = lang };
                try
                {
                    var (total, done) = Project.Progress(f, lang);
                    row.Words = total;
                    row.Percent = total == 0 ? 100 : done * 100 / total;
                }
                catch (IOException) { }
                Files.Add(row);
            }
    }

    // ---------- QA-Einstellungen ----------

    [RelayCommand]
    private void AddRegexRule() => RegexRules.Add(new RegexRuleViewModel { Name = "Neue Regel" });

    [RelayCommand]
    private void RemoveRegexRule(RegexRuleViewModel? rule)
    {
        if (rule != null) RegexRules.Remove(rule);
    }

    [RelayCommand]
    private void SaveQaSettings()
    {
        if (Project == null) return;
        Project.Qa.ForbiddenWords = ForbiddenWords.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).ToList();
        Project.Qa.RegexRules = RegexRules.Where(r => r.TargetPattern.Length > 0).Select(r => r.ToRule()).ToList();
        foreach (var r in Project.Qa.RegexRules)
        {
            try { _ = new System.Text.RegularExpressions.Regex(r.TargetPattern); if (r.SourcePattern != null) _ = new System.Text.RegularExpressions.Regex(r.SourcePattern); }
            catch (ArgumentException ex) { Status = $"Regel „{r.Name}“ ist ungültig: {ex.Message}"; return; }
        }
        Project.Save();
        Status = "QA-Einstellungen gespeichert.";
    }

    // ---------- Team: Arbeitsschritte ----------

    public ObservableCollection<TeamStepRow> TeamSteps { get; } = new();
    public ObservableCollection<string> TeamUserNames { get; } = new();
    public IReadOnlyList<string> StepNames { get; } = Enum.GetValues<WorkflowStep>().Select(TeamStepRow.StepName).ToList();
    [ObservableProperty] public partial bool IsTeamAdmin { get; set; }
    [ObservableProperty] public partial string TeamMe { get; set; } = "";
    [ObservableProperty] public partial string TeamInfo { get; set; } = "";
    public bool HasTeamSteps => TeamSteps.Count > 0;

    /// <summary>Arbeitsschritt einer Datei (null = kein gemeinsames Projekt oder Server ohne Arbeitsschritte).</summary>
    public FileStep? TeamStepFor(string fileId, string language) =>
        TeamSteps.FirstOrDefault(t => t.FileId == fileId && t.Language.Equals(language, StringComparison.OrdinalIgnoreCase))?.Step;

    public async Task LoadTeamStepsAsync()
    {
        TeamSteps.Clear();
        TeamInfo = "";
        if (Project is not { IsShared: true } p) { OnPropertyChanged(nameof(HasTeamSteps)); return; }
        try
        {
            var (files, users, me) = await Task.Run(p.TeamWorkflow);
            TeamMe = me.User;
            IsTeamAdmin = me.Role == "Admin";
            TeamUserNames.Clear();
            TeamUserNames.Add("");
            foreach (var u in users.Where(u => u.Role != "Reader")) TeamUserNames.Add(u.Name);
            foreach (var f in files)
                foreach (var step in f.Steps ?? [])
                    TeamSteps.Add(new TeamStepRow(f.Id, f.Name, step, me.User, IsTeamAdmin));
            var mine = TeamSteps.Count(t => t.IsMine);
            TeamInfo = mine > 0 ? $"Sie sind bei {mine} Datei(en) an der Reihe." : "";
        }
        catch (Core.Remote.RemoteException ex) { TeamInfo = ex.Message; }
        OnPropertyChanged(nameof(HasTeamSteps));
    }

    [RelayCommand]
    private Task RefreshTeam() => LoadTeamStepsAsync();

    [RelayCommand]
    private async Task SaveTeamStep(TeamStepRow? row)
    {
        if (row == null || Project is not { IsShared: true } p) return;
        try
        {
            await Task.Run(() => p.SetTeamStep(row.FileId, row.Language,
                new WorkflowUpdate((WorkflowStep)row.StepIndex, row.Translator ?? "", row.Reviewer ?? "", row.Approver ?? "")));
            Status = $"Arbeitsschritt für „{row.FileName}“ ({row.Language}) gespeichert.";
        }
        catch (Core.Remote.RemoteException ex) { Status = ex.Message; }
        await LoadTeamStepsAsync();
    }

    [RelayCommand]
    private async Task AdvanceTeamStep(TeamStepRow? row)
    {
        if (row == null || Project is not { IsShared: true } p) return;
        Editor.SaveIfOpen();
        try
        {
            var next = await Task.Run(() => p.SetTeamStep(row.FileId, row.Language, new WorkflowUpdate(Advance: true)));
            Status = $"„{row.FileName}“ ({row.Language}) abgegeben – jetzt: {TeamStepRow.StepName(next.Step)}" + (next.Assignee is { } a ? $" bei {a}." : ".");
        }
        catch (Core.Remote.RemoteException ex) { Status = ex.Message; }
        await LoadTeamStepsAsync();
        if (Editor.CurrentFile?.Id == row.FileId) Editor.ApplyTeamStep();
    }

    // ---------- Projektvorlagen und QA-Profile ----------

    public ObservableCollection<ProjectTemplate> Templates { get; } = new(ProjectTemplates.Load());
    public bool HasTemplates => Templates.Count > 0;
    [ObservableProperty] public partial ProjectTemplate? NewTemplate { get; set; }
    [ObservableProperty] public partial string TemplateName { get; set; } = "";

    partial void OnNewTemplateChanged(ProjectTemplate? value)
    {
        if (value == null) return;
        NewSource = value.SourceLanguage;
        NewTargets = string.Join(", ", value.TargetLanguages);
        if (NewTopic.Trim().Length == 0) NewTopic = value.Topic;
    }

    private void ReloadTemplates()
    {
        Templates.Clear();
        foreach (var t in ProjectTemplates.Load()) Templates.Add(t);
        OnPropertyChanged(nameof(HasTemplates));
    }

    [RelayCommand]
    private void SaveAsTemplate()
    {
        if (Project == null) return;
        SaveQaSettings();
        var name = TemplateName.Trim().Length > 0 ? TemplateName.Trim() : Project.Name;
        ProjectTemplates.SaveFrom(Project, name);
        ReloadTemplates();
        TemplateName = "";
        Status = $"Vorlage „{name}“ gespeichert – beim nächsten neuen Projekt unter „Vorlage“ wählen.";
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (NewTemplate is not { } t) return;
        ProjectTemplates.Delete(t.Name);
        NewTemplate = null;
        ReloadTemplates();
        Status = $"Vorlage „{t.Name}“ gelöscht.";
    }

    public ObservableCollection<QaProfile> QaProfileList { get; } = new(QaProfiles.Load());
    [ObservableProperty] public partial QaProfile? SelectedQaProfile { get; set; }
    [ObservableProperty] public partial string QaProfileName { get; set; } = "";

    private void ReloadQaProfiles(string? select = null)
    {
        QaProfileList.Clear();
        foreach (var p in QaProfiles.Load()) QaProfileList.Add(p);
        SelectedQaProfile = QaProfileList.FirstOrDefault(p => p.Name == select);
    }

    /// <summary>Die QA-Einstellungen des Projekts anzeigen (nach Laden eines Profils oder Öffnen).</summary>
    private void ShowQa(QaSettings qa)
    {
        Qa = null;
        Qa = qa;
        ForbiddenWords = string.Join(", ", qa.ForbiddenWords);
        RegexRules.Clear();
        foreach (var r in qa.RegexRules) RegexRules.Add(RegexRuleViewModel.From(r));
    }

    [RelayCommand]
    private void LoadQaProfile()
    {
        if (Project == null || SelectedQaProfile is not { } profile) return;
        Project.Qa = QaProfiles.CloneSettings(profile.Settings);
        Project.Save();
        ShowQa(Project.Qa);
        Status = $"QA-Profil „{profile.Name}“ übernommen.";
    }

    [RelayCommand]
    private void SaveQaProfile()
    {
        if (Project == null) return;
        var name = QaProfileName.Trim().Length > 0 ? QaProfileName.Trim() : SelectedQaProfile?.Name ?? "";
        if (name.Length == 0) { Status = "Bitte einen Namen für das QA-Profil eingeben."; return; }
        SaveQaSettings();
        QaProfiles.Save(name, Project.Qa);
        ReloadQaProfiles(name);
        QaProfileName = "";
        Status = $"QA-Profil „{name}“ gespeichert.";
    }

    [RelayCommand]
    private void DeleteQaProfile()
    {
        if (SelectedQaProfile is not { } profile) return;
        QaProfiles.Delete(profile.Name);
        ReloadQaProfiles();
        Status = $"QA-Profil „{profile.Name}“ gelöscht.";
    }

    // ---------- Projektpakete ----------

    [RelayCommand]
    private async Task ImportPackage()
    {
        var files = await AppServices.Platform!.PickFilesAsync("Projektpaket öffnen (.sdlppx)", ["*.sdlppx"], false);
        if (files.Count == 0) return;
        await RunBusy("Lese Projektpaket …", async () =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdlppx");
            await using (var s = await files[0].OpenRead())
            await using (var fs = File.Create(tmp))
                await s.CopyToAsync(fs);
            try
            {
                var info = ProjectPackage.Inspect(tmp);
                var safe = string.Concat(info.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var folder = Path.Combine(ProjectsRoot, safe);
                if (Directory.Exists(folder)) folder += "_" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                if (!await LeaveProjectAsync()) return;
                var project = await Task.Run(() => ProjectPackage.Import(tmp, folder));
                SetProject(project);
                Status = $"Paket „{info.Name}“ geöffnet: {project.Files.Count} Datei(en), {project.ProjectTm.Count()} TM-Einheiten.";
            }
            finally { File.Delete(tmp); }
        });
    }

    [RelayCommand]
    private async Task ExportReturnPackage()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await using var output = await AppServices.Platform!.SaveFileAsync("Rückgabepaket speichern", Project.Name + ".sdlrpx");
        if (output == null) return;
        await RunBusy("Erzeuge Rückgabepaket …", async () =>
        {
            var buffer = new MemoryStream();
            await Task.Run(() => ProjectPackage.ExportReturnPackage(Project, buffer));
            buffer.Position = 0;
            await buffer.CopyToAsync(output);
            Status = "Rückgabepaket gespeichert.";
        });
    }

    // ---------- Texterkennung für gescannte PDFs ----------

    [RelayCommand]
    private async Task OcrPdf()
    {
        if (Project == null) return;
        var ai = AppServices.Ai;
        var claude = ai.Get(ai.TranslateProviderId) is { Kind: AiProviderKind.Anthropic } t ? t
                   : ai.Providers.FirstOrDefault(p => p.Kind == AiProviderKind.Anthropic);
        if (claude == null)
        {
            Status = "Für die Texterkennung wird ein Claude-Zugang benötigt (Einstellungen). Alternativ das PDF vorher mit einem OCR-Programm umwandeln.";
            return;
        }
        var files = await AppServices.Platform!.PickFilesAsync("Gescanntes PDF erkennen", ["*.pdf"], false);
        if (files.Count == 0) return;
        await RunBusy("Texterkennung läuft (kann einige Minuten dauern) …", async () =>
        {
            byte[] pdf;
            await using (var s = await files[0].OpenRead())
            {
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                pdf = ms.ToArray();
            }
            var text = await new ClaudeChatModel(claude).ExtractPdfTextAsync(pdf);
            var docx = new MemoryStream();
            PdfFilter.OcrTextToDocx(text, Project.SourceLanguage, docx);
            docx.Position = 0;
            var name = Path.GetFileNameWithoutExtension(files[0].Name) + "_erkannt.docx";
            await Task.Run(() => Project.AddFile(docx, name));
            RefreshFiles();
            Status = $"Text erkannt und als „{name}“ hinzugefügt. Bitte das Ergebnis stichprobenartig mit dem Original vergleichen.";
        });
    }

    // ---------- Dateien ----------

    [RelayCommand]
    private async Task AddFiles()
    {
        if (Project == null) return;
        var patterns = FileFilters.SupportedExtensions.Select(e => "*" + e).ToList();
        var files = await AppServices.Platform!.PickFilesAsync("Dateien hinzufügen", patterns, true);
        await AddPickedFilesAsync(files);
    }

    /// <summary>Gewählte Dateien ins offene Projekt aufnehmen (auch aus „Erste Schritte“).</summary>
    internal async Task AddPickedFilesAsync(IReadOnlyList<PickedFile> files)
    {
        if (Project == null || files.Count == 0) return;
        await RunBusy("Lese Dateien ein …", async () =>
        {
            foreach (var f in files)
            {
                Status = $"Lese {f.Name} …";
                await using var stream = await f.OpenRead();
                var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                buffer.Position = 0;
                await Task.Run(() => Project.AddFile(buffer, f.Name));
            }
            RefreshFiles();
            Status = $"{files.Count} Datei(en) hinzugefügt.";
            await Privacy.SuggestAsync();
        });
    }

    [RelayCommand]
    private void RemoveFile(FileRowViewModel? row)
    {
        if (Project == null || row == null) return;
        if (Editor.CurrentFile == row.File) Editor.Close();
        Project.RemoveFile(row.File);
        RefreshFiles();
        Status = $"{row.Name} aus dem Projekt entfernt.";
    }

    [RelayCommand]
    private async Task OpenInEditor(FileRowViewModel? row)
    {
        row ??= SelectedFile;
        if (Project == null || row == null) return;
        await RunBusy($"Öffne {row.Name} …", async () =>
        {
            await Editor.OpenAsync(row.File, row.TargetLanguage);
            SelectedTab = 1;
            Status = $"{row.Name} ({row.TargetLanguage}) geöffnet.";
        });
    }

    [RelayCommand]
    private Task ExportAnonymizedSource(FileRowViewModel? row) => ExportAnonymized(row, source: true);

    [RelayCommand]
    private Task ExportAnonymizedTarget(FileRowViewModel? row) => ExportAnonymized(row, source: false);

    /// <summary>Anonymisierte Ausgangs- oder Zieldatei speichern (geschützte Angaben einheitlich durch Platzhalter ersetzt).</summary>
    private async Task ExportAnonymized(FileRowViewModel? row, bool source)
    {
        row ??= SelectedFile;
        if (Project == null || row == null) return;
        Editor.SaveIfOpen();
        var name = Project.AnonymizedFileName(row.File, row.TargetLanguage, source);
        await using var output = await AppServices.Platform!.SaveFileAsync("Anonymisierte Fassung speichern", name);
        if (output == null) return;
        await RunBusy("Anonymisiere …", async () =>
        {
            var buffer = new MemoryStream();
            var project = Project;
            var replaced = await Task.Run(() => project.ExportAnonymized(row.File, row.TargetLanguage, source, buffer));
            buffer.Position = 0;
            await buffer.CopyToAsync(output);
            Status = $"„{name}“ gespeichert – {replaced} geschützte Angaben ersetzt. Bitte vor dem Weitergeben durchsehen.";
        });
    }

    [RelayCommand]
    private async Task ExportTarget(FileRowViewModel? row)
    {
        row ??= SelectedFile;
        if (Project == null || row == null) return;
        Editor.SaveIfOpen();
        var name = Project.TargetFileName(row.File, row.TargetLanguage);
        await using var output = await AppServices.Platform!.SaveFileAsync("Zieldatei speichern", name);
        if (output == null) return;
        await RunBusy("Erzeuge Zieldatei …", async () =>
        {
            var buffer = new MemoryStream();
            await Task.Run(() => Project.ExportTarget(row.File, Project.LoadDocument(row.File, row.TargetLanguage), buffer));
            buffer.Position = 0;
            await buffer.CopyToAsync(output);
            Status = $"Zieldatei „{name}“ gespeichert.";
        });
    }

    [RelayCommand]
    private async Task ExportBilingual(FileRowViewModel? row)
    {
        row ??= SelectedFile;
        if (Project == null || row == null) return;
        Editor.SaveIfOpen();
        var name = Path.GetFileNameWithoutExtension(row.Name) + "_" + row.TargetLanguage + ".xlf";
        await using var output = await AppServices.Platform!.SaveFileAsync("Zweisprachiges XLIFF speichern", name);
        if (output == null) return;
        var doc = Project.LoadDocument(row.File, row.TargetLanguage);
        XliffFilter.ExportBilingual(doc, output);
        Status = $"„{name}“ gespeichert.";
    }

    // ---------- Zahlen anpassen, Pseudoübersetzung ----------

    [RelayCommand]
    private async Task LocalizeNumbers()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await RunBusy("Passe Zahlen und Datum an …", async () =>
        {
            var changed = 0;
            await Task.Run(() =>
            {
                foreach (var (f, lang) in Project.Documents())
                {
                    var doc = Project.LoadDocument(f, lang);
                    var before = changed;
                    foreach (var s in doc.Segments.Where(s => !s.Locked && s.Target.Length > 0))
                    {
                        var localized = Core.Tm.AutoLocalizer.Localize(s.Source, s.Target, doc.SourceLanguage, doc.TargetLanguage);
                        if (localized == s.Target) continue;
                        s.Target = localized;
                        s.Modified = DateTime.UtcNow;
                        changed++;
                    }
                    if (changed > before) Project.SaveDocument(f, doc);
                }
            });
            Editor.Reload();
            Status = changed == 0 ? "Alle Zahlen und Datumsangaben stehen schon in der Form der Zielsprache." : $"{changed} Segmente angepasst (Zahlen und Datum in der Form der Zielsprache).";
        });
    }

    /// <summary>Zieldatei mit Pseudoübersetzung erzeugen – das Projekt bleibt unverändert.</summary>
    [RelayCommand]
    private async Task PseudoTranslate()
    {
        var row = SelectedFile ?? Files.FirstOrDefault();
        if (Project == null || row == null) { Status = "Bitte zuerst eine Datei hinzufügen."; return; }
        Editor.SaveIfOpen();
        var target = Project.TargetFileName(row.File, row.TargetLanguage);
        var name = Path.GetFileNameWithoutExtension(target) + "_pseudo" + Path.GetExtension(target);
        await using var output = await AppServices.Platform!.SaveFileAsync("Pseudoübersetzung speichern", name);
        if (output == null) return;
        await RunBusy("Erzeuge Pseudoübersetzung …", async () =>
        {
            var doc = Project.LoadDocument(row.File, row.TargetLanguage);
            foreach (var s in doc.Segments.Where(s => !s.Locked)) s.Target = Core.Tm.PseudoTranslator.Translate(s.Source);
            await Task.Run(() => Project.ExportTarget(row.File, doc, output));
            Status = $"Pseudoübersetzung „{name}“ gespeichert – im Originalprogramm öffnen und Sonderzeichen, Länge und fehlende Textstellen prüfen.";
        });
    }

    // ---------- Prüfdatei (Lektorat in Word) ----------

    /// <summary>Beim Zurückholen auch unveränderte, übersetzte Segmente als lektoriert markieren.</summary>
    [ObservableProperty] public partial bool ReviewMarksUnchanged { get; set; }

    [RelayCommand]
    private async Task ExportReview(FileRowViewModel? row)
    {
        row ??= SelectedFile;
        if (Project == null || row == null) return;
        Editor.SaveIfOpen();
        var name = Path.GetFileNameWithoutExtension(row.Name) + "_" + row.TargetLanguage + "_Pruefung.docx";
        await using var output = await AppServices.Platform!.SaveFileAsync("Prüfdatei speichern", name);
        if (output == null) return;
        var doc = Project.LoadDocument(row.File, row.TargetLanguage);
        ReviewDocx.Export(doc, output, L.DocumentLanguage);
        Status = $"Prüfdatei „{name}“ gespeichert – der Prüfer bearbeitet nur die Spalte „Übersetzung“.";
    }

    [RelayCommand]
    private async Task ImportReview()
    {
        if (Project == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("Korrigierte Prüfdatei wählen", ["*.docx"], false);
        if (files.Count == 0) return;
        Editor.SaveIfOpen();
        await RunBusy("Lese Prüfdatei …", async () =>
        {
            var ms = new MemoryStream();
            await using (var s = await files[0].OpenRead()) await s.CopyToAsync(ms);
            ms.Position = 0;
            var id = ReviewDocx.DocumentIdOf(ms);
            // passende Datei über die beim Export gespeicherte Kennung, sonst die gewählte
            (ProjectFile File, string Language, BilingualDocument Doc)? match = null;
            foreach (var (f, lang) in Project.Documents())
            {
                var d = Project.LoadDocument(f, lang);
                if (d.Id == id) { match = (f, lang, d); break; }
            }
            if (match == null && SelectedFile is { } sel) match = (sel.File, sel.TargetLanguage, Project.LoadDocument(sel.File, sel.TargetLanguage));
            if (match is not { } m) { Status = "Die Prüfdatei passt zu keiner Datei des Projekts – bitte die Datei in der Liste auswählen und erneut versuchen."; return; }
            ms.Position = 0;
            var result = await Task.Run(() => ReviewDocx.Import(ms, m.Doc, Project, markReviewed: true, markUnchangedReviewed: ReviewMarksUnchanged));
            Project.SaveDocument(m.File, m.Doc);
            Editor.Reload();
            RefreshFiles();
            var name = m.File.OriginalName;
            Status = $"Prüfdatei für „{name}“ ({m.Language}) übernommen: {result.Changed} Segmente geändert, {result.Comments} Anmerkungen, {result.Unchanged} unverändert." +
                     (result.TagWarnings.Count > 0 ? $" Tags prüfen in Segment {string.Join(", ", result.TagWarnings)}." : "") +
                     (result.Problems.Count > 0 ? " " + string.Join(" ", result.Problems.Take(3)) : "");
        });
    }

    // ---------- Stapelaufgaben ----------

    [RelayCommand]
    private async Task Pretranslate()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await RunBusy("Vorübersetzung …", async () =>
        {
            var applied = 0;
            var confirmed = 0;
            await Task.Run(() =>
            {
                foreach (var (f, lang) in Project.Documents())
                {
                    var doc = Project.LoadDocument(f, lang);
                    var (a, c) = Workflow.Pretranslate(Project, doc, confirmExact: false);
                    applied += a;
                    confirmed += c;
                    Project.SaveDocument(f, doc);
                }
            });
            Editor.Reload();
            RefreshFiles();
            Status = $"Vorübersetzung fertig: {applied} Segmente aus dem TM eingesetzt (ab {Project.PretranslateMinScore} %), {confirmed} Kontexttreffer bestätigt.";
        });
    }

    // ---------- Angebot aus der Analyse ----------

    private readonly Dictionary<string, AnalysisResult> _analysis = new();
    public ObservableCollection<WeightRow> WeightRows { get; } = new();
    public IReadOnlyList<string> RateUnitNames { get; } = new[] { "je Wort", "je Zeichen", "je Normzeile (55 Zeichen)" }.Select(L.T).ToList();
    [ObservableProperty] public partial string QuoteText { get; set; } = "";
    public bool HasQuote => _analysis.Count > 0;

    private RateSettings? Rates => Project?.Rates;

    public decimal RatePrice { get => Rates?.Price ?? 0; set { if (Rates is { } r) { r.Price = Math.Max(0, value); RatesChanged(); } } }
    public int RateUnitIndex { get => (int)(Rates?.Unit ?? RateUnit.Word); set { if (Rates is { } r && value >= 0) { r.Unit = (RateUnit)value; RatesChanged(); } } }
    public string RateCurrency { get => Rates?.Currency ?? "EUR"; set { if (Rates is { } r) { r.Currency = value.Trim(); RatesChanged(); } } }
    public decimal RateVat { get => Rates?.VatPercent ?? 0; set { if (Rates is { } r) { r.VatPercent = Math.Max(0, value); RatesChanged(); } } }
    public decimal RateMinimum { get => Rates?.MinimumFee ?? 0; set { if (Rates is { } r) { r.MinimumFee = Math.Max(0, value); RatesChanged(); } } }

    /// <summary>Preise des Projekts anzeigen (nach dem Öffnen); neue Projekte übernehmen die zuletzt verwendeten.</summary>
    private void LoadRates()
    {
        _analysis.Clear();
        QuoteText = "";
        WeightRows.Clear();
        if (Rates is { } r)
            foreach (var band in Enum.GetValues<MatchBand>())
                WeightRows.Add(new WeightRow(band, r.WeightOf(band), () => { r.Weights[band] = WeightRows.First(w => w.Band == band).Weight; RatesChanged(); }));
        OnPropertyChanged(nameof(RatePrice));
        OnPropertyChanged(nameof(RateUnitIndex));
        OnPropertyChanged(nameof(RateCurrency));
        OnPropertyChanged(nameof(RateVat));
        OnPropertyChanged(nameof(RateMinimum));
        OnPropertyChanged(nameof(HasQuote));
    }

    private void RatesChanged()
    {
        if (Project is not { } p) return;
        OnPropertyChanged(nameof(RatePrice));
        OnPropertyChanged(nameof(RateUnitIndex));
        OnPropertyChanged(nameof(RateCurrency));
        OnPropertyChanged(nameof(RateVat));
        OnPropertyChanged(nameof(RateMinimum));
        p.Save();
        AppServices.Ui.LastRates = p.Rates.Clone();
        AppServices.Ui.Save();
        UpdateQuote();
    }

    public List<Quote> Quotes() => Project == null ? [] : _analysis.Select(a => QuoteCalculator.Calculate(a.Value, Project.Rates, a.Key)).ToList();

    private void UpdateQuote()
    {
        OnPropertyChanged(nameof(HasQuote));
        if (Project == null || _analysis.Count == 0) { QuoteText = ""; return; }
        var r = Project.Rates;
        var unit = L.T(QuoteCalculator.UnitName(r.Unit, "de"));
        var lines = new List<string>();
        var net = 0m;
        foreach (var q in Quotes())
        {
            lines.Add(L.T($"{q.Language}: {q.Units:N0} {unit}, gewichtet {q.WeightedUnits:N0} ({q.SavingsPercent:0.#} % durch Treffer gespart) → {q.Net:N2} {r.Currency}") +
                      (q.MinimumApplied ? L.T(" (Mindestbetrag)") : ""));
            net += q.Net;
        }
        var vat = Math.Round(net * r.VatPercent / 100m, 2);
        lines.Add(L.T($"Netto {net:N2} {r.Currency} + MwSt. {vat:N2} = gesamt {net + vat:N2} {r.Currency}"));
        QuoteText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task ExportQuote()
    {
        if (Project == null || _analysis.Count == 0) { Status = "Bitte zuerst „Analyse“ ausführen."; return; }
        var name = string.Concat(Project.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + L.T("_Angebot") + ".docx";
        await using var output = await AppServices.Platform!.SaveFileAsync("Angebot speichern", name);
        if (output == null) return;
        QuoteCalculator.ExportDocx(Project.Name, Project.SourceLanguage, Quotes(), Project.Rates, output, L.DocumentLanguage);
        Status = $"Angebot „{name}“ gespeichert.";
    }

    [RelayCommand]
    private async Task Analyze()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await RunBusy("Analysiere …", async () =>
        {
            AnalysisRows.Clear();
            _analysis.Clear();
            foreach (var lang in Project.TargetLanguages)
            {
                var result = await Task.Run(() => Analyzer.Analyze(Project.AllDocuments(lang), Project.TmsFor(lang)));
                _analysis[lang] = result;
                AnalysisRows.Add(new AnalysisRow($"— {lang} —", result.TotalSegments, result.TotalWords, result.Bands.Values.Sum(b => b.Characters)));
                foreach (var (band, c) in result.Bands.Where(b => b.Value.Segments > 0))
                    AnalysisRows.Add(new AnalysisRow(AnalysisResult.BandName(band), c.Segments, c.Words, c.Characters));
            }
            UpdateQuote();
            Status = "Analyse fertig.";
        });
    }

    [RelayCommand]
    private async Task RunQaAll()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await RunBusy("Qualitätsprüfung …", async () =>
        {
            var total = await Task.Run(() => Project.Documents().Sum(d =>
                QaChecker.Check(Project.LoadDocument(d.File, d.Language), Project.Qa, Project.ProjectTermbase, brief: Project.Brief(d.File, d.Language))
                    .Count(i => i.Severity != QaSeverity.Info)));
            Status = total == 0 ? "QA: keine Fehler oder Warnungen." : $"QA: {total} Fehler/Warnungen. Details im Editor unter „Meldungen“.";
        });
    }
}
