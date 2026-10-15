using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Remote;

/// <summary>Ein gemeinsames Projekt auf dem Teamserver.</summary>
public sealed record SharedProjectInfo(string Name, string SourceLanguage, List<string> TargetLanguages, string Topic, List<SharedFileInfo> Files);

public sealed record SharedFileInfo(string Id, string Name, string FilterId, List<string> Languages);

public sealed record SharedProjectCreate(string Name, string SourceLanguage, List<string> TargetLanguages, string? Topic);

/// <summary>Ein Dokument mit seiner Versionsnummer (steigt bei jeder Änderung).</summary>
public sealed record SharedDocument(BilingualDocument Document, long Version, Dictionary<int, long> SegmentVersions, List<SegmentLock> Locks);

/// <summary>Änderung eines Segments durch einen Benutzer.</summary>
public sealed record SegmentUpdate(string Target, SegmentStatus Status, TranslationOrigin Origin, int? MatchPercent, string? Comment, long BaseVersion);

public sealed record SegmentChange(long Version, int SegmentId, string Target, SegmentStatus Status, TranslationOrigin Origin,
    int? MatchPercent, string? Comment, string User, DateTime When);

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

    public byte[] Target(string project, string fileId, string lang) =>
        Convert.FromBase64String(api.Send<FileUpload>(HttpMethod.Get, $"projects/{E(project)}/files/{E(fileId)}/{E(lang)}/target").Base64);
}

public sealed record FileUpload(string Base64);
