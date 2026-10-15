using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// PDF mit Textebene: Textblöcke werden per Layoutanalyse erkannt und zu Absätzen.
/// Ein PDF lässt sich nicht verlustfrei neu setzen, daher entsteht als Zieldatei ein Word-Dokument
/// (ein Absatz pro Textblock, Seitenumbrüche bleiben). Gescannte PDFs ohne Textebene brauchen vorher OCR.
/// </summary>
public sealed partial class PdfFilter : IFileFilter
{
    public string Id => "pdf";
    public string Name => "PDF (Ausgabe als Word)";
    public IReadOnlyList<string> Extensions { get; } = [".pdf"];

    public string TargetFileName(string sourceFileName, string targetLanguage) =>
        Path.GetFileNameWithoutExtension(sourceFileName) + "_" + targetLanguage + ".docx";

    [GeneratedRegex(@"(\p{Ll})-\s*\n\s*(\p{Ll})")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex LineBreak();

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var pdf = PdfDocument.Open(buffer);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        var anyText = false;

        foreach (var page in pdf.GetPages())
        {
            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
            if (words.Count == 0) continue;
            anyText = true;
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var b = 0;
            foreach (var block in blocks)
            {
                var text = CleanBlock(block.Text);
                b++;
                if (!Segmenter.HasTranslatableText(text)) continue;
                var para = new Paragraph { Locator = $"{page.Number}:{b}", Context = $"Seite {page.Number}" };
                segmenter.Segment(para, text);
                doc.Paragraphs.Add(para);
            }
        }
        if (!anyText)
            throw new InvalidDataException("Das PDF enthält keinen Text (vermutlich gescannt). Bitte vorher eine Texterkennung (OCR) durchführen.");
        doc.Renumber();
        return doc;
    }

    /// <summary>Zeilenumbrüche im Block zu Leerzeichen, Silbentrennung am Zeilenende auflösen.</summary>
    internal static string CleanBlock(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HyphenBreak().Replace(text, "$1$2");
        return LineBreak().Replace(text, " ").Trim();
    }

    /// <summary>Erkannter Text (eine Zeile je Absatz, Seitenmarken „=== Seite n ===“) als Word-Datei zum Übersetzen.</summary>
    public static void OcrTextToDocx(string text, string language, Stream output)
    {
        var paragraphs = new List<(string, bool)>();
        var pageBreak = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("=== Seite"))
            {
                pageBreak = paragraphs.Count > 0;
                continue;
            }
            if (line.Length == 0) continue;
            paragraphs.Add((line, pageBreak));
            pageBreak = false;
        }
        DocxFilter.CreateSimple(output, paragraphs, language);
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var lastPage = "";
        var paragraphs = document.Paragraphs.Select(p =>
        {
            var page = p.Locator.Split(':')[0];
            var pageBreak = lastPage != "" && page != lastPage;
            lastPage = page;
            return (TagText.StripTags(p.JoinTarget()).Trim(), pageBreak);
        }).ToList();
        DocxFilter.CreateSimple(output, paragraphs, document.TargetLanguage);
    }
}
