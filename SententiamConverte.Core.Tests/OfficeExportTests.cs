using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>Word und OpenDocument: Formatierung, Lage, Felder und Grafiken bleiben beim Export wie in der Quelle.</summary>
public class OfficeExportTests
{
    private const string Ns =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" mc:Ignorable=\"wps\"";

    private static byte[] Docx(string body)
    {
        var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            using var w = new StreamWriter(main.GetStream(FileMode.Create), new UTF8Encoding(false));
            w.Write($"<w:document {Ns}><w:body>{body}</w:body></w:document>");
        }
        return ms.ToArray();
    }

    private static string DocumentXml(byte[] docx)
    {
        using var word = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return word.MainDocumentPart!.Document!.OuterXml;
    }

    private static (BilingualDocument Doc, byte[] Output) RoundTrip(IFileFilter filter, byte[] input, Func<string, string> translate)
    {
        var doc = filter.Read(new MemoryStream(input), "x", "de-DE", "en-GB", new Segmenter("de-DE"));
        foreach (var s in doc.Segments.Where(s => !s.Locked)) s.Target = translate(s.Source);
        var output = new MemoryStream();
        filter.Write(doc, new MemoryStream(input), output);
        return (doc, output.ToArray());
    }

    [Fact]
    public void PageNumberFieldStaysOneUntouchedRun()
    {
        const string field = "<w:r><w:rPr><w:sz w:val=\"15\"/></w:rPr><w:fldChar w:fldCharType=\"begin\"/><w:instrText xml:space=\"preserve\"> PAGE </w:instrText>"
                             + "<w:fldChar w:fldCharType=\"separate\"/><w:t>1</w:t><w:fldChar w:fldCharType=\"end\"/></w:r>";
        var docx = Docx($"<w:p><w:r><w:rPr><w:sz w:val=\"15\"/></w:rPr><w:t xml:space=\"preserve\">Seite </w:t></w:r>{field}</w:p>");
        var (doc, output) = RoundTrip(new DocxFilter(), docx, s => s.Replace("Seite", "Page"));

        Assert.Equal("Seite " + TagText.Placeholder(1), doc.Paragraphs[0].JoinSource());   // die Seitenzahl ist kein Text zum Übersetzen
        var xml = DocumentXml(output);
        Assert.Contains("Page </w:t>", xml);
        var run = XElement.Parse(xml).Descendants().Single(e => e.Name.LocalName == "r" && e.Descendants().Any(d => d.Name.LocalName == "instrText"));
        Assert.Equal(["rPr", "fldChar", "instrText", "fldChar", "t", "fldChar"], run.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void ContentControlsAreTranslatedAndKeepTheirProperties()
    {
        var docx = Docx("<w:p><w:r><w:t xml:space=\"preserve\">Name: </w:t></w:r><w:sdt><w:sdtPr><w:alias w:val=\"Vollständiger Name\"/><w:tag w:val=\"name\"/></w:sdtPr>"
                        + "<w:sdtContent><w:r><w:rPr><w:b/></w:rPr><w:t>Ihr Name hier</w:t></w:r></w:sdtContent></w:sdt></w:p>");
        var (doc, output) = RoundTrip(new DocxFilter(), docx, s => s.Replace("Ihr Name hier", "Your name here"));

        Assert.Contains("Ihr Name hier", doc.Paragraphs[0].JoinSource());
        var sdt = XElement.Parse(DocumentXml(output)).Descendants().Single(e => e.Name.LocalName == "sdt");
        Assert.Contains("w:val=\"Vollständiger Name\"", sdt.ToString());
        Assert.Contains("w:val=\"name\"", sdt.ToString());
        var content = sdt.Elements().Single(e => e.Name.LocalName == "sdtContent");
        Assert.Equal("Your name here", content.Value);
        Assert.Contains(content.Descendants(), e => e.Name.LocalName == "b");   // fett wie im Original
    }

    [Fact]
    public void TextBoxIsTranslatedOnceAndBothCopiesAreUpdated()
    {
        const string box = "<w:txbxContent><w:p><w:r><w:t>Kontakt und Adresse</w:t></w:r></w:p></w:txbxContent>";
        var docx = Docx("<w:p><w:r><mc:AlternateContent>"
                        + $"<mc:Choice Requires=\"wps\"><w:drawing><wp:inline><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx>{box}</wps:txbx></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice>"
                        + $"<mc:Fallback><w:pict><v:shape><v:textbox>{box}</v:textbox></v:shape></w:pict></mc:Fallback>"
                        + "</mc:AlternateContent></w:r></w:p><w:p><w:r><w:t>Haupttext.</w:t></w:r></w:p>");
        var (doc, output) = RoundTrip(new DocxFilter(), docx, s => s.Replace("Kontakt und Adresse", "Contact and address").Replace("Haupttext.", "Main text."));

        Assert.Single(doc.Paragraphs, p => p.JoinSource() == "Kontakt und Adresse");
        var xml = DocumentXml(output);
        Assert.DoesNotContain("Kontakt", xml);
        Assert.Equal(2, xml.Split("Contact and address").Length - 1);                   // moderne Form und Ersatzdarstellung
        Assert.Contains("Main text.", xml);
        Assert.Contains("v:textbox", xml);
    }

    [Fact]
    public void UnchangedOdtComesBackIdentical()
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3"><office:body><office:text><text:p text:style-name="P1"><draw:frame draw:name="Bild1" svg:width="2cm" svg:height="2cm" text:anchor-type="paragraph"><draw:image xlink:href="Pictures/bild.png"/></draw:frame>Kopf <text:span text:style-name="T1">fett</text:span> und weit.</text:p><text:p><draw:frame draw:name="Rahmen1" text:anchor-type="page" svg:x="12cm" svg:y="2cm"><draw:text-box><text:p text:style-name="Seitenleiste">Kontakt<text:tab/>Berlin</text:p></draw:text-box></draw:frame></text:p></office:text></office:body></office:document-content>
            """;
        const string styles = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" office:version="1.3"><office:master-styles><style:master-page style:name="Standard"><style:footer><text:p text:style-name="Footer">Seite <text:page-number text:select-page="current">1</text:page-number> von <text:page-count>2</text:page-count></text:p></style:footer></style:master-page></office:master-styles></office:document-styles>
            """;
        var odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", content), ("styles.xml", styles), ("Pictures/bild.png", "png"));
        var (doc, output) = RoundTrip(new OdfFilter(), odt, s => s);

        Assert.Contains(doc.Paragraphs, p => p.JoinSource() == "Kontakt\tBerlin");
        Assert.Contains(doc.Paragraphs, p => p.Context == "Kopf-/Fußzeile");
        static string Xml(string x) => XDocument.Parse(x, LoadOptions.PreserveWhitespace).ToString(SaveOptions.DisableFormatting);
        Assert.Equal(Xml(content), Xml(Entry(output, "content.xml")));
        Assert.Equal(Xml(styles), Xml(Entry(output, "styles.xml")));
        Assert.Equal("png", Entry(output, "Pictures/bild.png"));
    }

    [Fact]
    public void OdtWhitespaceFollowsTheOdfRules()
    {
        // so schreiben andere Programme manchmal: eingerückt und mit rohen Mehrfach-Leerzeichen
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" office:version="1.3"><office:body><office:text><text:p>
                Erster   Satz <text:span>mit</text:span>   Format.<text:s text:c="2"/>Danach.
              </text:p></office:text></office:body></office:document-content>
            """;
        var odt = Zip(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", content));
        var (doc, output) = RoundTrip(new OdfFilter(), odt, s => s.Replace("Erster", "First"));
        Assert.Equal("Erster Satz ⟨1⟩mit⟨/1⟩ Format.  Danach. ", doc.Paragraphs[0].JoinSource());
        Assert.Contains("<text:p>First Satz <text:span>mit</text:span> Format. <text:s />Danach. </text:p>", Entry(output, "content.xml"));
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                var e = zip.CreateEntry(name, name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static string Entry(byte[] zip, string name)
    {
        using var z = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }
}
