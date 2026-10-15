using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class SegmenterTests
{
    [Fact]
    public void SplitsSentencesButNotAbbreviations()
    {
        var (_, pieces) = new Segmenter("de-DE").Split("Das ist z. B. ein Test. Am 3. Mai kam Dr. Meier. Fertig!");
        Assert.Equal(["Das ist z. B. ein Test.", "Am 3. Mai kam Dr. Meier.", "Fertig!"], pieces.Select(p => p.Text));
        Assert.Equal(" ", pieces[0].TrailingWhitespace);
    }

    [Fact]
    public void DoesNotSplitInsideTagPair()
    {
        var (_, pieces) = new Segmenter("en-US").Split("⟨1⟩First one. Second one.⟨/1⟩ Third.");
        Assert.Equal(2, pieces.Count);
        Assert.True(TagText.IsBalanced(pieces[0].Text));
    }

    [Fact]
    public void JoinRestoresOriginal()
    {
        const string text = "  Eins. Zwei?  Drei";
        var p = new Model.Paragraph();
        new Segmenter("de").Segment(p, text);
        Assert.Equal(text, p.JoinSource());
    }
}

public class FuzzyTests
{
    [Fact]
    public void ScoresFuzzyPercent()
    {
        Assert.Equal(100, FuzzyMatcher.Score("Die Schraube festziehen.", "Die Schraube festziehen."));
        Assert.Equal(99, FuzzyMatcher.Score("Die ⟨1⟩Schraube⟨/1⟩ festziehen.", "Die Schraube festziehen."));
        var near = FuzzyMatcher.Score("Die Schrauben festziehen.", "Die Schraube festziehen.");
        var far = FuzzyMatcher.Score("Die Mutter lösen.", "Die Schraube festziehen.");
        Assert.InRange(near, 85, 98);
        Assert.True(far < 60);
    }
}

public class TmTests
{
    [Fact]
    public void FindsExactAndFuzzyMatches()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "pl-PL", "Die Schraube mit 5 Nm festziehen.", "Dokręcić śrubę momentem 5 Nm.");
        tm.Add("de-DE", "pl-PL", "Das Gerät ausschalten.", "Wyłączyć urządzenie.");
        tm.Add("de-DE", "en-GB", "Das Gerät ausschalten.", "Switch off the device.");

        var exact = tm.Search("de-AT", "pl-PL", "Das Gerät ausschalten.");
        Assert.Equal(100, exact[0].Score);
        Assert.Equal("Wyłączyć urządzenie.", exact[0].Unit.Target);

        var fuzzy = tm.Search("de-DE", "pl-PL", "Die Schraube mit 7 Nm festziehen.");
        Assert.Single(fuzzy);
        Assert.InRange(fuzzy[0].Score, 80, 98);

        Assert.Single(tm.Concordance("de-DE", "pl-PL", "Schraube"));
    }

    [Fact]
    public void TmxRoundTrip()
    {
        using var dir = new TempDir();
        using var tm = new TranslationMemory(dir.File("a.sctm"));
        tm.Add("de-DE", "en-GB", "Text ⟨1⟩fett⟨/1⟩.", "Text in ⟨1⟩bold⟨/1⟩.");
        using var ms = new MemoryStream();
        TmxIo.Export(tm, ms);
        ms.Position = 0;
        using var tm2 = new TranslationMemory(dir.File("b.sctm"));
        var r = TmxIo.Import(ms, tm2);
        Assert.Equal(1, r.Imported);
        Assert.Equal("Text in ⟨1⟩bold⟨/1⟩.", tm2.All().Single().Target);
    }
}

public class TermbaseTests
{
    [Fact]
    public void RecognizesInflectedTerms()
    {
        using var dir = new TempDir();
        using var tb = new Termbase(dir.File("t.sctb"));
        tb.AddConcept([("de-DE", "Schraube", TermStatus.Preferred), ("pl-PL", "śruba", TermStatus.Preferred), ("pl-PL", "wkręt", TermStatus.Forbidden)]);
        var hits = tb.Recognize("Alle Schrauben anziehen.", "de-DE", "pl-PL");
        Assert.Single(hits);
        Assert.Equal("śruba", hits[0].Translations[0].Text);

        using var ms = new MemoryStream();
        TbxIo.Export(tb, ms);
        ms.Position = 0;
        using var tb2 = new Termbase(dir.File("t2.sctb"));
        Assert.Equal(1, TbxIo.Import(ms, tb2));
        Assert.Contains(tb2.AllConcepts().Single().Terms, t => t.Text == "wkręt" && t.Status == TermStatus.Forbidden);
    }
}

