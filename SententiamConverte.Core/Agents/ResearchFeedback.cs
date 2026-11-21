using System.Text.Json;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Eine Rückmeldung: Wurde die empfohlene Benennung beim Bestätigen übernommen? Mit Gütestufe und den stützenden Seiten;
/// dazu, was in der Übersetzung stand und wie es erkannt wurde („wörtlich“, „gebeugt“, „KI-Zuordnung“, „Prüfer“ – ältere
/// Einträge: leer).
/// </summary>
public sealed record FeedbackEntry(string Key, string Expression, string Grade, bool Accepted, List<string> Domains, DateTime When,
    string? Chosen = null, string? How = null);

/// <summary>Wie oft Empfehlungen einer Gütestufe übernommen wurden.</summary>
public sealed record GradeCalibration(string Grade, int Count, int Accepted);

/// <summary>
/// Rückmeldung aus dem Bestätigen: Ob eine empfohlene Benennung stehen blieb oder ersetzt wurde, ist der beste Maßstab
/// dafür, ob eine Recherche gut war – besser als „belegt“. Daraus ergeben sich zwei Dinge: Die Gütestufen werden
/// überprüfbar („Güte A wird zu 96 % übernommen“), und die Seiten, die eine übernommene Benennung stützten, gewinnen Ruf in
/// der Quellen-Datenbank (ersetzte verlieren ihn). Je Segment und Ausdruck zählt eine Rückmeldung; ändert sie sich bei einem
/// erneuten Bestätigen, wird die frühere zurückgenommen. Im Datenordner dieses Rechners, die letzten 2000.
/// </summary>
public static class ResearchFeedback
{
    private const int Keep = 2000;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly AsyncLocal<string?> LocalFolder = new();

    /// <summary>Für Tests: in diesem Ablauf eine eigene Rückmeldung in diesem Ordner.</summary>
    public static void UseFolderHere(string folder) => LocalFolder.Value = folder;

    private static string PathOf => Path.Combine(LocalFolder.Value ?? AppPaths.DataRoot, "recherche-rueckmeldung.json");

    public static List<FeedbackEntry> All()
    {
        lock (Lock)
        {
            try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<List<FeedbackEntry>>(File.ReadAllText(PathOf)) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException) { return []; }
        }
    }

    /// <summary>Die Seiten, die die empfohlene Benennung eines Dossiers wörtlich stützen (nur Web, keine eigenen Fachtexte).</summary>
    public static List<string> SupportingDomains(TermDossier d)
    {
        var recommended = d.Recommended?.Trim() ?? "";
        if (recommended.Length == 0) return [];
        return d.Sources.Where(s => s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                    && Resources.ReferenceLibrary.Mentions((s.Quote ?? "") + " " + s.Title, recommended))
            .Select(s => Resources.SiteCatalog.DomainOf(s.Url)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rückmeldung festhalten (<paramref name="key"/>: Projekt, Datei, Segment, Ausdruck). Gleiche Rückmeldung noch einmal:
    /// nichts; geänderte: die frühere wird zurückgenommen, auch in der Quellen-Datenbank. Liefert, ob sich etwas änderte.
    /// </summary>
    public static bool Record(string key, TermDossier d, bool accepted, string? chosen = null, string? how = null)
    {
        lock (Lock)
        {
            var all = All();
            var old = all.FirstOrDefault(e => e.Key == key);
            if (old != null && old.Accepted == accepted) return false;
            if (old != null)
            {
                Resources.SiteCatalog.RecordFeedback(old.Domains, old.Accepted, -1);
                all.Remove(old);
            }
            var domains = SupportingDomains(d);
            all.Add(new FeedbackEntry(key, d.Expression, DossierStore.Grade(d), accepted, domains, DateTime.UtcNow, chosen, how));
            if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
            if (!Save(all)) return false;
            Resources.SiteCatalog.RecordFeedback(domains, accepted);
            return true;
        }
    }

    /// <summary>
    /// Rückmeldung zurücknehmen – beim erneuten Bestätigen ist der Ausdruck nur noch umschrieben, die frühere Rückmeldung
    /// passt nicht mehr zur Übersetzung. Liefert, ob es eine gab.
    /// </summary>
    public static bool Withdraw(string key)
    {
        lock (Lock)
        {
            var all = All();
            var old = all.FirstOrDefault(e => e.Key == key);
            if (old == null) return false;
            all.Remove(old);
            if (!Save(all)) return false;
            Resources.SiteCatalog.RecordFeedback(old.Domains, old.Accepted, -1);
            return true;
        }
    }

    private static bool Save(List<FeedbackEntry> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(all, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Je Gütestufe: wie viele Rückmeldungen, wie viele übernommen.</summary>
    public static List<GradeCalibration> Calibration() =>
        All().GroupBy(e => e.Grade).OrderBy(g => g.Key).Select(g => new GradeCalibration(g.Key, g.Count(), g.Count(e => e.Accepted))).ToList();

    /// <summary>„Übernommen beim Bestätigen: Güte A 12 von 12 · B 8 von 10 …“ (oder null, solange es keine Rückmeldung gibt).</summary>
    public static string? Summary()
    {
        var c = Calibration();
        return c.Count == 0 ? null : string.Join(" · ", c.Select(g => $"{g.Grade} {g.Accepted}/{g.Count}"));
    }
}
