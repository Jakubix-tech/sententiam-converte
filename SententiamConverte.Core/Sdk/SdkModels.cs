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

public sealed record ProjectCreateDto(string Name, string SourceLanguage, List<string> TargetLanguages, string? Topic = null);

public sealed record ProjectUpdateDto(string? Topic = null, int? PretranslateMinScore = null, bool? ConfirmContextMatches = null, string? SourceProfile = null);

public sealed record FileDto(string Id, string Name, string Format, List<string> Languages, string? PackagePath);

public sealed record ProjectDto(string Name, string SourceLanguage, List<string> TargetLanguages, string Topic, string? SourceProfile,
    int PretranslateMinScore, bool ConfirmContextMatches, bool IsShared, List<FileDto> Files, List<ResourceRef> Resources, List<string> ResourceErrors);

public sealed record SegmentDto(int Id, string Source, string Target, SegmentStatus Status, TranslationOrigin Origin, int? MatchPercent,
    string? Comment, string? Context, bool Locked, List<SegmentRevision> History);

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
        s.Comment, d.ParagraphOf(s)?.Context, s.Locked, s.History);
}
