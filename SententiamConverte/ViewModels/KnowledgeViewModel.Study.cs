using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein eingearbeiteter Begriff in der Vorschau: Fundstelle, Benennung, Zielbenennung, Definition.</summary>
public sealed class StudiedTermRow(StudiedTerm term)
{
    public StudiedTerm Term { get; } = term;
    public string Header => $"{Term.Section} · {Term.Term}" + (Term.Target is { } t ? $" → {t}" : "");
    public string Definition => Term.Definition;
    public string Kind => Term.LegalDefinition ? L.T("Legaldefinition") : L.T("aus der Überschrift");
}

/// <summary>
/// Einarbeiten in ein Gesetz: amtliche Fassung laden, jeden Paragraphen in den eigenen Fachtexten durchsuchbar machen,
/// Begriffe mit Definition und Fundstelle in die gemeinsame Terminologie des Sprachpaars und in die gewählte
/// Wissensdatenbank legen.
/// </summary>
public partial class KnowledgeViewModel
{
    private CancellationTokenSource? _studyCts;

    [ObservableProperty] public partial string StudyAbbreviation { get; set; } = "StGB";
    [ObservableProperty] public partial string StudyTargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial bool StudyToTermbase { get; set; } = true;
    [ObservableProperty] public partial bool StudyToKnowledge { get; set; } = true;
    [ObservableProperty] public partial bool IsStudying { get; set; }
    [ObservableProperty] public partial string StudyLog { get; set; } = "";

    public ObservableCollection<StudiedTermRow> StudiedTerms { get; } = new();

    /// <summary>Häufige Bundesgesetze als Vorschlag (Abkürzung wie auf gesetze-im-internet.de).</summary>
    public string StudyExamples => "StGB, StPO, BGB, ZPO, HGB, GG, AO, OWiG, StVO, ArbSchG, BetrVG, UrhG, InsO, GmbHG";

    /// <summary>Zielsprache aus dem offenen Projekt vorschlagen.</summary>
    private void SuggestStudyLanguage()
    {
        if (_main.Project?.TargetLanguages.FirstOrDefault(t => !Languages.Matches(t, "de-DE")) is { } target) StudyTargetLanguage = target;
    }

    [RelayCommand]
    private async Task Study()
    {
        if (IsStudying) return;
        var abbr = StudyAbbreviation.Trim();
        if (abbr.Length == 0) { StudyLog = "Bitte die Abkürzung des Gesetzes angeben, z. B. StGB." + "\n"; return; }
        var typed = StudyTargetLanguage.Trim();
        var target = typed.Length > 0 && !Languages.Matches(typed, "de-DE") ? typed : null;
        if (Bases.Count == 0) OnProjectChanged();
        IsStudying = true;
        StudyLog = "";
        StudiedTerms.Clear();
        var cts = _studyCts = new CancellationTokenSource();
        var log = new LogLines(m => StudyLog += m + "\n");
        Termbase? ownTermbase = null;
        KnowledgeBase? ownKnowledge = null;
        try
        {
            ITermbase? termbase = null;
            string? termbaseName = null;
            if (StudyToTermbase && target == null) StudyLog += "Ohne Zielsprache keine Terminologie – Begriffe kommen nur ins Wissen und in die eigenen Fachtexte." + "\n";
            else if (StudyToTermbase)
            {
                var (name, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, "de-DE", target!, _main.Project);
                // die vom Projekt geöffnete Terminologie mitbenutzen, damit ihr Zwischenspeicher stimmt
                termbase = _main.Project?.Termbases.OfType<Termbase>().FirstOrDefault(x => SamePath(x.Path, path)) ?? (ownTermbase = new Termbase(path));
                termbaseName = name;
            }
            IKnowledgeBase? knowledge = null;
            if (StudyToKnowledge && SelectedBase != null)
                knowledge = SelectedBase.IsRemote ? SelectedBase : ownKnowledge = new KnowledgeBase(SelectedBase.Location, SelectedBase.Name);
            else if (StudyToKnowledge)
            {
                var path = SharedKnowledge("de-DE");                    // noch keine Datenbank da: die deutsche des Benutzers
                knowledge = ownKnowledge = new KnowledgeBase(path, Path.GetFileNameWithoutExtension(path));
            }
            var library = ReferenceLibrary.Shared(ReferenceLibrarySettings.Load().IndexPath);

            var result = await LawStudy.StudyAsync(abbr, target, library, termbase, knowledge, log, cts.Token);
            foreach (var term in result.Terms.OrderByDescending(x => x.LegalDefinition).ThenBy(x => SectionOrder(x.Section)).Take(400))
                StudiedTerms.Add(new StudiedTermRow(term));
            _main.Status = L.T($"{result.Abbreviation} eingearbeitet: {result.TermsSaved} Begriffe in „{termbaseName ?? "–"}“, {result.KnowledgeSaved} Einträge in „{knowledge?.Name ?? "–"}“, {result.Sections} Paragraphen in den eigenen Fachtexten.");
            if (SelectedBase == null && knowledge != null)
            {
                OnProjectChanged();
                SelectedBase = Bases.FirstOrDefault(b => !b.IsRemote && SamePath(b.Location, knowledge.Location)) ?? SelectedBase;
            }
            Load();
            _main.Resources.Refresh();
            _main.Resources.RefreshDatabases();
            _main.Resources.RefreshLibrary();
        }
        catch (OperationCanceledException) { StudyLog += "Abgebrochen." + "\n"; }
        catch (Exception ex) when (ex is AiException or HttpRequestException or IOException or Microsoft.Data.Sqlite.SqliteException or RemoteException or System.Xml.XmlException)
        {
            StudyLog += "Fehler: " + ex.Message + "\n";
        }
        finally
        {
            ownTermbase?.Dispose();
            ownKnowledge?.Dispose();
            IsStudying = false;
        }
    }

    [RelayCommand]
    private void CancelStudy() => _studyCts?.Cancel();

    /// <summary>Die an die Sprache gebundene Wissensdatenbank des Benutzers; gibt es keine, wird „Wissen de-DE“ angelegt.</summary>
    private string SharedKnowledge(string language)
    {
        var existing = LanguageResources.Load().Concat(_main.Project?.Resources ?? [])
            .Where(r => r.Kind == ResourceKind.Knowledge && !r.IsRemote && r.WriteBack && r.Languages.Count > 0 && r.AppliesTo(language))
            .FirstOrDefault(r => File.Exists(r.Location));
        if (existing != null) return existing.Location;
        var name = $"Wissen {language}";
        var r = new DatabaseRecommendation(ResourceKind.Knowledge, name, DatabaseCatalog.SuggestedPath(ResourceKind.Knowledge, name), [language], "");
        DatabaseCatalog.Create(r, _main.Project is { } p && Languages.Matches(p.SourceLanguage, language) ? p : null);
        return r.Path;
    }

    /// <summary>„§ 11“ vor „§ 11a“ vor „§ 12“ (Artikel ebenso).</summary>
    private static (int, string) SectionOrder(string section)
    {
        var digits = new string(section.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return (int.TryParse(digits, out var n) ? n : int.MaxValue, section);
    }
}
