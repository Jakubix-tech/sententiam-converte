using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

/// <summary>Recherche zum Dokument: Ausdrücke finden, Schwierigkeit, Dossiers speichern und zum Text wiederfinden.</summary>
public sealed class DocumentResearchTests
{
    private sealed class Finder(string answer) : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(answer);
        }
    }

    private static BilingualDocument Doc(params string[] lines)
    {
        var doc = new BilingualDocument { FileName = "a.txt", SourceLanguage = "es-ES", TargetLanguage = "es-MX" };
        var seg = new Segmenter("es-ES");
        foreach (var line in lines)
        {
            var p = new Paragraph { Locator = "p" + doc.Paragraphs.Count };
            seg.Segment(p, line);
            doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    [Fact]
    public async Task ExpressionsAreFoundWithContextAndDifficulty()
    {
        var model = new Finder("""
            {"items": [
              {"expression": "Registro de la Propiedad", "category": "legal", "reason": "Institution des spanischen Rechts", "difficulty": "hard", "difficulty_reason": "in Mexiko anders organisiert"},
              {"expression": "zumo", "category": "realia", "reason": "regional verschieden", "difficulty": "easy", "difficulty_reason": "in Mexiko jugo"},
              {"expression": "steht nicht im Text", "category": "term", "reason": "erfunden", "difficulty": "easy"}
            ]}
            """);
        var doc = Doc("La finca está inscrita en el Registro de la Propiedad.", "Se sirve zumo de naranja.", "El Registro de la Propiedad confirma la carga.");
        var items = await new DocumentResearch(new AgentContext { Model = model }).FindAsync(doc, "es-ES", "es-MX", null, "Immobilienrecht",
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Registro de la Propiedad", "zumo"], items.Select(i => i.Expression));      // nur, was im Dokument steht
        var registro = items[0];
        Assert.Equal((ResearchDifficulty.Hard, "legal"), (registro.Difficulty, registro.Category));
        Assert.Equal([1, 3], registro.SegmentIds);
        Assert.Equal("La finca está inscrita en el Registro de la Propiedad.", registro.Context);
        Assert.Contains("Spanish as used in Spain (es-ES)", model.Prompt);                       // Sprache mit Land
        Assert.Contains("as used in Mexico (es-MX)", model.Prompt);
    }

    private sealed class Counting(Func<string, string> answer) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            lock (Prompts) Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer(messages[^1].Text));
        }
    }

    [Fact]
    public async Task TheWholeDocumentIsReadInPartsAndRulesFindTheRest()
    {
        // ein langes Dokument: früher sah die KI nur Anfang, Mitte und Ende (12 000 Zeichen) und nannte höchstens 15 Ausdrücke
        var filler = Enumerable.Range(1, 60).Select(i => $"Absatz {i}: Die Pumpe läuft im Normalbetrieb ohne besondere Vorkommnisse weiter.").ToList();
        var lines = new List<string> { "Die Gleitringdichtung (GLRD) ist zu prüfen." };
        lines.AddRange(filler.Take(30));
        lines.Add("Nach § 242 StGB und der Verordnung (EU) 2016/679 gilt Folgendes.");
        lines.AddRange(filler.Skip(30));
        lines.Add("Am Ende steht die Kavitation im Laufrad.");
        var doc = Doc([.. lines]);
        var model = new Counting(p => p.Contains("Kavitation")
            ? """{"items": [{"expression": "Kavitation", "category": "term", "reason": "Fachbegriff", "difficulty": "easy"}, {"expression": "Gleitringdichtung", "category": "term", "reason": "x", "difficulty": "hard"}]}"""
            : """{"items": [{"expression": "Gleitringdichtung", "category": "term", "reason": "Fachbegriff", "difficulty": "medium"}]}""");

        var items = await new DocumentResearch(new AgentContext { Model = model }).FindAsync(doc, "de-DE", "en-GB", null, null,
            ct: TestContext.Current.CancellationToken);

        Assert.True(model.Prompts.Count >= 2, "in Abschnitten gelesen");
        Assert.All(model.Prompts, p => Assert.Contains("List ALL expressions", p));
        Assert.Contains(model.Prompts, p => p.Contains("Am Ende steht die Kavitation"));          // auch das Ende des Dokuments
        Assert.Contains(items, i => i.Expression == "Kavitation");
        Assert.Equal(ResearchDifficulty.Hard, items.Single(i => i.Expression == "Gleitringdichtung").Difficulty);   // höchste Einschätzung zählt
        // ohne KI erkannt: Abkürzung, Paragraph, EU-Rechtsakt
        Assert.Equal("abbreviation", items.Single(i => i.Expression == "GLRD").Category);
        Assert.Equal("legal", items.Single(i => i.Expression == "§ 242 StGB").Category);
        Assert.Equal("legal", items.Single(i => i.Expression == "Verordnung (EU) 2016/679").Category);
        Assert.DoesNotContain(items, i => i.Expression is "EU" or "II");
    }

    [Fact]
    public void WhenTheAiRunsOutOfIdeasSimpleQueriesFollow()
    {
        var q = new TermQuery("Gleitringdichtung", "de-DE", "en-GB", Topic: "Pumpen");
        var queries = TermComparisonAgent.FallbackQueries(q, "Englisch", ["mechanical seal"], ["\"mechanical seal\" Pumpen"]);
        Assert.Equal(["\"mechanical seal\"", "\"mechanical seal\" glossary"], queries["en-GB"]);       // schon versuchte fehlen
        Assert.Equal(["\"Gleitringdichtung\" Englisch", "\"Gleitringdichtung\" Pumpen"], queries["de-DE"]);
    }

    [Fact]
    public void DifficultyFollowsTheEquivalenceFound()
    {
        var item = new ResearchItem { Expression = "x", Difficulty = ResearchDifficulty.Easy };
        TermDossier With(params Equivalence[] e) => new() { Candidates = e.Select(x => new DossierCandidate { Term = "t", Equivalence = x }).ToList() };
        Assert.Equal(ResearchDifficulty.Hard, DocumentResearch.Assess(item, With(Equivalence.None), 3).Item1);
        Assert.Contains("Keine Entsprechung", DocumentResearch.Assess(item, With(), 0).Item2);
        Assert.Equal(ResearchDifficulty.Medium, DocumentResearch.Assess(item, With(Equivalence.Partial, Equivalence.None), 3).Item1);
        Assert.Equal(ResearchDifficulty.Easy, DocumentResearch.Assess(item, With(Equivalence.Full), 3).Item1);
        Assert.Equal(ResearchDifficulty.Medium, DocumentResearch.Assess(item, With(Equivalence.Full), 0).Item1);   // kaum Belege
    }

    [Fact]
    public void DossiersAreStoredFoundAgainAndTaken()
    {
        using var dir = new TempDir();
        var project = TranslationProject.Create(dir.File("p"), "Kauf", "es-ES", ["es-MX"]);
        var dossier = new TermDossier
        {
            Expression = "Registro de la Propiedad", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Meaning = "Grundbuch in Spanien",
            Candidates = [new() { Term = "Registro Público de la Propiedad", Equivalence = Equivalence.Partial }, new() { Term = "Catastro", Equivalence = Equivalence.None }],
            Recommended = "Registro Público de la Propiedad", Strategy = "funktionale Entsprechung", Reasoning = "So heißt die Stelle in Mexiko.",
            Difficulty = ResearchDifficulty.Medium,
        };
        DossierStore.Add(project, dossier);
        DossierStore.Add(project, dossier);                                                       // ersetzt, verdoppelt nicht
        var all = DossierStore.Load(project);
        Assert.Single(all);

        // Klick auf einen Satz, ein Wort oder eine Markierung
        Assert.Single(DossierStore.ForText(all, "La finca está inscrita en el ⟨1⟩Registro de la Propiedad⟨/1⟩.", "es-MX"));
        Assert.Single(DossierStore.ForText(all, "Registro", "es-MX"));
        Assert.Empty(DossierStore.ForText(all, "La finca", "es-MX"));
        Assert.Empty(DossierStore.ForText(all, "Registro de la Propiedad", "de-DE"));

        // in die Termbank: Empfehlung bevorzugt, Variante ohne Entsprechung „nicht verwenden“
        DossierStore.ToTermbase(all[0], project.ProjectTermbase, "Recht");
        var concept = Assert.Single(project.ProjectTermbase.AllConcepts());
        Assert.Contains(concept.Terms, t => t is { Text: "Registro Público de la Propiedad", Status: TermStatus.Preferred });
        Assert.Contains(concept.Terms, t => t is { Text: "Catastro", Status: TermStatus.Forbidden });
        Assert.Contains("Grundbuch", concept.Definition);

        DossierStore.Remove(project, all[0]);
        Assert.Empty(DossierStore.Load(project));
        project.Dispose();
    }
}
