using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Segment in der Übersicht „Was die KI gesammelt hat“: Kurzfassung, Klick zeigt die Einzelheiten.</summary>
public sealed class SegmentFindingRow(SegmentViewModel segment, string summary)
{
    public SegmentViewModel Segment { get; } = segment;
    public string Header => L.T($"Segment {Segment.Id}");
    public string Summary { get; } = summary;
}

/// <summary>
/// Formulierungen prüfen und verbessern: Jede Übersetzung wird im Netz der Zielsprache geprüft – sind Satzbau und
/// Wortverbindungen üblich, werden die Wörter so verwendet? –, mit der Recherche zu jedem Teil des Satzes; Fragliches
/// ersetzt die bessere Fassung (als Entwurf). Was die KI dabei gesammelt hat, steht je Segment im Reiter KI (beim
/// Anklicken) und für alle Segmente in einer Übersicht – mit Erklärungen, Zahlen und Belegen.
/// </summary>
public partial class EditorViewModel
{
    /// <summary>Für Tests: statt der echten Netzprüfung ein Ergebnis liefern (Quelle, Übersetzung, Recherche).</summary>
    public static Func<string, string, IReadOnlyList<TermDossier>, CancellationToken, Task<TranslationWebCheckResult>>? WebCheckOverride { get; set; }

    public ObservableCollection<SegmentFindingRow> SegmentFindings { get; } = new();
    public bool HasSegmentFindings => SegmentFindings.Count > 0;

    /// <summary>Netzprüfungen dieser Sitzung je Segment (für den Bericht): Ergebnis und die Fassung vor der Verbesserung.</summary>
    private readonly Dictionary<int, (TranslationWebCheckResult Check, string? Before)> _wordingChecks = new();

    /// <summary>Neues Dokument: Übersicht und Prüfungen gehören zum vorigen.</summary>
    private void ClearFindings()
    {
        SegmentFindings.Clear();
        _wordingChecks.Clear();
        OnPropertyChanged(nameof(HasSegmentFindings));
    }

