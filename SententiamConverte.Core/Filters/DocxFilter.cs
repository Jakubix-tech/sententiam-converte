using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using WParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Paragraph = SententiamConverte.Core.Model.Paragraph;
using Languages = DocumentFormat.OpenXml.Wordprocessing.Languages;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Word (.docx): Haupttext, Tabellen, Kopf-/Fußzeilen, Fuß-/Endnoten und Textfelder.
/// Abweichende Zeichenformate werden zu Tag-Paaren, Bilder/Felder/Umbrüche zu Platzhaltern.
/// Beim Zurückschreiben werden die Original-Elemente (Bilder usw.) unverändert wieder eingesetzt.
/// Änderungsverfolgung: Einfügungen werden übernommen, Löschungen verworfen.
/// </summary>
public sealed class DocxFilter : IFileFilter
{
    public string Id => "docx";
    public string Name => "Microsoft Word";
    public IReadOnlyList<string> Extensions { get; } = [".docx", ".docm", ".dotx"];

    // ---------- Zerlegen eines Absatzes in eine Folge von Bausteinen ----------

    private enum TokKind { Text, RunChild, Element, HyperlinkStart, HyperlinkEnd, Keep }

    private sealed record Tok(TokKind Kind, int Ord, int Child = -1, string RunProps = "", string Text = "");

    private sealed class Walk
    {
        public readonly List<Tok> Tokens = new();
        public readonly List<Run> Runs = new();
        public readonly List<List<OpenXmlElement>> RunChildren = new();
        public readonly List<OpenXmlElement> Elements = new();
        public readonly List<Hyperlink> Hyperlinks = new();
        public readonly List<OpenXmlElement> Kept = new();
    }

    private static Walk WalkParagraph(WParagraph p)
    {
        var w = new Walk();
        WalkContainer(p, w);
        return w;
    }

    private static void WalkContainer(OpenXmlElement container, Walk w)
    {
        foreach (var child in container.ChildElements.ToList())
        {
            switch (child)
            {
                case ParagraphProperties:
                case ProofError:
                case DeletedRun:
                    break;
                case Run run:
                    WalkRun(run, w);
                    break;
                case Hyperlink h:
                    var hOrd = w.Hyperlinks.Count;
                    w.Hyperlinks.Add(h);
                    w.Tokens.Add(new Tok(TokKind.HyperlinkStart, hOrd));
                    WalkContainer(h, w);
                    w.Tokens.Add(new Tok(TokKind.HyperlinkEnd, hOrd));
                    break;
                case InsertedRun or CustomXmlRun:
                    WalkContainer(child, w);
                    break;
                case BookmarkStart or BookmarkEnd:
                    w.Tokens.Add(new Tok(TokKind.Keep, w.Kept.Count));
                    w.Kept.Add(child);
                    break;
                default:
                    w.Tokens.Add(new Tok(TokKind.Element, w.Elements.Count));
                    w.Elements.Add(child);
                    break;
            }
        }
    }

    private static void WalkRun(Run run, Walk w)
    {
        var ord = w.Runs.Count;
        w.Runs.Add(run);
        var children = run.ChildElements.Where(c => c is not RunProperties).ToList();
        w.RunChildren.Add(children);
        var rPr = run.RunProperties?.OuterXml ?? "";
        for (var i = 0; i < children.Count; i++)
        {
            switch (children[i])
            {
                case Text t:
                    w.Tokens.Add(new Tok(TokKind.Text, ord, i, rPr, t.Text));
                    break;
                case TabChar:
                    w.Tokens.Add(new Tok(TokKind.Text, ord, i, rPr, "\t"));
                    break;
                case NoBreakHyphen:
                    w.Tokens.Add(new Tok(TokKind.Text, ord, i, rPr, "‑"));
                    break;
                case SoftHyphen or LastRenderedPageBreak:
                    break;
                default:
                    w.Tokens.Add(new Tok(TokKind.RunChild, ord, i, rPr));
                    break;
            }
        }
    }

