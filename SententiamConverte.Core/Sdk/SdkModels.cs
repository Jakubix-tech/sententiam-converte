using System.Text.Json.Serialization;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Sdk;

/// <summary>Einstellungen der internen Schnittstelle – vom Gastgeber (Desktop-App, Teamserver) gesetzt.</summary>
public class SdkOptions
{
    /// <summary>Ordner, in dem die Projekte liegen.</summary>
    public required string ProjectsRoot { get; init; }

    /// <summary>KI-Einstellungen für die Agenten.</summary>
    public required Func<AiSettings> Ai { get; init; }

    /// <summary>Ein bereits geöffnetes Projekt (z. B. das im Fenster), damit alle Wege dieselbe Instanz nutzen.</summary>
    public Func<string, TranslationProject?>? OpenProject { get; init; }

    /// <summary>Vor jedem Zugriff auf ein Projekt (Desktop-App: offenes Dokument im Editor zuerst speichern).</summary>
    public Action<TranslationProject>? BeforeProjectAccess { get; init; }

    /// <summary>Ein Dokument wurde geändert (Projekt, Datei-ID, Zielsprache) – Editor bzw. Teamserver laden neu.</summary>
    public Action<TranslationProject, string, string>? DocumentChanged { get; init; }

    /// <summary>Erweiterungen, deren Befehle aufrufbar sind.</summary>
    public ExtensionManager? Extensions { get; init; }
}

public sealed record ApiInfo(string Name, string Version, string Documentation, List<string> Areas);

/// <summary>Eine Datei mit Inhalt. In JSON: <c>{ "name": …, "base64": … }</c>.</summary>
public sealed record FileContent(string Name, [property: JsonPropertyName("base64")] byte[] Content)
{
    public static FileContent FromFile(string path) => new(Path.GetFileName(path), File.ReadAllBytes(path));
}

public sealed record FormatInfo(string Id, string Name, IReadOnlyList<string> Extensions);

/// <summary>Neues Projekt; mit <see cref="Template"/> werden die Einstellungen und Datenbanken einer Projektvorlage übernommen.</summary>
public sealed record ProjectCreateDto(string Name, string SourceLanguage, List<string> TargetLanguages, string? Topic = null, string? Template = null);

public sealed record ProjectUpdateDto(string? Topic = null, int? PretranslateMinScore = null, bool? ConfirmContextMatches = null, string? SourceProfile = null,
    bool? AutoLocalize = null, string? Client = null);

public sealed record FileDto(string Id, string Name, string Format, List<string> Languages, string? PackagePath);

public sealed record ProjectDto(string Name, string SourceLanguage, List<string> TargetLanguages, string Topic, string? SourceProfile,
    int PretranslateMinScore, bool ConfirmContextMatches, bool IsShared, List<FileDto> Files, List<ResourceRef> Resources, List<string> ResourceErrors);

/// <summary>
/// Ein Segment. <see cref="ReviewBaseline"/>: bestätigte Fassung vor einer offenen Änderung des Prüfers;
/// <see cref="Errors"/>: erfasste Fehler der Fehlerbewertung; <see cref="MaxLength"/>: Höchstlänge aus der Datei.
/// </summary>
public sealed record SegmentDto(int Id, string Source, string Target, SegmentStatus Status, TranslationOrigin Origin, int? MatchPercent,
    string? Comment, string? Context, bool Locked, List<SegmentRevision> History, string? ReviewBaseline = null, List<Qa.LqaError>? Errors = null,
    int? MaxLength = null);

public sealed record SegmentUpdateDto(string? Target = null, SegmentStatus? Status = null, string? Comment = null);

public sealed record ConfirmDto(SegmentStatus? Status = null);

public sealed record ConfirmResult(SegmentDto Segment, int Propagated);

public sealed record SplitDto(int SourceIndex);

public sealed record PretranslateDto(bool ConfirmExact = false, bool Overwrite = false);

public sealed record PretranslateResult(string File, string Language, int Applied, int Confirmed);

public sealed record AnalysisRowDto(string Language, string Band, int Segments, int Words, int Characters);

public sealed record QaIssueDto(string File, string Language, int SegmentId, string Severity, string Check, string Message);

public sealed record TmAddDto(string SourceLanguage, string TargetLanguage, string Source, string Target,
    string? Previous = null, string? Next = null, string? Structure = null);

public sealed record ConceptDto(List<TermDto> Terms, string? Definition = null, string? Domain = null, string? Source = null);

public sealed record AlignRequest(FileContent Source, FileContent Target, string SourceLanguage, string TargetLanguage, string? ImportIntoProject = null);

/// <summary>Entweder ein Segment (Projekt, Datei, Sprache, Segment) oder ein freier Text mit Sprachen.</summary>
public sealed record TranslateRequest(string? Project = null, string? File = null, string? Language = null, int? SegmentId = null,
    string? Text = null, string? SourceLanguage = null, string? TargetLanguage = null, string? Topic = null, string? ProviderId = null);

public sealed record TranslationResult(string Translation);

public sealed record EvaluateRequest(string Source, string SourceLanguage, string TargetLanguage, List<TranslationCandidate> Candidates,
    string? Topic = null, bool CheckUsage = false, string? Project = null);

public sealed record UsageRequest(string Expression, string Language, string? Topic = null, List<string>? Alternatives = null);