public class QaTests
{
    [Fact]
    public void FindsNumberTagAndTermErrors()
    {
        using var dir = new TempDir();
        using var tb = new Termbase(dir.File("t.sctb"));
        tb.AddConcept([("de-DE", "Schraube", TermStatus.Preferred), ("en-GB", "screw", TermStatus.Preferred)]);
        var doc = new BilingualDocument { SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        doc.Paragraphs.Add(new Model.Paragraph
        {
            Segments =
            [
                new Segment { Source = "Die ⟨1⟩Schraube⟩ mit 1.000,5 Nm.", Target = "The bolt with 1,000.5 Nm." },
                new Segment { Source = "Die Schraube mit 5 Nm.", Target = "The screw with 6 Nm." },
            ],
        });
        doc.Renumber();
        var issues = QaChecker.Check(doc, new QaSettings(), tb);
        Assert.Contains(issues, i => i.SegmentId == 1 && i.Check == "Terminologie");
        Assert.DoesNotContain(issues, i => i.SegmentId == 1 && i.Check == "Zahlen");
        Assert.Contains(issues, i => i.SegmentId == 2 && i.Check == "Zahlen");
    }
}

public class DocxFilterTests
{
    private static byte[] MakeDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.Run(new W.Text("Das ist ") { Space = SpaceProcessingModeValues.Preserve }),
                    new W.Run(new W.RunProperties(new W.Bold()), new W.Text("wichtig")),
                    new W.Run(new W.Text(". Zweiter Satz mit Umbruch") { Space = SpaceProcessingModeValues.Preserve }),
                    new W.Run(new W.Break()),
                    new W.Run(new W.Text("hier."))),
                new W.Paragraph(new W.Run(new W.Text("12345")))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    [Fact]
    public void ReadsTagsAndWritesTranslation()
    {
        var bytes = MakeDocx();
        var filter = new DocxFilter();
        var doc = filter.Read(new MemoryStream(bytes), "a.docx", "de-DE", "en-GB", new Segmenter("de-DE"));

        Assert.Single(doc.Paragraphs);                 // "12345" hat keinen Text zum Übersetzen
        var segs = doc.Segments.ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal("Das ist ⟨1⟩wichtig⟨/1⟩.", segs[0].Source);
        Assert.Equal("Zweiter Satz mit Umbruch⟨2/⟩hier.", segs[1].Source);

        segs[0].Target = "This is ⟨1⟩important⟨/1⟩.";
        segs[1].Target = "Second sentence with break⟨2/⟩here.";
        using var output = new MemoryStream();
        filter.Write(doc, new MemoryStream(bytes), output);

        output.Position = 0;
        using var result = WordprocessingDocument.Open(output, false);
        var body = result.MainDocumentPart!.Document!.Body!;
        var p = body.Elements<W.Paragraph>().First();
        Assert.Equal("This is important. Second sentence with breakhere.", p.InnerText);
        Assert.Contains(p.Descendants<W.Run>(), r => r.RunProperties?.Bold != null && r.InnerText == "important");
        Assert.Single(p.Descendants<W.Break>());
        Assert.Equal("12345", body.Elements<W.Paragraph>().Last().InnerText);
    }
}

public class XliffFilterTests
{
    private const string Xliff = """
        <?xml version="1.0" encoding="utf-8"?>
        <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
          <file original="a.txt" source-language="de-DE" target-language="en-GB" datatype="plaintext">
            <body>
              <trans-unit id="1"><source>Hallo <g id="b1">Welt</g>!</source></trans-unit>
              <trans-unit id="2"><source>Zweite <x id="x1"/>Zeile.</source><target state="translated">Second <x id="x1"/>line.</target></trans-unit>
            </body>
          </file>
        </xliff>
        """;

