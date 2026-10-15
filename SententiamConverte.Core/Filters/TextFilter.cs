using System.Text;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>Reiner Text: jede Zeile ist ein Absatz.</summary>
public sealed class TextFilter : IFileFilter
{
    public string Id => "txt";
    public string Name => "Textdatei";
    public IReadOnlyList<string> Extensions { get; } = [".txt"];

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Segmenter.HasTranslatableText(lines[i])) continue;
            var p = new Paragraph { Locator = i.ToString() };
            segmenter.Segment(p, lines[i]);
            doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var reader = new StreamReader(original, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var p in document.Paragraphs)
            lines[int.Parse(p.Locator)] = TagText.StripTags(p.JoinTarget());
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(string.Join(newline, lines));
    }
}
