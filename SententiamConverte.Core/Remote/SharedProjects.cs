using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Remote;

/// <summary>Ein gemeinsames Projekt auf dem Teamserver.</summary>
public sealed record SharedProjectInfo(string Name, string SourceLanguage, List<string> TargetLanguages, string Topic, List<SharedFileInfo> Files);

/// <summary>Datei eines gemeinsamen Projekts; <see cref="Steps"/>: Arbeitsschritt je Zielsprache (ältere Server liefern keine).</summary>
public sealed record SharedFileInfo(string Id, string Name, string FilterId, List<string> Languages, List<FileStep>? Steps = null);

/// <summary>Arbeitsschritte einer Datei im Team: erst Übersetzung, dann Lektorat, dann Freigabe.</summary>
public enum WorkflowStep { Translation, Review, Approval, Done }

/// <summary>Stand einer Datei in einer Zielsprache: aktueller Schritt und wer welchen Schritt erledigt.</summary>
public sealed record FileStep(string Language, WorkflowStep Step, string? Translator, string? Reviewer, string? Approver)
{
    /// <summary>Wer gerade an der Reihe ist (null = jeder mit Schreibrecht).</summary>
    public string? Assignee => Step switch
    {
        WorkflowStep.Translation => Translator,
        WorkflowStep.Review => Reviewer,
        WorkflowStep.Approval => Approver,
        _ => null,
    };

    public static WorkflowStep Next(WorkflowStep step) => step == WorkflowStep.Done ? WorkflowStep.Done : step + 1;

    /// <summary>Darf <paramref name="user"/> die Datei gerade bearbeiten?</summary>
    public bool MayEdit(string user, bool admin) =>
        admin || (Step != WorkflowStep.Done || (Translator ?? Reviewer ?? Approver) == null) && (Assignee == null || Assignee == user);
}

/// <summary>
/// Schritt oder Zuweisungen ändern (Verwaltung); <see cref="Advance"/>: der Zuständige gibt an den nächsten Schritt ab.
/// Bei den Namen gilt: null = unverändert, "" = niemand.
/// </summary>
public sealed record WorkflowUpdate(WorkflowStep? Step = null, string? Translator = null, string? Reviewer = null, string? Approver = null, bool Advance = false);

/// <summary>Ein Benutzer des Teamservers (für Zuweisungen).</summary>
public sealed record TeamUser(string Name, string Role);

public sealed record SharedProjectCreate(string Name, string SourceLanguage, List<string> TargetLanguages, string? Topic);

/// <summary>Ein Dokument mit seiner Versionsnummer (steigt bei jeder Änderung).</summary>
public sealed record SharedDocument(BilingualDocument Document, long Version, Dictionary<int, long> SegmentVersions, List<SegmentLock> Locks);

/// <summary>
/// Änderung eines Segments durch einen Benutzer. Lektorat (seit 1.3): <see cref="ReviewBaseline"/> und
/// <see cref="Errors"/> – null = unverändert (ältere Programme senden sie nicht), "" bzw. leere Liste = keine.
/// </summary>
public sealed record SegmentUpdate(string Target, SegmentStatus Status, TranslationOrigin Origin, int? MatchPercent, string? Comment, long BaseVersion,
    string? ReviewBaseline = null, List<Qa.LqaError>? Errors = null);

/// <summary>Eine Änderung auf dem Server; Lektoratsfelder wie bei <see cref="SegmentUpdate"/> (null bei älteren Servern).</summary>
public sealed record SegmentChange(long Version, int SegmentId, string Target, SegmentStatus Status, TranslationOrigin Origin,
    int? MatchPercent, string? Comment, string User, DateTime When, string? ReviewBaseline = null, List<Qa.LqaError>? Errors = null);

/// <summary>Wer gerade an welchem Segment arbeitet (läuft nach kurzer Zeit ohne Erneuerung ab).</summary>
public sealed record SegmentLock(int SegmentId, string User, DateTime Until);

/// <summary>
/// Änderungen seit einer Version. <see cref="Reset"/>: der Server kennt die Version nicht mehr (z. B. Neustart) –
/// das Dokument muss neu geladen werden.
/// </summary>
public sealed record SharedChanges(long Version, List<SegmentChange> Changes, List<SegmentLock> Locks, bool Reset);

/// <summary>Antwort auf eine Segmentänderung: übernommen, oder Konflikt mit der aktuellen Fassung auf dem Server.</summary>
public sealed record SegmentUpdateResult(bool Accepted, long Version, SegmentChange? Current);

/// <summary>Zugriff auf gemeinsame Projekte eines Teamservers.</summary>
public sealed class SharedProjectClient(RemoteApi api)
{
    public string ServerName => api.Account.Name;
    private static string E(string s) => Uri.EscapeDataString(s);

    public List<SharedProjectInfo> List() => api.Send<List<SharedProjectInfo>>(HttpMethod.Get, "projects");
    public SharedProjectInfo Get(string project) => api.Send<SharedProjectInfo>(HttpMethod.Get, $"projects/{E(project)}");
    public void Create(SharedProjectCreate p) => api.Send(HttpMethod.Post, "projects", p);

    public SharedFileInfo Upload(string project, string fileName, byte[] content) =>
        api.Send<SharedFileInfo>(HttpMethod.Post, $"projects/{E(project)}/files?name={E(fileName)}", new FileUpload(Convert.ToBase64String(content)));

    public void DeleteFile(string project, string fileId) => api.Send(HttpMethod.Delete, $"projects/{E(project)}/files/{E(fileId)}");

    public SharedDocument Document(string project, string fileId, string lang) =>
        api.Send<SharedDocument>(HttpMethod.Get, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}");

    public SharedChanges Changes(string project, string fileId, string lang, long since) =>
        api.Send<SharedChanges>(HttpMethod.Get, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/changes?since={since}");

    public SegmentUpdateResult Update(string project, string fileId, string lang, int segmentId, SegmentUpdate update) =>
        api.Send<SegmentUpdateResult>(HttpMethod.Put, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/segments/{segmentId}", update);

    public void Lock(string project, string fileId, string lang, int segmentId) =>
        api.Send(HttpMethod.Post, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/segments/{segmentId}/lock");

    /// <summary>Sperrt ein Segment; false, wenn es gerade jemand anderes bearbeitet.</summary>
    public bool LockAndReport(string project, string fileId, string lang, int segmentId) =>
        api.Send<bool>(HttpMethod.Post, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/segments/{segmentId}/lock");

    internal T Send<T>(Func<T> f) => f();

    public void Unlock(string project, string fileId, string lang, int segmentId) =>
        api.Send(HttpMethod.Delete, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/segments/{segmentId}/lock");

    /// <summary>Wer bin ich auf diesem Server (Name und Rolle).</summary>
    public WhoAmI Me() => api.Send<WhoAmI>(HttpMethod.Get, "me");

    /// <summary>Benutzer des Servers (für Zuweisungen).</summary>
    public List<TeamUser> Users() => api.Send<List<TeamUser>>(HttpMethod.Get, "users");

    /// <summary>Arbeitsschritt ändern bzw. an den nächsten Schritt abgeben.</summary>
    public FileStep SetWorkflow(string project, string fileId, string lang, WorkflowUpdate update) =>
        api.Send<FileStep>(HttpMethod.Put, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/workflow", update);

    public byte[] Target(string project, string fileId, string lang) =>
        Convert.FromBase64String(api.Send<FileUpload>(HttpMethod.Get, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/target").Base64);
}

public sealed record FileUpload(string Base64);
