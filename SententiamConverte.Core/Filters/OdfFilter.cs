using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// OpenDocument: Texte (.odt), Tabellen (.ods) und Präsentationen (.odp), auch Kopf-/Fußzeilen, Fußnoten,
/// Textrahmen und Notizen. Zeichenformate und Links werden zu Tag-Paaren; Bilder, Notizen, Zeilenumbrüche
/// und Felder zu Platzhaltern, deren Original-Elemente beim Zurückschreiben unverändert eingesetzt werden.
/// </summary>
public sealed class OdfFilter : IFileFilter
{
    public string Id => "odf";
    public string Name => "OpenDocument";
    public IReadOnlyList<string> Extensions { get; } = [".odt", ".ods", ".odp", ".ott"];

    private static readonly XNamespace T = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly string[] Parts = ["content.xml", "styles.xml"];

    private static IEnumerable<XElement> Paragraphs(XDocument x) =>
        x.Descendants().Where(e => e.Name == T + "p" || e.Name == T + "h");

    // ---------- Zerlegen ----------

    private enum Kind { Text, Start, End, Placeholder }
    private sealed record Tok(Kind Kind, string Text = "", int Element = -1);

    private static List<Tok> Walk(XElement container, List<XElement> elements)
    {
        var toks = new List<Tok>();
        foreach (var node in container.Nodes())
        {
            switch (node)
            {
                case XText t:
                    toks.Add(new Tok(Kind.Text, t.Value));
                    break;
                case XElement e when e.Name == T + "s":
                    toks.Add(new Tok(Kind.Text, new string(' ', (int?)e.Attribute(T + "c") ?? 1)));
                    break;
                case XElement e when e.Name == T + "tab":
                    toks.Add(new Tok(Kind.Text, "\t"));
                    break;
                case XElement e when e.Name == T + "span" || e.Name == T + "a":
                    var ord = elements.Count;
                    elements.Add(e);
                    toks.Add(new Tok(Kind.Start, Element: ord));
                    toks.AddRange(Walk(e, elements));
                    toks.Add(new Tok(Kind.End, Element: ord));
                    break;
                case XElement e:
                    toks.Add(new Tok(Kind.Placeholder, Element: elements.Count));
                    elements.Add(e);
                    break;
            }
        }
        return toks;
    }

    private static string ToTagged(XElement p, Dictionary<int, string> tagData)
    {
        var sb = new StringBuilder();
        var next = 1;
        var open = new Dictionary<int, int>();       // Element → Tag-Nummer
        List<int>? pending = null;
        void Flush()
        {
            if (pending == null) return;
            var id = next++;
            tagData[id] = "p:" + string.Join(',', pending);
            sb.Append(TagText.Placeholder(id));
            pending = null;
        }
        foreach (var t in Walk(p, new List<XElement>()))
        {
            switch (t.Kind)
            {
                case Kind.Text:
                    Flush();
                    sb.Append(t.Text);
                    break;
                case Kind.Placeholder:
                    (pending ??= new()).Add(t.Element);
                    break;
                case Kind.Start:
                    Flush();
                    var id = next++;
                    open[t.Element] = id;
                    tagData[id] = "s:" + t.Element;
                    sb.Append(TagText.Start(id));
                    break;
                case Kind.End:
                    Flush();
                    sb.Append(TagText.End(open[t.Element]));
                    break;
            }
        }
        Flush();
        return sb.ToString();
    }

