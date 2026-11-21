using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace SententiamConverte.Core.Tests;

public class FilterTests
{
    /// <summary>Liest eine Datei, ersetzt jede Quelle über die Funktion und schreibt zurück.</summary>
    private static (BilingualDocument Doc, byte[] Output) RoundTrip(IFileFilter filter, byte[] input, string name, Func<string, string> translate)
    {
        var doc = filter.Read(new MemoryStream(input), name, "de-DE", "en-GB", new Segmenter("de-DE"));
        foreach (var s in doc.Segments.Where(s => !s.Locked))
        {
            s.Target = translate(s.Source);
            s.Status = SegmentStatus.Translated;
        }
        var output = new MemoryStream();
        filter.Write(doc, new MemoryStream(input), output);
        return (doc, output.ToArray());
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b).TrimStart('﻿');

    [Fact]
    public void Xlsx()
    {
        var ms = new MemoryStream();
        using (var xl = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = xl.AddWorkbookPart();
            wb.Workbook = new X.Workbook();
            var sst = wb.AddNewPart<SharedStringTablePart>();
            sst.SharedStringTable = new X.SharedStringTable(
                new X.SharedStringItem(new X.Text("Hallo Welt")),
                new X.SharedStringItem(
                    new X.Run(new X.Text("Achtung: ") { Space = SpaceProcessingModeValues.Preserve }),
                    new X.Run(new X.RunProperties(new X.Bold()), new X.Text("heiß"))));
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new X.Worksheet(new X.SheetData(new X.Row(
                new X.Cell { CellReference = "A1", DataType = X.CellValues.SharedString, CellValue = new X.CellValue("0") },
                new X.Cell { CellReference = "B1", DataType = X.CellValues.SharedString, CellValue = new X.CellValue("1") },
                new X.Cell { CellReference = "C1", CellValue = new X.CellValue("42") })));
            wb.Workbook.AppendChild(new X.Sheets(new X.Sheet { Name = "Tabelle1", SheetId = 1, Id = wb.GetIdOfPart(ws) }));
        }
        var (doc, output) = RoundTrip(new XlsxFilter(), ms.ToArray(), "a.xlsx", s => s.Replace("Hallo Welt", "Hello world").Replace("Achtung: ", "Caution: ").Replace("heiß", "hot"));
        Assert.Equal("Tabelle1!A1", doc.Paragraphs[0].Context);
        Assert.Equal("Achtung: ⟨1⟩heiß⟨/1⟩", doc.Paragraphs[1].Segments[0].Source);

        using var result = SpreadsheetDocument.Open(new MemoryStream(output), false);
        var items = result.WorkbookPart!.SharedStringTablePart!.SharedStringTable!.Elements<X.SharedStringItem>().ToList();
        Assert.Equal("Hello world", items[0].InnerText);
        Assert.Equal("Caution: hot", items[1].InnerText);
        Assert.NotNull(items[1].Elements<X.Run>().Last().RunProperties?.GetFirstChild<X.Bold>());
    }

