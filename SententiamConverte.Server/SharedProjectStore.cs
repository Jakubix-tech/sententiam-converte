using System.Collections.Concurrent;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;

namespace SententiamConverte.Server;

/// <summary>
/// Gemeinsame Projekte: mehrere Personen bearbeiten gleichzeitig dieselbe Datei. Jede Segmentänderung erhöht
/// die Version des Dokuments; Clients holen Änderungen seit ihrer Version ab. Ein Segment, das sich seit der
/// Fassung des Clients geändert hat, wird nicht überschrieben (Konflikt). Sperren zeigen, wer woran arbeitet.
/// </summary>
public sealed class SharedProjectStore : IDisposable
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, TranslationProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DocState> _docs = new();
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(2);

    private sealed class DocState
    {
        public required BilingualDocument Doc;
        public required ProjectFile File;
        public required TranslationProject Project;
        public long Version;
        public readonly Dictionary<int, long> SegmentVersions = new();
        public readonly List<SegmentChange> Changes = new();
        public readonly Dictionary<int, SegmentLock> Locks = new();
        /// <summary>Wer mit einer älteren Version kommt, muss das Dokument neu laden (Segmente geteilt/zusammengeführt).</summary>
        public long ResetVersion;
    }

    public SharedProjectStore(string dataDir)
    {
        _root = Path.Combine(dataDir, "projekte");
        Directory.CreateDirectory(_root);
    }

    private string Folder(string name) =>
        ResourceStore.ValidName(name) ? Path.Combine(_root, name) : throw new ArgumentException("Ungültiger Projektname.");

    private TranslationProject Project(string name) =>
        _projects.GetOrAdd(name, n =>
        {
            var folder = Folder(n);
            if (!File.Exists(Path.Combine(folder, TranslationProject.FileName))) throw new KeyNotFoundException(n);
            return TranslationProject.Open(folder);
        });

    /// <summary>Dieselbe Projektinstanz für die REST-API (v2), damit beide Wege dieselbe Sperre nutzen.</summary>
    public TranslationProject? TryProject(string name)
    {
        if (!ResourceStore.ValidName(name) || !File.Exists(Path.Combine(_root, name, TranslationProject.FileName))) return null;
        return Project(name);
    }

    /// <summary>
    /// Die REST-API hat das Dokument auf der Platte geändert: neu einlesen und jede Änderung als Version melden,
    /// damit die verbundenen Programme sie beim nächsten Abgleich übernehmen.
    /// </summary>
    public void Reload(string project, string fileId, string lang, string user)
    {
        if (!_docs.TryGetValue($"{project}|{fileId}|{lang}", out var s)) return;
        lock (s.Project)
        {
            var fresh = s.Project.LoadDocument(s.File, lang);
            var old = s.Doc.Segments.ToDictionary(x => x.Id);
            var structureChanged = fresh.Segments.Count() != old.Count || fresh.Segments.Any(x => !old.ContainsKey(x.Id));
            if (structureChanged)
            {
                s.Doc = fresh;
                s.Version++;
                s.ResetVersion = s.Version;
                s.SegmentVersions.Clear();
                s.Changes.Clear();
                return;
            }
            foreach (var seg in fresh.Segments)
            {
                var o = old[seg.Id];
                if ((o.Target, o.Status, o.Origin, o.MatchPercent, o.Comment) == (seg.Target, seg.Status, seg.Origin, seg.MatchPercent, seg.Comment)) continue;
                s.Version++;
                s.SegmentVersions[seg.Id] = s.Version;
                s.Changes.Add(new SegmentChange(s.Version, seg.Id, seg.Target, seg.Status, seg.Origin, seg.MatchPercent, seg.Comment, user, DateTime.UtcNow));
            }
            s.Doc = fresh;
        }
    }

    public List<SharedProjectInfo> List() =>
        Directory.GetDirectories(_root)
            .Where(d => File.Exists(Path.Combine(d, TranslationProject.FileName)))
            .Select(d => Info(Path.GetFileName(d))).ToList();

    public SharedProjectInfo Info(string name)
    {
        var p = Project(name);
        return new SharedProjectInfo(name, p.SourceLanguage, p.TargetLanguages, p.Topic,
            p.Files.Select(f => new SharedFileInfo(f.Id, f.OriginalName, f.FilterId, p.LanguagesOf(f).ToList())).ToList());
    }

    public void Create(SharedProjectCreate c)
    {
        var folder = Folder(c.Name);
        if (Directory.Exists(folder)) throw new ArgumentException("Ein Projekt mit diesem Namen gibt es schon.");
        _projects[c.Name] = TranslationProject.Create(folder, c.Name, c.SourceLanguage, c.TargetLanguages, c.Topic ?? "");
    }

    public SharedFileInfo AddFile(string project, string fileName, byte[] content)
    {
        var p = Project(project);
        lock (p)
        {
            var f = p.AddFile(new MemoryStream(content), Path.GetFileName(fileName));
            return new SharedFileInfo(f.Id, f.OriginalName, f.FilterId, p.LanguagesOf(f).ToList());
        }
    }

    public void DeleteFile(string project, string fileId)
    {
        var p = Project(project);
        lock (p)
        {
            var f = p.Files.FirstOrDefault(x => x.Id == fileId) ?? throw new KeyNotFoundException(fileId);
            foreach (var key in _docs.Keys.Where(k => k.StartsWith($"{project}|{fileId}|")).ToList()) _docs.TryRemove(key, out _);
            p.RemoveFile(f);
        }
    }

    private DocState State(string project, string fileId, string lang) =>
        _docs.GetOrAdd($"{project}|{fileId}|{lang}", _ =>
        {
            var p = Project(project);
            var f = p.Files.FirstOrDefault(x => x.Id == fileId) ?? throw new KeyNotFoundException(fileId);
            if (!p.LanguagesOf(f).Contains(lang, StringComparer.OrdinalIgnoreCase)) throw new KeyNotFoundException(lang);
            return new DocState { Doc = p.LoadDocument(f, lang), File = f, Project = p };
        });

    private static void DropExpiredLocks(DocState s)
    {
        foreach (var id in s.Locks.Where(kv => kv.Value.Until < DateTime.UtcNow).Select(kv => kv.Key).ToList()) s.Locks.Remove(id);
    }

    public SharedDocument Document(string project, string fileId, string lang)
    {
        var s = State(project, fileId, lang);
        lock (s.Project)
        {
            DropExpiredLocks(s);
            // Kopie über JSON, damit spätere Änderungen das ausgelieferte Objekt nicht verändern
            var copy = System.Text.Json.JsonSerializer.Deserialize<BilingualDocument>(System.Text.Json.JsonSerializer.Serialize(s.Doc))!;
            return new SharedDocument(copy, s.Version, new Dictionary<int, long>(s.SegmentVersions), s.Locks.Values.ToList());
        }
    }

    public SharedChanges Changes(string project, string fileId, string lang, long since)
    {
        var s = State(project, fileId, lang);
        lock (s.Project)
        {
            DropExpiredLocks(s);
            if (since > s.Version || since < s.ResetVersion) return new SharedChanges(s.Version, [], s.Locks.Values.ToList(), Reset: true);
            var changes = s.Changes.Where(c => c.Version > since)
                .GroupBy(c => c.SegmentId).Select(g => g.Last()).ToList();
            return new SharedChanges(s.Version, changes, s.Locks.Values.ToList(), Reset: false);
        }
    }

    public SegmentUpdateResult Update(string project, string fileId, string lang, int segmentId, SegmentUpdate u, string user)
    {
        var s = State(project, fileId, lang);
        lock (s.Project)
        {
            var seg = s.Doc.Segments.FirstOrDefault(x => x.Id == segmentId) ?? throw new KeyNotFoundException(segmentId.ToString());
            var current = s.SegmentVersions.GetValueOrDefault(segmentId);
            if (current > u.BaseVersion)
            {
                // Inzwischen hat jemand anderes geändert – nicht überschreiben
                var latest = s.Changes.LastOrDefault(c => c.SegmentId == segmentId);
                return new SegmentUpdateResult(false, s.Version, latest);
            }
            if (s.Locks.TryGetValue(segmentId, out var l) && l.User != user && l.Until > DateTime.UtcNow)
                return new SegmentUpdateResult(false, s.Version, s.Changes.LastOrDefault(c => c.SegmentId == segmentId)
                    ?? new SegmentChange(current, segmentId, seg.Target, seg.Status, seg.Origin, seg.MatchPercent, seg.Comment, l.User, DateTime.UtcNow));

            seg.Target = u.Target;
            seg.Status = u.Status;
            seg.Origin = u.Origin;
            seg.MatchPercent = u.MatchPercent;
            seg.Comment = u.Comment;
            seg.Modified = DateTime.UtcNow;
            s.Version++;
            s.SegmentVersions[segmentId] = s.Version;
            var change = new SegmentChange(s.Version, segmentId, u.Target, u.Status, u.Origin, u.MatchPercent, u.Comment, user, DateTime.UtcNow);
            s.Changes.Add(change);
            if (s.Changes.Count > 20000) s.Changes.RemoveRange(0, 10000);
            s.Project.SaveDocument(s.File, s.Doc);
            return new SegmentUpdateResult(true, s.Version, change);
        }
    }

    public bool Lock(string project, string fileId, string lang, int segmentId, string user)
    {
        var s = State(project, fileId, lang);
        lock (s.Project)
        {
            DropExpiredLocks(s);
            if (s.Locks.TryGetValue(segmentId, out var l) && l.User != user) return false;
            // Eine Person hält höchstens eine Sperre je Dokument
            foreach (var mine in s.Locks.Where(kv => kv.Value.User == user).Select(kv => kv.Key).ToList()) s.Locks.Remove(mine);
            s.Locks[segmentId] = new SegmentLock(segmentId, user, DateTime.UtcNow + LockTime);
            return true;
        }
    }

    public void Unlock(string project, string fileId, string lang, int segmentId, string user)
    {
        var s = State(project, fileId, lang);
        lock (s.Project)
            if (s.Locks.TryGetValue(segmentId, out var l) && l.User == user) s.Locks.Remove(segmentId);
    }

    public byte[] Target(string project, string fileId, string lang)
    {
        var s = State(project, fileId, lang);
        var ms = new MemoryStream();
        lock (s.Project) s.Project.ExportTarget(s.File, s.Doc, ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var p in _projects.Values) p.Dispose();
    }
}
