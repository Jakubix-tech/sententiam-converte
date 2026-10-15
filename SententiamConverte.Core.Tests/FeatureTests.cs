using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

public class FeatureTests
{
    [Fact]
    public void NumbersAreAdaptedInFuzzyMatches()
    {
        Assert.Equal("Dokręcić śrubę momentem 7 Nm.",
            Placeables.AdaptNumbers("Die Schraube mit 7 Nm anziehen.", "Die Schraube mit 5 Nm anziehen.", "Dokręcić śrubę momentem 5 Nm.", "pl-PL"));
        Assert.Equal("Weight: 1,250.5 kg",
            Placeables.AdaptNumbers("Gewicht: 1.250,5 kg", "Gewicht: 980,5 kg", "Weight: 980.5 kg", "en-GB"));
        // mehrdeutig → unverändert
        Assert.Equal("5 und 5", Placeables.AdaptNumbers("7", "5", "5 und 5", "de-DE"));

        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Die Schraube mit 5 Nm anziehen.", "Tighten the screw to 5 Nm.");
        var match = tm.Search("de-DE", "en-GB", "Die Schraube mit 7 Nm anziehen.").Single();
        Assert.InRange(match.Score, 95, 98);
        var seg = new Segment { Source = "Die Schraube mit 7 Nm anziehen." };
        Workflow.ApplyMatch(seg, match, "en-GB");
        Assert.Equal("Tighten the screw to 7 Nm.", seg.Target);
    }

