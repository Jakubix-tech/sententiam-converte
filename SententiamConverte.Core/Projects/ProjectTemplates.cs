using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Projects;

/// <summary>
/// Vorlage für neue Projekte, z. B. je Kunde: Sprachen, Fachgebiet, Fachquellen, QA, Datenschutz, Preise,
/// Fehlerbewertung und die zusätzlich angebundenen Datenbanken. Gespeichert in DataRoot/projektvorlagen.json.
/// </summary>
public sealed class ProjectTemplate
{
    public string Name { get; set; } = "";
    public string Client { get; set; } = "";
    public string SourceLanguage { get; set; } = "de-DE";
    public List<string> TargetLanguages { get; set; } = new();
    public string Topic { get; set; } = "";
    public string? SourceProfile { get; set; }
    public int PretranslateMinScore { get; set; } = 100;
    public bool ConfirmContextMatches { get; set; } = true;
    public bool AutoLocalize { get; set; } = true;
    public QaSettings Qa { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public RateSettings Rates { get; set; } = new();
    public LqaSettings Lqa { get; set; } = new();

    /// <summary>Zusätzlich angebundene Datenbanken (lokal oder Teamserver); projekteigene gehören nicht dazu.</summary>
    public List<ResourceRef> Resources { get; set; } = new();

    public override string ToString() => Name;
}

public static class ProjectTemplates
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "projektvorlagen.json");

    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    public static List<ProjectTemplate> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<ProjectTemplate>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public static void Save(List<ProjectTemplate> templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(templates.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), Json));
    }

    /// <summary>Die Einstellungen eines Projekts als Vorlage (gleichnamige wird ersetzt).</summary>
    public static ProjectTemplate SaveFrom(TranslationProject p, string name)
    {
        var t = new ProjectTemplate
        {
            Name = name.Trim(), Client = p.Client, SourceLanguage = p.SourceLanguage, TargetLanguages = p.TargetLanguages.ToList(), Topic = p.Topic,
            SourceProfile = p.SourceProfile, PretranslateMinScore = p.PretranslateMinScore, ConfirmContextMatches = p.ConfirmContextMatches,
            AutoLocalize = p.AutoLocalize, Qa = Clone(p.Qa), Privacy = p.Privacy.Clone(), Rates = p.Rates.Clone(), Lqa = Clone(p.Lqa),
            Resources = p.Resources.Select(Clone).ToList(),
        };
        var all = Load();
        all.RemoveAll(x => x.Name.Equals(t.Name, StringComparison.CurrentCultureIgnoreCase));
        all.Add(t);
        Save(all);
        return t;
    }

    public static void Delete(string name)
    {
        var all = Load();
        if (all.RemoveAll(x => x.Name == name) > 0) Save(all);
    }

    /// <summary>Einstellungen und Datenbanken der Vorlage auf ein (neues) Projekt übertragen.</summary>
    public static void Apply(ProjectTemplate t, TranslationProject p)
    {
        if (p.Client.Length == 0) p.Client = t.Client;
        if (p.Topic.Length == 0) p.Topic = t.Topic;
        p.SourceProfile = t.SourceProfile;
        p.PretranslateMinScore = t.PretranslateMinScore;
        p.ConfirmContextMatches = t.ConfirmContextMatches;
        p.AutoLocalize = t.AutoLocalize;
        p.Qa = Clone(t.Qa);
        p.Privacy = t.Privacy.Clone();
        p.Rates = t.Rates.Clone();
        p.Lqa = Clone(t.Lqa);
        foreach (var r in t.Resources) p.Attach(Clone(r));
        p.Save();
    }
}

/// <summary>Benannte QA-Einstellungen, z. B. je Kunde („Kunde A: strenge Längen“, „Software: Platzhalter“).</summary>
public sealed record QaProfile(string Name, QaSettings Settings)
{
    public override string ToString() => Name;
}

public static class QaProfiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "qa-profile.json");

    public static List<QaProfile> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<QaProfile>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public static void Save(string name, QaSettings settings)
    {
        var all = Load();
        all.RemoveAll(p => p.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        all.Add(new QaProfile(name.Trim(), ProjectTemplates.Clone(settings)));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), Json));
    }

    public static void Delete(string name)
    {
        var all = Load();
        if (all.RemoveAll(p => p.Name == name) == 0) return;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }

    public static QaSettings CloneSettings(QaSettings s) => ProjectTemplates.Clone(s);
}
