using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Schritt des automatischen Ablaufs im Bericht: Name, Stand, Ergebnis, Dauer.</summary>
public sealed partial class PipelineStepRow(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial string Duration { get; set; } = "";
}

/// <summary>Ein Fehler der Bewertung im Bericht: wo, was, wie schwer, Anmerkung (bei der KI mit Verbesserung).</summary>
public sealed class LqaReportRow(SegmentViewModel segment, LqaError error)
{
    public SegmentViewModel Segment { get; } = segment;
    public LqaError Error { get; } = error;
    public string Header => L.T($"Segment {Segment.Id}") + " · " + L.T(Error.Category) + " · " + L.T(Lqa.SeverityName(Error.Severity)) + " · " +
                           (Error.Reviewer == QualityEvaluator.Reviewer ? L.T("Prüfer KI") : Error.Reviewer);
    public string Texts { get; } = TagText.StripTags(segment.Source) + "\n→ " + TagText.StripTags(segment.Model.Target);
    public string Note => Error.Note;
    public bool HasNote => Error.Note.Length > 0;
    public bool CanApply { get; } = Lqa.ApplySuggestion(segment.Model.Target, error) != null;
}

/// <summary>Ein Ausdruck, der gründlicher recherchiert werden sollte – mit den Gründen.</summary>
public sealed class FollowUpRow(FollowUpCandidate candidate)
{
    public FollowUpCandidate Candidate { get; } = candidate;
    public string Header => $"{Candidate.Expression} → {Candidate.Dossier.Recommended ?? "?"}";
    public string Reasons => string.Join("\n", Candidate.Reasons.Select(r => "• " + L.T(r)));
    public string Segments => L.T("Segmente") + ": " + string.Join(", ", Candidate.SegmentIds);
}

/// <summary>Eine Verbesserung nach dem Lektorat: vorher → nachher, warum.</summary>
public sealed class CorrectionRow(SegmentViewModel segment, string before, string after, string why)
{
    public SegmentViewModel Segment { get; } = segment;
    public string Header => L.T($"Segment {Segment.Id}");
    public string Change { get; } = $"„{before}“\n→ „{after}“";
    public string Why { get; } = why;
}

/// <summary>
/// „Automatisch übersetzen“: alles nacheinander, wie ein sorgfältiger Übersetzer vorgeht – Steckbrief lesen, die leeren
/// Segmente übersetzen (auf Wunsch mit Recherche je Segment und Netzprüfung), QA-Prüfung, KI-Lektorat, unsichere Ausdrücke
/// gründlicher nachrecherchieren und betroffene Segmente neu übersetzen, die Verbesserungen des Lektorats übernehmen, die
/// geänderten Segmente erneut bewerten. Am Ende öffnet sich der Bericht im Programm – mit jedem Schritt, der Bewertung,
/// den Korrekturen, den Ausdrücken, die noch mehr Recherche brauchen, und dem Protokoll. Alles bleibt Entwurf.
/// </summary>
public partial class EditorViewModel
{
    [ObservableProperty] public partial bool IsReportOpen { get; set; }

    /// <summary>Protokoll aufgeklappt (Reiter KI und Bericht); nach dem automatischen Ablauf von selbst.</summary>
    [ObservableProperty] public partial bool IsLogExpanded { get; set; }

    /// <summary>Im Dialog von „Automatisch übersetzen“: KI-Lektorat mit Korrektur und erneuter Bewertung.</summary>
    [ObservableProperty] public partial bool AutoReview { get; set; } = true;

    /// <summary>Im Dialog von „Automatisch übersetzen“: unsichere Ausdrücke gründlicher nachrecherchieren.</summary>
    [ObservableProperty] public partial bool AutoFollowUp { get; set; } = true;

    /// <summary>Der Dialog gilt „Automatisch übersetzen“ (zeigt die Schritte zum An- und Abwählen).</summary>
    [ObservableProperty] public partial bool BatchIsAuto { get; set; }

