using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Eintrag des Recherche-Gedächtnisses in der Liste: Ausdruck → Empfehlung, belegt?, aus welchem Projekt, Thema, Datum.</summary>
public sealed class MemoryRow(ResearchMemoryEntry entry)
{
    public ResearchMemoryEntry Entry { get; } = entry;
    public string Header => $"{Entry.Dossier.Expression} → {Entry.Dossier.Recommended ?? "?"}";
    public string Info => L.T(DossierStore.IsEvidenced(Entry.Dossier) ? "belegt" : "nicht belegt") + " · " + Entry.Project +
                          (string.IsNullOrWhiteSpace(Entry.Topic) ? "" : " · " + Entry.Topic) + " · " + Entry.Saved.ToLocalTime().ToString("dd.MM.yyyy");
    public string Meaning => AiTranslator.Short(Entry.Dossier.Meaning, 200) ?? "";
    public bool HasMeaning => Meaning.Length > 0;
}

/// <summary>
/// Recherche-Gedächtnis über alle Projekte (siehe <see cref="ResearchMemory"/>): vor jeder Recherche nachschauen, was
/// schon belegt ist, und die Einträge des Sprachpaars als Liste im Reiter Recherche – durchsuchen und entfernen.
/// </summary>
public partial class EditorViewModel
{
    public ObservableCollection<MemoryRow> MemoryEntries { get; } = new();
    [ObservableProperty] public partial bool IsMemoryExpanded { get; set; }
    [ObservableProperty] public partial string MemoryFilter { get; set; } = "";
    [ObservableProperty] public partial string MemoryInfo { get; set; } = "";

    partial void OnIsMemoryExpandedChanged(bool value)
    {
        if (value) RefreshMemory();
    }

    partial void OnMemoryFilterChanged(string value) => RefreshMemory();

    /// <summary>Die Einträge des Sprachpaars dieses Dokuments (gefiltert).</summary>
    public void RefreshMemory()
    {
        MemoryEntries.Clear();
        if (_doc == null) { MemoryInfo = ""; return; }
        var from = ResearchFrom ?? _doc.SourceLanguage;
        var to = ResearchTo ?? _doc.TargetLanguage;
        // nur Einträge mit Erklärungen in der eingestellten Sprache – keine deutschen Erklärungen, wenn auf Polnisch erklärt wird
        var language = AppServices.Ai.ExplanationLanguage;
        var everything = ResearchMemory.Entries(from, to);
        var all = everything.Where(e => ResearchMemory.ExplainedIn(e.Dossier).Equals(language, StringComparison.OrdinalIgnoreCase)).ToList();
        var filter = MemoryFilter.Trim();
        foreach (var e in all.Where(e => filter.Length == 0
                                         || e.Dossier.Expression.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                         || (e.Dossier.Recommended ?? "").Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                         || (e.Topic ?? "").Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                             .OrderBy(e => e.Dossier.Expression, StringComparer.CurrentCultureIgnoreCase).Take(300))
            MemoryEntries.Add(new MemoryRow(e));
        MemoryInfo = L.T($"{all.Count} Einträge ({all.Count(e => DossierStore.IsEvidenced(e.Dossier))} belegt) für {Languages.DisplayName(from)} → {Languages.DisplayName(to)}")
                     + (everything.Count > all.Count ? " " + L.T($"– dazu {everything.Count - all.Count} mit Erklärungen in einer anderen Sprache (nicht gezeigt, nicht übernommen).") : "");
    }

    /// <summary>Einen Eintrag aus dem Gedächtnis entfernen (die Dossiers der Projekte bleiben).</summary>
    [RelayCommand]
    private void ForgetMemoryEntry(MemoryRow? row)
    {
        if (row == null) return;
        ResearchMemory.Forget(row.Entry.Dossier.SourceLanguage, row.Entry.Dossier.TargetLanguage, row.Entry.Dossier.Expression,
            ResearchMemory.ExplainedIn(row.Entry.Dossier));
        RefreshMemory();
        _main.Status = L.T($"„{row.Entry.Dossier.Expression}“ aus dem Recherche-Gedächtnis entfernt.");
    }

    /// <summary>
    /// Vor einer Recherche ins Gedächtnis schauen: ein belegtes Ergebnis zum selben Thema wird übernommen – mit Herkunft, als
    /// Dossier dieses Projekts, ohne neue Recherche (null: neu recherchieren, ab <paramref name="previous"/>).
    /// </summary>
    private TermDossier? Recall(TranslationProject project, ResearchItem item, string from, string to, TermDossier? projectDossier, out TermDossier? previous)
    {
        var (reuse, prior) = ResearchMemory.Lookup(item.Expression, from, to, project.Topic, projectDossier, AppServices.Ai.ExplanationLanguage);
        previous = prior;
        if (reuse == null) return null;
        reuse.SegmentIds = item.SegmentIds.ToList();
        reuse.FileId = CurrentFile?.Id;
        reuse.Context = item.Context;
        AiLog += L.T($"„{item.Expression}“ → „{reuse.Recommended}“ aus dem Recherche-Gedächtnis übernommen (belegt, aus {reuse.Origin}) – keine neue Recherche nötig.") + "\n";
        return reuse;
    }
}
