using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Meldung im Reiter „Meldungen“: zu welchem Segment, woher, wie schwer, was.</summary>
/// <param name="Severity">„Fehler“, „Warnung“ oder „Hinweis“.</param>
public sealed record MessageRow(int SegmentId, string Kind, string Severity, string Text)
{
    public string Header => L.T($"Segment {SegmentId}") + " · " + L.T(Severity) + " · " + L.T(Kind);

    public IBrush Brush => Severity switch
    {
        "Fehler" => new SolidColorBrush(Color.Parse("#C53030")),
        "Warnung" => new SolidColorBrush(Color.Parse("#C05621")),
        _ => new SolidColorBrush(Color.Parse("#2B6CB0")),
    };

    internal int Rank => Severity switch { "Fehler" => 0, "Warnung" => 1, _ => 2 };

    /// <summary>Lange Meldungen (etwa mit einem Stück KI-Antwort) stehen in der Liste gekürzt, ganz im Tooltip.</summary>
    public string? LongText => Text.Length > 300 || Text.Count(c => c == '\n') >= 5 ? L.Lines(Text) : null;
}

/// <summary>
/// Alle Meldungen an einer Stelle: QA (mit Rechtschreibung, Grammatik, KI-Prüfung), Fehler aus Lektorat und KI-Lektorat,
/// die intelligente Bewertung, die Recherche zu den Ausdrücken des Satzes, geschützte Angaben und die Meldungen der KI
/// aus Übersetzung, Recherche, Netzprüfung und Rückmeldung. Vorgabe: nur die des gewählten Segments; sonst alle, nach
/// Segment geordnet, ein Klick springt hin. Die Meldungen der KI stehen gespeichert beim Dokument (siehe
/// <see cref="SegmentNotes"/>), die Recherche in ihren Dossiers – beides ist nach dem erneuten Öffnen wieder da.
/// </summary>
public partial class EditorViewModel
{
    public ObservableCollection<MessageRow> Messages { get; } = new();

    /// <summary>Nur die Meldungen des gewählten Segments (Vorgabe) oder alle.</summary>
    [ObservableProperty] public partial bool MessagesOnlySelected { get; set; } = true;

    public bool HasMessages => Messages.Count > 0;
    public string MessagesEmptyText => L.T(MessagesOnlySelected ? "Keine Meldungen zu diesem Segment." : "Keine Meldungen im Dokument.");
    public string MessagesTabHeader => L.T("Meldungen") + (Messages.Count > 0 ? $" ({Messages.Count})" : "");

    /// <summary>Die gespeicherten Meldungen der KI zum offenen Dokument.</summary>
    private List<SegmentNote> _notes = new();
    private bool _messagesQueued;
    /// <summary>Warum ein Ausdruck nicht recherchiert wird – je Dokument einmal bestimmt (die Prüfung geht über alle Segmente).</summary>
    private readonly Dictionary<string, string?> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

    partial void OnMessagesOnlySelectedChanged(bool value) => RefreshMessages();

    /// <summary>
    /// Eine Zeile des KI-Protokolls, die zu Segmenten gehört: ins Protokoll und als Meldung bei jedem dieser Segmente
    /// (gespeichert, Zeichen ⓘ mit Tooltip am Segment).
    /// </summary>
    private void LogFor(IEnumerable<int> segmentIds, string kind, string severity, string line)
    {
        AiLog += line + "\n";
        if (Project == null || CurrentFile == null) return;
        foreach (var id in segmentIds.Distinct())
        {
            var note = new SegmentNote(id, kind, severity, line, DateTime.UtcNow);
            _notes.RemoveAll(n => n.SegmentId == id && n.Kind == kind && n.Text == line);
            _notes.Add(note);
            SegmentNotes.Add(Project, CurrentFile.Id, note);
            if (_all.FirstOrDefault(s => s.Id == id) is { } seg) ShowNotes(seg);
        }
        ScheduleMessagesRefresh();
    }

    private void LogFor(SegmentViewModel seg, string kind, string severity, string line) => LogFor([seg.Id], kind, severity, line);

    /// <summary>Die Meldungen der KI am Segment (Tooltip am ⓘ), die neuesten zuerst.</summary>
    private void ShowNotes(SegmentViewModel seg) =>
        seg.NotesInfo = string.Join("\n", _notes.Where(n => n.SegmentId == seg.Id && n.Kind != AiCheckName)
            .OrderByDescending(n => n.When).Take(6).Select(n => n.Text));

    /// <summary>Beim Öffnen: gespeicherte Meldungen laden; frühere KI-Prüfungen kommen wieder in die QA-Liste.</summary>
    private void LoadNotes()
    {
        _notes = Project != null && CurrentFile != null ? SegmentNotes.Load(Project, CurrentFile.Id) : new();
        var ids = _all.Select(s => s.Id).ToHashSet();
        foreach (var n in _notes.Where(n => n.Kind == AiCheckName && ids.Contains(n.SegmentId)))
            QaIssues.Add(new QaRow(new QaIssue(n.SegmentId, n.Severity == "Fehler" ? QaSeverity.Error : QaSeverity.Warning, AiCheckName, n.Text)));
        _skipReasons.Clear();
        foreach (var s in _all) ShowNotes(s);
        UpdateAllIssues();
        RefreshMessages();
    }

