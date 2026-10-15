using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Projects;

public sealed class ProjectFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string OriginalName { get; set; } = "";
    public string FilterId { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.UtcNow;

    /// <summary>Gesetzt, wenn die Datei nur für eine Zielsprache existiert (z. B. zweisprachige Datei aus einem Projektpaket).</summary>
    public string? OnlyLanguage { get; set; }

    /// <summary>Pfad der Datei im Projektpaket (für das Rückgabepaket).</summary>
    public string? PackagePath { get; set; }
}

/// <summary>Verbindung einer Arbeitskopie mit einem gemeinsamen Projekt auf dem Teamserver.</summary>
public sealed class SharedLink
{
    public string Server { get; set; } = "";
    public string Project { get; set; } = "";
}

/// <summary>Ergebnis eines Abgleichs mit dem Teamserver.</summary>
public sealed record SyncResult(List<int> ChangedSegments, List<SegmentLock> Locks, List<string> Conflicts, bool Reloaded);

/// <summary>
/// Ein Übersetzungsprojekt: ein Ordner mit project.json, den Originaldateien,
/// den zweisprachigen Arbeitsdateien (pro Zielsprache) und den Projekt-TM/-Terminologien.
/// </summary>
public sealed class TranslationProject : IDisposable
{
    public const string FileName = "project.json";

    public string Name { get; set; } = "";
    public string SourceLanguage { get; set; } = "de-DE";
    public List<string> TargetLanguages { get; set; } = new();

    /// <summary>Fachgebiet / Thema – geht in jede KI-Anfrage und in die Recherche der Agenten ein.</summary>
    public string Topic { get; set; } = "";
    public string Client { get; set; } = "";
    public DateTime? Due { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<ProjectFile> Files { get; set; } = new();

    /// <summary>Zusätzlich angebundene TMs, Terminologien und Wissensdatenbanken (lokal oder Teamserver, je Sprache).</summary>
    public List<ResourceRef> Resources { get; set; } = new();

    // Ältere Projektdateien: werden beim Öffnen in Resources übernommen.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? ExternalTms { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? ExternalTermbases { get; set; }

    /// <summary>Ab welcher Trefferquote beim Vorübersetzen eingesetzt wird.</summary>
    public int PretranslateMinScore { get; set; } = 100;
    public int LookupMinScore { get; set; } = 70;

    /// <summary>Fachquellen-Profil (Einstellungen → Fachquellen), das die Agenten für dieses Projekt nutzen.</summary>
    public string? SourceProfile { get; set; }

    /// <summary>Kontexttreffer (101 %) beim Vorübersetzen gleich bestätigen.</summary>
    public bool ConfirmContextMatches { get; set; } = true;
    public QaSettings Qa { get; set; } = new();

    /// <summary>Gesetzt, wenn dies die Arbeitskopie eines gemeinsamen Projekts auf dem Teamserver ist.</summary>
    public SharedLink? Shared { get; set; }

    private SharedProjectClient? _sharedClient;
    private readonly Dictionary<string, SharedState> _sharedState = new();

    private sealed class SharedState
    {
        public long Version;
        public Dictionary<int, long> SegmentVersions = new();
        public Dictionary<int, (string Target, SegmentStatus Status, string? Comment)> Known = new();
    }

    [JsonIgnore] public bool IsShared => Shared != null;

    [JsonIgnore] public string Folder { get; private set; } = "";
    [JsonIgnore] public List<ITranslationMemory> Tms { get; } = new();
    [JsonIgnore] public List<ITermbase> Termbases { get; } = new();
    [JsonIgnore] public List<IKnowledgeBase> KnowledgeBases { get; } = new();

    /// <summary>Ressourcen, die beim Öffnen nicht erreichbar waren (z. B. Server offline).</summary>
    [JsonIgnore] public List<string> ResourceErrors { get; } = new();

    private readonly Dictionary<ILanguageResource, ResourceRef> _refs = new();

    /// <summary>Das Projekt-TM, in das bestätigte Segmente geschrieben werden.</summary>
    [JsonIgnore] public TranslationMemory ProjectTm => (TranslationMemory)Tms[0];
    [JsonIgnore] public Termbase ProjectTermbase => (Termbase)Termbases[0];
    [JsonIgnore] public KnowledgeBase ProjectKnowledge => (KnowledgeBase)KnowledgeBases[0];

    /// <summary>Serverzugänge zum Öffnen von Teamressourcen (wird von der Oberfläche gesetzt).</summary>
    public static Func<ServerSettings> Servers { get; set; } = ServerSettings.Load;

    private bool Applies(ILanguageResource r, string language) => !_refs.TryGetValue(r, out var rf) || rf.AppliesTo(language);
    private bool Writes(ILanguageResource r, string language) => _refs.TryGetValue(r, out var rf) && rf.WriteBack && rf.AppliesTo(language);

    public IReadOnlyList<ITranslationMemory> TmsFor(string targetLanguage) => Tms.Where(t => Applies(t, targetLanguage)).ToList();
    public IReadOnlyList<ITermbase> TermbasesFor(string targetLanguage) => Termbases.Where(t => Applies(t, targetLanguage)).ToList();
    public IReadOnlyList<IKnowledgeBase> KnowledgeFor(string language) => KnowledgeBases.Where(k => Applies(k, language)).ToList();

    /// <summary>Gemeinsame TMs, in die bestätigte Segmente zusätzlich geschrieben werden.</summary>
    public IReadOnlyList<ITranslationMemory> SharedTmsToWrite(string targetLanguage) => Tms.Skip(1).Where(t => Writes(t, targetLanguage)).ToList();
    public IReadOnlyList<ITermbase> SharedTermbasesToWrite(string targetLanguage) => Termbases.Skip(1).Where(t => Writes(t, targetLanguage)).ToList();

    public ResourceRef? RefOf(ILanguageResource resource) => _refs.GetValueOrDefault(resource);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TranslationProject Create(string folder, string name, string sourceLanguage, IEnumerable<string> targetLanguages, string topic = "")
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException($"Der Ordner {folder} ist nicht leer.");
        Directory.CreateDirectory(folder);
        var p = new TranslationProject
        {
            Name = name,
            SourceLanguage = sourceLanguage,
            TargetLanguages = targetLanguages.ToList(),
            Topic = topic,
            Folder = folder,
        };
        p.OpenResources();
        p.Save();
        return p;
    }

