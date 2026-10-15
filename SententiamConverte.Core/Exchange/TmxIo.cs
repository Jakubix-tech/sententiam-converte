using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Tm;

using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Exchange;

/// <summary>TMX 1.4 – das Standardformat zum Austausch von Translation Memories (von praktisch allen Übersetzungsprogrammen unterstützt).</summary>
public static class TmxIo
{
    public sealed record ImportResult(int Imported, IReadOnlyCollection<string> Languages);

    /// <summary>
    /// Importiert alle Übersetzungseinheiten. Ist <paramref name="onlySource"/>/<paramref name="onlyTarget"/> gesetzt,
    /// werden nur passende Sprachpaare übernommen; sonst jedes Paar aus Ausgangssprache und weiterer Sprache.
    /// </summary>
    public static ImportResult Import(Stream input, ITranslationMemory tm, string? onlySource = null, string? onlyTarget = null)
    {
        var x = XDocument.Load(input);
        var header = x.Root?.Element("header");
        var headerSrc = (string?)header?.Attribute("srclang");
        var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<TmEntry> Units()
        {
            foreach (var tu in x.Descendants("tu"))
            {
                var tuvs = tu.Elements("tuv")
                    .Select(v => (Lang: (string?)v.Attribute(XNamespace.Xml + "lang") ?? (string?)v.Attribute("lang") ?? "",
                                  Seg: v.Element("seg")))
                    .Where(v => v.Seg != null && v.Lang != "").ToList();
                if (tuvs.Count < 2) continue;
                var srcLang = (string?)tu.Attribute("srclang") ?? headerSrc;
                var src = srcLang is null or "*all*" ? tuvs[0] : tuvs.FirstOrDefault(v => Languages.Matches(v.Lang, srcLang));
                if (src.Seg == null) continue;
                var created = ParseDate((string?)tu.Attribute("changedate") ?? (string?)tu.Attribute("creationdate"));
                string? Prop(string type) => tu.Elements("prop").FirstOrDefault(p => (string?)p.Attribute("type") == type)?.Value;
                var prev = Prop(PrevProp);
                var next = Prop(NextProp);
                var structure = Prop(StructureProp);
                foreach (var tgt in tuvs.Where(v => !ReferenceEquals(v.Seg, src.Seg)))
                {
                    if (onlySource != null && !Languages.Matches(src.Lang, onlySource)) continue;
                    if (onlyTarget != null && !Languages.Matches(tgt.Lang, onlyTarget)) continue;
                    langs.Add(src.Lang);
                    langs.Add(tgt.Lang);
                    yield return new TmEntry(src.Lang, tgt.Lang, SegToTagged(src.Seg!), SegToTagged(tgt.Seg!), created, "import", structure, prev, next);
                }
            }
        }

        var n = tm.AddMany(Units());
        return new ImportResult(n, langs);
    }

    private static DateTime? ParseDate(string? s) =>
        s != null && DateTime.TryParseExact(s, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private static string SegToTagged(XElement seg)
    {
        var sb = new StringBuilder();
        var next = 100;
        foreach (var node in seg.Nodes())
        {
            switch (node)
            {
                case XText t: sb.Append(t.Value); break;
                case XElement { Name.LocalName: "bpt" } e: sb.Append(TagText.Start((int?)e.Attribute("i") ?? next++)); break;
                case XElement { Name.LocalName: "ept" } e: sb.Append(TagText.End((int?)e.Attribute("i") ?? next++)); break;
                case XElement { Name.LocalName: "hi" } e: sb.Append(e.Value); break;
                case XElement e: sb.Append(TagText.Placeholder((int?)e.Attribute("x") ?? next++)); break;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<XNode> TaggedToSeg(string text)
    {
        foreach (var part in TagText.Split(text))
        {
            if (part.Text != null) { yield return new XText(part.Text); continue; }
            var t = part.Tag!.Value;
            yield return t.Kind switch
            {
                TagText.TagKind.Start => new XElement("bpt", new XAttribute("i", t.Id), $"<{t.Id}>"),
                TagText.TagKind.End => new XElement("ept", new XAttribute("i", t.Id), $"</{t.Id}>"),
                _ => new XElement("ph", new XAttribute("x", t.Id), $"<{t.Id}/>"),
            };
        }
    }

    private const string PrevProp = "x-context-prev";
    private const string NextProp = "x-context-next";
    private const string StructureProp = "x-context";

    public static int Export(ITranslationMemory tm, Stream output)
    {
        var units = tm.All().ToList();
        var srcLang = units.GroupBy(u => u.SourceLanguage).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "*all*";
        // Eine Einheit mit mehreren Kontexten wird je Kontext ausgegeben (TMX kennt nur einen Kontext je tu).
        XElement Tu(TmUnit u, (string Previous, string Next, string Structure)? c) => new("tu",
            new XAttribute("creationdate", u.Created.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")),
            new XAttribute("changedate", u.Modified.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")),
            new XAttribute("srclang", u.SourceLanguage),
            u.UsageCount > 0 ? new XAttribute("usagecount", u.UsageCount) : null,
            c != null ? new XElement("prop", new XAttribute("type", PrevProp), c.Value.Previous) : null,
            c is { Next.Length: > 0 } ? new XElement("prop", new XAttribute("type", NextProp), c.Value.Next) : null,
            (c?.Structure ?? u.Context) is { Length: > 0 } st ? new XElement("prop", new XAttribute("type", StructureProp), st) : null,
            new XElement("tuv", new XAttribute(XNamespace.Xml + "lang", u.SourceLanguage), new XElement("seg", TaggedToSeg(u.Source))),
            new XElement("tuv", new XAttribute(XNamespace.Xml + "lang", u.TargetLanguage), new XElement("seg", TaggedToSeg(u.Target))));
        var body = new XElement("body", units.SelectMany(u =>
        {
            var contexts = tm is TranslationMemory local ? local.ContextsOf(u.Id) : [];
            return contexts.Count == 0
                ? [Tu(u, null)]
                : contexts.Select(c => Tu(u, c));
        }));
        var x = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("tmx", new XAttribute("version", "1.4"),
                new XElement("header",
                    new XAttribute("creationtool", "Sententiam converte"),
                    new XAttribute("creationtoolversion", "0.1"),
                    new XAttribute("segtype", "sentence"),
                    new XAttribute("o-tmf", "SententiamConverte"),
                    new XAttribute("adminlang", "en-US"),
                    new XAttribute("srclang", srcLang),
                    new XAttribute("datatype", "xml")),
                body));
        x.Save(output);
        return units.Count;
    }
}
