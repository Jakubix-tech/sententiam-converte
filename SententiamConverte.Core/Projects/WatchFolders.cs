using System.Text.Json;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Projects;

/// <summary>Ein überwachter Eingangsordner: neue Dateien gehen automatisch in ein Projekt und werden vorübersetzt.</summary>
public sealed class WatchRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Folder { get; set; } = "";
    public string ProjectFolder { get; set; } = "";
    public bool Pretranslate { get; set; } = true;
    public bool AiTranslate { get; set; }
    public bool ExportTargets { get; set; } = true;

    /// <summary>Wohin die Zieldateien kommen; leer = Unterordner „fertig“ im Eingangsordner.</summary>
    public string OutputFolder { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string DoneFolder => Path.Combine(Folder, "erledigt");
    public string TargetFolder => OutputFolder.Length > 0 ? OutputFolder : Path.Combine(Folder, "fertig");
}

/// <summary>Ergebnis für eine verarbeitete Datei.</summary>
public sealed record WatchResult(string File, int Segments, int FromTm, int ByAi, List<string> Outputs);

/// <summary>Die überwachten Ordner (DataRoot/ueberwachte-ordner.json).</summary>
public static class WatchRules
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "ueberwachte-ordner.json");

    public static List<WatchRule> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<WatchRule>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public static void Save(IEnumerable<WatchRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(rules.ToList(), Json));
    }
}

/// <summary>
/// Verarbeitet neue Dateien eines überwachten Ordners: ins Projekt aufnehmen, aus dem TM vorübersetzen, auf Wunsch
/// leere Segmente per KI übersetzen (als Entwurf), Zieldateien ausgeben und das Original nach „erledigt“ verschieben.
/// Bestätigt wird nichts – die Übersetzung bleibt zum Prüfen im Projekt.
/// </summary>
public static class WatchProcessor
{
    /// <summary>Dateien, die fertig geschrieben sind (seit <paramref name="settle"/> unverändert) und unterstützt werden.</summary>
    public static List<string> PendingFiles(WatchRule rule, TimeSpan? settle = null)
    {
        if (!Directory.Exists(rule.Folder)) return [];
        var minAge = settle ?? TimeSpan.FromSeconds(3);
        return Directory.EnumerateFiles(rule.Folder)
            .Where(f => FileFilters.ForFile(f) != null && !Path.GetFileName(f).StartsWith('~') && !Path.GetFileName(f).StartsWith('.'))
            .Where(f =>
            {
                var info = new FileInfo(f);
                return info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc >= minAge;
            })
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToList();
    }

    public static async Task<WatchResult> ProcessAsync(WatchRule rule, string path, TranslationProject project, AgentContext? ai = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        ProjectFile file;
        await using (var input = File.OpenRead(path)) file = project.AddFile(input, name);
        int segments = 0, fromTm = 0, byAi = 0;
        var outputs = new List<string>();
        foreach (var lang in project.LanguagesOf(file))
        {
            ct.ThrowIfCancellationRequested();
            var doc = project.LoadDocument(file, lang);
            segments += doc.Segments.Count(s => !s.Locked);
            if (rule.Pretranslate) fromTm += Workflow.Pretranslate(project, doc).Applied;
            if (rule.AiTranslate && ai != null)
            {
                var translator = new AiTranslator(ai);
                var todo = doc.Segments.Where(s => !s.Locked && s.Target.Length == 0).ToList();
                var dossiers = Agents.DossierStore.Load(project);
                foreach (var s in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    log?.Report($"{name} ({lang}): KI übersetzt {byAi + 1}/{todo.Count} …");
                    var ctxInfo = Workflow.ContextOf(doc, s);
                    s.Target = await translator.TranslateAsync(s.Source, doc.SourceLanguage, doc.TargetLanguage, project.Topic,
                        Workflow.Lookup(project, doc, s), Workflow.Terms(project, doc, s), ctxInfo.Previous, ctxInfo.Next, ct: ct,
                        knowledge: Workflow.Knowledge(project, doc, s), brief: project.Brief(file, lang), research: Workflow.Research(project, doc, s, dossiers));
                    if (project.AutoLocalize) s.Target = Tm.AutoLocalizer.Localize(s.Source, s.Target, doc.SourceLanguage, doc.TargetLanguage);
                    s.Origin = TranslationOrigin.Ai;
                    s.Suggestion = s.Target;
                    s.Status = SegmentStatus.Draft;
                    byAi++;
                }
            }
            project.SaveDocument(file, doc);
            if (rule.ExportTargets)
            {
                var folder = Path.Combine(rule.TargetFolder, lang);
                Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, project.TargetFileName(file, lang));
                await using (var output = File.Create(target)) project.ExportTarget(file, doc, output);
                outputs.Add(target);
            }
        }
        Directory.CreateDirectory(rule.DoneFolder);
        var done = Path.Combine(rule.DoneFolder, name);
        if (File.Exists(done)) done = Path.Combine(rule.DoneFolder, $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(name)}");
        File.Move(path, done);
        return new WatchResult(name, segments, fromTm, byAi, outputs);
    }
}