    public static TranslationProject Open(string folderOrFile)
    {
        var folder = File.Exists(folderOrFile) ? Path.GetDirectoryName(folderOrFile)! : folderOrFile;
        var json = File.ReadAllText(Path.Combine(folder, FileName));
        var p = JsonSerializer.Deserialize<TranslationProject>(json, Json) ?? throw new InvalidDataException("project.json ist leer.");
        p.Folder = folder;
        p.OpenResources();
        return p;
    }

    private void OpenResources()
    {
        Tms.Add(new TranslationMemory(Path.Combine(Folder, "tm", "projekt.sctm"), "Projekt-TM"));
        Termbases.Add(new Termbase(Path.Combine(Folder, "terminologie", "projekt.sctb"), "Projekt-Terminologie"));
        KnowledgeBases.Add(new KnowledgeBase(Path.Combine(Folder, "wissen", "projekt.sckb"), "Projekt-Wissen"));

        // alte Projektdateien übernehmen
        foreach (var path in ExternalTms ?? []) Resources.Add(new ResourceRef { Kind = ResourceKind.Tm, Location = path });
        foreach (var path in ExternalTermbases ?? []) Resources.Add(new ResourceRef { Kind = ResourceKind.Termbase, Location = path });
        ExternalTms = null;
        ExternalTermbases = null;

        var servers = Servers();
        foreach (var r in Resources) OpenRef(r, servers);
        if (Shared != null)
        {
            try
            {
                var account = servers.Get(Shared.Server) ?? throw new RemoteException($"Serverzugang „{Shared.Server}“ ist nicht eingerichtet.");
                _sharedClient = new SharedProjectClient(ResourceFactory.Api(account));
                SyncFilesFromServer();
            }
            catch (RemoteException ex) { ResourceErrors.Add($"Gemeinsames Projekt: {ex.Message}"); }
        }
    }

    // ---------- Gemeinsame Projekte ----------

    /// <summary>Legt eine Arbeitskopie eines gemeinsamen Projekts an (Ordner auf diesem Rechner, Dateien auf dem Server).</summary>
    public static TranslationProject OpenShared(string folder, string serverName, string projectName)
    {
        var account = Servers().Get(serverName) ?? throw new RemoteException($"Serverzugang „{serverName}“ ist nicht eingerichtet.");
        var info = new SharedProjectClient(ResourceFactory.Api(account)).Get(projectName);
        if (Directory.Exists(folder) && File.Exists(Path.Combine(folder, FileName))) return Open(folder);
        Directory.CreateDirectory(folder);
        var p = new TranslationProject
        {
            Name = info.Name, SourceLanguage = info.SourceLanguage, TargetLanguages = info.TargetLanguages, Topic = info.Topic,
            Folder = folder, Shared = new SharedLink { Server = serverName, Project = projectName },
        };
        p.OpenResources();
        p.Save();
        return p;
    }

