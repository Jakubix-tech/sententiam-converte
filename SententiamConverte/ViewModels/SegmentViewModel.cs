using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Model;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Zeile im Editor.</summary>
public partial class SegmentViewModel : ObservableObject
{
    public Segment Model { get; }
    public string? Context { get; }

    public SegmentViewModel(Segment model, string? context, System.Collections.Generic.IReadOnlyDictionary<int, string>? tagData = null)
    {
        Model = model;
        Context = context;
        TagData = tagData;
    }

    /// <summary>Tag-Daten des Absatzes: welche Formatierung ein Tag-Paar trägt (für die Anzeige im Editor).</summary>
    public System.Collections.Generic.IReadOnlyDictionary<int, string>? TagData { get; }

    public int Id => Model.Id;
    public string Source => Model.Source;
    public bool IsLocked => Model.Locked;

    /// <summary>Gemeinsames Projekt: wer dieses Segment gerade bearbeitet (null = niemand anderes).</summary>
    public string? LockedBy
    {
        get => _lockedBy;
        set
        {
            if (_lockedBy == value) return;
            _lockedBy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(LockText));
        }
    }
    private string? _lockedBy;

    public bool IsReadOnly => Model.Locked || _lockedBy != null || DocumentReadOnly?.Invoke() == true;

    /// <summary>Vom Editor gesetzt: die ganze Datei ist gerade nur lesbar (Team: nicht an der Reihe).</summary>
    public System.Func<bool>? DocumentReadOnly { get; set; }

    public void RefreshReadOnly() => OnPropertyChanged(nameof(IsReadOnly));
    public string? LockText => _lockedBy == null ? null : "🔒 " + _lockedBy;

    public string Target
    {
        get => Model.Target;
        set
        {
            if (Model.Target == value) return;
            // Änderungsverfolgung: vor der ersten Änderung des Prüfers die bestätigte Fassung merken
            if (IsTracking?.Invoke() == true) Core.Qa.TrackChanges.Remember(Model);
            Model.Target = value;
            if (Model.ReviewBaseline == value) Model.ReviewBaseline = null;     // zurückgeändert
            Model.Modified = System.DateTime.UtcNow;
            if (Model.IsConfirmed || Model.Status == SegmentStatus.NotTranslated)
                Model.Status = value.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated;
            if (Model.Origin is TranslationOrigin.None or TranslationOrigin.TranslationMemory or TranslationOrigin.AutoPropagated or TranslationOrigin.Ai)
                Model.Origin = TranslationOrigin.Manual;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTargetTags));
            OnPropertyChanged(nameof(MissingTags));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(OriginText));
            OnPropertyChanged(nameof(LengthText));
            OnPropertyChanged(nameof(IsTooLong));
            OnPropertyChanged(nameof(HasChange));
            OnPropertyChanged(nameof(Baseline));
        }
    }

    /// <summary>Vom Editor gesetzt: ob Änderungen gerade verfolgt werden (Lektorat).</summary>
    public System.Func<bool>? IsTracking { get; set; }

    /// <summary>Offene Änderung des Prüfers gegenüber der bestätigten Fassung.</summary>
    public bool HasChange => Core.Qa.TrackChanges.HasChange(Model);
    public string? Baseline => Model.ReviewBaseline;

    /// <summary>Erfasste Fehler der Fehlerbewertung.</summary>
    public int ErrorCount => Model.Errors?.Count ?? 0;
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Höchstlänge dieses Segments (eigene aus der Datei oder die des Projekts); null = keine.</summary>
    public int? Limit { get; set; }

    public bool HasLimit => Limit != null;
    public string? LengthText => Limit is { } l ? $"{TagText.StripTags(Model.Target).Length}/{l}" : null;
    public bool IsTooLong => Limit is { } l && TagText.StripTags(Model.Target).Length > l;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    public partial int IssueCount { get; set; }

    public bool HasIssues => IssueCount > 0;

    /// <summary>Die QA-Meldungen dieses Segments für den Tooltip am ⚠ (je Zeile „Prüfung: Meldung“).</summary>
    [ObservableProperty] public partial string IssueInfo { get; set; } = "";

    /// <summary>Die erfassten Fehler für den Tooltip am ✗ (je Zeile Kategorie, Schwere, Anmerkung).</summary>
    public string ErrorInfo => Model.Errors is { Count: > 0 } errors ? string.Join("\n", errors.Select(e => new LqaErrorRow(e).Text)) : "";

    /// <summary>Die Meldungen der KI zu diesem Segment (Übersetzung, Recherche, Netzprüfung, Rückmeldung) für den Tooltip am ⓘ.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string NotesInfo { get; set; } = "";

    public bool HasNotes => NotesInfo.Length > 0;

    /// <summary>Geschützte Angaben in diesem Segment, je Zeile „Art „Wert“ → [TAG_1]“; leer = keine (oder Datenschutz aus).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSensitive))]
    public partial string SensitiveInfo { get; set; } = "";

    public bool HasSensitive => SensitiveInfo.Length > 0;

    /// <summary>Intelligente Bewertung dieses Laufs: Stufe („easy“ … „very_hard“) und die Zeile aus dem Protokoll (Tooltip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssessment), nameof(AssessmentBrush))]
    public partial string AssessmentLevel { get; set; } = "";

    [ObservableProperty] public partial string AssessmentInfo { get; set; } = "";

    public bool HasAssessment => AssessmentLevel.Length > 0;

    public Avalonia.Media.IBrush AssessmentBrush => Avalonia.Media.Brush.Parse(AssessmentLevel switch
    {
        "easy" => "#38A169",
        "hard" => "#DD6B20",
        "very_hard" => "#C53030",
        _ => "#D69E2E",
    });

    public void ShowAssessment(string level, string info)
    {
        AssessmentLevel = level;
        AssessmentInfo = info;
    }

    /// <summary>Nach Änderungen am Modell von außen (Bestätigen, TM-Treffer) aufrufen.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(HasTargetTags));
        OnPropertyChanged(nameof(MissingTags));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(HasComment));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(IsTooLong));
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(Baseline));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorInfo));
    }

    public bool HasComment => !string.IsNullOrEmpty(Model.Comment);

    /// <summary>Vorschau mit farbigen Tags unter dem Zielfeld, sobald Quelle oder Ziel Tags haben.</summary>
    public bool HasTargetTags => Model.Target.Contains(TagText.Open) || (Model.Source.Contains(TagText.Open) && Model.Target.Length > 0);

    /// <summary>Tags der Quelle, die im Ziel noch fehlen (Hinweis unter dem Zielfeld).</summary>
    public string? MissingTags
    {
        get
        {
            if (Model.Target.Length == 0) return null;
            var missing = TagText.Tags(Model.Source)
                .Select(t => Model.Source.Substring(t.Index, t.Length))
                .Where(t => !Model.Target.Contains(t)).ToList();
            return missing.Count == 0 ? null : "fehlt: " + string.Join(" ", missing) + "  (Strg+Umschalt+G fügt ein)";
        }
    }

    public string StatusText => Model.Status switch
    {
        SegmentStatus.NotTranslated => "offen",
        SegmentStatus.Draft => "Entwurf",
        SegmentStatus.Translated => "übersetzt",
        SegmentStatus.TranslationRejected => "abgelehnt",
        SegmentStatus.Reviewed => "lektoriert",
        SegmentStatus.ReviewRejected => "Lektorat abgel.",
        SegmentStatus.Signed => "freigegeben",
        _ => "",
    };

    public string OriginText => Model.Origin switch
    {
        TranslationOrigin.TranslationMemory => $"TM {Model.MatchPercent}%",
        TranslationOrigin.AutoPropagated => "AP",
        TranslationOrigin.Ai => "KI",
        TranslationOrigin.Source => "Quelle",
        TranslationOrigin.Manual => "",
        _ => "",
    };

    public IBrush StatusBrush => Model.Locked
        ? Brushes.Gray
        : Model.Status switch
        {
            SegmentStatus.Translated => new SolidColorBrush(Color.Parse("#2E9E4F")),
            SegmentStatus.Reviewed or SegmentStatus.Signed => new SolidColorBrush(Color.Parse("#2B6CB0")),
            SegmentStatus.Draft => new SolidColorBrush(Color.Parse("#D69E2E")),
            SegmentStatus.TranslationRejected or SegmentStatus.ReviewRejected => new SolidColorBrush(Color.Parse("#C53030")),
            _ => new SolidColorBrush(Color.Parse("#A0AEC0")),
        };
}
