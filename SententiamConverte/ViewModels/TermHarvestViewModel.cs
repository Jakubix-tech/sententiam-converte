using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Fachbegriffe vor dem Übersetzen aus den Projektdateien gewinnen: Kandidaten nach Häufigkeit, auf Wunsch von der KI
/// ausgewählt und mit Übersetzungsvorschlag – prüfen, berichtigen, in die Terminologie übernehmen.
/// </summary>
public partial class TermHarvestViewModel(MainViewModel main) : ViewModelBase
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<TermSuggestionRow> Terms { get; } = new();
    public IReadOnlyList<string> TargetLanguages => main.Project?.TargetLanguages ?? [];
    [ObservableProperty] public partial string? TargetLanguage { get; set; }
    [ObservableProperty] public partial bool UseAi { get; set; } = true;
    [ObservableProperty] public partial bool ToShared { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Info { get; set; } = "";
    public bool HasTerms => Terms.Count > 0;

    public bool AiReady
    {
        get
        {
            var ai = AppServices.Ai;
            if (ai.Get(ai.TranslateProviderId) is not { } p || p.Kind == AiProviderKind.MachineTranslation) return ai.Get(ai.ResearchProviderId) is not null;
            return p.Kind != AiProviderKind.BuiltIn || BuiltInAi.Model(p.Model) is { } m && BuiltInAi.IsInstalled(m);
        }
    }

    /// <summary>Nach Projektwechsel.</summary>
    public void Refresh()
    {
        Terms.Clear();
        Info = "";
        OnPropertyChanged(nameof(TargetLanguages));
        if (TargetLanguage == null || !TargetLanguages.Contains(TargetLanguage)) TargetLanguage = TargetLanguages.FirstOrDefault();
        OnPropertyChanged(nameof(HasTerms));
        OnPropertyChanged(nameof(AiReady));
    }

    private List<string> SourceSentences() => main.Project is not { } p || TargetLanguage == null ? [] :
        p.Files.SelectMany(f => p.LanguagesOf(f).Contains(TargetLanguage) ? p.LoadDocument(f, TargetLanguage).Segments : [])
            .Where(s => !s.Locked).Select(s => s.Source).ToList();

    [RelayCommand]
    private async Task Find()
    {
        if (main.Project is not { } p || TargetLanguage is not { } tgt || IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Terms.Clear();
        try
        {
            main.Editor.SaveIfOpen();
            Info = "Lese die Projektdateien …";
            var sentences = await Task.Run(SourceSentences);
            var termbases = p.TermbasesFor(tgt);
            bool Known(string term) => termbases.Any(tb => tb.Lookup(p.SourceLanguage, term, 3)
                .Any(c => c.Terms.Any(t => Languages.Matches(t.Language, p.SourceLanguage) && string.Equals(t.Text, term, StringComparison.CurrentCultureIgnoreCase))));
            var candidates = await Task.Run(() => TermExtractor.Candidates(sentences, p.SourceLanguage, 300, Known));
            if (UseAi && AiReady && candidates.Count > 0)
            {
                Info = $"{candidates.Count} Kandidaten gefunden – die KI wählt die Fachbegriffe aus und schlägt Übersetzungen vor …";
                var ai = AppServices.Ai;
                var providerId = ai.Get(ai.TranslateProviderId)?.Kind == AiProviderKind.MachineTranslation ? ai.ResearchProviderId : ai.TranslateProviderId;
                var model = AgentContext.From(ai, providerId).Model;
                var refined = await TermExtractor.RefineAsync(model, candidates, sentences, p.SourceLanguage, tgt, p.Topic, _cts.Token);
                foreach (var t in refined.Where(t => !Known(t.Source))) Terms.Add(new TermSuggestionRow(t, p.SourceLanguage, tgt));
                Info = Terms.Count == 0 ? "Die KI hat keine weiteren Fachbegriffe gefunden." :
                    $"{Terms.Count} Fachbegriffe mit Übersetzungsvorschlag – bitte prüfen und berichtigen, dann übernehmen.";
            }
            else
            {
                foreach (var c in candidates.Take(150)) Terms.Add(new TermSuggestionRow(new TermSuggestion(c.Term, "", c.Count, c.Example), p.SourceLanguage, tgt) { Selected = false });
                Info = candidates.Count == 0 ? "Keine wiederkehrenden Begriffe gefunden." :
                    $"{Terms.Count} Kandidaten nach Häufigkeit – Fachbegriffe anhaken, Übersetzung eintragen, übernehmen.";
            }
        }
        catch (OperationCanceledException) { Info = "Abgebrochen."; }
        catch (Exception ex) when (ex is AiException or IOException or System.Net.Http.HttpRequestException or InvalidOperationException) { Info = "Fehler: " + ex.Message; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasTerms));
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        var all = Terms.Any(t => !t.Selected);
        foreach (var t in Terms) t.Selected = all;
    }

    [RelayCommand]
    private void AddTerms()
    {
        if (main.Project is not { } p) return;
        var chosen = Terms.Where(t => t.Selected && t.Target.Trim().Length > 0).ToList();
        if (chosen.Count == 0) { Info = "Bitte Begriffe anhaken und eine Übersetzung eintragen."; return; }
        var concepts = chosen.Select(t => ((IReadOnlyList<(string, string, Core.Terminology.TermStatus)>)
            [(t.SourceLanguage, t.Source, TermStatus.Preferred), (t.TargetLanguage, t.Target.Trim(), TermStatus.Preferred)], (string?)null, (string?)p.Topic, (string?)"Projektdateien"));
        string name;
        if (ToShared)
        {
            var (shared, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, p.SourceLanguage, chosen[0].TargetLanguage, p);
            if (p.Termbases.OfType<Termbase>().FirstOrDefault(t => string.Equals(Path.GetFullPath(t.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } open)
                open.AddConcepts(concepts);
            else
                using (var tb = new Termbase(path)) tb.AddConcepts(concepts);
            name = shared;
        }
        else
        {
            p.ProjectTermbase.AddConcepts(concepts);
            name = L.T("Projekt-Terminologie");
        }
        foreach (var t in chosen) Terms.Remove(t);
        OnPropertyChanged(nameof(HasTerms));
        Info = $"{chosen.Count} Begriffe in {name} übernommen.";
        main.Status = Info;
        main.Resources.Refresh();
    }
}
