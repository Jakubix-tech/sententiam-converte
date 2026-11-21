using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public sealed record KnowledgeRow(KnowledgeEntry Entry)
{
    public string Header => $"[{Entry.Language}] {L.T(KnowledgeBase.KindName(Entry.Kind))} · {Entry.Topic}";
    public string Title => Entry.Title;
    public string Content => Entry.Content;
    public string? Quote => string.IsNullOrEmpty(Entry.Quote) ? null : $"„{Entry.Quote}“";
    public string Source => string.Join(" · ", new[] { Entry.SourceTitle, Entry.SourceUrl, Entry.Author, Entry.Modified.ToLocalTime().ToString("g") }
        .Where(x => !string.IsNullOrEmpty(x)));
}

public sealed record ActivityRow(string Server, ActivityEntry Entry)
{
    public string Text => $"{Entry.When.ToLocalTime():g} · {Entry.User} · {L.T(ResourceFactory.KindName(Entry.Kind))} „{Entry.Resource}“ ({Server})";
    public string Detail => Entry.Text;
}

/// <summary>Bereich „Wissen“: Wissensdatenbanken durchsuchen und pflegen, Wissens-Agent, Team-Aktivität.</summary>
public partial class KnowledgeViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public KnowledgeViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<IKnowledgeBase> Bases { get; } = new();
    public ObservableCollection<KnowledgeRow> Entries { get; } = new();
    public ObservableCollection<ActivityRow> Activity { get; } = new();
    public ObservableCollection<LanguageChoice> AgentLanguages { get; } = new();
    public IReadOnlyList<string> KindNames { get; } = Enum.GetValues<KnowledgeKind>().Select(k => L.T(KnowledgeBase.KindName(k))).ToList();

    [ObservableProperty] public partial IKnowledgeBase? SelectedBase { get; set; }
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string LanguageFilter { get; set; } = "";
    [ObservableProperty] public partial KnowledgeRow? SelectedEntry { get; set; }

    // Bearbeiten / Neu
    [ObservableProperty] public partial long EditId { get; set; }
    [ObservableProperty] public partial string EditLanguage { get; set; } = "";
    [ObservableProperty] public partial string EditTopic { get; set; } = "";
    [ObservableProperty] public partial int EditKind { get; set; }
    [ObservableProperty] public partial string EditTitle { get; set; } = "";
    [ObservableProperty] public partial string EditContent { get; set; } = "";
    [ObservableProperty] public partial string EditSource { get; set; } = "";

    /// <summary>Name für eine neue Wissensdatenbank.</summary>
    [ObservableProperty] public partial string NewBaseName { get; set; } = "";

    // Agent
    [ObservableProperty] public partial string AgentTopic { get; set; } = "";
    [ObservableProperty] public partial string AgentExtraLanguages { get; set; } = "";
    [ObservableProperty] public partial string AgentFocus { get; set; } = "";
    [ObservableProperty] public partial int AgentPages { get; set; } = 3;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Log { get; set; } = "";

    /// <summary>Wo Wissensdatenbanken ohne Projekt liegen (auch das allgemeine KI-Gedächtnis).</summary>
    public static string LocalFolder => Path.Combine(Core.Ai.AppPaths.DataRoot, "ressourcen");

    private readonly Dictionary<string, KnowledgeBase> _local = new(StringComparer.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string path) =>
        SamePath(path, AgentsViewModel.GlobalMemoryPath) ? L.T("KI-Gedächtnis (allgemein)") : Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// Zur Wahl stehen die Datenbanken des offenen Projekts und – immer, auch ohne Projekt – das allgemeine
    /// KI-Gedächtnis und alle Wissensdatenbanken im Benutzerordner.
    /// </summary>
    public void OnProjectChanged()
    {
        var selected = SelectedBase?.Location;
        Bases.Clear();
        AgentLanguages.Clear();
        var p = _main.Project;
        if (p != null)
            foreach (var kb in p.KnowledgeBases) Bases.Add(kb);
        Directory.CreateDirectory(LocalFolder);
        var languageBases = Core.Resources.LanguageResources.Load().Where(r => r.Kind == Core.Remote.ResourceKind.Knowledge && !r.IsRemote).Select(r => r.Location);
        var files = Directory.GetFiles(LocalFolder, "*.sckb").Append(AgentsViewModel.GlobalMemoryPath).Concat(languageBases)
            .Where(File.Exists)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => !SamePath(f, AgentsViewModel.GlobalMemoryPath)).ToList();
        foreach (var f in files)
        {
            if (Bases.Any(b => !b.IsRemote && SamePath(b.Location, f))) continue;
            try
            {
                if (!_local.TryGetValue(f, out var kb)) _local[f] = kb = new KnowledgeBase(f, DisplayName(f));
                Bases.Add(kb);
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }   // kaputte Datei überspringen
        }
        SelectedBase = Bases.FirstOrDefault(b => selected != null && b.Location == selected)
                       ?? (p != null ? Bases.FirstOrDefault(b => b.IsRemote) ?? Bases.FirstOrDefault() : Bases.FirstOrDefault());
        RefreshStudyFiles();
        if (p == null)
        {
            foreach (var l in new[] { "de-DE", "en-GB" }) AgentLanguages.Add(new LanguageChoice { Code = l, IsChecked = true });
            return;
        }
        AgentTopic = p.Topic;
        SuggestStudyLanguage();
        SuggestTopicTmLanguages();
        foreach (var l in new[] { p.SourceLanguage }.Concat(p.TargetLanguages).Distinct())
            AgentLanguages.Add(new LanguageChoice { Code = l, IsChecked = true });
        EditLanguage = p.SourceLanguage;
        EditTopic = p.Topic;
    }

    /// <summary>Das Gedächtnis der Agenten auswählen: Projekt-Wissen bzw. ohne Projekt das allgemeine KI-Gedächtnis.</summary>
    public void ShowMemory()
    {
        OnProjectChanged();
        SelectedBase = _main.Project != null
            ? Bases.FirstOrDefault()
            : Bases.FirstOrDefault(b => !b.IsRemote && SamePath(b.Location, AgentsViewModel.GlobalMemoryPath)) ?? SelectedBase;
        Filter = "";
        Load();
    }

    /// <summary>Neue Wissensdatenbank im Benutzerordner; bei offenem Projekt gleich angebunden (schreibend).</summary>
    [RelayCommand]
    private void NewBase()
    {
        var name = NewBaseName.Trim();
        if (name.Length == 0) { _main.Status = "Bitte einen Namen für die neue Datenbank angeben."; return; }
        var file = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.GetFullPath(Path.Combine(LocalFolder, file + ".sckb"));
        var existed = File.Exists(path);
        Directory.CreateDirectory(LocalFolder);
        if (!_local.ContainsKey(path)) _local[path] = new KnowledgeBase(path, name);
        var p = _main.Project;
        if (p != null && !p.KnowledgeBases.Any(b => !b.IsRemote && SamePath(b.Location, path)))
        {
            p.Attach(new ResourceRef { Kind = Core.Remote.ResourceKind.Knowledge, Location = path, WriteBack = true });
            _main.Resources.Refresh();
        }
        OnProjectChanged();
        SelectedBase = Bases.FirstOrDefault(b => !b.IsRemote && SamePath(b.Location, path)) ?? SelectedBase;
        NewBaseName = "";
        _main.Status = existed ? $"Die Wissensdatenbank „{name}“ gibt es schon – sie ist jetzt ausgewählt."
            : p != null ? $"Neue Wissensdatenbank „{name}“ angelegt und ans Projekt angebunden."
            : $"Neue Wissensdatenbank „{name}“ angelegt.";
    }

    partial void OnSelectedBaseChanged(IKnowledgeBase? value) => Load();
    partial void OnFilterChanged(string value) => Load();
    partial void OnLanguageFilterChanged(string value) => Load();

    partial void OnSelectedEntryChanged(KnowledgeRow? value)
    {
        if (value == null) return;
        var e = value.Entry;
        EditId = e.Id;
        EditLanguage = e.Language;
        EditTopic = e.Topic;
        EditKind = (int)e.Kind;
        EditTitle = e.Title;
        EditContent = e.Content;
        EditSource = e.SourceUrl ?? "";
    }

    [RelayCommand]
    private void Load()
    {
        Entries.Clear();
        if (SelectedBase == null) return;
        try
        {
            var list = string.IsNullOrWhiteSpace(Filter)
                ? SelectedBase.Browse(LanguageFilter.Trim().Length == 0 ? null : LanguageFilter.Trim(), null, 0, 200)
                : SelectedBase.Search(LanguageFilter.Trim().Length == 0 ? null : LanguageFilter.Trim(), Filter, 50);
            foreach (var e in list) Entries.Add(new KnowledgeRow(e));
        }
        catch (RemoteException ex) { _main.Status = ex.Message; }
    }

    [RelayCommand]
    private void NewEntry()
    {
        SelectedEntry = null;
        EditId = 0;
        EditTitle = EditContent = EditSource = "";
        EditKind = (int)KnowledgeKind.Fact;
    }

    [RelayCommand]
    private void SaveEntry()
    {
        if (SelectedBase == null || EditTitle.Trim().Length == 0) { _main.Status = "Bitte Titel angeben."; return; }
        var entry = new KnowledgeEntry
        {
            Id = EditId,
            Language = EditLanguage.Trim(),
            Topic = EditTopic.Trim(),
            Kind = (KnowledgeKind)EditKind,
            Title = EditTitle.Trim(),
            Content = EditContent.Trim(),
            SourceUrl = EditSource.Trim().Length > 0 ? EditSource.Trim() : null,
            Author = SelectedEntry?.Entry.Author,
            Quote = SelectedEntry?.Entry.Quote,
            SourceTitle = SelectedEntry?.Entry.SourceTitle,
            Created = SelectedEntry?.Entry.Created ?? DateTime.UtcNow,
        };
        try
        {
            if (EditId == 0) EditId = SelectedBase.Add(entry);
            else SelectedBase.Update(entry);
            _main.Status = "Wissenseintrag gespeichert.";
            Load();
        }
        catch (RemoteException ex) { _main.Status = ex.Message; }
    }

    [RelayCommand]
    private void DeleteEntry(KnowledgeRow? row)
    {
        row ??= SelectedEntry;
        if (row == null || SelectedBase == null) return;
        try
        {
            SelectedBase.Delete(row.Entry.Id);
            Entries.Remove(row);
        }
        catch (RemoteException ex) { _main.Status = ex.Message; }
    }

    // ---------- Wissens-Agent ----------

    [RelayCommand]
    private async Task RunAgent()
    {
        if (IsRunning || SelectedBase == null) return;
        var langs = AgentLanguages.Where(l => l.IsChecked).Select(l => l.Code)
            .Concat(AgentExtraLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (AgentTopic.Trim().Length == 0 || langs.Count == 0) { Log = "Bitte Thema und mindestens eine Sprache angeben.\n"; return; }
        IsRunning = true;
        Log = "";
        _cts = new CancellationTokenSource();
        try
        {
            var ctx = AgentContext.From(AppServices.Ai, AppServices.Ai.ResearchProviderId, new Progress<string>(m => Log += m + "\n"));
            // eigene Verbindung: der Agent schreibt weiter, auch wenn man inzwischen ein anderes Projekt öffnet
            using var own = SelectedBase.IsRemote ? null : new KnowledgeBase(SelectedBase.Location, SelectedBase.Name);
            IKnowledgeBase target = own ?? SelectedBase;
            _main.Status = "Wissens-Agent arbeitet – läuft weiter, auch wenn Sie den Reiter oder das Projekt wechseln.";
            var result = await new KnowledgeAgent(ctx).RunAsync(AgentTopic.Trim(), langs, target,
                AgentFocus.Trim().Length > 0 ? AgentFocus.Trim() : null, Math.Clamp(AgentPages, 1, 8), Environment.UserName, _cts.Token,
                _main.CurrentProfile);
            _main.Status = $"Wissens-Agent: {result.Added} neue Einträge in „{target.Name}“."
                           + (result.Coverage is { Count: > 0 } c ? $" Abgedeckt: {c.Count(x => x.Entries > 0)} von {c.Count} Unterthemen und Sprachen." : "");
            Load();
        }
        catch (OperationCanceledException) { Log += "Abgebrochen.\n"; }
        catch (Exception ex) { Log += "Fehler: " + ex.Message + "\n"; }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ---------- Team-Aktivität ----------

    [RelayCommand]
    private async Task LoadActivity()
    {
        Activity.Clear();
        var servers = AppServices.Servers;
        if (servers.Servers.Count == 0)
        {
            ActivityInfo = "Kein Teamserver eingerichtet – Team-Aktivität gibt es nur bei der Arbeit mit einem Teamserver (Einstellungen → Teamserver). Die Agenten laufen davon unabhängig weiter.";
            return;
        }
        ActivityInfo = "Frage die Teamserver …";
        var errors = new List<string>();
        foreach (var account in servers.Servers)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                foreach (var a in await ResourceFactory.Api(account).ActivityAsync(100, timeout.Token))
                    Activity.Add(new ActivityRow(account.Name, a));
            }
            catch (Exception ex) when (ex is RemoteException or OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                errors.Add($"{account.Name}: {(ex is OperationCanceledException ? "keine Antwort in 20 s" : ex.Message)}");
            }
        }
        var sorted = Activity.OrderByDescending(a => a.Entry.When).ToList();
        Activity.Clear();
        foreach (var a in sorted) Activity.Add(a);
        var unreachable = string.Join("; ", errors);
        ActivityInfo = (sorted.Count == 0 ? "Keine Aktivität." : $"{sorted.Count} Einträge.") + (errors.Count > 0 ? $"\nNicht erreichbar: {unreachable}" : "");
    }

    [ObservableProperty] public partial string ActivityInfo { get; set; } = "";
}
