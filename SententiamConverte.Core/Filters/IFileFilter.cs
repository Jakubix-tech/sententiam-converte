using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Liest eine Ausgangsdatei in ein zweisprachiges Dokument und schreibt die Übersetzung
/// wieder ins Originalformat zurück.
/// </summary>
public interface IFileFilter
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }

    BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter);

    /// <summary>Erzeugt die Zieldatei. <paramref name="original"/> ist die unveränderte Ausgangsdatei.</summary>
    void Write(BilingualDocument document, Stream original, Stream output);

    /// <summary>Dateiname der Zieldatei (PDF wird z. B. als DOCX ausgegeben).</summary>
    string TargetFileName(string sourceFileName, string targetLanguage) =>
        Path.GetFileNameWithoutExtension(sourceFileName) + "_" + targetLanguage + Path.GetExtension(sourceFileName);
}

public static class FileFilters
{
    public static readonly IReadOnlyList<IFileFilter> All =
    [
        new DocxFilter(),
        new XlsxFilter(),
        new PptxFilter(),
        new XliffFilter(),
        new PdfFilter(),
        new HtmlFilter(),
        new MarkdownFilter(),
        new ResxFilter(),
        new JsonFilter(),
        new PoFilter(),
        new SubtitleFilter(),
        new TextFilter(),
    ];

    public static IFileFilter? ForFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return All.FirstOrDefault(f => f.Extensions.Contains(ext));
    }

    public static IFileFilter ById(string id) =>
        All.FirstOrDefault(f => f.Id == id) ?? throw new InvalidOperationException($"Unbekannter Dateifilter: {id}");

    public static IEnumerable<string> SupportedExtensions => All.SelectMany(f => f.Extensions);

    /// <summary>Liest einen Stream vollständig in einen seekbaren MemoryStream (Android-Streams sind oft nicht seekbar).</summary>
    internal static MemoryStream Buffer(Stream input)
    {
        var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