    private SharedProjectClient SharedClient =>
        _sharedClient ?? throw new RemoteException("Der Teamserver ist nicht erreichbar.");

    /// <summary>Dateiliste vom Server übernehmen.</summary>
    public void SyncFilesFromServer()
    {
        var info = SharedClient.Get(Shared!.Project);
        TargetLanguages = info.TargetLanguages;
        Topic = info.Topic.Length > 0 ? info.Topic : Topic;
        Files = info.Files.Select(f => new ProjectFile
        {
            Id = f.Id, OriginalName = f.Name, FilterId = f.FilterId,
            OnlyLanguage = f.Languages.Count == 1 && TargetLanguages.Count > 1 ? f.Languages[0] : null,
        }).ToList();
        Save();
    }

    private string Key(ProjectFile f, string lang) => $"{f.Id}|{lang}";

    private BilingualDocument LoadShared(ProjectFile file, string lang)
    {
        try
        {
            var sd = SharedClient.Document(Shared!.Project, file.Id, lang);
            _sharedState[Key(file, lang)] = new SharedState
            {
                Version = sd.Version,
                SegmentVersions = sd.SegmentVersions,
                Known = sd.Document.Segments.ToDictionary(x => x.Id, x => (x.Target, x.Status, x.Comment)),
            };
            WriteCache(file, sd.Document);
            return sd.Document;
        }
        catch (RemoteException) when (File.Exists(DocumentPath(file, lang)))
        {
            // Server nicht erreichbar: zuletzt gesehene Fassung anzeigen
            return JsonSerializer.Deserialize<BilingualDocument>(File.ReadAllText(DocumentPath(file, lang)), Json)!;
        }
    }

    /// <summary>Lokale Änderungen an den Server schicken. Konflikte: die Serverfassung gilt, die eigene kommt in den Verlauf.</summary>
    private List<string> PushShared(ProjectFile file, BilingualDocument doc)
    {
        var conflicts = new List<string>();
        if (!_sharedState.TryGetValue(Key(file, doc.TargetLanguage), out var st)) return conflicts;
        foreach (var seg in doc.Segments)
        {
            st.Known.TryGetValue(seg.Id, out var known);
            if (known == (seg.Target, seg.Status, seg.Comment)) continue;
            var result = SharedClient.Update(Shared!.Project, file.Id, doc.TargetLanguage, seg.Id,
                new SegmentUpdate(seg.Target, seg.Status, seg.Origin, seg.MatchPercent, seg.Comment, st.SegmentVersions.GetValueOrDefault(seg.Id)));
            if (result.Accepted)
            {
                st.Known[seg.Id] = (seg.Target, seg.Status, seg.Comment);
                st.SegmentVersions[seg.Id] = result.Version;
            }
            else if (result.Current is { } cur)
            {
                ApplyRemote(seg, cur, st, conflicts);
            }
        }
        return conflicts;
    }

    private static void ApplyRemote(Segment seg, SegmentChange c, SharedState st, List<string> conflicts)
    {
        st.Known.TryGetValue(seg.Id, out var known);
        var localEdited = known != (seg.Target, seg.Status, seg.Comment);
        if (localEdited && seg.Target != c.Target && seg.Target.Length > 0)
        {
            // eigene Fassung nicht verlieren
            seg.History.Add(new SegmentRevision(DateTime.UtcNow, seg.Target, seg.Status, seg.Origin));
            conflicts.Add($"Segment {seg.Id}: zwischenzeitlich von {c.User} geändert – dessen Fassung übernommen, deine steht im Verlauf.");
        }
        seg.Target = c.Target;
        seg.Status = c.Status;
        seg.Origin = c.Origin;
        seg.MatchPercent = c.MatchPercent;
        seg.Comment = c.Comment;
        st.Known[seg.Id] = (c.Target, c.Status, c.Comment);
        st.SegmentVersions[seg.Id] = c.Version;
    }

