using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Resources;

/// <summary>
/// Sprachbezogene Ressourcen: TMs, Terminologien und Wissensdatenbanken, die nicht an ein Projekt gebunden sind,
/// sondern an Sprachen (z. B. „de-DE, pl-PL“). Sie gelten in jedem Projekt mit passenden Sprachen und für die
/// Agenten auch ohne geöffnetes Projekt. Die Liste liegt im Benutzerordner, nicht in den Projektdateien.
/// </summary>
public static class LanguageResources
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly object Lock = new();

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "sprachressourcen.json");

    public static List<ResourceRef> Load()
    {
        lock (Lock)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ResourceRef>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException) { return []; }
        }
    }

    private static void Save(List<ResourceRef> refs)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(refs, Json));
        }
    }

    public static bool Same(ResourceRef a, ResourceRef b) =>
        a.Kind == b.Kind && string.Equals(a.Server ?? "", b.Server ?? "", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(a.Location, b.Location, StringComparison.OrdinalIgnoreCase);

    public static void Add(ResourceRef r)
    {
        var all = Load();
        all.RemoveAll(x => Same(x, r));
        all.Add(r);
        Save(all);
    }

    public static void Remove(ResourceRef r)
    {
        var all = Load();
        if (all.RemoveAll(x => Same(x, r)) > 0) Save(all);
    }

    /// <summary>
    /// Die Ressourcen für ein Sprachpaar: Mit zwei oder mehr Sprachen (z. B. TM de-DE–pl-PL) müssen Ausgangs- und eine
    /// Zielsprache passen, mit einer Sprache (z. B. Wissen pl-PL) genügt diese, ohne Sprachangabe gilt sie überall.
    /// </summary>
    public static List<ResourceRef> For(string source, IEnumerable<string> targets)
    {
        var t = targets.ToList();
        return Load().Where(r => r.Languages.Count switch
        {
            0 => true,
            1 => r.AppliesTo(source) || t.Any(r.AppliesTo),
            _ => r.AppliesTo(source) && t.Any(r.AppliesTo),
        }).ToList();
    }
}

/// <summary>
/// Eigene Verbindungen eines Agenten zu den Datenbanken, die er braucht. Sie sind unabhängig vom gerade geöffneten
/// Projekt: Ein Agent läuft weiter (und speichert sein Ergebnis), auch wenn man inzwischen ein anderes Projekt öffnet.
/// </summary>
public sealed class AgentResources : IDisposable
{
    /// <summary>Allgemeines KI-Gedächtnis, wenn kein Projekt geöffnet ist.</summary>
    public static string GlobalMemoryPath => Path.Combine(AppPaths.DataRoot, "ressourcen", "ki-gedaechtnis.sckb");

    /// <summary>Wissensdatenbanken; die erste ist das Gedächtnis, in das Ergebnisse geschrieben werden.</summary>
    public List<IKnowledgeBase> Knowledge { get; } = new();
    public List<ITermbase> Termbases { get; } = new();
    public List<ITranslationMemory> Tms { get; } = new();

    /// <summary>Was nicht geöffnet werden konnte (z. B. Teamserver nicht erreichbar).</summary>
    public List<string> Errors { get; } = new();

    public void Add(ILanguageResource resource)
    {
        switch (resource)
        {
            case ITranslationMemory tm: Tms.Add(tm); break;
            case ITermbase tb: Termbases.Add(tb); break;
            case IKnowledgeBase kb: Knowledge.Add(kb); break;
        }
    }

    public void Open(IEnumerable<ResourceRef> refs, Remote.ServerSettings servers)
    {
        foreach (var r in refs)
        {
            if (Knowledge.Concat<ILanguageResource>(Termbases).Concat(Tms).Any(x => !x.IsRemote && !r.IsRemote
                    && string.Equals(Path.GetFullPath(x.Location), Path.GetFullPath(r.Location), StringComparison.OrdinalIgnoreCase)))
                continue;
            try { Add(ResourceFactory.Open(r, servers)); }
            catch (Exception ex) when (ex is Remote.RemoteException or IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                Errors.Add($"{r.DisplayName}: {ex.Message}");
            }
        }
    }

    /// <summary>Ohne Projekt: das allgemeine KI-Gedächtnis und die sprachbezogenen Ressourcen der Sprachen.</summary>
    public static AgentResources ForLanguages(string source, IEnumerable<string> targets, Remote.ServerSettings servers)
    {
        var res = new AgentResources();
        Directory.CreateDirectory(Path.GetDirectoryName(GlobalMemoryPath)!);
        res.Knowledge.Add(new KnowledgeBase(GlobalMemoryPath, "KI-Gedächtnis"));
        res.Open(LanguageResources.For(source, targets), servers);
        return res;
    }

    public void Dispose()
    {
        foreach (var r in Knowledge.Concat<ILanguageResource>(Termbases).Concat(Tms)) r.Dispose();
        Knowledge.Clear();
        Termbases.Clear();
        Tms.Clear();
    }
}