    // ---------- Lesen ----------

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var word = WordprocessingDocument.Open(buffer, false);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };

        foreach (var (key, root, context) in TextParts(word))
        {
            var paragraphs = root.Descendants<WParagraph>().ToList();
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var para = new Paragraph { Locator = $"{key}#{i}", Context = context };
                var text = ToTagged(WalkParagraph(paragraphs[i]), para.TagData);
                if (!Segmenter.HasTranslatableText(text)) continue;
                segmenter.Segment(para, text);
                if (paragraphs[i].ParagraphProperties?.ParagraphStyleId?.Val?.Value is { } style)
                    para.Context = string.IsNullOrEmpty(para.Context) ? style : $"{para.Context} · {style}";
                doc.Paragraphs.Add(para);
            }
        }
        doc.Renumber();
        return doc;
    }

    private static IEnumerable<(string Key, OpenXmlPartRootElement Root, string? Context)> TextParts(WordprocessingDocument word)
    {
        var main = word.MainDocumentPart ?? throw new InvalidDataException("Kein Word-Dokument.");
        if (main.Document is { } d) yield return (main.Uri.ToString(), d, null);
        foreach (var h in main.HeaderParts.OrderBy(p => p.Uri.ToString()))
            if (h.Header is { } r) yield return (h.Uri.ToString(), r, "Kopfzeile");
        foreach (var f in main.FooterParts.OrderBy(p => p.Uri.ToString()))
            if (f.Footer is { } r) yield return (f.Uri.ToString(), r, "Fußzeile");
        if (main.FootnotesPart?.Footnotes is { } fn) yield return (main.FootnotesPart.Uri.ToString(), fn, "Fußnote");
        if (main.EndnotesPart?.Endnotes is { } en) yield return (main.EndnotesPart.Uri.ToString(), en, "Endnote");
    }

    /// <summary>
    /// TagData-Schlüssel: 0 = Grundformat ("b:"+rPr), sonst "f:"+rPr (Format), "h:"+Nr (Hyperlink),
    /// "p:"+Liste (Platzhalter aus "r{Lauf}.{Kind}" bzw. "e{Element}").
    /// </summary>
    private static string ToTagged(Walk w, Dictionary<int, string> tagData)
    {
        var baseProps = w.Tokens.Where(t => t.Kind == TokKind.Text)
            .GroupBy(t => t.RunProps).OrderByDescending(g => g.Sum(t => t.Text.Length))
            .Select(g => g.Key).FirstOrDefault() ?? "";
        tagData[0] = "b:" + baseProps;

        var sb = new StringBuilder();
        var nextId = 1;
        int? openFormat = null;
        string? openProps = null;
        List<string>? pending = null;

        void FlushPlaceholder()
        {
            if (pending == null) return;
            var id = nextId++;
            tagData[id] = "p:" + string.Join(',', pending);
            sb.Append(TagText.Placeholder(id));
            pending = null;
        }
        void CloseFormat()
        {
            if (openFormat == null) return;
            sb.Append(TagText.End(openFormat.Value));
            openFormat = null;
            openProps = null;
        }

        foreach (var t in w.Tokens)
        {
            switch (t.Kind)
            {
                case TokKind.Text:
                    FlushPlaceholder();
                    var props = t.RunProps == baseProps ? null : t.RunProps;
                    if (props != openProps)
                    {
                        CloseFormat();
                        if (props != null)
                        {
                            openFormat = nextId++;
                            openProps = props;
                            tagData[openFormat.Value] = "f:" + props;
                            sb.Append(TagText.Start(openFormat.Value));
                        }
                    }
                    sb.Append(t.Text);
                    break;
                case TokKind.RunChild:
                    (pending ??= new()).Add($"r{t.Ord}.{t.Child}");
                    break;
                case TokKind.Element:
                    (pending ??= new()).Add($"e{t.Ord}");
                    break;
                case TokKind.HyperlinkStart:
                    FlushPlaceholder();
                    CloseFormat();
                    var hid = nextId++;
                    tagData[hid] = "h:" + t.Ord;
                    sb.Append(TagText.Start(hid));
                    break;
                case TokKind.HyperlinkEnd:
                    FlushPlaceholder();
                    CloseFormat();
                    var startId = tagData.Last(kv => kv.Value == "h:" + t.Ord).Key;
                    sb.Append(TagText.End(startId));
                    break;
                case TokKind.Keep:
                    break;
            }
        }
        FlushPlaceholder();
        CloseFormat();
        return sb.ToString();
    }

    // ---------- Schreiben ----------

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var buffer = new MemoryStream();
        original.CopyTo(buffer);
        buffer.Position = 0;
        using (var word = WordprocessingDocument.Open(buffer, true))
        {
            var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
            foreach (var (key, root, _) in TextParts(word).ToList())
            {
                var paragraphs = root.Descendants<WParagraph>().ToList();
                for (var i = 0; i < paragraphs.Count; i++)
                    if (byLocator.TryGetValue($"{key}#{i}", out var para))
                        Rebuild(paragraphs[i], para, document.TargetLanguage);
                root.Save();
            }
            SetDocumentLanguage(word, document.TargetLanguage);
        }
        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    private static void Rebuild(WParagraph p, Paragraph para, string targetLanguage)
    {
        var w = WalkParagraph(p);
        var baseProps = para.TagData.TryGetValue(0, out var b) && b.StartsWith("b:") ? b[2..] : "";

        foreach (var child in p.ChildElements.Where(c => c is not ParagraphProperties).ToList())
            child.Remove();

        var containers = new Stack<(int Id, OpenXmlElement El)>();
        containers.Push((0, p));
        var formats = new Stack<(int Id, string Props)>();
        var usedPlaceholders = new HashSet<int>();

        void AppendText(string text)
        {
            var props = formats.Count > 0 ? formats.Peek().Props : baseProps;
            var run = new Run();
            run.RunProperties = MakeProps(props, targetLanguage);
            var parts = text.Split('\t');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) run.AppendChild(new TabChar());
                if (parts[i].Length > 0) run.AppendChild(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            containers.Peek().El.AppendChild(run);
        }

        void AppendPlaceholder(int id)
        {
            if (!para.TagData.TryGetValue(id, out var data) || !data.StartsWith("p:")) return;
            if (!usedPlaceholders.Add(id)) return;
            foreach (var reference in data[2..].Split(','))
            {
                if (reference.StartsWith('e'))
                {
                    var el = w.Elements[int.Parse(reference[1..])];
                    el.Remove();
                    containers.Peek().El.AppendChild(el);
                }
                else
                {
                    var dot = reference.IndexOf('.');
                    var runOrd = int.Parse(reference[1..dot]);
                    var child = w.RunChildren[runOrd][int.Parse(reference[(dot + 1)..])];
                    child.Remove();
                    var run = new Run();
                    if (w.Runs[runOrd].RunProperties is { } rp) run.RunProperties = (RunProperties)rp.CloneNode(true);
                    run.AppendChild(child);
                    containers.Peek().El.AppendChild(run);
                }
            }
        }

        foreach (var part in TagText.Split(para.JoinTarget()))
        {
            if (part.Text != null)
            {
                AppendText(part.Text);
                continue;
            }
            var tag = part.Tag!.Value;
            para.TagData.TryGetValue(tag.Id, out var data);
            switch (tag.Kind)
            {
                case TagText.TagKind.Start when data?.StartsWith("f:") == true:
                    formats.Push((tag.Id, data[2..]));
                    break;
                case TagText.TagKind.Start when data?.StartsWith("h:") == true:
                    var link = (Hyperlink)w.Hyperlinks[int.Parse(data[2..])].CloneNode(false);
                    containers.Peek().El.AppendChild(link);
                    containers.Push((tag.Id, link));
                    break;
                case TagText.TagKind.End:
                    if (formats.Any(f => f.Id == tag.Id))
                        while (formats.Count > 0 && formats.Pop().Id != tag.Id) { }
                    else if (containers.Any(c => c.Id == tag.Id && c.Id != 0))
                        while (containers.Count > 1 && containers.Pop().Id != tag.Id) { }
                    break;
                case TagText.TagKind.Placeholder:
                    AppendPlaceholder(tag.Id);
                    break;
            }
        }

        // Platzhalter, die im Ziel fehlen (Bilder, Felder), nicht verlieren – ans Absatzende.
        while (containers.Count > 1) containers.Pop();
        foreach (var id in para.TagData.Where(kv => kv.Value.StartsWith("p:")).Select(kv => kv.Key).ToList())
            AppendPlaceholder(id);

        // Textmarken wieder einsetzen: Anfänge nach den Absatzeigenschaften, Enden ans Ende.
        var anchor = (OpenXmlElement?)p.ParagraphProperties;
        foreach (var k in w.Kept)
        {
            k.Remove();
            if (k is BookmarkStart)
            {
                if (anchor == null) p.PrependChild(k); else anchor.InsertAfterSelf(k);
                anchor = k;
            }
            else p.AppendChild(k);
        }
    }

    private static RunProperties? MakeProps(string xml, string targetLanguage)
    {
        var props = xml.Length > 0 ? new RunProperties(xml) : new RunProperties();
        props.Languages = new Languages { Val = targetLanguage };
        return props;
    }

    private static void SetDocumentLanguage(WordprocessingDocument word, string language)
    {
        var styles = word.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        var rPr = styles?.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        if (rPr == null) return;
        rPr.Languages ??= new Languages();
        rPr.Languages.Val = language;
        styles!.Save();
    }

    public string TargetFileName(string sourceFileName, string targetLanguage) =>
        Path.GetFileNameWithoutExtension(sourceFileName) + "_" + targetLanguage + Path.GetExtension(sourceFileName);

    /// <summary>Neues Word-Dokument aus reinen Absätzen (für PDF-Ausgabe und Exporte).</summary>
    public static void CreateSimple(Stream output, IEnumerable<(string Text, bool PageBreakBefore)> paragraphs, string language)
    {
        using var buffer = new MemoryStream();
        using (var word = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new Body();
            foreach (var (text, pageBreak) in paragraphs)
            {
                var p = new WParagraph();
                if (pageBreak) p.ParagraphProperties = new ParagraphProperties(new PageBreakBefore());
                var run = new Run(new RunProperties(new Languages { Val = language }));
                run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                p.AppendChild(run);
                body.AppendChild(p);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        buffer.Position = 0;
        buffer.CopyTo(output);
    }
}
