using System.Text;
using DocumentFormat.OpenXml;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Gemeinsame Logik für Absätze aus „Läufen“ (r mit rPr und t) in Excel (si) und PowerPoint (a:p).
/// Abweichende Formate werden zu Tag-Paaren, alles andere (Umbrüche, Felder) zu Platzhaltern,
/// deren Original-Elemente beim Zurückschreiben unverändert wieder eingesetzt werden.
/// </summary>
internal sealed class RunContainerCodec(Func<string, string, OpenXmlElement> makeRun, params string[] keepNames)
{
    private readonly HashSet<string> _keep = new(keepNames);

    private sealed record Tok(bool IsText, string Props, string Text, int Element);

    private List<Tok> Walk(OpenXmlElement container, List<OpenXmlElement> elements)
    {
        var toks = new List<Tok>();
        foreach (var child in container.ChildElements)
        {
            var name = child.LocalName;
            if (_keep.Contains(name)) continue;
            if (name == "t")
                toks.Add(new Tok(true, "", child.InnerText, -1));
            else if (name == "r")
            {
                var props = child.ChildElements.FirstOrDefault(c => c.LocalName == "rPr")?.OuterXml ?? "";
                var text = string.Concat(child.ChildElements.Where(c => c.LocalName == "t").Select(c => c.InnerText));
                toks.Add(new Tok(true, props, text, -1));
            }
            else
            {
                toks.Add(new Tok(false, "", "", elements.Count));
                elements.Add(child);
            }
        }
        return toks;
    }

    public string ToTagged(OpenXmlElement container, Dictionary<int, string> tagData)
    {
        var toks = Walk(container, new List<OpenXmlElement>());
        var baseProps = toks.Where(t => t.IsText).GroupBy(t => t.Props)
            .OrderByDescending(g => g.Sum(t => t.Text.Length)).Select(g => g.Key).FirstOrDefault() ?? "";
        tagData[0] = "b:" + baseProps;

        var sb = new StringBuilder();
        var next = 1;
        int? open = null;
        string? openProps = null;
        List<int>? pending = null;

        void Flush()
        {
            if (pending == null) return;
            var id = next++;
            tagData[id] = "p:" + string.Join(',', pending);
            sb.Append(TagText.Placeholder(id));
            pending = null;
        }

        foreach (var t in toks)
        {
            if (!t.IsText)
            {
                (pending ??= new()).Add(t.Element);
                continue;
            }
            Flush();
            var props = t.Props == baseProps ? null : t.Props;
            if (props != openProps)
            {
                if (open != null) sb.Append(TagText.End(open.Value));
                open = null;
                openProps = null;
                if (props != null)
                {
                    open = next++;
                    openProps = props;
                    tagData[open.Value] = "f:" + props;
                    sb.Append(TagText.Start(open.Value));
                }
            }
            sb.Append(t.Text);
        }
        Flush();
        if (open != null) sb.Append(TagText.End(open.Value));
        return sb.ToString();
    }

    public void Rebuild(OpenXmlElement container, string tagged, Dictionary<int, string> tagData)
    {
        var elements = new List<OpenXmlElement>();
        Walk(container, elements);
        var baseProps = tagData.TryGetValue(0, out var b) && b.StartsWith("b:") ? b[2..] : "";

        // Feste Elemente (Absatz-Eigenschaften usw.) behalten: was vor dem ersten Inhalt stand, bleibt vorn.
        var children = container.ChildElements.ToList();
        var firstContent = children.FindIndex(c => !_keep.Contains(c.LocalName));
        var leading = children.Where((c, i) => _keep.Contains(c.LocalName) && (firstContent < 0 || i < firstContent)).ToList();
        var trailing = children.Where((c, i) => _keep.Contains(c.LocalName) && firstContent >= 0 && i > firstContent).ToList();
        foreach (var c in children) c.Remove();
        foreach (var c in leading) container.AppendChild(c);

        var formats = new Stack<(int Id, string Props)>();
        var used = new HashSet<int>();

        void Placeholder(int id)
        {
            if (!tagData.TryGetValue(id, out var data) || !data.StartsWith("p:") || !used.Add(id)) return;
            foreach (var idx in data[2..].Split(',').Select(int.Parse))
                container.AppendChild(elements[idx]);
        }

        foreach (var part in TagText.Split(tagged))
        {
            if (part.Text != null)
            {
                if (part.Text.Length > 0)
                    container.AppendChild(makeRun(formats.Count > 0 ? formats.Peek().Props : baseProps, part.Text));
                continue;
            }
            var tag = part.Tag!.Value;
            tagData.TryGetValue(tag.Id, out var d);
            switch (tag.Kind)
            {
                case TagText.TagKind.Start when d?.StartsWith("f:") == true:
                    formats.Push((tag.Id, d[2..]));
                    break;
                case TagText.TagKind.End when formats.Any(f => f.Id == tag.Id):
                    while (formats.Count > 0 && formats.Pop().Id != tag.Id) { }
                    break;
                case TagText.TagKind.Placeholder:
                    Placeholder(tag.Id);
                    break;
            }
        }
        foreach (var id in tagData.Where(kv => kv.Value.StartsWith("p:")).Select(kv => kv.Key).ToList()) Placeholder(id);
        foreach (var c in trailing) container.AppendChild(c);
    }
}
