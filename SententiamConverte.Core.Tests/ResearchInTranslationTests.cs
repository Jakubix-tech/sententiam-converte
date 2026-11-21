using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

/// <summary>Was recherchiert und in der Terminologie abgelegt ist, bekommt die Übersetzung zu jedem Satz mit.</summary>
public sealed class ResearchInTranslationTests
{
    private sealed class Capture(string answer) : IChatModel
    {
        public readonly List<string> Prompts = new();
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task TranslationGetsTheResearchAndDefinitionsOfItsSentence()
    {
        using var dir = new TempDir();
        var project = TranslationProject.Create(dir.File("p"), "Kauf", "es-ES", ["es-MX"]);
        DossierStore.Add(project, new TermDossier
        {
            Expression = "Registro de la Propiedad", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Meaning = "Grundbuch in Spanien",
            Candidates = [new() { Term = "Registro Público de la Propiedad", Equivalence = Equivalence.Partial }, new() { Term = "Catastro", Equivalence = Equivalence.None }],
            Recommended = "Registro Público de la Propiedad", Strategy = "funktionale Entsprechung",
            Sources = [new SourceRef("es-MX", "Registro Público de la Propiedad", "https://ejemplo.mx/rpp") { Quote = "El Registro Público de la Propiedad inscribe los inmuebles." }],
        });
        // Empfehlung in keiner Quelle der Zielsprache belegt: bleibt draußen
        DossierStore.Add(project, new TermDossier
        {
            Expression = "finca", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Meaning = "Grundstück", Recommended = "predio",
            Sources = [new SourceRef("es-MX", "Finca", "https://ejemplo.mx/finca") { Quote = "La finca es rústica." }],
        });
        project.ProjectTermbase.AddConcept([("es-ES", "escritura", TermStatus.Preferred), ("es-MX", "escritura pública", TermStatus.Preferred)],
            "Notariell beurkundeter Vertrag, mit dem das Eigentum übergeht", "Recht", null);

        var doc = new BilingualDocument { FileName = "a.txt", SourceLanguage = "es-ES", TargetLanguage = "es-MX" };
        var paragraph = new Paragraph { Locator = "p0" };
        new Segmenter("es-ES").Segment(paragraph, "La finca y la escritura están en el Registro de la Propiedad.");
        doc.Paragraphs.Add(paragraph);
        var segment = doc.Segments.Single();

        var research = Workflow.Research(project, doc, segment);
        Assert.Equal(["Registro de la Propiedad"], research.Select(d => d.Expression));

        var model = new Capture("La finca y la escritura pública están en el Registro Público de la Propiedad.");
        await new AiTranslator(new AgentContext { Model = model }).TranslateAsync(segment.Source, "es-ES", "es-MX", null,
            terms: Workflow.Terms(project, doc, segment), research: research, ct: TestContext.Current.CancellationToken);
        var prompt = model.Prompts.Single();
        Assert.Contains("Researched expressions in this sentence", prompt);
        Assert.Contains("- Registro de la Propiedad → Registro Público de la Propiedad; meaning: Grundbuch in Spanien; strategy: funktionale Entsprechung; avoid: Catastro", prompt);
        Assert.DoesNotContain("- finca →", prompt);
        Assert.Contains("- escritura → escritura / escritura pública (meaning: Notariell beurkundeter Vertrag, mit dem das Eigentum übergeht)", prompt);

        // ein anderes Zielland bekommt die Dossiers nicht
        var other = new BilingualDocument { FileName = "a.txt", SourceLanguage = "es-ES", TargetLanguage = "de-DE" };
        other.Paragraphs.Add(paragraph);
        Assert.Empty(Workflow.Research(project, other, segment));
        project.Dispose();
    }

    [Fact]
    public void LongTextsAreShortenedAtAWordBoundary()
    {
        Assert.Null(AiTranslator.Short("  "));
        Assert.Equal("eine Zeile", AiTranslator.Short("eine\n  Zeile"));
        var shortened = AiTranslator.Short(string.Join(" ", Enumerable.Repeat("Wort", 60)), 40)!;
        Assert.EndsWith("Wort …", shortened);
        Assert.True(shortened.Length <= 42);
    }
}
