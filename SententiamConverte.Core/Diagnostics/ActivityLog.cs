using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Diagnostics;

/// <summary>Einstellungen des Ablaufprotokolls (gehören zu den KI-Einstellungen).</summary>
public sealed class ActivityLogSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Wie viele Tage Protokolle aufgehoben werden.</summary>
    public int KeepDays { get; set; } = 14;
}

/// <summary>
/// Ablaufprotokoll zum Nachvollziehen: jeder Schritt des Programms (Meldungen der Oberfläche, Reiterwechsel, Fehler) und
/// jede Anfrage an eine Schnittstelle – was wohin geschickt wurde und was von wo zurückkam, mit Dauer. Eine JSON-Zeile je
/// Eintrag in <c>protokoll/protokoll-JJJJ-MM-TT.jsonl</c> im Datenordner; Anfrage und Antwort tragen dieselbe Kennung.
/// Das Protokoll bleibt auf diesem Rechner, Schlüssel stehen nicht darin. Es darf das Programm nie stören: Schreibfehler
/// werden verschluckt.
/// </summary>
public static class ActivityLog
{
    /// <summary>Längster gespeicherter Inhalt je Eintrag (Rest gekürzt).</summary>
    public const int MaxDetail = 20_000;

    private static readonly object Gate = new();
    private static readonly string Session = Guid.NewGuid().ToString("N")[..4];
    private static int _counter;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool Enabled { get; set; } = true;
    public static int KeepDays { get; set; } = 14;

    public static string Folder => FolderOverride ?? Path.Combine(AppPaths.DataRoot, "protokoll");

    /// <summary>Für Tests: eigener Ordner, damit nichts im echten Protokoll landet.</summary>
    internal static string? FolderOverride { get; set; }
    public static string CurrentFile => Path.Combine(Folder, $"protokoll-{DateTime.Now:yyyy-MM-dd}.jsonl");

    /// <summary>Kennung, die Anfrage und Antwort verbindet (eindeutig je Programmstart).</summary>
    public static string NextId() => $"{Session}-{Interlocked.Increment(ref _counter)}";

    public static void Apply(ActivityLogSettings settings)
    {
        Enabled = settings.Enabled;
        KeepDays = Math.Max(1, settings.KeepDays);
    }

    /// <summary>
    /// Eintrag schreiben. <paramref name="area"/>: Bereich (Oberfläche, Datenausgang, Lokale Übersetzung …),
    /// <paramref name="step"/>: was geschah, <paramref name="target"/>: Dienst oder Ort, <paramref name="detail"/>: Inhalt.
    /// </summary>
    public static void Write(string area, string step, string? target = null, string? detail = null, long? milliseconds = null, string? id = null)
    {
        if (!Enabled) return;
        var entry = new JsonObject { ["zeit"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), ["bereich"] = area, ["schritt"] = step };
        if (id != null) entry["id"] = id;
        if (target != null) entry["ziel"] = target;
        if (milliseconds != null) entry["dauer_ms"] = milliseconds;
        if (detail is { Length: > 0 })
            entry["inhalt"] = detail.Length > MaxDetail ? detail[..MaxDetail] + $"\n… ({detail.Length - MaxDetail:N0} weitere Zeichen)" : detail;
        var line = entry.ToJsonString(Json);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Protokolle, die älter als <see cref="KeepDays"/> sind, löschen.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var limit = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(Folder, "protokoll-*.jsonl"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file)["protokoll-".Length..], "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day) && day < limit)
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Alle Protokolle löschen.</summary>
    public static void DeleteAll()
    {
        lock (Gate)
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var file in Directory.EnumerateFiles(Folder, "protokoll-*.jsonl")) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
