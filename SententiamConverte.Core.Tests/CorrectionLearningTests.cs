using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Tests;

[CollectionDefinition("Learning", DisableParallelization = true)]
public sealed class LearningCollection;

/// <summary>Aus Korrekturen lernen: Änderung erkennen, einordnen, Regel vorschlagen, an KI und QA weitergeben.</summary>
[Collection("Learning")]
public sealed class CorrectionLearningTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<LearningSuggestion> _hints = new();
    private readonly Action<LearningSuggestion> _onHint;

    public CorrectionLearningTests()
    {
        CorrectionLearning.StorePath = _dir.File("gelernt.json");
        CorrectionLearning.Enabled = true;
        _onHint = _hints.Add;
        CorrectionLearning.Suggested += _onHint;
    }

    public void Dispose()
    {
        CorrectionLearning.Suggested -= _onHint;
        CorrectionLearning.Enabled = false;
        CorrectionLearning.StorePath = Path.Combine(Path.GetTempPath(), "sc-learning-unused.json");
        _dir.Dispose();
    }

    private sealed class Capture : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult("Der Nutzer klickt.");
        }
    }

    [Fact]
    public void ChangesAreFoundAndClassified()
    {
        Assert.Equal([("Benutzer", "Nutzer")], CorrectionLearning.Changes("Der ⟨1⟩Benutzer⟨/1⟩ klickt auf OK.", "Der ⟨1⟩Nutzer⟨/1⟩ klickt auf OK."));
        Assert.Equal([("auf den Knopf", "die Taste")], CorrectionLearning.Changes("Bitte auf den Knopf drücken.", "Bitte die Taste drücken."));
        Assert.Empty(CorrectionLearning.Changes("Gleich.", "Gleich."));

        Assert.Equal(CorrectionKind.Wording, CorrectionLearning.Classify("Benutzer", "Nutzer", "de-DE"));
        Assert.Equal(CorrectionKind.Address, CorrectionLearning.Classify("Pan", "Pani", "pl-PL"));
        Assert.Equal(CorrectionKind.Speaker, CorrectionLearning.Classify("zrobiłem", "zrobiłam", "pl-PL"));
        Assert.Equal(CorrectionKind.Speaker, CorrectionLearning.Classify("był", "była", "pl-PL"));
        Assert.Equal(CorrectionKind.Address, CorrectionLearning.Classify("Sie", "du", "de-DE"));
    }

    [Fact]
    public async Task RepeatedCorrectionsBecomeARuleForAiAndQa()
    {
        CorrectionLearning.Record("de-DE", "Der Benutzer klickt.", "Der Nutzer klickt.");
        Assert.Empty(_hints);                                                              // einmal ist keinmal
        CorrectionLearning.Record("de-DE", "Jeder Benutzer sieht das.", "Jeder Nutzer sieht das.");
        var hint = Assert.Single(_hints);
        Assert.Equal((CorrectionKind.Wording, "Benutzer", "Nutzer", 2), (hint.Kind, hint.Rule!.From, hint.Rule.To, hint.Rule.Count));
        Assert.Null(CorrectionLearning.Instructions("de-DE"));                             // erst nach Zustimmung

        CorrectionLearning.SetState(hint.Rule, RuleState.Active);
        var model = new Capture();
        await new AiTranslator(new AgentContext { Model = model }).TranslateAsync("The user clicks.", "en-GB", "de-DE", null, ct: TestContext.Current.CancellationToken);
        Assert.Contains("write \"Nutzer\" instead of \"Benutzer\"", model.Prompt);

        var doc = new BilingualDocument { SourceLanguage = "en-GB", TargetLanguage = "de-DE" };
        var p = new Paragraph { Locator = "p" };
        p.Segments.Add(new Segment { Id = 1, Source = "The user.", Target = "Der Benutzer." });
        p.Segments.Add(new Segment { Id = 2, Source = "Users.", Target = "Benutzerkonten." });                // nur ganze Wörter
        doc.Paragraphs.Add(p);
        var issue = Assert.Single(QaChecker.Check(doc, new QaSettings { Spelling = false }).Where(i => i.Check == "Gelernte Regel"));
        Assert.Equal(1, issue.SegmentId);

        // Abgeschaltet: nichts wird gelernt oder angewandt
        CorrectionLearning.Enabled = false;
        CorrectionLearning.Record("de-DE", "Das Gerät.", "Der Apparat.");
        Assert.Null(CorrectionLearning.Instructions("de-DE"));
        Assert.DoesNotContain(CorrectionLearning.Rules, r => r.From == "Das Gerät");
    }

    [Fact]
    public void AddressAndGenderChangesGoToTheBriefNotIntoRules()
    {
        CorrectionLearning.Record("pl-PL", "Dziękuję Panu za list.", "Dziękuję Pani za list.");
        var address = Assert.Single(_hints);
        Assert.Equal((CorrectionKind.Address, AddressForm.FormalFemale), (address.Kind, address.Address));
        CorrectionLearning.Record("pl-PL", "Wczoraj zrobiłem to.", "Wczoraj zrobiłam to.");
        Assert.Equal((CorrectionKind.Speaker, SpeakerForm.Female), (_hints[1].Kind, _hints[1].Speaker));
        Assert.Empty(CorrectionLearning.Rules);
    }

    [Fact]
    public void ConfirmingASegmentRecordsTheCorrection()
    {
        using var dir = new TempDir();
        var project = TranslationProject.Create(dir.File("p"), "Lernen", "en-GB", ["de-DE"]);
        project.AddFile(new MemoryStream("The user clicks.\nThe user waits.\n"u8.ToArray()), "a.txt");
        var file = project.Files.Single();
        var doc = project.LoadDocument(file, "de-DE");
        foreach (var (s, text) in doc.Segments.Zip(["Der Nutzer klickt.", "Der Nutzer wartet."]))
        {
            s.Suggestion = text.Replace("Nutzer", "Benutzer");                             // so kam es von der KI
            s.Target = text;                                                                // so hat es der Übersetzer korrigiert
            Workflow.Confirm(project, doc, s);
            Assert.Null(s.Suggestion);
        }
        Assert.Equal(RuleState.Suggested, CorrectionLearning.Rules.Single(r => r.From == "Benutzer").State);
        project.Dispose();
    }

    [Fact]
    public void HabitsPerClientAndTextTypeAreRemembered()
    {
        CorrectionLearning.RememberDefault("Firma A", "Manual", "pl-PL", AddressForm.Informal, SpeakerForm.None);
        CorrectionLearning.RememberDefault("", "letter", "pl-PL", AddressForm.FormalPlural, SpeakerForm.Organisation);
        Assert.Equal(AddressForm.Informal, CorrectionLearning.DefaultFor("Firma A", "manual", "pl-PL")!.Address);
        Assert.Equal(AddressForm.FormalPlural, CorrectionLearning.DefaultFor("Firma B", "letter", "pl-PL")!.Address);   // allgemeine Gewohnheit
        Assert.Null(CorrectionLearning.DefaultFor("Firma B", "manual", "pl-PL"));
        Assert.Null(CorrectionLearning.DefaultFor("Firma A", "manual", "de-DE"));
    }
}
