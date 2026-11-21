using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// DITA (Topics und Maps): Textblöcke wie Titel, Absätze, Listenpunkte, Tabellenzellen und Arbeitsschritte;
/// Auszeichnungen im Satz (Hervorhebung, Verweise, Programmelemente …) werden zu Tags. Code, Entwurfskommentare und
/// alles mit translate="no" bleibt unberührt. Die Zieldatei behält ihren Namen, damit Verweise zwischen Map und
/// Topics weiter stimmen (sie gehört in einen eigenen Ordner je Sprache).
/// </summary>
public sealed partial class DitaFilter : IFileFilter
{
    public string Id => "dita";
    public string Name => "DITA";
    public IReadOnlyList<string> Extensions { get; } = [".dita", ".ditamap"];

    public string TargetFileName(string sourceFileName, string targetLanguage) => Path.GetFileName(sourceFileName);

    /// <summary>Wird nicht übersetzt.</summary>
    private static readonly HashSet<string> Skip = ["codeblock", "draft-comment", "required-cleanup", "data", "data-about", "unknown",
        "foreign", "object", "svg-container", "mathml", "msgblock", "coderef", "prolog", "resourceid"];

    /// <summary>Auszeichnungen im Satz: werden zu Tag-Paaren, der Text darin wird übersetzt.</summary>
    private static readonly HashSet<string> PairedInline = ["b", "i", "u", "sup", "sub", "tt", "line-through", "overline", "ph", "keyword",
        "term", "xref", "cite", "q", "uicontrol", "wintitle", "menucascade", "filepath", "userinput", "systemoutput", "varname",
        "apiname", "option", "parmname", "cmdname", "msgph", "msgnum", "tm", "indexterm", "fn", "synph", "kwd", "var", "abbreviated-form",
        "text", "dfn", "shortcut", "sort-as"];

    /// <summary>Im Satz, aber wörtlich zu übernehmen.</summary>
    private static readonly HashSet<string> RawInline = ["codeph", "image", "draft-comment", "required-cleanup", "data", "data-about",
        "unknown", "foreign", "keyword-ref", "xmlelement", "xmlatt", "textentity", "parameterentity", "numcharref", "xmlnsname", "xmlpi"];

    /// <summary>Immer eigene Blöcke (auch wenn sie neben Text stehen).</summary>
    private static readonly HashSet<string> Block = ["title", "shortdesc", "p", "li", "sli", "dt", "dd", "entry", "stentry", "note",
        "cmd", "info", "stepresult", "stepxmp", "result", "context", "prereq", "postreq", "navtitle", "linktext", "searchtitle", "desc",
        "lq", "section", "example", "ul", "ol", "sl", "dl", "dlentry", "table", "tgroup", "thead", "tbody", "row", "simpletable",
        "sthead", "strow", "fig", "step", "steps", "steps-unordered", "substep", "substeps", "choices", "choice", "abstract", "body",
        "conbody", "taskbody", "refbody", "topicref", "topicmeta", "related-links", "link", "linkpool", "linklist", "pre", "lines", "screen",
        "alt", "glossterm", "glossdef", "troubleshooting", "cause", "remedy", "responsibleParty"];

    /// <summary>Übersetzbare Attribute.</summary>
    private static readonly string[] Attributes = ["navtitle", "alt", "title"];