    public ObservableCollection<PipelineStepRow> PipelineSteps { get; } = new();
    public ObservableCollection<LqaReportRow> ReportErrors { get; } = new();
    public ObservableCollection<string> ReportCategories { get; } = new();
    public ObservableCollection<FollowUpRow> FollowUps { get; } = new();
    public ObservableCollection<CorrectionRow> Corrections { get; } = new();
    public bool HasPipelineSteps => PipelineSteps.Count > 0;
    public bool HasReportErrors => ReportErrors.Count > 0;
    public bool HasFollowUps => FollowUps.Count > 0;
    public bool HasCorrections => Corrections.Count > 0;

    /// <summary>Zeit je Ausdruck bei der Nachrecherche (gründlicher als im Übersetzungslauf).</summary>
    public static TimeSpan FollowUpBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Höchstens so viele Ausdrücke werden im automatischen Ablauf nachrecherchiert (die unsichersten zuerst).</summary>
    public const int MaxFollowUps = 5;

    /// <summary>Für Tests: statt der echten KI-Bewertung Fehler liefern (Segment-Nr., Quelle, Übersetzung).</summary>
    public static Func<IReadOnlyList<(int Id, string Source, string Target)>, CancellationToken, Task<Dictionary<int, List<LqaError>>>>? EvaluateOverride { get; set; }

    [RelayCommand]
    private void OpenReport()
    {
        RefreshReport();
        IsReportOpen = true;
    }

    [RelayCommand]
    private void CloseReport() => IsReportOpen = false;

    /// <summary>Bewertung, Fehlerliste und die Ausdrücke, die mehr Recherche brauchen, neu zusammenstellen.</summary>
    public void RefreshReport()
    {
        RefreshErrorList();
        FollowUps.Clear();
        if (_doc != null && Project != null)
        {
            var to = ResearchTo ?? _doc.TargetLanguage;
            foreach (var f in ResearchFollowUp.Candidates(DossierStore.Load(Project), _all.Select(s => s.Model).ToList(), to, DoubtfulPhrases()))
                FollowUps.Add(new FollowUpRow(f));
        }
        OnPropertyChanged(nameof(HasFollowUps));
        OnPropertyChanged(nameof(HasCorrections));
        OnPropertyChanged(nameof(HasPipelineSteps));
    }

    /// <summary>Alle erfassten Fehler und die Strafpunkte je Kategorie (Reiter Lektorat und Bericht).</summary>
    private void RefreshErrorList()
    {
        ReportErrors.Clear();
        ReportCategories.Clear();
        if (_doc != null && Project != null)
        {
            foreach (var s in _all)
                foreach (var e in s.Model.Errors ?? [])
                    ReportErrors.Add(new LqaReportRow(s, e));
            var r = Lqa.Evaluate([_doc], Project.Lqa);
            foreach (var c in r.Categories.Where(c => c.Minor + c.Major + c.Critical > 0))
                ReportCategories.Add(L.T($"{c.Category}: leicht {c.Minor}, schwer {c.Major}, kritisch {c.Critical} → {c.Points} Punkte"));
        }
        OnPropertyChanged(nameof(HasReportErrors));
    }

    /// <summary>Neues Dokument: Ablauf, Korrekturen und Bericht gehören zum vorigen.</summary>
    private void ClearReport()
    {
        IsReportOpen = false;
        PipelineSteps.Clear();
        Corrections.Clear();
        FollowUps.Clear();
        OnPropertyChanged(nameof(HasPipelineSteps));
        OnPropertyChanged(nameof(HasCorrections));
        OnPropertyChanged(nameof(HasFollowUps));
    }

    /// <summary>Wendungen, die die Netzprüfung dieser Sitzung fraglich oder falsch fand (je Segment).</summary>
    private List<(int SegmentId, string Phrase)> DoubtfulPhrases() =>
        _wordingChecks.SelectMany(kv => kv.Value.Check.Phrases
            .Where(p => p.Verdict is PhraseVerdict.Questionable or PhraseVerdict.Wrong)
            .Select(p => (kv.Key, p.Phrase))).ToList();

