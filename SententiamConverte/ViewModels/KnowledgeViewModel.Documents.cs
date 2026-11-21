using System;
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
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine eingelesene Datei der eigenen Fachtexte zur Auswahl fürs Einarbeiten.</summary>
public sealed partial class StudyFileRow(LibraryFile file) : ObservableObject
{
    public LibraryFile File { get; } = file;
    [ObservableProperty] public partial bool IsChecked { get; set; } = true;
    public string Name => Path.GetFileName(File.Path);
    public string Info => string.Join(" · ", new[]
    {
        File.Language is { } l ? Languages.DisplayName(l) : L.T("Sprache unbekannt"),
        File.Pages > 0 ? L.T($"{File.Pages} Seiten") : L.T($"{File.Passages} Abschnitte"),
        File.Version is { } v ? L.T(v) : null,
    }.Where(x => !string.IsNullOrEmpty(x)));
}

/// <summary>Ein gefundener Begriff in der Vorschau: Benennung, Definitionssatz, Fundstelle – zum Abhaken vor dem Speichern.</summary>
public sealed partial class DocumentTermRow(DocumentTerm term) : ObservableObject
{
    public DocumentTerm Term { get; } = term;
    [ObservableProperty] public partial bool IsChecked { get; set; } = true;
    public string Header => Term.Term + (Term.Abbreviation is { } a ? $" ({a})" : "") + $" [{Term.Language}]";
    public string Definition => Term.Definition;
    public string Info => $"{Term.Label} · " + L.T($"{Term.Count}× in den eigenen Fachtexten") + " · " + L.T(Term.Kind switch
    {
        DocumentTermKind.Definition => "Definitionssatz",
        DocumentTermKind.Glossary => "Glossar",
        DocumentTermKind.Abbreviation => "Abkürzung",
        _ => "KI, Beleg wörtlich geprüft",
    });
}

/// <summary>
/// Einarbeiten in eigene Dokumente: Begriffe mit ihrem Definitionssatz aus den eingelesenen Dateien herausziehen
/// (Regeln, auf Wunsch KI mit wörtlicher Prüfung), auswählen und mit Fundstelle in Terminologie und Wissen ablegen.
/// </summary>
public partial class KnowledgeViewModel
{
    private CancellationTokenSource? _docCts;

    public ObservableCollection<StudyFileRow> StudyFiles { get; } = new();
    public ObservableCollection<DocumentTermRow> DocumentTerms { get; } = new();

    [ObservableProperty] public partial string DocSourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string DocTargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial bool DocUseAi { get; set; } = true;
    [ObservableProperty] public partial bool IsStudyingDocs { get; set; }
    [ObservableProperty] public partial string DocLog { get; set; } = "";
    [ObservableProperty] public partial bool HasStudyFiles { get; set; }
    [ObservableProperty] public partial bool HasDocumentTerms { get; set; }

