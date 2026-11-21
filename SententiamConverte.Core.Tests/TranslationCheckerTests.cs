using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Übersetzung prüfen: Rückübersetzung und Abgleich mehrerer Modelle.</summary>
public class TranslationCheckerTests
{
    /// <summary>Antwortet je nach Art der Anfrage – beide Methoden laufen gleichzeitig, die Reihenfolge ist offen.</summary>
    private sealed class ByPrompt(string backTranslation, string backJudge, string alternative, string consensusJudge) : IChatModel
    {
        public readonly List<string> Prompts = new();
        public string DisplayName => "Prüfmodell";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            lock (Prompts) Prompts.Add(messages[^1].Text);
            return Task.FromResult(system switch
            {
                _ when system.Contains("as literally as possible") => backTranslation,
                _ when system.Contains("compare meanings") => backJudge,
                _ when system.Contains("professional translator") => alternative,
                _ => consensusJudge,
            });
        }
    }

    private static AgentContext Context(IChatModel model) => new() { Model = model, ExplanationLanguage = "Deutsch" };

    [Fact]
    public async Task AFaithfulTranslationIsPlausible()
    {
        var model = new ByPrompt("Die Schraube mit 5 Nm anziehen.", """{"score": 0.95, "issues": []}""",
            "Tighten the screw with ⟨1⟩5 Nm⟨/1⟩.", """{"score": 0.9, "issues": []}""");
        var check = await new TranslationChecker(Context(model)).CheckAsync("Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.",
            "de-DE", "en-GB", "Montage", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0.9, check.Score);
        Assert.Equal("plausibel", check.Verdict);
        Assert.False(check.Doubtful);
        Assert.Equal(["Tighten the screw with ⟨1⟩5 Nm⟨/1⟩."], check.Alternatives);                    // zwei gleiche Vorschläge, einmal
        Assert.True(check.SingleModel);
        Assert.Contains(model.Prompts, p => p.Contains("Tighten the screw to 5 Nm.") && !p.Contains("Schraube"));   // Rückübersetzung ohne Original, ohne Tags
        Assert.Contains("Rückübersetzung: Die Schraube mit 5 Nm anziehen.", check.Format());
    }

    [Fact]
    public async Task AChangedFactIsFoundByBothMethods()
    {
        var model = new ByPrompt("Die Schraube mit 50 Nm anziehen.", """{"score": 0.3, "issues": ["Zahl geändert: 50 statt 5 Nm"]}""",
            "Tighten the screw to 5 Nm.", """{"score": 0.4, "issues": ["weicht beim Drehmoment von den anderen ab"]}""");
        var check = await new TranslationChecker(Context(model)).CheckAsync("Die Schraube mit 5 Nm anziehen.", "Tighten the screw to 50 Nm.",
            "de-DE", "en-GB", null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(0.3, check.Score);                                                         // der schlechtere Befund zählt
        Assert.True(check.Doubtful);
        Assert.Equal("wahrscheinlich fehlerhaft", check.Verdict);
        Assert.Contains(check.Findings, f => f.Method == TranslationChecker.BackTranslation && f.Issues.Contains("Zahl geändert: 50 statt 5 Nm"));
        Assert.Contains(check.Findings, f => f.Method == TranslationChecker.Consensus && f.Score == 0.4);
        Assert.Contains("(Nur ein Modell eingerichtet", check.Format());
    }

    [Fact]
    public async Task IdenticalIndependentTranslationsNeedNoJudgement()
    {
        var model = new ByPrompt("", "", "Pull out the plug.", """{"score": 0, "issues": ["sollte nicht gefragt werden"]}""");
        var (finding, alternatives) = await new TranslationChecker(Context(model)).ConsensusAsync("Stecker ziehen.", "Pull out the plug.",
            "de-DE", "en-GB", null, 2, TestContext.Current.CancellationToken);
        Assert.Equal(1, finding.Score);
        Assert.Single(alternatives);
        Assert.DoesNotContain(model.Prompts, p => p.Contains("Translation under review"));
    }
}
