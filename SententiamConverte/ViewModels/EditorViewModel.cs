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
    /// <summary>Fuzzy-Treffer, den die KI gezielt anpassen kann.</summary>
    public bool CanRepair => Match.Score is >= Core.Agents.FuzzyRepair.MinScore and < 100;
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

public enum SegmentFilter { All, Open, Unconfirmed, WithIssues, Changed, WithErrors }

/// <summary>Ein erfasster Fehler des gewählten Segments.</summary>
public sealed record LqaErrorRow(Core.Qa.LqaError Error)
{
    public string Text => (Error.Reviewer == Core.Agents.QualityEvaluator.Reviewer ? L.T("KI-Vorschlag") + ": " : "") +
                          $"{L.T(Error.Category)} · {L.T(Core.Qa.Lqa.SeverityName(Error.Severity))}" + (Error.Note.Length > 0 ? " – " + Error.Note : "");
}

public partial class EditorViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public MainViewModel Main => _main;
    private BilingualDocument? _doc;
    private List<SegmentViewModel> _all = new();
    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _batchCts;

    public EditorViewModel(MainViewModel main)
    {
        _main = main;
        // die Meldungsliste folgt allem, was QA und Recherche dazu beitragen
        QaIssues.CollectionChanged += (_, _) => ScheduleMessagesRefresh();
        ResearchItems.CollectionChanged += (_, _) => ScheduleMessagesRefresh();
    }

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

    public IReadOnlyList<string> FilterNames { get; } = new[] { "Alle Segmente", "Nicht übersetzt", "Nicht bestätigt", "Mit QA-Meldungen", "Vom Prüfer geändert", "Mit Fehlerbewertung" }.Select(L.T).ToList();

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

    // ---------- Darstellung wie in einer Textverarbeitung ----------

    /// <summary>Schriftgröße der Segmenttexte (Zoom).</summary>
    [ObservableProperty] public partial double EditorFontSize { get; set; } = 15;

    /// <summary>Rechtschreibfehler beim Tippen unterstreichen.</summary>
    [ObservableProperty] public partial bool SpellCheckEnabled { get; set; } = true;

    /// <summary>Quelle oben, Ziel darunter (interlinear); der Quelltext bleibt auswählbar, das Ziel bearbeitbar.</summary>
    [ObservableProperty] public partial bool Interlinear { get; set; } = AppServices.Ui.EditorInterlinear;

    // Lage der Spalten im Segment: nebeneinander Quelle | Ziel, untereinander beide über die volle Breite
    public int TargetRow => Interlinear ? 1 : 0;
    public int TargetColumn => Interlinear ? 2 : 3;
    public int TextSpan => Interlinear ? 2 : 1;

    partial void OnInterlinearChanged(bool value)
    {
        OnPropertyChanged(nameof(TargetRow));
        OnPropertyChanged(nameof(TargetColumn));
        OnPropertyChanged(nameof(TextSpan));
        AppServices.Ui.EditorInterlinear = value;
        AppServices.Ui.Save();
    }

    [ObservableProperty] public partial string? SourceLanguage { get; set; }
    [ObservableProperty] public partial string? TargetLanguage { get; set; }

    /// <summary>Für die Zielsprache ist kein Wörterbuch geladen – der Editor bietet es an.</summary>
    [ObservableProperty] public partial bool DictionaryMissing { get; set; }
    [ObservableProperty] public partial string DictionaryInfo { get; set; } = "";

    /// <summary>Die gewählte KI ist die eingebaute, aber noch nicht heruntergeladen – der Editor bietet den Download an.</summary>
    [ObservableProperty] public partial bool AiMissing { get; set; }
    [ObservableProperty] public partial string AiInfo { get; set; } = "";
    public AiDownloadService AiDownload => AppServices.AiDownload;

    /// <summary>Prüfen, ob die KI für Übersetzungen bereitsteht (beim Öffnen und nach Änderungen in den Einstellungen).</summary>
    [RelayCommand]
    private void GoToProject() => _main.SelectedTab = 0;

    /// <summary>Markierten Text unter „Hilfsmittel“ nachschlagen (Sprache der Spalte).</summary>
    public void LookUp(string term, bool inTarget)
    {
        if (_doc == null) return;
        _main.Tools.Prepare(term, inTarget ? _doc.TargetLanguage : _doc.SourceLanguage);
        _main.SelectedTab = MainViewModel.ToolsTab;
    }

    // ---------- Team: Arbeitsschritte ----------

    /// <summary>Die Datei ist gerade nur lesbar (gemeinsames Projekt, jemand anderes ist an der Reihe).</summary>
    [ObservableProperty] public partial bool DocumentReadOnly { get; set; }
    [ObservableProperty] public partial string WorkflowNote { get; set; } = "";

    /// <summary>Arbeitsschritt der offenen Datei anwenden: nur lesbar, wenn jemand anderes an der Reihe ist.</summary>
    public void ApplyTeamStep()
    {
        var step = CurrentFile != null && _doc != null ? _main.TeamStepFor(CurrentFile.Id, _doc.TargetLanguage) : null;
        var mayEdit = step == null || step.MayEdit(_main.TeamMe, _main.IsTeamAdmin);
        DocumentReadOnly = !mayEdit;
        WorkflowNote = step == null || (step.Assignee == null && step.Step != Core.Remote.WorkflowStep.Done) ? ""
            : mayEdit ? $"Schritt „{TeamStepRow.StepName(step.Step)}“ – Sie sind an der Reihe. Wenn Sie fertig sind: im Reiter Projekt „Abgeben“."
            : step.Step == Core.Remote.WorkflowStep.Done ? "Diese Datei ist abgeschlossen – nur Lesen."
            : $"Diese Datei ist im Schritt „{TeamStepRow.StepName(step.Step)}“ bei {step.Assignee} – nur Lesen.";
        foreach (var s in _all) s.RefreshReadOnly();
    }

    private bool CheckWritable()
    {
        if (!DocumentReadOnly) return true;
        _main.Status = WorkflowNote;
        return false;
    }

    // ---------- Diktat ----------

    public bool DictationAvailable => AppServices.Recorder is { IsAvailable: true };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DictationLabel))]
    public partial bool IsDictating { get; set; }
    public string DictationLabel => IsDictating ? L.T("⏹ Diktat beenden") : L.T("🎤 Diktat");

    /// <summary>
    /// Diktat ein/aus (Strg+Umschalt+D): beim Beenden wird die Aufnahme erkannt und der Text zurückgegeben, den die
    /// Oberfläche an der Schreibmarke einsetzt.
    /// </summary>
    public async Task<string?> ToggleDictationAsync()
    {
        if (AppServices.Recorder is not { } recorder) return null;
        if (!recorder.IsAvailable) { _main.Status = recorder.Hint; return null; }
        var config = AppServices.Ai.Transcription;
        if (!IsDictating)
        {
            if (!config.IsConfigured)
            {
                _main.Status = "Für das Diktat zuerst unter „KI einrichten“ → Diktat eine Spracherkennung wählen.";
                return null;
            }
            try
            {
                recorder.Start();
                IsDictating = true;
                _main.Status = "Diktat läuft – sprechen, dann erneut klicken (Strg+Umschalt+D).";
            }
            catch (InvalidOperationException ex) { _main.Status = ex.Message; }
            return null;
        }
        IsDictating = false;
        try
        {
            var wav = await recorder.StopAsync();
            if (wav.Length < 1000) { _main.Status = "Keine Aufnahme – war das Mikrofon an?"; return null; }
            _main.Status = "Erkenne Sprache …";
            var text = await Core.Ai.Transcriber.TranscribeAsync(wav, TargetLanguage, config);
            _main.Status = text.Length == 0 ? "Nichts erkannt." : "Diktat eingefügt.";
            return text;
        }
        catch (Exception ex) when (ex is Core.Ai.AiException or System.Net.Http.HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _main.Status = "Diktat: " + ex.Message;
            return null;
        }
    }

    // ---------- Lektorat: Änderungsverfolgung und Fehlerbewertung ----------

    /// <summary>Änderungen an bestätigten Segmenten verfolgen (die bestätigte Fassung bleibt sichtbar).</summary>
    [ObservableProperty] public partial bool TrackChangesOn { get; set; } = AppServices.Ui.EditorTrackChanges;
    [ObservableProperty] public partial bool ShowChanges { get; set; } = true;

    partial void OnTrackChangesOnChanged(bool value)
    {
        AppServices.Ui.EditorTrackChanges = value;
        AppServices.Ui.Save();
    }

    public int ChangeCount => _all.Count(s => s.HasChange);

    private void AfterReview(string message)
    {
        SaveIfOpen();
        OnPropertyChanged(nameof(ChangeCount));
        UpdateLqa();
        ApplyFilterKeepingSelection();
        ScheduleMessagesRefresh();
        _main.Status = message;
    }

    private void ApplyFilterKeepingSelection()
    {
        if ((SegmentFilter)FilterIndex is not (SegmentFilter.Changed or SegmentFilter.WithErrors)) return;
        var selected = Selected;
        ApplyFilter();
        if (selected != null && Segments.Contains(selected)) Selected = selected;
    }

    [RelayCommand]
    private void AcceptChange()
    {
        if (Selected is not { HasChange: true } s) return;
        Core.Qa.TrackChanges.Accept(s.Model);
        s.Refresh();
        AfterReview("Änderung angenommen.");
    }

    [RelayCommand]
    private void RejectChange()
    {
        if (Selected is not { HasChange: true } s) return;
        Core.Qa.TrackChanges.Reject(s.Model);
        s.Refresh();
        AfterReview("Änderung abgelehnt – die vorige Fassung gilt wieder.");
    }

    [RelayCommand]
    private void AcceptAllChanges()
    {
        var changed = _all.Where(s => s.HasChange).ToList();
        foreach (var s in changed) { Core.Qa.TrackChanges.Accept(s.Model); s.Refresh(); }
        AfterReview($"{changed.Count} Änderungen angenommen.");
    }

    [RelayCommand]
    private void RejectAllChanges()
    {
        var changed = _all.Where(s => s.HasChange).ToList();
        foreach (var s in changed) { Core.Qa.TrackChanges.Reject(s.Model); s.Refresh(); }
        AfterReview($"{changed.Count} Änderungen abgelehnt.");
    }

    public IReadOnlyList<string> LqaCategories { get; } = Core.Qa.Lqa.Categories.Select(L.T).ToList();
    private static readonly Core.Qa.LqaSeverity[] SeverityOrder = [Core.Qa.LqaSeverity.Minor, Core.Qa.LqaSeverity.Major, Core.Qa.LqaSeverity.Critical, Core.Qa.LqaSeverity.Neutral];
    public IReadOnlyList<string> LqaSeverities { get; } = SeverityOrder.Select(s => L.T(Core.Qa.Lqa.SeverityName(s))).ToList();
    [ObservableProperty] public partial int LqaCategoryIndex { get; set; }
    [ObservableProperty] public partial int LqaSeverityIndex { get; set; }
    [ObservableProperty] public partial string LqaNote { get; set; } = "";
    [ObservableProperty] public partial string LqaSummary { get; set; } = "";
    public ObservableCollection<LqaErrorRow> SegmentErrors { get; } = new();

    public double LqaPassScore
    {
        get => Project?.Lqa.PassScore ?? 98;
        set
        {
            if (Project == null) return;
            Project.Lqa.PassScore = Math.Clamp(value, 0, 100);
            Project.Save();
            UpdateLqa();
        }
    }

    private void FillErrors(SegmentViewModel? seg)
    {
        SegmentErrors.Clear();
        foreach (var e in seg?.Model.Errors ?? []) SegmentErrors.Add(new LqaErrorRow(e));
    }

    private void UpdateLqa()
    {
        if (_doc == null || Project == null) { LqaSummary = ""; return; }
        var r = Core.Qa.Lqa.Evaluate([_doc], Project.Lqa);
        LqaSummary = r.Errors == 0
            ? "Noch keine Fehler erfasst."
            : $"{r.Errors} Fehler, {r.Points} Strafpunkte bei {r.Words} Wörtern → {r.Score:0.0} von 100 (bestanden ab {r.PassScore:0.#}): " +
              (r.Passed ? "bestanden" : "nicht bestanden");
        OnPropertyChanged(nameof(LqaPassScore));
        RefreshErrorList();
    }

    /// <summary>Fertige Übersetzung von der KI bewerten lassen: Fehlervorschläge (Prüfer „KI“) in allen übersetzten Segmenten.</summary>
    [RelayCommand]
    private async Task EvaluateDocumentWithAi()
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        IsAiRunning = true;
        AiLog = "";
        var startedOn = SidePanelTab;
        try
        {
            _main.Status = "KI bewertet die Übersetzung …";
            // frühere KI-Vorschläge ersetzen, Fehler menschlicher Prüfer bleiben
            var count = await EvaluateCoreAsync(_all, CancellationToken.None);
            AfterReview(count == 0 ? "KI-Bewertung: keine Fehler gefunden." : $"KI-Bewertung: {count} Fehler vorgeschlagen (Prüfer „KI“) – bitte prüfen.");
            ShowResultTab(6, startedOn);
        }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    /// <summary>Alle Fehlervorschläge der KI wieder entfernen.</summary>
    [RelayCommand]
    private void RemoveAiErrors()
    {
        var removed = 0;
        foreach (var s in _all)
        {
            removed += s.Model.Errors?.RemoveAll(e => e.Reviewer == Core.Agents.QualityEvaluator.Reviewer) ?? 0;
            if (s.Model.Errors is { Count: 0 }) s.Model.Errors = null;
            s.Refresh();
        }
        FillErrors(Selected);
        AfterReview($"{removed} Fehlervorschläge der KI entfernt.");
    }

    [RelayCommand]
    private void AddLqaError()
    {
        if (Selected is not { } s || LqaCategoryIndex < 0) return;
        var error = new Core.Qa.LqaError(Core.Qa.Lqa.Categories[LqaCategoryIndex], SeverityOrder[Math.Max(0, LqaSeverityIndex)], LqaNote.Trim(),
            Environment.UserName, DateTime.UtcNow);
        (s.Model.Errors ??= new()).Add(error);
        SegmentErrors.Add(new LqaErrorRow(error));
        RecordTermError(s, error);
        LqaNote = "";
        s.Refresh();
        AfterReview($"Fehler in Segment {s.Id} erfasst.");
    }

    [RelayCommand]
    private void RemoveLqaError(LqaErrorRow? row)
    {
        if (Selected is not { } s || row == null || s.Model.Errors == null) return;
        s.Model.Errors.Remove(row.Error);
        if (s.Model.Errors.Count == 0) s.Model.Errors = null;
        SegmentErrors.Remove(row);
        s.Refresh();
        AfterReview("Fehler entfernt.");
    }

    [RelayCommand]
    private async Task ExportLqaReport()
    {
        if (_doc == null || Project == null) return;
        SaveIfOpen();
        var name = System.IO.Path.GetFileNameWithoutExtension(_doc.FileName) + "_" + _doc.TargetLanguage + L.T("_Bewertung") + ".docx";
        await using var output = await AppServices.Platform!.SaveFileAsync("Bewertungsbericht speichern", name);
        if (output == null) return;
        Core.Qa.Lqa.ExportDocx(_doc.FileName + " (" + _doc.TargetLanguage + ")", [_doc], Project.Lqa, output, L.DocumentLanguage);
        _main.Status = $"Bewertungsbericht „{name}“ gespeichert.";
    }

    /// <summary>Höchstlänge: eigene des Segments (z. B. XLIFF „maxwidth“), sonst die des Projekts.</summary>
    private int? LimitOf(Segment s) => s.MaxLength ?? (Project?.Qa.MaxChars is > 0 and var m ? m : null);

    /// <summary>Zahlen und Datum in die Schreibweise der Zielsprache bringen, wenn das Projekt es wünscht.</summary>
    private void AutoLocalize(SegmentViewModel seg)
    {
        if (Project?.AutoLocalize != true || _doc == null || seg.Model.Target.Length == 0) return;
        seg.Model.Target = Core.Tm.AutoLocalizer.Localize(seg.Model.Source, seg.Model.Target, _doc.SourceLanguage, _doc.TargetLanguage);
    }

    public void CheckAi()
    {
        OnPropertyChanged(nameof(ResearchEngine));
        var ai = AppServices.Ai;
        var model = ai.Get(ai.TranslateProviderId) is { Kind: Core.Ai.AiProviderKind.BuiltIn } p ? Core.Ai.BuiltInAi.Model(p.Model) : null;
        AiMissing = model != null && !Core.Ai.BuiltInAi.IsInstalled(model);
        AiInfo = model == null || !AiMissing ? "" :
            $"Die kostenlose KI ({model.Name}) ist noch nicht heruntergeladen: einmalig {Core.Ai.BuiltInAi.FormatSize(Core.Ai.BuiltInAi.MissingBytes(model))}, " +
            "danach übersetzt sie ohne Konto und offline auf diesem Rechner.";
    }

    [RelayCommand]
    private async Task DownloadAi()
    {
        var ai = AppServices.Ai;
        if (ai.Get(ai.TranslateProviderId) is not { Kind: Core.Ai.AiProviderKind.BuiltIn } p || Core.Ai.BuiltInAi.Model(p.Model) is not { } model) return;
        if (await AppServices.AiDownload.InstallAsync(model)) _main.Status = $"{model.Name} ist bereit.";
        CheckAi();
    }

    [RelayCommand]
    private void OpenAiSettings() => _main.SelectedTab = MainViewModel.AiTab;

    [RelayCommand]
    private void ZoomIn() => EditorFontSize = Math.Min(EditorFontSize + 1, 28);

    [RelayCommand]
    private void ZoomOut() => EditorFontSize = Math.Max(EditorFontSize - 1, 10);

    private void CheckDictionary(string language)
    {
        var spell = AppServices.Spell;
        DictionaryMissing = !spell.IsAvailable(language) && SententiamConverte.Core.Qa.SpellChecker.DownloadableLanguages.Any(l => Core.Model.Languages.Matches(l, language));
        DictionaryInfo = DictionaryMissing ? $"Für {language} ist noch kein Wörterbuch geladen – ohne Wörterbuch werden keine Rechtschreibfehler angezeigt." : "";
        if (!DictionaryMissing) _ = spell.PreloadAsync(language);
    }

    /// <summary>Freies Wörterbuch der Zielsprache laden (LibreOffice-Projekt) und sofort prüfen.</summary>
    [RelayCommand]
    private async Task DownloadDictionary()
    {
        if (TargetLanguage is not { } lang) return;
        DictionaryInfo = "Lade Wörterbuch …";
        try
        {
            await AppServices.Spell.DownloadAsync(lang);
            await AppServices.Spell.PreloadAsync(lang);
            CheckDictionary(lang);
            var current = TargetLanguage;
            TargetLanguage = null;                        // Editoren prüfen neu
            TargetLanguage = current;
            _main.Status = $"Wörterbuch {lang} geladen.";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or NotSupportedException or TaskCanceledException)
        {
            DictionaryInfo = "Fehler: " + ex.Message;
        }
    }

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
        SourceLanguage = doc.SourceLanguage;
        TargetLanguage = doc.TargetLanguage;
        CheckDictionary(doc.TargetLanguage);
        CheckAi();
        CanResegment = Workflow.CanResegment(doc) && !project.IsShared;
        StartSync(project, file, doc);
        _all = doc.Paragraphs.SelectMany(p => p.Segments.Select(s => new SegmentViewModel(s, p.Context, p.TagData) { Limit = LimitOf(s), IsTracking = () => TrackChangesOn, DocumentReadOnly = () => DocumentReadOnly })).ToList();
        ApplyFilter();
        UpdateProgress();
        UpdateLqa();
        OnPropertyChanged(nameof(ChangeCount));
        ApplyTeamStep();
        LoadBrief();
        AutoReadBrief();                                         // fehlt der Steckbrief, liest die KI im Hintergrund
        RefreshSensitive();                                      // 🔒 an Segmenten mit geschützten Angaben
        QaIssues.Clear();                                        // die Meldungen gehören zum vorigen Dokument
        LoadResearch();
        LoadNotes();                                             // gespeicherte Meldungen der KI, frühere KI-Prüfungen
        _aiResults.Clear();                                      // KI-Ergebnisse gehören zu den Segmenten des vorigen Dokuments
        ClearFindings();
        ClearReport();
        ClearMarked();
        _markCache.Clear();
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

    /// <summary>
    /// Laufende KI-Arbeit anhalten und warten, bis sie steht – vor einem Projektwechsel. Sonst schriebe eine Übersetzung
    /// oder Recherche des alten Dokuments ihre Meldungen und Ergebnisse womöglich ins neue Projekt. Was fertig ist, bleibt:
    /// eine angehaltene Übersetzung speichert ihren Stand, solange das Dokument noch offen ist. False, wenn sie nicht
    /// rechtzeitig anhält (dann nicht wechseln).
    /// </summary>
    public async Task<bool> StopWorkAsync(TimeSpan wait)
    {
        if (!IsAiWorking && _researchWorker == null) return true;
        _researchQueue.Clear();
        _batchCts?.Cancel();
        _researchCts?.Cancel();
        _lookupCts?.Cancel();
        _markCts?.Cancel();
        var until = DateTime.UtcNow + wait;
        while ((IsAiWorking || _researchWorker != null) && DateTime.UtcNow < until) await Task.Delay(50);
        return !IsAiWorking && _researchWorker == null;
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
        _notes.Clear();
        Messages.Clear();
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
        var keep = Selected;
        Segments.Clear();
        foreach (var s in _all)
        {
            var show = filter switch
            {
                SegmentFilter.Open => !s.IsLocked && s.Target.Length == 0,
                SegmentFilter.Unconfirmed => !s.IsLocked && !s.Model.IsConfirmed,
                SegmentFilter.WithIssues => withIssues.Contains(s.Id),
                SegmentFilter.Changed => s.HasChange,
                SegmentFilter.WithErrors => s.HasErrors,
                _ => true,
            };
            if (show) Segments.Add(s);
        }
        // gewähltes Segment bleibt gewählt, wenn es weiter zu sehen ist
        if (keep != null && Segments.Contains(keep) && Selected != keep) Selected = keep;
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
        ClearMarkedFor(value);                                   // die Erklärung einer Markierung gehört zum vorigen Segment
        RefreshDossiers();
        if (MessagesOnlySelected) RefreshMessages();             // Reiter „Meldungen“: die des gewählten Segments
        if (SidePanelTab == ResearchTab) _ = StudySelectedSegmentAsync();     // Recherche-Reiter offen: Ausdrücke des Segments klären
        _lookupCts?.Cancel();
        Matches.Clear();
        Terms.Clear();
        Fragments.Clear();
        SpellingErrors.Clear();
        Knowledge.Clear();
        _suggestions = new();
        ApplyAiResult(value != null ? _aiResults.GetValueOrDefault(value) : null);   // je Segment sein eigenes Ergebnis
        OnPropertyChanged(nameof(Comment));
        FillHistory(value);
        FillErrors(value);
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
                AutoLocalize(seg);
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
        AutoLocalize(Selected);
        Selected.Refresh();
    }

    // ---------- Fuzzy-Treffer gezielt anpassen ----------

    /// <summary>Zuletzt angepasster Treffer: vorher (TM) und nachher (angepasst) zum Hervorheben der Änderung.</summary>
    [ObservableProperty] public partial string? RepairBefore { get; set; }
    [ObservableProperty] public partial string? RepairAfter { get; set; }
    [ObservableProperty] public partial string RepairNote { get; set; } = "";

    public bool HasRepair => RepairAfter != null;
    partial void OnRepairAfterChanged(string? value) => OnPropertyChanged(nameof(HasRepair));

    public bool RepairFuzzyMatches
    {
        get => AppServices.Ui.RepairFuzzyMatches;
        set
        {
            if (AppServices.Ui.RepairFuzzyMatches == value) return;
            AppServices.Ui.RepairFuzzyMatches = value;
            AppServices.Ui.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Sprachmodell zum Anpassen (eine klassische maschinelle Übersetzung kann keine Anweisungen befolgen).</summary>
    private static bool CanFollowInstructions(AgentContext ctx) => Core.Privacy.PrivacyContext.Unwrap(ctx.Model) is not Core.Ai.IMachineTranslator;

    /// <summary>Den Treffer Nr. n per KI an den neuen Ausgangstext anpassen.</summary>
    [RelayCommand]
    private async Task RepairMatch(int number)
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning) return;
        var row = Matches.FirstOrDefault(m => m.Number == number);
        if (row == null) return;
        var seg = Selected;
        IsAiRunning = true;
        try
        {
            var ctx = CreateContext(AppServices.Ai.TranslateProviderId);
            if (!CanFollowInstructions(ctx)) ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var result = await new Core.Agents.FuzzyRepair(ctx).RepairAsync(seg.Source, _doc.SourceLanguage, _doc.TargetLanguage, row.Match,
                Terms.Select(t => t.Hit).ToList(), _brief);
            seg.Model.Target = result.Target;
            seg.Model.Suggestion = result.Target;
            seg.Model.Origin = TranslationOrigin.Ai;
            seg.Model.MatchPercent = row.Match.Score;
            seg.Model.Status = SegmentStatus.Draft;
            AutoLocalize(seg);
            seg.Refresh();
            ShowRepair(row.Match.Unit.Target, seg.Model.Target, result.ChangedTooMuch);
            _main.Status = $"TM-Treffer ({row.Match.Score} %) angepasst.";
        }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    private void ShowRepair(string before, string after, bool tooMuch)
    {
        RepairBefore = before;
        RepairAfter = after;
        RepairNote = tooMuch ? "Achtung: Die KI hat mehr geändert, als sich im Ausgangstext geändert hat – bitte prüfen." : "";
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
        _all = _doc.Paragraphs.SelectMany(p => p.Segments.Select(s => new SegmentViewModel(s, p.Context, p.TagData) { Limit = LimitOf(s), IsTracking = () => TrackChangesOn, DocumentReadOnly = () => DocumentReadOnly })).ToList();
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
        if (!CheckWritable()) return;
        if (Selected == null || _doc == null || Project == null) return;
        var current = Selected;
        if (current.Target.Trim().Length == 0) { _main.Status = "Leeres Segment kann nicht bestätigt werden."; return; }
        var propagated = Workflow.Confirm(Project, _doc, current.Model);
        current.Refresh();
        RecordResearchFeedback(current);                         // Empfehlung übernommen oder ersetzt? Lehrt Gütestufen und Quellen
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
        if (!CheckWritable()) return;
        if (Selected == null || _doc == null || Project == null) return;
        Workflow.Confirm(Project, _doc, Selected.Model, SegmentStatus.Reviewed);
        Selected.Refresh();
        UpdateProgress();
        MoveNext(onlyOpen: false);
    }

    [RelayCommand]
    private void Reject()
    {
        if (!CheckWritable()) return;
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

    /// <summary>Wie oft kommt die Wendung vor? Ergebnis der Zählung (mehrere Varianten mit „|“).</summary>
    [ObservableProperty] public partial string PhraseCountText { get; set; } = "";

    /// <summary>
    /// Wendung(en) in den eigenen Daten zählen – Projekt-TM, gemeinsame und geladene freie Sprachdaten; für deutsche
    /// Einzelwörter zusätzlich die Häufigkeit im DWDS. Genau und privat statt geschätzter Trefferzahlen im Netz.
    /// </summary>
    [RelayCommand]
    private async Task CountPhrase()
    {
        if (Project == null || _doc == null || ConcordanceQuery.Trim().Length == 0) return;
        var project = Project;
        var language = ConcordanceInTarget ? _doc.TargetLanguage : _doc.SourceLanguage;
        var tms = project.TmsFor(_doc.TargetLanguage);
        var variants = Core.Tm.PhraseCounter.Variants(ConcordanceQuery);
        PhraseCountText = L.T("Zähle …");
        var lines = new List<string>();
        foreach (var v in variants)
        {
            var f = await Task.Run(() => Core.Tm.PhraseCounter.Count(tms, v, language));
            var line = L.T($"„{v}“: {f.Units:N0} Sätze in Ihren Daten") +
                       (f.PerTm.Count > 0 ? " (" + string.Join(", ", f.PerTm.Take(4).Select(p => $"{p.Tm} {p.Units:N0}")) + ")" : "");
            if (Core.Model.Languages.Primary(language) == "de" && !v.Contains(' '))
            {
                try
                {
                    if (await Core.Ai.Dictionaries.DwdsFrequencyAsync(v) is { } dwds)
                        line += " · " + L.T($"DWDS: Häufigkeitsklasse {dwds.Class} von 6, {dwds.Hits:N0} Treffer");
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Core.Privacy.OutboundDeclinedException or System.Text.Json.JsonException) { }
            }
            lines.Add(line);
        }
        PhraseCountText = string.Join("\n", lines);
        SidePanelTab = 2;
    }

    /// <summary>Die Wendung der Konkordanz unter „Hilfsmittel“ je Land im Netz prüfen.</summary>
    [RelayCommand]
    private void CheckPresence()
    {
        if (_doc == null || ConcordanceQuery.Trim().Length == 0) return;
        _main.Tools.PreparePresence(ConcordanceQuery, ConcordanceInTarget ? _doc.TargetLanguage : _doc.SourceLanguage);
        _main.SelectedTab = MainViewModel.ToolsTab;
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
        var issues = RunQaQuiet();
        _main.Status = $"QA: {issues.Count(i => i.Severity == QaSeverity.Error)} Fehler, {issues.Count(i => i.Severity == QaSeverity.Warning)} Warnungen.";
        SidePanelTab = 3;
    }

    /// <summary>QA-Prüfung ohne Reiterwechsel: Meldungen in die Liste, Zahl an jedes Segment.</summary>
    private List<QaIssue> RunQaQuiet()
    {
        if (_doc == null || Project == null) return [];
        var aiChecks = QaIssues.Where(q => q.Check == AiCheckName).ToList();       // KI-Prüfungen bleiben stehen (sie kosten Anfragen)
        QaIssues.Clear();
        var issues = QaChecker.Check(_doc, Project.Qa, Project.ProjectTermbase, AppServices.Spell,
            CurrentFile == null ? null : Project.Brief(CurrentFile, _doc.TargetLanguage));
        foreach (var row in issues.Select(i => new QaRow(i)).Concat(aiChecks).OrderBy(q => q.SegmentId)) QaIssues.Add(row);
        UpdateAllIssues();
        return issues;
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

    /// <summary>Für gleichzeitige Arbeiten: jede Protokollzeile beginnt mit <paramref name="prefix"/> (z. B. dem Ausdruck).</summary>
    private AgentContext CreateContext(string? providerId, string prefix) =>
        AgentContext.From(AppServices.Ai, providerId, new Progress<string>(m => AiLog += $"[{prefix}] {m}\n"));

    private (string? Prev, string? Next) Neighbours(SegmentViewModel seg)
    {
        var i = _all.IndexOf(seg);
        return (i > 0 ? _all[i - 1].Source : null, i >= 0 && i < _all.Count - 1 ? _all[i + 1].Source : null);
    }

    /// <summary>Mehrere Nachbarsegmente als Zusammenhang (so viele, wie die intelligente Bewertung für nötig hält).</summary>
    private (string? Prev, string? Next) Neighbours(SegmentViewModel seg, int before, int after)
    {
        var (b, a) = NeighbourSources(seg, Math.Max(before, after));
        var prev = string.Join(" ", b.Skip(Math.Max(0, b.Count - before)));
        var next = string.Join(" ", a.Take(after));
        return (prev.Length > 0 ? prev : null, next.Length > 0 ? next : null);
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
            var (knowledge, research) = await Task.Run(() => (Workflow.Knowledge(project, doc, seg.Model), Workflow.Research(project, doc, seg.Model)));
            var brief = await EnsureBriefAsync(CancellationToken.None);
            var text = await new AiTranslator(ctx).TranslateAsync(seg.Source, doc.SourceLanguage, doc.TargetLanguage, project.Topic,
                Matches.Select(m => m.Match).ToList(), Terms.Select(t => t.Hit).ToList(), prev, next, knowledge: knowledge, brief: brief, research: research);
            seg.Model.Target = text;
            seg.Model.Origin = TranslationOrigin.Ai;
            seg.Model.Suggestion = seg.Model.Target;
            seg.Model.Status = SegmentStatus.Draft;
            seg.Model.MatchPercent = null;
            AutoLocalize(seg);
            seg.Refresh();
            _main.Status = $"KI-Vorschlag eingesetzt ({ctx.Model.DisplayName}).";
        }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    /// <summary>Alle leeren Segmente per KI übersetzen (als Entwurf).</summary>
    [RelayCommand]
    private Task AiTranslateAll() => TranslateBatchAsync(selected: false);

    /// <summary>In der Segmentliste markiert (Strg+Klick, Umschalt+Klick auf Nummer oder Status) – setzt die Ansicht.</summary>
    public IReadOnlyList<SegmentViewModel> MarkedSegments { get; set; } = [];

    /// <summary>
    /// „Ausgewählte per KI“: wie „Leere per KI“, aber für die markierten Segmente (ohne Markierung das gewählte) – auch
    /// schon übersetzte werden neu übersetzt (als Entwurf); bestätigte und gesperrte bleiben unverändert.
    /// </summary>
    [RelayCommand]
    private Task AiTranslateSelected() => TranslateBatchAsync(selected: true);

    private List<SegmentViewModel> BatchTargets(bool selected) => selected
        ? (MarkedSegments.Count > 0 ? MarkedSegments : Selected is { } s ? [s] : [])
            .Where(x => !x.IsLocked && !x.Model.IsConfirmed).Distinct().OrderBy(x => _all.IndexOf(x)).ToList()
        : _all.Where(s => !s.IsLocked && s.Target.Length == 0).ToList();

    private async Task TranslateBatchAsync(bool selected)
    {
        if (_doc == null || Project == null || IsAiRunning || IsAskingBatchResearch) return;
        var count = BatchTargets(selected).Count;
        if (selected && count == 0)
        {
            _main.Status = L.T("Keine ausgewählten Segmente zum Übersetzen – bestätigte und gesperrte bleiben unverändert.");
            return;
        }
        // Auch recherchieren? Dann sucht die KI vor jedem Segment seine Ausdrücke im Netz und übersetzt mit den Ergebnissen;
        // danach (im Dialog wählbar) prüft sie jede Übersetzung im Netz und verbessert fragliche Wendungen
        var (withResearch, withCheck, budget, smart) = count > 0 ? await AskBatchQuestionsAsync(count, selected) : (false, false, BatchResearchBudget, false);
        if (_doc == null || Project == null || IsAiRunning) return;
        var single = selected && count == 1;
        IsAiRunning = true;
        _batchRunning = true;                                    // die Recherche-Warteschlange wartet so lange
        AiLog = "";
        var cts = _batchCts = new CancellationTokenSource();
        var run = new TranslationRun();
        BatchResearchState? research = null;
        try
        {
            var todo = BatchTargets(selected);
            var project = Project;
            var doc = _doc;
            var brief = todo.Count > 0 ? await EnsureBriefAsync(cts.Token) : null;
            var dossiers = await Task.Run(() => DossierStore.Load(project));
            if (withResearch || withCheck) research = NewBatchState(project, doc, dossiers, brief, withResearch, withCheck, budget, single, smart);
            await TranslateSegmentsAsync(todo, research, brief, dossiers, run, cts.Token);
            _main.Status = $"KI hat {run.Done} Segmente als Entwurf übersetzt" + (run.Repaired > 0 ? $", davon {run.Repaired} TM-Treffer gezielt angepasst." : ".") +
                           (research is { DoResearch: true } ? " " + L.T($"{research.Researched} Ausdrücke recherchiert, {research.Evidenced} davon belegt – belegte gingen in die Übersetzung ein.") : "") +
                           (research is { WebCheck: true } ? " " + L.T($"{research.Checked} Übersetzungen im Netz geprüft, {research.Improved} verbessert.") : "") +
                           run.PoolUsage;
        }
        catch (OperationCanceledException) { _main.Status = $"Abgebrochen nach {run.Done} Segmenten."; }
        catch (Exception ex) { _main.Status = $"KI nach {run.Done} Segmenten: {ex.Message}"; }
        finally
        {
            IsAiRunning = false;
            _batchRunning = false;
            SaveIfOpen();
            UpdateProgress();
            if (research is { Researched: > 0 } && Project == research.Project)
            {
                _dossiers = DossierStore.Load(research.Project);           // neue Dossiers auch im Recherche-Reiter
                RefreshDossiers();
            }
        }
    }

    /// <summary>
    /// Was die intelligente Bewertung als „bleibt unverändert“ eingestuft hat (Eigennamen, Marken, Produkte): als Hinweis an
    /// die Übersetzung – ohne Recherche und ohne Beleg, denn ein Name braucht keinen.
    /// </summary>
    private static IEnumerable<TermDossier> KeepUnchanged(BatchResearchState? research, SegmentViewModel seg) =>
        research?.Assessments.GetValueOrDefault(seg.Id)?.Items.Where(i => i.Action == Core.Agents.TriageAction.Keep)
            .Select(i => new TermDossier { Expression = i.Expression, Recommended = i.Expression, Strategy = "keep unchanged (proper name, brand or product)" })
        ?? [];

    /// <summary>Zähler eines Übersetzungslaufs (für Statuszeile und Ablaufbericht).</summary>
    private sealed class TranslationRun
    {
        public int Done;
        public int Repaired;
        public string PoolUsage = "";
    }

    /// <summary>Recherche und Netzprüfung für einen Übersetzungslauf; bei einem einzelnen Segment alle seine Ausdrücke.</summary>
    private static BatchResearchState NewBatchState(TranslationProject project, BilingualDocument doc, List<TermDossier> dossiers, DocumentBrief? brief,
        bool withResearch, bool withCheck, TimeSpan? budget, bool single, bool smart = false) => new(project, doc, dossiers, brief)
    {
        DoResearch = withResearch, WebCheck = withCheck, Budget = budget, Smart = smart,
        // harte Grenze je Schritt: doppeltes Budget, mindestens die feste; ohne Budget ohne Grenze
        StepLimit = budget is { } b ? TimeSpan.FromTicks(Math.Max(BatchStepLimit.Ticks, 2 * b.Ticks)) : Timeout.InfiniteTimeSpan,
        MaxPerSegment = single ? int.MaxValue : MaxResearchPerSegment,
    };

    /// <summary>
    /// Segmente per KI übersetzen (als Entwurf): je Segment ggf. erst seine Ausdrücke recherchieren, danach ggf. die
    /// Übersetzung im Netz prüfen. Ein Verbund bearbeitet mehrere Segmente gleichzeitig (je Modell eines); die
    /// Fortsetzungen laufen im UI-Thread, daher braucht die Warteschlange keine Sperre.
    /// </summary>
    private async Task TranslateSegmentsAsync(List<SegmentViewModel> todo, BatchResearchState? research, DocumentBrief? brief,
        List<TermDossier> dossiers, TranslationRun run, CancellationToken ct)
    {
        if (_doc == null || Project == null || todo.Count == 0) return;
        var ctx = CreateContext(AppServices.Ai.TranslateProviderId);
        var translator = new AiTranslator(ctx);
        var parallel = Math.Clamp((ctx.Model as Core.Ai.IParallelChatModel)?.Parallelism ?? 1, 1, Math.Max(1, todo.Count));
        var queue = new Queue<SegmentViewModel>(todo);
        var project = Project;
        var doc = _doc;
        var repair = RepairFuzzyMatches && CanFollowInstructions(ctx);
        var start = run.Done;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Worker()
        {
            while (queue.Count > 0)
            {
                stop.Token.ThrowIfCancellationRequested();
                var seg = queue.Dequeue();
                try { await TranslateOneAsync(seg); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFor(seg, "Übersetzung", "Fehler", L.T($"Segment {seg.Id}: Übersetzung nicht möglich – {ex.Message}"));
                    throw;
                }
            }
        }
        async Task TranslateOneAsync(SegmentViewModel seg)
        {
            if (research is { DoResearch: true }) await ResearchForTranslationAsync(seg, research, stop.Token);
            var n = run.Done - start + 1;
            _main.Status = parallel > 1
                ? $"KI übersetzt ({n}/{todo.Count}) – {parallel} Modelle gleichzeitig …"
                : $"KI übersetzt Segment {seg.Id} ({n}/{todo.Count}) …";
            var (matches, terms, knowledge) = await Task.Run(() => (
                Workflow.Lookup(project, doc, seg.Model),
                Workflow.Terms(project, doc, seg.Model),
                Workflow.Knowledge(project, doc, seg.Model)));
            var (prev, next) = research?.Assessments.GetValueOrDefault(seg.Id) is { } a && (a.ContextBefore > 1 || a.ContextAfter > 1)
                ? Neighbours(seg, a.ContextBefore, a.ContextAfter) : Neighbours(seg);
            var fuzzy = repair ? matches.FirstOrDefault(m => m.Score is >= Core.Agents.FuzzyRepair.MinScore and < 100) : null;
            if (fuzzy != null)
            {
                seg.Model.Target = (await new Core.Agents.FuzzyRepair(ctx).RepairAsync(seg.Source, doc.SourceLanguage, doc.TargetLanguage,
                    fuzzy, terms, brief, stop.Token)).Target;
                seg.Model.MatchPercent = fuzzy.Score;
                run.Repaired++;
            }
            else
                seg.Model.Target = await translator.TranslateAsync(seg.Source, doc.SourceLanguage, doc.TargetLanguage, project.Topic,
                    matches, terms, prev, next, ct: stop.Token, knowledge: knowledge, brief: brief,
                    research: KeepUnchanged(research, seg).Concat(Workflow.Research(project, doc, seg.Model, dossiers)).ToList());
            seg.Model.Origin = TranslationOrigin.Ai;
            seg.Model.Suggestion = seg.Model.Target;
            seg.Model.Status = SegmentStatus.Draft;
            AutoLocalize(seg);
            seg.Refresh();
            if (research != null) await CheckAndImproveAsync(seg, research, stop.Token);
            run.Done++;
            if (run.Done % 10 == 0) SaveIfOpen();
        }
        var workers = Enumerable.Range(0, parallel).Select(_ => Worker()).ToList();
        try { await Task.WhenAll(workers); }
        catch
        {
            stop.Cancel();                               // ein Fehler hält die anderen an
            throw;
        }
        if (Core.Privacy.PrivacyContext.Unwrap(ctx.Model) is Core.Ai.PooledChatModel pool)
            run.PoolUsage = " " + string.Join(", ", pool.Usage.Where(u => u.Done > 0).Select(u => $"{u.Name}: {u.Done}"));
    }

    [RelayCommand]
    private void CancelAi() => _batchCts?.Cancel();

    /// <summary>Grammatik der Übersetzungen prüfen (KI oder LanguageTool, siehe „KI einrichten“); Ergebnisse in der QA-Liste.</summary>
    [RelayCommand]
    private async Task CheckGrammar()
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        var settings = AppServices.Ai.Grammar;
        if (settings.Engine == Core.Qa.GrammarEngine.LanguageToolPublic && Project.Privacy.Enabled)
        {
            _main.Status = "Vertrauliches Projekt: die öffentliche LanguageTool-Schnittstelle wird nicht genutzt – KI (mit Platzhaltern) oder eigenen Server wählen.";
            return;
        }
        IsAiRunning = true;
        AiLog = "";
        var startedOn = SidePanelTab;
        var cts = _batchCts = new CancellationTokenSource();
        try
        {
            var model = settings.Engine == Core.Qa.GrammarEngine.Ai
                ? CreateContext(AppServices.Ai.EvaluateProviderId ?? AppServices.Ai.ResearchProviderId ?? AppServices.Ai.TranslateProviderId).Model
                : null;
            var segments = _all.Where(s => !s.IsLocked && s.Target.Trim().Length > 0).Select(s => (s.Id, s.Target)).ToList();
            var issues = await Core.Qa.GrammarChecker.CheckAsync(segments, _doc.TargetLanguage, settings, model,
                new Progress<string>(m => _main.Status = m), cts.Token);
            foreach (var old in QaIssues.Where(q => q.Issue.Check == "Grammatik").ToList()) QaIssues.Remove(old);
            foreach (var i in issues) QaIssues.Add(new QaRow(i.ToQa()));
            UpdateAllIssues();
            ShowResultTab(3, startedOn);
            _main.Status = issues.Count == 0 ? "Grammatik: keine Fehler gefunden." : $"Grammatik: {issues.Count} Hinweise (unter „Meldungen“).";
        }
        catch (OperationCanceledException) { _main.Status = "Grammatikprüfung abgebrochen."; }
        catch (Exception ex) { _main.Status = "Grammatik: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    /// <summary>Bewertet aktuelle Übersetzung, TM-Treffer und einen frischen KI-Vorschlag gegeneinander.</summary>
    [RelayCommand]
    private async Task EvaluateSegment()
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning) return;
        var seg = Selected;
        IsAiRunning = true;
        AiLog = "";
        BeginAiResult(seg);
        SidePanelTab = 7;
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
                Project.Topic, Matches.Select(m => m.Match).ToList(), Terms.Select(t => t.Hit).ToList(), prev, next, brief: _brief,
                research: DossierStore.ForTranslation(_dossiers, seg.Source, _doc.TargetLanguage));
            if (candidates.All(c => c.Text != aiText)) candidates.Add(new("KI", aiText));

            var ctx = CreateContext(AppServices.Ai.EvaluateProviderId);
            var proj = Project;
            var d = _doc;
            var kn = await Task.Run(() => Workflow.Knowledge(proj, d, seg.Model));
            var result = await new EvaluationAgent(ctx).RunAsync(seg.Source, _doc.SourceLanguage, _doc.TargetLanguage, candidates,
                Project.Topic, Terms.Select(t => t.Hit).ToList(), AiCheckUsage && ctx.Search != null, knowledge: kn);
            ShowAiResult(seg, AgentsViewModel.FormatEvaluation(result), evaluation: result);
        }
        catch (Exception ex) { ShowAiResult(seg, "Fehler: " + ex.Message); }
        finally { IsAiRunning = false; }
    }

    // ---------- Übersetzung prüfen: Rückübersetzung und Abgleich mehrerer Modelle ----------

    /// <summary>Unabhängige Übersetzungen anderer Modelle aus der letzten Prüfung – zum Übernehmen.</summary>
    public ObservableCollection<string> CheckAlternatives { get; } = new();

    /// <summary>Beim Prüfen des ganzen Dokuments auch mehrere Modelle abgleichen (gründlicher, etwa doppelt so viele Anfragen).</summary>
    [ObservableProperty] public partial bool CheckConsensusInBatch { get; set; }

    public const string AiCheckName = "KI-Prüfung";

    /// <summary>Prüft die Übersetzung des Segments: Rückübersetzung und Abgleich mehrerer Modelle.</summary>
    [RelayCommand]
    private async Task CheckSegment()
    {
        if (Selected == null || _doc == null || Project == null || IsAiRunning) return;
        var seg = Selected;
        SidePanelTab = 7;
        BeginAiResult(seg);
        if (seg.Target.Trim().Length == 0) { AiResult = "Das Segment hat noch keine Übersetzung."; return; }
        IsAiRunning = true;
        AiLog = "";
        var cts = _batchCts = new CancellationTokenSource();
        try
        {
            var ctx = CreateContext(AppServices.Ai.EvaluateProviderId);
            ctx.Say("Rückübersetzung und unabhängige Übersetzungen …");
            var check = await new TranslationChecker(ctx).CheckAsync(seg.Source, seg.Target, _doc.SourceLanguage, _doc.TargetLanguage,
                Project.Topic, ct: cts.Token);
            ShowAiResult(seg, check.Format(), check.Alternatives.Where(a => a != seg.Target));
            MarkCheck(seg, check);
        }
        catch (OperationCanceledException) { ShowAiResult(seg, "Abgebrochen."); }
        catch (Exception ex) { ShowAiResult(seg, "Fehler: " + ex.Message); }
        finally { IsAiRunning = false; }
    }

    /// <summary>Markierten Text als geschützte Angabe aufnehmen (Datenschutz).</summary>
    public void Protect(string text)
    {
        _main.Privacy.Protect(text);
        _main.Status = Project?.Privacy.Enabled == true
            ? $"„{text.Trim()}“ wird geschützt: nur als Platzhalter an Online-KI und ins TM."
            : $"„{text.Trim()}“ steht in der Schutzliste – Datenschutz ist für dieses Projekt noch ausgeschaltet (Reiter Projekt).";
    }

    /// <summary>Eine der unabhängigen Übersetzungen übernehmen (als Entwurf).</summary>
    [RelayCommand]
    private void UseAlternative(string? text)
    {
        if (Selected == null || string.IsNullOrWhiteSpace(text) || Selected.IsReadOnly) return;
        Selected.Model.Target = TagText.Repair(Selected.Source, text);           // Tags der Quelle bleiben erhalten
        Selected.Model.Origin = TranslationOrigin.Ai;
        Selected.Model.Suggestion = Selected.Model.Target;
        Selected.Model.Status = SegmentStatus.Draft;
        Selected.Refresh();
        _main.Status = "Alternative übernommen (Entwurf).";
    }

    /// <summary>Zweifelhafte Prüfung als QA-Meldung beim Segment vermerken (eine frühere KI-Prüfung wird ersetzt).</summary>
    private void MarkCheck(SegmentViewModel seg, TranslationCheck check)
    {
        foreach (var old in QaIssues.Where(q => q.SegmentId == seg.Id && q.Check == AiCheckName).ToList()) QaIssues.Remove(old);
        if (check.Doubtful)
        {
            var issues = check.Findings.SelectMany(f => f.Issues).Distinct().ToList();
            var message = $"{check.Verdict} ({check.Score * 100:0} %)" + (issues.Count > 0 ? ": " + string.Join("; ", issues) : "");
            var row = new QaRow(new QaIssue(seg.Id, check.Score < 0.6 ? QaSeverity.Error : QaSeverity.Warning, AiCheckName, message));
            var at = QaIssues.TakeWhile(q => q.SegmentId <= seg.Id).Count();
            QaIssues.Insert(at, row);
        }
        // gespeichert beim Dokument: nach dem erneuten Öffnen steht die Prüfung wieder in der Liste
        _notes.RemoveAll(n => n.SegmentId == seg.Id && n.Kind == AiCheckName);
        var note = check.Doubtful && QaIssues.LastOrDefault(q => q.SegmentId == seg.Id && q.Check == AiCheckName) is { } added
            ? new SegmentNote(seg.Id, AiCheckName, added.Severity, added.Message, DateTime.UtcNow) : null;
        if (note != null) _notes.Add(note);
        if (Project != null && CurrentFile != null) SegmentNotes.Replace(Project, CurrentFile.Id, seg.Id, AiCheckName, note);
        UpdateIssues(seg);
    }

    /// <summary>
    /// Alle übersetzten Segmente auf den Sinn prüfen (Rückübersetzung, auf Wunsch auch Abgleich mehrerer Modelle) –
    /// im Verbund mehrere Segmente gleichzeitig. Zweifelhafte erscheinen im Reiter „Meldungen“.
    /// </summary>
    [RelayCommand]
    private async Task CheckDocumentWithAi()
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        IsAiRunning = true;
        AiLog = "";
        var startedOn = SidePanelTab;
        var cts = _batchCts = new CancellationTokenSource();
        var done = 0;
        var doubtful = 0;
        try
        {
            var ctx = CreateContext(AppServices.Ai.EvaluateProviderId);
            var checker = new TranslationChecker(ctx);
            var todo = _all.Where(s => !s.IsLocked && s.Target.Trim().Length > 0).ToList();
            var parallel = Math.Clamp((ctx.Model as Core.Ai.IParallelChatModel)?.Parallelism ?? 1, 1, Math.Max(1, todo.Count));
            var queue = new Queue<SegmentViewModel>(todo);
            var doc = _doc;
            var project = Project;
            async Task Worker()
            {
                while (queue.Count > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var seg = queue.Dequeue();
                    _main.Status = $"KI prüft den Sinn ({done + 1}/{todo.Count}) …";
                    var check = await checker.CheckAsync(seg.Source, seg.Target, doc.SourceLanguage, doc.TargetLanguage, project.Topic,
                        backTranslation: true, consensus: CheckConsensusInBatch, ct: cts.Token);
                    MarkCheck(seg, check);
                    if (check.Doubtful) doubtful++;
                    done++;
                }
            }
            var workers = Enumerable.Range(0, parallel).Select(_ => Worker()).ToList();
            try { await Task.WhenAll(workers); }
            catch
            {
                cts.Cancel();
                throw;
            }
            _main.Status = $"KI-Prüfung: {doubtful} von {done} Segmenten zweifelhaft – siehe QA.";
            ShowResultTab(3, startedOn);
        }
        catch (OperationCanceledException) { _main.Status = $"KI-Prüfung abgebrochen nach {done} Segmenten."; }
        catch (Exception ex) { _main.Status = $"KI-Prüfung nach {done} Segmenten: {ex.Message}"; }
        finally { IsAiRunning = false; }
    }

    private EvaluationResult? _lastEvaluation;
    private TermComparisonResult? _lastComparison;
    private string? _lastComparisonTopic;

    /// <summary>Ergebnis der KI zu einem Segment – es bleibt bei diesem Segment, auch wenn inzwischen ein anderes gewählt ist.</summary>
    private sealed record AiSegmentResult(string Text, List<string> Alternatives, EvaluationResult? Evaluation, TermComparisonResult? Comparison, string? ComparisonTopic);
    private readonly Dictionary<SegmentViewModel, AiSegmentResult> _aiResults = new();

    /// <summary>Vor einer KI-Aktion: altes Ergebnis des Segments verwerfen, Anzeige leeren.</summary>
    private void BeginAiResult(SegmentViewModel seg)
    {
        _aiResults.Remove(seg);
        if (Selected == seg) ApplyAiResult(null);
    }

    /// <summary>
    /// Ergebnis beim Segment ablegen, für das die Aktion lief. Ist es noch gewählt, steht es sofort da; sonst sagt die
    /// Statuszeile Bescheid – so landet nie ein Vorschlag (und „übernehmen“) beim falschen Segment.
    /// </summary>
    private void ShowAiResult(SegmentViewModel seg, string text, IEnumerable<string>? alternatives = null,
        EvaluationResult? evaluation = null, TermComparisonResult? comparison = null)
    {
        var r = new AiSegmentResult(text, alternatives?.ToList() ?? [], evaluation, comparison, Project?.Topic);
        _aiResults[seg] = r;
        if (Selected == seg) ApplyAiResult(r);
        else _main.Status = L.T($"KI-Ergebnis für Segment {seg.Id} liegt vor – zum Ansehen das Segment wählen.");
    }

    private void ApplyAiResult(AiSegmentResult? r)
    {
        AiResult = r?.Text ?? "";
        CheckAlternatives.Clear();
        foreach (var a in r?.Alternatives ?? []) CheckAlternatives.Add(a);
        _lastEvaluation = r?.Evaluation;
        _lastComparison = r?.Comparison;
        _lastComparisonTopic = r?.ComparisonTopic;
        HasComparison = r?.Comparison != null;
    }

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
        BeginAiResult(seg);
        SidePanelTab = 7;
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var (prev, next) = Neighbours(seg);
            var context = string.Join("\n", new[] { prev, seg.Source, next }.Where(x => x != null).Select(x => TagText.StripTags(x!)));
            var query = new TermQuery(term.Trim(), _doc.SourceLanguage, _doc.TargetLanguage, context, Project.Topic,
                _main.CurrentProfile, null, Terms.Select(t => t.Hit).ToList());
            var comparison = await new TermComparisonAgent(ctx).RunAsync(query);
            ShowAiResult(seg, TermComparisonAgent.Format(comparison), comparison: comparison);
        }
        catch (Exception ex) { ShowAiResult(seg, "Fehler: " + ex.Message); }
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
        Selected.Model.Target = TagText.Repair(Selected.Source, text);
        Selected.Model.Origin = TranslationOrigin.Ai;
        Selected.Model.Suggestion = Selected.Model.Target;
        Selected.Model.Status = SegmentStatus.Draft;
        Selected.Refresh();
    }

    /// <summary>Prüft den markierten Ausdruck (oder die ganze Übersetzung) auf Üblichkeit im Netz.</summary>
    public async Task CheckUsageAsync(string expression, IReadOnlyList<string>? alternatives = null, string? language = null)
    {
        if (_doc == null || Selected is not { } seg || IsAiRunning || expression.Trim().Length == 0) return;
        IsAiRunning = true;
        AiLog = "";
        BeginAiResult(seg);
        SidePanelTab = 7;
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var r = await new UsageCheckAgent(ctx).RunAsync(expression.Trim(), language ?? _doc.TargetLanguage, Project?.Topic, alternatives,
                suggestAlternatives: alternatives is not { Count: > 0 });
            ShowAiResult(seg, AgentsViewModel.FormatUsage(r));
        }
        catch (Exception ex) { ShowAiResult(seg, "Fehler: " + ex.Message); }
        finally { IsAiRunning = false; }
    }

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
