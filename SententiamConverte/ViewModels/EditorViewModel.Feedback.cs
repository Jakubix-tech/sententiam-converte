using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Rückmeldung aus der eigenen Arbeit (siehe <see cref="ResearchFeedback"/>): Beim Bestätigen sieht das Programm, ob die
/// empfohlene Benennung eines recherchierten Ausdrucks stehen blieb (übernommen) oder etwas anderes an ihre Stelle trat
/// (ersetzt); ein Terminologie-Fehler, den ein Prüfer erfasst, zählt als ersetzt. Daraus lernen die Gütestufen und der Ruf
/// der Seiten in der Quellen-Datenbank. Erkannt wird wörtlich, gebeugt und umgestellt sofort (siehe
/// <see cref="RecommendationUse"/>); ist der Satz umformuliert, ordnet die KI im Hintergrund zu, welche Wörter den Ausdruck
/// wiedergeben – eine eigene Benennung des Übersetzers zählt als ersetzt, eine bloße Umschreibung zählt nicht.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>„Übernommen beim Bestätigen: A 12/12 · B 8/10 …“ – für den Reiter Recherche.</summary>
    [ObservableProperty] public partial string FeedbackSummary { get; set; } = "";

    /// <summary>Für Tests: statt der KI zuordnen (Ausgangstext, Übersetzung, offene Dossiers).</summary>
    public static Func<string, string, IReadOnlyList<TermDossier>, CancellationToken, Task<Dictionary<TermDossier, TermUse>>>? TermUseOverride { get; set; }

    /// <summary>Eine Zuordnung nach der anderen – die KI soll nicht mit der eigentlichen Arbeit um die Wette rechnen.</summary>
    private readonly SemaphoreSlim _feedbackGate = new(1, 1);
    private readonly List<Task> _feedbackRuns = new();
    /// <summary>Schon zugeordnet (Schlüssel, Übersetzung): erneutes Bestätigen ohne Änderung fragt die KI nicht noch einmal.</summary>
    private readonly Dictionary<(string Key, string Target), TermUse?> _termUses = new();

    /// <summary>Für Tests: bis die Zuordnungen im Hintergrund fertig sind.</summary>
    public Task FeedbackDone
    {
        get { lock (_feedbackRuns) return Task.WhenAll(_feedbackRuns.ToArray()); }
    }

    private void RefreshFeedbackSummary() =>
        FeedbackSummary = ResearchFeedback.Summary() is { } s ? L.T("Übernommen beim Bestätigen (je Gütestufe): ") + s : "";

    private string FeedbackKey(SegmentViewModel seg, TermDossier d) =>
        $"{Project?.Name}|{CurrentFile?.Id}|{seg.Id}|{d.Expression.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Nach dem Bestätigen: je recherchiertem Ausdruck des Segments übernommen, ersetzt oder umschrieben. Was wörtlich oder
    /// gebeugt dasteht, gleich; umformulierte Sätze ordnet die KI im Hintergrund zu (ohne KI-Zugang: keine Rückmeldung).
    /// </summary>
    private void RecordResearchFeedback(SegmentViewModel seg)
    {
        if (_doc == null || Project == null) return;
        var target = TagText.StripTags(seg.Model.Target);
        var changed = false;
        var open = new List<(TermDossier Dossier, string Key)>();
        foreach (var d in DossierStore.ForText(_dossiers, seg.Source, _doc.TargetLanguage).Where(d => d.Recommended is { Length: > 1 }))
        {
            var key = FeedbackKey(seg, d);
            if (RecommendationUse.Quick(target, d) is { } use) changed |= ApplyTermUse(seg, d, key, use);
            else open.Add((d, key));
        }
        if (changed) RefreshFeedbackSummary();
        if (open.Count == 0) return;
        var run = AssignRephrasedAsync(seg, seg.Source, target, open, _doc.SourceLanguage, _doc.TargetLanguage);
        lock (_feedbackRuns)
        {
            _feedbackRuns.RemoveAll(t => t.IsCompleted);
            _feedbackRuns.Add(run);
        }
    }

    /// <summary>Umformuliert: Die KI sagt, welche Wörter den Ausdruck wiedergeben; nur am Text Bestätigtes zählt.</summary>
    private async Task AssignRephrasedAsync(SegmentViewModel seg, string source, string target, List<(TermDossier Dossier, string Key)> open,
        string from, string to)
    {
        try
        {
            if (open.Any(o => !_termUses.ContainsKey((o.Key, target))))
            {
                if (TermUseOverride == null && AppServices.Ai.Get(AppServices.Ai.EvaluateProviderId) == null) return;
                await _feedbackGate.WaitAsync();
                try
                {
                    // erst hier: was ein gleichzeitiges Bestätigen schon zugeordnet hat, nicht noch einmal fragen
                    var todo = open.Where(o => !_termUses.ContainsKey((o.Key, target))).ToList();
                    var dossiers = todo.Select(o => o.Dossier).ToList();
                    var uses = todo.Count == 0 ? new Dictionary<TermDossier, TermUse>()
                        : TermUseOverride != null ? await TermUseOverride(source, target, dossiers, CancellationToken.None)
                        : await RecommendationUse.AskAsync(CreateContext(AppServices.Ai.EvaluateProviderId, L.T("Rückmeldung")), source, target, dossiers,
                            from, to, CancellationToken.None);
                    foreach (var o in todo) _termUses[(o.Key, target)] = uses.GetValueOrDefault(o.Dossier);
                }
                finally { _feedbackGate.Release(); }
            }
            var changed = false;
            foreach (var (d, key) in open)
                if (_termUses.GetValueOrDefault((key, target)) is { } use) changed |= ApplyTermUse(seg, d, key, use);
            if (changed) RefreshFeedbackSummary();
        }
        catch (Exception ex)
        {
            // keine Zuordnung, keine Rückmeldung – wie früher bei umformulierten Sätzen
            AiLog += L.T($"Rückmeldung Segment {seg.Id}: KI-Zuordnung nicht möglich – ") + ex.Message + "\n";       // nur ins Protokoll
        }
    }

    /// <summary>Übernommen oder ersetzt festhalten; umschrieben nimmt eine frühere Rückmeldung zum selben Ausdruck zurück.</summary>
    private bool ApplyTermUse(SegmentViewModel seg, TermDossier d, string key, TermUse use)
    {
        bool changed;
        try { changed = use.Accepted is { } a ? ResearchFeedback.Record(key, d, a, use.Rendering, use.How) : ResearchFeedback.Withdraw(key); }
        catch (IOException) { return false; }
        if (changed || use.How == "KI-Zuordnung")
        {
            var line = L.T($"Rückmeldung Segment {seg.Id}: „{d.Expression}“ – {RecommendationUse.Describe(use)}");
            // gehört das Segment noch zum offenen Dokument (die Zuordnung läuft im Hintergrund), auch als Meldung dort
            if (_all.Contains(seg)) LogFor(seg, "Rückmeldung", "Hinweis", line);
            else AiLog += line + "\n";
        }
        return changed;
    }

    /// <summary>Ein Prüfer erfasst einen Terminologie-Fehler: die Empfehlung, die in der Übersetzung steht und gemeint ist, gilt als ersetzt.</summary>
    private void RecordTermError(SegmentViewModel seg, Core.Qa.LqaError error)
    {
        if (_doc == null || Project == null || error.Reviewer == QualityEvaluator.Reviewer
            || !error.Category.StartsWith("Terminologie", StringComparison.Ordinal)) return;
        var target = TagText.StripTags(seg.Model.Target);
        var changed = false;
        foreach (var d in DossierStore.ForText(_dossiers, seg.Source, _doc.TargetLanguage).Where(d => d.Recommended is { Length: > 1 }))
        {
            // gemeint, wenn die Anmerkung die Benennung oder den Ausdruck nennt – oder ohne Anmerkung, wenn es der einzige ist
            var named = RecommendationUse.Appears(error.Note, d.Recommended!) || DocumentResearch.Contains(error.Note, d.Expression);
            if (!RecommendationUse.Appears(target, d.Recommended!) || !named && error.Note.Trim().Length > 0) continue;
            try { changed |= ResearchFeedback.Record(FeedbackKey(seg, d), d, false, d.Recommended, "Prüfer"); }
            catch (IOException) { }
        }
        if (changed) RefreshFeedbackSummary();
    }
}