    /// <summary>
    /// Ein Segment nach der Übersetzung: (falls gewünscht) seine Wendungen im Netz prüfen und Fragliches verbessern, dann
    /// Recherche und Prüfung als Bericht beim Segment ablegen und in die Übersicht eintragen.
    /// </summary>
    private async Task CheckAndImproveAsync(SegmentViewModel seg, BatchResearchState state, CancellationToken ct)
    {
        var to = ResearchTo ?? state.Doc.TargetLanguage;
        var research = DossierStore.ForText(state.Dossiers, seg.Source, to);
        TranslationWebCheckResult? check = null;
        string? before = null;
        if (state.WebCheck && TagText.StripTags(seg.Model.Target).Trim().Length > 0)
        {
            _main.Status = L.T($"Prüfe die Formulierungen von Segment {seg.Id} im Netz …");
            try
            {
                var target = seg.Model.Target;
                check = await WithinLimitAsync(limit => WebCheckOverride != null ? WebCheckOverride(seg.Source, target, research, limit)
                    : new TranslationWebCheck(state.CheckContext ??= CreateContext(AppServices.Ai.ResearchProviderId))
                        .RunAsync(seg.Source, target, state.Doc.SourceLanguage, to, state.Project.Topic, limit, research),
                    $"Segment {seg.Id}: Netzprüfung", state.StepLimit, ct);
                if (check != null) state.Checked++;
                if (check?.Improved is { Length: > 0 } better)
                {
                    before = TagText.StripTags(seg.Model.Target);
                    seg.Model.Target = TagText.Repair(seg.Source, better);
                    seg.Model.Origin = TranslationOrigin.Ai;
                    seg.Model.Suggestion = seg.Model.Target;
                    seg.Model.Status = SegmentStatus.Draft;
                    seg.Refresh();
                    state.Improved++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFor(seg, "Netzprüfung", "Warnung", L.T($"Segment {seg.Id}: Netzprüfung nicht möglich – {ex.Message}"));
            }
        }
        if (check != null)
        {
            _wordingChecks[seg.Id] = (check, before);
            var doubtful = check.Phrases.Count(p => p.Verdict is PhraseVerdict.Questionable or PhraseVerdict.Wrong);
            LogFor(seg, "Netzprüfung", doubtful > 0 && before == null ? "Warnung" : "Hinweis", FindingSummary([], check, before));
        }
        if (research.Count == 0 && check == null) return;
        var result = new AiSegmentResult(SegmentReport(seg, research, check, before, to), [], null, null, state.Project.Topic);
        _aiResults[seg] = result;
        if (Selected == seg) ApplyAiResult(result);
        if (SegmentFindings.FirstOrDefault(f => f.Segment == seg) is { } old) SegmentFindings.Remove(old);
        var row = new SegmentFindingRow(seg, FindingSummary(research, check, before));
        var at = SegmentFindings.TakeWhile(f => f.Segment.Id < seg.Id).Count();
        SegmentFindings.Insert(at, row);
        OnPropertyChanged(nameof(HasSegmentFindings));
    }

    /// <summary>Kurzfassung für die Übersicht aller Segmente.</summary>
    private static string FindingSummary(IReadOnlyList<TermDossier> research, TranslationWebCheckResult? check, string? before)
    {
        var parts = new List<string>();
        if (research.Count > 0)
            parts.Add(L.T($"{research.Count} Ausdrücke recherchiert ({research.Count(DossierStore.IsEvidenced)} belegt): ")
                      + string.Join(", ", research.Take(4).Select(d => $"{d.Expression} → {d.Recommended ?? "?"}")));
        if (check != null)
        {
            var doubtful = check.Phrases.Count(p => p.Verdict is PhraseVerdict.Questionable or PhraseVerdict.Wrong);
            parts.Add(doubtful == 0 ? L.T($"Netzprüfung: {check.Phrases.Count} Wendungen üblich")
                : L.T($"Netzprüfung: {doubtful} von {check.Phrases.Count} Wendungen fraglich") + (before != null ? " – " + L.T("verbessert") : ""));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Bericht beim Segment: Recherche zu jedem Teil (mit Bedeutung, Begründung, Quelle) und die Netzprüfung.</summary>
    private static string SegmentReport(SegmentViewModel seg, IReadOnlyList<TermDossier> research, TranslationWebCheckResult? check, string? before,
        string targetLanguage)
    {
        var sb = new StringBuilder();
        if (research.Count > 0)
        {
            sb.AppendLine(L.T("Recherche zu diesem Segment"));
            foreach (var d in research)
            {
                sb.AppendLine($"• {d.Expression} → {d.Recommended ?? "?"} (" +
                              L.T(DossierStore.IsEvidenced(d) ? "belegt – ging in die Übersetzung ein" : "nicht belegt – nur als Hinweis für die Prüfung") + ")");
                if (AiTranslator.Short(d.Meaning, 300) is { } meaning) sb.AppendLine("    " + L.T("Bedeutung") + ": " + meaning);
                if (AiTranslator.Short(d.Reasoning, 400) is { } why) sb.AppendLine("    " + L.T("Begründung") + ": " + why);
                foreach (var s in d.Sources.Take(2))
                    sb.AppendLine("    " + L.T("Quelle") + $": {s.Title} – {Core.Ai.SourceLink.WithTextFragment(s.Url, s.Quote)}");
            }
            sb.AppendLine();
        }
        if (before != null)
        {
            sb.AppendLine(L.T("Verbessert") + $": „{before}“ → „{TagText.StripTags(seg.Model.Target)}“");
            sb.AppendLine();
        }
        if (check != null) sb.Append(FormatWebCheck(check, targetLanguage));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formulierungen verbessern: das gewählte Segment (<paramref name="scope"/> „selected“) oder alle übersetzten,
    /// nicht bestätigten Segmente (<c>„all“</c>) im Netz prüfen und Fragliches durch die bessere Fassung ersetzen.
    /// </summary>
    [RelayCommand]
    private async Task ImproveWording(string? scope)
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        var all = scope == "all";
        var targets = all
            ? _all.Where(s => !s.IsLocked && !s.Model.IsConfirmed && TagText.StripTags(s.Target).Trim().Length > 0).ToList()
            : Selected is { } sel && TagText.StripTags(sel.Target).Trim().Length > 0 ? [sel] : [];
        if (targets.Count == 0)
        {
            _main.Status = L.T(all ? "Keine übersetzten, unbestätigten Segmente zum Verbessern." : "Das gewählte Segment hat noch keine Übersetzung.");
            return;
        }
        if (!all) SidePanelTab = 7;
        IsAiRunning = true;
        _batchRunning = true;
        AiLog = "";
        var cts = _batchCts = new CancellationTokenSource();
        var project = Project;
        var state = new BatchResearchState(project, _doc, DossierStore.Load(project), _brief) { WebCheck = true };
        try
        {
            foreach (var seg in targets)
            {
                cts.Token.ThrowIfCancellationRequested();
                await CheckAndImproveAsync(seg, state, cts.Token);
            }
            _main.Status = L.T($"Formulierungen geprüft: {state.Checked} Segmente, {state.Improved} verbessert.");
        }
        catch (OperationCanceledException) { _main.Status = L.T($"Abgebrochen – {state.Checked} Segmente geprüft, {state.Improved} verbessert."); }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally
        {
            IsAiRunning = false;
            _batchRunning = false;
            SaveIfOpen();
        }
    }

    /// <summary>
    /// Bericht als HTML-Seite im Projektordner („berichte“): welche Sätze und Ausdrücke wie übersetzt wurden und warum –
    /// Recherche mit Bedeutung, Begründung und Belegen, Netzprüfung mit Zahlen und Urteil, Verbesserungen.
    /// </summary>
    [RelayCommand]
    private async Task CreateReport()
    {
        if (_doc == null || Project == null || CurrentFile == null) return;
        var to = _doc.TargetLanguage;
        var dossiers = DossierStore.Load(Project);
        var segments = _all.Select(s =>
        {
            var c = _wordingChecks.TryGetValue(s.Id, out var found) ? found : default;
            return new Core.Exchange.ReportSegment(s.Id, TagText.StripTags(s.Source), TagText.StripTags(s.Model.Target), s.Model.Origin,
                s.Model.MatchPercent, DossierStore.ForText(dossiers, s.Source, to), c.Check, c.Before);
        }).ToList();
        var name = System.IO.Path.GetFileNameWithoutExtension(CurrentFile.OriginalName);
        var title = L.T($"Übersetzungsbericht – {name}");
        var html = Core.Exchange.TranslationReport.Html(title, _doc.SourceLanguage, to, segments, ResearchEngine, L.T);
        try
        {
            var folder = System.IO.Path.Combine(Project.Folder, "berichte");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"Bericht {name} {to} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.html");
            await System.IO.File.WriteAllTextAsync(path, html);
            LastReportPath = path;
            _main.Status = L.T($"Bericht erstellt: {path}");
            if (AppServices.Platform != null) await AppServices.Platform.OpenFileAsync(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _main.Status = L.T("Bericht nicht gespeichert: ") + ex.Message;
        }
    }

    /// <summary>Wo der zuletzt erstellte Bericht liegt.</summary>
    public string? LastReportPath { get; private set; }

    /// <summary>In der Übersicht angeklickt: zum Segment springen und die Einzelheiten im Reiter KI zeigen.</summary>
    [RelayCommand]
    private void OpenFinding(SegmentFindingRow? row)
    {
        if (row == null) return;
        Selected = row.Segment;
        SidePanelTab = 7;
    }
}
