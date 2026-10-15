using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public sealed record MatchRow(int Number, TmMatch Match)
{
    public string Score => Match.Score >= 101 ? "101 % Kontext" : $"{Match.Score} %";
    public string Source => Match.Unit.Source;
    public string Target => Match.Unit.Target;
    public string Info => string.Join(" · ", new[]
    {
        Match.TmName,
        Match.Unit.Modified.ToLocalTime().ToString("d"),
        Match.Unit.Origin,
        Match.ContextMatch switch
        {
            TmContextMatch.Previous => "gleicher vorheriger Satz",
            TmContextMatch.PreviousAndNext => "gleiche Nachbarsätze",
            TmContextMatch.Id => "gleiche ID",
            _ => null,
        },
        Match.Ambiguous ? "mehrere Übersetzungen – Kontext prüfen" : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
    public string? ContextText => Match.Unit.ContextPrevious is { Length: > 0 } p ? $"zuvor: {p}" : null;
}

public sealed record TermRow(TermHit Hit)
{
    public string Source => Hit.SourceTerm.Text;
    public string Targets => Hit.Translations.Count == 0
        ? "(keine Übersetzung)"
        : string.Join("; ", Hit.Translations.Select(t => t.Status == TermStatus.Forbidden ? $"✗ {t.Text}" : t.Text));
    public string? Definition => Hit.Concept.Definition;
}

public sealed record ConcordanceRow(string Source, string Target, string Info);

public sealed record QaRow(QaIssue Issue)
{
    public int SegmentId => Issue.SegmentId;
    public string Severity => Issue.Severity switch { QaSeverity.Error => "Fehler", QaSeverity.Warning => "Warnung", _ => "Hinweis" };
    public string Check => Issue.Check;
    public string Message => Issue.Message;
}

public sealed record FragmentRow(FragmentMatch Fragment)
{
    public string Phrase => Fragment.Phrase;
    public string Target => TagText.StripTags(Fragment.Unit.Target);
    public string Info => $"{Fragment.TmName} · ganzer TM-Eintrag";
}

public sealed record SuggestionItem(string Word, string Replacement);

public sealed record SpellRow(string Word, IReadOnlyList<SuggestionItem> Suggestions);

public sealed record HistoryRow(string Header, string Text, string Diff);

public enum SegmentFilter { All, Open, Unconfirmed, WithIssues }

public partial class EditorViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private BilingualDocument? _doc;
    private List<SegmentViewModel> _all = new();
    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _batchCts;

    public EditorViewModel(MainViewModel main) => _main = main;

    public ProjectFile? CurrentFile { get; private set; }
    public BilingualDocument? CurrentDocument => _doc;
    private TranslationProject? Project => _main.Project;

    public ObservableCollection<SegmentViewModel> Segments { get; } = new();
    public ObservableCollection<MatchRow> Matches { get; } = new();
    public ObservableCollection<TermRow> Terms { get; } = new();
    public ObservableCollection<ConcordanceRow> Concordance { get; } = new();
    public ObservableCollection<QaRow> QaIssues { get; } = new();
    public ObservableCollection<FragmentRow> Fragments { get; } = new();
    public ObservableCollection<SpellRow> SpellingErrors { get; } = new();
    public ObservableCollection<HistoryRow> History { get; } = new();
    public ObservableCollection<KnowledgeRow> Knowledge { get; } = new();
    private List<string> _suggestions = new();

    public IReadOnlyList<string> FilterNames { get; } = new[] { "Alle Segmente", "Nicht übersetzt", "Nicht bestätigt", "Mit QA-Meldungen" }.Select(L.T).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    public partial string DocumentTitle { get; set; } = "";

    public bool HasDocument => _doc != null;

    [ObservableProperty] public partial SegmentViewModel? Selected { get; set; }
    [ObservableProperty] public partial int FilterIndex { get; set; }
    [ObservableProperty] public partial string ConcordanceQuery { get; set; } = "";
    [ObservableProperty] public partial bool ConcordanceInTarget { get; set; }
    [ObservableProperty] public partial string FindText { get; set; } = "";
    [ObservableProperty] public partial string ReplaceText { get; set; } = "";
    [ObservableProperty] public partial string Progress { get; set; } = "";
    [ObservableProperty] public partial string AiResult { get; set; } = "";
    [ObservableProperty] public partial string AiLog { get; set; } = "";
    [ObservableProperty] public partial bool AiCheckUsage { get; set; } = true;
    [ObservableProperty] public partial bool IsAiRunning { get; set; }
    [ObservableProperty] public partial int SidePanelTab { get; set; }
    [ObservableProperty] public partial bool CanResegment { get; set; }

    /// <summary>Kommentar zum gewählten Segment.</summary>
    public string Comment
    {
        get => Selected?.Model.Comment ?? "";
        set
        {
            if (Selected == null) return;
            Selected.Model.Comment = value.Length == 0 ? null : value;
            Selected.Refresh();
            OnPropertyChanged();
        }
    }

    /// <summary>Die Ansicht setzt hierüber den Fokus ins Zielfeld eines Segments.</summary>
    public event Action<SegmentViewModel>? FocusRequested;

    // ---------- Öffnen / Speichern ----------

    public async Task OpenAsync(ProjectFile file, string targetLanguage)
    {
        SaveIfOpen();
        var project = Project!;
        var doc = await Task.Run(() => project.LoadDocument(file, targetLanguage));
        _doc = doc;
        CurrentFile = file;
        DocumentTitle = $"{file.OriginalName} · {doc.SourceLanguage} → {doc.TargetLanguage}";
        CanResegment = Workflow.CanResegment(doc) && !project.IsShared;
        StartSync(project, file, doc);
        _all = doc.Paragraphs.SelectMany(p => p.Segments.Select(s => new SegmentViewModel(s, p.Context))).ToList();
        ApplyFilter();
        UpdateProgress();
        Selected = Segments.FirstOrDefault(s => !s.Model.IsConfirmed && !s.IsLocked) ?? Segments.FirstOrDefault();
    }

    public void Reload()
    {
        if (CurrentFile != null && _doc != null && Project != null)
        {
            var file = CurrentFile;
            var lang = _doc.TargetLanguage;
            _doc = null;
            _ = OpenAsync(file, lang);
        }
    }

    public void Close()
    {
        StopSync();
        SaveIfOpen();
        _doc = null;
        CurrentFile = null;
        _all.Clear();
        Segments.Clear();
        Matches.Clear();
        Terms.Clear();
        QaIssues.Clear();
        Fragments.Clear();
        SpellingErrors.Clear();
        History.Clear();
        DocumentTitle = "";
    }

    public void SaveIfOpen()
    {
        if (_doc != null && CurrentFile != null && Project != null)
            Project.SaveDocument(CurrentFile, _doc);
    }

    [RelayCommand]
    private void Save()
    {
        SaveIfOpen();
        _main.RefreshFiles();
        _main.Status = "Gespeichert.";
    }

    private void UpdateProgress()
    {
        if (_doc == null) { Progress = ""; return; }
        var segs = _doc.Segments.Where(s => !s.Locked).ToList();
        var done = segs.Count(s => s.IsConfirmed);
        var draft = segs.Count(s => s.Status == SegmentStatus.Draft);
        Progress = $"{done}/{segs.Count} bestätigt · {draft} Entwurf";
    }

    partial void OnFilterIndexChanged(int value) => ApplyFilter();

    private void ApplyFilter()
    {
        var withIssues = QaIssues.Select(q => q.SegmentId).ToHashSet();
        var filter = (SegmentFilter)FilterIndex;
        Segments.Clear();
        foreach (var s in _all)
        {
            var show = filter switch
            {
                SegmentFilter.Open => !s.IsLocked && s.Target.Length == 0,
                SegmentFilter.Unconfirmed => !s.IsLocked && !s.Model.IsConfirmed,
                SegmentFilter.WithIssues => withIssues.Contains(s.Id),
                _ => true,
            };
            if (show) Segments.Add(s);
        }
    }

    // ---------- Gemeinsames Projekt: Abgleich und Sperren ----------

    private Avalonia.Threading.DispatcherTimer? _syncTimer;
    private bool _syncing;
    private int? _myLock;

    [ObservableProperty] public partial string SyncInfo { get; set; } = "";

    private void StartSync(TranslationProject project, ProjectFile file, BilingualDocument doc)
    {
        StopSync();
        if (!project.IsShared) { SyncInfo = ""; return; }
        SyncInfo = "gemeinsames Projekt – wird abgeglichen";
        _syncTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _syncTimer.Tick += async (_, _) => await SyncNowAsync(project, file, doc);
        _syncTimer.Start();
    }

    private void StopSync()
    {
        _syncTimer?.Stop();
        _syncTimer = null;
        if (_myLock is { } id && Project is { IsShared: true } p && CurrentFile is { } f && _doc is { } d)
            _ = Task.Run(() => p.UnlockSegment(f, d.TargetLanguage, id));
        _myLock = null;
    }

    /// <summary>Eigene Änderungen senden, die der anderen übernehmen, Sperren anzeigen.</summary>
    public async Task SyncNowAsync(TranslationProject project, ProjectFile file, BilingualDocument doc)
    {
        if (_syncing || !ReferenceEquals(doc, _doc)) return;
        _syncing = true;
        try
        {
            var result = await Task.Run(() => project.Sync(file, doc));
            if (!ReferenceEquals(doc, _doc)) return;
            if (result.Reloaded)
            {
                await OpenAsync(file, doc.TargetLanguage);
                _main.Status = "Gemeinsames Dokument neu geladen (Server wurde neu gestartet).";
                return;
            }
            foreach (var id in result.ChangedSegments)
                _all.FirstOrDefault(s => s.Id == id)?.Refresh();
            var locks = result.Locks.ToDictionary(l => l.SegmentId, l => l.User);
            foreach (var s in _all)
                s.LockedBy = locks.TryGetValue(s.Id, out var who) && s.Id != _myLock ? who : null;
            if (result.ChangedSegments.Count > 0) UpdateProgress();
            if (result.Conflicts.Count > 0) _main.Status = string.Join(" ", result.Conflicts);
            SyncInfo = $"gemeinsam · abgeglichen {DateTime.Now:T}" + (locks.Keys.Count(k => k != _myLock) is var others && others > 0 ? $" · {others} in Bearbeitung" : "");
        }
        catch (Exception ex) { SyncInfo = "Abgleich fehlgeschlagen: " + ex.Message; }
        finally { _syncing = false; }
    }

    private void LockForEditing(SegmentViewModel seg)
    {
        if (Project is not { IsShared: true } p || CurrentFile is not { } f || _doc is not { } d) return;
        var previous = _myLock;
        _myLock = seg.Id;
        _ = Task.Run(() =>
        {
            if (previous is { } old && old != seg.Id) p.UnlockSegment(f, d.TargetLanguage, old);
            return p.LockSegment(f, d.TargetLanguage, seg.Id);
        }).ContinueWith(t =>
        {
            if (t.Result) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _main.Status = $"Segment {seg.Id} wird gerade von jemand anderem bearbeitet.");
        });
    }

    // ---------- Auswahl: TM, Terminologie ----------

    partial void OnSelectedChanged(SegmentViewModel? value)
    {
        _lookupCts?.Cancel();
        Matches.Clear();
        Terms.Clear();
        Fragments.Clear();
        SpellingErrors.Clear();
        Knowledge.Clear();
        _suggestions = new();
        AiResult = "";
        OnPropertyChanged(nameof(Comment));
        FillHistory(value);
        if (value == null || _doc == null || Project == null) return;
        LockForEditing(value);
        var cts = _lookupCts = new CancellationTokenSource();
        var project = Project;
        var doc = _doc;
        _ = LookupAsync(value, project, doc, cts.Token);
    }

    private async Task LookupAsync(SegmentViewModel seg, TranslationProject project, BilingualDocument doc, CancellationToken ct)
    {
        try
        {
            var (matches, terms, fragments, suggestions, knowledge) = await Task.Run(() =>
            {
                var m = Workflow.Lookup(project, doc, seg.Model);
                var t = Workflow.Terms(project, doc, seg.Model);
                var f = Workflow.Fragments(project, doc, seg.Model);
                var sg = Workflow.Suggestions(project, doc, seg.Model, m, f);
                var k = Workflow.Knowledge(project, doc, seg.Model);
                return (m, t, f, sg, k);
            }, ct);
            if (ct.IsCancellationRequested) return;
            var n = 1;
            foreach (var m in matches) Matches.Add(new MatchRow(n++, m));
            foreach (var t in terms) Terms.Add(new TermRow(t));
            foreach (var f in fragments) Fragments.Add(new FragmentRow(f));
            foreach (var k in knowledge) Knowledge.Add(new KnowledgeRow(k));
            _suggestions = suggestions;

            // Leeres Segment: besten Treffer ab Schwelle automatisch einsetzen (automatisches Einsetzen)
            if (seg.Target.Length == 0 && matches.Count > 0 && matches[0].Score >= project.LookupMinScore && !seg.IsLocked)
            {
                Workflow.ApplyMatch(seg.Model, matches[0], doc.TargetLanguage);
                seg.Refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _main.Status = "TM-/Terminologiesuche: " + ex.Message; }
    }

    [RelayCommand]
    private void ApplyMatch(int number)
    {
        if (Selected == null) return;
        var row = Matches.FirstOrDefault(m => m.Number == number);
        if (row == null) return;
        Workflow.ApplyMatch(Selected.Model, row.Match, _doc?.TargetLanguage ?? "");
        Selected.Refresh();
    }

    [RelayCommand]
    private void InsertFragment(FragmentRow? row)
    {
        if (Selected == null || row == null) return;
        Selected.Target = Selected.Target.Length == 0 ? row.Target : Selected.Target.TrimEnd() + " " + row.Target;
    }

    /// <summary>AutoSuggest: passende Vorschläge zum gerade getippten Wortanfang.</summary>
    public IReadOnlyList<string> SuggestionsFor(string prefix)
    {
        if (prefix.Length < 2) return [];
        return _suggestions
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !s.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Length).Take(8).ToList();
    }

    // ---------- Teilen, Zusammenführen ----------

    /// <summary>Teilt das gewählte Segment an der Cursorposition im Quelltext.</summary>
    public void SplitAt(int sourceIndex)
    {
        if (Selected == null || _doc == null) return;
        var second = Workflow.Split(_doc, Selected.Model, sourceIndex);
        if (second == null) { _main.Status = "Hier kann nicht geteilt werden (z. B. innerhalb einer Formatierung)."; return; }
        RebuildRows(Selected.Model);
        _main.Status = "Segment geteilt.";
    }

    [RelayCommand]
    private void MergeWithNext()
    {
        if (Selected == null || _doc == null) return;
        if (!Workflow.MergeWithNext(_doc, Selected.Model))
        {
            _main.Status = "Zusammenführen geht nur mit dem folgenden Segment desselben Absatzes.";
            return;
        }
        RebuildRows(Selected.Model);
        _main.Status = "Segmente zusammengeführt.";
    }

    private void RebuildRows(Segment keep)
    {
        if (_doc == null) return;
        _all = _doc.Paragraphs.SelectMany(p => p.Segments.Select(s => new SegmentViewModel(s, p.Context))).ToList();
        ApplyFilter();
        UpdateProgress();
        SaveIfOpen();
        Selected = Segments.FirstOrDefault(s => s.Model == keep);
        if (Selected != null) FocusRequested?.Invoke(Selected);
    }

    // ---------- Verlauf ----------

    private void FillHistory(SegmentViewModel? seg)
    {
        History.Clear();
        if (seg == null) return;
        string? previous = null;
        foreach (var r in seg.Model.History)
        {
            var header = $"{r.When.ToLocalTime():g} · {StatusName(r.Status)} · {r.Origin}";
            History.Add(new HistoryRow(header, r.Target, previous == null ? "" : TextDiff.Words(previous, r.Target)));
            previous = r.Target;
        }
        if (previous != null && previous != seg.Target && seg.Target.Length > 0)
            History.Add(new HistoryRow("jetzt (unbestätigt)", seg.Target, TextDiff.Words(previous, seg.Target)));
    }

    private static string StatusName(SegmentStatus s) => s switch
    {
        SegmentStatus.Translated => "übersetzt",
        SegmentStatus.Reviewed => "lektoriert",
        SegmentStatus.Signed => "freigegeben",
        SegmentStatus.TranslationRejected => "abgelehnt",
        _ => s.ToString(),
    };

    // ---------- Rechtschreibung ----------

    [RelayCommand]
    private void CheckSpelling()
    {
        SpellingErrors.Clear();
        if (Selected == null || _doc == null) return;
        if (!AppServices.Spell.IsAvailable(_doc.TargetLanguage))
        {
            _main.Status = $"Kein Wörterbuch für {_doc.TargetLanguage}. Unter Einstellungen → Rechtschreibung laden.";
            return;
        }
        foreach (var e in AppServices.Spell.Check(Selected.Target, _doc.TargetLanguage))
            SpellingErrors.Add(new SpellRow(e.Word, e.Suggestions.Select(x => new SuggestionItem(e.Word, x)).ToList()));
        _main.Status = SpellingErrors.Count == 0 ? "Keine Rechtschreibfehler gefunden." : $"{SpellingErrors.Count} unbekannte Wörter.";
    }

    [RelayCommand]
    private void ApplySpelling(SuggestionItem? item)
    {
        if (Selected == null || item == null) return;
        Selected.Target = System.Text.RegularExpressions.Regex.Replace(Selected.Target,
            $@"(?<!\p{{L}}){System.Text.RegularExpressions.Regex.Escape(item.Word)}(?!\p{{L}})", item.Replacement);
        CheckSpelling();
    }

    [RelayCommand]
    private void AddToDictionary(SpellRow? row)
    {
        if (row == null || _doc == null) return;
        AppServices.Spell.AddToUserDictionary(_doc.TargetLanguage, row.Word);
        CheckSpelling();
    }

    // ---------- Vorschau ----------

    [RelayCommand]
    private async Task Preview()
    {
        if (_doc == null || CurrentFile == null || Project == null) return;
        SaveIfOpen();
        var name = Project.TargetFileName(CurrentFile, _doc.TargetLanguage);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SententiamConverte-Vorschau", Guid.NewGuid().ToString("N")[..6] + "_" + name);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            var project = Project;
            var file = CurrentFile;
            var doc = _doc;
            await Task.Run(() =>
            {
                using var fs = System.IO.File.Create(path);
                project.ExportTarget(file, doc, fs);
            });
            if (!await AppServices.Platform!.OpenFileAsync(path))
                _main.Status = $"Vorschau gespeichert unter {path} (kein Programm zum Öffnen gefunden).";
            else _main.Status = "Vorschau geöffnet.";
        }
        catch (Exception ex) { _main.Status = "Vorschau: " + ex.Message; }
    }

    [RelayCommand]
    private void InsertTerm(TermRow? row)
    {
        if (Selected == null || row == null) return;
        var term = row.Hit.Translations.FirstOrDefault(t => t.Status != TermStatus.Forbidden);
        if (term == null) return;
        Selected.Target = Selected.Target.Length == 0 ? term.Text : Selected.Target.TrimEnd() + " " + term.Text;
    }

    // ---------- Bearbeiten ----------

    [RelayCommand]
    private void ConfirmAndNext()
    {
        if (Selected == null || _doc == null || Project == null) return;
        var current = Selected;
        if (current.Target.Trim().Length == 0) { _main.Status = "Leeres Segment kann nicht bestätigt werden."; return; }
        var propagated = Workflow.Confirm(Project, _doc, current.Model);
        current.Refresh();
        if (propagated > 0)
        {
            foreach (var s in _all.Where(s => s.Source == current.Source)) s.Refresh();
            _main.Status = $"Bestätigt; in {propagated} gleiche Segmente übernommen.";
        }
        else _main.Status = $"Segment {current.Id} bestätigt und ins TM übernommen.";
        UpdateProgress();
        SaveIfOpen();
        MoveNext(onlyOpen: true);
    }

    [RelayCommand]
    private void MoveNext(bool onlyOpen = false)
    {
        if (Selected == null) return;
        var idx = Segments.IndexOf(Selected);
        var next = Segments.Skip(idx + 1).FirstOrDefault(s => !s.IsLocked && (!onlyOpen || !s.Model.IsConfirmed));
        if (next != null) { Selected = next; FocusRequested?.Invoke(next); }
    }

    [RelayCommand]
    private void MovePrevious()
    {
        if (Selected == null) return;
        var idx = Segments.IndexOf(Selected);
        var prev = Segments.Take(idx).LastOrDefault(s => !s.IsLocked);
        if (prev != null) { Selected = prev; FocusRequested?.Invoke(prev); }
    }

    [RelayCommand]
    private void CopySource()
    {
        if (Selected == null) return;
        Workflow.CopySourceToTarget(Selected.Model);
        Selected.Refresh();
    }

    [RelayCommand]
    private void ClearTarget()
    {
        if (Selected == null) return;
        Selected.Target = "";
    }

    [RelayCommand]
    private void SetReviewed()
    {
        if (Selected == null || _doc == null || Project == null) return;
        Workflow.Confirm(Project, _doc, Selected.Model, SegmentStatus.Reviewed);
        Selected.Refresh();
        UpdateProgress();
        MoveNext(onlyOpen: false);
    }

    [RelayCommand]
    private void Reject()
    {
        if (Selected == null) return;
        Selected.Model.Status = SegmentStatus.TranslationRejected;
        Selected.Refresh();
        UpdateProgress();
    }

    [RelayCommand]
    private void ReplaceAll()
    {
        if (_doc == null || FindText.Length == 0) return;
        var n = Workflow.ReplaceInTargets(_doc, FindText, ReplaceText, matchCase: false);
        foreach (var s in _all) s.Refresh();
        UpdateProgress();
        _main.Status = $"{n} Segmente geändert.";
    }

    // ---------- Konkordanz, QA ----------

    [RelayCommand]
    private async Task SearchConcordance()
    {
        Concordance.Clear();
        if (Project == null || _doc == null || ConcordanceQuery.Trim().Length == 0) return;
        var project = Project;
        var doc = _doc;
        var q = ConcordanceQuery;
        var inTarget = ConcordanceInTarget;
        var units = await Task.Run(() => project.TmsFor(doc.TargetLanguage)
            .SelectMany(tm => tm.Concordance(doc.SourceLanguage, doc.TargetLanguage, q, inTarget, 30).Select(u => (tm.Name, u))).ToList());
        foreach (var (tm, u) in units) Concordance.Add(new ConcordanceRow(u.Source, u.Target, tm));
        if (units.Count == 0) Concordance.Add(new ConcordanceRow("(nichts gefunden)", "", ""));
        SidePanelTab = 2;
    }

    /// <summary>Markierten Text in der Konkordanz suchen (F3).</summary>
    public void ConcordanceFor(string text)
    {
        ConcordanceQuery = text;
        _ = SearchConcordance();
    }

    [RelayCommand]
    private void RunQa()
    {
        if (_doc == null || Project == null) return;
        QaIssues.Clear();
        var issues = QaChecker.Check(_doc, Project.Qa, Project.ProjectTermbase, AppServices.Spell);
        foreach (var i in issues.OrderBy(i => i.SegmentId)) QaIssues.Add(new QaRow(i));
        foreach (var s in _all) s.IssueCount = issues.Count(i => i.SegmentId == s.Id);
        _main.Status = $"QA: {issues.Count(i => i.Severity == QaSeverity.Error)} Fehler, {issues.Count(i => i.Severity == QaSeverity.Warning)} Warnungen.";
        SidePanelTab = 3;
    }

    [RelayCommand]
    private void GoToSegment(QaRow? row)
    {
        if (row == null) return;
        var seg = Segments.FirstOrDefault(s => s.Id == row.SegmentId) ?? _all.FirstOrDefault(s => s.Id == row.SegmentId);
        if (seg == null) return;
        if (!Segments.Contains(seg)) { FilterIndex = 0; }
        Selected = seg;
        FocusRequested?.Invoke(seg);
    }

    // ---------- KI ----------

    private AgentContext CreateContext(string? providerId) =>
        AgentContext.From(AppServices.Ai, providerId, new Progress<string>(m => AiLog += m + "\n"));

    private (string? Prev, string? Next) Neighbours(SegmentViewModel seg)
    {
        var i = _all.IndexOf(seg);
        return (i > 0 ? _all[i - 1].Source : null, i >= 0 && i < _all.Count - 1 ? _all[i + 1].Source : null);
    }

    [RelayCommand]
    private async Task AiTranslate()
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning) return;
        var seg = Selected;
        IsAiRunning = true;
        AiLog = "";
        try
        {
            var ctx = CreateContext(AppServices.Ai.TranslateProviderId);
            var (prev, next) = Neighbours(seg);
            var project = Project;
            var doc = _doc;
            var knowledge = await Task.Run(() => Workflow.Knowledge(project, doc, seg.Model));
            var text = await new AiTranslator(ctx).TranslateAsync(seg.Source, _doc.SourceLanguage, _doc.TargetLanguage, Project.Topic,
                Matches.Select(m => m.Match).ToList(), Terms.Select(t => t.Hit).ToList(), prev, next, knowledge: knowledge);
            seg.Model.Target = text;
            seg.Model.Origin = TranslationOrigin.Ai;
            seg.Model.Status = SegmentStatus.Draft;
            seg.Model.MatchPercent = null;
            seg.Refresh();
            _main.Status = $"KI-Vorschlag eingesetzt ({ctx.Model.DisplayName}).";
        }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    /// <summary>Alle leeren Segmente per KI übersetzen (als Entwurf).</summary>
    [RelayCommand]
    private async Task AiTranslateAll()
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        IsAiRunning = true;
        AiLog = "";
        var cts = _batchCts = new CancellationTokenSource();
        var done = 0;
        try
        {
            var ctx = CreateContext(AppServices.Ai.TranslateProviderId);
            var translator = new AiTranslator(ctx);
            var todo = _all.Where(s => !s.IsLocked && s.Target.Length == 0).ToList();
            foreach (var seg in todo)
            {
                cts.Token.ThrowIfCancellationRequested();
                _main.Status = $"KI übersetzt Segment {seg.Id} ({done + 1}/{todo.Count}) …";
                var project = Project;
                var doc = _doc;
                var (matches, terms, knowledge) = await Task.Run(() => (
                    Workflow.Lookup(project, doc, seg.Model),
                    Workflow.Terms(project, doc, seg.Model),
                    Workflow.Knowledge(project, doc, seg.Model)));
                var (prev, next) = Neighbours(seg);
                seg.Model.Target = await translator.TranslateAsync(seg.Source, doc.SourceLanguage, doc.TargetLanguage, project.Topic,
                    matches, terms, prev, next, ct: cts.Token, knowledge: knowledge);
                seg.Model.Origin = TranslationOrigin.Ai;
                seg.Model.Status = SegmentStatus.Draft;
                seg.Refresh();
                done++;
                if (done % 10 == 0) SaveIfOpen();
            }
            _main.Status = $"KI hat {done} Segmente als Entwurf übersetzt.";
        }
        catch (OperationCanceledException) { _main.Status = $"Abgebrochen nach {done} Segmenten."; }
        catch (Exception ex) { _main.Status = $"KI nach {done} Segmenten: {ex.Message}"; }
        finally
        {
            IsAiRunning = false;
            SaveIfOpen();
            UpdateProgress();
        }
    }

    [RelayCommand]
    private void CancelAi() => _batchCts?.Cancel();

    /// <summary>Bewertet aktuelle Übersetzung, TM-Treffer und einen frischen KI-Vorschlag gegeneinander.</summary>
    [RelayCommand]
    private async Task EvaluateSegment()
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning) return;
        var seg = Selected;
        IsAiRunning = true;
        AiLog = "";
        AiResult = "";
        SidePanelTab = 6;
        try
        {
            var candidates = new List<TranslationCandidate>();
            if (seg.Target.Trim().Length > 0) candidates.Add(new("Aktuell", seg.Target));
            foreach (var m in Matches.Take(3).Where(m => m.Target != seg.Target))
                candidates.Add(new($"TM {m.Match.Score}%", m.Target));

            var translateCtx = CreateContext(AppServices.Ai.TranslateProviderId);
            translateCtx.Say("Hole zusätzlich einen KI-Vorschlag …");
            var (prev, next) = Neighbours(seg);
            var aiText = await new AiTranslator(translateCtx).TranslateAsync(seg.Source, _doc.SourceLanguage, _doc.TargetLanguage,
                Project.Topic, Matches.Select(m => m.Match).ToList(), Terms.Select(t => t.Hit).ToList(), prev, next);
            if (candidates.All(c => c.Text != aiText)) candidates.Add(new("KI", aiText));

            var ctx = CreateContext(AppServices.Ai.EvaluateProviderId);
            var proj = Project;
            var d = _doc;
            var kn = await Task.Run(() => Workflow.Knowledge(proj, d, seg.Model));
            var result = await new EvaluationAgent(ctx).RunAsync(seg.Source, _doc.SourceLanguage, _doc.TargetLanguage, candidates,
                Project.Topic, Terms.Select(t => t.Hit).ToList(), AiCheckUsage && ctx.Search != null, knowledge: kn);
            _lastEvaluation = result;
            AiResult = AgentsViewModel.FormatEvaluation(result);
        }
        catch (Exception ex) { AiResult = "Fehler: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    private EvaluationResult? _lastEvaluation;
    private TermComparisonResult? _lastComparison;
    private string? _lastComparisonTopic;

    [ObservableProperty] public partial bool HasComparison { get; set; }

    /// <summary>
    /// Terminologie-Assistent: klärt einen Begriff im Satzkontext, vergleicht die Begriffe beider Sprachen/Systeme
    /// auf Fachseiten und empfiehlt mit Begründung eine Entsprechung.
    /// </summary>
    public async Task ClarifyTermAsync(string term)
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning || term.Trim().Length == 0) return;
        var seg = Selected;
        IsAiRunning = true;
        AiLog = "";
        AiResult = "";
        HasComparison = false;
        SidePanelTab = 6;
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var (prev, next) = Neighbours(seg);
            var context = string.Join("\n", new[] { prev, seg.Source, next }.Where(x => x != null).Select(x => TagText.StripTags(x!)));
            var query = new TermQuery(term.Trim(), _doc.SourceLanguage, _doc.TargetLanguage, context, Project.Topic,
                _main.CurrentProfile, null, Terms.Select(t => t.Hit).ToList());
            _lastComparison = await new TermComparisonAgent(ctx).RunAsync(query);
            _lastComparisonTopic = Project.Topic;
            AiResult = TermComparisonAgent.Format(_lastComparison);
            HasComparison = true;
        }
        catch (Exception ex) { AiResult = "Fehler: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    [RelayCommand]
    private void SaveComparisonToTermbase()
    {
        if (_lastComparison == null || _doc == null || Project == null) return;
        var tb = Project.SharedTermbasesToWrite(_doc.TargetLanguage).FirstOrDefault() ?? Project.ProjectTermbase;
        try
        {
            TermComparisonAgent.SaveToTermbase(_lastComparison, tb, _doc.SourceLanguage, _doc.TargetLanguage, _lastComparisonTopic);
            _main.Status = $"„{_lastComparison.Source.Term}“ → „{_lastComparison.Recommendation.Term}“ in „{tb.Name}“ übernommen.";
            _main.Resources.Refresh();
            if (Selected != null) OnSelectedChanged(Selected);
        }
        catch (Exception ex) { _main.Status = ex.Message; }
    }

    [RelayCommand]
    private void SaveComparisonToKnowledge()
    {
        if (_lastComparison == null || _doc == null || Project == null) return;
        var kb = Project.KnowledgeFor(_doc.TargetLanguage).FirstOrDefault(k => k.IsRemote) ?? Project.ProjectKnowledge;
        try
        {
            TermComparisonAgent.SaveToKnowledge(_lastComparison, kb, _doc.TargetLanguage, _lastComparisonTopic, Environment.UserName);
            _main.Status = $"Begründung als Wissen in „{kb.Name}“ gespeichert.";
        }
        catch (Exception ex) { _main.Status = ex.Message; }
    }

    [RelayCommand]
    private void UseBestEvaluated()
    {
        if (Selected == null || _lastEvaluation == null) return;
        var text = _lastEvaluation.Suggestion
                   ?? _lastEvaluation.Scores.FirstOrDefault(s => s.Label == _lastEvaluation.BestLabel)?.Text
                   ?? _lastEvaluation.Scores.FirstOrDefault()?.Text;
        if (text == null) return;
        Selected.Model.Target = TagText.EnsureTags(Selected.Source, text);
        Selected.Model.Origin = TranslationOrigin.Ai;
        Selected.Model.Status = SegmentStatus.Draft;
        Selected.Refresh();
    }

    /// <summary>Prüft den markierten Ausdruck (oder die ganze Übersetzung) auf Üblichkeit im Netz.</summary>
    public async Task CheckUsageAsync(string expression)
    {
        if (_doc == null || IsAiRunning || expression.Trim().Length == 0) return;
        IsAiRunning = true;
        AiLog = "";
        AiResult = "";
        SidePanelTab = 6;
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var r = await new UsageCheckAgent(ctx).RunAsync(expression.Trim(), _doc.TargetLanguage, Project?.Topic);
            AiResult = AgentsViewModel.FormatUsage(r);
        }
        catch (Exception ex) { AiResult = "Fehler: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    [RelayCommand]
    private Task CheckUsageOfTarget() => CheckUsageAsync(Selected?.Target is { Length: > 0 } t ? TagText.StripTags(t) : "");

    /// <summary>Markierten Quelltext mit Übersetzung in die Projekt-Terminologie aufnehmen.</summary>
    public void AddTerm(string source, string target)
    {
        if (Project == null || _doc == null || source.Trim().Length == 0) return;
        Project.ProjectTermbase.AddConcept([
            (_doc.SourceLanguage, source.Trim(), TermStatus.Preferred),
            (_doc.TargetLanguage, target.Trim(), TermStatus.Preferred)]);
        _main.Status = $"Begriff „{source.Trim()}“ aufgenommen.";
        _main.Resources.Refresh();
        if (Selected != null) OnSelectedChanged(Selected);
    }
}
