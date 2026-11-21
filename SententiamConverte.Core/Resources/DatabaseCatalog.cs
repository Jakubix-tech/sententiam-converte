using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Resources;

/// <summary>Eine Datenbank, wie die Übersicht sie zeigt.</summary>
/// <param name="Origin">„Benutzerordner“, „für alle Projekte mit …“ oder „Projekt X“</param>
public sealed record DatabaseInfo(ResourceKind Kind, string Name, string Path, string Origin, IReadOnlyList<string> Languages, long Entries,
    bool IsLanguageBound, string? ProjectFolder = null);

/// <summary>Eine empfohlene Datenbank: Typ, Name, Ort, Sprachen und warum sie hilft.</summary>
public sealed record DatabaseRecommendation(ResourceKind Kind, string Name, string Path, IReadOnlyList<string> Languages, string Reason);

/// <summary>
/// Überblick über alle Datenbanken (TM, Terminologie, Wissen) an einem Ort, Empfehlungen, was für ein Projekt oder ein
/// Sprachpaar noch fehlt, und Werkzeuge, sie anzulegen, wiederzuverwenden und zusammenzuführen. Neue Datenbanken
/// liegen alle im Ordner <see cref="Folder"/>, damit man sie leicht findet.
/// </summary>
public static class DatabaseCatalog
{
    /// <summary>Standardordner für Datenbanken, die nicht zu einem einzelnen Projekt gehören.</summary>
    public static string Folder => Path.Combine(AppPaths.DataRoot, "ressourcen");

    public static string KindName(ResourceKind kind) => kind switch
    {
        ResourceKind.Tm => "Translation Memory",
        ResourceKind.Termbase => "Terminologie",
        _ => "Wissensdatenbank",
    };

    private static string Pair(string source, string target) => $"{source}–{target}";

    public static string SuggestedPath(ResourceKind kind, string name) =>
        Path.Combine(Folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ResourceFactory.Extension(kind));

    /// <summary>
    /// Was einem Projekt noch fehlt, damit Übersetzungen, Begriffe und Wissen über das Projekt hinaus wiederverwendet
    /// werden: je Sprachpaar eine gemeinsame TM und Terminologie, je Zielsprache eine Wissensdatenbank. Gezählt wird
    /// alles, was außer den projekteigenen Datenbanken angebunden ist (auch sprachbezogen).
    /// </summary>
    public static List<DatabaseRecommendation> Recommend(TranslationProject p)
    {
        var refs = p.Resources.Concat(p.LanguageRefs).ToList();
        bool Covered(ResourceKind kind, params string[] languages) =>
            refs.Any(r => r.Kind == kind && languages.All(l => r.Languages.Count == 0 || r.AppliesTo(l)));
        var list = new List<DatabaseRecommendation>();
        foreach (var target in p.TargetLanguages)
        {
            var pair = Pair(p.SourceLanguage, target);
            var languages = new[] { p.SourceLanguage, target };
            if (!Covered(ResourceKind.Tm, languages))
                list.Add(new(ResourceKind.Tm, $"TM {pair}", SuggestedPath(ResourceKind.Tm, $"TM {pair}"), languages,
                    $"Bestätigte Übersetzungen {Languages.DisplayName(p.SourceLanguage)} → {Languages.DisplayName(target)} landen hier und werden in jedem Projekt mit diesem Sprachpaar als Treffer vorgeschlagen. Die Projekt-TM gilt nur für dieses Projekt."));
            if (!Covered(ResourceKind.Termbase, languages))
                list.Add(new(ResourceKind.Termbase, $"Terminologie {pair}", SuggestedPath(ResourceKind.Termbase, $"Terminologie {pair}"), languages,
                    "Fachbegriffe, die in allen Projekten gleich übersetzt werden sollen: Der Editor zeigt sie in jedem Projekt mit diesem Sprachpaar, die KI hält sich daran, die Qualitätsprüfung meldet Abweichungen."));
            if (!Covered(ResourceKind.Knowledge, target))
                list.Add(new(ResourceKind.Knowledge, $"Wissen {target}", SuggestedPath(ResourceKind.Knowledge, $"Wissen {target}"), [target],
                    $"Stilregeln, Sprachgebrauch und Fachwissen für {Languages.DisplayName(target)}: Die KI liest passende Einträge bei jeder Übersetzung mit, die Agenten legen hier ihre Rechercheergebnisse ab."));
        }
        return list;
    }

    /// <summary>Legt die empfohlene Datenbank an (falls es die Datei noch nicht gibt) und bindet sie an die Sprachen.</summary>
    public static void Create(DatabaseRecommendation r, TranslationProject? project)
    {
        EnsureFile(r.Kind, r.Path);
        var reference = new ResourceRef { Kind = r.Kind, Location = r.Path, Languages = r.Languages.ToList(), WriteBack = true };
        if (project != null) project.AttachForLanguages(reference);
        else LanguageResources.Add(reference);
    }

    /// <summary>Legt eine leere Datenbankdatei an.</summary>
    public static void EnsureFile(ResourceKind kind, string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ILanguageResource created = kind switch
        {
            ResourceKind.Tm => new TranslationMemory(path),
            ResourceKind.Termbase => new Termbase(path),
            _ => new KnowledgeBase(path),
        };
        created.Dispose();
    }

