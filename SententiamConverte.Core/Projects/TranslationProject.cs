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
        Files.Remove(file);
        File.Delete(OriginalPath(file));
        foreach (var lang in TargetLanguages)
            if (File.Exists(DocumentPath(file, lang))) File.Delete(DocumentPath(file, lang));
        Save();
    }

    public BilingualDocument LoadDocument(ProjectFile file, string targetLanguage)
    {
        var json = File.ReadAllText(DocumentPath(file, targetLanguage));
        return JsonSerializer.Deserialize<BilingualDocument>(json, Json)!;
    }

    public void SaveDocument(ProjectFile file, BilingualDocument doc)
    {
        Directory.CreateDirectory(Path.Combine(Folder, "arbeit"));
        var path = DocumentPath(file, doc.TargetLanguage);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Schreibt die übersetzte Zieldatei im Originalformat.</summary>
    public void ExportTarget(ProjectFile file, BilingualDocument doc, Stream output)
    {
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