    /// <summary>Im Bericht angeklickt: zum Segment springen, Reiter Lektorat.</summary>
    [RelayCommand]
    private void GoToReportError(LqaReportRow? row)
    {
        if (row == null) return;
        IsReportOpen = false;
        Selected = row.Segment;
        SidePanelTab = 6;
    }

    /// <summary>Den Vorschlag eines Fehlers übernehmen (als Entwurf); der Fehler gilt damit als behoben.</summary>
    [RelayCommand]
    private void ApplyReportSuggestion(LqaReportRow? row)
    {
        if (row == null) return;
        if (ApplySuggestion(row.Segment, row.Error, onlyOpen: false) is not { } correction)
        {
            _main.Status = L.T("Der Vorschlag passt nicht mehr zur Übersetzung – bitte von Hand verbessern.");
            return;
        }
        Corrections.Insert(0, correction);
        FillErrors(Selected);
        UpdateLqa();
        RefreshReport();
        SaveIfOpen();
        _main.Status = L.T($"Vorschlag in Segment {row.Segment.Id} übernommen (Entwurf).");
    }

    /// <summary>
    /// Vorschlag eines Fehlers einsetzen: die bemängelte Stelle wird durch die bessere Fassung ersetzt, das Segment wird
    /// Entwurf, der Fehler fällt aus der Bewertung. null, wenn es keinen Vorschlag gibt, die Stelle nicht (mehr) in der
    /// Übersetzung steht oder das Segment bestätigt ist (<paramref name="onlyOpen"/>).
    /// </summary>
    private CorrectionRow? ApplySuggestion(SegmentViewModel seg, LqaError error, bool onlyOpen = true)
    {
        if (seg.IsLocked || onlyOpen && seg.Model.IsConfirmed) return null;
        var before = seg.Model.Target;
        if (Lqa.ApplySuggestion(before, error) is not { } after) return null;
        seg.Model.Target = after;
        seg.Model.Origin = TranslationOrigin.Ai;
        seg.Model.Status = SegmentStatus.Draft;
        seg.Model.Errors?.Remove(error);
        if (seg.Model.Errors is { Count: 0 }) seg.Model.Errors = null;
        seg.Refresh();
        return new CorrectionRow(seg, TagText.StripTags(before), TagText.StripTags(after), error.Note);
    }

    /// <summary>Im Bericht: diesen Ausdruck gründlich recherchieren (Recherche-Reiter, ohne Zeitgrenze, mit Begriffsfeld).</summary>
    [RelayCommand]
    private void ResearchFollowUpItem(FollowUpRow? row)
    {
        if (row == null) return;
        var c = row.Candidate;
        EnqueueResearch([new ResearchItem { Expression = c.Expression, Category = c.Dossier.Category, Difficulty = ResearchDifficulty.Hard, SegmentIds = c.SegmentIds.ToList() }],
            front: true);
        IsReportOpen = false;
        SidePanelTab = ResearchTab;
        _main.Status = L.T($"„{c.Expression}“ wird gründlich recherchiert – im Reiter Recherche.");
    }

    /// <summary>
    /// Die KI bewertet diese Segmente (Fehlervorschläge, Prüfer „KI“); frühere Vorschläge der KI für diese Segmente
    /// werden ersetzt, Fehler menschlicher Prüfer bleiben. Liefert die Zahl der vorgeschlagenen Fehler.
    /// </summary>
    private async Task<int> EvaluateCoreAsync(IReadOnlyList<SegmentViewModel> segments, CancellationToken ct)
    {
        if (_doc == null) return 0;
        var list = segments.Where(s => !s.IsLocked && s.Target.Trim().Length > 0).ToList();
        if (list.Count == 0) return 0;
        var input = list.Select(s => (s.Id, s.Source, s.Target)).ToList();
        Dictionary<int, List<LqaError>> found;
        if (EvaluateOverride != null) found = await EvaluateOverride(input, ct);
        else
        {
            var ctx = CreateContext(AppServices.Ai.EvaluateProviderId ?? AppServices.Ai.ResearchProviderId ?? AppServices.Ai.TranslateProviderId);
            if (!CanFollowInstructions(ctx)) ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            found = await new QualityEvaluator(ctx).EvaluateAsync(input, _doc.SourceLanguage, _doc.TargetLanguage, _brief, ct);
        }
        var count = 0;
        foreach (var s in list)
        {
            s.Model.Errors?.RemoveAll(e => e.Reviewer == QualityEvaluator.Reviewer);
            if (found.TryGetValue(s.Id, out var errors))
            {
                (s.Model.Errors ??= new()).AddRange(errors);
                count += errors.Count;
            }
            if (s.Model.Errors is { Count: 0 }) s.Model.Errors = null;
            s.Refresh();
        }
        FillErrors(Selected);
        UpdateLqa();
        return count;
    }

