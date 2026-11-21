using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tests;

/// <summary>KI-Übersetzungen mit erfundenen oder verformten Tags (so beobachtet bei kleinen lokalen Modellen).</summary>
public class TagRepairTests
{
    [Fact]
    public void InventedTagsAreRemovedWithTheirSpaces()
    {
        Assert.Equal("Development of a system for material tracking from design to production.",
            TagText.Repair("Entwicklung eines Systems zur Materialverfolgung von der Konstruktion bis in die Produktion.",
                "Development ⟨1⟩ of a system for material tracking ⟨/1⟩ from design to production ⟨2/⟩."));
        Assert.Equal("Coordination of complex construction projects: clarifying requirements.",
            TagText.Repair("Koordination komplexer Bauprojekte: Anforderungen klären.",
                "⟨1⟩ Coordination of complex construction projects ⟨/1⟩: clarifying requirements."));
    }

    [Fact]
    public void DeformedTagsAreRepairedAndExtraOnesDropped()
    {
        Assert.Equal("⟨1⟩Ochotnicza straż pożarna⟨/1⟩ Stimpfach; ⟨2/⟩Szkolenie pierwszej pomocy",
            TagText.Repair("⟨1⟩Freiwillige Feuerwehr⟨/1⟩ Stimpfach; ⟨2/⟩Erste-Hilfe-Schulung",
                "⟨1⟩Ochotnicza straż pożarna⟨/1> Stimpfach; ⟨2/⟩Szkolenie pierwszej pomocy⟨/2>"));
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", TagText.Repair("Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "Tighten the screw to ⟨ 1 ⟩5 Nm</1⟩."));
    }

    [Fact]
    public void StrayTagMarksAreRemoved()
    {
        // so beobachtet bei kostenlosen Online-Modellen im Verbund
        Assert.Equal("Do not use abrasive agents.", TagText.Repair("Verwenden Sie keine Scheuermittel.", "Do not use abrasive⟩/1⟩ agents."));
        Assert.Equal("Maintenance by authorised personnel only.", TagText.Repair("Wartung nur durch Fachpersonal.", "Maintenance by authorised⟩ personnel only."));
        Assert.Equal("Use ⟨1⟩only⟨/1⟩ water.", TagText.Repair("Nur ⟨1⟩Wasser⟨/1⟩ verwenden.", "Use ⟨1⟩only⟨/1⟩ ⟩ water."));
    }

    [Fact]
    public async Task InAGroupAnInflatedTranslationIsRedoneByAnotherModel()
    {
        var answers = new Dictionary<string, string>
        {
            ["a"] = "Put the cover back on and ensure it is properly secured with the provided fixtures.",
            ["b"] = "Put the cover back on.",
        };
        var config = new SententiamConverte.Core.Ai.AiProviderConfig
        {
            Name = "Verbund", Kind = SententiamConverte.Core.Ai.AiProviderKind.Pool,
            Members = new[] { "a", "b" }.Select(m => new SententiamConverte.Core.Ai.AiProviderConfig { Name = m, Model = m, BaseUrl = "http://x/v1" }).ToList(),
        };
        var pool = new SententiamConverte.Core.Ai.PooledChatModel(config, c => new Fixed(answers[c.Model]));
        var translator = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = pool });
        Assert.Equal("Put the cover back on.", await translator.TranslateAsync("Setzen Sie die Abdeckung wieder ein.", "de-DE", "en-GB", null,
            ct: TestContext.Current.CancellationToken));
        Assert.False(SententiamConverte.Core.Agents.AiTranslator.TooLong("Wartung nur durch Fachpersonal.", "Maintenance by authorised personnel only."));
        Assert.True(SententiamConverte.Core.Agents.AiTranslator.TooLong("Setzen Sie die Abdeckung wieder ein.", answers["a"]));
        // so beobachtet: angehängter Satz bzw. angehängter Nebensatz
        Assert.True(SententiamConverte.Core.Agents.AiTranslator.Inflated("Setzen Sie die Abdeckung wieder ein.",
            "Put the cover back on. \nEnsure that the cover is securely in place."));
        Assert.True(SententiamConverte.Core.Agents.AiTranslator.Inflated("Die Abdeckung mit einem feuchten Tuch abwischen.",
            "Wipe the cover with a damp cloth, making sure to clean it thoroughly and then dry it completely."));
        Assert.False(SententiamConverte.Core.Agents.AiTranslator.Inflated("Bewahren Sie diese Anleitung auf.", "Keep this manual in a safe place."));
        Assert.False(SententiamConverte.Core.Agents.AiTranslator.Inflated("Keep this manual.", "Bewahren Sie diese Anleitung auf."));
    }

    private sealed class Fixed(string answer) : SententiamConverte.Core.Ai.IChatModel
    {
        public string DisplayName => answer;
        public Task<string> CompleteAsync(string system, IReadOnlyList<SententiamConverte.Core.Ai.ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult(answer);
    }

    [Fact]
    public void CorrectTagsAndOrdinaryTextStayUntouched()
    {
        Assert.Equal("Tighten ⟨1⟩5 Nm⟨/1⟩ now.", TagText.Repair("Mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "Tighten ⟨1⟩5 Nm⟨/1⟩ now."));
        Assert.Equal("If 1 < 2 and 3 > 2", TagText.Repair("Wenn 1 < 2 und 3 > 2", "If 1 < 2 and 3 > 2"));
        Assert.Equal("Use <b>bold</b>.", TagText.Repair("<b>Fett</b> verwenden.", "Use <b>bold</b>."));
        // fehlende Tags werden wie bisher ergänzt
        Assert.Equal("⟨1⟩Tighten the screw.⟨/1⟩", TagText.Repair("⟨1⟩Schraube anziehen.⟨/1⟩", "Tighten the screw."));
    }
}
