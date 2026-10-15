using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    public string Header => $"[{Entry.Language}] {KnowledgeBase.KindName(Entry.Kind)} · {Entry.Topic}";
    public string Title => Entry.Title;
    public string Content => Entry.Content;
    public string? Quote => string.IsNullOrEmpty(Entry.Quote) ? null : $"„{Entry.Quote}“";
    public string Source => string.Join(" · ", new[] { Entry.SourceTitle, Entry.SourceUrl, Entry.Author, Entry.Modified.ToLocalTime().ToString("g") }
        .Where(x => !string.IsNullOrEmpty(x)));
}

public sealed record ActivityRow(string Server, ActivityEntry Entry)
{
    public string Text => $"{Entry.When.ToLocalTime():g} · {Entry.User} · {ResourceFactory.KindName(Entry.Kind)} „{Entry.Resource}“ ({Server})";
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
    public IReadOnlyList<string> KindNames { get; } = Enum.GetValues<KnowledgeKind>().Select(KnowledgeBase.KindName).ToList();

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

    // Agent
    [ObservableProperty] public partial string AgentTopic { get; set; } = "";
    [ObservableProperty] public partial string AgentFocus { get; set; } = "";
    [ObservableProperty] public partial int AgentPages { get; set; } = 3;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Log { get; set; } = "";

    public void OnProjectChanged()
    {
        Bases.Clear();
        AgentLanguages.Clear();
        var p = _main.Project;
        if (p == null) return;
        foreach (var kb in p.KnowledgeBases) Bases.Add(kb);
        SelectedBase = Bases.FirstOrDefault(b => b.IsRemote) ?? Bases.FirstOrDefault();
        AgentTopic = p.Topic;
        foreach (var l in new[] { p.SourceLanguage }.Concat(p.TargetLanguages).Distinct())
            AgentLanguages.Add(new LanguageChoice { Code = l, IsChecked = true });
        EditLanguage = p.SourceLanguage;
        EditTopic = p.Topic;
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
        var langs = AgentLanguages.Where(l => l.IsChecked).Select(l => l.Code).ToList();
        if (AgentTopic.Trim().Length == 0 || langs.Count == 0) { Log = "Bitte Thema und mindestens eine Sprache angeben.\n"; return; }
        IsRunning = true;
        Log = "";
        _cts = new CancellationTokenSource();
        try
        {
            var ctx = AgentContext.From(AppServices.Ai, AppServices.Ai.ResearchProviderId, new Progress<string>(m => Log += m + "\n"));
            var target = SelectedBase;
            var result = await new KnowledgeAgent(ctx).RunAsync(AgentTopic.Trim(), langs, target,
                AgentFocus.Trim().Length > 0 ? AgentFocus.Trim() : null, Math.Clamp(AgentPages, 1, 8), Environment.UserName, _cts.Token);
            _main.Status = $"Wissens-Agent: {result.Added} neue Einträge in „{target.Name}“.";
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
        if (servers.Servers.Count == 0) { _main.Status = "Kein Teamserver eingerichtet (Einstellungen → Teamserver)."; return; }
        foreach (var account in servers.Servers)
        {
            try
            {
                foreach (var a in await ResourceFactory.Api(account).ActivityAsync(100))
                    Activity.Add(new ActivityRow(account.Name, a));
            }
            catch (RemoteException ex) { _main.Status = ex.Message; }
        }
        var sorted = Activity.OrderByDescending(a => a.Entry.When).ToList();
        Activity.Clear();
        foreach (var a in sorted) Activity.Add(a);
    }
}