    /// <summary>
    /// Abgleich mit dem Server: eigene Änderungen senden, Änderungen der anderen übernehmen, Sperren abholen.
    /// </summary>
    public SyncResult Sync(ProjectFile file, BilingualDocument doc)
    {
        if (!IsShared) return new SyncResult([], [], [], false);
        var conflicts = PushShared(file, doc);
        var key = Key(file, doc.TargetLanguage);
        if (!_sharedState.TryGetValue(key, out var st)) return new SyncResult([], [], conflicts, false);
        var changes = SharedClient.Changes(Shared!.Project, file.Id, doc.TargetLanguage, st.Version);
        if (changes.Reset) return new SyncResult([], changes.Locks, conflicts, Reloaded: true);
        var changed = new List<int>();
        foreach (var c in changes.Changes)
        {
            var seg = doc.Segments.FirstOrDefault(x => x.Id == c.SegmentId);
            if (seg == null) continue;
            if ((seg.Target, seg.Status, seg.Comment) == (c.Target, c.Status, c.Comment))
            {
                st.Known[seg.Id] = (c.Target, c.Status, c.Comment);
                st.SegmentVersions[seg.Id] = Math.Max(st.SegmentVersions.GetValueOrDefault(seg.Id), c.Version);
                continue;
            }
            ApplyRemote(seg, c, st, conflicts);
            changed.Add(seg.Id);
        }
        st.Version = changes.Version;
        WriteCache(file, doc);
        return new SyncResult(changed, changes.Locks, conflicts, false);
    }

    public bool LockSegment(ProjectFile file, string lang, int segmentId)
    {
        if (!IsShared) return true;
        try { return SharedClient.Send(() => SharedClient.LockAndReport(Shared!.Project, file.Id, lang, segmentId)); }
        catch (RemoteException) { return true; }
    }

    public void UnlockSegment(ProjectFile file, string lang, int segmentId)
    {
        if (!IsShared) return;
        try { SharedClient.Unlock(Shared!.Project, file.Id, lang, segmentId); }
        catch (RemoteException) { }
    }

    private void WriteCache(ProjectFile file, BilingualDocument doc)
    {
        Directory.CreateDirectory(Path.Combine(Folder, "arbeit"));
        File.WriteAllText(DocumentPath(file, doc.TargetLanguage), JsonSerializer.Serialize(doc, Json));
    }

