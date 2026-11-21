using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Sdk;

/// <summary>
/// Programmierschnittstelle mit allen Funktionen von Sententiam converte.
/// <list type="bullet">
/// <item><b>Intern</b>: in Erweiterungen über <see cref="ExtensionContext.Api"/> – direkt im Programm, ohne Netz.</item>
/// <item><b>Extern</b>: <c>SententiamClient</c> (Paket SententiamConverte.Sdk) spricht die REST-API eines Teamservers
/// oder der Desktop-App an.</item>
/// </list>
/// Beide verhalten sich gleich; Code, der gegen diese Schnittstelle geschrieben ist, läuft in beiden Fällen.
/// <para>
/// Bezeichner: <c>project</c> ist der Projektname, <c>file</c> die Datei-ID oder der Dateiname, <c>language</c> die
/// Zielsprache, <c>id</c> die Segmentnummer. Fehler: <see cref="KeyNotFoundException"/> (Projekt, Datei oder Segment
/// fehlt), <see cref="ArgumentException"/> (ungültige Angabe), <see cref="UnauthorizedAccessException"/> (keine
/// Berechtigung), <see cref="InvalidOperationException"/> (KI-Anbieter oder Server nicht erreichbar).
/// </para>
/// </summary>
public interface ISententiam
{
    // ---------- Allgemein ----------

    Task<ApiInfo> InfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FormatInfo>> FormatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SourceProfile>> SourceProfilesAsync(CancellationToken ct = default);

