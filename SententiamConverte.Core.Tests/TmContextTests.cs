using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

public class TmContextTests
{
    [Fact]
    public void SameSentenceDifferentContextsKeepsBothTranslations()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        // „Klage“: im Zivilrecht „action“, im Kontext einer Beschwerde „complaint“
        tm.Add("de-DE", "en-GB", "Die Klage ist zulässig.", "The action is admissible.", previous: "Zivilprozess vor dem Landgericht.");
        tm.Add("de-DE", "en-GB", "Die Klage ist zulässig.", "The complaint is admissible.", previous: "Beschwerde bei der Aufsichtsbehörde.");
        Assert.Equal(2, tm.Count());

        var civil = tm.Search("de-DE", "en-GB", "Die Klage ist zulässig.", context: new TmContext("Zivilprozess vor dem Landgericht.", null, null));
        Assert.Equal(101, civil[0].Score);
        Assert.Equal(TmContextMatch.Previous, civil[0].ContextMatch);
        Assert.Equal("The action is admissible.", civil[0].Unit.Target);
        Assert.True(civil[1].Ambiguous);
        Assert.Equal(99, civil[1].Score);                                // andere Übersetzung, anderer Kontext: 1 % Abzug

        var unknown = tm.Search("de-DE", "en-GB", "Die Klage ist zulässig.", context: new TmContext("Etwas ganz anderes.", null, null));
        Assert.All(unknown, m => Assert.Equal(99, m.Score));            // mehrdeutig – nicht blind einsetzen
    }

    [Fact]
    public void CorrectionInSameContextReplacesTranslation()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Vertrag kündigen.", "Cancel contract.", previous: "");
        tm.Add("de-DE", "en-GB", "Vertrag kündigen.", "Terminate the contract.", previous: "");
        var unit = Assert.Single(tm.All());
        Assert.Equal("Terminate the contract.", unit.Target);

        // gleiche Übersetzung in neuem Kontext: keine Dublette, nur weiterer Kontext
        tm.Add("de-DE", "en-GB", "Vertrag kündigen.", "Terminate the contract.", previous: "Fristen beachten.");
        Assert.Single(tm.All());
        Assert.Equal(2, tm.ContextsOf(unit.Id).Count);
        Assert.Equal(101, tm.Search("de-DE", "en-GB", "Vertrag kündigen.", context: new TmContext("Fristen beachten.", null, null))[0].Score);
    }

    [Fact]
    public void WithoutContextBehavesAsBefore()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Hallo.", "Hi.");
        tm.Add("de-DE", "en-GB", "Hallo.", "Hello.");
        Assert.Equal("Hello.", Assert.Single(tm.All()).Target);
        Assert.Equal(100, tm.Search("de-DE", "en-GB", "Hallo.", context: new TmContext("x", null, null))[0].Score);
    }

    [Fact]
    public void SoftwareIdsCountAsContext()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("en-GB", "de-DE", "Open", "Öffnen", context: "Menu.File.Open", previous: "x");
        tm.Add("en-GB", "de-DE", "Open", "Geöffnet", context: "Status.Door", previous: "y");
        var m = tm.Search("en-GB", "de-DE", "Open", context: new TmContext("anders", null, "Status.Door", StructureIsId: true))[0];
        Assert.Equal(101, m.Score);
        Assert.Equal(TmContextMatch.Id, m.ContextMatch);
        Assert.Equal("Geöffnet", m.Unit.Target);
    }

    [Fact]
    public void ContextSurvivesTmx()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Die Klage ist zulässig.", "The action is admissible.", context: "Urteil", previous: "Zivilprozess.", next: "Gründe:");
        var ms = new MemoryStream();
        TmxIo.Export(tm, ms);
        ms.Position = 0;
        using var tm2 = new TranslationMemory(dir.File("b.sctm"));
        TmxIo.Import(ms, tm2);
        var hit = tm2.Search("de-DE", "en-GB", "Die Klage ist zulässig.", context: new TmContext("Zivilprozess.", "Gründe:", "Urteil"))[0];
        Assert.Equal(101, hit.Score);
        Assert.Equal(TmContextMatch.PreviousAndNext, hit.ContextMatch);
    }

    [Fact]
    public void ProjectConfirmsAndPretranslatesWithContext()
    {
        using var dir = new TempDir();
        using var project = TranslationProject.Create(dir.File("p"), "Recht", "de-DE", ["en-GB"]);
        var text = "Zivilprozess.\nDie Klage ist zulässig.\nBeschwerde.\nDie Klage ist zulässig.\n"u8.ToArray();
        var file = project.AddFile(new MemoryStream(text), "a.txt");
        var doc = project.LoadDocument(file, "en-GB");
        var segs = doc.Segments.ToList();
        segs[1].Target = "The action is admissible.";
        Workflow.Confirm(project, doc, segs[1]);
        segs[3].Target = "The complaint is admissible.";
        Workflow.Confirm(project, doc, segs[3]);

        // Neues Dokument mit denselben Zusammenhängen: beide werden kontextrichtig vorübersetzt und bestätigt
        var file2 = project.AddFile(new MemoryStream(text), "b.txt");
        var doc2 = project.LoadDocument(file2, "en-GB");
        var (applied, confirmed) = Workflow.Pretranslate(project, doc2);
        var s2 = doc2.Segments.ToList();
        Assert.Equal("The action is admissible.", s2[1].Target);
        Assert.Equal("The complaint is admissible.", s2[3].Target);
        Assert.Equal(101, s2[1].MatchPercent);
        Assert.Equal(2, confirmed);

        var analysis = Analyzer.Analyze([project.LoadDocument(file2, "en-GB")], project.TmsFor("en-GB"));
        Assert.True(analysis.Bands[MatchBand.PerfectMatch].Segments >= 1);
    }
}
