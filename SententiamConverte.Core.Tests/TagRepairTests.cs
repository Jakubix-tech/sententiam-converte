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
    public void CorrectTagsAndOrdinaryTextStayUntouched()
    {
        Assert.Equal("Tighten ⟨1⟩5 Nm⟨/1⟩ now.", TagText.Repair("Mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "Tighten ⟨1⟩5 Nm⟨/1⟩ now."));
        Assert.Equal("If 1 < 2 and 3 > 2", TagText.Repair("Wenn 1 < 2 und 3 > 2", "If 1 < 2 and 3 > 2"));
        Assert.Equal("Use <b>bold</b>.", TagText.Repair("<b>Fett</b> verwenden.", "Use <b>bold</b>."));
        // fehlende Tags werden wie bisher ergänzt
        Assert.Equal("⟨1⟩Tighten the screw.⟨/1⟩", TagText.Repair("⟨1⟩Schraube anziehen.⟨/1⟩", "Tighten the screw."));
    }
}
