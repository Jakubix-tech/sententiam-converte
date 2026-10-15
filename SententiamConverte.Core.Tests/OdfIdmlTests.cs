using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

public class OdfIdmlTests
{
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        return ms.ToArray();
    }

    private static string Entry(byte[] zip, string name)
    {
        using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    private static (BilingualDocument, byte[]) RoundTrip(IFileFilter f, byte[] input, Func<string, string> translate)
    {
        var doc = f.Read(new MemoryStream(input), "x", "de-DE", "en-GB", new Segmenter("de-DE"));
        foreach (var s in doc.Segments.Where(s => !s.Locked)) s.Target = translate(s.Source);
        var output = new MemoryStream();
        f.Write(doc, new MemoryStream(input), output);
        return (doc, output.ToArray());
    }

    [Fact]
    public void OdtRoundTrip()
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3"><office:body><office:text><text:h text:outline-level="1">Einleitung</text:h><text:p>Das ist <text:span text:style-name="T1">wichtig</text:span>.<text:line-break/>Mehr  Text<text:tab/>hier mit <text:a xlink:href="https://x.y">Link</text:a>.</text:p><text:p>12345</text:p></office:text></office:body></office:document-content>
            """;
        var odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", content), ("META-INF/manifest.xml", "<manifest/>"));
        var (doc, output) = RoundTrip(new OdfFilter(), odt, s => s
            .Replace("Einleitung", "Introduction").Replace("Das ist ", "This is ").Replace("wichtig", "important")
            .Replace("Mehr  Text", "More  text").Replace("hier mit ", "here with ").Replace("Link", "link"));
        Assert.Equal("Überschrift", doc.Paragraphs[0].Context);
        Assert.Equal("Das ist ⟨1⟩wichtig⟨/1⟩.⟨2/⟩Mehr  Text\thier mit ⟨3⟩Link⟨/3⟩.", doc.Paragraphs[1].JoinSource());

        var xml = Entry(output, "content.xml");
        Assert.Contains("<text:h text:outline-level=\"1\">Introduction</text:h>", xml);
        Assert.Contains("This is <text:span text:style-name=\"T1\">important</text:span>.<text:line-break />More <text:s />text<text:tab />here with <text:a xlink:href=\"https://x.y\">link</text:a>.", xml);
        Assert.Contains("<text:p>12345</text:p>", xml);
        using var z = new ZipArchive(new MemoryStream(output), ZipArchiveMode.Read);
        Assert.Equal("mimetype", z.Entries[0].FullName);
    }

    [Fact]
    public void OdsCells()
    {
        const string content = """
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"><office:body><office:spreadsheet><table:table table:name="T"><table:table-row><table:table-cell office:value-type="string"><text:p>Menge</text:p></table:table-cell><table:table-cell office:value-type="float" office:value="3"><text:p>3</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>
            """;
        var ods = Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"), ("content.xml", content));
        var (doc, output) = RoundTrip(new OdfFilter(), ods, s => s.Replace("Menge", "Quantity"));
        Assert.Equal("Tabelle", Assert.Single(doc.Paragraphs).Context);
        Assert.Contains("<text:p>Quantity</text:p>", Entry(output, "content.xml"));
    }

    [Fact]
    public void IdmlRoundTrip()
    {
        const string story = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <idPkg:Story xmlns:idPkg="http://ns.adobe.com/AdobeInDesign/idml/1.0/packaging" DOMVersion="18.0"><Story Self="u1">
            <ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/Body"><CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content>Die Pumpe ist </Content></CharacterStyleRange><CharacterStyleRange AppliedCharacterStyle="CharacterStyle/Fett" FontStyle="Bold"><Content>sehr</Content></CharacterStyleRange><CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content> leise.<?ACE 7?></Content><Br/><Content>Zweiter Absatz </Content><Rectangle Self="r1"/><Content>mit Bild.</Content></CharacterStyleRange></ParagraphStyleRange>
            <ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/Tab"><CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Table Self="t1"><Cell Self="c1"><ParagraphStyleRange AppliedParagraphStyle="ParagraphStyle/Cell"><CharacterStyleRange AppliedCharacterStyle="CharacterStyle/$ID/[No character style]"><Content>Zelle</Content></CharacterStyleRange></ParagraphStyleRange></Cell></Table></CharacterStyleRange></ParagraphStyleRange>
            </Story></idPkg:Story>
            """;
        var idml = Zip(("mimetype", "application/vnd.adobe.indesign-idml-package"), ("designmap.xml", "<Document/>"), ("Stories/Story_u1.xml", story));
        var (doc, output) = RoundTrip(new IdmlFilter(), idml, s => s
            .Replace("Die Pumpe ist ", "The pump is ").Replace("sehr", "very").Replace(" leise.", " quiet.")
            .Replace("Zweiter Absatz ", "Second paragraph ").Replace("mit Bild.", "with picture.").Replace("Zelle", "Cell"));
        var sources = doc.Paragraphs.Select(p => p.JoinSource()).ToList();
        Assert.Contains("Die Pumpe ist ⟨1⟩sehr⟨/1⟩ leise.⟨2/⟩", sources);
        Assert.Contains("Zweiter Absatz ⟨1/⟩mit Bild.", sources);
        Assert.Contains("Zelle", sources);

        var x = XDocument.Parse(Entry(output, "Stories/Story_u1.xml"));
        var text = string.Concat(x.Descendants("Content").Select(c => c.Value));
        Assert.Contains("The pump is very quiet.", text);
        Assert.Contains("Second paragraph with picture.", text);
        Assert.Contains("Cell", text);
        Assert.Single(x.Descendants("Rectangle"));
        Assert.Single(x.Descendants("Table"));
        Assert.Equal("Bold", (string?)x.Descendants("CharacterStyleRange").First(c => c.Value == "very").Attribute("FontStyle"));
        Assert.Contains(x.DescendantNodes().OfType<XProcessingInstruction>(), pi => pi.Target == "ACE");
        Assert.Equal(1, x.Descendants("Br").Count());
    }
}