    /// <summary>QA-Zahl und Tooltip am ⚠ eines Segments aus der QA-Liste.</summary>
    private void UpdateIssues(SegmentViewModel seg) => UpdateIssues(seg, QaIssues.Where(q => q.SegmentId == seg.Id));

    private static void UpdateIssues(SegmentViewModel seg, IEnumerable<QaRow> rows)
    {
        var mine = rows.ToList();
        seg.IssueCount = mine.Count;
        seg.IssueInfo = string.Join("\n", mine.Select(q => $"{L.T(q.Check)}: {L.Lines(q.Message)}"));
    }

    /// <summary>Alle Segmente auf einmal (nach einer QA-Prüfung des Dokuments).</summary>
    private void UpdateAllIssues()
    {
        var bySegment = QaIssues.ToLookup(q => q.SegmentId);
        foreach (var s in _all) UpdateIssues(s, bySegment[s.Id]);
    }

    /// <summary>Viele Änderungen nacheinander (Stapel): die Liste einmal danach neu aufbauen.</summary>
    private void ScheduleMessagesRefresh()
    {
        if (_messagesQueued) return;
        _messagesQueued = true;
        Dispatcher.UIThread.Post(RefreshMessages, DispatcherPriority.Background);
    }

    private void RefreshMessages()
    {
        _messagesQueued = false;
        Messages.Clear();
        if (_doc != null)
        {
            IEnumerable<SegmentViewModel> segments = MessagesOnlySelected ? Selected is { } s ? [s] : [] : _all;
            foreach (var seg in segments)
            foreach (var m in MessagesFor(seg, _skipReasons))
                Messages.Add(m);
        }
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(MessagesTabHeader));
        OnPropertyChanged(nameof(MessagesEmptyText));
    }

    private static string SeverityOf(LqaSeverity s) => s switch
    {
        LqaSeverity.Critical or LqaSeverity.Major => "Fehler",
        LqaSeverity.Minor => "Warnung",
        _ => "Hinweis",
    };

    /// <summary>Alles, was zu einem Segment vorliegt – Fehler zuerst, dann Warnungen, dann Hinweise.</summary>
    private List<MessageRow> MessagesFor(SegmentViewModel seg, Dictionary<string, string?> skipCache)
    {
        var list = new List<MessageRow>();
        foreach (var q in QaIssues.Where(q => q.SegmentId == seg.Id))
            list.Add(new MessageRow(seg.Id, q.Check, q.Severity, q.Message));
        foreach (var e in seg.Model.Errors ?? [])
            list.Add(new MessageRow(seg.Id, e.Reviewer == QualityEvaluator.Reviewer ? "KI-Lektorat" : "Lektorat", SeverityOf(e.Severity),
                $"{L.T(e.Category)} · {L.T(Lqa.SeverityName(e.Severity))}" + (e.Note.Length > 0 ? " – " + e.Note : "")));
        if (seg.HasAssessment) list.Add(new MessageRow(seg.Id, "Bewertung", "Hinweis", seg.AssessmentInfo));
        if (_doc != null)
        {
            // die Recherche zu den Ausdrücken des Satzes – aus den gespeicherten Dossiers, auch nach dem erneuten Öffnen
            var to = ResearchTo ?? _doc.TargetLanguage;
            foreach (var d in DossierStore.ForText(_dossiers, seg.Source, to))
                list.Add(new MessageRow(seg.Id, "Recherche", DossierStore.Grade(d) is "C" or "D" ? "Warnung" : "Hinweis",
                    L.T($"„{d.Expression}“ → „{d.Recommended ?? "?"}“ – {DossierStore.GradeText(d)}")));
            foreach (var item in ResearchItems.Select(r => r.Item).Where(i => i.SegmentIds.Contains(seg.Id)))
            {
                if (!skipCache.TryGetValue(item.Expression, out var why))
                    skipCache[item.Expression] = why = DocumentResearch.SkipReason(item.Expression, _doc.Segments);
                if (why != null) list.Add(new MessageRow(seg.Id, "Recherche", "Hinweis", L.T($"„{item.Expression}“ wird nicht recherchiert: {L.T(why)}.")));
            }
        }
        if (seg.HasSensitive) list.Add(new MessageRow(seg.Id, "Datenschutz", "Hinweis", seg.SensitiveInfo));
        foreach (var n in _notes.Where(n => n.SegmentId == seg.Id && n.Kind != AiCheckName).OrderByDescending(n => n.When))
            list.Add(new MessageRow(seg.Id, n.Kind, n.Severity, n.Text));
        return list.OrderBy(m => m.Rank).ToList();
    }

    /// <summary>Klick auf eine Meldung: zum Segment springen.</summary>
    [RelayCommand]
    private void GoToMessage(MessageRow? row)
    {
        if (row == null || Selected?.Id == row.SegmentId) return;
        var seg = Segments.FirstOrDefault(s => s.Id == row.SegmentId) ?? _all.FirstOrDefault(s => s.Id == row.SegmentId);
        if (seg == null) return;
        if (!Segments.Contains(seg)) FilterIndex = 0;
        Selected = seg;
        FocusRequested?.Invoke(seg);
    }
}
