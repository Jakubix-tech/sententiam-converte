using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Api;

/// <summary>Wer die API aufruft und was er darf.</summary>
public sealed record ApiUser(string Name, bool CanWrite, bool IsAdmin = false);

/// <summary>Einstellungen der API – vom Gastgeber (Desktop-App oder Teamserver) gesetzt.</summary>
public sealed class ApiOptions
{
    /// <summary>Ordner, in dem die Projekte liegen.</summary>
    public required string ProjectsRoot { get; init; }

    /// <summary>Prüft den Zugangsschlüssel (Bearer); null = abgelehnt.</summary>
    public required Func<string, ApiUser?> Authorize { get; init; }

    /// <summary>KI-Einstellungen für die Agenten.</summary>
    public required Func<Core.Ai.AiSettings> Ai { get; init; }

    /// <summary>Ein bereits geöffnetes Projekt (z. B. das in der Desktop-App), damit beide dieselbe Instanz nutzen.</summary>
    public Func<string, TranslationProject?>? OpenProject { get; init; }

    /// <summary>Vor jedem Zugriff auf ein Projekt (Desktop-App: offenes Dokument im Editor zuerst speichern).</summary>
    public Action<TranslationProject>? BeforeProjectAccess { get; init; }

    /// <summary>Die API hat ein Dokument geändert (Projekt, Datei-ID, Zielsprache) – Editor bzw. Teamserver laden neu.</summary>
    public Action<TranslationProject, string, string>? DocumentChanged { get; init; }

    /// <summary>Erweiterungen, deren Befehle über die API aufrufbar sind.</summary>
    public Core.Extensions.ExtensionManager? Extensions { get; init; }
}

public sealed record ApiInfo(string Name, string Version, string Documentation, List<string> Areas);

public sealed record ProjectCreateDto(string Name, string SourceLanguage, List<string> TargetLanguages, string? Topic);

public sealed record ProjectUpdateDto(string? Topic, int? PretranslateMinScore, bool? ConfirmContextMatches, string? SourceProfile);

public sealed record FileDto(string Id, string Name, string Format, List<string> Languages, string? PackagePath);

public sealed record ProjectDto(string Name, string SourceLanguage, List<string> TargetLanguages, string Topic, string? SourceProfile,
    int PretranslateMinScore, bool ConfirmContextMatches, bool IsShared, List<FileDto> Files, List<ResourceRef> Resources, List<string> ResourceErrors);

public sealed record FileUploadDto(string Name, string Base64);

public sealed record SegmentDto(int Id, string Source, string Target, SegmentStatus Status, TranslationOrigin Origin, int? MatchPercent,
    string? Comment, string? Context, bool Locked, List<SegmentRevision> History);

public sealed record SegmentUpdateDto(string? Target, SegmentStatus? Status, string? Comment);

public sealed record ConfirmDto(SegmentStatus? Status);

public sealed record SplitDto(int SourceIndex);

public sealed record PretranslateDto(bool ConfirmExact = false, bool Overwrite = false);

public sealed record PretranslateResult(string File, string Language, int Applied, int Confirmed);

public sealed record AnalysisRowDto(string Language, string Band, int Segments, int Words, int Characters);

public sealed record QaIssueDto(string File, string Language, int SegmentId, string Severity, string Check, string Message);

public sealed record TmAddDto(string SourceLanguage, string TargetLanguage, string Source, string Target, string? Previous, string? Next, string? Structure);

public sealed record ConceptDto(List<TermDto> Terms, string? Definition, string? Domain, string? Source);

public sealed record AlignRequest(FileUploadDto Source, FileUploadDto Target, string SourceLanguage, string TargetLanguage, string? ImportIntoProject);

public sealed record TranslateRequest(string? Project, string? File, string? Language, int? SegmentId,
    string? Text, string? SourceLanguage, string? TargetLanguage, string? Topic, string? ProviderId);

public sealed record EvaluateRequest(string Source, string SourceLanguage, string TargetLanguage, List<TranslationCandidate> Candidates,
    string? Topic, bool CheckUsage, string? Project);

public sealed record UsageRequest(string Expression, string Language, string? Topic, List<string>? Alternatives);

public sealed record ResearchRequest(string Topic, string SourceLanguage, List<string> TargetLanguages, List<string>? SeedTerms,
    int PagesPerLanguage = 3, string? SourceProfile = null, string? SaveToProject = null);

public sealed record KnowledgeAgentRequest(string Project, string Topic, List<string> Languages, string? Focus, int PagesPerLanguage = 3,
    string? KnowledgeBase = null, string? SourceProfile = null);

public sealed record CompareRequest(string Term, string SourceLanguage, string TargetLanguage, string? Context, string? Topic,
    string? SourceProfile, List<string>? Candidates, string? Project, bool SaveToTermbase = false, bool SaveToKnowledge = false);

public sealed record ExtensionRunDto(string? Project, string? File, string? Language, int? SegmentId, Dictionary<string, string>? Arguments);

public static class Dto
{
    public static FileDto File(TranslationProject p, ProjectFile f) => new(f.Id, f.OriginalName, f.FilterId, p.LanguagesOf(f).ToList(), f.PackagePath);

    public static ProjectDto Project(TranslationProject p) => new(p.Name, p.SourceLanguage, p.TargetLanguages, p.Topic, p.SourceProfile,
        p.PretranslateMinScore, p.ConfirmContextMatches, p.IsShared, p.Files.Select(f => File(p, f)).ToList(), p.Resources, p.ResourceErrors);

    public static SegmentDto Segment(BilingualDocument d, Segment s) => new(s.Id, s.Source, s.Target, s.Status, s.Origin, s.MatchPercent,
        s.Comment, d.ParagraphOf(s)?.Context, s.Locked, s.History);
}
