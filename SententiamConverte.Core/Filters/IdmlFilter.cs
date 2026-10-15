using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// IDML (Austauschformat für Layoutprogramme): Texte aller Textabschnitte (Stories), auch in Tabellenzellen.
/// Absätze enden an &lt;Br/&gt;; abweichende Zeichenformate werden zu Tag-Paaren, Hyperlinks ebenfalls,
/// verankerte Objekte, Fußnoten und Sonderzeichen-Anweisungen zu Platzhaltern.
/// </summary>
public sealed class IdmlFilter : IFileFilter
{
    public string Id => "idml";
    public string Name => "IDML";
    public IReadOnlyList<string> Extensions { get; } = [".idml"];

    // Ein Stück Absatz: Text (mit Zeichenformat-Lauf), Platzhalter oder Hyperlink-Klammer
    private enum Kind { Text, Placeholder, Pi, LinkStart, LinkEnd }
    private sealed record Tok(Kind Kind, int Csr, string Text = "", XNode? Node = null, XElement? Link = null);

    private sealed class Para
    {
        public readonly List<Tok> Tokens = new();
        public bool EndsWithBr;
    }

    /// <summary>Zerlegt einen ParagraphStyleRange in Absätze (Trenner: Br).</summary>
    private static List<Para> Split(XElement psr, List<XElement> csrs)
    {
        var paras = new List<Para> { new() };
        foreach (var csr in psr.Elements().Where(e => e.Name.LocalName == "CharacterStyleRange"))
        {
            var ci = csrs.Count;
            csrs.Add(csr);
            WalkCsr(csr, ci, paras, null);
        }
        if (paras.Count > 1 && paras[^1].Tokens.Count == 0) paras.RemoveAt(paras.Count - 1);
        return paras;
    }

