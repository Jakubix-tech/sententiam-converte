using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Datenbanken an Sprachen statt an ein Projekt binden; Agenten mit eigenen Verbindungen, die einen Projektwechsel
/// überstehen; Beleg-Prüfung der Recherche.
/// </summary>
[Collection("BuiltInAi")]
public sealed class LanguageResourcesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    public LanguageResourcesTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _dir.File("home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public void LanguageBoundResourcesApplyToEveryProjectWithThoseLanguages()
    {
        var pl = TranslationProject.Create(_dir.File("p1"), "Polnisch 1", "de-DE", ["pl-PL"]);
        var kbPath = _dir.File("de-pl-wissen.sckb");
        new KnowledgeBase(kbPath).Dispose();                                                          // wie „Neue Datenbank anlegen“
        pl.AttachForLanguages(new ResourceRef { Kind = ResourceKind.Knowledge, Location = kbPath, WriteBack = true });
        Assert.Equal(["de-DE", "pl-PL"], LanguageResources.Load().Single().Languages);               // ohne Angabe: Sprachen des Projekts
        Assert.Contains(pl.KnowledgeBases, k => k.Location == kbPath);
        Assert.Empty(pl.Resources);                                                                   // nicht in der Projektdatei
        pl.Dispose();

        var pl2 = TranslationProject.Create(_dir.File("p2"), "Polnisch 2", "de-DE", ["pl-PL", "en-GB"]);
        Assert.Contains(pl2.KnowledgeBases, k => k.Location == kbPath);                               // gilt auch hier
        Assert.Single(pl2.LanguageRefs);
        var fr = TranslationProject.Create(_dir.File("p3"), "Französisch", "en-GB", ["fr-FR"]);
        Assert.DoesNotContain(fr.KnowledgeBases, k => k.Location == kbPath);                          // andere Sprachen: nicht
        fr.Dispose();
        var en = TranslationProject.Create(_dir.File("p4"), "Englisch", "de-DE", ["en-GB"]);
        Assert.DoesNotContain(en.KnowledgeBases, k => k.Location == kbPath);                          // nur die Ausgangssprache passt: nicht
        en.Dispose();

        pl2.Detach(pl2.LanguageRefs.Single());                                                        // lösen = für alle Projekte lösen
        Assert.Empty(LanguageResources.Load());
        pl2.Dispose();
    }

    [Fact]
    public void AgentsKeepTheirOwnConnectionsWhenTheProjectCloses()
    {
        var p = TranslationProject.Create(_dir.File("p"), "Projekt", "de-DE", ["pl-PL"]);
        using var res = p.OpenForAgent();
        var memory = new KnowledgeMemory(res.Knowledge[0], res.Knowledge, res.Termbases, res.Tms, "de-DE", ["pl-PL"]);
        p.Dispose();                                                                                  // anderes Projekt geöffnet

        memory.Remember("Abkantpresse", "prasa krawędziowa", "pl-PL", "https://pl.wikipedia.org/wiki/Prasa_krawędziowa");
        Assert.Contains("prasa krawędziowa", memory.Search("Abkantpresse", "de-DE"));
        using var reopened = TranslationProject.Open(_dir.File("p"));
        Assert.Equal(1, reopened.ProjectKnowledge.Count());                                           // Ergebnis im richtigen Projekt
    }

    [Fact]
    public void WithoutAProjectAgentsUseTheGeneralMemoryAndTheLanguageDatabases()
    {
        var kbPath = _dir.File("de-pl.sckb");
        new KnowledgeBase(kbPath).Dispose();
        new KnowledgeBase(_dir.File("en-fr.sckb")).Dispose();
        LanguageResources.Add(new ResourceRef { Kind = ResourceKind.Knowledge, Location = kbPath, Languages = ["de-DE", "pl-PL"] });
        LanguageResources.Add(new ResourceRef { Kind = ResourceKind.Knowledge, Location = _dir.File("en-fr.sckb"), Languages = ["en-GB", "fr-FR"] });
        using var res = AgentResources.ForLanguages("de-DE", ["pl-PL"], new ServerSettings());
        Assert.Equal(AgentResources.GlobalMemoryPath, res.Knowledge[0].Location);
        Assert.Equal(2, res.Knowledge.Count);
        Assert.Contains(res.Knowledge, k => k.Location == kbPath);
    }

    [Fact]
    public void TheProgramChecksWhetherTheProposedTermIsInASourceThatWasRead()
    {
        var steps = new List<ResearchStep>
        {
            new(1, "", "search", """{"query":"Abkantpresse","language":"de-DE","source":"wikipedia"}""",
                "1. Gesenkbiegepresse – https://de.wikipedia.org/wiki/Gesenkbiegepresse\n   In other languages: pl: Prasa krawędziowa"),
            new(2, "", "law", """{"jurisdiction":"pl","query":"x"}""", "Nothing found. Main acts: Kodeks cywilny"),
        };
        var found = Assert.Single(ResearchAgent.CheckEvidence("…", "prasa krawędziowa (krawędziarka)", steps));
        Assert.True(found.Found);
        Assert.Contains("Wikipedia", found.Source);
        Assert.StartsWith("✔", found.ToString());

        var missing = Assert.Single(ResearchAgent.CheckEvidence("…", "giętarka krawędziowa", steps));
        Assert.False(missing.Found);
        Assert.StartsWith("⚠", missing.ToString());
        Assert.False(ResearchAgent.CheckEvidence("…", "Kodeks cywilny", steps).Single().Found);        // Programmhinweise sind kein Beleg
        Assert.Equal(["Prasa krawędziowa"], ResearchAgent.CheckEvidence("Auf Polnisch heißt das „Prasa krawędziowa“.", null, steps).Select(e => e.Term));
    }
}
