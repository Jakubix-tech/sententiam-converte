using System.Text.Json;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Agents;

/// <summary>Was die KI in einem Ausgangssatz an Ausdrücken fand, die Recherche brauchen – und wie sie den Satz bewertete.</summary>
public sealed class SegmentScan
{
    /// <summary>Der Ausgangssatz ohne Tags, Leerraum zusammengefasst (siehe <see cref="SegmentScanStore.KeyOf"/>).</summary>
    public string Source { get; set; } = "";
    public string TargetLanguage { get; set; } = "";
    /// <summary>Die gefundenen Ausdrücke (ohne Segmentnummern – die gehören zum jeweiligen Dokument); leer = nichts zu recherchieren.</summary>
    public List<ResearchItem> Items { get; set; } = new();
    /// <summary>Intelligente Bewertung („easy“ … „very_hard“) und ihre Zeile – leer, solange keine lief.</summary>
    public string? AssessmentLevel { get; set; }
    public string? AssessmentInfo { get; set; }
    public DateTime When { get; set; }
}

/// <summary>
/// Gefundene Ausdrücke je Ausgangssatz, im Projektordner („recherche-ausdruecke.json“): Wer ein Dokument schließt und
/// wieder öffnet oder denselben Satz in einem anderen Dokument des Projekts hat, bekommt die Ausdrücke und ihre Dossiers
/// sofort – die KI muss den Satz nicht noch einmal lesen, und ein diesmal etwas anders erfasster Ausdruck löst keine neue
/// Recherche aus. Ein geänderter Satz ist ein neuer Satz. Die letzten 20 000 Sätze.
/// </summary>
public static class SegmentScanStore
{
    private const int Keep = 20000;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string PathOf(TranslationProject p) => Path.Combine(p.Folder, "recherche-ausdruecke.json");