    private static ResourceKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sctm" => ResourceKind.Tm,
        ".sctb" => ResourceKind.Termbase,
        ".sckb" => ResourceKind.Knowledge,
        _ => null,
    };

    private static long Count(ResourceKind kind, string path)
    {
        try
        {
            using ILanguageResource r = kind switch
            {
                ResourceKind.Tm => new TranslationMemory(path),
                ResourceKind.Termbase => new Termbase(path),
                _ => new KnowledgeBase(path),
            };
            return r switch
            {
                ITranslationMemory tm => tm.Count(),
                ITermbase tb => tb.ConceptCount(),
                IKnowledgeBase kb => kb.Count(),
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { return -1; }
    }

    /// <summary>
    /// Alle Datenbanken an einem Ort: der Standardordner, alle sprachbezogenen und die projekteigenen Datenbanken der
    /// angegebenen (z. B. zuletzt geöffneten) Projekte – damit man Projekt-Datenbanken in weiteren Projekten
    /// wiederverwenden kann.
    /// </summary>
    public static List<DatabaseInfo> All(IEnumerable<string> projectFolders)
    {
        var bound = LanguageResources.Load().Where(r => !r.IsRemote).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DatabaseInfo>();
        void Add(string path, string origin, string? projectFolder = null, string? name = null)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || !seen.Add(full) || KindOf(full) is not { } kind) return;
            var languageRef = bound.FirstOrDefault(r => string.Equals(Path.GetFullPath(r.Location), full, StringComparison.OrdinalIgnoreCase));
            var langs = languageRef?.Languages ?? [];
            list.Add(new DatabaseInfo(kind, name ?? Path.GetFileNameWithoutExtension(full), full,
                languageRef != null ? $"für alle Projekte mit {(langs.Count == 0 ? "beliebigen Sprachen" : string.Join(", ", langs))}" : origin,
                langs, Count(kind, full), languageRef != null, projectFolder));
        }
        foreach (var r in bound) Add(r.Location, "für alle Projekte");
        if (Directory.Exists(Folder))
            foreach (var f in Directory.EnumerateFiles(Folder).Where(f => KindOf(f) != null).OrderBy(f => f))
                Add(f, Path.GetFileName(f) == Path.GetFileName(AgentResources.GlobalMemoryPath) ? "KI-Gedächtnis (allgemein)" : "Benutzerordner");
        foreach (var folder in projectFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = ProjectName(folder);
            if (name == null) continue;
            Add(Path.Combine(folder, "tm", "projekt.sctm"), $"Projekt „{name}“", folder, $"Projekt-TM „{name}“");
            Add(Path.Combine(folder, "terminologie", "projekt.sctb"), $"Projekt „{name}“", folder, $"Projekt-Terminologie „{name}“");
            Add(Path.Combine(folder, "wissen", "projekt.sckb"), $"Projekt „{name}“", folder, $"Projekt-Wissen „{name}“");
        }
        return list;
    }

    private static string? ProjectName(string folder)
    {
        try
        {
            var file = Path.Combine(folder, TranslationProject.FileName);
            return File.Exists(file) ? (string?)JsonNode.Parse(File.ReadAllText(file))?["Name"] ?? Path.GetFileName(folder) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Führt eine Datenbank in eine andere gleichen Typs zusammen (die Quelle bleibt unverändert). Was es im Ziel schon
    /// gibt, wird nicht verdoppelt. Ergebnis: Anzahl übernommener Einträge.
    /// </summary>
    public static int Merge(string sourcePath, string targetPath)
    {
        var kind = KindOf(sourcePath) ?? throw new ArgumentException("Unbekannter Datenbanktyp: " + sourcePath);
        if (KindOf(targetPath) != kind) throw new ArgumentException("Nur Datenbanken gleichen Typs lassen sich zusammenführen.");
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) return 0;
        switch (kind)
        {
            case ResourceKind.Tm:
            {
                using var source = new TranslationMemory(sourcePath);
                using var target = new TranslationMemory(targetPath);
                var existing = target.All().Select(u => (u.SourceLanguage, u.TargetLanguage, u.Source, u.Target)).ToHashSet();
                return target.AddMany(source.All()
                    .Where(u => !existing.Contains((u.SourceLanguage, u.TargetLanguage, u.Source, u.Target)))
                    .Select(u => new TmEntry(u.SourceLanguage, u.TargetLanguage, u.Source, u.Target, u.Created, u.Origin, null, u.ContextPrevious, u.ContextNext)));
            }
            case ResourceKind.Termbase:
            {
                using var source = new Termbase(sourcePath);
                using var target = new Termbase(targetPath);
                static string Key(Concept c) => string.Join("|", c.Terms.Select(t => $"{t.Language}:{t.Text}".ToLowerInvariant()).Order());
                var existing = target.AllConcepts().Select(Key).ToHashSet();
                return target.AddConcepts(source.AllConcepts().Where(c => !existing.Contains(Key(c)))
                    .Select(c => ((IReadOnlyList<(string, string, TermStatus)>)c.Terms.Select(t => (t.Language, t.Text, t.Status)).ToList(), c.Definition, c.Domain, c.Source)));
            }
            default:
            {
                using var source = new KnowledgeBase(sourcePath);
                using var target = new KnowledgeBase(targetPath);
                var existing = target.All().Select(e => (e.Title, e.Content)).ToHashSet();
                var added = 0;
                foreach (var e in source.All().Where(e => !existing.Contains((e.Title, e.Content))))
                {
                    target.Add(e with { Id = 0 });
                    added++;
                }
                return added;
            }
        }
    }
}
