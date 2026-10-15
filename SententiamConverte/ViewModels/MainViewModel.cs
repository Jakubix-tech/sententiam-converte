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

public sealed record AnalysisRow(string Band, int Segments, int Words, int Characters);

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

    public MainViewModel()
    {
        Editor = new EditorViewModel(this);
        Resources = new ResourcesViewModel(this);
        Agents = new AgentsViewModel(this);
        Settings = new SettingsViewModel(this);
        Knowledge = new KnowledgeViewModel(this);
        Core.Projects.TranslationProject.Servers = () => AppServices.Servers;
        foreach (var p in AppServices.Ui.RecentProjects.Where(p => File.Exists(Path.Combine(p, TranslationProject.FileName))))
            RecentProjects.Add(p);
        LanguageCodes = new ObservableCollection<string>(Languages.Common.Select(l => l.Code));
    }

    // ---------- Zustand ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title))]
    public partial TranslationProject? Project { get; set; }

    public bool HasProject => Project != null;
    public string Title => Project == null ? "Sententiam converte" : $"Sententiam converte – {Project.Name}";

    [ObservableProperty] public partial string Status { get; set; } = "Bereit.";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial int SelectedTab { get; set; }

    public ObservableCollection<string> RecentProjects { get; } = new();
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
            var project = await Task.Run(() => TranslationProject.Create(folder, name, NewSource.Trim(), targets, NewTopic.Trim()));
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
        await RunBusy("Öffne Projekt …", async () =>
        {
            var project = await Task.Run(() => TranslationProject.Open(folder));
            SetProject(project);
            Status = $"Projekt „{project.Name}“ geöffnet.";
        });
    }

    private void SetProject(TranslationProject project)
    {
        Editor.Close();
        Project?.Dispose();
        Project = project;
        ProjectTopic = project.Topic;
        PretranslateMinScore = project.PretranslateMinScore;
        ConfirmContextMatches = project.ConfirmContextMatches;
        OnPropertyChanged(nameof(ProfileNames));
        ProjectProfile = project.SourceProfile ?? "(keins)";
        Qa = project.Qa;
        IsPackageProject = project.IsFromPackage;
        ForbiddenWords = string.Join(", ", project.Qa.ForbiddenWords);
        RegexRules.Clear();
        foreach (var r in project.Qa.RegexRules) RegexRules.Add(RegexRuleViewModel.From(r));
        AppServices.Ui.Remember(project.Folder);
        RecentProjects.Remove(project.Folder);
        RecentProjects.Insert(0, project.Folder);
        RefreshFiles();
        Resources.Refresh();
        Agents.OnProjectChanged();
        Knowledge.OnProjectChanged();
        if (project.ResourceErrors.Count > 0)
            Status = "Nicht erreichbar: " + string.Join("; ", project.ResourceErrors);
    }

    partial void OnSelectedTabChanged(int value)
    {
        // Beim Wechsel zurück zum Projekt den Fortschritt aus dem Editor übernehmen
        if (value == 0 && Project != null)
        {
            Editor.SaveIfOpen();
            RefreshFiles();
        }
    }

    partial void OnProjectTopicChanged(string value)
    {
        if (Project == null || Project.Topic == value) return;
        Project.Topic = value;
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
        if (files.Count == 0) return;
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

    [RelayCommand]
    private async Task Analyze()
    {
        if (Project == null) return;
        Editor.SaveIfOpen();
        await RunBusy("Analysiere …", async () =>
        {
            AnalysisRows.Clear();
            foreach (var lang in Project.TargetLanguages)
            {
                var result = await Task.Run(() => Analyzer.Analyze(Project.AllDocuments(lang), Project.TmsFor(lang)));
                AnalysisRows.Add(new AnalysisRow($"— {lang} —", result.TotalSegments, result.TotalWords, result.Bands.Values.Sum(b => b.Characters)));
                foreach (var (band, c) in result.Bands.Where(b => b.Value.Segments > 0))
                    AnalysisRows.Add(new AnalysisRow(AnalysisResult.BandName(band), c.Segments, c.Words, c.Characters));
            }
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
                QaChecker.Check(Project.LoadDocument(d.File, d.Language), Project.Qa, Project.ProjectTermbase)
                    .Count(i => i.Severity != QaSeverity.Info)));
            Status = total == 0 ? "QA: keine Fehler oder Warnungen." : $"QA: {total} Fehler/Warnungen. Details im Editor unter „QA“.";
        });
    }
}
