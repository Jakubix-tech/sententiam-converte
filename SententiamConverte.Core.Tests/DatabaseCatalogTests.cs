using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Datenbanken empfehlen, anlegen, finden, in anderen Projekten wiederverwenden und zusammenführen.</summary>
[Collection("BuiltInAi")]
public sealed class DatabaseCatalogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    public DatabaseCatalogTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _dir.File("home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public void MissingSharedDatabasesAreRecommendedCreatedAndThenCountForEveryProject()
    {
        using var p = TranslationProject.Create(_dir.File("p1"), "Anleitung", "de-DE", ["pl-PL", "en-GB"]);
        var recs = DatabaseCatalog.Recommend(p);
        Assert.Equal(6, recs.Count);                                                  // je Sprachpaar TM und Terminologie, je Zielsprache Wissen
        var tm = recs.First(r => r.Kind == ResourceKind.Tm);
        Assert.Equal("TM de-DE–pl-PL", tm.Name);
        Assert.Equal(Path.Combine(DatabaseCatalog.Folder, "TM de-DE–pl-PL.sctm"), tm.Path);
        Assert.Contains("jedem Projekt mit diesem Sprachpaar", tm.Reason);

        foreach (var r in recs) DatabaseCatalog.Create(r, p);
        Assert.Empty(DatabaseCatalog.Recommend(p));
        Assert.True(File.Exists(tm.Path));
        Assert.Contains(p.Tms, t => t.Location == tm.Path);

        using var p2 = TranslationProject.Create(_dir.File("p2"), "Zweites", "de-DE", ["pl-PL"]);
        Assert.Empty(DatabaseCatalog.Recommend(p2));                                  // gilt schon für alle Projekte mit de-DE/pl-PL
        using var p3 = TranslationProject.Create(_dir.File("p3"), "Drittes", "de-DE", ["fr-FR"]);
        Assert.Equal(3, DatabaseCatalog.Recommend(p3).Count);
    }

    [Fact]
    public void AllDatabasesAreListedIncludingThoseOfOtherProjects()
    {
        using (var p = TranslationProject.Create(_dir.File("alt"), "Altes Projekt", "de-DE", ["pl-PL"]))
            p.ProjectTm.Add("de-DE", "pl-PL", "Hallo", "Cześć");
        DatabaseCatalog.EnsureFile(ResourceKind.Knowledge, Path.Combine(DatabaseCatalog.Folder, "Stil Polnisch.sckb"));
        LanguageResources.Add(new ResourceRef { Kind = ResourceKind.Knowledge, Location = Path.Combine(DatabaseCatalog.Folder, "Stil Polnisch.sckb"), Languages = ["pl-PL"] });

        var all = DatabaseCatalog.All([_dir.File("alt")]);
        var projectTm = Assert.Single(all, d => d.Name == "Projekt-TM „Altes Projekt“");
        Assert.Equal(1, projectTm.Entries);
        Assert.Equal(_dir.File("alt"), projectTm.ProjectFolder);
        var style = Assert.Single(all, d => d.Name == "Stil Polnisch");
        Assert.True(style.IsLanguageBound);
        Assert.Equal("für alle Projekte mit pl-PL", style.Origin);

        // Projekt-TM in einem neuen Projekt wiederverwenden
        using var neu = TranslationProject.Create(_dir.File("neu"), "Neues Projekt", "de-DE", ["pl-PL"]);
        neu.Attach(new ResourceRef { Kind = ResourceKind.Tm, Location = projectTm.Path });
        Assert.Contains(neu.TmsFor("pl-PL").SelectMany(t => t.Search("de-DE", "pl-PL", "Hallo")), m => m.Unit.Target == "Cześć");
    }

    [Fact]
    public void DatabasesOfTheSameKindCanBeMergedWithoutDuplicates()
    {
        var a = _dir.File("a.sctm");
        var b = _dir.File("b.sctm");
        using (var tmA = new TranslationMemory(a))
        {
            tmA.Add("de-DE", "pl-PL", "Gerät ausschalten.", "Wyłącz urządzenie.");
            tmA.Add("de-DE", "pl-PL", "Stecker ziehen.", "Wyjmij wtyczkę.");
        }
        using (var tmB = new TranslationMemory(b)) tmB.Add("de-DE", "pl-PL", "Gerät ausschalten.", "Wyłącz urządzenie.");
        Assert.Equal(1, DatabaseCatalog.Merge(a, b));
        Assert.Equal(0, DatabaseCatalog.Merge(a, b));                                 // zweimal: nichts doppelt
        using (var tmB = new TranslationMemory(b)) Assert.Equal(2, tmB.Count());

        var k1 = _dir.File("k1.sckb");
        var k2 = _dir.File("k2.sckb");
        using (var kb = new KnowledgeBase(k1)) kb.Add(new KnowledgeEntry { Title = "Anrede", Content = "Im Polnischen „Pan/Pani“.", Language = "pl-PL" });
        DatabaseCatalog.EnsureFile(ResourceKind.Knowledge, k2);
        Assert.Equal(1, DatabaseCatalog.Merge(k1, k2));
        Assert.Throws<ArgumentException>(() => DatabaseCatalog.Merge(a, k2));        // nur gleicher Typ
    }
}