    /// <summary>Hier zählen Zeilenumbrüche und Leerzeichen.</summary>
    private static readonly HashSet<string> Preformatted = ["pre", "lines", "screen"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    /// <summary>Zusammenhängende Inhalte, die zu einem Absatz werden – beim Lesen und Schreiben in gleicher Reihenfolge.</summary>
    private sealed record Unit(XElement Parent, List<XNode> Nodes);

    private static bool TranslateNo(XElement e) => e.Attribute("translate")?.Value.Equals("no", StringComparison.OrdinalIgnoreCase) == true;

    private static string Local(XElement e) => e.Name.LocalName;

    private static bool HasText(XElement e) => e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));

    private static bool IsInline(XNode n, bool parentHasText) => n switch
    {
        XText or XComment or XProcessingInstruction => true,
        XElement e when Local(e) == "image" => parentHasText,    // Bild im Satz bleibt stehen, eigenständig wird sein <alt> übersetzt
        XElement e when PairedInline.Contains(Local(e)) || RawInline.Contains(Local(e)) => true,
        XElement e when Block.Contains(Local(e)) || Skip.Contains(Local(e)) => false,
        XElement => parentHasText,                               // unbekannte Elemente (Spezialisierungen): im Satz, wenn Text daneben steht
        _ => false,
    };

    private static IEnumerable<Unit> Units(XElement container)
    {
        if (Skip.Contains(Local(container)) || TranslateNo(container)) yield break;
        var hasText = HasText(container);
        var nodes = container.Nodes().ToList();
        if (nodes.All(n => IsInline(n, hasText)))
        {
            if (nodes.Count > 0) yield return new Unit(container, nodes);
            yield break;
        }
        // gemischter Inhalt: Text neben Blöcken wird zu eigenen Absätzen, Blöcke einzeln
        var run = new List<XNode>();
        foreach (var n in nodes)
        {
            if (IsInline(n, hasText)) { run.Add(n); continue; }
            if (run.Count > 0) { yield return new Unit(container, run); run = []; }
            foreach (var u in Units((XElement)n)) yield return u;
        }
        if (run.Count > 0) yield return new Unit(container, run);
    }

    private static IEnumerable<Unit> Translatable(XDocument xml) => xml.Root == null ? [] : Units(xml.Root);

    private static bool Preserve(XElement e) => e.AncestorsAndSelf().Any(x =>
        Preformatted.Contains(Local(x)) || x.Attribute(XNamespace.Xml + "space")?.Value == "preserve");

    private static IEnumerable<(XElement Element, XAttribute Attribute)> AttributeUnits(XDocument xml)
    {
        foreach (var el in xml.Descendants())
        {
            if (el.AncestorsAndSelf().Any(x => Skip.Contains(Local(x)) || TranslateNo(x))) continue;
            foreach (var a in Attributes)
                if (el.Attribute(a) is { } attr && attr.Value.Any(char.IsLetter)) yield return (el, attr);
        }
    }

    private static XDocument Parse(Stream input)
    {
        // DOCTYPE bleibt erhalten, die DTD wird aber nicht geladen (kein Netz- oder Dateizugriff)
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null, MaxCharactersFromEntities = 10_000_000 };
        using var reader = XmlReader.Create(FileFilters.Buffer(input), settings);
        var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (xml.DocumentType is { InternalSubset: "" } dt) dt.InternalSubset = null;   // sonst steht "[]" im DOCTYPE
        return xml;
    }

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var xml = Parse(input);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        var i = 0;
        foreach (var unit in Translatable(xml))
        {
            var tagData = new Dictionary<int, string>();
            var text = Text(unit, tagData);
            Add(doc, segmenter, $"b{i++}", text, tagData, Context(unit));
        }
        var a = 0;
        foreach (var (el, attr) in AttributeUnits(xml))
            Add(doc, segmenter, $"a{a++}", attr.Value, new(), $"{Local(el)}@{attr.Name.LocalName}");
        doc.Renumber();
        return doc;
    }

    /// <summary>Absatztext mit Tags: Leerraum zusammengefasst, in vorformatiertem Text bleiben Zeilenumbrüche als Platzhalter.</summary>
    private static string Text(Unit unit, Dictionary<int, string> tagData)
    {
        var tagged = ToTagged(unit.Nodes, tagData);
        if (!Preserve(unit.Parent)) return Ws().Replace(tagged, " ").Trim();
        var parts = tagged.Trim().Split('\n');
        return string.Join("", parts.Select((x, k) => k == 0 ? x : Markup.Raw(tagData, "\n") + x));
    }

    private static string Context(Unit u) =>
        u.Parent.Parent is { } p && Local(u.Parent) == "title" ? $"{Local(p)}/title" : Local(u.Parent);

    private static void Add(BilingualDocument doc, Segmenter segmenter, string locator, string text, Dictionary<int, string> tagData, string context)
    {
        if (!Segmenter.HasTranslatableText(text)) return;
        var para = new Paragraph { Locator = locator, Context = context, TagData = tagData };
        segmenter.Segment(para, text);
        doc.Paragraphs.Add(para);
    }

    private static string ToTagged(IEnumerable<XNode> nodes, Dictionary<int, string> tagData)
    {
        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e when !RawInline.Contains(Local(e)) && !TranslateNo(e) && e.Nodes().Any():
                    var id = Markup.Pair(tagData, OpenTag(e), $"</{QualifiedName(e, e.Name)}>");
                    sb.Append(TagText.Start(id)).Append(ToTagged(e.Nodes(), tagData)).Append(TagText.End(id));
                    break;
                default:
                    sb.Append(Markup.Raw(tagData, node.ToString(SaveOptions.DisableFormatting)));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string QualifiedName(XElement scope, XName name)
    {
        if (name.Namespace == XNamespace.None) return name.LocalName;
        if (name.Namespace == XNamespace.Xml) return "xml:" + name.LocalName;
        var prefix = scope.GetPrefixOfNamespace(name.Namespace);
        return string.IsNullOrEmpty(prefix) ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string OpenTag(XElement e)
    {
        var sb = new StringBuilder("<").Append(QualifiedName(e, e.Name));
        foreach (var a in e.Attributes())
        {
            var name = a.IsNamespaceDeclaration
                ? (a.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + a.Name.LocalName)
                : QualifiedName(e, a.Name);
            sb.Append(' ').Append(name).Append("=\"").Append(EscapeAttribute(a.Value)).Append('"');
        }
        return sb.Append('>').ToString();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string s) => Escape(s).Replace("\"", "&quot;");

    /// <summary>Den übersetzten Text (mit Tags) wieder in XML-Knoten verwandeln.</summary>
    private static List<XNode> Nodes(XElement parent, string tagged, Dictionary<int, string> tagData)
    {
        // Namensräume aus dem Umfeld deklarieren, damit Präfixe in den Tags gültig bleiben
        var declarations = parent.AncestorsAndSelf().SelectMany(x => x.Attributes()).Where(a => a.IsNamespaceDeclaration)
            .GroupBy(a => a.Name).Select(g => g.First())
            .Select(a => (a.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + a.Name.LocalName) + "=\"" + EscapeAttribute(a.Value) + "\"");
        var wrapper = XElement.Parse($"<r {string.Join(" ", declarations)}>{Markup.Unpack(tagged, tagData, Escape)}</r>", LoadOptions.PreserveWhitespace);
        var nodes = wrapper.Nodes().ToList();
        foreach (var n in nodes) n.Remove();
        return nodes;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var xml = Parse(original);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        xml.Root?.SetAttributeValue(XNamespace.Xml + "lang", document.TargetLanguage);

        var i = 0;
        foreach (var unit in Translatable(xml).ToList())
        {
            if (!byLocator.TryGetValue($"b{i++}", out var p)) continue;
            var raw = ToTagged(unit.Nodes, new());
            var lead = raw[..(raw.Length - raw.TrimStart().Length)];
            var trail = raw[raw.TrimEnd().Length..];
            var replacement = Nodes(unit.Parent, p.JoinTarget().Trim(), p.TagData);
            if (lead.Length > 0) replacement.Insert(0, new XText(lead));
            if (trail.Length > 0) replacement.Add(new XText(trail));
            unit.Nodes[0].AddBeforeSelf(replacement);
            foreach (var n in unit.Nodes) n.Remove();
        }

        var a = 0;
        foreach (var (_, attr) in AttributeUnits(xml).ToList())
            if (byLocator.TryGetValue($"a{a++}", out var p))
                attr.Value = TagText.StripTags(p.JoinTarget()).Trim();

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = xml.Declaration == null,
            NewLineHandling = NewLineHandling.None,
        };
        using var writer = XmlWriter.Create(output, settings);
        xml.Save(writer);
    }
}
