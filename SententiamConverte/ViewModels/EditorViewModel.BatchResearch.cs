using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// „Leere per KI“ mit Recherche: Vorher fragt ein Dialog „Auch recherchieren?“. Bei Ja sucht das Programm vor jedem
/// Segment dessen Ausdrücke, die Recherche brauchen, recherchiert die noch ohne Dossier (Fachseiten, Belege) und übersetzt
/// das Segment dann mit den Ergebnissen. Was schon recherchiert ist, wird nicht noch einmal gesucht – auch nicht, wenn
/// derselbe Ausdruck in mehreren Segmenten steht.
/// </summary>
public partial class EditorViewModel
{
    private TaskCompletionSource<bool>? _batchResearchAnswer;

    [ObservableProperty] public partial bool IsAskingBatchResearch { get; set; }
    [ObservableProperty] public partial string BatchResearchQuestion { get; set; } = "";

    /// <summary>Im Dialog: danach jede Übersetzung im Netz prüfen (Satzbau, Wortverbindungen) und Fragliches verbessern.</summary>
    [ObservableProperty] public partial bool BatchWebCheck { get; set; } = true;

    /// <summary>Zeitbudget je Ausdruck bei „Leere per KI“ mit Recherche: danach mit dem Gefundenen abschließen.</summary>
    public static TimeSpan BatchResearchBudget { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Harte Grenze je Schritt (Ausdrücke finden, recherchieren, im Netz prüfen): danach ohne ihn weiter.</summary>
    public static TimeSpan BatchStepLimit { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>So viele Ausdrücke je Segment werden während der Übersetzung recherchiert (die schwierigsten zuerst).</summary>
    public const int MaxResearchPerSegment = 4;

    /// <summary>„Leere per KI“ oder „Formulierungen verbessern“ läuft: die Recherche-Warteschlange wartet so lange.</summary>
    private bool _batchRunning;

    /// <summary>Einen Schritt mit Zeitgrenze ausführen; läuft er zu lange, wird er abgebrochen und null geliefert.</summary>
    private async Task<T?> WithinLimitAsync<T>(Func<CancellationToken, Task<T>> step, string what, TimeSpan stepLimit, CancellationToken ct) where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (stepLimit != Timeout.InfiniteTimeSpan) limit.CancelAfter(stepLimit);
        try { return await step(limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AiLog += L.T($"{what}: dauerte länger als {stepLimit.TotalMinutes:0.#} min – weiter ohne.") + "\n";
            return null;
        }
    }

    /// <summary>Wählbare Zeit je Ausdruck (null = ohne Grenze); „intelligent“: die KI bewertet jedes Segment und vergibt die Zeit.</summary>
    public sealed record ResearchTimeOption(string Label, TimeSpan? Budget, bool Smart = false)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<ResearchTimeOption> ResearchTimeOptions { get; } =
    [
        new(L.T("Intelligent – die KI bewertet jedes Segment und vergibt die Zeit"), null, Smart: true),
        new(L.T("2 Minuten – schnell"), TimeSpan.FromMinutes(2)),
        new(L.T("5 Minuten"), TimeSpan.FromMinutes(5)),
        new(L.T("10 Minuten"), TimeSpan.FromMinutes(10)),
        new(L.T("20 Minuten – gründlich"), TimeSpan.FromMinutes(20)),
        new(L.T("ohne Zeitgrenze – bis die Recherche fertig ist"), null),
    ];

    /// <summary>Im Dialog bei einem einzelnen Segment: Zeit je Ausdruck.</summary>
    [ObservableProperty] public partial ResearchTimeOption? SelectedResearchTime { get; set; }

    /// <summary>Der Dialog gilt einem einzelnen Segment: dann ist die Zeit je Ausdruck wählbar.</summary>
    [ObservableProperty] public partial bool BatchIsSingleSegment { get; set; }

    /// <summary>
    /// Eine Zeile ins Protokoll: wie lange die Recherche eines Ausdrucks dauerte, wie viele Runden und Seiten, ob belegt –
    /// so lässt sich auf dem eigenen Rechner ablesen, was eine Recherche kostet.
    /// </summary>
    private void LogResearchDuration(string expression, TermDossier dossier, TimeSpan elapsed, ResearchDifficulty? level = null, TimeSpan? budget = null)
    {
        var time = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s" : $"{elapsed.TotalSeconds:0} s";
        var state = L.T(DossierStore.IsEvidenced(dossier) ? "belegt" : "nicht belegt");
        AiLog += L.T($"⏱ „{expression}“: {time}, {dossier.Rounds} Runde(n), {dossier.PagesRead} Seiten – {state}") + "\n";
        // für die intelligente Bewertung: wie lange Ausdrücke dieser Stufe mit dieser KI wirklich brauchen (nur Zahlen)
        if (level is { } l)
            ResearchStats.Record(new ResearchStat(DateTime.UtcNow, ResearchModelName, l, budget?.TotalMinutes ?? 0, Math.Round(elapsed.TotalMinutes, 2),
                dossier.Rounds, DossierStore.IsEvidenced(dossier)));
    }

    /// <summary>Unter welchem Namen die Statistik die Recherche-KI führt.</summary>
    private static string ResearchModelName => AppServices.Ai.Get(AppServices.Ai.ResearchProviderId)?.Name ?? "KI";

    /// <summary>Die Termbank-Einträge der Segmente, in denen der Ausdruck steht – die Recherche kennt sie dann schon.</summary>
    private List<Core.Terminology.TermHit> KnownTerms(TranslationProject project, BilingualDocument doc, IEnumerable<int> segmentIds)
    {
        var ids = segmentIds.ToHashSet();
        return _all.Where(s => ids.Contains(s.Id)).SelectMany(s => Workflow.Terms(project, doc, s.Model))
            .DistinctBy(h => h.SourceTerm.Text + "|" + string.Join("/", h.Translations.Select(t => t.Text))).ToList();
    }

    /// <summary>Für Tests: Antwort auf „Auch recherchieren?“ ohne Dialog (null = Dialog zeigen).</summary>
    public static bool? BatchResearchAnswerOverride { get; set; }

    /// <summary>Für Tests mit <see cref="BatchResearchAnswerOverride"/>: auch im Netz prüfen?</summary>
    public static bool BatchWebCheckOverride { get; set; }

    /// <summary>Für Tests mit <see cref="BatchResearchAnswerOverride"/>: Zeit je Ausdruck bei einem einzelnen Segment.</summary>
    public static TimeSpan? BatchResearchTimeOverride { get; set; }

    /// <summary>Für Tests mit <see cref="BatchResearchAnswerOverride"/>: intelligente Bewertung?</summary>
    public static bool BatchSmartOverride { get; set; }

    /// <summary>Für Tests: statt der KI eine Bewertung liefern (Segment, gefundene Ausdrücke).</summary>
    public static Func<string, IReadOnlyList<ResearchItem>, CancellationToken, Task<SegmentAssessment>>? TriageOverride { get; set; }

    /// <summary>
    /// Vor „Leere per KI“ bzw. „Ausgewählte per KI“: Auch recherchieren? (Ja/Nein) – im Dialog auch: danach im Netz prüfen?
    /// Bei einem einzelnen Segment zusätzlich die Zeit je Ausdruck (null = ohne Grenze).
    /// </summary>
    private async Task<(bool Research, bool WebCheck, TimeSpan? Budget, bool Smart)> AskBatchQuestionsAsync(int segments, bool selected, bool auto = false)
    {
        var single = selected && segments == 1;
        if (BatchResearchAnswerOverride is { } answer)
            return (answer, BatchWebCheckOverride, single ? BatchResearchTimeOverride : BatchResearchBudget, BatchSmartOverride);
        BatchIsSingleSegment = single;
        BatchIsAuto = auto;
        SelectedResearchTime ??= ResearchTimeOptions[0];                // Vorgabe: intelligent
        var research = await AskBatchResearchAsync(segments, selected, auto);
        var choice = SelectedResearchTime ?? ResearchTimeOptions[0];
        return (research, BatchWebCheck, choice.Smart ? BatchResearchBudget : choice.Budget, choice.Smart);
    }

    private Task<bool> AskBatchResearchAsync(int segments, bool selected, bool auto = false)
    {
        _batchResearchAnswer?.TrySetResult(false);
        _batchResearchAnswer = new TaskCompletionSource<bool>();
        BatchResearchQuestion = auto
            ? L.T($"„Automatisch übersetzen“ führt alles nacheinander aus: Steckbrief lesen, die {segments} leeren Segmente übersetzen, QA-Prüfung, KI-Lektorat, unsichere Ausdrücke gründlicher nachrecherchieren, die Verbesserungen übernehmen und die geänderten Segmente erneut bewerten. Am Ende öffnet sich der Bericht mit dem Protokoll; alles bleibt Entwurf. Soll die KI beim Übersetzen auch recherchieren (Fachseiten im Netz, wörtliche Belege)? Das dauert deutlich länger, macht die Übersetzung aber sicherer.")
            : selected && segments == 1
            ? L.T("„Ausgewählte per KI“ übersetzt das gewählte Segment. Soll die KI dabei alle Ausdrücke darin recherchieren, die Recherche brauchen (Fachseiten im Netz, wörtliche Belege), und mit den Ergebnissen übersetzen? Wie lange sie je Ausdruck suchen darf, wählen Sie unten; was schon recherchiert ist, wird nicht noch einmal gesucht.")
            : selected
                ? L.T($"„Ausgewählte per KI“ übersetzt {segments} ausgewählte Segmente (bestätigte bleiben unverändert). Soll die KI dabei für jedes Segment die Ausdrücke recherchieren, die Recherche brauchen (Fachseiten im Netz, wörtliche Belege), und mit den Ergebnissen übersetzen? Das dauert deutlich länger; was schon recherchiert ist, wird nicht noch einmal gesucht.")
                : L.T($"„Leere per KI“ übersetzt {segments} Segmente. Soll die KI dabei für jedes Segment die Ausdrücke recherchieren, die Recherche brauchen (Fachseiten im Netz, wörtliche Belege), und mit den Ergebnissen übersetzen? Das dauert deutlich länger; was schon recherchiert ist, wird nicht noch einmal gesucht.");
        IsAskingBatchResearch = true;
        return _batchResearchAnswer.Task;
    }

    [RelayCommand]
    private void BatchResearchYes()
    {
        IsAskingBatchResearch = false;
        _batchResearchAnswer?.TrySetResult(true);
    }

    [RelayCommand]
    private void BatchResearchNo()
    {
        IsAskingBatchResearch = false;
        _batchResearchAnswer?.TrySetResult(false);
    }

    /// <summary>Was die Recherche während „Leere per KI“ gemacht hat.</summary>
    private sealed class BatchResearchState(TranslationProject project, BilingualDocument doc, List<TermDossier> dossiers, DocumentBrief? brief)
    {
        public bool DoResearch { get; init; }
        public bool WebCheck { get; init; }

        /// <summary>Intelligente Bewertung: die KI schätzt jedes Segment ein und vergibt die Zeit je Ausdruck.</summary>
        public bool Smart { get; init; }

        /// <summary>Die Bewertung je Segment (auch für den Zusammenhang beim Übersetzen).</summary>
        public Dictionary<int, SegmentAssessment> Assessments { get; } = new();
        public AgentContext? ResearchContext { get; set; }

        /// <summary>So viele Ausdrücke gleichzeitig (einmal je Lauf bestimmt).</summary>
        public int? Parallel { get; set; }

        /// <summary>Was gerade recherchiert wird (für die Statuszeile).</summary>
        public HashSet<string> Active { get; } = new(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>Zeit je Ausdruck (null = ohne Grenze).</summary>
        public TimeSpan? Budget { get; init; } = BatchResearchBudget;

        /// <summary>Harte Grenze je Schritt (unendlich = ohne).</summary>
        public TimeSpan StepLimit { get; init; } = BatchStepLimit;

        /// <summary>So viele Ausdrücke je Segment (bei einem einzelnen Segment alle).</summary>
        public int MaxPerSegment { get; init; } = MaxResearchPerSegment;

        /// <summary>Für die Statuszeile: „höchstens 4 min“ bzw. „ohne Zeitgrenze“.</summary>
        public string LimitText => StepLimit == Timeout.InfiniteTimeSpan ? L.T("ohne Zeitgrenze") : L.T($"höchstens {StepLimit.TotalMinutes:0.#} min");
        public AgentContext? CheckContext { get; set; }
        public int Checked { get; set; }
        public int Improved { get; set; }
        public TranslationProject Project { get; } = project;
        public BilingualDocument Doc { get; } = doc;
        public List<TermDossier> Dossiers { get; } = dossiers;
        public DocumentBrief? Brief { get; } = brief;
        public Dictionary<string, Task> Running { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public DocumentResearch? Research { get; set; }
        public int Researched { get; set; }
        public int Evidenced { get; set; }
    }

    /// <summary>
    /// Vor dem Übersetzen eines Segments: seine Ausdrücke finden (sind sie noch nicht bekannt, liest die KI das Segment) und
    /// recherchieren, was noch kein Dossier hat. Eine fehlgeschlagene Recherche hält die Übersetzung nicht auf.
    /// </summary>
    private async Task ResearchForTranslationAsync(SegmentViewModel seg, BatchResearchState state, CancellationToken ct)
    {
        var from = ResearchFrom ?? state.Doc.SourceLanguage;
        var to = ResearchTo ?? state.Doc.TargetLanguage;
        // im Stapel: mit Zeitbudget, ohne Begriffsfeld (das bleibt der Recherche im Reiter vorbehalten)
        DocumentResearch Researcher() => state.Research ??= new DocumentResearch(CreateContext(AppServices.Ai.ResearchProviderId))
            { TimeBudget = state.Budget, ConceptFields = false };

        var items = ResearchItems.Select(r => r.Item).Where(i => i.SegmentIds.Contains(seg.Id)).ToList();
        if (items.Count == 0 && _scannedSegments.Add(seg.Id) && TagText.StripTags(seg.Source).Trim().Length > 0)
        {
            _main.Status = L.T($"Segment {seg.Id}: suche Ausdrücke, die Recherche brauchen …");
            try
            {
                items = await WithinLimitAsync(async limit => FindOverride != null ? await FindOverride([seg.Model])
                    : await Researcher().FindInAsync(state.Doc, [seg.Model], from, to, state.Brief, state.Project.Topic, ct: limit),
                    $"Segment {seg.Id}: Ausdrücke suchen", state.StepLimit, ct) ?? [];
                foreach (var i in items.Where(i => !ResearchItems.Any(r => Same(r.Expression, i.Expression))))
                    ResearchItems.Add(new ResearchItemRow(i));
                OnPropertyChanged(nameof(HasResearchItems));
                RememberScan(seg, items, to);                    // beim Projekt: nach dem erneuten Öffnen nicht noch einmal lesen
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFor(seg, "Recherche", "Warnung", L.T($"Segment {seg.Id}: Ausdrücke nicht gefunden – {ex.Message}"));
                return;
            }
        }
        // Geschütztes (Name, Anschrift …) geht nicht ins Netz, ein bloßes Datum braucht keine Recherche
        var checkedItems = items.Select(i => (Item: i, Why: DocumentResearch.SkipReason(i.Expression, state.Doc.Segments))).ToList();
        foreach (var (skipped, why) in checkedItems.Where(x => x.Why != null))
            AiLog += L.T($"„{skipped.Expression}“ wird nicht recherchiert: {L.T(why!)}.") + "\n";
        var todo = checkedItems.Where(x => x.Why == null).Select(x => x.Item)
            .Where(i => !state.Dossiers.Any(d => Same(d.Expression, i.Expression) && Languages.Matches(d.TargetLanguage, to)))
            .OrderByDescending(i => i.Difficulty).ToList();

        // intelligente Bewertung: wie schwierig ist das Segment, welcher Ausdruck braucht wie viel Zeit, was bleibt einfach
        // stehen (Eigennamen), wie viele Nachbarsegmente braucht der Zusammenhang
        SegmentAssessment? assessment = null;
        if (state.Smart && todo.Count > 0)
        {
            _main.Status = L.T($"Segment {seg.Id}: die KI bewertet, wo sich Recherche lohnt …");
            var (before, after) = NeighbourSources(seg, SegmentTriage.MaxContext);
            var forTriage = todo;
            assessment = await WithinLimitAsync(limit => TriageOverride != null ? TriageOverride(seg.Source, forTriage, limit)
                : new SegmentTriage(state.ResearchContext ??= CreateContext(AppServices.Ai.ResearchProviderId)).AssessAsync(
                    TagText.StripTags(seg.Source), before, after, forTriage, from, to, state.Project.Topic,
                    KnownTerms(state.Project, state.Doc, [seg.Id]), limit,
                    // gelernt auf diesem Rechner: wie lange Ausdrücke jeder Stufe mit dieser KI bis zum Beleg brauchen
                    ResearchStats.PromptNote(ResearchModelName), level => ResearchStats.LearnedMinutes(ResearchModelName, level)),
                $"Segment {seg.Id}: Bewertung", BatchStepLimit, ct);
            if (assessment != null)
            {
                state.Assessments[seg.Id] = assessment;
                AiLog += AssessmentLine(seg, assessment) + "\n";
                seg.ShowAssessment(assessment.Difficulty, AssessmentLine(seg, assessment));
                RememberAssessment(seg, to);
                todo = todo.Where(i => assessment.For(i.Expression)?.Action is null or TriageAction.Research)
                    .OrderByDescending(i => assessment.For(i.Expression)?.Minutes ?? 0).ToList();
            }
        }
        if (assessment == null) todo = todo.Take(state.MaxPerSegment).ToList();
        // mehrere Ausdrücke gleichzeitig: während der eine auf Webseiten wartet, nutzt der andere die KI
        var parallel = todo.Count > 1 ? state.Parallel ??= ResearchParallelism() : 1;
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(todo.Select((item, n) => GatedAsync(item, n + 1)));

        async Task GatedAsync(ResearchItem item, int number)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!state.Running.TryGetValue(item.Expression, out var running))
                    state.Running[item.Expression] = running = ResearchOneAsync(item, number, todo.Count);
                await running;                                           // steht der Ausdruck in mehreren Segmenten: nur einmal
            }
            finally { gate.Release(); }
        }

        // die Segmente, zu denen eine Meldung über den Ausdruck gehört
        IEnumerable<int> Where(ResearchItem item) => item.SegmentIds.Append(seg.Id);

        async Task ResearchOneAsync(ResearchItem item, int number, int count)
        {
            // Zeit je Ausdruck: von der Bewertung, sonst die gewählte; die harte Grenze ist mindestens das Doppelte
            var minutes = assessment?.For(item.Expression)?.Minutes;
            var budget = minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : state.Budget;
            var stepLimit = minutes is > 0 ? TimeSpan.FromTicks(Math.Max(BatchStepLimit.Ticks, 2 * budget!.Value.Ticks)) : state.StepLimit;
            // Zusammenhang für die Recherche: so viele Nachbarsegmente, wie die Bewertung für nötig hält
            if (assessment is { ContextBefore: > 0 } or { ContextAfter: > 0 })
                item.Context = ContextWindow(seg, assessment.ContextBefore, assessment.ContextAfter);
            var limitText = minutes is > 0 ? L.T($"höchstens {budget!.Value.TotalMinutes:0.#} min") : state.LimitText;
            state.Active.Add(item.Expression);
            ShowActive();
            try
            {
                // im Recherche-Gedächtnis schon belegt (anderes Projekt, selbes Thema)? Dann übernehmen statt recherchieren
                var dossier = Recall(state.Project, item, from, to, null, out var previous);
                if (dossier == null)
                {
                    var known = KnownTerms(state.Project, state.Doc, item.SegmentIds.Append(seg.Id));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    // gleichzeitig: jede Recherche mit eigenem Zugang (eigene Seitenabfrage, eigener Datenschutz-Abgleich) und
                    // mit dem Ausdruck vor jeder Protokollzeile
                    var itemContext = parallel > 1 ? CreateContext(AppServices.Ai.ResearchProviderId, item.Expression) : null;
                    DocumentResearch NewResearcher(TimeSpan? time) =>
                        new(itemContext ?? (state.ResearchContext ??= CreateContext(AppServices.Ai.ResearchProviderId))) { TimeBudget = time, ConceptFields = false };
                    var researcher = minutes is > 0 || itemContext != null ? NewResearcher(budget) : Researcher();
                    dossier = await WithinLimitAsync(limit => ResearchOverride != null ? ResearchOverride(item, from, to, limit)
                        : researcher.ResearchAsync(item, from, to, state.Project.Topic, _main.CurrentProfile, known, CurrentFile?.Id, limit, previous),
                        $"Recherche „{item.Expression}“", stepLimit, ct);
                    if (dossier == null) return;
                    LogResearchDuration(item.Expression, dossier, watch.Elapsed, item.Difficulty, budget);

                    // intelligent: reicht das für die Übersetzung? Wenn nicht, sagt die KI, was fehlt – eine gezielte zweite
                    // Runde mit mehr Zeit, die auf dem Gefundenen aufbaut. Belegt und eindeutig reicht ohne Prüfung.
                    // Eine gescheiterte Prüfung oder zweite Runde wirft das Ergebnis der ersten nie weg.
                    if (assessment != null && !ResearchCheck.ClearlySufficient(dossier))
                    {
                        SufficiencyVerdict? verdict = null;
                        var checking = dossier;
                        try
                        {
                            verdict = await WithinLimitAsync(limit => SufficiencyOverride != null ? SufficiencyOverride(checking, limit)
                                : new ResearchCheck(itemContext ?? (state.ResearchContext ??= CreateContext(AppServices.Ai.ResearchProviderId)))
                                    .CheckAsync(checking, item.Context, from, to, limit),
                                $"Prüfung „{item.Expression}“", BatchStepLimit, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            LogFor(Where(item), "Recherche", "Warnung", L.T($"Prüfung „{item.Expression}“: {ex.Message}"));
                        }
                        if (verdict is { Sufficient: false })
                        {
                            var more = TimeSpan.FromMinutes(Math.Clamp(2 * (minutes ?? budget?.TotalMinutes ?? 3), 3, SegmentTriage.MaxMinutes));
                            LogFor(Where(item), "Recherche", "Hinweis",
                                L.T($"„{item.Expression}“: reicht noch nicht – es fehlt: {verdict.Missing} Zweite Runde mit höchstens {more.TotalMinutes:0.#} min."));
                            var first = dossier;
                            var again = System.Diagnostics.Stopwatch.StartNew();
                            TermDossier? second = null;
                            try
                            {
                                second = await WithinLimitAsync(limit => ResearchOverride != null ? ResearchOverride(item, from, to, limit)
                                    : NewResearcher(more).ResearchAsync(item, from, to, state.Project.Topic, _main.CurrentProfile, known, CurrentFile?.Id, limit,
                                        first, verdict.Missing),
                                    $"Zweite Runde „{item.Expression}“", TimeSpan.FromTicks(Math.Max(BatchStepLimit.Ticks, 2 * more.Ticks)), ct);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                LogFor(Where(item), "Recherche", "Warnung", L.T($"Zweite Runde „{item.Expression}“: {ex.Message}"));
                            }
                            if (second != null)
                            {
                                LogResearchDuration(item.Expression, second, again.Elapsed, item.Difficulty, more);
                                dossier = second;
                                var sure = ResearchCheck.ClearlySufficient(second);
                                LogFor(Where(item), "Recherche", sure ? "Hinweis" : "Warnung", L.T(sure
                                    ? $"„{item.Expression}“: jetzt belegt und eindeutig."
                                    : $"„{item.Expression}“: weiter unsicher – steht im Bericht unter „Gründlicher recherchieren?“."));
                            }
                        }
                        else if (verdict != null)
                            LogFor(Where(item), "Recherche", "Hinweis", L.T($"„{item.Expression}“: reicht für die Übersetzung.") + (verdict.Why.Length > 0 ? $" ({verdict.Why})" : ""));
                    }
                }
                DossierStore.Add(state.Project, dossier);
                state.Dossiers.Add(dossier);
                state.Researched++;
                if (DossierStore.IsEvidenced(dossier)) state.Evidenced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFor(Where(item), "Recherche", "Warnung", L.T($"Recherche „{item.Expression}“: {ex.Message}"));
            }
            finally
            {
                state.Active.Remove(item.Expression);
                ShowActive();
            }

            void ShowActive()
            {
                if (state.Active.Count == 0) return;
                _main.Status = state.Active.Count == 1
                    ? L.T($"Recherchiere „{state.Active.First()}“ für Segment {seg.Id} ({number} von {count}, {limitText}) …")
                    : L.T($"Recherchiere gleichzeitig {string.Join(", ", state.Active.Select(a => $"„{a}“"))} für Segment {seg.Id} …");
            }
        }
    }

    /// <summary>Für Tests und zum Abstimmen: so viele Ausdrücke gleichzeitig (null = nach der KI).</summary>
    public static int? ParallelResearchOverride { get; set; }

    /// <summary>Für Tests: statt der KI prüfen, ob eine Recherche reicht.</summary>
    public static Func<TermDossier, CancellationToken, Task<SufficiencyVerdict>>? SufficiencyOverride { get; set; }

    /// <summary>
    /// Wie viele Ausdrücke gleichzeitig recherchiert werden: Ein Verbund so viele, wie Modelle bereitstehen (höchstens 4);
    /// eine Online-KI 3; die KI auf diesem Rechner beantwortet eine Anfrage nach der anderen – mit 2 überlappen sich wenigstens
    /// das Warten auf Webseiten und die Arbeit der KI.
    /// </summary>
    private int ResearchParallelism()
    {
        if (ParallelResearchOverride is { } n) return Math.Max(1, n);
        try
        {
            var model = Core.Privacy.PrivacyContext.Unwrap(CreateContext(AppServices.Ai.ResearchProviderId).Model);
            if (model is Core.Ai.IParallelChatModel { Parallelism: > 1 } pool) return Math.Min(4, pool.Parallelism);
        }
        catch (Core.Ai.AiException) { return 1; }
        return AppServices.Ai.Get(AppServices.Ai.ResearchProviderId) is { IsLocal: false } ? 3 : 2;
    }

    /// <summary>Die Quelltexte der Nachbarsegmente: bis zu <paramref name="max"/> davor und danach.</summary>
    private (List<string> Before, List<string> After) NeighbourSources(SegmentViewModel seg, int max)
    {
        var i = _all.IndexOf(seg);
        if (i < 0) return ([], []);
        var before = _all.Skip(Math.Max(0, i - max)).Take(Math.Min(max, i)).Select(s => TagText.StripTags(s.Source)).ToList();
        var after = _all.Skip(i + 1).Take(max).Select(s => TagText.StripTags(s.Source)).ToList();
        return (before, after);
    }

    /// <summary>Zusammenhang für Recherche und Übersetzung: die Nachbarsegmente davor, das Segment, die danach.</summary>
    private string ContextWindow(SegmentViewModel seg, int before, int after)
    {
        var (b, a) = NeighbourSources(seg, Math.Max(before, after));
        return string.Join(" ", b.Skip(Math.Max(0, b.Count - before)).Append(TagText.StripTags(seg.Source)).Concat(a.Take(after)));
    }

    /// <summary>Eine Zeile Bewertung ins Protokoll: Schwierigkeit, Grund, Zusammenhang, was mit jedem Ausdruck geschieht.</summary>
    private static string AssessmentLine(SegmentViewModel seg, SegmentAssessment a)
    {
        var level = L.T(a.Difficulty switch { "easy" => "einfach", "hard" => "schwer", "very_hard" => "sehr schwer", _ => "mittel" });
        var items = string.Join(" · ", a.Items.Select(i => i.Action switch
        {
            TriageAction.Keep => L.T($"„{i.Expression}“ bleibt unverändert"),
            TriageAction.Skip => L.T($"„{i.Expression}“ nicht nötig"),
            _ => L.T($"„{i.Expression}“ {i.Minutes:0.#} min"),
        } + (i.Why.Length > 0 ? $" ({i.Why})" : "")));
        return L.T($"Segment {seg.Id} – Bewertung: {level}") + (a.Reason.Length > 0 ? $" ({a.Reason})" : "") + ". " +
               L.T($"Zusammenhang: {a.ContextBefore} Segmente davor, {a.ContextAfter} danach.") + (items.Length > 0 ? " " + items : "");
    }
}
