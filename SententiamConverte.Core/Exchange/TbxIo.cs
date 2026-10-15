using System.Xml.Linq;
using SententiamConverte.Core.Terminology;

using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Exchange;

/// <summary>
/// Terminologie-Austausch: TBX (v2 „martif“ und v3), außerdem verbreitete XML-Terminologieexporte (conceptGrp/languageGrp),
/// weil die Elemente nach lokalem Namen gesucht werden.
/// </summary>
public static class TbxIo
{
    private static readonly HashSet<string> EntryNames = ["termEntry", "conceptEntry", "conceptGrp"];
    private static readonly HashSet<string> LangNames = ["langSet", "langSec", "languageGrp"];

    public static int Import(Stream input, ITermbase termbase)
    {
        var x = XDocument.Load(input);
        var count = 0;
        foreach (var entry in x.Descendants().Where(e => EntryNames.Contains(e.Name.LocalName)))
        {
            var terms = new List<(string, string, TermStatus)>();
            foreach (var langSet in entry.Descendants().Where(e => LangNames.Contains(e.Name.LocalName)))
            {
                var lang = (string?)langSet.Attribute(XNamespace.Xml + "lang")
                           ?? (string?)langSet.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Attribute("lang")
                           ?? (string?)langSet.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Attribute("type");
                if (string.IsNullOrEmpty(lang)) continue;
                foreach (var term in langSet.Descendants().Where(e => e.Name.LocalName == "term"))
                {
                    var holder = term.Parent!;
                    var statusText = string.Join(' ', holder.Descendants()
                        .Where(e => e.Name.LocalName is "termNote" or "descrip")
                        .Select(e => e.Value)).ToLowerInvariant();
                    var status = statusText.Contains("deprecated") || statusText.Contains("forbidden") || statusText.Contains("superseded") || statusText.Contains("verboten")
                        ? TermStatus.Forbidden
                        : statusText.Contains("admitted") || statusText.Contains("zugelassen") ? TermStatus.Admitted : TermStatus.Preferred;
                    if (term.Value.Trim().Length > 0) terms.Add((lang, term.Value.Trim(), status));
                }
            }
            if (terms.Count == 0) continue;
            var definition = entry.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "descrip" && ((string?)e.Attribute("type"))?.ToLowerInvariant() == "definition")?.Value;
            var domain = entry.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "descrip" && ((string?)e.Attribute("type"))?.ToLowerInvariant() is "subjectfield" or "domain")?.Value;
            termbase.AddConcept(terms, definition, domain, "TBX-Import");
            count++;
        }
        return count;
    }

    public static int Export(ITermbase termbase, Stream output)
    {
        var concepts = termbase.AllConcepts().ToList();
        var body = new XElement("body",
            concepts.Select(c => new XElement("termEntry", new XAttribute("id", "c" + c.Id),
                c.Definition != null ? new XElement("descrip", new XAttribute("type", "definition"), c.Definition) : null,
                c.Domain != null ? new XElement("descrip", new XAttribute("type", "subjectField"), c.Domain) : null,
                c.Terms.GroupBy(t => t.Language).Select(g => new XElement("langSet",
                    new XAttribute(XNamespace.Xml + "lang", g.Key),
                    g.Select(t => new XElement("tig",
                        new XElement("term", t.Text),
                        new XElement("termNote", new XAttribute("type", "administrativeStatus"), t.Status switch
                        {
                            TermStatus.Forbidden => "deprecatedTerm-admn-sts",
                            TermStatus.Admitted => "admittedTerm-admn-sts",
                            _ => "preferredTerm-admn-sts",
                        }),
                        t.Note != null ? new XElement("note", t.Note) : null)))))));
        var x = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("martif", new XAttribute("type", "TBX"), new XAttribute(XNamespace.Xml + "lang", "en"),
                new XElement("martifHeader", new XElement("fileDesc", new XElement("sourceDesc", new XElement("p", "Sententiam converte")))),
                new XElement("text", body)));
        x.Save(output);
        return concepts.Count;
    }
}
