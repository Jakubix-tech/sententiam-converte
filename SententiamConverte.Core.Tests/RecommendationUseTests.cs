using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Rückmeldung auch bei umformulierten Sätzen: gebeugt und umgestellt sofort erkennen, sonst ordnet die KI zu – aber nur,
/// was sich am Text bestätigen lässt; eine eigene Benennung zählt als ersetzt, eine Umschreibung nicht.
/// </summary>
public sealed class RecommendationUseTests
{
    private static TermDossier Dossier(string recommended, params string[] others) => new()
    {
        Expression = "GLRD", TargetLanguage = "en-GB", Recommended = recommended,
        Candidates = [new() { Term = recommended }, .. others.Select(o => new DossierCandidate { Term = o })],
    };

    [Fact]
    public void InflectedAndReorderedTermsAreRecognised()
    {
        Assert.True(RecommendationUse.Appears("Wymieniono uszczelnienia mechanicznego nie trzeba.", "uszczelnienie mechaniczne"));
        Assert.True(RecommendationUse.Appears("Mechaniczne uszczelnienie jest zużyte.", "uszczelnienie mechaniczne"));   // umgestellt
        Assert.True(RecommendationUse.Appears("Beide Laufräder sind verschlissen.", "Laufrad"));                        // Umlaut im Plural
        Assert.True(RecommendationUse.Appears("Both mechanical seals were replaced.", "mechanical seal"));
        Assert.False(RecommendationUse.Appears("Wymieniono uszczelkę.", "uszczelnienie mechaniczne"));                  // anderes Wort
        Assert.True(RecommendationUse.Appears("The seal, which is mechanical, is worn.", "mechanical seal"));          // nah beieinander
        Assert.False(RecommendationUse.Appears("The seal is worn and after a long time the mechanical parts are fine.", "mechanical seal")); // zu weit auseinander
        Assert.False(RecommendationUse.Appears("Check the fact sheet.", "face"));                                     // kurze Wörter: nicht ähnlich genug
    }

    [Fact]
    public void QuickDecidesWhatStandsThere()
    {
        var d = Dossier("uszczelnienie mechaniczne", "uszczelnienie czołowe");
        Assert.Equal(new TermUse(TermUseKind.Kept, "uszczelnienie mechaniczne", "gebeugt"),
            RecommendationUse.Quick("Stan uszczelnienia mechanicznego jest zły.", d));
        Assert.Equal(TermUseKind.OtherCandidate, RecommendationUse.Quick("Uszczelnienia czołowego nie wymieniono.", d)!.Kind);
        Assert.Null(RecommendationUse.Quick("Trzeba je wymienić.", d));                                                  // umformuliert: die KI fragen
        // ein längerer Kandidat, der die Empfehlung enthält, wurde gewählt
        var shortRec = Dossier("seal", "mechanical seal");
        Assert.Equal(new TermUse(TermUseKind.OtherCandidate, "mechanical seal", "wörtlich"), RecommendationUse.Quick("The mechanical seal is worn.", shortRec));
        Assert.Equal(TermUseKind.Kept, RecommendationUse.Quick("The seal is worn.", shortRec)!.Kind);
    }

    [Fact]
    public void TheAiAnswerOnlyCountsWhenTheTextConfirmsIt()
    {
        var d = Dossier("mechanical seal", "face seal");
        const string target = "The seal unit at the shaft has to be replaced.";
        // eigene Benennung des Übersetzers: ersetzt
        Assert.Equal(new TermUse(TermUseKind.OwnTerm, "seal unit", "KI-Zuordnung"), RecommendationUse.FromAnswer(target, d, "seal unit", "other", null));
        Assert.False(RecommendationUse.FromAnswer(target, d, "seal unit", "other", null)!.Accepted);
        // was nicht in der Übersetzung steht, zählt nicht
        Assert.Null(RecommendationUse.FromAnswer(target, d, "sealing assembly", "other", null));
        // „Empfehlung“, aber ohne jede Ähnlichkeit: lieber keine Rückmeldung
        Assert.Null(RecommendationUse.FromAnswer(target, d, "shaft", "recommended", null));
        // verkürzt („seal“ für „mechanical seal“): übernommen
        Assert.Equal(TermUseKind.Kept, RecommendationUse.FromAnswer("Replace the seal.", d, "seal", "recommended", null)!.Kind);
        // „other“, aber die Empfehlung steht doch drin: übernommen
        Assert.Equal(TermUseKind.Kept, RecommendationUse.FromAnswer("The worn mechanical seals go.", d, "worn mechanical seals", "other", null)!.Kind);
        // ein genannter Kandidat muss einer sein
        Assert.Equal(TermUseKind.OtherCandidate, RecommendationUse.FromAnswer("Replace the face seals.", d, "face seals", "candidate", "face seal")!.Kind);
        Assert.Null(RecommendationUse.FromAnswer(target, d, "seal unit", "candidate", "lip seal"));
        // umschrieben: keine Rückmeldung
        var p = RecommendationUse.FromAnswer("It has to be replaced.", d, "It", "paraphrased", null)!;
        Assert.Equal(TermUseKind.Paraphrased, p.Kind);
        Assert.Null(p.Accepted);
        Assert.Null(RecommendationUse.FromAnswer(target, d, "x", "unsure", null));
    }

