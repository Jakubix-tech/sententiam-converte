using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;

namespace SententiamConverte.Core.Tests;

/// <summary>Alignment von Hand korrigieren, Formatierungs-Tags angleichen, mit KI prüfen und neu ausrichten.</summary>
public class AlignmentEditorTests
{
    private static readonly string[] De = ["Schalten Sie das Gerät aus.", "Ziehen Sie den Netzstecker.", "Warten Sie 5 Minuten.", "Öffnen Sie die Abdeckung."];
    private static readonly string[] En = ["Switch off the device.", "Pull out the mains plug.", "Wait 5 minutes.", "Open the cover."];

    [Fact]
    public void PairsCanBeMergedSplitMovedResegmentedAndConfirmed()
    {
        var s = new AlignmentSession("de-DE", "en-GB", De, En);
        Assert.Equal(4, s.Pairs.Count);

        s.MergeWithNext(0);
        Assert.Equal(3, s.Pairs.Count);
        Assert.Equal(2, s.Pairs[0].Source.Count);
        s.Split(0);
        Assert.Equal(4, s.Pairs.Count);
        Assert.Equal(["Pull out the mains plug."], s.Pairs[1].Target);

        s.MoveDown(0, source: false);                                   // Zielsatz rutscht ins nächste Paar
        Assert.Empty(s.Pairs[0].Target);
        Assert.Equal(2, s.Pairs[1].Target.Count);
        s.MoveUp(1, source: false);                                     // und zurück
        Assert.Equal(["Switch off the device."], s.Pairs[0].Target);

        s.SetText(2, source: true, "Warten Sie\n5 Minuten.");          // neu segmentieren: ein Umbruch teilt den Satz
        Assert.Equal(["Warten Sie", "5 Minuten."], s.Pairs[2].Source);
        s.SetText(2, source: true, "Warten Sie 5 Minuten.");

        s.Confirm(3);
        Assert.True(s.Pairs[3].Confirmed && s.Pairs[3].Accepted);
        s.Pairs[0].Target.Clear();                                      // etwas durcheinanderbringen …
        s.Pairs[1].Target.Insert(0, "Switch off the device.");
        s.Realign();                                                    // … neu ausrichten: der Anker bleibt
        Assert.Contains(s.Pairs, p => p.Confirmed && p.SourceText == "Öffnen Sie die Abdeckung." && p.TargetText == "Open the cover.");
        Assert.Equal(4, s.Accepted.Count());
    }

    [Fact]
    public void FormattingTagsOfTheTranslationAreMatchedToTheSource()
    {
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.", AlignmentSession.MatchTags("Mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "Tighten the screw to ⟨3⟩5 Nm⟨/3⟩."));
        Assert.Equal("Use ⟨1⟩only⟨/1⟩ ⟨2⟩water⟨/2⟩.", AlignmentSession.MatchTags("Nur ⟨1⟩Wasser⟨/1⟩ ⟨2⟩verwenden⟨/2⟩.", "Use ⟨7⟩only⟨/7⟩ ⟨9⟩water⟨/9⟩."));
        Assert.Equal("Open the cover.", AlignmentSession.MatchTags("Abdeckung öffnen.", "Open the ⟨4⟩cover⟨/4⟩."));   // Quelle ohne Tags
    }

    private sealed class Scripted(params string[] replies) : IChatModel
    {
        private int _turn;
        public string DisplayName => "Drehbuch";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult(replies[Math.Min(_turn++, replies.Length - 1)]);
    }

    [Fact]
    public async Task TheAiChecksPairsAndRealignsUncertainSections()
    {
        var ct = TestContext.Current.CancellationToken;
        // verschobene Übersetzung: zwei Ausgangssätze teilen sich eine Übersetzung
        var s = new AlignmentSession("de-DE", "en-GB",
            ["Schalten Sie das Gerät aus.", "Ziehen Sie den Netzstecker.", "Warten Sie 5 Minuten."],
            ["Switch off the device and pull out the mains plug.", "Wait 5 minutes."]);
        var ai = new AiAligner(new Scripted(
            """{"items": [{"i": 1, "score": 0.3, "note": "Teil fehlt"}, {"i": 2, "score": 1}]}""",
            """{"pairs": [{"s": [1, 2], "t": [1]}, {"s": [3], "t": [2]}]}"""));
        foreach (var p in s.Pairs) p.Confidence = 0.9;                   // die Länge allein hielt alles für sicher
        await ai.CheckAsync(s, ct);
        Assert.Contains(s.Pairs, p => p.AiScore == 0.3 && !p.Accepted && p.AiNote == "Teil fehlt");
        Assert.NotEmpty(s.UncertainRuns());

        Assert.Equal(1, await ai.FixUncertainAsync(s, ct));
        var first = s.Pairs[0];
        Assert.Equal(2, first.Source.Count);
        Assert.Equal("Switch off the device and pull out the mains plug.", first.TargetText);

        // unstimmige Antwort (Satz doppelt): bisherige Zuordnung bleibt
        var bad = new AiAligner(new Scripted("""{"pairs": [{"s": [1], "t": [1]}, {"s": [1], "t": [2]}]}"""));
        Assert.Null(await bad.AlignAsync(["a", "b"], ["x", "y"], "de-DE", "en-GB", ct));
    }
}
