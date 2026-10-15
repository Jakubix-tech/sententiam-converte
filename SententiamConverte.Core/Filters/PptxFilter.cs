using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using A = DocumentFormat.OpenXml.Drawing;
using Paragraph = SententiamConverte.Core.Model.Paragraph;

namespace SententiamConverte.Core.Filters;

/// <summary>PowerPoint (.pptx): Folien (auch Tabellen und Formen) und Notizen, in Folienreihenfolge.</summary>
public sealed class PptxFilter : IFileFilter
{
    public string Id => "pptx";
    public string Name => "Microsoft PowerPoint";
    public IReadOnlyList<string> Extensions { get; } = [".pptx", ".pptm", ".ppsx"];

    private static RunContainerCodec CodecFor(string language) => new((props, text) =>
    {
        var run = new A.Run();
        run.RunProperties = props.Length > 0 ? new A.RunProperties(props) : new A.RunProperties();
        run.RunProperties.Language = language;
        run.Text = new A.Text(text);
        return run;
    }, "pPr", "endParaRPr");

    private static IEnumerable<(string Key, OpenXmlPartRootElement Root, string Context)> TextParts(PresentationDocument pres)
    {
        var pp = pres.PresentationPart ?? throw new InvalidDataException("Keine PowerPoint-Präsentation.");
        var ids = pp.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList() ?? [];
        var n = 0;
        foreach (var id in ids)
        {
            n++;
            if (id.RelationshipId?.Value is not { } rid || pp.GetPartById(rid) is not SlidePart slide) continue;
            if (slide.Slide is { } s) yield return (slide.Uri.ToString(), s, $"Folie {n}");
            if (slide.NotesSlidePart?.NotesSlide is { } notes)
                yield return (slide.NotesSlidePart.Uri.ToString(), notes, $"Notizen Folie {n}");
        }
    }

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var pres = PresentationDocument.Open(buffer, false);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        var codec = CodecFor(targetLanguage);
        foreach (var (key, root, context) in TextParts(pres))
        {
            var paras = root.Descendants<A.Paragraph>().ToList();
            for (var i = 0; i < paras.Count; i++)
            {
                var para = new Paragraph { Locator = $"{key}#{i}", Context = context };
                var text = codec.ToTagged(paras[i], para.TagData);
                if (!Segmenter.HasTranslatableText(text)) continue;
                segmenter.Segment(para, text);
                doc.Paragraphs.Add(para);
            }
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var buffer = new MemoryStream();
        original.CopyTo(buffer);
        buffer.Position = 0;
        using (var pres = PresentationDocument.Open(buffer, true))
        {
            var codec = CodecFor(document.TargetLanguage);
            var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
            foreach (var (key, root, _) in TextParts(pres).ToList())
            {
                var paras = root.Descendants<A.Paragraph>().ToList();
                for (var i = 0; i < paras.Count; i++)
                    if (byLocator.TryGetValue($"{key}#{i}", out var p)) codec.Rebuild(paras[i], p.JoinTarget(), p.TagData);
                root.Save();
            }
        }
        buffer.Position = 0;
        buffer.CopyTo(output);
    }
}
