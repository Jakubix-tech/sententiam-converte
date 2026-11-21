using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Fuzzy-Treffer gezielt anpassen: nur die geänderte Stelle, Warnung bei zu viel Änderung.</summary>
public sealed class FuzzyRepairTests
{
    private sealed class Editor(string answer) : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(answer);
        }
    }

    private static TmMatch Match(string source, string target, int score) =>
        new(new TmUnit(1, "en-GB", "de-DE", source, target, DateTime.UtcNow, DateTime.UtcNow, 1, "Kunde", null), score, "Projekt-TM");

    [Fact]
    public async Task OnlyTheChangedWordsChange()
    {
        var model = new Editor("Vor der Reinigung die Pumpe ausschalten.");
        var match = Match("Switch off the pump before maintenance.", "Vor der Wartung die Pumpe ausschalten.", 85);
        var result = await new FuzzyRepair(new AgentContext { Model = model }).RepairAsync("Switch off the pump before cleaning.", "en-GB", "de-DE", match,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Vor der Reinigung die Pumpe ausschalten.", result.Target);
        Assert.False(result.ChangedTooMuch);
        Assert.Contains("Old source: Switch off the pump before maintenance.", model.Prompt);
        Assert.Contains("Approved translation: Vor der Wartung die Pumpe ausschalten.", model.Prompt);
        Assert.Contains("New source: Switch off the pump before cleaning.", model.Prompt);
        Assert.Contains("removed \"maintenance.\"; added \"cleaning.\"", model.Prompt);
    }

    [Fact]
    public async Task RewritingEverythingIsFlagged()
    {
        var model = new Editor("Bevor Sie reinigen, schalten Sie bitte unbedingt zuerst die gesamte Pumpenanlage komplett aus.");
        var match = Match("Switch off the pump before maintenance.", "Vor der Wartung die Pumpe ausschalten.", 85);
        var result = await new FuzzyRepair(new AgentContext { Model = model }).RepairAsync("Switch off the pump before cleaning.", "en-GB", "de-DE", match,
            ct: TestContext.Current.CancellationToken);
        Assert.True(result.ChangedTooMuch);
    }

    [Fact]
    public async Task TagsOfTheNewSourceAreKept()
    {
        var model = new Editor("Vor der Reinigung die ⟨1⟩Pumpe⟨/1⟩ ausschalten.");
        var match = Match("Switch off the pump before maintenance.", "Vor der Wartung die Pumpe ausschalten.", 80);
        var result = await new FuzzyRepair(new AgentContext { Model = model }).RepairAsync("Switch off the ⟨1⟩pump⟨/1⟩ before cleaning.", "en-GB", "de-DE", match,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("⟨1⟩Pumpe⟨/1⟩", result.Target);
        Assert.Equal("only formatting or punctuation", FuzzyRepair.Differences("Switch ⟨1⟩off⟨/1⟩.", "Switch off."));
    }
}
