using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Einarbeiten in eigene Dokumente: Begriffe mit dem Definitionssatz aus dem Dokument – nach Regeln und von der KI,
/// deren Belegsatz wörtlich im Text stehen muss.
/// </summary>
public sealed class DocumentStudyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static LibraryFile File(string language, string path = @"C:\fachtexte\Pumpen.pdf") =>
        new(path, "Pumpen-Handbuch", language, null, "Stand: 01.03.2021", 3, 4, null);

    [Fact]
    public void GermanDefinitionsAbbreviationsAndGlossariesFollowRules()
    {
        var passages = new List<(int?, string)>
        {
            (1, "Unter einer Gleitringdichtung versteht man eine axiale Dichtung zwischen Welle und Gehäuse.\nDie Pumpe ist ausgeschaltet."),
            (1, "Als Kavitation bezeichnet man die Bildung und Implosion von Dampfblasen in der Flüssigkeit."),
            (2, "Kreiselpumpen sind Strömungsmaschinen, die Flüssigkeiten fördern. Das Ergebnis ist eine Verbesserung um 20 %."),
            (2, "Der Begriff Förderhöhe bezeichnet die nutzbare mechanische Arbeit je Gewichtseinheit. Ziel ist eine lange Lebensdauer."),
            (2, "Der Spaltring ist ein Verschleißteil am Laufrad. Bei Wartung der Gleitringdichtung (GLRD) die Pumpe abschalten."),
            (3, "Begriffe\nLaufrad: rotierendes Bauteil, das die Energie auf die Flüssigkeit überträgt\nSpirale – Gehäuse, das die Strömung sammelt und verzögert"),
            (3, "3.1 Saugstutzen\nAnschluss, durch den die Flüssigkeit in die Pumpe eintritt.\nHinweis: Vor dem Öffnen den Druck ablassen."),
        };
        var terms = DocumentStudy.ExtractRules(passages, File("de-DE"));
        string Def(string term) => terms.Single(t => t.Term == term && t.Kind != DocumentTermKind.Abbreviation).Definition;

        Assert.Equal("Unter einer Gleitringdichtung versteht man eine axiale Dichtung zwischen Welle und Gehäuse.", Def("Gleitringdichtung"));
        Assert.Equal("Als Kavitation bezeichnet man die Bildung und Implosion von Dampfblasen in der Flüssigkeit.", Def("Kavitation"));
        Assert.StartsWith("Kreiselpumpen sind Strömungsmaschinen", Def("Kreiselpumpen"));
        Assert.StartsWith("Der Begriff Förderhöhe bezeichnet", Def("Förderhöhe"));
        Assert.Equal("Der Spaltring ist ein Verschleißteil am Laufrad.", Def("Spaltring"));
        Assert.Equal("Laufrad: rotierendes Bauteil, das die Energie auf die Flüssigkeit überträgt", Def("Laufrad"));
        Assert.StartsWith("Spirale – Gehäuse", Def("Spirale"));
        Assert.Equal("Saugstutzen: Anschluss, durch den die Flüssigkeit in die Pumpe eintritt.", Def("Saugstutzen"));
        var abbreviation = terms.Single(t => t.Abbreviation == "GLRD");
        Assert.Equal(("Gleitringdichtung", 2, DocumentTermKind.Abbreviation), (abbreviation.Term, abbreviation.Page, abbreviation.Kind));
        // „Ergebnis ist eine …“, „Ziel ist eine …“, „Hinweis: …“, „Die Pumpe ist ausgeschaltet.“ sind keine Begriffe
        Assert.DoesNotContain(terms, t => t.Term is "Ergebnis" or "Ziel" or "Hinweis" or "Pumpe");
        var first = terms.First(t => t.Term == "Gleitringdichtung" && t.Kind == DocumentTermKind.Definition);
        Assert.Equal(("Pumpen-Handbuch, S. 1", "file:///C:/fachtexte/Pumpen.pdf#page=1"), (first.Label, first.Uri));
    }

    [Theory]
    [InlineData("einer Gleitringdichtung", "GLRD", "Gleitringdichtung")]
    [InlineData("Vorschriften der Europäischen Union", "EU", "Europäischen Union")]
    [InlineData("vom Bundesamt für Sicherheit in der Informationstechnik", "BSI", "Bundesamt für Sicherheit in der Informationstechnik")]
    [InlineData("die Pumpe arbeitet mit Druck", "PN", null)]
    [InlineData("nach dem Deutschen Institut für Normung", "DIN", "Deutschen Institut für Normung")]
    public void AbbreviationsFindTheirLongForm(string text, string abbreviation, string? expected) =>
        Assert.Equal(expected, DocumentStudy.LongForm(text, abbreviation));

    [Fact]
    public void EnglishAndPolishDefinitions()
    {
        var en = DocumentStudy.ExtractRules([(1, "A mechanical seal is a device that seals a rotating shaft. This is a common design. The term cavitation refers to the formation of vapour bubbles.")], File("en-GB"));
        Assert.Equal(["cavitation", "mechanical seal"], en.Select(t => t.Term).Order());
        var pl = DocumentStudy.ExtractRules([(1, "Pompa wirowa to maszyna przepływowa do tłoczenia cieczy. To jest ważne dla obsługi pompy.")], File("pl-PL"));
        Assert.Equal("pompa wirowa", Assert.Single(pl).Term);
    }

    private sealed class Model(string reply) : IChatModel
    {
        public string DisplayName => "Prüf-KI";
        public string LastPrompt { get; private set; } = "";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            LastPrompt = messages[^1].Text;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task AiTermsNeedAVerbatimQuoteThatContainsTheTerm()
    {
        var model = new Model("""
            {"terms": [
              {"term": "Wellenhülse", "p": 1, "quote": "Die Wellenhülse schützt die Welle vor Verschleiß durch die Packung."},
              {"term": "Laufrad", "p": 1, "quote": "Das Laufrad überträgt die Energie auf das Fördermedium und erhöht den Druck."},
              {"term": "Stopfbuchse", "p": 1, "quote": "Die Wellenhülse schützt die Welle vor Verschleiß durch die Packung."},
              {"term": "Lager", "p": 7, "quote": "Die Lager tragen die Welle."}
            ]}
            """);
        var passages = new List<(int?, string)> { (4, "Die Wellenhülse schützt die  Welle vor Verschleiß durch die Packung. Sie wird bei jeder Wartung geprüft.") };
        var terms = await DocumentStudy.ExtractWithAiAsync(model, passages, File("de-DE"), "Pumpen", TestContext.Current.CancellationToken);

        var term = Assert.Single(terms);                     // Laufrad: Satz steht nicht im Text; Stopfbuchse: nicht im Satz; Lager: kein Abschnitt 7
        Assert.Equal(("Wellenhülse", "Die Wellenhülse schützt die Welle vor Verschleiß durch die Packung.", 4, DocumentTermKind.Ai),
            (term.Term, term.Definition, term.Page, term.Kind));
        Assert.Contains("subject field: Pumpen", model.LastPrompt);
        Assert.Contains("[1] Die Wellenhülse schützt", model.LastPrompt);
    }

    [Fact]
    public async Task StudiesIndexedDocumentsAndSavesWithReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _dir.File("texte");
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, "Wartung.txt"),
            "Stand: 01.03.2021\n\nUnter einer Gleitringdichtung versteht man eine axiale Dichtung zwischen Welle und Gehäuse. "
            + "Die Gleitringdichtung (GLRD) wird alle 500 Stunden geprüft. Die Gleitringdichtung muss trocken bleiben.");
        System.IO.File.WriteAllText(Path.Combine(folder, "Maintenance.txt"),
            "A mechanical seal is a device that seals a rotating shaft between the pump casing and the shaft.");
        using var library = new ReferenceLibrary(_dir.File("index.scidx"));
        await library.ScanAsync([folder], ct: ct);
        var files = library.Files();

        var terms = await DocumentStudy.ExtractAsync(library, files, null, ct: ct);
        var seal = Assert.Single(terms, t => t.Term == "Gleitringdichtung");
        Assert.Equal(("GLRD", DocumentTermKind.Definition, 3), (seal.Abbreviation, seal.Kind, seal.Count));   // Abkürzung dem Begriff zugeordnet
        Assert.Contains(terms, t => t.Term == "mechanical seal" && t.Language.StartsWith("en"));

        using var termbase = new Termbase(_dir.File("t.sctb"));
        using var knowledge = new KnowledgeBase(_dir.File("w.sckb"), "Wissen");
        var saved = DocumentStudy.Save(terms, "de-DE", "en-GB", termbase, knowledge);
        Assert.Equal((2, 2, 0), (saved.TermsSaved, saved.KnowledgeSaved, saved.OtherLanguage));
        var concept = termbase.Lookup("de-DE", "Gleitringdichtung").Single();
        Assert.Contains(concept.Terms, t => t is { Text: "GLRD", Status: TermStatus.Admitted });
        Assert.EndsWith("(Wartung)", concept.Definition);
        Assert.StartsWith("Unter einer Gleitringdichtung versteht man", concept.Definition);
        var entry = knowledge.Search("de-DE", "Gleitringdichtung").First();
        Assert.Equal(("Wartung (Stand: 01.03.2021)", "Einarbeiten"), (entry.SourceTitle, entry.Author));
        Assert.EndsWith("Wartung.txt", entry.SourceUrl);
        Assert.Equal("Unter einer Gleitringdichtung versteht man eine axiale Dichtung zwischen Welle und Gehäuse. (Abkürzung: GLRD)", entry.Content);

        Assert.Equal((0, 0, 0), DocumentStudy.Save(terms, "de-DE", "en-GB", termbase, knowledge) is var again ? (again.TermsSaved, again.KnowledgeSaved, again.OtherLanguage) : default);
        Assert.Equal(1, DocumentStudy.Save(terms, "de-DE", "pl-PL", null, null).OtherLanguage);   // englischer Begriff passt nicht zu de–pl
    }
}
