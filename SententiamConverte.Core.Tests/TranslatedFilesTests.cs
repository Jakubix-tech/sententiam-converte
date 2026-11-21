using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Alte Übersetzungen übernehmen: Dateien zuordnen, ausrichten, ins TM schreiben, Fachbegriffe vorschlagen.</summary>
[Collection("BuiltInAi")]
public sealed class TranslatedFilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    public TranslatedFilesTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _dir.File("home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static ImportFile Txt(string name, params string[] paragraphs) => new(name, Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs)));

    private static readonly string[] De =
    [
        "Der Mieter zahlt die Miete bis zum dritten Werktag des Monats auf das Konto des Vermieters.",
        "Die Kaution beträgt drei Monatsmieten und wird auf einem Sparkonto angelegt.",
        "Der Mietvertrag kann mit einer Frist von drei Monaten gekündigt werden.",
    ];
    private static readonly string[] En =
    [
        "The tenant pays the rent to the landlord's account by the third working day of the month.",
        "The deposit amounts to three months' rent and is placed in a savings account.",
        "The tenancy agreement may be terminated with three months' notice.",
    ];
    private static readonly string[] Pl =
    [
        "Najemca płaci czynsz na konto wynajmującego do trzeciego dnia roboczego miesiąca.",
        "Kaucja wynosi trzy miesięczne czynsze i jest lokowana na rachunku oszczędnościowym.",
        "Umowa najmu może zostać wypowiedziana z zachowaniem trzymiesięcznego okresu wypowiedzenia.",
    ];

    [Fact]
    public void LanguagesAreReadFromFileNamesAndContent()
    {
        Assert.Equal(("vertrag", "de"), TranslatedFiles.SplitName("Vertrag_DE.docx"));
        Assert.Equal(("vertrag", "en"), TranslatedFiles.SplitName("EN-Vertrag.docx"));
        Assert.Equal(("handbuch_v2", "en"), TranslatedFiles.SplitName("Handbuch v2.en-GB.docx"));
        Assert.Equal(("bericht", "pl"), TranslatedFiles.SplitName("Bericht (polski).txt"));
        Assert.Equal(("text", "uk"), TranslatedFiles.SplitName("text_uk-UA.docx"));                 // Ukrainisch, nicht Vereinigtes Königreich
        Assert.Equal(("report", null), TranslatedFiles.SplitName("Report.docx"));
        Assert.Equal(("de", null), TranslatedFiles.SplitName("de.docx"));                            // nie das einzige Wort

        Assert.Equal("de", TranslatedFiles.DetectLanguage(string.Join(" ", De)));
        Assert.Equal("en", TranslatedFiles.DetectLanguage(string.Join(" ", En)));
        Assert.Equal("pl", TranslatedFiles.DetectLanguage(string.Join(" ", Pl)));
        Assert.Equal("ru", TranslatedFiles.DetectLanguage("Арендатор оплачивает аренду до третьего рабочего дня месяца."));
        Assert.Equal("uk", TranslatedFiles.DetectLanguage("Орендар сплачує орендну плату до третього робочого дня місяця, і це є обов'язковим."));
        Assert.Null(TranslatedFiles.DetectLanguage("12345 67"));
    }

    [Fact]
    public void FilesArePairedByNameLanguageAndContent()
    {
        var files = new List<ImportFile>
        {
            Txt("Mietvertrag_EN.txt", En), Txt("Mietvertrag_DE.txt", De), Txt("Mietvertrag_PL.txt", Pl),   // ein Original, zwei Übersetzungen
            Txt("Kaution.txt", De[1]), Txt("Deposit.txt", En[1]),                                          // Namen verschieden, Sprache erkannt
            Txt("Notiz.txt", "Nur eine Notiz ohne Gegenstück, sie bleibt übrig und wird nicht zugeordnet."),
        };
        var result = TranslatedFiles.Pair(files, "de-DE", ["en-GB", "pl-PL"]);

        Assert.Contains(result.Pairs, p => p.Source.Name == "Mietvertrag_DE.txt" && p.Target!.Name == "Mietvertrag_EN.txt" && p.TargetLanguage == "en-GB");
        Assert.Contains(result.Pairs, p => p.Source.Name == "Mietvertrag_DE.txt" && p.Target!.Name == "Mietvertrag_PL.txt" && p.TargetLanguage == "pl-PL");
        Assert.Contains(result.Pairs, p => p.Source.Name == "Kaution.txt" && p.Target!.Name == "Deposit.txt" && p.TargetLanguage == "en-GB");
        Assert.Equal(3, result.Pairs.Count);
        Assert.Equal("Notiz.txt", Assert.Single(result.Unpaired).Name);

        // Übersetzung zuerst gewählt: Original und Übersetzung werden trotzdem richtig erkannt; Tauschen geht von Hand
        var swapped = Assert.Single(TranslatedFiles.Pair([Txt("b.txt", En), Txt("a.txt", De)], "de-DE", ["en-GB"]).Pairs);
        Assert.Equal("a.txt", swapped.Source.Name);
        swapped.Swap();
        Assert.Equal(("b.txt", "en-GB", "de-DE"), (swapped.Source.Name, swapped.SourceLanguage, swapped.TargetLanguage));
    }

    [Fact]
    public void AlignedSentencesGoIntoTheTmWithContextAndPrivacy()
    {
        var pair = Assert.Single(TranslatedFiles.Pair(
            [Txt("Vertrag_DE.txt", [.. De, "Herr Müller unterschreibt am 12.03.2024."]), Txt("Vertrag_EN.txt", [.. En, "Mr Müller signs on 12/03/2024."])],
            "de-DE", ["en-GB"]).Pairs);
        var session = TranslatedFiles.Align(pair);
        Assert.Equal(4, session.Accepted.Count());

        using var tm = new TranslationMemory(_dir.File("tm.sctm"));
        Assert.Equal(4, TranslatedFiles.ImportToTm(session, tm, "Vertrag_DE.txt", privacy: new PrivacySettings { Enabled = true }));
        Assert.DoesNotContain(tm.All(), u => u.Source.Contains("Müller") || u.Target.Contains("Müller"));
        Assert.Contains(tm.All(), u => u.Target == "Mr [NAME_1] signs on [DATE_1].");
        // Nachbarsätze als Kontext: derselbe Satz an derselben Stelle ergibt einen Kontexttreffer
        var match = tm.Search("de-DE", "en-GB", De[1], context: new TmContext(De[0], De[2], null)).First();
        Assert.Equal(101, match.Score);
    }

    [Fact]
    public void BilingualFilesNeedNoAlignmentAndSharedDatabasesAreCreatedOnce()
    {
        var xliff = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file source-language="de-DE" target-language="pl-PL" datatype="plaintext" original="a.txt"><body>
                <trans-unit id="1"><source>Die Kaution beträgt drei Monatsmieten.</source><target>Kaucja wynosi trzy miesięczne czynsze.</target></trans-unit>
                <trans-unit id="2"><source>Noch offen.</source></trans-unit>
              </body></file>
            </xliff>
            """;
        var pair = Assert.Single(TranslatedFiles.Pair([new ImportFile("vertrag_pl.xlf", Encoding.UTF8.GetBytes(xliff))], "de-DE", ["en-GB", "pl-PL"]).Pairs);
        Assert.True(pair.IsBilingual);
        Assert.Equal("pl-PL", pair.TargetLanguage);
        var session = TranslatedFiles.Align(pair);
        var only = Assert.Single(session.Accepted);                                            // unübersetzte Einheit fällt weg
        Assert.True(only.Confirmed);

        var (name, path, created) = TranslatedFiles.SharedDatabase(ResourceKind.Tm, "de-DE", "pl-PL", null);
        Assert.Equal("TM de-DE–pl-PL", name);
        Assert.True(created && File.Exists(path));
        Assert.False(TranslatedFiles.SharedDatabase(ResourceKind.Tm, "de-DE", "pl-PL", null).Created);   // beim zweiten Mal dieselbe
        Assert.Contains(LanguageResources.Load(), r => r.Location == path);                     // gilt in jedem Projekt de→pl
    }

    private sealed class Scripted(string reply) : IChatModel
    {
        public string DisplayName => "Drehbuch";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) => Task.FromResult(reply);
    }

    [Fact]
    public async Task TermSuggestionsAreCheckedAgainstTheSentences()
    {
        var pairs = De.Zip(Pl).Select(x => (x.First, x.Second)).Append(("Der Mietvertrag endet.", "Umowa najmu wygasa.")).ToList();
        var model = new Scripted("""
            {"terms": [
              {"s": "Mietvertrag", "t": "Umowa najmu"},
              {"s": "Kaution", "t": "kaucja"},
              {"s": "Sparkonto", "t": "rachunek oszczędnościowy"},
              {"s": "Mieter", "t": "lokator"},
              {"s": "[NAME_1]", "t": "[NAME_1]"},
              {"s": "Vermieter", "t": "wynajmujący"}
            ]}
            """);
        var known = new HashSet<string> { "Vermieter" };
        var terms = await new TermSuggester(model).SuggestAsync(pairs, "de-DE", "pl-PL", "Mietrecht", known.Contains, TestContext.Current.CancellationToken);

        Assert.Equal("Mietvertrag", terms[0].Source);                                       // am häufigsten belegt
        Assert.Equal(2, terms[0].Count);
        Assert.Contains(terms, t => t is { Source: "Kaution", Target: "kaucja" });         // „Kaucja“ im Satz – Groß/klein egal
        Assert.Contains(terms, t => t is { Source: "Sparkonto", Target: "rachunek oszczędnościowy" });  // gebeugt: „rachunku oszczędnościowym“
        Assert.DoesNotContain(terms, t => t.Source == "Mieter");                            // „lokator“ steht nirgends – erfunden
        Assert.DoesNotContain(terms, t => t.Source.Contains("NAME"));                       // Platzhalter
        Assert.DoesNotContain(terms, t => t.Source == "Vermieter");                         // schon in der Terminologie
        Assert.Equal("Der Mietvertrag endet.", terms[0].Example);
        Assert.Equal("umowa najmu", terms[0].Target);                                       // „Umowa“ nur am Satzanfang groß

        Assert.Equal("kaucja", TermSuggester.NormalizeCase("Kaucja", ["Kaucja wynosi trzy czynsze."], "pl-PL"));
        Assert.Equal("Lipsk", TermSuggester.NormalizeCase("Lipsk", ["Sąd w Lipsku oddalił apelację."], "pl-PL"));   // Eigenname
        Assert.Equal("Kaution", TermSuggester.NormalizeCase("Kaution", ["Kaution beträgt …"], "de-DE"));             // deutsche Substantive
        Assert.Equal("EU", TermSuggester.NormalizeCase("EU", ["EU-Recht gilt."], "en-GB"));
        Assert.Equal("Betriebskosten", TermSuggester.NormalizeCase("betriebskosten", ["Über die Betriebskosten wird abgerechnet."], "de-DE"));
        Assert.Equal("monatliche Miete", TermSuggester.NormalizeCase("Monatliche Miete", ["Er zahlt die monatliche Miete."], "de-DE"));
    }
}
