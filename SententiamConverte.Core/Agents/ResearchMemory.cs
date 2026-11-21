using System.Text.Json;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Agents;

/// <summary>Ein Eintrag im Recherche-Gedächtnis: das Dossier, aus welchem Projekt, zu welchem Thema, wann.</summary>
public sealed class ResearchMemoryEntry
{
    public TermDossier Dossier { get; set; } = new();
    public string Project { get; set; } = "";
    public string? Topic { get; set; }
    public DateTime Saved { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Recherche-Gedächtnis über alle Projekte: Jedes Dossier, das ein Projekt speichert, kommt auch hierher – je Sprachpaar
/// (mit Land: Spanisch in Spanien ist nicht Spanisch in Mexiko) eine Datei im Datenordner dieses Rechners, nie auf einen
/// Server. Ohne Satzzusammenhang, Segmentnummern und Zitate aus eigenen Fachtexten (die können vertraulich sein); aus
/// vertraulichen Projekten nur, wenn dort eingeschaltet. Eine neue Recherche schaut zuerst hier nach (belegt und zum
/// Thema passend: übernehmen, sonst dort ansetzen), die Übersetzung nutzt die belegten Einträge, die zum Thema passen.
/// Ein belegter Eintrag wird nie durch einen unbelegten ersetzt.
/// <para>
/// Auch die Erklärungssprache zählt (Bedeutung, Begründung, Hinweise schreibt die KI in der eingestellten Sprache): Übernommen
/// und angezeigt wird nur, was in der aktuellen Erklärungssprache geschrieben ist – je Ausdruck und Erklärungssprache ein
/// Eintrag. Ein Eintrag in anderer Erklärungssprache gibt der Übersetzung nur die belegte Benennung (ohne Erklärungstext)
/// und einer neuen Recherche nur, was sprachneutral ist: Kandidaten, Anfragen, Belege.
/// </para>
/// </summary>
public static class ResearchMemory
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (DateTime Written, List<ResearchMemoryEntry> Entries)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static bool _enabled = true;
    private static readonly AsyncLocal<bool?> LocalEnabled = new();
    private static readonly AsyncLocal<string?> LocalFolder = new();

    /// <summary>Ein- und ausschalten (die Kerntests schalten es aus, außer wo sie es selbst prüfen).</summary>
    public static bool Enabled
    {
        get => LocalEnabled.Value ?? _enabled;
        set => _enabled = value;
    }

    /// <summary>Für Tests: in diesem Ablauf (und nur hier) ein eigenes, eingeschaltetes Gedächtnis in diesem Ordner.</summary>
    public static void UseFolderHere(string folder)
    {
        LocalFolder.Value = folder;
        LocalEnabled.Value = true;
    }

    public static string Folder => LocalFolder.Value ?? Path.Combine(AppPaths.DataRoot, "recherche-gedaechtnis");

    private static string FileFor(string source, string target) =>
        Path.Combine(Folder, $"{Safe(source)}__{Safe(target)}.json");

    private static string Safe(string language) => string.Concat(language.Where(c => char.IsLetterOrDigit(c) || c == '-'));

    /// <summary>Die aktuelle Erklärungssprache (setzt die Oberfläche; ohne Angabe wird nicht danach unterschieden).</summary>
    public static Func<string?>? CurrentExplanationLanguage { get; set; }

    /// <summary>In welcher Sprache die Erklärungen eines Dossiers stehen; ältere Dossiers: Deutsch (die Vorgabe des Programms).</summary>
    public static string ExplainedIn(TermDossier d) => string.IsNullOrWhiteSpace(d.ExplanationLanguage) ? "Deutsch" : d.ExplanationLanguage.Trim();

    private static bool SameExplanation(TermDossier d, string? language) =>
        string.IsNullOrWhiteSpace(language) || ExplainedIn(d).Equals(language.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Key(TermDossier d) => d.Expression.Trim().ToLowerInvariant() + "|" + ExplainedIn(d).ToLowerInvariant();

    private static bool SameExpression(TermDossier d, string expression) => DocumentResearch.SameExpression(d.Expression, expression);

    /// <summary>Alle Einträge eines Sprachpaars (aus dem Zwischenspeicher, solange die Datei unverändert ist).</summary>
    public static List<ResearchMemoryEntry> Entries(string sourceLanguage, string targetLanguage)
    {
        if (!Enabled) return [];
        var path = FileFor(sourceLanguage, targetLanguage);
        lock (Lock)
        {
            if (!File.Exists(path)) return [];
            var written = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Entries.ToList();
            List<ResearchMemoryEntry> entries;
            try { entries = JsonSerializer.Deserialize<List<ResearchMemoryEntry>>(File.ReadAllText(path)) ?? []; }
            catch (Exception ex) when (ex is JsonException or IOException) { entries = []; }
            Cache[path] = (written, entries);
            return entries.ToList();
        }
    }

    /// <summary>Darf ein Dossier dieses Projekts ins Gedächtnis? Nicht vertraulich, oder dort ausdrücklich eingeschaltet.</summary>
    public static bool MayRemember(TranslationProject project) => !project.Privacy.Enabled || project.Privacy.ShareResearch;

    /// <summary>
    /// Ein Dossier aufnehmen (von <see cref="DossierStore.Add"/> gerufen). Was selbst aus dem Gedächtnis kam, wird nicht noch
    /// einmal aufgenommen; ein belegter Eintrag wird nur durch einen neueren belegten ersetzt.
    /// </summary>
    public static void Remember(TermDossier d, string project, string? topic)
    {
        if (!Enabled || d.Origin != null || string.IsNullOrWhiteSpace(d.Expression)
            || string.IsNullOrWhiteSpace(d.SourceLanguage) || string.IsNullOrWhiteSpace(d.TargetLanguage)) return;
        var copy = Strip(d);
        lock (Lock)
        {
            var entries = Entries(d.SourceLanguage, d.TargetLanguage);
            var old = entries.FirstOrDefault(e => Key(e.Dossier) == Key(copy));
            if (old != null && DossierStore.IsEvidenced(old.Dossier) && !DossierStore.IsEvidenced(copy)) return;
            entries.RemoveAll(e => Key(e.Dossier) == Key(copy));
            entries.Add(new ResearchMemoryEntry { Dossier = copy, Project = project, Topic = topic, Saved = DateTime.UtcNow });
            Write(d.SourceLanguage, d.TargetLanguage, entries);
        }
    }

    /// <summary>Einen Eintrag entfernen (Liste im Reiter Recherche); ohne Erklärungssprache alle zu diesem Ausdruck.</summary>
    public static void Forget(string sourceLanguage, string targetLanguage, string expression, string? explanationLanguage = null)
    {
        lock (Lock)
        {
            var entries = Entries(sourceLanguage, targetLanguage);
            if (entries.RemoveAll(e => SameExpression(e.Dossier, expression) && SameExplanation(e.Dossier, explanationLanguage)) > 0)
                Write(sourceLanguage, targetLanguage, entries);
        }
    }

    private static void Write(string source, string target, List<ResearchMemoryEntry> entries)
    {
        Directory.CreateDirectory(Folder);
        var path = FileFor(source, target);
        File.WriteAllText(path, JsonSerializer.Serialize(entries.OrderBy(e => e.Dossier.Expression, StringComparer.CurrentCultureIgnoreCase).ToList(), Json));
        Cache[path] = (File.GetLastWriteTimeUtc(path), entries);
    }

    /// <summary>Was nicht ins Gedächtnis gehört: Satzzusammenhang, Datei, Segmente, Vorfassung, Zitate aus eigenen Fachtexten.</summary>
    private static TermDossier Strip(TermDossier d)
    {
        var copy = JsonSerializer.Deserialize<TermDossier>(JsonSerializer.Serialize(d))!;
        copy.Context = null;
        copy.FileId = null;
        copy.SegmentIds = [];
        copy.Previous = null;
        copy.Duration = null;
        copy.Sources = copy.Sources.Where(s => s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        return copy;
    }

    /// <summary>Gleiches Thema: beide leer bzw. eines unbekannt, gleich, oder ein gemeinsames Wort ab vier Buchstaben.</summary>
    public static bool SameTopic(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;
        if (a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        static HashSet<string> Words(string s) => s.Split([' ', ',', ';', '/', '-', '(', ')', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4).Select(w => w.ToLowerInvariant()).ToHashSet();
        return Words(a).Overlaps(Words(b));
    }

    /// <summary>Der Eintrag zu einem Ausdruck in dieser Erklärungssprache (ohne Angabe: irgendeiner), oder null.</summary>
    public static ResearchMemoryEntry? Find(string expression, string sourceLanguage, string targetLanguage, string? explanationLanguage = null) =>
        Entries(sourceLanguage, targetLanguage).Where(e => SameExpression(e.Dossier, expression) && SameExplanation(e.Dossier, explanationLanguage))
            .OrderByDescending(e => DossierStore.IsEvidenced(e.Dossier)).ThenByDescending(e => e.Saved).FirstOrDefault();

    /// <summary>
    /// Vor einer Recherche: Hat das Gedächtnis ein belegtes Ergebnis zum selben Thema und das Projekt keins, wird es
    /// übernommen (mit Herkunft, ohne neue Recherche). Sonst setzt die Recherche beim Dossier des Projekts an, ersatzweise
    /// beim Eintrag im Gedächtnis.
    /// </summary>
    public static (TermDossier? Reuse, TermDossier? Previous) Lookup(string expression, string sourceLanguage, string targetLanguage, string? topic,
        TermDossier? projectDossier, string? explanationLanguage = null)
    {
        explanationLanguage ??= CurrentExplanationLanguage?.Invoke();
        // übernommen wird nur, was in der aktuellen Erklärungssprache geschrieben ist; ansetzen kann die Recherche an jedem
        // (sie nutzt davon nur Kandidaten, Anfragen und Belege)
        var entry = Find(expression, sourceLanguage, targetLanguage, explanationLanguage);
        var any = entry ?? Find(expression, sourceLanguage, targetLanguage);
        if (entry != null && DossierStore.IsEvidenced(entry.Dossier) && !(projectDossier != null && DossierStore.IsEvidenced(projectDossier))
            && SameTopic(entry.Topic, topic))
        {
            var reuse = JsonSerializer.Deserialize<TermDossier>(JsonSerializer.Serialize(entry.Dossier))!;
            reuse.Origin = $"{entry.Project} ({entry.Saved.ToLocalTime():dd.MM.yyyy})";
            return (reuse, null);
        }
        return (null, projectDossier ?? any?.Dossier);
    }

    /// <summary>
    /// Für die Übersetzung: belegte Einträge zum selben Thema, die das Projekt nicht selbst hat. Steht ein Eintrag nur in
    /// einer anderen Erklärungssprache, geht nur die belegte Benennung mit – ohne Bedeutung, Strategie, Begründung.
    /// </summary>
    public static List<TermDossier> ForTranslation(string sourceLanguage, string targetLanguage, string? topic, IEnumerable<TermDossier> own,
        string? explanationLanguage = null)
    {
        explanationLanguage ??= CurrentExplanationLanguage?.Invoke();
        var have = own.Select(d => d.Expression.Trim().ToLowerInvariant()).ToHashSet();
        return Entries(sourceLanguage, targetLanguage)
            .Where(e => !have.Contains(e.Dossier.Expression.Trim().ToLowerInvariant()) && SameTopic(e.Topic, topic) && DossierStore.IsEvidenced(e.Dossier))
            .GroupBy(e => e.Dossier.Expression.Trim().ToLowerInvariant())
            .Select(g => g.FirstOrDefault(e => SameExplanation(e.Dossier, explanationLanguage)) is { } same ? same.Dossier : Neutral(g.First().Dossier))
            .ToList();
    }

    /// <summary>Nur das Sprachneutrale: Ausdruck, Benennungen, Belege – kein Erklärungstext in einer anderen Sprache.</summary>
    private static TermDossier Neutral(TermDossier d)
    {
        var copy = JsonSerializer.Deserialize<TermDossier>(JsonSerializer.Serialize(d))!;
        copy.Meaning = "";
        copy.Strategy = null;
        copy.Reasoning = null;
        copy.KeyFeatures = [];
        copy.Warnings = [];
        copy.DifficultyReason = "";
        copy.ConceptField = null;
        foreach (var c in copy.Candidates)
        {
            c.SuitableWhen = null;
            c.UnsuitableWhen = null;
            c.Differences = [];
        }
        return copy;
    }
}