    [Fact]
    public void Pptx()
    {
        var ms = new MemoryStream();
        using (var pres = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
        {
            var pp = pres.AddPresentationPart();
            pp.Presentation = new P.Presentation();
            var slide = pp.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(
                    new A.BodyProperties(),
                    new A.Paragraph(
                        new A.Run(new A.RunProperties { Language = "de-DE" }, new A.Text("Neue ")),
                        new A.Run(new A.RunProperties { Language = "de-DE", Bold = true }, new A.Text("Funktionen")),
                        new A.EndParagraphRunProperties()))))));
            pp.Presentation.SlideIdList = new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = pp.GetIdOfPart(slide) });
        }
        var (doc, output) = RoundTrip(new PptxFilter(), ms.ToArray(), "a.pptx", s => s.Replace("Neue ", "New ").Replace("Funktionen", "features"));
        Assert.Equal("⟨1⟩Neue ⟨/1⟩Funktionen", doc.Segments.Single().Source);  // häufigstes Format ist die Grundformatierung
        Assert.Equal("Folie 1", doc.Paragraphs[0].Context);

        using var result = PresentationDocument.Open(new MemoryStream(output), false);
        var para = result.PresentationPart!.SlideParts.Single().Slide!.Descendants<A.Paragraph>().Single();
        Assert.Equal("New features", para.InnerText);
        Assert.True(para.Elements<A.Run>().Last().RunProperties!.Bold!.Value);
        Assert.Equal("en-GB", para.Elements<A.Run>().First().RunProperties!.Language!.Value);
        Assert.NotNull(para.GetFirstChild<A.EndParagraphRunProperties>());
    }

    [Fact]
    public void Html()
    {
        const string html = """
            <!DOCTYPE html><html lang="de"><head><title>Startseite</title><meta name="description" content="Unsere Produkte"></head>
            <body><h1>Willkommen</h1><p>Das ist <b>wichtig</b>.<br>Neue Zeile mit <a href="/x">Link</a>.</p>
            <img src="a.png" alt="Ein Bild"><script>var x = "nicht übersetzen";</script>
            <ul><li>Erster Punkt</li></ul><p translate="no">Markenname</p></body></html>
            """;
        var (doc, output) = RoundTrip(new HtmlFilter(), Encoding.UTF8.GetBytes(html), "a.html", s => s
            .Replace("Startseite", "Home").Replace("Unsere Produkte", "Our products").Replace("Willkommen", "Welcome")
            .Replace("Das ist ", "This is ").Replace("wichtig", "important").Replace("Neue Zeile mit ", "New line with ")
            .Replace("Link", "link").Replace("Ein Bild", "A picture").Replace("Erster Punkt", "First item"));
        Assert.DoesNotContain(doc.Segments, s => s.Source.Contains("nicht übersetzen") || s.Source.Contains("Markenname"));
        var result = Utf8(output);
        Assert.Contains("<html lang=\"en-GB\">", result);
        Assert.Contains("<title>Home</title>", result);
        Assert.Contains("<h1>Welcome</h1>", result);
        Assert.Contains("This is <b>important</b>.", result);
        Assert.Contains("<a href=\"/x\">link</a>", result);
        Assert.Contains("alt=\"A picture\"", result);
        Assert.Contains("content=\"Our products\"", result);
        Assert.Contains("nicht übersetzen", result);
        Assert.Contains("<li>First item</li>", result);
    }

    [Fact]
    public void Markdown()
    {
        const string md = "# Titel\n\nEin **fetter** Satz mit `code` und\neinem [Link](https://x.y).\n\n```\nCode bleibt\n```\n\n- Punkt eins\n\n| Name | Wert |\n|---|---|\n| Farbe | Rot |\n";
        var (doc, output) = RoundTrip(new MarkdownFilter(), Encoding.UTF8.GetBytes(md), "a.md", s => s
            .Replace("Titel", "Title").Replace("Ein ", "A ").Replace("fetter", "bold").Replace(" Satz mit ", " sentence with ")
            .Replace(" und einem ", " and a ").Replace("Link", "link").Replace("Punkt eins", "Item one")
            .Replace("Name", "Name").Replace("Wert", "Value").Replace("Farbe", "Colour").Replace("Rot", "Red"));
        Assert.Contains(doc.Segments, s => s.Source.StartsWith("Ein ⟨1⟩fetter⟨/1⟩ Satz mit ⟨2/⟩"));
        var result = Utf8(output);
        Assert.Contains("# Title", result);
        Assert.Contains("A **bold** sentence with `code` and a [link](https://x.y).", result);
        Assert.Contains("Code bleibt", result);
        Assert.Contains("- Item one", result);
        Assert.Contains("| Colour | Red |", result);
    }

    [Fact]
    public void Resx()
    {
        const string resx = """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="Greeting" xml:space="preserve"><value>Hallo {0}, willkommen!</value><comment>Begrüßung</comment></data>
              <data name="Icon" type="System.Resources.ResXFileRef"><value>icon.png</value></data>
            </root>
            """;
        var (doc, output) = RoundTrip(new ResxFilter(), Encoding.UTF8.GetBytes(resx), "Strings.resx", s => s.Replace("Hallo ", "Hello ").Replace(", willkommen!", ", welcome!"));
        Assert.Equal("Hallo ⟨1/⟩, willkommen!", doc.Segments.Single().Source);
        Assert.Contains("<value>Hello {0}, welcome!</value>", Utf8(output));
        Assert.Equal("Strings.en-GB.resx", new ResxFilter().TargetFileName("Strings.resx", "en-GB"));
    }

    [Fact]
    public void Json()
    {
        const string json = """{ "menu": { "open": "Öffnen", "count": "%d Dateien" }, "url": "https://example.com", "n": 5, "list": ["Ja", "Nein"] }""";
        var (doc, output) = RoundTrip(new JsonFilter(), Encoding.UTF8.GetBytes(json), "de.json", s => s
            .Replace("Öffnen", "Open").Replace(" Dateien", " files").Replace("Ja", "Yes").Replace("Nein", "No"));
        Assert.Equal(4, doc.Segments.Count());
        var result = Utf8(output);
        Assert.Contains("\"open\": \"Open\"", result);
        Assert.Contains("\"count\": \"%d files\"", result);
        Assert.Contains("https://example.com", result);
        Assert.Contains("\"Yes\"", result);
    }

    [Fact]
    public void Po()
    {
        const string po = "msgid \"\"\nmsgstr \"Content-Type: text/plain; charset=UTF-8\\n\"\n\n#. Knopf\nmsgid \"Save\"\nmsgstr \"\"\n\n#, fuzzy\nmsgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"%d Datei\"\nmsgstr[1] \"%d Dateien\"\n";
        var filter = new PoFilter();
        var doc = filter.Read(new MemoryStream(Encoding.UTF8.GetBytes(po)), "a.po", "en-GB", "de-DE", new Segmenter("en"));
        var segs = doc.Segments.ToList();
        Assert.Equal(3, segs.Count);
        Assert.Equal("Knopf", doc.Paragraphs[0].Context);
        Assert.Equal(SegmentStatus.Draft, segs[1].Status);           // fuzzy
        Assert.Equal("⟨1/⟩ Datei", segs[1].Target);
        segs[0].Target = "Speichern"; segs[0].Status = SegmentStatus.Translated;
        segs[1].Status = SegmentStatus.Translated;
        segs[2].Status = SegmentStatus.Translated;
        var output = new MemoryStream();
        filter.Write(doc, new MemoryStream(Encoding.UTF8.GetBytes(po)), output);
        var result = Utf8(output.ToArray());
        Assert.Contains("msgid \"Save\"\nmsgstr \"Speichern\"", result);
        Assert.Contains("msgstr[1] \"%d Dateien\"", result);
        Assert.DoesNotContain("fuzzy", result);
        Assert.StartsWith("msgid \"\"", result);
    }

    [Fact]
    public void Srt()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:03,000\nGuten Tag.\nWie geht's?\n\n2\n00:00:04,000 --> 00:00:05,000\nDanke.\n";
        var (doc, output) = RoundTrip(new SubtitleFilter(), Encoding.UTF8.GetBytes(srt), "a.srt", s => s
            .Replace("Guten Tag.", "Good day.").Replace("Wie geht's?", "How are you?").Replace("Danke.", "Thanks."));
        Assert.Equal("Guten Tag.⟨1/⟩Wie geht's?", doc.Segments.First().Source);
        Assert.Equal("1\n00:00:01,000 --> 00:00:03,000\nGood day.\nHow are you?\n\n2\n00:00:04,000 --> 00:00:05,000\nThanks.\n", Utf8(output));
    }

    [Fact]
    public void Dita()
    {
        const string topic = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE task PUBLIC "-//OASIS//DTD DITA Task//EN" "task.dtd">
            <task id="pumpe" xml:lang="de-DE">
              <title>Pumpe <ph>einschalten</ph></title>
              <shortdesc>So starten Sie die Pumpe.</shortdesc>
              <taskbody>
                <prereq>Der Tank ist gefüllt.</prereq>
                <steps>
                  <step><cmd>Klicken Sie auf <uicontrol>Start</uicontrol>.</cmd>
                    <info>Die Anzeige wird grün. Siehe <xref href="wartung.dita">Wartung</xref>.</info></step>
                </steps>
                <example>
                  <codeblock>pumpe --start  # nicht übersetzen</codeblock>
                  <p translate="no">Markenname</p>
                  <note>Hinweis vorab:<ul><li>Erster Punkt</li></ul></note>
                  <lines>Zeile eins
            Zeile zwei</lines>
                  <fig><image href="pumpe.png"><alt>Die Pumpe</alt></image></fig>
                  <p>Mit <codeph>--start</codeph> und <image href="i.png" alt="Symbol"/> starten.</p>
                </example>
              </taskbody>
            </task>
            """;
        var filter = new DitaFilter();
        var (doc, output) = RoundTrip(filter, Encoding.UTF8.GetBytes(topic), "pumpe.dita", s => s
            .Replace("Pumpe ", "Switch on ").Replace("einschalten", "the pump").Replace("So starten Sie die Pumpe.", "How to start the pump.")
            .Replace("Der Tank ist gefüllt.", "The tank is full.").Replace("Klicken Sie auf ", "Click ").Replace("Start", "Start")
            .Replace("Die Anzeige wird grün.", "The display turns green.").Replace("Siehe ", "See ").Replace("Wartung", "Maintenance")
            .Replace("Hinweis vorab:", "Note first:").Replace("Erster Punkt", "First item").Replace("Zeile eins", "Line one")
            .Replace("Zeile zwei", "Line two").Replace("Die Pumpe", "The pump").Replace("Mit ", "Use ").Replace(" und ", " and ")
            .Replace(" starten.", " to start.").Replace("Symbol", "Icon"));

        Assert.DoesNotContain(doc.Segments, s => s.Source.Contains("nicht übersetzen") || s.Source.Contains("Markenname") || s.Source.Contains("--start"));
        Assert.Contains(doc.Segments, s => s.Source == "Hinweis vorab:");                   // Text neben einer Liste geht nicht verloren
        Assert.Contains(doc.Paragraphs, p => p.Context == "task/title");
        Assert.Equal("pumpe.dita", filter.TargetFileName("pumpe.dita", "en-GB"));           // Verweise zwischen Dateien bleiben gültig

        var result = Utf8(output);
        var xml = System.Xml.Linq.XDocument.Parse(result);                                 // wohlgeformt
        Assert.Contains("<!DOCTYPE task PUBLIC \"-//OASIS//DTD DITA Task//EN\" \"task.dtd\">", result);
        Assert.Equal("en-GB", xml.Root!.Attribute(System.Xml.Linq.XNamespace.Xml + "lang")!.Value);
        Assert.Contains("<title>Switch on <ph>the pump</ph></title>", result);
        Assert.Contains("<cmd>Click <uicontrol>Start</uicontrol>.</cmd>", result);
        Assert.Contains("See <xref href=\"wartung.dita\">Maintenance</xref>.", result);
        Assert.Contains("<codeblock>pumpe --start  # nicht übersetzen</codeblock>", result);
        Assert.Contains("<p translate=\"no\">Markenname</p>", result);
        Assert.Contains("<note>Note first:<ul><li>First item</li></ul></note>", result);
        Assert.Contains("<lines>Line one\nLine two</lines>", result.Replace("\r\n", "\n"));
        Assert.Contains("<alt>The pump</alt>", result);
        Assert.Contains("<p>Use <codeph>--start</codeph> and <image href=\"i.png\" alt=\"Icon\" /> to start.</p>", result);
    }

    [Fact]
    public void DitaMap()
    {
        const string map = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE map PUBLIC "-//OASIS//DTD DITA Map//EN" "map.dtd">
            <map xml:lang="de-DE">
              <title>Handbuch</title>
              <topicref href="pumpe.dita" navtitle="Pumpe einschalten">
                <topicmeta><navtitle>Pumpe einschalten</navtitle></topicmeta>
              </topicref>
            </map>
            """;
        var (doc, output) = RoundTrip(new DitaFilter(), Encoding.UTF8.GetBytes(map), "handbuch.ditamap", s => s
            .Replace("Handbuch", "Manual").Replace("Pumpe einschalten", "Switch on the pump"));
        Assert.Equal(3, doc.Segments.Count());
        var result = Utf8(output);
        Assert.Contains("<title>Manual</title>", result);
        Assert.Contains("href=\"pumpe.dita\" navtitle=\"Switch on the pump\"", result);
        Assert.Contains("<navtitle>Switch on the pump</navtitle>", result);
        Assert.Same(FileFilters.ForFile("x.ditamap"), FileFilters.ForFile("y.dita"));
    }
}
