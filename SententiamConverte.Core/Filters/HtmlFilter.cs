using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// HTML: Textblöcke (Absätze, Überschriften, Listen, Tabellenzellen …) und übersetzbare Attribute
/// (alt, title, placeholder, aria-label, meta description). Inline-Elemente werden zu Tags.
/// Skripte, Stile, Code und Elemente mit translate="no" bleiben unberührt.
/// </summary>
public sealed partial class HtmlFilter : IFileFilter
{
    public string Id => "html";
    public string Name => "HTML";
    public IReadOnlyList<string> Extensions { get; } = [".html", ".htm", ".xhtml"];

    private static readonly HashSet<string> Skip = ["SCRIPT", "STYLE", "CODE", "PRE", "SVG", "NOSCRIPT", "TEMPLATE", "MATH", "IFRAME", "OBJECT"];
    private static readonly HashSet<string> PairedInline = ["A", "B", "STRONG", "I", "EM", "U", "SPAN", "SMALL", "SUP", "SUB", "MARK", "ABBR", "CITE", "Q", "S", "DEL", "INS", "FONT", "LABEL", "KBD", "VAR", "DFN", "TIME", "BDI", "BDO"];
    private static readonly HashSet<string> EmptyInline = ["BR", "IMG", "WBR", "INPUT", "SELECT", "TEXTAREA", "BUTTON", "CODE", "SVG", "MATH"];
    private static readonly string[] Attributes = ["alt", "title", "placeholder", "aria-label"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    private static bool IsInline(INode n) => n is IText || n is IComment ||
        (n is IElement e && (PairedInline.Contains(e.TagName) || EmptyInline.Contains(e.TagName)));

    private static bool TranslateNo(IElement e) =>
        e.GetAttribute("translate")?.Equals("no", StringComparison.OrdinalIgnoreCase) == true || e.ClassList.Contains("notranslate");

    /// <summary>Übersetzbare Blöcke in fester Reihenfolge – beim Lesen und Schreiben identisch.</summary>
    private static IEnumerable<IElement> Blocks(IElement root)
    {
        foreach (var child in root.Children)
        {
            if (Skip.Contains(child.TagName) || TranslateNo(child)) continue;
            if (PairedInline.Contains(child.TagName) || EmptyInline.Contains(child.TagName))
                continue; // inline auf oberster Ebene gehört zum Elternblock
            if (child.ChildNodes.All(IsInline))
            {
                if (child.TextContent.Any(char.IsLetter)) yield return child;
            }
            else
            {
                // gemischter Inhalt: Kinder einzeln
                foreach (var b in Blocks(child)) yield return b;
            }
        }
    }

    private static IEnumerable<(IElement Element, string Attribute)> AttributeUnits(IDocument doc)
    {
        foreach (var el in doc.All)
        {
            if (Skip.Contains(el.TagName)) continue;
            foreach (var a in Attributes)
                if (el.GetAttribute(a) is { } v && v.Any(char.IsLetter)) yield return (el, a);
            if (el.TagName == "META" && el.GetAttribute("name")?.ToLowerInvariant() is "description" or "keywords"
                && el.GetAttribute("content") is { } c && c.Any(char.IsLetter))
                yield return (el, "content");
        }
    }

    private static IDocument Parse(Stream input) => new HtmlParser().ParseDocument(input);

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var html = Parse(input);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };

        if (html.Head?.QuerySelector("title") is { } title && title.TextContent.Any(char.IsLetter))
            Add(doc, segmenter, "title", Ws().Replace(title.TextContent, " ").Trim(), new(), "Seitentitel");

        var i = 0;
        foreach (var block in Blocks(html.Body ?? html.DocumentElement))
        {
            var tagData = new Dictionary<int, string>();
            var text = Ws().Replace(ToTagged(block, tagData), " ").Trim();
            Add(doc, segmenter, $"b{i++}", text, tagData, block.TagName.ToLowerInvariant());
        }
        var a = 0;
        foreach (var (el, attr) in AttributeUnits(html))
            Add(doc, segmenter, $"a{a++}", el.GetAttribute(attr)!, new(), $"{el.TagName.ToLowerInvariant()}@{attr}");

        doc.Renumber();
        return doc;
    }

    private static void Add(BilingualDocument doc, Segmenter segmenter, string locator, string text, Dictionary<int, string> tagData, string context)
    {
        if (!Segmenter.HasTranslatableText(text)) return;
        var para = new Paragraph { Locator = locator, Context = context, TagData = tagData };
        segmenter.Segment(para, text);
        doc.Paragraphs.Add(para);
    }

    private static string ToTagged(INode node, Dictionary<int, string> tagData)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText t:
                    sb.Append(t.Data);
                    break;
                case IElement e when PairedInline.Contains(e.TagName) && !TranslateNo(e):
                    var shell = (IElement)e.Clone(false);
                    var outer = shell.OuterHtml;
                    var open = outer[..(outer.LastIndexOf("</", StringComparison.Ordinal) is var k and >= 0 ? k : outer.Length)];
                    var id = Markup.Pair(tagData, open, $"</{e.LocalName}>");
                    sb.Append(TagText.Start(id)).Append(ToTagged(e, tagData)).Append(TagText.End(id));
                    break;
                case IElement e:
                    sb.Append(Markup.Raw(tagData, e.OuterHtml));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var html = Parse(original);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        html.DocumentElement.SetAttribute("lang", document.TargetLanguage);

        if (byLocator.TryGetValue("title", out var tp) && html.Head?.QuerySelector("title") is { } title)
            title.TextContent = TagText.StripTags(tp.JoinTarget()).Trim();

        var i = 0;
        foreach (var block in Blocks(html.Body ?? html.DocumentElement).ToList())
            if (byLocator.TryGetValue($"b{i++}", out var p))
                block.InnerHtml = Markup.Unpack(p.JoinTarget().Trim(), p.TagData, Escape);

        var a = 0;
        foreach (var (el, attr) in AttributeUnits(html).ToList())
            if (byLocator.TryGetValue($"a{a++}", out var p))
                el.SetAttribute(attr, TagText.StripTags(p.JoinTarget()).Trim());

        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        html.ToHtml(writer, HtmlMarkupFormatter.Instance);
    }
}