    private static void WalkCsr(XElement container, int ci, List<Para> paras, XElement? link)
    {
        foreach (var child in container.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Properties":
                    break;
                case "Content":
                    foreach (var n in child.Nodes())
                        if (n is XText t) paras[^1].Tokens.Add(new Tok(Kind.Text, ci, t.Value));
                        else if (n is XProcessingInstruction pi) paras[^1].Tokens.Add(new Tok(Kind.Pi, ci, Node: pi));
                    break;
                case "Br":
                    paras[^1].EndsWithBr = true;
                    paras.Add(new Para());
                    break;
                case "HyperlinkTextSource":
                    paras[^1].Tokens.Add(new Tok(Kind.LinkStart, ci, Link: child));
                    WalkCsr(child, ci, paras, child);
                    paras[^1].Tokens.Add(new Tok(Kind.LinkEnd, ci, Link: child));
                    break;
                default:
                    paras[^1].Tokens.Add(new Tok(Kind.Placeholder, ci, Node: child));
                    break;
            }
        }
    }

    private static string ToTagged(Para para, List<XElement> csrs, Dictionary<int, string> tagData, Dictionary<int, XNode> live, Dictionary<int, XElement> links)
    {
        var baseCsr = para.Tokens.Where(t => t.Kind == Kind.Text).GroupBy(t => Shell(csrs[t.Csr]))
            .OrderByDescending(g => g.Sum(t => t.Text.Length)).Select(g => g.First().Csr).FirstOrDefault(-1);
        var baseShell = baseCsr >= 0 ? Shell(csrs[baseCsr]) : para.Tokens.Count > 0 ? Shell(csrs[para.Tokens[0].Csr]) : "";
        tagData[0] = "b:" + (baseCsr >= 0 ? baseCsr : para.Tokens.Count > 0 ? para.Tokens[0].Csr : -1);

        var sb = new StringBuilder();
        var next = 1;
        int? open = null;
        string? openShell = null;
        var linkIds = new Dictionary<XElement, int>();
        void Close()
        {
            if (open == null) return;
            sb.Append(TagText.End(open.Value));
            open = null;
            openShell = null;
        }
        foreach (var t in para.Tokens)
        {
            switch (t.Kind)
            {
                case Kind.Text:
                    var shell = Shell(csrs[t.Csr]);
                    var wanted = shell == baseShell ? null : shell;
                    if (wanted != openShell)
                    {
                        Close();
                        if (wanted != null)
                        {
                            open = next++;
                            openShell = wanted;
                            tagData[open.Value] = "f:" + t.Csr;
                            sb.Append(TagText.Start(open.Value));
                        }
                    }
                    sb.Append(t.Text);
                    break;
                case Kind.Placeholder or Kind.Pi:
                    var pid = next++;
                    tagData[pid] = (t.Kind == Kind.Pi ? "pi:" : "p:") + t.Csr;
                    live[pid] = t.Node!;
                    sb.Append(TagText.Placeholder(pid));
                    break;
                case Kind.LinkStart:
                    Close();
                    var lid = next++;
                    linkIds[t.Link!] = lid;
                    links[lid] = t.Link!;
                    tagData[lid] = "h:" + t.Csr;
                    sb.Append(TagText.Start(lid));
                    break;
                case Kind.LinkEnd:
                    Close();
                    sb.Append(TagText.End(linkIds[t.Link!]));
                    break;
            }
        }
        Close();
        return sb.ToString();
    }

    /// <summary>Formatmerkmale eines Zeichenformat-Laufs (Attribute + Properties), ohne den Inhalt.</summary>
    private static string Shell(XElement csr) =>
        string.Join("|", csr.Attributes().OrderBy(a => a.Name.ToString()).Select(a => a + "")) +
        (csr.Element("Properties")?.ToString(SaveOptions.DisableFormatting) ?? "");

    private static IEnumerable<ZipArchiveEntry> Stories(ZipArchive zip) =>
        zip.Entries.Where(e => e.FullName.StartsWith("Stories/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal);

    private static List<XElement> Psrs(XDocument x) =>
        x.Descendants().Where(e => e.Name.LocalName == "ParagraphStyleRange").ToList();

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        foreach (var story in Stories(zip))
        {
            XDocument x;
            using (var s = story.Open()) x = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            var psrs = Psrs(x);
            for (var r = 0; r < psrs.Count; r++)
            {
                var csrs = new List<XElement>();
                var paras = Split(psrs[r], csrs);
                for (var i = 0; i < paras.Count; i++)
                {
                    var para = new Paragraph
                    {
                        Locator = $"{story.FullName}#{r}#{i}",
                        Context = psrs[r].Ancestors().Any(a => a.Name.LocalName == "Cell") ? "Tabelle" : Path.GetFileNameWithoutExtension(story.FullName),
                    };
                    var text = ToTagged(paras[i], csrs, para.TagData, new(), new());
                    if (!Segmenter.HasTranslatableText(text)) continue;
                    segmenter.Segment(para, text);
                    doc.Paragraphs.Add(para);
                }
            }
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var source = new ZipArchive(FileFilters.Buffer(original), ZipArchiveMode.Read);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        var replaced = new Dictionary<string, byte[]>();
        foreach (var story in Stories(source))
        {
            XDocument x;
            using (var s = story.Open()) x = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            var psrs = Psrs(x);
            var changed = false;
            // Innere (Tabellenzellen) zuerst nicht nötig: Platzhalter werden als lebende Elemente verschoben.
            for (var r = 0; r < psrs.Count; r++)
            {
                var csrs = new List<XElement>();
                var paras = Split(psrs[r], csrs);
                if (!Enumerable.Range(0, paras.Count).Any(i => byLocator.ContainsKey($"{story.FullName}#{r}#{i}"))) continue;
                var rebuilt = new List<XElement>();
                for (var i = 0; i < paras.Count; i++)
                {
                    var tagData = new Dictionary<int, string>();
                    var live = new Dictionary<int, XNode>();
                    var links = new Dictionary<int, XElement>();
                    var sourceText = ToTagged(paras[i], csrs, tagData, live, links);
                    var text = byLocator.TryGetValue($"{story.FullName}#{r}#{i}", out var p) ? p.JoinTarget() : sourceText;
                    rebuilt.AddRange(Build(text, tagData, live, links, csrs, paras[i].EndsWithBr));
                }
                foreach (var csr in psrs[r].Elements().Where(e => e.Name.LocalName == "CharacterStyleRange").ToList()) csr.Remove();
                psrs[r].Add(rebuilt);
                changed = true;
            }
            if (!changed) continue;
            using var ms = new MemoryStream();
            x.Save(ms, SaveOptions.DisableFormatting);
            replaced[story.FullName] = ms.ToArray();
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in source.Entries)
        {
            var target = zip.CreateEntry(entry.FullName, entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using var ws = target.Open();
            if (replaced.TryGetValue(entry.FullName, out var bytes)) ws.Write(bytes);
            else
            {
                using var rs = entry.Open();
                rs.CopyTo(ws);
            }
        }
    }

    /// <summary>Baut die Zeichenformat-Läufe eines Absatzes aus dem Text mit Tags neu auf.</summary>
    private static List<XElement> Build(string text, Dictionary<int, string> tagData, Dictionary<int, XNode> live,
        Dictionary<int, XElement> links, List<XElement> csrs, bool endsWithBr)
    {
        var result = new List<XElement>();
        var baseIdx = tagData.TryGetValue(0, out var b) ? int.Parse(b[2..]) : -1;
        var formats = new Stack<(int Id, int Csr)>();
        XElement? link = null;
        int? linkId = null;
        XElement? current = null;
        int currentCsr = int.MinValue;
        XElement? currentLink = null;

        XElement Container()
        {
            var csrIdx = formats.Count > 0 ? formats.Peek().Csr : baseIdx;
            if (current == null || currentCsr != csrIdx || currentLink != link)
            {
                var orig = csrIdx >= 0 ? csrs[csrIdx] : null;
                current = orig != null ? new XElement(orig.Name, orig.Attributes(), orig.Element("Properties") is { } props ? new XElement(props) : null)
                                       : new XElement("CharacterStyleRange");
                currentCsr = csrIdx;
                currentLink = link;
                result.Add(current);
                if (link != null)
                {
                    var shell = new XElement(link.Name, link.Attributes());
                    current.Add(shell);
                    return shell;
                }
            }
            return link != null ? current.Elements().Last() : current;
        }

        void AddText(string s)
        {
            var c = Container();
            if (c.Elements().LastOrDefault() is { Name.LocalName: "Content" } last && c.LastNode == last) last.Add(new XText(s));
            else c.Add(new XElement(c.Name.Namespace + "Content", s));
        }

        var used = new HashSet<int>();
        void Placeholder(int id)
        {
            if (!live.TryGetValue(id, out var node) || !used.Add(id)) return;
            var c = Container();
            if (tagData[id].StartsWith("pi:"))
            {
                var pi = new XProcessingInstruction(((XProcessingInstruction)node).Target, ((XProcessingInstruction)node).Data);
                if (c.Elements().LastOrDefault() is { Name.LocalName: "Content" } last && c.LastNode == last) last.Add(pi);
                else c.Add(new XElement(c.Name.Namespace + "Content", pi));
            }
            else
            {
                if (node.Parent != null) node.Remove();
                c.Add(node);
            }
        }

        foreach (var part in TagText.Split(text))
        {
            if (part.Text != null) { if (part.Text.Length > 0) AddText(part.Text); continue; }
            var tag = part.Tag!.Value;
            tagData.TryGetValue(tag.Id, out var data);
            switch (tag.Kind)
            {
                case TagText.TagKind.Start when data?.StartsWith("f:") == true:
                    formats.Push((tag.Id, int.Parse(data[2..])));
                    break;
                case TagText.TagKind.Start when data?.StartsWith("h:") == true && links.TryGetValue(tag.Id, out var l):
                    link = l;
                    linkId = tag.Id;
                    break;
                case TagText.TagKind.End when formats.Any(f => f.Id == tag.Id):
                    while (formats.Count > 0 && formats.Pop().Id != tag.Id) { }
                    break;
                case TagText.TagKind.End when tag.Id == linkId:
                    link = null;
                    linkId = null;
                    break;
                case TagText.TagKind.Placeholder:
                    Placeholder(tag.Id);
                    break;
            }
        }
        link = null;
        foreach (var id in live.Keys.ToList()) Placeholder(id);
        if (endsWithBr)
        {
            var c = result.Count > 0 ? result[^1] : Container();
            c.Add(new XElement(c.Name.Namespace + "Br"));
        }
        return result;
    }
}