    /// <summary>Ein Schritt des Ablaufs: im Bericht und im Protokoll festhalten; ein Fehler hält die übrigen Schritte nicht auf.</summary>
    private async Task StepAsync(string name, Func<Task<string>> work, CancellationToken ct, string? skipBecause = null)
    {
        var row = new PipelineStepRow(L.T(name));
        PipelineSteps.Add(row);
        OnPropertyChanged(nameof(HasPipelineSteps));
        if (skipBecause != null)
        {
            row.State = L.T("übersprungen");
            row.Result = L.T(skipBecause);
            AiLog += $"– {row.Name}: {row.State} ({row.Result})\n";
            return;
        }
        row.State = L.T("läuft …");
        AiLog += $"▶ {row.Name}\n";
        var watch = Stopwatch.StartNew();
        try
        {
            row.Result = await work();
            row.State = L.T("fertig");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            row.State = L.T("abgebrochen");
            throw;
        }
        catch (Exception ex)
        {
            row.State = L.T("Fehler");
            row.Result = ex.Message;
        }
        finally
        {
            row.Duration = watch.Elapsed.TotalMinutes >= 1 ? $"{(int)watch.Elapsed.TotalMinutes} min {watch.Elapsed.Seconds} s" : $"{watch.Elapsed.TotalSeconds:0} s";
            AiLog += $"{(row.State == L.T("fertig") ? "✔" : "✖")} {row.Name}: {row.Result} ({row.Duration})\n";
        }
    }

