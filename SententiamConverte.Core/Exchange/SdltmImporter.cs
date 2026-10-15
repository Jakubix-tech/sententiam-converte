using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Tm;

using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Exchange;

/// <summary>
/// Liest TM-Dateien im Format .sdltm (SQLite-Datenbank, Segmente als XML in translation_units).
/// Dient allein der Übernahme vorhandener Daten (Interoperabilität); ein TMX-Export ist nicht nötig.
/// </summary>
public static class SdltmImporter
{
    public static int Import(string sdltmPath, ITranslationMemory target)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sdltmPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source_segment, target_segment, creation_date FROM translation_units";
        using var r = cmd.ExecuteReader();

        IEnumerable<TmEntry> Units()
        {
            while (r.Read())
            {
                var (srcLang, src) = Parse(r.GetString(0));
                var (tgtLang, tgt) = Parse(r.GetString(1));
                DateTime? created = r.IsDBNull(2) ? null : DateTime.TryParse(r.GetValue(2).ToString(), out var d) ? d : null;
                if (src.Length > 0 && tgt.Length > 0) yield return new TmEntry(srcLang, tgtLang, src, tgt, created, "import");
            }
        }
        return target.AddMany(Units());
    }

    /// <summary>Segment-XML des .sdltm-Formats → Text mit Tags.</summary>
    internal static (string Language, string Text) Parse(string xml)
    {
        var x = XElement.Parse(xml);
        var lang = x.Element("CultureName")?.Value ?? "";
        var sb = new StringBuilder();
        foreach (var el in x.Element("Elements")?.Elements() ?? [])
        {
            switch (el.Name.LocalName)
            {
                case "Text":
                    sb.Append(el.Element("Value")?.Value);
                    break;
                case "Tag":
                    var type = el.Element("Type")?.Value;
                    var id = int.TryParse(el.Element("Anchor")?.Value, out var a) ? a : 0;
                    sb.Append(type switch
                    {
                        "Start" => TagText.Start(id),
                        "End" => TagText.End(id),
                        _ => TagText.Placeholder(id == 0 ? 90 + sb.Length % 9 : id),
                    });
                    break;
            }
        }
        return (lang, sb.ToString());
    }
}
