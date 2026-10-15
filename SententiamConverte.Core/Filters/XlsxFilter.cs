using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using Paragraph = SententiamConverte.Core.Model.Paragraph;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Excel (.xlsx): Texte aus der Tabelle gemeinsamer Zeichenfolgen und Inline-Texte in Zellen.
/// Zahlen, Datumswerte und Formeln bleiben unberührt; Zellformatierung innerhalb eines Textes wird zu Tags.
/// </summary>
public sealed class XlsxFilter : IFileFilter
{
    public string Id => "xlsx";
    public string Name => "Microsoft Excel";
    public IReadOnlyList<string> Extensions { get; } = [".xlsx", ".xlsm"];

    private static readonly RunContainerCodec Codec = new((props, text) =>
    {
        var run = new Run();
        if (props.Length > 0) run.RunProperties = new RunProperties(props);
        run.Text = new Text(text) { Space = SpaceProcessingModeValues.Preserve };
        return run;
    }, "phoneticPr", "rPh");

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var xl = SpreadsheetDocument.Open(buffer, false);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        var wb = xl.WorkbookPart ?? throw new InvalidDataException("Keine Excel-Arbeitsmappe.");
        var context = SharedStringContext(wb);

        if (wb.SharedStringTablePart?.SharedStringTable is { } sst)
        {
            var items = sst.Elements<SharedStringItem>().ToList();
            for (var i = 0; i < items.Count; i++)
                AddParagraph(doc, segmenter, items[i], $"s#{i}", context.GetValueOrDefault(i));
        }
        foreach (var (sheetName, part) in Sheets(wb))
            foreach (var cell in part.Worksheet!.Descendants<Cell>().Where(c => c.InlineString != null))
                AddParagraph(doc, segmenter, cell.InlineString!, $"{part.Uri}#{cell.CellReference}", $"{sheetName}!{cell.CellReference}");

        doc.Renumber();
        return doc;
    }

    private static void AddParagraph(BilingualDocument doc, Segmenter segmenter, OpenXmlElement container, string locator, string? context)
    {
        var para = new Paragraph { Locator = locator, Context = context };
        var text = Codec.ToTagged(container, para.TagData);
        if (!Segmenter.HasTranslatableText(text)) return;
        segmenter.Segment(para, text);
        doc.Paragraphs.Add(para);
    }

    private static IEnumerable<(string Name, WorksheetPart Part)> Sheets(WorkbookPart wb)
    {
        foreach (var sheet in wb.Workbook?.Sheets?.Elements<Sheet>() ?? [])
            if (sheet.Id?.Value is { } rid && wb.GetPartById(rid) is WorksheetPart wp)
                yield return (sheet.Name?.Value ?? "", wp);
    }

    /// <summary>Wo eine gemeinsame Zeichenfolge zuerst benutzt wird („Tabelle1!B3“) – als Kontext im Editor.</summary>
    private static Dictionary<int, string> SharedStringContext(WorkbookPart wb)
    {
        var map = new Dictionary<int, string>();
        foreach (var (name, part) in Sheets(wb))
            foreach (var cell in part.Worksheet!.Descendants<Cell>())
                if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out var idx))
                    map.TryAdd(idx, $"{name}!{cell.CellReference}");
        return map;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var buffer = new MemoryStream();
        original.CopyTo(buffer);
        buffer.Position = 0;
        using (var xl = SpreadsheetDocument.Open(buffer, true))
        {
            var wb = xl.WorkbookPart!;
            var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
            if (wb.SharedStringTablePart?.SharedStringTable is { } sst)
            {
                var items = sst.Elements<SharedStringItem>().ToList();
                for (var i = 0; i < items.Count; i++)
                    if (byLocator.TryGetValue($"s#{i}", out var p)) Codec.Rebuild(items[i], p.JoinTarget(), p.TagData);
                sst.Save();
            }
            foreach (var (_, part) in Sheets(wb))
            {
                var changed = false;
                foreach (var cell in part.Worksheet!.Descendants<Cell>().Where(c => c.InlineString != null))
                    if (byLocator.TryGetValue($"{part.Uri}#{cell.CellReference}", out var p))
                    {
                        Codec.Rebuild(cell.InlineString!, p.JoinTarget(), p.TagData);
                        changed = true;
                    }
                if (changed) part.Worksheet!.Save();
            }
        }
        buffer.Position = 0;
        buffer.CopyTo(output);
    }
}