    [Fact]
    public void RoundTrip()
    {
        var filter = new XliffFilter();
        var bytes = System.Text.Encoding.UTF8.GetBytes(Xliff);
        var doc = filter.Read(new MemoryStream(bytes), "a.xlf", "de-DE", "en-GB", new Segmenter("de"));
        var segs = doc.Segments.ToList();
        Assert.Equal("Hallo ⟨1⟩Welt⟨/1⟩!", segs[0].Source);
        Assert.Equal("Second ⟨1/⟩line.", segs[1].Target);
        Assert.Equal(SegmentStatus.Translated, segs[1].Status);

        segs[0].Target = "Hello ⟨1⟩world⟨/1⟩!";
        segs[0].Status = SegmentStatus.Translated;
        using var output = new MemoryStream();
        filter.Write(doc, new MemoryStream(bytes), output);
        var xml = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(">Hello <g id=\"b1\">world</g>!</target>", xml);
        Assert.Contains("state=\"translated\"", xml);
    }
}

public class ProjectTests
{
    [Fact]
    public void ConfirmWritesTmAndPropagates()
    {
        using var dir = new TempDir();
        using var project = TranslationProject.Create(dir.File("p"), "Test", "de-DE", ["en-GB"], "Maschinenbau");
        var file = project.AddFile(new MemoryStream("Gerät aus.\nGerät aus.\nAnders.\n"u8.ToArray()), "a.txt");
        var doc = project.LoadDocument(file, "en-GB");
        var segs = doc.Segments.ToList();
        segs[0].Target = "Device off.";
        Assert.Equal(1, Workflow.Confirm(project, doc, segs[0]));
        Assert.Equal("Device off.", segs[1].Target);
        Assert.Equal(1, project.ProjectTm.Count());

        project.SaveDocument(file, doc);
        using var reopened = TranslationProject.Open(dir.File("p"));
        var output = new MemoryStream();
        reopened.ExportTarget(reopened.Files[0], reopened.LoadDocument(reopened.Files[0], "en-GB"), output);
        Assert.StartsWith("Device off.\nDevice off.\nAnders.", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }
}

public class AiHelperTests
{
    [Fact]
    public void ExtractsJsonFromChattyAnswer()
    {
        var r = JsonExtract.Parse<Dictionary<string, int>>("Gern! ```json\n{\"a\": 1, \"b\": 2,}\n``` Viel Erfolg.");
        Assert.Equal(2, r["b"]);
    }

    [Fact]
    public void PhraseMatchIgnoresCaseAndAccents()
    {
        Assert.True(UsageCheckAgent.ContainsPhrase("Le CAFÉ  noir est bon", "café noir"));
        Assert.False(UsageCheckAgent.ContainsPhrase("café au lait", "café noir"));
    }

    private sealed class FakeModel(params string[] answers) : IChatModel
    {
        private int _i;
        public string DisplayName => "fake";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
            Task.FromResult(answers[Math.Min(_i++, answers.Length - 1)]);
    }

    private sealed class FakeSearch : ISearchProvider
    {
        public string Name => "fake";
        public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default) =>
            Task.FromResult(query.Contains("Drehmoment")
                ? new List<SearchResult> { new("A", "https://a", "das Drehmoment prüfen"), new("B", "https://b", "Drehmoment prüfen und") }
                : new List<SearchResult> { new("C", "https://c", "etwas anderes") });
    }

    [Fact]
    public async Task UsageAgentCountsEvidence()
    {
        var ctx = new AgentContext
        {
            Model = new FakeModel("{\"variants\": [\"Drehkraft prüfen\"]}", "{\"verdict\": \"common\", \"recommended\": \"Drehmoment prüfen\", \"explanation\": \"Belegt.\"}"),
            Search = new FakeSearch(),
        };
        var r = await new UsageCheckAgent(ctx).RunAsync("Drehmoment prüfen", "de-DE");
        Assert.Equal(UsageVerdict.Common, r.Verdict);
        Assert.Equal(2, r.Evidence[0].ExactHits);
        Assert.Equal(0, r.Evidence[1].ExactHits);
    }
}