    [Fact]
    public void FragmentsFindWholeShortUnits()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Sicherheitshinweise beachten", "Observe the safety instructions");
        tm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Switch off the device.");
        tm.Add("de-DE", "en-GB", "Völlig anderer Satz hier.", "Something else.");
        var f = tm.Fragments("de-DE", "en-GB", "Vor der Wartung Sicherheitshinweise beachten und das Gerät ausschalten.");
        Assert.Equal(["sicherheitshinweise beachten", "gerät ausschalten"], f.Select(x => TranslationMemory.Norm(x.Phrase)));
    }

    [Fact]
    public void SplitAndMergeKeepTextIntact()
    {
        var doc = new BilingualDocument { FilterId = "docx", SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        var para = new Model.Paragraph();
        new Segmentation.Segmenter("de").Segment(para, "Erster Teil und ⟨1⟩zweiter⟨/1⟩ Teil. Rest.");
        doc.Paragraphs.Add(para);
        doc.Renumber();
        var original = para.JoinSource();
        var first = para.Segments[0];

        Assert.Null(Workflow.Split(doc, first, first.Source.IndexOf("zweiter")));     // innerhalb eines Tag-Paars
        var second = Workflow.Split(doc, first, first.Source.IndexOf(" und"));
        Assert.NotNull(second);
        Assert.Equal("Erster Teil", first.Source);
        Assert.Equal("und ⟨1⟩zweiter⟨/1⟩ Teil.", second!.Source);
        Assert.Equal(original, para.JoinSource());
        Assert.Equal(3, doc.Segments.Count());

        Assert.True(Workflow.MergeWithNext(doc, first));
        Assert.Equal(original, para.JoinSource());
        Assert.Equal(2, doc.Segments.Count());

        Assert.False(Workflow.MergeWithNext(new BilingualDocument { FilterId = "xliff", Paragraphs = [para] }, first));
    }

    [Fact]
    public void AlignerPairsByLengthAndNumbers()
    {
        string[] de = ["Die Pumpe ist abzuschalten.", "Danach 10 Minuten warten.", "Der Filter wird gereinigt und anschließend wieder eingesetzt, bevor die Anlage in Betrieb geht.", "Fertig."];
        string[] en = ["Switch off the pump.", "Then wait 10 minutes.", "Clean the filter.", "Then reinsert it before the system is put into operation.", "Done."];
        var pairs = Aligner.Align(de, en);
        Assert.Equal(4, pairs.Count);
        Assert.Equal("Then wait 10 minutes.", pairs[1].TargetText);
        Assert.Equal(2, pairs[2].Target.Count);                  // 1:2
        Assert.Equal("Done.", pairs[3].TargetText);
        Assert.True(pairs[1].Accepted);
    }

    [Fact]
    public void RegexRulesAndSpelling()
    {
        var doc = new BilingualDocument { SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        doc.Paragraphs.Add(new Model.Paragraph
        {
            Segments = [new Segment { Source = "© Firma 2026", Target = "Company 2026 teh end" }],
        });
        doc.Renumber();
        var settings = new QaSettings
        {
            RegexRules = [new RegexRule { Name = "Copyright fehlt", SourcePattern = "©", TargetPattern = "©" }],
        };

        using var dir = new TempDir();
        File.WriteAllText(dir.File("en-GB.aff"), "SET UTF-8\n");
        File.WriteAllText(dir.File("en-GB.dic"), "3\nCompany\nthe\nend\n");
        var spell = new SpellChecker(dir.Path);
        Assert.True(spell.IsAvailable("en-GB"));

        var issues = QaChecker.Check(doc, settings, null, spell);
        Assert.Contains(issues, i => i.Check == "Regel" && i.Message == "Copyright fehlt");
        Assert.Contains(issues, i => i.Check == "Rechtschreibung" && i.Message.Contains("teh"));
        spell.AddToUserDictionary("en-GB", "teh");
        Assert.Empty(new SpellChecker(dir.Path).Check("teh end", "en-GB"));
    }

    [Fact]
    public void RobotsRulesAreRespected()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /privat/
            Allow: /privat/offen.html

            User-agent: SententiamConverte
            Disallow: /
            """, "SententiamConverte/0.1");
        Assert.False(rules.Allows("/irgendwas"));
        var general = RobotsRules.Parse("User-agent: *\nDisallow: /privat/\nAllow: /privat/offen.html\n", "SententiamConverte");
        Assert.True(general.Allows("/artikel"));
        Assert.False(general.Allows("/privat/x"));
        Assert.True(general.Allows("/privat/offen.html"));
    }

    private static string SdlSegment(string lang, string text) =>
        $"<Segment><Elements><Text><Value>{text}</Value></Text></Elements><CultureName>{lang}</CultureName></Segment>";

    [Fact]
    public void SdltmImport()
    {
        using var dir = new TempDir();
        var path = dir.File("alt.sdltm");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE translation_units(id INTEGER PRIMARY KEY, source_segment TEXT, target_segment TEXT, creation_date TEXT);" +
                              "INSERT INTO translation_units(source_segment,target_segment,creation_date) VALUES($s,$t,'2024-05-01 10:00:00')";
            cmd.Parameters.AddWithValue("$s", SdlSegment("de-DE", "Guten Morgen"));
            cmd.Parameters.AddWithValue("$t", SdlSegment("en-GB", "Good morning"));
            cmd.ExecuteNonQuery();
        }
        using var tm = new TranslationMemory(dir.File("neu.sctm"));
        Assert.Equal(1, SdltmImporter.Import(path, tm));
        Assert.Equal("Good morning", tm.Search("de-DE", "en-GB", "Guten Morgen").Single().Unit.Target);
    }

    [Fact]
    public void PackageRoundTrip()
    {
        using var dir = new TempDir();
        const string sdlxliff = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2" xmlns:sdl="http://sdl.com/FileTypes/SdlXliff/1.0">
              <file original="a.docx" source-language="de-DE" target-language="en-GB" datatype="x-sdlfilterframework2">
                <body>
                  <trans-unit id="u1">
                    <source>Hallo Welt.</source>
                    <seg-source><mrk mtype="seg" mid="1">Hallo Welt.</mrk></seg-source>
                    <target><mrk mtype="seg" mid="1"></mrk></target>
                    <sdl:seg-defs><sdl:seg id="1" /></sdl:seg-defs>
                  </trans-unit>
                </body>
              </file>
            </xliff>
            """;
        var package = dir.File("auftrag.sdlppx");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("Auftrag.sdlproj").Open()))
                w.Write("<Project Name=\"Auftrag 42\"><LanguageDirections><LanguageDirection SourceLanguageCode=\"de-DE\" TargetLanguageCode=\"en-GB\" /></LanguageDirections></Project>");
            using (var w = new StreamWriter(zip.CreateEntry("en-GB/a.docx.sdlxliff").Open()))
                w.Write(sdlxliff);
        }

        using var project = ProjectPackage.Import(package, dir.File("projekt"));
        Assert.Equal("Auftrag 42", project.Name);
        Assert.True(project.IsFromPackage);
        var file = project.Files.Single();
        Assert.Equal(["en-GB"], project.LanguagesOf(file));

        var doc = project.LoadDocument(file, "en-GB");
        var seg = doc.Segments.Single();
        seg.Target = "Hello world.";
        Workflow.Confirm(project, doc, seg);
        project.SaveDocument(file, doc);

        var output = new MemoryStream();
        ProjectPackage.ExportReturnPackage(project, output);
        output.Position = 0;
        using var result = new ZipArchive(output, ZipArchiveMode.Read);
        var text = new StreamReader(result.GetEntry("en-GB/a.docx.sdlxliff")!.Open(), Encoding.UTF8).ReadToEnd();
        Assert.Contains(">Hello world.</mrk>", text);
        Assert.Contains("conf=\"Translated\"", text);
        Assert.NotNull(result.GetEntry("Auftrag.sdlproj"));
    }

    [Fact]
    public void OcrTextBecomesDocx()
    {
        var ms = new MemoryStream();
        Filters.PdfFilter.OcrTextToDocx("=== Seite 1 ===\nTitel\n\nErster Absatz.\n=== Seite 2 ===\nZweite Seite.", "de-DE", ms);
        var doc = new Filters.DocxFilter().Read(new MemoryStream(ms.ToArray()), "x.docx", "de-DE", "en-GB", new Segmentation.Segmenter("de"));
        Assert.Equal(["Titel", "Erster Absatz.", "Zweite Seite."], doc.Segments.Select(s => s.Source));
    }

    [Fact]
    public void LocalProvidersAreRecognised()
    {
        Assert.True(new AiProviderConfig { BaseUrl = "http://localhost:8080/v1" }.IsLocal);
        Assert.True(new AiProviderConfig { BaseUrl = "http://192.168.1.20:11434/v1" }.IsLocal);
        Assert.False(new AiProviderConfig { BaseUrl = "https://api.anthropic.com" }.IsLocal);
    }
}
