using System.Xml;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
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

    /// <summary>
    /// Importiert alle Einträge; die Datei wird schrittweise gelesen (auch sehr große Exporte wie IATE).
    /// <paramref name="languages"/>: nur Benennungen dieser Sprachen, und nur Einträge, die mindestens zwei davon haben.
    /// </summary>
    public static int Import(Stream input, ITermbase termbase, IReadOnlyCollection<string>? languages = null,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        long seen = 0;
        IEnumerable<XElement> Entries()
        {
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && EntryNames.Contains(reader.LocalName))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++seen % 2000 == 0) progress?.Report(seen);
                    yield return (XElement)XNode.ReadFrom(reader);
                }
                else reader.Read();
            }
            progress?.Report(seen);
        }

        var concepts = Concepts(Entries(), languages);
        if (termbase is Termbase local) return local.AddConcepts(concepts);
        var count = 0;
        foreach (var c in concepts)
        {
            termbase.AddConcept(c.Terms, c.Definition, c.Domain, c.Source);
            count++;
        }
        return count;
    }

    private static IEnumerable<(IReadOnlyList<(string Lang, string Text, TermStatus Status)> Terms, string? Definition, string? Domain, string? Source)> Concepts(
        IEnumerable<XElement> entries, IReadOnlyCollection<string>? languages)
    {
        foreach (var entry in entries)
        {
            var terms = new List<(string Lang, string Text, TermStatus Status)>();
            foreach (var langSet in entry.Descendants().Where(e => LangNames.Contains(e.Name.LocalName)))
            {
                var lang = (string?)langSet.Attribute(XNamespace.Xml + "lang")
                           ?? (string?)langSet.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Attribute("lang")
                           ?? (string?)langSet.Elements().FirstOrDefault(e => e.Name.LocalName == "language")?.Attribute("type");
                if (string.IsNullOrEmpty(lang)) continue;
                if (languages != null && !languages.Any(l => Languages.Matches(l, lang))) continue;
                foreach (var term in langSet.Descendants().Where(e => e.Name.LocalName == "term"))
                {
                    var holder = term.Parent!;
                    var statusText = string.Join(' ', holder.Descendants()
                        .Where(e => e.Name.LocalName is "termNote" or "descrip")
                        .Select(e => e.Value)).ToLowerInvariant();
                    var status = statusText.Contains("deprecated") || statusText.Contains("forbidden") || statusText.Contains("superseded") || statusText.Contains("verboten")
                                 || statusText.Contains("obsolete")
                        ? TermStatus.Forbidden
                        : statusText.Contains("admitted") || statusText.Contains("zugelassen") ? TermStatus.Admitted : TermStatus.Preferred;
                    if (term.Value.Trim().Length > 0) terms.Add((lang, term.Value.Trim(), status));
                }
            }
            if (terms.Count == 0) continue;
            if (languages != null && terms.Select(t => Languages.Primary(t.Lang)).Distinct().Count() < Math.Min(2, languages.Count)) continue;
            var definition = entry.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "descrip" && ((string?)e.Attribute("type"))?.ToLowerInvariant() == "definition")?.Value;
            var domain = entry.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "descrip" && ((string?)e.Attribute("type"))?.ToLowerInvariant() is "subjectfield" or "domain")?.Value;
            yield return (terms, definition, domain, "TBX-Import");
        }
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