    /// <summary>„Automatisch übersetzen“ – siehe Klassenbeschreibung.</summary>
    [RelayCommand]
    private async Task AutoTranslate()
    {
        if (_doc == null || Project == null || IsAiRunning || IsAskingBatchResearch) return;
        var empty = BatchTargets(false).Count;
        var (withResearch, withCheck, budget, smart) = await AskBatchQuestionsAsync(empty, selected: false, auto: true);
        if (_doc == null || Project == null || IsAiRunning) return;
        IsAiRunning = true;
        _batchRunning = true;
        AiLog = "";
        IsLogExpanded = false;
        IsReportOpen = false;
        PipelineSteps.Clear();
        Corrections.Clear();
        var cts = _batchCts = new CancellationTokenSource();
        var ct = cts.Token;
        var project = Project;
        var doc = _doc;
        var run = new TranslationRun();
        BatchResearchState? research = null;
        DocumentBrief? brief = null;
        var changed = new HashSet<SegmentViewModel>();
        double? scoreBefore = null;
        try
        {
            await StepAsync("Steckbrief lesen", async () =>
            {
                brief = await EnsureBriefAsync(ct);
                return brief == null ? L.T("kein Steckbrief – die KI übersetzt ohne")
                    : L.T("Steckbrief liegt vor") + (brief.TextType is { Length: > 0 } type ? $": {type}" : "") + (brief.Audience is { Length: > 0 } who ? $", {who}" : "");
            }, ct);

            var dossiers = await Task.Run(() => DossierStore.Load(project));
            if (withResearch || withCheck) research = NewBatchState(project, doc, dossiers, brief, withResearch, withCheck, budget, single: false, smart);
            var todo = BatchTargets(false);
            await StepAsync(withResearch ? "Übersetzen mit Recherche je Segment" : "Übersetzen", async () =>
            {
                await TranslateSegmentsAsync(todo, research, brief, dossiers, run, ct);
                var parts = new List<string> { L.T($"{run.Done} Segmente übersetzt") };
                if (run.Repaired > 0) parts.Add(L.T($"{run.Repaired} TM-Treffer angepasst"));
                if (research is { DoResearch: true }) parts.Add(L.T($"{research.Researched} Ausdrücke recherchiert, {research.Evidenced} belegt"));
                if (research is { WebCheck: true }) parts.Add(L.T($"{research.Checked} im Netz geprüft, {research.Improved} verbessert"));
                return string.Join(", ", parts) + run.PoolUsage;
            }, ct, todo.Count == 0 ? "keine leeren Segmente" : null);

            await StepAsync("QA-Prüfung", () =>
            {
                var issues = RunQaQuiet();
                return Task.FromResult(L.T($"{issues.Count(i => i.Severity == QaSeverity.Error)} Fehler, {issues.Count(i => i.Severity == QaSeverity.Warning)} Warnungen (Reiter QA)"));
            }, ct);

            if (AutoReview)
            {
                await StepAsync("KI-Lektorat", async () =>
                {
                    var n = await EvaluateCoreAsync(_all, ct);
                    var r = Lqa.Evaluate([doc], project.Lqa);
                    scoreBefore = r.Score;
                    return L.T($"{n} Fehler vorgeschlagen – {r.Score:0.0} von 100");
                }, ct);
                await StepAsync("Nachrecherche unsicherer Ausdrücke", () => FollowUpResearchAsync(project, doc, brief, changed, ct), ct,
                    !withResearch ? "ohne Recherche gewählt" : !AutoFollowUp ? "abgewählt" : null);
                await StepAsync("Verbesserungen des Lektorats übernehmen", () =>
                {
                    int applied = 0, manual = 0;
                    foreach (var s in _all.Where(s => !changed.Contains(s)).ToList())
                        foreach (var e in (s.Model.Errors ?? []).Where(e => e.Reviewer == QualityEvaluator.Reviewer).ToList())
                        {
                            if (ApplySuggestion(s, e) is { } correction)
                            {
                                Corrections.Add(correction);
                                changed.Add(s);
                                applied++;
                            }
                            else manual++;
                        }
                    OnPropertyChanged(nameof(HasCorrections));
                    return Task.FromResult(L.T($"{applied} Vorschläge übernommen, {manual} von Hand zu prüfen"));
                }, ct);
                await StepAsync("Geänderte Segmente erneut bewerten", async () =>
                {
                    var n = await EvaluateCoreAsync(changed.OrderBy(s => _all.IndexOf(s)).ToList(), ct);
                    var r = Lqa.Evaluate([doc], project.Lqa);
                    return L.T($"{changed.Count} Segmente geprüft, darin noch {n} Fehler – {scoreBefore ?? r.Score:0.0} → {r.Score:0.0} von 100");
                }, ct, changed.Count == 0 ? "nichts geändert" : null);
            }
            _main.Status = L.T("Automatische Übersetzung fertig – der Bericht zeigt jeden Schritt.");
        }
        catch (OperationCanceledException)
        {
            _main.Status = L.T("Automatische Übersetzung abgebrochen.");
            AiLog += L.T("Abgebrochen.") + "\n";
        }
        finally
        {
            IsAiRunning = false;
            _batchRunning = false;
            SaveIfOpen();
            UpdateProgress();
            UpdateLqa();
            if (Project == project)
            {
                _dossiers = DossierStore.Load(project);
                RefreshDossiers();
            }
            RefreshReport();
            IsLogExpanded = true;                                        // am Ende: Protokoll aufgeklappt, Bericht offen
            IsReportOpen = true;
        }
    }

