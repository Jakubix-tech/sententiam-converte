using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Ein Ausdruck des gewählten Segments im Recherche-Reiter: was er bedeutet und wie er in der Zielsprache heißt – oder
/// klar, dass er noch nicht recherchiert ist bzw. gerade recherchiert wird.
/// </summary>
public sealed class SegmentExpressionRow(string expression, TermDossier? dossier, bool queued)
{
    public string Expression { get; } = expression;
    public TermDossier? Dossier { get; } = dossier;
    public bool IsResearched => Dossier != null;
    public bool IsQueued { get; } = queued;
    public string Target => Dossier?.Recommended is { Length: > 0 } r ? r
        : Dossier != null ? L.T("keine Entsprechung belegt") : L.T(IsQueued ? "wird recherchiert …" : "noch nicht recherchiert");
    public string Header => $"{Expression}  →  {Target}" + (Dossier != null ? $"   [{L.T("Güte")} {DossierStore.Grade(Dossier)}]" : "");
    public string Meaning => Dossier?.Meaning ?? "";
    public bool HasMeaning => Meaning.Length > 0;
    public double Opacity => IsResearched ? 1 : 0.6;
}

/// <summary>
/// Recherche, die weiterläuft: eine Warteschlange, die nacheinander Dossiers erstellt – egal, welcher Reiter oder welches
/// Segment gerade gewählt ist; jedes Dossier erscheint, sobald es fertig ist. Bei offenem Recherche-Reiter zeigt das
/// gewählte Segment seine Ausdrücke mit Bedeutung und Entsprechung; was noch fehlt, kommt vorn in die Warteschlange.
/// </summary>
public partial class EditorViewModel
{
    private const int ResearchTab = 8;
    private readonly List<(ResearchItem Item, string? From, string? To)> _researchQueue = new();
    private readonly HashSet<int> _scannedSegments = new();
    private Task? _researchWorker;
    private string? _currentResearch;

    /// <summary>Für Tests: statt der echten Recherche (Websuche, KI) ein Dossier liefern.</summary>
    public static Func<ResearchItem, string, string, CancellationToken, Task<TermDossier>>? ResearchOverride { get; set; }

    /// <summary>Für Tests: statt der KI die Ausdrücke eines Segments liefern.</summary>
    public static Func<IReadOnlyList<Segment>, Task<List<ResearchItem>>>? FindOverride { get; set; }

    /// <summary>Die KI sieht gerade das Dokument (oder ein Segment) nach Ausdrücken durch.</summary>
    [ObservableProperty] public partial bool IsScanning { get; set; }

    /// <summary>Bei offenem Recherche-Reiter die Ausdrücke des gewählten Segments gleich recherchieren.</summary>
    [ObservableProperty] public partial bool AutoResearchSegment { get; set; } = true;

    [ObservableProperty] public partial string SegmentResearchNote { get; set; } = "";

    public ObservableCollection<SegmentExpressionRow> SegmentExpressions { get; } = new();
    public bool HasSegmentExpressions => SegmentExpressions.Count > 0;
    public bool IsResearchBusy => IsResearching || IsScanning;

    /// <summary>Reiterköpfe mit ⏳, solange dort gearbeitet wird – die Arbeit läuft weiter, wenn man den Reiter wechselt.</summary>
    public string AiTabHeader => L.T("KI") + (IsAiRunning ? " ⏳" : "");
    public string ResearchTabHeader => L.T("Recherche") + (IsResearchBusy ? " ⏳" : "");

    /// <summary>Arbeitet die KI im Editor (Übersetzung, Prüfung, Recherche)? Ein Projektwechsel hält sie vorher an.</summary>
    public bool IsAiWorking => IsAiRunning || IsResearchBusy;