    // ---------- Lesen ----------

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        using var buffer = FileFilters.Buffer(input);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        foreach (var part in Parts)
        {
            var entry = zip.GetEntry(part);
            if (entry == null) continue;
            XDocument x;
            using (var s = entry.Open()) x = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            var paras = Paragraphs(x).ToList();
            for (var i = 0; i < paras.Count; i++)
            {
                var para = new Paragraph { Locator = $"{part}#{i}", Context = ContextOf(paras[i], part) };
                var text = ToTagged(paras[i], para.TagData);
                if (!Segmenter.HasTranslatableText(text)) continue;
                segmenter.Segment(para, text);
                doc.Paragraphs.Add(para);
            }
        }
        if (doc.Paragraphs.Count == 0 && zip.GetEntry("content.xml") == null)
            throw new InvalidDataException("Keine OpenDocument-Datei.");
        doc.Renumber();
        return doc;
    }

    private static string? ContextOf(XElement p, string part)
    {
        if (part == "styles.xml") return "Kopf-/Fußzeile";
        var cell = p.Ancestors().FirstOrDefault(a => a.Name.LocalName == "table-cell");
        if (cell != null) return "Tabelle";
        if (p.Ancestors().Any(a => a.Name.LocalName == "note-body")) return "Fußnote";
        if (p.Ancestors().FirstOrDefault(a => a.Name.LocalName == "page") is { } page)
            return "Folie " + ((string?)page.Attributes().FirstOrDefault(a => a.Name.LocalName == "name") ?? "");
        return p.Name.LocalName == "h" ? "Überschrift" : null;
    }

    // ---------- Schreiben ----------

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        using var source = new ZipArchive(FileFilters.Buffer(original), ZipArchiveMode.Read);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        var replaced = new Dictionary<string, byte[]>();
        foreach (var part in Parts)
        {
            var entry = source.GetEntry(part);
            if (entry == null) continue;
            XDocument x;
            using (var s = entry.Open()) x = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            var paras = Paragraphs(x).ToList();
            for (var i = 0; i < paras.Count; i++)
                if (byLocator.TryGetValue($"{part}#{i}", out var para))
                    Rebuild(paras[i], para);
            using var ms = new MemoryStream();
            x.Save(ms, SaveOptions.DisableFormatting);
            replaced[part] = ms.ToArray();
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in source.Entries)
        {
            // „mimetype“ muss unkomprimiert als erster Eintrag stehen
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

    private static void Rebuild(XElement p, Paragraph para)
    {
        var elements = new List<XElement>();
        Walk(p, elements);
        p.RemoveNodes();
        var stack = new Stack<(int Id, XElement El)>();
        stack.Push((0, p));
        var used = new HashSet<int>();

        void Placeholder(int id)
        {
            if (!para.TagData.TryGetValue(id, out var d) || !d.StartsWith("p:") || !used.Add(id)) return;
            foreach (var ord in d[2..].Split(',').Select(int.Parse))
            {
                var el = elements[ord];
                if (el.Parent != null) el.Remove();
                stack.Peek().El.Add(el);
            }
        }

        foreach (var part in TagText.Split(para.JoinTarget()))
        {
            if (part.Text != null)
            {
                AppendText(stack.Peek().El, part.Text, stack.Count == 1 && !p.Nodes().Any());
                continue;
            }
            var tag = part.Tag!.Value;
            para.TagData.TryGetValue(tag.Id, out var data);
            switch (tag.Kind)
            {
                case TagText.TagKind.Start when data?.StartsWith("s:") == true:
                    var orig = elements[int.Parse(data[2..])];
                    var shell = new XElement(orig.Name, orig.Attributes());
                    stack.Peek().El.Add(shell);
                    stack.Push((tag.Id, shell));
                    break;
                case TagText.TagKind.End when stack.Any(s => s.Id == tag.Id):
                    while (stack.Count > 1 && stack.Pop().Id != tag.Id) { }
                    break;
                case TagText.TagKind.Placeholder:
                    Placeholder(tag.Id);
                    break;
            }
        }
        while (stack.Count > 1) stack.Pop();
        foreach (var id in para.TagData.Where(kv => kv.Value.StartsWith("p:")).Select(kv => kv.Key).ToList()) Placeholder(id);
    }

    /// <summary>Text in ODF-Form: Tabulatoren als text:tab, mehrere Leerzeichen als text:s.</summary>
    private static void AppendText(XElement container, string text, bool atStart)
    {
        var sb = new StringBuilder();
        void FlushText()
        {
            if (sb.Length == 0) return;
            container.Add(new XText(sb.ToString()));
            sb.Clear();
        }
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\t')
            {
                FlushText();
                container.Add(new XElement(T + "tab"));
                i++;
                continue;
            }
            if (c == ' ')
            {
                var n = 1;
                while (i + n < text.Length && text[i + n] == ' ') n++;
                var leading = atStart && i == 0;
                if (n == 1 && !leading) sb.Append(' ');
                else
                {
                    var literal = leading ? 0 : 1;
                    if (literal == 1) sb.Append(' ');
                    FlushText();
                    container.Add(n - literal == 1 ? new XElement(T + "s") : new XElement(T + "s", new XAttribute(T + "c", n - literal)));
                }
                i += n;
                continue;
            }
            sb.Append(c);
            i++;
        }
        FlushText();
    }
}
