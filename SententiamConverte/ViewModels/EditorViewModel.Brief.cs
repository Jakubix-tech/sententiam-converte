using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Steckbrief des Dokuments: Vor der KI-Übersetzung liest die KI das Dokument einmal (Thema, Textsorte, Anrede, Sprecher);
/// sind Anrede oder Sprecher unklar, fragt das Programm einmal je Dokument. Jede KI-Übersetzung bekommt den Steckbrief mit.
/// </summary>
public partial class EditorViewModel
{
    private DocumentBrief? _brief;
    private TaskCompletionSource<bool>? _briefAnswer;

    public bool HasBrief => _brief != null;

    /// <summary>Aus Korrekturen lernen (Hinweisbalken über dem Editor).</summary>
    public LearningViewModel Learning => _main.Learning;

    /// <summary>Vor der KI-Übersetzung das Dokument lesen (Einstellung, gilt für alle Projekte).</summary>
    public bool ReadDocumentFirst
    {
        get => AppServices.Ui.ReadDocumentFirst;
        set
        {
            if (AppServices.Ui.ReadDocumentFirst == value) return;
            AppServices.Ui.ReadDocumentFirst = value;
            AppServices.Ui.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial string BriefTopic { get; set; } = "";
    [ObservableProperty] public partial string BriefTextType { get; set; } = "";
    [ObservableProperty] public partial string BriefAudience { get; set; } = "";
    [ObservableProperty] public partial string BriefTone { get; set; } = "";
    [ObservableProperty] public partial string BriefNames { get; set; } = "";
    [ObservableProperty] public partial string BriefNotes { get; set; } = "";
    [ObservableProperty] public partial int BriefAddressIndex { get; set; }
    [ObservableProperty] public partial int BriefSpeakerIndex { get; set; }

    /// <summary>Rückfrage zu Anrede und Sprecher läuft.</summary>
    [ObservableProperty] public partial bool IsAskingBrief { get; set; }

    public string BriefQuestionText => _brief == null ? "" : string.Join(" · ", new[] { _brief.TextType, _brief.Recipient, _brief.Author }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Anredeformen der Zielsprache (Reihenfolge wie <see cref="AddressForm"/>).</summary>
    public IReadOnlyList<string> AddressOptions => Enum.GetValues<AddressForm>().Select(f => f switch
    {
        AddressForm.Unknown => L.T("offen"),
        AddressForm.None => L.T("keine Anrede"),
        _ => $"{AddressForms.Label(f, TargetLanguage ?? "")} – " + L.T(f switch
        {
            AddressForm.FormalPlural => "höflich, an mehrere oder allgemein",
            AddressForm.FormalMale => "höflich, an einen Mann",
            AddressForm.FormalFemale => "höflich, an eine Frau",
            AddressForm.FormalUnknownGender => "höflich, Geschlecht unbekannt",
            AddressForm.Informal => "vertraut, Anleitungen im Imperativ",
            _ => "unpersönlich, ohne Anrede",
        }),
    }).ToList();

    public IReadOnlyList<string> SpeakerOptions { get; } =
        [L.T("offen"), L.T("niemand spricht in der Ich-Form"), L.T("ein Mann"), L.T("eine Frau"), L.T("eine Organisation (wir)")];

    /// <summary>Beim Öffnen eines Dokuments: gespeicherten Steckbrief anzeigen.</summary>
    private void LoadBrief()
    {
        _brief = Project != null && CurrentFile != null && _doc != null ? Project.Brief(CurrentFile, _doc.TargetLanguage) : null;
        ShowBrief();
    }

    private void ShowBrief()
    {
        var b = _brief;
        BriefTopic = b?.Topic ?? "";
        BriefTextType = b?.TextType ?? "";
        BriefAudience = b?.Audience ?? "";
        BriefTone = b?.Tone ?? "";
        BriefNames = b == null ? "" : string.Join(", ", b.Names);
        BriefNotes = b?.Notes ?? "";
        BriefAddressIndex = (int)(b?.Address ?? AddressForm.Unknown);
        BriefSpeakerIndex = (int)(b?.Speaker ?? SpeakerForm.Unknown);
        OnPropertyChanged(nameof(HasBrief));
        OnPropertyChanged(nameof(BriefAddress));
        OnPropertyChanged(nameof(BriefSpeaker));
        OnPropertyChanged(nameof(AddressOptions));
        OnPropertyChanged(nameof(BriefQuestionText));
        ShowConventions();
    }

    public AddressForm? BriefAddress => _brief?.Address;
    public SpeakerForm? BriefSpeaker => _brief?.Speaker;

    /// <summary>Aus einer Korrektur: Anrede oder Sprecher gelten für das ganze Dokument.</summary>
    public void SetBriefFromCorrection(AddressForm? address, SpeakerForm? speaker)
    {
        if (address is { } a) BriefAddressIndex = (int)a;
        if (speaker is { } s) BriefSpeakerIndex = (int)s;
        StoreBrief(confirmed: true);
        _main.Status = "Steckbrief angepasst – gilt für die weitere KI-Übersetzung dieses Dokuments.";
    }

    private void StoreBrief(bool confirmed)
    {
        if (Project == null || CurrentFile == null || _doc == null) return;
        var b = _brief ?? new DocumentBrief();
        b.Topic = BriefTopic.Trim();
        b.TextType = BriefTextType.Trim();
        b.Audience = BriefAudience.Trim();
        b.Tone = BriefTone.Trim();
        b.Names = BriefNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        b.Notes = BriefNotes.Trim();
        b.Address = (AddressForm)Math.Clamp(BriefAddressIndex, 0, Enum.GetValues<AddressForm>().Length - 1);
        b.Speaker = (SpeakerForm)Math.Clamp(BriefSpeakerIndex, 0, Enum.GetValues<SpeakerForm>().Length - 1);
        if (confirmed)
        {
            b.Confirmed = true;
            // Gewohnheit merken: so spricht man bei diesem Kunden in dieser Textsorte an
            CorrectionLearning.RememberDefault(Project.Client, b.TextType, _doc.TargetLanguage, b.Address, b.Speaker);
        }
        _brief = b;
        Project.SetBrief(CurrentFile, _doc.TargetLanguage, b);
        OnPropertyChanged(nameof(HasBrief));
    }

    /// <summary>Änderungen am Steckbrief übernehmen (gilt als bestätigt – das Programm fragt nicht mehr).</summary>
    [RelayCommand]
    private void SaveBrief()
    {
        StoreBrief(confirmed: true);
        _main.Status = "Steckbrief gespeichert.";
    }

    /// <summary>Dokument (neu) lesen lassen.</summary>
    [RelayCommand]
    private async Task ReadDocument()
    {
        if (_doc == null || Project == null || IsAiRunning) return;
        IsAiRunning = true;
        try { await CreateBriefAsync(CancellationToken.None); }
        catch (Exception ex) { _main.Status = "KI: " + ex.Message; }
        finally { IsAiRunning = false; }
    }

    /// <summary>
    /// Die KI liest das Dokument und füllt den Steckbrief: Was schon eingetragen ist (vom Nutzer oder bestätigt), bleibt
    /// stehen – ergänzt werden nur leere Felder. Wechselt man währenddessen das Dokument, landet das Ergebnis trotzdem beim
    /// richtigen. <paramref name="ask"/>: offene Anrede oder offenen Sprecher gleich erfragen (nicht beim Öffnen).
    /// </summary>
    private async Task CreateBriefAsync(CancellationToken ct, bool ask = true)
    {
        var project = Project!;
        var doc = _doc!;
        var file = CurrentFile!;
        _main.Status = "KI liest das Dokument …";
        var ctx = CreateContext(AppServices.Ai.ResearchProviderId ?? AppServices.Ai.TranslateProviderId);
        var read = await new DocumentReader(ctx).ReadAsync(doc, project.Topic, ct);
        // Unklares aus früheren Dokumenten dieser Art übernehmen (gleicher Kunde, gleiche Textsorte)
        if (read.NeedsAnswer && CorrectionLearning.DefaultFor(project.Client, read.TextType, doc.TargetLanguage) is { } usual)
        {
            if (read.Address == AddressForm.Unknown) read.Address = usual.Address;
            if (read.Speaker == SpeakerForm.Unknown && usual.Speaker != SpeakerForm.Unknown) read.Speaker = usual.Speaker;
        }
        // Vorhandenes bleibt (auch Dokumentart und landesübliche Ausdrücke), nur Lücken werden gefüllt
        var brief = project.Brief(file, doc.TargetLanguage) ?? (_doc == doc ? _brief : null);
        if (brief != null) brief.FillFrom(read);
        else brief = read;
        project.SetBrief(file, doc.TargetLanguage, brief);
        if (_doc != doc) return;
        _brief = brief;
        ShowBrief();
        _main.Status = "Steckbrief erstellt.";
        if (ask && brief.NeedsAnswer) await AskBriefAsync();
    }

    private Task? _briefReading;

    /// <summary>Ist eine KI bereit, ohne erst etwas herunterzuladen? (Beim Öffnen wird nur dann gelesen.)</summary>
    private static bool AiReady()
    {
        var ai = AppServices.Ai;
        if (ai.Get(ai.ResearchProviderId ?? ai.TranslateProviderId) is not { } p) return false;
        return p.Kind != Core.Ai.AiProviderKind.BuiltIn || Core.Ai.BuiltInAi.Model(p.Model) is { } m && Core.Ai.BuiltInAi.IsInstalled(m);
    }

    /// <summary>
    /// Beim Öffnen: Fehlt der Steckbrief oder ist er lückenhaft und noch nie gelesen, liest die KI das Dokument im
    /// Hintergrund und füllt die leeren Felder – ohne Rückfrage; die kommt erst vor der KI-Übersetzung.
    /// </summary>
    private void AutoReadBrief()
    {
        if (!ReadDocumentFirst || _doc == null || Project == null || CurrentFile == null || _briefReading != null) return;
        if (_brief is { ShouldRead: false } || !AiReady()) return;
        _briefReading = ReadInBackgroundAsync();

        async Task ReadInBackgroundAsync()
        {
            try { await CreateBriefAsync(CancellationToken.None, ask: false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _main.Status = "Steckbrief nicht erstellt: " + ex.Message; }
            finally { _briefReading = null; }
        }
    }

    /// <summary>
    /// Steckbrief für die KI-Übersetzung: vorhanden oder neu gelesen (wenn eingeschaltet); offene Anrede oder offener
    /// Sprecher werden einmal erfragt. Ohne Sprachmodell (z. B. nur maschinelle Übersetzung) bleibt es beim Vorhandenen.
    /// </summary>
    private async Task<DocumentBrief?> EnsureBriefAsync(CancellationToken ct)
    {
        if (_doc == null || Project == null || CurrentFile == null) return _brief;
        if (_briefReading is { } running) await running;                 // liest schon (beim Öffnen gestartet)
        if ((_brief == null || _brief.ShouldRead) && ReadDocumentFirst)
        {
            try { await CreateBriefAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _main.Status = "Steckbrief nicht erstellt: " + ex.Message; }
        }
        else if (_brief is { NeedsAnswer: true }) await AskBriefAsync();
        return _brief;
    }

    /// <summary>Einmal je Dokument: Wer wird angesprochen, wer spricht?</summary>
    private Task<bool> AskBriefAsync()
    {
        _briefAnswer?.TrySetResult(false);
        _briefAnswer = new TaskCompletionSource<bool>();
        OnPropertyChanged(nameof(BriefQuestionText));
        IsAskingBrief = true;
        return _briefAnswer.Task;
    }

    [RelayCommand]
    private void AnswerBrief()
    {
        StoreBrief(confirmed: true);
        IsAskingBrief = false;
        _briefAnswer?.TrySetResult(true);
        _main.Status = "Anrede und Sprecher übernommen.";
    }

    /// <summary>Später entscheiden: übersetzen ohne Vorgabe zu Anrede und Sprecher (beim nächsten Mal wird wieder gefragt).</summary>
    [RelayCommand]
    private void SkipBriefQuestion()
    {
        IsAskingBrief = false;
        _briefAnswer?.TrySetResult(false);
    }
}
