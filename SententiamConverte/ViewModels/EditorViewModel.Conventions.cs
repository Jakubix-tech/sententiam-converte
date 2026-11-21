using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein landesüblicher Ausdruck in der Anzeige; das Häkchen bestimmt, ob die Übersetzung ihn verwendet.</summary>
public sealed partial class ConventionRow(CountryConvention convention, Action changed) : ObservableObject
{
    public CountryConvention Convention { get; } = convention;
    public string Title => $"„{Convention.Source}“ → „{Convention.Target}“";
    public string Why => Convention.Why ?? "";
    public string Evidence => string.Join("\n", Convention.Evidence) + "\n" +
                              L.T(Convention.ByFrequency ? "nach Häufigkeit gewählt" : "Vorschlag der KI – die Zahlen sind nicht aussagekräftig");
    public List<DossierSourceRow> Sources => Convention.Sources.Select((s, i) => new DossierSourceRow(s, i + 1, $"{s.Title} ({new Uri(s.Url).Host})")).ToList();

    [ObservableProperty] public partial bool IsUsed { get; set; } = convention.Use;

    partial void OnIsUsedChanged(bool value)
    {
        Convention.Use = value;
        changed();
    }
}

/// <summary>Dokumentart und landesübliche Ausdrücke (gehören zum Steckbrief und gehen in jede KI-Übersetzung ein).</summary>
public partial class EditorViewModel
{
    public ObservableCollection<ConventionRow> Conventions { get; } = new();
    [ObservableProperty] public partial string DocumentKindText { get; set; } = "";
    [ObservableProperty] public partial string ConventionNotes { get; set; } = "";
    [ObservableProperty] public partial string ConventionStatus { get; set; } = "";
    public bool HasConventionInfo => DocumentKindText.Length > 0 || Conventions.Count > 0;

    /// <summary>Aus dem Steckbrief anzeigen (beim Öffnen des Dokuments und nach jeder Änderung).</summary>
    private void ShowConventions()
    {
        var b = _brief;
        Conventions.Clear();
        foreach (var c in b?.Conventions ?? []) Conventions.Add(new ConventionRow(c, SaveConventions));
        DocumentKindText = b?.DocumentKind is { Length: > 0 } kind
            ? kind + (b.Country is { Length: > 0 } country ? $" · {Controls.LanguageOption.For(_doc?.TargetLanguage ?? "").Name} ({country})" : "")
            : "";
        ConventionNotes = b == null ? "" : string.Join("\n", b.CountryNotes.Select(n => "• " + n));
        OnPropertyChanged(nameof(HasConventionInfo));
    }

    private void SaveConventions()
    {
        if (_brief == null || Project == null || CurrentFile == null || _doc == null) return;
        Project.SetBrief(CurrentFile, _doc.TargetLanguage, _brief);
    }

    /// <summary>
    /// Dokumentart bestimmen und die im Zielland üblichen Formulierungen für Überschriften, Titel und feste Wendungen
    /// finden und zählen. Fehlt der Steckbrief, liest die KI das Dokument zuerst.
    /// </summary>
    [RelayCommand]
    private async Task AnalyseConventions()
    {
        if (_doc == null || Project == null || CurrentFile == null || IsResearching || IsAiRunning) return;
        IsResearching = true;
        try
        {
            if (_brief == null) await CreateBriefAsync(CancellationToken.None);
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var analysis = await new DocumentConventions(ctx).AnalyseAsync(_doc, _doc.TargetLanguage, Project.Topic, _brief);
            var brief = _brief ?? new DocumentBrief();
            DocumentConventions.Apply(brief, analysis);
            _brief = brief;
            Project.SetBrief(CurrentFile, _doc.TargetLanguage, brief);
            ShowConventions();
            ConventionStatus = analysis.Conventions.Count == 0
                ? L.T("Keine landesüblichen Ausdrücke gefunden.")
                : L.T($"{analysis.Conventions.Count} landesübliche Ausdrücke – gehen ab jetzt in jede KI-Übersetzung dieses Dokuments ein. Modell: {analysis.Model}, gezählt mit: {analysis.Search}.");
        }
        catch (Exception ex) { ConventionStatus = "KI: " + ex.Message; }
        finally { IsResearching = false; }
    }
}