    /// <summary>
    /// Nachrecherche: die Ausdrücke, die nach <see cref="ResearchFollowUp"/> unsicher sind, gründlicher recherchieren
    /// (längeres Zeitbudget); wo sich dadurch die belegte Übersetzung ändert, die betroffenen Segmente neu übersetzen.
    /// </summary>
    private async Task<string> FollowUpResearchAsync(TranslationProject project, BilingualDocument doc, DocumentBrief? brief,
        HashSet<SegmentViewModel> changed, CancellationToken ct)
    {
        var from = ResearchFrom ?? doc.SourceLanguage;
        var to = ResearchTo ?? doc.TargetLanguage;
        var candidates = ResearchFollowUp.Candidates(DossierStore.Load(project), _all.Select(s => s.Model).ToList(), to, DoubtfulPhrases())
            .Where(c => DocumentResearch.SkipReason(c.Expression, doc.Segments) == null)
            .Take(MaxFollowUps).ToList();
        if (candidates.Count == 0) return L.T("keine unsicheren Ausdrücke");
        DocumentResearch? researcher = null;
        var stepLimit = TimeSpan.FromTicks(FollowUpBudget.Ticks * 2);
        var evidenced = 0;
        var retranslate = new HashSet<SegmentViewModel>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            AiLog += L.T($"Nachrecherche „{c.Expression}“: {string.Join("; ", c.Reasons.Select(L.T))}") + "\n";
            _main.Status = L.T($"Recherchiere „{c.Expression}“ gründlicher ({i + 1} von {candidates.Count}) …");
            var item = new ResearchItem { Expression = c.Expression, Category = c.Dossier.Category, Difficulty = ResearchDifficulty.Hard, SegmentIds = c.SegmentIds.ToList() };
            var watch = Stopwatch.StartNew();
            // ein anderes Projekt hat ihn vielleicht schon belegt (Recherche-Gedächtnis, selbes Thema)
            var fresh = Recall(project, item, from, to, c.Dossier, out var previous);
            try
            {
                // sonst: beim vorigen Dossier ansetzen – dessen Kandidaten, Anfragen und Belege; dazu die Termbank der betroffenen Segmente
                var known = KnownTerms(project, doc, c.SegmentIds);
                fresh ??= await WithinLimitAsync(limit => ResearchOverride != null ? ResearchOverride(item, from, to, limit)
                    : (researcher ??= new DocumentResearch(CreateContext(AppServices.Ai.ResearchProviderId)) { TimeBudget = FollowUpBudget, ConceptFields = false })
                        .ResearchAsync(item, from, to, project.Topic, _main.CurrentProfile, known, CurrentFile?.Id, limit, previous: previous),
                    $"Nachrecherche „{c.Expression}“", stepLimit, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AiLog += L.T($"Nachrecherche „{c.Expression}“: {ex.Message}") + "\n";
                continue;
            }
            if (fresh == null) continue;
            if (fresh.Origin == null) LogResearchDuration(c.Expression, fresh, watch.Elapsed, ResearchDifficulty.Hard, FollowUpBudget);
            DossierStore.Add(project, fresh);
            var ok = DossierStore.IsEvidenced(fresh);
            if (ok) evidenced++;
            AiLog += L.T(ok ? $"„{c.Expression}“ → „{fresh.Recommended}“: jetzt belegt" : $"„{c.Expression}“ → „{fresh.Recommended}“: weiter nicht belegt") + "\n";
            if (ResearchFollowUp.ChangesTranslation(c.Dossier, fresh))
                foreach (var s in _all.Where(s => c.SegmentIds.Contains(s.Id) && !s.IsLocked && !s.Model.IsConfirmed))
                    retranslate.Add(s);
        }
        if (retranslate.Count > 0)
        {
            foreach (var s in retranslate)                                     // die alten Fehler gelten für die alte Fassung
            {
                s.Model.Errors?.RemoveAll(e => e.Reviewer == QualityEvaluator.Reviewer);
                if (s.Model.Errors is { Count: 0 }) s.Model.Errors = null;
            }
            await TranslateSegmentsAsync(retranslate.OrderBy(s => _all.IndexOf(s)).ToList(), null, brief, DossierStore.Load(project), new TranslationRun(), ct);
            changed.UnionWith(retranslate);
        }
        return L.T($"{candidates.Count} Ausdrücke gründlicher recherchiert, {evidenced} belegt; {retranslate.Count} Segmente neu übersetzt");
    }
}
