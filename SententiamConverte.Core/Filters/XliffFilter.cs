using System.Text;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// XLIFF 1.2, XLIFF 2.x und die XLIFF-Variante .sdlxliff. Vorhandene Segmentierung (mrk mtype="seg" bzw. segment)
/// wird übernommen; vorhandene Übersetzungen und ihr Status werden eingelesen.
/// </summary>
public sealed class XliffFilter : IFileFilter
{
    public string Id => "xliff";
    public string Name => "XLIFF";
    public IReadOnlyList<string> Extensions { get; } = [".xlf", ".xliff", ".sdlxliff", ".mqxliff"];

    // Namensraum der Segment-Metadaten im .sdlxliff-Format (technisch für die Kompatibilität nötig)
    private static readonly XNamespace SegNs = "http://sdl.com/FileTypes/SdlXliff/1.0";

    private static readonly HashSet<string> PairedElements = ["g", "pc", "mrk"];

    public string TargetFileName(string sourceFileName, string targetLanguage) => sourceFileName;

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var x = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var root = x.Root ?? throw new InvalidDataException("Leere XLIFF-Datei.");
        var ns = root.Name.Namespace;
        var version = (string?)root.Attribute("version") ?? "1.2";
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };

        if (version.StartsWith('2'))
        {
            var units = root.Descendants(ns + "unit").ToList();
            for (var u = 0; u < units.Count; u++)
            {
                if ((string?)units[u].Attribute("translate") == "no") continue;
                var para = new Paragraph { Locator = u.ToString(), Context = (string?)units[u].Attribute("name") };
                var segs = units[u].Elements(ns + "segment").ToList();
                for (var s = 0; s < segs.Count; s++)
                {
                    var src = segs[s].Element(ns + "source");
                    if (src == null) continue;
                    var seg = new Segment { Key = s.ToString(), Source = ToTagged(src, para.TagData) };
                    if (segs[s].Element(ns + "target") is { } tgt)
                    {
                        seg.Target = ToTagged(tgt, para.TagData);
                        seg.Status = (string?)segs[s].Attribute("state") switch
                        {
                            "translated" => SegmentStatus.Translated,
                            "reviewed" => SegmentStatus.Reviewed,
                            "final" => SegmentStatus.Signed,
                            _ => seg.Target.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated,
                        };
                    }
                    seg.Locked = !Segmenter.HasTranslatableText(seg.Source);
                    seg.TrailingWhitespace = s < segs.Count - 1 ? " " : "";
                    para.Segments.Add(seg);
                }
                if (para.Segments.Count > 0) doc.Paragraphs.Add(para);
            }
        }
        else
        {
            var units = root.Descendants(ns + "trans-unit").ToList();
            for (var u = 0; u < units.Count; u++)
            {
                var tu = units[u];
                if ((string?)tu.Attribute("translate") == "no") continue;
                var para = new Paragraph { Locator = u.ToString(), Context = (string?)tu.Attribute("resname") };
                var segSource = tu.Element(ns + "seg-source");
                var target = tu.Element(ns + "target");
                var segMarks = segSource?.Descendants(ns + "mrk").Where(m => (string?)m.Attribute("mtype") == "seg").ToList();

                if (segMarks is { Count: > 0 })
                {
                    foreach (var mrk in segMarks)
                    {
                        var mid = (string?)mrk.Attribute("mid") ?? "";
                        var seg = new Segment { Key = mid, Source = ToTagged(mrk, para.TagData) };
                        var tmrk = target?.Descendants(ns + "mrk").FirstOrDefault(m => (string?)m.Attribute("mid") == mid);
                        if (tmrk != null) seg.Target = ToTagged(tmrk, para.TagData);
                        seg.Status = ReadSegDefStatus(tu, mid, seg.Target);
                        seg.Locked = !Segmenter.HasTranslatableText(seg.Source) || IsSegDefLocked(tu, mid);
                        para.Segments.Add(seg);
                    }
                    // Leerraum zwischen den Segmenten aus seg-source übernehmen
                    for (var i = 0; i < segMarks.Count - 1; i++)
                        if (segMarks[i].NextNode is XText ws && string.IsNullOrWhiteSpace(ws.Value))
                            para.Segments[i].TrailingWhitespace = ws.Value;
                }
                else if (tu.Element(ns + "source") is { } src)
                {
                    var seg = new Segment { Key = "", Source = ToTagged(src, para.TagData) };
                    if (target != null)
                    {
                        seg.Target = ToTagged(target, para.TagData);
                        seg.Status = (string?)target.Attribute("state") switch
                        {
                            "translated" => SegmentStatus.Translated,
                            "reviewed" or "signed-off" => SegmentStatus.Reviewed,
                            "final" => SegmentStatus.Signed,
                            _ => seg.Target.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated,
                        };
                    }
                    seg.Locked = !Segmenter.HasTranslatableText(seg.Source);
                    para.Segments.Add(seg);
                }
                if (para.Segments.Count > 0) doc.Paragraphs.Add(para);
            }
        }
        doc.Renumber();
        return doc;
    }

    private static SegmentStatus ReadSegDefStatus(XElement tu, string mid, string target)
    {
        var conf = SegDef(tu, mid)?.Attribute("conf")?.Value;
        return conf switch
        {
            "Translated" => SegmentStatus.Translated,
            "Draft" => SegmentStatus.Draft,
            "RejectedTranslation" => SegmentStatus.TranslationRejected,
            "ApprovedTranslation" => SegmentStatus.Reviewed,
            "RejectedSignOff" => SegmentStatus.ReviewRejected,
            "ApprovedSignOff" => SegmentStatus.Signed,
            _ => target.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated,
        };
    }

    private static bool IsSegDefLocked(XElement tu, string mid) =>
        (string?)SegDef(tu, mid)?.Attribute("locked") == "true";

    private static XElement? SegDef(XElement tu, string mid) =>
        tu.Element(SegNs + "seg-defs")?.Elements(SegNs + "seg").FirstOrDefault(s => (string?)s.Attribute("id") == mid);

    /// <summary>Inline-Elemente → Tags. Gleiche Elemente bekommen dieselbe Nummer (Quelle und Ziel).</summary>
    private static string ToTagged(XElement container, Dictionary<int, string> tagData)
    {
        var sb = new StringBuilder();
        foreach (var node in container.Nodes())
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement e when PairedElements.Contains(e.Name.LocalName) && (string?)e.Attribute("mtype") != "seg":
                    var shell = new XElement(e.Name, e.Attributes()).ToString(SaveOptions.DisableFormatting);
                    var id = IdFor(tagData, "g:" + shell);
                    sb.Append(TagText.Start(id)).Append(ToTagged(e, tagData)).Append(TagText.End(id));
                    break;
                case XElement e:
                    sb.Append(TagText.Placeholder(IdFor(tagData, "x:" + e.ToString(SaveOptions.DisableFormatting))));
                    break;
            }
        }
        return sb.ToString();
    }

    private static int IdFor(Dictionary<int, string> tagData, string data)
    {
        foreach (var kv in tagData)
            if (kv.Value == data) return kv.Key;
        var id = tagData.Count == 0 ? 1 : tagData.Keys.Max() + 1;
        tagData[id] = data;
        return id;
    }

    private static List<XNode> FromTagged(string text, Dictionary<int, string> tagData)
    {
        var top = new XElement("root");
        var stack = new Stack<(int Id, XElement El)>();
        stack.Push((0, top));
        foreach (var part in TagText.Split(text))
        {
            if (part.Text != null)
            {
                stack.Peek().El.Add(new XText(part.Text));
                continue;
            }
            var tag = part.Tag!.Value;
            if (!tagData.TryGetValue(tag.Id, out var data)) continue;
            switch (tag.Kind)
            {
                case TagText.TagKind.Start when data.StartsWith("g:"):
                    var el = ParseInline(data[2..]);
                    stack.Peek().El.Add(el);
                    stack.Push((tag.Id, el));
                    break;
                case TagText.TagKind.End:
                    if (stack.Any(s => s.Id == tag.Id))
                        while (stack.Count > 1 && stack.Pop().Id != tag.Id) { }
                    break;
                case TagText.TagKind.Placeholder when data.StartsWith("x:"):
                    stack.Peek().El.Add(ParseInline(data[2..]));
                    break;
            }
        }
        return top.Nodes().ToList();
    }

    /// <summary>Gespeichertes Inline-Element wieder einlesen, ohne die beim Speichern mitgeschriebenen xmlns-Angaben.</summary>
    private static XElement ParseInline(string xml)
    {
        var el = XElement.Parse(xml);
        foreach (var e in el.DescendantsAndSelf())
            e.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        return el;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var x = XDocument.Load(original, LoadOptions.PreserveWhitespace);
        var root = x.Root!;
        var ns = root.Name.Namespace;
        var version = (string?)root.Attribute("version") ?? "1.2";
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);

        if (version.StartsWith('2'))
        {
            if (root.Attribute("trgLang") == null) root.SetAttributeValue("trgLang", document.TargetLanguage);
            var units = root.Descendants(ns + "unit").ToList();
            for (var u = 0; u < units.Count; u++)
            {
                if (!byLocator.TryGetValue(u.ToString(), out var para)) continue;
                var segs = units[u].Elements(ns + "segment").ToList();
                foreach (var seg in para.Segments)
                {
                    var el = segs[int.Parse(seg.Key!)];
                    if (seg.Target.Length == 0) continue;
                    var tgt = el.Element(ns + "target");
                    if (tgt == null) { tgt = new XElement(ns + "target"); el.Element(ns + "source")!.AddAfterSelf(tgt); }
                    tgt.ReplaceNodes(FromTagged(seg.Target, para.TagData));
                    el.SetAttributeValue("state", seg.Status switch
                    {
                        SegmentStatus.Reviewed => "reviewed",
                        SegmentStatus.Signed => "final",
                        SegmentStatus.Translated => "translated",
                        _ => "initial",
                    });
                }
            }
        }
        else
        {
            foreach (var file in root.Elements(ns + "file"))
                file.SetAttributeValue("target-language", document.TargetLanguage);
            var units = root.Descendants(ns + "trans-unit").ToList();
            for (var u = 0; u < units.Count; u++)
            {
                if (!byLocator.TryGetValue(u.ToString(), out var para)) continue;
                var tu = units[u];
                var segSource = tu.Element(ns + "seg-source");
                var target = tu.Element(ns + "target");

                if (para.Segments.Any(s => !string.IsNullOrEmpty(s.Key)) && segSource != null)
                {
                    if (target == null)
                    {
                        target = new XElement(ns + "target", segSource.Nodes());
                        segSource.AddAfterSelf(target);
                    }
                    foreach (var seg in para.Segments)
                    {
                        var mrk = target.Descendants(ns + "mrk").FirstOrDefault(m => (string?)m.Attribute("mid") == seg.Key);
                        if (mrk == null) continue;
                        mrk.ReplaceNodes(FromTagged(seg.Target.Length > 0 ? seg.Target : "", para.TagData));
                        if (SegDef(tu, seg.Key!) is { } segDef && seg.Target.Length > 0)
                        {
                            segDef.SetAttributeValue("conf", seg.Status switch
                            {
                                SegmentStatus.Translated => "Translated",
                                SegmentStatus.TranslationRejected => "RejectedTranslation",
                                SegmentStatus.Reviewed => "ApprovedTranslation",
                                SegmentStatus.ReviewRejected => "RejectedSignOff",
                                SegmentStatus.Signed => "ApprovedSignOff",
                                _ => "Draft",
                            });
                            if (seg.MatchPercent is { } pct) segDef.SetAttributeValue("percent", pct);
                        }
                    }
                }
                else
                {
                    var seg = para.Segments[0];
                    if (seg.Target.Length == 0) continue;
                    if (target == null)
                    {
                        target = new XElement(ns + "target");
                        tu.Element(ns + "source")!.AddAfterSelf(target);
                    }
                    target.ReplaceNodes(FromTagged(seg.Target, para.TagData));
                    target.SetAttributeValue(XNamespace.Xml + "lang", document.TargetLanguage);
                    target.SetAttributeValue("state", seg.Status switch
                    {
                        SegmentStatus.Reviewed => "signed-off",
                        SegmentStatus.Signed => "final",
                        SegmentStatus.Translated => "translated",
                        _ => "needs-review-translation",
                    });
                }
            }
        }
        x.Save(output, SaveOptions.DisableFormatting);
    }

    /// <summary>Exportiert ein beliebiges Dokument als zweisprachige XLIFF-1.2-Datei (zum Austausch mit anderen Werkzeugen).</summary>
    public static void ExportBilingual(BilingualDocument doc, Stream output)
    {
        XNamespace ns = "urn:oasis:names:tc:xliff:document:1.2";
        var body = new XElement(ns + "body");
        foreach (var p in doc.Paragraphs)
            foreach (var s in p.Segments)
                body.Add(new XElement(ns + "trans-unit",
                    new XAttribute("id", s.Id),
                    new XElement(ns + "source", TagText.StripTags(s.Source)),
                    s.Target.Length > 0
                        ? new XElement(ns + "target", new XAttribute("state", s.IsConfirmed ? "translated" : "needs-review-translation"), TagText.StripTags(s.Target))
                        : null,
                    p.Context != null ? new XElement(ns + "note", p.Context) : null));
        var x = new XDocument(new XElement(ns + "xliff", new XAttribute("version", "1.2"),
            new XElement(ns + "file",
                new XAttribute("original", doc.FileName),
                new XAttribute("source-language", doc.SourceLanguage),
                new XAttribute("target-language", doc.TargetLanguage),
                new XAttribute("datatype", "plaintext"),
                body)));
        x.Save(output);
    }
}