public sealed record ResearchRequest(string Topic, string SourceLanguage, List<string> TargetLanguages, List<string>? SeedTerms = null,
    int PagesPerLanguage = 3, string? SourceProfile = null, string? SaveToProject = null);

public sealed record KnowledgeAgentRequest(string Project, string Topic, List<string> Languages, string? Focus = null, int PagesPerLanguage = 3,
    string? KnowledgeBase = null, string? SourceProfile = null);

public sealed record CompareRequest(string Term, string SourceLanguage, string TargetLanguage, string? Context = null, string? Topic = null,
    string? SourceProfile = null, List<string>? Candidates = null, string? Project = null, bool SaveToTermbase = false, bool SaveToKnowledge = false);

// ---------- Lektorat, Angebot, TM-Pflege, alte Übersetzungen, Team, Automatisierung (seit 2.1) ----------

/// <summary>Korrigierte Prüfdatei (Word) zurückgeben.</summary>
public sealed record ReviewImportDto(FileContent File, bool MarkUnchangedReviewed = false);

/// <summary>Ein Fehler für die Fehlerbewertung.</summary>
public sealed record LqaErrorDto(string Category, Qa.LqaSeverity Severity, string? Note = null);

/// <summary>Angebot für alle Zielsprachen.</summary>
public sealed record QuoteDto(List<Analysis.Quote> Quotes, decimal Net, decimal Vat, decimal Total, string Currency, Analysis.RateUnit Unit);

/// <summary>Suchen und Ersetzen im Projekt-TM; mit <see cref="Preview"/> wird nichts geändert.</summary>
public sealed record TmReplaceDto(string Find, string Replace, bool InSource = false, bool InTarget = true, bool MatchCase = false,
    bool Regex = false, bool WholeWords = false, bool Preview = false);

public sealed record TmReplaceResult(int Count, List<Tm.TmReplacement> Changes);

public sealed record TmCleanDto(bool Duplicates = true, bool EmptyTargets = true, bool Identical = false);

/// <summary>Gleiche Quelle, verschiedene Übersetzungen.</summary>
public sealed record InconsistentDto(string Source, List<Tm.TmUnit> Units);

/// <summary>Fachbegriffe aus den Projektdateien gewinnen (für eine Zielsprache).</summary>
public sealed record ExtractTermsDto(string Language, bool UseAi = true, int Max = 150);

/// <summary>Alte Übersetzungen: alle Dateien auf einmal; zugeordnet wird über Dateiname, Sprache und Zahlen.</summary>
public sealed record ImportTranslationsDto(List<FileContent> Files, string? SourceLanguage = null, List<string>? TargetLanguages = null,
    bool Shared = true, bool UseAi = false, bool SuggestTerms = false, bool KeepTags = true);

public sealed record ImportedPairDto(string Source, string? Target, string SourceLanguage, string TargetLanguage, string How, int Imported, int Uncertain, string Tm);

public sealed record ImportTranslationsResult(List<ImportedPairDto> Pairs, List<string> Unpaired, List<Alignment.TermSuggestion> Terms);

/// <summary>Eine erkannte geschützte Angabe (Datenschutz).</summary>
public sealed record DetectedDto(Privacy.SensitiveKind Kind, string Value, int Count);

/// <summary>Arbeitsschritt einer Datei in einer Zielsprache.</summary>
public sealed record FileStepDto(string FileId, string FileName, FileStep Step);

/// <summary>Einen Eingangsordner einmal verarbeiten (nur Verwaltung, Pfade auf dem Rechner des Dienstes).</summary>
public sealed record WatchRunDto(string Folder, bool Pretranslate = true, bool AiTranslate = false, bool ExportTargets = true, string? OutputFolder = null);

/// <summary>Freien Sprachdatensatz laden und anbinden (nur Verwaltung).</summary>
public sealed record LanguageDataInstallDto(string Id, string SourceLanguage, string TargetLanguage, bool ForLanguages = true);

/// <summary>Aufnahme (WAV) in Text umwandeln – mit der Spracherkennung aus den KI-Einstellungen.</summary>
public sealed record TranscribeDto(FileContent Audio, string Language);

public sealed record ExtensionCommandInfo(string Id, string Name, string? Description, string Scope);

public sealed record ExtensionInfo(string Id, string Name, string Version, string? Author, string? Description, bool Enabled, bool Loaded,
    string? Error, List<ExtensionCommandInfo> Commands, List<string> Contributions);

public sealed record ExtensionRunDto(string? Project = null, string? File = null, string? Language = null, int? SegmentId = null,
    Dictionary<string, string>? Arguments = null);

public static class Dto
{
    public static FileDto File(TranslationProject p, ProjectFile f) => new(f.Id, f.OriginalName, f.FilterId, p.LanguagesOf(f).ToList(), f.PackagePath);

    public static ProjectDto Project(TranslationProject p) => new(p.Name, p.SourceLanguage, p.TargetLanguages, p.Topic, p.SourceProfile,
        p.PretranslateMinScore, p.ConfirmContextMatches, p.IsShared, p.Files.Select(f => File(p, f)).ToList(), p.Resources, p.ResourceErrors);

    public static SegmentDto Segment(BilingualDocument d, Segment s) => new(s.Id, s.Source, s.Target, s.Status, s.Origin, s.MatchPercent,
        s.Comment, d.ParagraphOf(s)?.Context, s.Locked, s.History, s.ReviewBaseline, s.Errors, s.MaxLength);
}
