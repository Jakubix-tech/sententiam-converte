using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Tags für textbasierte Formate: "raw:…" ist ein Platzhalter, der wörtlich zurückgeschrieben wird,
/// "pair:Anfang\u0001Ende" ein Tag-Paar mit seinem Anfangs- und Endtext.
/// </summary>
internal static partial class Markup
{
    private const char Sep = '\u0001';

    public static string Raw(Dictionary<int, string> tagData, string raw) => TagText.Placeholder(Add(tagData, "raw:" + raw));

    public static int Pair(Dictionary<int, string> tagData, string open, string close) => Add(tagData, "pair:" + open + Sep + close);

    public static int Add(Dictionary<int, string> tagData, string data)
    {
        var id = tagData.Count == 0 ? 1 : Math.Max(1, tagData.Keys.Max() + 1);
        tagData[id] = data;
        return id;
    }

    /// <summary>Setzt den Text mit Tags wieder in das Ausgangsformat um.</summary>
    public static string Unpack(string tagged, Dictionary<int, string> tagData, Func<string, string>? escape = null)
    {
        var sb = new StringBuilder();
        foreach (var part in TagText.Split(tagged))
        {
            if (part.Text != null) { sb.Append(escape == null ? part.Text : escape(part.Text)); continue; }
            var tag = part.Tag!.Value;
            if (!tagData.TryGetValue(tag.Id, out var data)) continue;
            if (data.StartsWith("raw:"))
            {
                sb.Append(data[4..]);
            }
            else if (data.StartsWith("pair:"))
            {
                var bits = data[5..].Split(Sep);
                sb.Append(tag.Kind == TagText.TagKind.Start ? bits[0] : bits.ElementAtOrDefault(1) ?? "");
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\{\{?[\w.:,\-# ]*\}?\}|%(?:\d+\$)?[-+ #0]*\d*(?:\.\d+)?[sdifuxXeEgGcp@]|\$\{[^}]*\}|</?[a-zA-Z][^<>]*>|\\[nrt]")]
    private static partial Regex CodeRegex();

    /// <summary>Software-Platzhalter ({0}, %s, ${name}, HTML-Tags, \n) in Platzhalter-Tags verwandeln.</summary>
    public static string Protect(string text, Dictionary<int, string> tagData) =>
        CodeRegex().Replace(text, m => Raw(tagData, m.Value));
}