    /// <summary>Schlüssel eines Satzes: ohne Tags, Leerraum zusammengefasst, klein.</summary>
    public static string KeyOf(string source) =>
        string.Join(' ', TagText.StripTags(source).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static List<SegmentScan> Load(TranslationProject p)
    {
        lock (Lock)
        {
            try { return File.Exists(PathOf(p)) ? JsonSerializer.Deserialize<List<SegmentScan>>(File.ReadAllText(PathOf(p))) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>Je Satz der neueste Eintrag für diese Zielsprache.</summary>
    public static Dictionary<string, SegmentScan> Index(IEnumerable<SegmentScan> scans, string targetLanguage)
    {
        var index = new Dictionary<string, SegmentScan>(StringComparer.Ordinal);
        foreach (var s in scans.Where(s => Languages.Matches(s.TargetLanguage, targetLanguage)).OrderBy(s => s.When)) index[s.Source] = s;
        return index;
    }

    private static ResearchItem Copy(ResearchItem i) => new()
    {
        Expression = i.Expression, Context = i.Context, Category = i.Category, Reason = i.Reason,
        Difficulty = i.Difficulty, DifficultyReason = i.DifficultyReason,
    };

    /// <summary>
    /// Festhalten, was die KI in einem Satz fand (<paramref name="items"/>, null = unverändert) und wie sie ihn bewertete
    /// (<paramref name="level"/>, null = unverändert). Liefert den gespeicherten Eintrag; bei einem Schreibfehler nur im
    /// Speicher.
    /// </summary>
    public static SegmentScan Save(TranslationProject p, string source, string targetLanguage, IEnumerable<ResearchItem>? items,
        string? level = null, string? info = null) =>
        Update(p, [(source, items)], targetLanguage, level, info)[0];

    /// <summary>Viele Sätze auf einmal (Durchsicht des ganzen Dokuments) – die Datei wird nur einmal geschrieben.</summary>
    public static List<SegmentScan> SaveMany(TranslationProject p, IEnumerable<(string Source, IEnumerable<ResearchItem> Items)> scans, string targetLanguage) =>
        Update(p, scans.Select(x => (x.Source, (IEnumerable<ResearchItem>?)x.Items)).ToList(), targetLanguage, null, null);

    private static List<SegmentScan> Update(TranslationProject p, IReadOnlyList<(string Source, IEnumerable<ResearchItem>? Items)> scans, string targetLanguage,
        string? level, string? info)
    {
        lock (Lock)
        {
            var all = Load(p);
            var result = new List<SegmentScan>();
            foreach (var (source, items) in scans)
            {
                var key = KeyOf(source);
                var entry = all.LastOrDefault(s => s.Source == key && s.TargetLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
                            ?? new SegmentScan { Source = key, TargetLanguage = targetLanguage };
                all.Remove(entry);
                if (items != null)
                    entry.Items = items.Where(i => i.Expression.Trim().Length > 0).DistinctBy(i => i.Expression.Trim().ToLowerInvariant()).Select(Copy).ToList();
                if (level != null)
                {
                    entry.AssessmentLevel = level;
                    entry.AssessmentInfo = info;
                }
                entry.When = DateTime.UtcNow;
                all.Add(entry);
                result.Add(entry);
            }
            if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
            try { File.WriteAllText(PathOf(p), JsonSerializer.Serialize(all, Json)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
    }

    /// <summary>
    /// Die gespeicherten Ausdrücke für die Segmente eines Dokuments, je Ausdruck einmal (auch über Beugung und Artikel
    /// hinweg) mit allen Segmenten, in denen er steht. Dazu, welche Segmente schon gelesen sind.
    /// </summary>
    public static (List<ResearchItem> Items, HashSet<int> Scanned) ForDocument(IReadOnlyDictionary<string, SegmentScan> index, IEnumerable<Segment> segments)
    {
        var items = new List<ResearchItem>();
        var scanned = new HashSet<int>();
        foreach (var seg in segments)
        {
            if (!index.TryGetValue(KeyOf(seg.Source), out var scan)) continue;
            scanned.Add(seg.Id);
            foreach (var i in scan.Items)
            {
                var have = items.FirstOrDefault(x => DocumentResearch.SameExpression(x.Expression, i.Expression));
                if (have == null)
                {
                    have = Copy(i);
                    if (have.Context.Length == 0) have.Context = TagText.StripTags(seg.Source);
                    items.Add(have);
                }
                if (!have.SegmentIds.Contains(seg.Id)) have.SegmentIds.Add(seg.Id);
            }
        }
        return (items, scanned);
    }
}

/// <summary>Eine Meldung zu einem Segment aus der Arbeit der KI (Übersetzung, Recherche, Netzprüfung, Rückmeldung …).</summary>
/// <param name="Severity">„Fehler“, „Warnung“ oder „Hinweis“.</param>
public sealed record SegmentNote(int SegmentId, string Kind, string Severity, string Text, DateTime When);

/// <summary>
/// Meldungen je Segment eines Dokuments, im Projektordner unter „meldungen“ – damit sie auch nach dem Schließen und
/// erneuten Öffnen beim Segment stehen. Je Segment die letzten 30, je Dokument die letzten 5000.
/// </summary>
public static class SegmentNotes
{
    private const int KeepPerSegment = 30;
    private const int Keep = 5000;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string PathOf(TranslationProject p, string fileId) =>
        Path.Combine(p.Folder, "meldungen", string.Concat(fileId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");

    public static List<SegmentNote> Load(TranslationProject p, string fileId)
    {
        lock (Lock)
        {
            try { return File.Exists(PathOf(p, fileId)) ? JsonSerializer.Deserialize<List<SegmentNote>>(File.ReadAllText(PathOf(p, fileId))) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>Alle Meldungen einer Art zu einem Segment durch eine neue ersetzen (null: nur entfernen) – z. B. eine neue KI-Prüfung.</summary>
    public static void Replace(TranslationProject p, string fileId, int segmentId, string kind, SegmentNote? note)
    {
        lock (Lock)
        {
            var all = Load(p, fileId);
            if (all.RemoveAll(n => n.SegmentId == segmentId && n.Kind == kind) == 0 && note == null) return;
            if (note != null) all.Add(note);
            Write(p, fileId, all);
        }
    }

    private static void Write(TranslationProject p, string fileId, List<SegmentNote> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(p, fileId))!);
            File.WriteAllText(PathOf(p, fileId), JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Meldung anhängen; dieselbe noch einmal für dasselbe Segment ersetzt die frühere (neue Zeit).</summary>
    public static void Add(TranslationProject p, string fileId, SegmentNote note)
    {
        lock (Lock)
        {
            var all = Load(p, fileId);
            all.RemoveAll(n => n.SegmentId == note.SegmentId && n.Kind == note.Kind && n.Text == note.Text);
            all.Add(note);
            var mine = all.Where(n => n.SegmentId == note.SegmentId).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - KeepPerSegment))) all.Remove(old);
            if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
            Write(p, fileId, all);
        }
    }
}