    private sealed class Model(string answer) : IChatModel
    {
        public string? Prompt;
        public string DisplayName => "Skript";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task TheAiAssignsAllExpressionsOfASegmentInOneCall()
    {
        var glrd = Dossier("mechanical seal", "face seal");
        var pump = new TermDossier { Expression = "Kreiselpumpe", TargetLanguage = "en-GB", Recommended = "centrifugal pump" };
        var ring = new TermDossier { Expression = "Spaltring", TargetLanguage = "en-GB", Recommended = "wear ring" };
        var model = new Model("""
            {"items": [
              {"expression": "GLRD", "rendering": "seal unit", "relation": "other"},
              {"expression": "Kreiselpumpe", "rendering": "it", "relation": "paraphrased"},
              {"expression": "Spaltring", "rendering": "clearance ring", "relation": "other"},
              {"expression": "erfunden", "rendering": "seal unit", "relation": "other"}
            ]}
            """);
        var r = await RecommendationUse.AskAsync(new AgentContext { Model = model }, "Die GLRD der Kreiselpumpe am Spaltring tauschen.",
            "Replace the seal unit of it at the ring.", [glrd, pump, ring], "de-DE", "en-GB", TestContext.Current.CancellationToken);

        Assert.Contains("\"GLRD\": recommended \"mechanical seal\"; other candidates: \"face seal\"", model.Prompt);
        Assert.Contains("Confirmed translation (", model.Prompt);
        Assert.Equal(TermUseKind.OwnTerm, r[glrd].Kind);
        Assert.Equal(TermUseKind.Paraphrased, r[pump].Kind);
        Assert.False(r.ContainsKey(ring));                                       // „clearance ring“ steht nicht in der Übersetzung
        Assert.Equal(2, r.Count);
        Assert.Equal("ersetzt durch eigene Benennung „seal unit“ (KI-Zuordnung)", RecommendationUse.Describe(r[glrd]));
    }

    [Fact]
    public void FeedbackKeepsWhatStoodThereAndCanBeWithdrawn()
    {
        using var dir = new TempDir();
        ResearchFeedback.UseFolderHere(dir.File("rm"));
        SiteCatalog.UseFileHere(dir.File("quellen.json"));
        var d = Dossier("mechanical seal", "face seal");
        d.Sources = [new SourceRef("en-GB", "Pumps", "https://pumps.example/a") { Quote = "Replace the mechanical seal.", Kind = SourceKinds.Manufacturer }];

        Assert.True(ResearchFeedback.Record("p|f|1|glrd", d, accepted: false, "seal unit", "KI-Zuordnung"));
        var e = Assert.Single(ResearchFeedback.All());
        Assert.Equal(("seal unit", "KI-Zuordnung"), (e.Chosen, e.How));
        Assert.Equal((0, 1), SiteCatalog.Load().Single(x => x.Domain == "pumps.example") is var s ? (s.Accepted, s.Rejected) : default);

        // erneut bestätigt, jetzt nur noch umschrieben: die Rückmeldung wird zurückgenommen, auch der Ruf der Seite
        Assert.True(ResearchFeedback.Withdraw("p|f|1|glrd"));
        Assert.Empty(ResearchFeedback.All());
        Assert.Equal((0, 0), SiteCatalog.Load().Single(x => x.Domain == "pumps.example") is var t ? (t.Accepted, t.Rejected) : default);
        Assert.False(ResearchFeedback.Withdraw("p|f|1|glrd"));
    }

    [Fact]
    public void OlderFeedbackFilesStayReadable()
    {
        using var dir = new TempDir();
        var folder = dir.File("alt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recherche-rueckmeldung.json"), """
            [{"Key": "p|f|1|glrd", "Expression": "GLRD", "Grade": "A", "Accepted": true, "Domains": ["din.de"], "When": "2026-11-10T08:00:00Z"}]
            """);
        ResearchFeedback.UseFolderHere(folder);
        var e = Assert.Single(ResearchFeedback.All());
        Assert.True(e.Accepted);
        Assert.Null(e.Chosen);
        Assert.Null(e.How);
        Assert.Equal("A 1/1", ResearchFeedback.Summary());
    }
}