    partial void OnIsAiRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(AiTabHeader));
        OnPropertyChanged(nameof(IsAiWorking));
    }

    partial void OnIsResearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ResearchTabHeader));
        OnPropertyChanged(nameof(IsResearchBusy));
        OnPropertyChanged(nameof(IsAiWorking));
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ResearchTabHeader));
        OnPropertyChanged(nameof(IsResearchBusy));
        OnPropertyChanged(nameof(IsAiWorking));
    }

    partial void OnSidePanelTabChanged(int value)
    {
        if (value == ResearchTab) _ = StudySelectedSegmentAsync();
    }

    /// <summary>
    /// Nach einer langen Arbeit den Ergebnisreiter zeigen – nur, wenn man in der Zwischenzeit nicht selbst einen anderen
    /// Reiter gewählt hat; die Arbeit läuft beim Wechseln ohnehin weiter, nur der Sprung zurück würde stören.
    /// </summary>
    private void ShowResultTab(int tab, int startedOn)
    {
        if (SidePanelTab == startedOn) SidePanelTab = tab;
    }

    /// <summary>Derselbe Ausdruck – auch mit Artikel, gebeugt oder anders geschrieben (siehe <see cref="DocumentResearch.SameExpression"/>).</summary>
    private static bool Same(string a, string b) => DocumentResearch.SameExpression(a, b);

    private TermDossier? FindDossier(string expression, string targetLanguage) =>
        _dossiers.Where(d => Same(d.Expression, expression) && Languages.Matches(d.TargetLanguage, targetLanguage))
            .OrderByDescending(d => d.Created).FirstOrDefault();

    private bool IsQueued(string expression) =>
        _researchQueue.Any(q => Same(q.Item.Expression, expression)) || _currentResearch is { } c && Same(c, expression);

    /// <summary>Ausdrücke des gewählten Segments mit Bedeutung und Zielsprache; was fehlt, steht klar als solches da.</summary>
    private void RefreshSegmentExpressions()
    {
        SegmentExpressions.Clear();
        SegmentResearchNote = "";
        if (_doc != null && Selected is { } seg)
        {
            var source = TagText.StripTags(seg.Source);
            var target = ResearchTo ?? _doc.TargetLanguage;
            var expressions = new List<string>();
            foreach (var e in ResearchItems.Select(r => r.Item)
                         .Where(i => i.SegmentIds.Contains(seg.Id) || DocumentResearch.Contains(source, i.Expression)).Select(i => i.Expression)
                         .Concat(DossierStore.ForText(_dossiers, source, target).Select(d => d.Expression)))
                if (!expressions.Any(x => Same(x, e))) expressions.Add(e);
            foreach (var e in expressions)
                SegmentExpressions.Add(new SegmentExpressionRow(e, FindDossier(e, target), IsQueued(e)));
            var missing = SegmentExpressions.Where(r => !r.IsResearched).ToList();
            if (missing.Count > 0)
                SegmentResearchNote = L.T($"Zu {string.Join(", ", missing.Select(r => $"„{r.Expression}“"))} habe ich noch nichts") + " – "
                    + L.T(missing.Any(r => r.IsQueued) ? "ich recherchiere jetzt." : "„Markierung / Segment“ oder „Angehakte recherchieren“ holt es nach.");
        }
        OnPropertyChanged(nameof(HasSegmentExpressions));
    }

    /// <summary>
    /// Recherche-Reiter offen, Segment gewählt: dessen Ausdrücke finden (ist das Dokument noch nicht durchgesehen, liest die
    /// KI das Segment) und, was noch kein Dossier hat, vorn in die Warteschlange stellen.
    /// </summary>
    private async Task StudySelectedSegmentAsync()
    {
        if (!AutoResearchSegment || SidePanelTab != ResearchTab || _doc == null || Project == null || Selected is not { } seg) return;
        if (_batchRunning) return;                                       // „Leere per KI“ recherchiert die Segmente selbst
        var doc = _doc;
        var target = ResearchTo ?? doc.TargetLanguage;
        var items = ResearchItems.Select(r => r.Item).Where(i => i.SegmentIds.Contains(seg.Id)).ToList();
        if (items.Count == 0 && _scannedSegments.Add(seg.Id) && TagText.StripTags(seg.Source).Trim().Length > 0)
        {
            IsScanning = true;
            try
            {
                items = FindOverride != null ? await FindOverride([seg.Model])
                    : await new DocumentResearch(CreateContext(AppServices.Ai.ResearchProviderId))
                        .FindInAsync(doc, [seg.Model], ResearchFrom ?? doc.SourceLanguage, target, _brief, Project?.Topic);
                foreach (var i in items.Where(i => !ResearchItems.Any(r => Same(r.Expression, i.Expression))))
                    ResearchItems.Add(new ResearchItemRow(i));
                OnPropertyChanged(nameof(HasResearchItems));
                if (_doc == doc) RememberScan(seg, items, target);    // beim Projekt: nach dem erneuten Öffnen nicht noch einmal lesen
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ResearchStatus = "KI: " + ex.Message; }
            finally { IsScanning = false; }
        }
        if (_doc != doc) return;
        var missing = items.Where(i => FindDossier(i.Expression, target) == null && !IsQueued(i.Expression)).ToList();
        if (missing.Count > 0) EnqueueResearch(missing, front: Selected == seg);
        RefreshSegmentExpressions();
    }

    /// <summary>Ausdrücke in die Warteschlange stellen (vorn: gleich als Nächstes) und die Abarbeitung starten, falls sie ruht.</summary>
    private void EnqueueResearch(IEnumerable<ResearchItem> items, bool front, string? from = null, string? to = null)
    {
        // Geschütztes (Name, Anschrift …) geht nicht ins Netz, ein bloßes Datum braucht keine Recherche
        var segments = _doc?.Segments ?? [];
        var list = items.ToList();
        foreach (var skipped in list.Where(i => DocumentResearch.SkipReason(i.Expression, segments) is not null).ToList())
        {
            ResearchStatus = L.T($"„{skipped.Expression}“ wird nicht recherchiert: {L.T(DocumentResearch.SkipReason(skipped.Expression, segments)!)}.");
            list.Remove(skipped);
        }
        var fresh = list.Where(i => !_researchQueue.Any(q => Same(q.Item.Expression, i.Expression)) && !(_currentResearch is { } c && Same(c, i.Expression)))
            .Select(i => (i, from, to)).ToList();
        if (front) _researchQueue.InsertRange(0, fresh);
        else _researchQueue.AddRange(fresh);
        if (_researchWorker == null)
        {
            var worker = RunResearchQueueAsync();
            if (!worker.IsCompleted) _researchWorker = worker;
        }
        RefreshSegmentExpressions();
    }

    private async Task RunResearchQueueAsync()
    {
        if (_doc == null || Project == null) return;
        var project = Project;
        var doc = _doc;
        IsResearching = true;
        var cts = _researchCts = new CancellationTokenSource();
        var done = 0;
        try
        {
            var research = ResearchOverride == null ? new DocumentResearch(CreateContext(AppServices.Ai.ResearchProviderId)) : null;
            while (_researchQueue.Count > 0)
            {
                cts.Token.ThrowIfCancellationRequested();
                // die eingebaute KI beantwortet eine Anfrage nach der anderen: während „Leere per KI“ warten
                if (_batchRunning) ResearchStatus = L.T("Recherche wartet, bis „Leere per KI“ fertig ist …");
                while (_batchRunning) await Task.Delay(500, cts.Token);
                var (item, from, to) = _researchQueue[0];
                _researchQueue.RemoveAt(0);
                _currentResearch = item.Expression;
                RefreshSegmentExpressions();
                ResearchStatus = L.T($"Recherchiere „{item.Expression}“ ({done + 1} von {done + 1 + _researchQueue.Count}) …");
                // die Termbank der Segmente, in denen der Ausdruck steht (sonst des gewählten)
                var known = item.SegmentIds.Count > 0 ? KnownTerms(project, doc, item.SegmentIds) : Terms.Select(t => t.Hit).ToList();
                var source = from ?? ResearchFrom ?? doc.SourceLanguage;
                var target = to ?? ResearchTo ?? doc.TargetLanguage;
                // im Recherche-Gedächtnis schon belegt? Übernehmen. Sonst beim vorhandenen Dossier ansetzen, statt von vorn zu beginnen
                var dossier = Recall(project, item, source, target, FindDossier(item.Expression, target), out var previous);
                if (dossier == null)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    dossier = ResearchOverride != null ? await ResearchOverride(item, source, target, cts.Token)
                        : await research!.ResearchAsync(item, source, target, project.Topic, _main.CurrentProfile, known, CurrentFile?.Id, cts.Token, previous);
                    LogResearchDuration(item.Expression, dossier, watch.Elapsed, item.Difficulty);
                }
                DossierStore.Add(project, dossier);
                done++;
                // gleich zeigen, nicht erst am Ende
                if (Project == project)
                {
                    _dossiers = DossierStore.Load(project);
                    RefreshDossiers();
                }
            }
            ResearchStatus = L.T($"{done} Dossiers erstellt.");
        }
        catch (OperationCanceledException)
        {
            _researchQueue.Clear();
            ResearchStatus = L.T($"Abgebrochen nach {done} Dossiers.");
        }
        catch (Exception ex)
        {
            _researchQueue.Clear();
            ResearchStatus = "KI: " + ex.Message;
        }
        finally
        {
            _currentResearch = null;
            _researchWorker = null;
            IsResearching = false;
            if (Project == project)
            {
                _dossiers = DossierStore.Load(project);
                RefreshDossiers();
            }
        }
    }
}
