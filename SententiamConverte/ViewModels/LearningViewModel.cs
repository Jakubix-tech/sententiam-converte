using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine gelernte Regel in der Liste.</summary>
public sealed class LearnedRuleRow(LearnedRule rule, LearningViewModel owner)
{
    public LearnedRule Rule { get; } = rule;
    public LearningViewModel Owner { get; } = owner;
    public string Text => $"„{Rule.From}“ → „{Rule.To}“";
    public string Info => $"{Rule.Language} · {Rule.Count}× · " + L.T(Rule.State switch
    {
        RuleState.Active => "aktiv",
        RuleState.Suggested => "vorgeschlagen",
        RuleState.Rejected => "abgelehnt",
        _ => "wird beobachtet",
    });
    public bool CanActivate => Rule.State != RuleState.Active;
}

/// <summary>
/// Aus Korrekturen lernen: Schalter, gelernte Regeln und der Hinweis im Editor, sobald sich eine Korrektur wiederholt
/// (oder eine Korrektur nur Anrede bzw. Geschlecht betrifft – dann geht es um den Steckbrief).
/// </summary>
public partial class LearningViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public LearningViewModel(MainViewModel main)
    {
        _main = main;
        CorrectionLearning.Enabled = AppServices.Ui.LearnFromCorrections;
        CorrectionLearning.Suggested += s => Dispatcher.UIThread.Post(() => Show(s));
        Refresh();
    }

    public bool Enabled
    {
        get => AppServices.Ui.LearnFromCorrections;
        set
        {
            if (AppServices.Ui.LearnFromCorrections == value) return;
            AppServices.Ui.LearnFromCorrections = value;
            CorrectionLearning.Enabled = value;
            AppServices.Ui.Save();
            if (!value) Hint = null;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<LearnedRuleRow> Rules { get; } = new();

    public void Refresh()
    {
        Rules.Clear();
        foreach (var r in CorrectionLearning.Rules.Where(r => r.State != RuleState.Collecting)
                     .OrderBy(r => r.State == RuleState.Active ? 0 : r.State == RuleState.Suggested ? 1 : 2).ThenByDescending(r => r.Count))
            Rules.Add(new LearnedRuleRow(r, this));
    }

    [RelayCommand]
    private void Activate(LearnedRuleRow? row)
    {
        if (row == null) return;
        CorrectionLearning.SetState(row.Rule, RuleState.Active);
        Refresh();
    }

    [RelayCommand]
    private void Remove(LearnedRuleRow? row)
    {
        if (row == null) return;
        CorrectionLearning.Remove(row.Rule);
        Refresh();
    }

    // ---------- Hinweis im Editor ----------

    [ObservableProperty] public partial LearningSuggestion? Hint { get; set; }

    public bool HasHint => Hint != null;

    public string HintText => Hint switch
    {
        { Kind: CorrectionKind.Wording, Rule: { } r } => L.T($"Gelernt: Sie ändern „{r.From}“ in „{r.To}“ ({r.Count}×). Künftig immer so?"),
        { Kind: CorrectionKind.Address, Address: { } a } => L.T($"Sie haben die Anrede zu „{AddressForms.Label(a, _main.Editor.TargetLanguage ?? "")}“ geändert. Gilt das für das ganze Dokument?"),
        { Kind: CorrectionKind.Speaker, Speaker: SpeakerForm.Female } => L.T("Sie haben die Ich-Form in die weibliche geändert. Schreibt im ganzen Dokument eine Frau?"),
        { Kind: CorrectionKind.Speaker } => L.T("Sie haben die Ich-Form in die männliche geändert. Schreibt im ganzen Dokument ein Mann?"),
        _ => "",
    };

    public string AcceptText => L.T(Hint?.Kind == CorrectionKind.Wording ? "Als Regel übernehmen" : "Ja, Steckbrief anpassen");

    partial void OnHintChanged(LearningSuggestion? value)
    {
        OnPropertyChanged(nameof(HasHint));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(AcceptText));
    }

    private void Show(LearningSuggestion s)
    {
        // Anrede oder Sprecher, die schon so im Steckbrief stehen, sind kein Hinweis wert
        if (s.Kind == CorrectionKind.Address && _main.Editor.BriefAddress == s.Address) return;
        if (s.Kind == CorrectionKind.Speaker && _main.Editor.BriefSpeaker == s.Speaker) return;
        Hint = s;
        Refresh();
    }

    [RelayCommand]
    private void AcceptHint()
    {
        switch (Hint)
        {
            case { Kind: CorrectionKind.Wording, Rule: { } rule }:
                CorrectionLearning.SetState(rule, RuleState.Active);
                _main.Status = "Regel übernommen – die KI hält sich künftig daran, die QA meldet Abweichungen.";
                break;
            case { Kind: CorrectionKind.Address, Address: { } address }:
                _main.Editor.SetBriefFromCorrection(address, null);
                break;
            case { Kind: CorrectionKind.Speaker, Speaker: { } speaker }:
                _main.Editor.SetBriefFromCorrection(null, speaker);
                break;
        }
        Hint = null;
        Refresh();
    }

    [RelayCommand]
    private void RejectHint()
    {
        if (Hint is { Kind: CorrectionKind.Wording, Rule: { } rule }) CorrectionLearning.SetState(rule, RuleState.Rejected);
        Hint = null;
        Refresh();
    }

    [RelayCommand]
    private void DismissHint() => Hint = null;
}