    // ---------- Projekte ----------

    Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken ct = default);
    Task<ProjectDto> CreateProjectAsync(ProjectCreateDto project, CancellationToken ct = default);
    Task<ProjectDto> GetProjectAsync(string project, CancellationToken ct = default);
    Task<ProjectDto> UpdateProjectAsync(string project, ProjectUpdateDto changes, CancellationToken ct = default);

    /// <summary>TM, Terminologie oder Wissensdatenbank anbinden (lokal oder Teamserver, auf Wunsch nur für bestimmte Sprachen).</summary>
    Task<ProjectDto> AttachResourceAsync(string project, ResourceRef resource, CancellationToken ct = default);
    Task<ProjectDto> DetachResourceAsync(string project, ResourceKind kind, string location, string? server = null, CancellationToken ct = default);

    // ---------- Dateien ----------

    Task<IReadOnlyList<FileDto>> FilesAsync(string project, CancellationToken ct = default);
    Task<FileDto> AddFileAsync(string project, FileContent file, CancellationToken ct = default);
    Task RemoveFileAsync(string project, string file, CancellationToken ct = default);
    Task<IReadOnlyList<SegmentDto>> SegmentsAsync(string project, string file, string language, CancellationToken ct = default);

    /// <summary>Zieldatei im Originalformat.</summary>
    Task<FileContent> TargetFileAsync(string project, string file, string language, CancellationToken ct = default);

    /// <summary>Zweisprachiges XLIFF zum Austausch.</summary>
    Task<FileContent> XliffAsync(string project, string file, string language, CancellationToken ct = default);

    /// <summary>Suchen und Ersetzen im Zieltext; liefert die Zahl der Ersetzungen.</summary>
    Task<int> ReplaceAsync(string project, string file, string language, string find, string replace, bool matchCase = false, CancellationToken ct = default);

    // ---------- Segmente ----------

    Task<SegmentDto> GetSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default);

    /// <summary>Übersetzung, Status oder Kommentar setzen (nicht angegebene Felder bleiben).</summary>
    Task<SegmentDto> UpdateSegmentAsync(string project, string file, string language, int id, SegmentUpdateDto update, CancellationToken ct = default);

    /// <summary>Bestätigen: ins TM schreiben (mit Kontext) und auf gleiche Segmente übertragen.</summary>
    Task<ConfirmResult> ConfirmSegmentAsync(string project, string file, string language, int id, SegmentStatus? status = null, CancellationToken ct = default);

    /// <summary>TM-Treffer mit Kontextbewertung (101 = Kontexttreffer).</summary>
    Task<IReadOnlyList<TmMatch>> MatchesAsync(string project, string file, string language, int id, int? minScore = null, CancellationToken ct = default);
    Task<IReadOnlyList<TermHit>> SegmentTermsAsync(string project, string file, string language, int id, CancellationToken ct = default);
    Task<IReadOnlyList<FragmentMatch>> FragmentsAsync(string project, string file, string language, int id, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeEntry>> SegmentKnowledgeAsync(string project, string file, string language, int id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SuggestionsAsync(string project, string file, string language, int id, CancellationToken ct = default);

    /// <summary>TM-Treffer Nr. <paramref name="number"/> (ab 1, wie Strg+1) einsetzen; Zahlen werden angepasst.</summary>
    Task<SegmentDto> ApplyMatchAsync(string project, string file, string language, int id, int number = 1, CancellationToken ct = default);
    Task<SegmentDto> CopySourceAsync(string project, string file, string language, int id, CancellationToken ct = default);

    /// <summary>An einer Stelle im Quelltext teilen; liefert beide Segmente.</summary>
    Task<IReadOnlyList<SegmentDto>> SplitSegmentAsync(string project, string file, string language, int id, int sourceIndex, CancellationToken ct = default);
    Task<SegmentDto> MergeSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default);

    // ---------- Stapelaufgaben und Qualität ----------

    Task<IReadOnlyList<PretranslateResult>> PretranslateAsync(string project, PretranslateDto? options = null, CancellationToken ct = default);
    Task<IReadOnlyList<AnalysisRowDto>> AnalysisAsync(string project, CancellationToken ct = default);
    Task<IReadOnlyList<QaIssueDto>> QaAsync(string project, CancellationToken ct = default);
    Task<QaSettings> SetQaSettingsAsync(string project, QaSettings settings, CancellationToken ct = default);

    // ---------- Translation Memory ----------

    Task<IReadOnlyList<TmMatch>> SearchTmAsync(string project, string source, string sourceLanguage, string targetLanguage,
        int? minScore = null, string? previous = null, string? next = null, CancellationToken ct = default);
    Task<IReadOnlyList<TmUnit>> ConcordanceAsync(string project, string text, string sourceLanguage, string targetLanguage, bool inTarget = false, CancellationToken ct = default);
    Task AddToTmAsync(string project, TmAddDto unit, CancellationToken ct = default);
    Task<IReadOnlyList<TmUnit>> TmUnitsAsync(string project, string? filter = null, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task UpdateTmUnitAsync(string project, long id, string source, string target, CancellationToken ct = default);
    Task DeleteTmUnitAsync(string project, long id, CancellationToken ct = default);

    /// <summary>TMX oder .sdltm ins Projekt-TM; liefert die Zahl der übernommenen Einheiten.</summary>
    Task<int> ImportTmAsync(string project, FileContent file, CancellationToken ct = default);
    Task<FileContent> ExportTmAsync(string project, CancellationToken ct = default);

    // ---------- Terminologie ----------

    /// <summary>Ohne <paramref name="query"/>: alle Begriffe der Projekt-Terminologie.</summary>
    Task<IReadOnlyList<Concept>> SearchTermsAsync(string project, string? query = null, string? language = null, CancellationToken ct = default);
    Task<long> AddConceptAsync(string project, ConceptDto concept, CancellationToken ct = default);
    Task DeleteConceptAsync(string project, long id, CancellationToken ct = default);
    Task<int> ImportTermsAsync(string project, FileContent file, CancellationToken ct = default);
    Task<FileContent> ExportTermsAsync(string project, CancellationToken ct = default);

    // ---------- Wissen ----------

    Task<IReadOnlyList<KnowledgeEntry>> SearchKnowledgeAsync(string project, string? query = null, string? language = null, CancellationToken ct = default);
    Task<long> AddKnowledgeAsync(string project, KnowledgeEntry entry, CancellationToken ct = default);
    Task UpdateKnowledgeAsync(string project, long id, KnowledgeEntry entry, CancellationToken ct = default);
    Task DeleteKnowledgeAsync(string project, long id, CancellationToken ct = default);

    // ---------- Pakete, Alignment, Texterkennung ----------

    /// <summary>Projektpaket (.sdlppx) als neues Projekt übernehmen.</summary>
    Task<ProjectDto> ImportPackageAsync(FileContent package, string? topic = null, string? name = null, CancellationToken ct = default);

    /// <summary>Rückgabepaket (.sdlrpx).</summary>
    Task<FileContent> ReturnPackageAsync(string project, CancellationToken ct = default);
    Task<IReadOnlyList<AlignedPair>> AlignAsync(AlignRequest request, CancellationToken ct = default);

    /// <summary>Gescanntes PDF per KI erkennen und als DOCX ins Projekt aufnehmen.</summary>
    Task<FileDto> RecognizePdfAsync(string project, FileContent pdf, CancellationToken ct = default);

    // ---------- Agenten ----------

    Task<TranslationResult> TranslateAsync(TranslateRequest request, CancellationToken ct = default);
    Task<EvaluationResult> EvaluateAsync(EvaluateRequest request, CancellationToken ct = default);
    Task<UsageResult> CheckUsageAsync(UsageRequest request, CancellationToken ct = default);
    Task<ResearchResult> ResearchAsync(ResearchRequest request, CancellationToken ct = default);
    Task<KnowledgeRunResult> FillKnowledgeAsync(KnowledgeAgentRequest request, CancellationToken ct = default);
    Task<TermComparisonResult> CompareTermAsync(CompareRequest request, CancellationToken ct = default);

    // ---------- Lektorat und Fehlerbewertung ----------

    /// <summary>Prüfdatei (Word-Tabelle) für das Lektorat ohne Übersetzungsprogramm.</summary>
    Task<FileContent> ReviewFileAsync(string project, string file, string language, CancellationToken ct = default);

    /// <summary>Korrigierte Prüfdatei einlesen: Änderungen als lektoriert, Anmerkungen und Word-Kommentare als Segmentkommentare.</summary>
    Task<Exchange.ReviewImportResult> ImportReviewAsync(string project, string file, string language, ReviewImportDto review, CancellationToken ct = default);

    /// <summary>Offene Änderung des Prüfers annehmen (die vorige Fassung entfällt).</summary>
    Task<SegmentDto> AcceptChangeAsync(string project, string file, string language, int id, CancellationToken ct = default);

    /// <summary>Offene Änderung des Prüfers ablehnen (die vorige Fassung gilt wieder).</summary>
    Task<SegmentDto> RejectChangeAsync(string project, string file, string language, int id, CancellationToken ct = default);

    Task<SegmentDto> AddErrorAsync(string project, string file, string language, int id, LqaErrorDto error, CancellationToken ct = default);

    /// <summary>Erfassten Fehler Nr. <paramref name="index"/> (ab 0) entfernen.</summary>
    Task<SegmentDto> RemoveErrorAsync(string project, string file, string language, int id, int index, CancellationToken ct = default);

    /// <summary>Fehlerbewertung (Punktzahl, Kategorien) – des Projekts oder einer Datei in einer Zielsprache.</summary>
    Task<LqaReport> EvaluationAsync(string project, string? file = null, string? language = null, CancellationToken ct = default);

    /// <summary>Bewertungsbericht als Word-Datei.</summary>
    Task<FileContent> EvaluationReportAsync(string project, string file, string language, CancellationToken ct = default);
    Task<LqaSettings> SetLqaSettingsAsync(string project, LqaSettings settings, CancellationToken ct = default);

    // ---------- Zahlen, Pseudoübersetzung, Angebot ----------

    /// <summary>Zahlen und Datum in allen Dateien in die Schreibweise der Zielsprache bringen; liefert die Zahl geänderter Segmente.</summary>
    Task<int> LocalizeNumbersAsync(string project, CancellationToken ct = default);

    /// <summary>Zieldatei mit Pseudoübersetzung (Akzentbuchstaben, 30 % länger) – das Projekt bleibt unverändert.</summary>
    Task<FileContent> PseudoTranslationAsync(string project, string file, string language, CancellationToken ct = default);

    /// <summary>Angebot aus der Analyse; mit <paramref name="rates"/> werden die Preise des Projekts vorher gesetzt.</summary>
    Task<QuoteDto> QuoteAsync(string project, Analysis.RateSettings? rates = null, CancellationToken ct = default);

    /// <summary>Angebot als Word-Datei.</summary>
    Task<FileContent> QuoteDocumentAsync(string project, CancellationToken ct = default);

    // ---------- Vorlagen und QA-Profile ----------

    Task<IReadOnlyList<Projects.ProjectTemplate>> TemplatesAsync(CancellationToken ct = default);

    /// <summary>Einstellungen und Datenbanken des Projekts als Vorlage speichern (gleichnamige wird ersetzt).</summary>
    Task<Projects.ProjectTemplate> SaveTemplateAsync(string project, string name, CancellationToken ct = default);
    Task<IReadOnlyList<Projects.QaProfile>> QaProfilesAsync(CancellationToken ct = default);
    Task<Projects.QaProfile> SaveQaProfileAsync(string project, string name, CancellationToken ct = default);
    Task<QaSettings> ApplyQaProfileAsync(string project, string name, CancellationToken ct = default);

    // ---------- TM-Pflege, Fachbegriffe, alte Übersetzungen ----------

    /// <summary>Suchen und Ersetzen im Projekt-TM (Tags bleiben unberührt; gleich gewordene Einträge werden zusammengeführt).</summary>
    Task<TmReplaceResult> ReplaceInTmAsync(string project, TmReplaceDto request, CancellationToken ct = default);
    Task<Tm.TmCleanResult> CleanTmAsync(string project, TmCleanDto request, CancellationToken ct = default);
    Task<IReadOnlyList<InconsistentDto>> InconsistentTmAsync(string project, CancellationToken ct = default);

    /// <summary>Fachbegriffe aus den Projektdateien; mit KI ausgewählt und mit Übersetzungsvorschlag.</summary>
    Task<IReadOnlyList<Alignment.TermSuggestion>> ExtractTermsAsync(string project, ExtractTermsDto request, CancellationToken ct = default);

    /// <summary>Alte Übersetzungen (Original und Übersetzung, auch mehrere Paare oder XLIFF) zuordnen, ausrichten und ins TM übernehmen.</summary>
    Task<ImportTranslationsResult> ImportTranslationsAsync(string project, ImportTranslationsDto request, CancellationToken ct = default);

    // ---------- Datenschutz ----------

    Task<Privacy.PrivacySettings> SetPrivacyAsync(string project, Privacy.PrivacySettings settings, CancellationToken ct = default);

    /// <summary>Was in den Projektdateien geschützt würde (Namen, Datum, Aktenzeichen …).</summary>
    Task<IReadOnlyList<DetectedDto>> DetectSensitiveAsync(string project, CancellationToken ct = default);

    // ---------- Team: Arbeitsschritte ----------

    Task<IReadOnlyList<FileStepDto>> WorkflowAsync(string project, CancellationToken ct = default);

    /// <summary>Schritt oder Zuständige setzen (Verwaltung) bzw. abgeben (<c>Advance</c>, wer an der Reihe ist).</summary>
    Task<FileStep> SetWorkflowAsync(string project, string file, string language, WorkflowUpdate update, CancellationToken ct = default);

    // ---------- Automatisierung, Sprachdaten, Diktat ----------

    /// <summary>Neue Dateien eines Eingangsordners einmal verarbeiten (nur Verwaltung).</summary>
    Task<IReadOnlyList<WatchResult>> ProcessFolderAsync(string project, WatchRunDto request, CancellationToken ct = default);

    /// <summary>Freie Sprachdaten (OPUS, IATE) für ein Sprachpaar.</summary>
    Task<IReadOnlyList<LanguageDataSet>> LanguageDataAsync(string sourceLanguage, string targetLanguage, CancellationToken ct = default);

    /// <summary>Datensatz laden und an das Projekt bzw. seine Sprachen binden (nur Verwaltung).</summary>
    Task<ProjectDto> InstallLanguageDataAsync(string project, LanguageDataInstallDto request, CancellationToken ct = default);

    /// <summary>Aufnahme in Text umwandeln (Spracherkennung aus den KI-Einstellungen).</summary>
    Task<string> TranscribeAsync(TranscribeDto request, CancellationToken ct = default);

    // ---------- Erweiterungen ----------

    Task<IReadOnlyList<ExtensionInfo>> ExtensionsAsync(CancellationToken ct = default);
    Task<ExtensionResult> RunCommandAsync(string command, ExtensionRunDto request, CancellationToken ct = default);
}
