using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SententiamConverte.Core.Model;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Zeile im Editor.</summary>
public partial class SegmentViewModel : ObservableObject
{
    public Segment Model { get; }
    public string? Context { get; }

    public SegmentViewModel(Segment model, string? context)
    {
        Model = model;
        Context = context;
    }

    public int Id => Model.Id;
    public string Source => Model.Source;
    public bool IsLocked => Model.Locked;

    public string Target
    {
        get => Model.Target;
        set
        {
            if (Model.Target == value) return;
            Model.Target = value;
            Model.Modified = System.DateTime.UtcNow;
            if (Model.IsConfirmed || Model.Status == SegmentStatus.NotTranslated)
                Model.Status = value.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated;
            if (Model.Origin is TranslationOrigin.None or TranslationOrigin.TranslationMemory or TranslationOrigin.AutoPropagated or TranslationOrigin.Ai)
                Model.Origin = TranslationOrigin.Manual;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(OriginText));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    public partial int IssueCount { get; set; }

    public bool HasIssues => IssueCount > 0;

    /// <summary>Nach Änderungen am Modell von außen (Bestätigen, TM-Treffer) aufrufen.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(HasComment));
    }

    public bool HasComment => !string.IsNullOrEmpty(Model.Comment);

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