    /// <summary>Die eingelesenen Dateien zur Auswahl (eingearbeitete Gesetze und unlesbare Dateien nicht).</summary>
    [RelayCommand]
    private void RefreshStudyFiles()
    {
        var checkedBefore = StudyFiles.Where(f => !f.IsChecked).Select(f => f.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        StudyFiles.Clear();
        var settings = ReferenceLibrarySettings.Load();
        if (File.Exists(settings.IndexPath))
            try
            {
                foreach (var f in ReferenceLibrary.Shared(settings.IndexPath).Files().Where(f => f.Problem == null && !f.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
                    StudyFiles.Add(new StudyFileRow(f) { IsChecked = !checkedBefore.Contains(f.Path) });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { DocLog = "Suchindex nicht lesbar: " + ex.Message; }
        HasStudyFiles = StudyFiles.Count > 0;
        if (_main.Project is { } p)
        {
            DocSourceLanguage = p.SourceLanguage;
            if (p.TargetLanguages.FirstOrDefault() is { } t) DocTargetLanguage = t;
        }
    }

    [RelayCommand]
    private async Task ExtractDocumentTerms()
    {
        if (IsStudyingDocs) return;
        var files = StudyFiles.Where(f => f.IsChecked).Select(f => f.File).ToList();
        if (files.Count == 0) { DocLog = "Bitte mindestens ein Dokument anhaken."; return; }
        IsStudyingDocs = true;
        DocLog = "";
        DocumentTerms.Clear();
        HasDocumentTerms = false;
        var cts = _docCts = new CancellationTokenSource();
        var log = new LogLines(m => DocLog += m + "\n");
        try
        {
            IChatModel? model = null;
            if (DocUseAi)
                try
                {
                    var ctx = AgentContext.From(AppServices.Ai, AppServices.Ai.ResearchProviderId, log);
                    // vertrauliche Unterlagen: Online-KI nur, wenn es bei den eigenen Fachtexten erlaubt ist
                    if (ctx.Library != null) model = ctx.Model;
                    else log.Report("Die KI läuft online und darf keine Auszüge aus den eigenen Fachtexten lesen (TM & Terminologie → Eigene Fachtexte) – es gelten nur die Regeln.");
                }
                catch (AiException) { log.Report("Keine KI eingerichtet – es gelten nur die Regeln."); }
            var library = ReferenceLibrary.Shared(ReferenceLibrarySettings.Load().IndexPath);
            var topic = _main.Project?.Topic is { Length: > 0 } t ? t : null;
            var terms = await DocumentStudy.ExtractAsync(library, files, model, topic, log, cts.Token);
            foreach (var term in terms) DocumentTerms.Add(new DocumentTermRow(term));
            HasDocumentTerms = DocumentTerms.Count > 0;
            log.Report(terms.Count == 0
                ? "Keine Begriffe mit Definitionssatz gefunden."
                : $"{terms.Count} Begriffe gefunden – prüfen, abhaken und „Übernehmen“.");
        }
        catch (OperationCanceledException) { log.Report("Abgebrochen."); }
        catch (Exception ex) when (ex is AiException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Report("Fehler: " + ex.Message);
        }
        finally { IsStudyingDocs = false; }
    }

    [RelayCommand]
    private void SaveDocumentTerms()
    {
        var chosen = DocumentTerms.Where(r => r.IsChecked).Select(r => r.Term).ToList();
        if (chosen.Count == 0) { DocLog += "Bitte mindestens einen Begriff anhaken." + "\n"; return; }
        var src = DocSourceLanguage.Trim();
        var tgt = DocTargetLanguage.Trim();
        Termbase? ownTermbase = null;
        KnowledgeBase? ownKnowledge = null;
        try
        {
            var (termbaseName, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, src, tgt, _main.Project);
            ITermbase termbase = _main.Project?.Termbases.OfType<Termbase>().FirstOrDefault(x => SamePath(x.Path, path)) ?? (ownTermbase = new Termbase(path));
            IKnowledgeBase knowledge;
            if (SelectedBase != null) knowledge = SelectedBase.IsRemote ? SelectedBase : ownKnowledge = new KnowledgeBase(SelectedBase.Location, SelectedBase.Name);
            else
            {
                var kbPath = SharedKnowledge(src);
                knowledge = ownKnowledge = new KnowledgeBase(kbPath, Path.GetFileNameWithoutExtension(kbPath));
            }
            var result = DocumentStudy.Save(chosen, src, tgt, termbase, knowledge);
            DocLog += $"Übernommen: {result.TermsSaved} Begriffe in „{termbaseName}“, {result.KnowledgeSaved} Einträge in „{knowledge.Name}“." + "\n"
                      + (result.OtherLanguage > 0 ? $"{result.OtherLanguage} Begriffe in anderen Sprachen nur ins Wissen." + "\n" : "");
            _main.Status = L.T($"Eigene Dokumente eingearbeitet: {result.TermsSaved} Begriffe, {result.KnowledgeSaved} Wissenseinträge.");
            foreach (var row in DocumentTerms.Where(r => r.IsChecked).ToList()) DocumentTerms.Remove(row);
            HasDocumentTerms = DocumentTerms.Count > 0;
            if (SelectedBase == null)
            {
                OnProjectChanged();
                SelectedBase = Bases.FirstOrDefault(b => !b.IsRemote && SamePath(b.Location, knowledge.Location)) ?? SelectedBase;
            }
            Load();
            _main.Resources.Refresh();
            _main.Resources.RefreshDatabases();
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or RemoteException)
        {
            DocLog += "Fehler: " + ex.Message + "\n";
        }
        finally
        {
            ownTermbase?.Dispose();
            ownKnowledge?.Dispose();
        }
    }

    [RelayCommand]
    private void CancelDocs() => _docCts?.Cancel();

    [RelayCommand]
    private async Task OpenDocumentTerm(DocumentTermRow? row)
    {
        if (row != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(row.Term.Uri);
    }
}