    private void OpenRef(ResourceRef r, ServerSettings servers)
    {
        try
        {
            var res = ResourceFactory.Open(r, servers);
            _refs[res] = r;
            switch (res)
            {
                case ITranslationMemory tm: Tms.Add(tm); break;
                case ITermbase tb: Termbases.Add(tb); break;
                case IKnowledgeBase kb: KnowledgeBases.Add(kb); break;
            }
        }
        catch (Exception ex) when (ex is RemoteException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            ResourceErrors.Add($"{r.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>Bindet eine Ressource an (lokal oder Teamserver).</summary>
    public void Attach(ResourceRef r)
    {
        if (Resources.Any(x => x.Kind == r.Kind && x.Server == r.Server && x.Location == r.Location)) return;
        Resources.Add(r);
        OpenRef(r, Servers());
        Save();
    }

    public void Detach(ResourceRef r)
    {
        Resources.Remove(r);
        foreach (var (res, rf) in _refs.Where(kv => kv.Value == r).ToList())
        {
            _refs.Remove(res);
            Tms.Remove(res as ITranslationMemory ?? null!);
            Termbases.Remove(res as ITermbase ?? null!);
            KnowledgeBases.Remove(res as IKnowledgeBase ?? null!);
            res.Dispose();
        }
        Save();
    }

    public void AttachTm(string path) => Attach(new ResourceRef { Kind = ResourceKind.Tm, Location = path });
    public void AttachTermbase(string path) => Attach(new ResourceRef { Kind = ResourceKind.Termbase, Location = path });

    public void Save() =>
        File.WriteAllText(Path.Combine(Folder, FileName), JsonSerializer.Serialize(this, Json));

    private string OriginalPath(ProjectFile f) => Path.Combine(Folder, "quelle", f.Id + Path.GetExtension(f.OriginalName));
    private string DocumentPath(ProjectFile f, string targetLanguage) => Path.Combine(Folder, "arbeit", $"{f.Id}.{targetLanguage}.json");

    /// <summary>Nimmt eine Datei ins Projekt auf und legt für jede Zielsprache eine Arbeitsdatei an.</summary>
    public ProjectFile AddFile(Stream content, string fileName, string? onlyLanguage = null, string? packagePath = null)
    {
        if (IsShared)
        {
            var ms = new MemoryStream();
            content.CopyTo(ms);
            var info = SharedClient.Upload(Shared!.Project, fileName, ms.ToArray());
            SyncFilesFromServer();
            return Files.First(f => f.Id == info.Id);
        }
        var filter = FileFilters.ForFile(fileName)
                     ?? throw new NotSupportedException($"Dateityp {Path.GetExtension(fileName)} wird nicht unterstützt.");
        var file = new ProjectFile { OriginalName = fileName, FilterId = filter.Id, OnlyLanguage = onlyLanguage, PackagePath = packagePath };
        Directory.CreateDirectory(Path.Combine(Folder, "quelle"));
        using (var fs = File.Create(OriginalPath(file))) content.CopyTo(fs);

        var segmenter = new Segmenter(SourceLanguage);
        foreach (var lang in LanguagesOf(file))
        {
            using var src = File.OpenRead(OriginalPath(file));
            var doc = filter.Read(src, fileName, SourceLanguage, lang, segmenter);
            SaveDocument(file, doc);
        }
        Files.Add(file);
        Save();
        return file;
    }

    /// <summary>Für welche Zielsprachen es diese Datei gibt.</summary>
    public IEnumerable<string> LanguagesOf(ProjectFile file) =>
        file.OnlyLanguage != null ? [file.OnlyLanguage] : TargetLanguages;

    /// <summary>Alle Kombinationen aus Datei und Zielsprache.</summary>
    public IEnumerable<(ProjectFile File, string Language)> Documents() =>
        Files.SelectMany(f => LanguagesOf(f).Select(l => (f, l)));

    public bool IsFromPackage => Files.Any(f => f.PackagePath != null);

    public void RemoveFile(ProjectFile file)
    {
        if (IsShared)
        {
            SharedClient.DeleteFile(Shared!.Project, file.Id);
            SyncFilesFromServer();
            return;
        }
        Files.Remove(file);
        File.Delete(OriginalPath(file));
        foreach (var lang in TargetLanguages)
            if (File.Exists(DocumentPath(file, lang))) File.Delete(DocumentPath(file, lang));
        Save();
    }

    public BilingualDocument LoadDocument(ProjectFile file, string targetLanguage)
    {
        if (IsShared) return LoadShared(file, targetLanguage);
        var json = File.ReadAllText(DocumentPath(file, targetLanguage));
        return JsonSerializer.Deserialize<BilingualDocument>(json, Json)!;
    }

    /// <summary>Konflikte beim letzten Speichern eines gemeinsamen Dokuments.</summary>
    [JsonIgnore] public List<string> LastConflicts { get; private set; } = new();

    public void SaveDocument(ProjectFile file, BilingualDocument doc)
    {
        if (IsShared)
        {
            LastConflicts = PushShared(file, doc);
            WriteCache(file, doc);
            return;
        }
        Directory.CreateDirectory(Path.Combine(Folder, "arbeit"));
        var path = DocumentPath(file, doc.TargetLanguage);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Schreibt die übersetzte Zieldatei im Originalformat.</summary>
    public void ExportTarget(ProjectFile file, BilingualDocument doc, Stream output)
    {
        if (IsShared)
        {
            PushShared(file, doc);
            output.Write(SharedClient.Target(Shared!.Project, file.Id, doc.TargetLanguage));
            return;
        }
        using var original = File.OpenRead(OriginalPath(file));
        FileFilters.ById(file.FilterId).Write(doc, original, output);
    }

    public string TargetFileName(ProjectFile file, string targetLanguage) =>
        FileFilters.ById(file.FilterId).TargetFileName(file.OriginalName, targetLanguage);

    public IEnumerable<BilingualDocument> AllDocuments(string targetLanguage) =>
        Files.Where(f => LanguagesOf(f).Contains(targetLanguage)).Select(f => LoadDocument(f, targetLanguage));

    /// <summary>Fortschritt: Wörter gesamt und bestätigt.</summary>
    public (int Total, int Confirmed) Progress(ProjectFile file, string targetLanguage)
    {
        var doc = LoadDocument(file, targetLanguage);
        var total = 0;
        var done = 0;
        foreach (var s in doc.Segments)
        {
            var w = Analysis.Analyzer.CountWords(s.Source);
            total += w;
            if (s.IsConfirmed || s.Locked) done += w;
        }
        return (total, done);
    }

    public void Dispose()
    {
        foreach (var tm in Tms) tm.Dispose();
        foreach (var tb in Termbases) tb.Dispose();
        foreach (var kb in KnowledgeBases) kb.Dispose();
    }
}
