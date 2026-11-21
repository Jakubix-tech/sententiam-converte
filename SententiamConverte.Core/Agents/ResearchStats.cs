using System.Text.Json;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>Eine abgeschlossene Recherche: mit welcher KI, wie schwierig, wie viel Zeit sie hatte und brauchte, ob belegt.</summary>
public sealed record ResearchStat(DateTime When, string Model, ResearchDifficulty Level, double BudgetMinutes, double Minutes, int Rounds, bool Evidenced);

/// <summary>Was die Statistik über eine Schwierigkeitsstufe sagt: wie oft belegt und nach wie vielen Minuten typischerweise.</summary>
public sealed record LevelStats(ResearchDifficulty Level, int Count, int Evidenced, double? TypicalMinutes);

/// <summary>
/// Recherche-Statistik dieses Rechners: Die intelligente Bewertung lernt daraus, wie viel Zeit ein Ausdruck mit der
/// eingestellten KI wirklich braucht, bis er belegt ist – die eingebaute KI auf einem Rechner ohne Grafikkarte ist
/// langsamer als eine Online-KI, das sollen die Minuten widerspiegeln. Je KI und Schwierigkeitsstufe; die letzten 500
/// Recherchen, im Datenordner dieses Rechners (nur Zahlen, keine Ausdrücke).
/// </summary>
public static class ResearchStats
{
    private const int Keep = 500;

    /// <summary>Ab so vielen belegten Recherchen einer Stufe gilt das Gelernte statt der festen Annahme.</summary>
    public const int MinSamples = 4;

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly AsyncLocal<string?> LocalFolder = new();

    /// <summary>Für Tests: in diesem Ablauf eine eigene Statistik in diesem Ordner.</summary>
    public static void UseFolderHere(string folder) => LocalFolder.Value = folder;

    private static string PathOf => Path.Combine(LocalFolder.Value ?? AppPaths.DataRoot, "recherche-statistik.json");

    public static List<ResearchStat> All()
    {
        lock (Lock)
        {
            try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<List<ResearchStat>>(File.ReadAllText(PathOf)) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException) { return []; }
        }
    }

    public static void Record(ResearchStat stat)
    {
        lock (Lock)
        {
            var all = All();
            all.Add(stat);
            if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
                File.WriteAllText(PathOf, JsonSerializer.Serialize(all, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Je Schwierigkeitsstufe: Anzahl, davon belegt, und die typische Dauer bis zum Beleg (Median).</summary>
    public static List<LevelStats> Summary(string model)
    {
        var mine = All().Where(s => s.Model.Equals(model, StringComparison.OrdinalIgnoreCase)).ToList();
        return Enum.GetValues<ResearchDifficulty>().Select(level =>
        {
            var of = mine.Where(s => s.Level == level).ToList();
            var evidenced = of.Where(s => s.Evidenced).Select(s => s.Minutes).Order().ToList();
            return new LevelStats(level, of.Count, evidenced.Count, evidenced.Count > 0 ? evidenced[evidenced.Count / 2] : null);
        }).Where(l => l.Count > 0).ToList();
    }

    /// <summary>
    /// Gelernte Zeit für eine Stufe: die typische Dauer bis zum Beleg mit etwas Spielraum (×1,25), sobald es genug Fälle
    /// gibt; sonst null (dann gilt die feste Annahme).
    /// </summary>
    public static double? LearnedMinutes(string model, ResearchDifficulty level) =>
        Summary(model).FirstOrDefault(l => l.Level == level) is { Evidenced: >= MinSamples, TypicalMinutes: { } typical }
            ? Math.Clamp(Math.Round(typical * 1.25, 1), SegmentTriage.MinMinutes, SegmentTriage.MaxMinutes)
            : null;

    /// <summary>Hinweis für die Bewertung: was auf diesem Rechner gemessen wurde (oder null, wenn noch nichts da ist).</summary>
    public static string? PromptNote(string model)
    {
        var summary = Summary(model).Where(l => l.Count >= 2).ToList();
        if (summary.Count == 0) return null;
        return $"Measured on this computer with {model}: " + string.Join("; ", summary.Select(l =>
            $"{l.Level.ToString().ToLowerInvariant()} expressions: {l.Evidenced} of {l.Count} evidenced" +
            (l.TypicalMinutes is { } t ? $", typically after {t:0.#} min" : ""))) +
            ". Set the minutes so that an expression can be evidenced in time – not much more.";
    }
}
