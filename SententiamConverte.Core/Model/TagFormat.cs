using System.Text.RegularExpressions;

namespace SententiamConverte.Core.Model;

/// <summary>Sichtbare Formatierung, für die ein Tag-Paar steht.</summary>
[Flags]
public enum TextFormat
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Superscript = 8,
    Subscript = 16,
    Strike = 32,
    Link = 64,
}

/// <summary>
/// Liest aus den Tag-Daten eines Absatzes (vom Dateifilter abgelegt), welche Formatierung ein Tag-Paar trägt –
/// damit der Editor fett, kursiv usw. zeigen kann statt nur „⟨1⟩…⟨/1⟩“.
/// </summary>
public static partial class TagFormat
{
    [GeneratedRegex("<w:(?<tag>b|i|strike|dstrike)(?:\\s+w:val=\"(?<val>[^\"]*)\")?\\s*/>")]
    private static partial Regex OnOff();

    [GeneratedRegex("<w:u\\s+w:val=\"(?<val>[^\"]*)\"")]
    private static partial Regex Underline();

    [GeneratedRegex("<w:vertAlign\\s+w:val=\"(?<val>[^\"]*)\"")]
    private static partial Regex VertAlign();

    public static TextFormat Of(string? data)
    {
        if (string.IsNullOrEmpty(data)) return TextFormat.None;
        if (data.StartsWith("h:")) return TextFormat.Link;
        if (data.StartsWith("pair:")) return FromMarkup(data[5..].Split('\u0001')[0]);
        if (data.StartsWith("f:")) return FromWordProps(data[2..]);
        return TextFormat.None;
    }

    /// <summary>Formatierung eines Tag-Paars aus den Tag-Daten des Absatzes.</summary>
    public static TextFormat Of(IReadOnlyDictionary<int, string>? tagData, int id) =>
        tagData != null && tagData.TryGetValue(id, out var d) ? Of(d) : TextFormat.None;

    /// <summary>Kurze Beschreibung, z. B. für Tooltips: „fett, kursiv“.</summary>
    public static string Describe(TextFormat f)
    {
        var parts = new List<string>();
        if (f.HasFlag(TextFormat.Bold)) parts.Add("fett");
        if (f.HasFlag(TextFormat.Italic)) parts.Add("kursiv");
        if (f.HasFlag(TextFormat.Underline)) parts.Add("unterstrichen");
        if (f.HasFlag(TextFormat.Strike)) parts.Add("durchgestrichen");
        if (f.HasFlag(TextFormat.Superscript)) parts.Add("hochgestellt");
        if (f.HasFlag(TextFormat.Subscript)) parts.Add("tiefgestellt");
        if (f.HasFlag(TextFormat.Link)) parts.Add("Verweis");
        return string.Join(", ", parts);
    }

    private static TextFormat FromWordProps(string rPr)
    {
        var f = TextFormat.None;
        foreach (Match m in OnOff().Matches(rPr))
        {
            var on = m.Groups["val"].Success is false || m.Groups["val"].Value is not ("0" or "false" or "off");
            if (!on) continue;
            f |= m.Groups["tag"].Value switch { "b" => TextFormat.Bold, "i" => TextFormat.Italic, _ => TextFormat.Strike };
        }
        if (Underline().Match(rPr) is { Success: true } u && u.Groups["val"].Value != "none") f |= TextFormat.Underline;
        if (VertAlign().Match(rPr) is { Success: true } v)
            f |= v.Groups["val"].Value switch { "superscript" => TextFormat.Superscript, "subscript" => TextFormat.Subscript, _ => TextFormat.None };
        return f;
    }

    /// <summary>HTML- und Markdown-Auszeichnungen.</summary>
    private static TextFormat FromMarkup(string open)
    {
        var o = open.Trim().ToLowerInvariant();
        if (o is "**" or "__") return TextFormat.Bold;
        if (o is "*" or "_") return TextFormat.Italic;
        if (o is "~~") return TextFormat.Strike;
        if (o.StartsWith('[')) return TextFormat.Link;
        if (!o.StartsWith('<')) return TextFormat.None;
        var name = new string(o.Skip(1).TakeWhile(char.IsLetterOrDigit).ToArray());
        var f = name switch
        {
            "b" or "strong" => TextFormat.Bold,
            "i" or "em" or "cite" or "dfn" => TextFormat.Italic,
            "u" or "ins" => TextFormat.Underline,
            "s" or "del" or "strike" => TextFormat.Strike,
            "sup" => TextFormat.Superscript,
            "sub" => TextFormat.Subscript,
            "a" => TextFormat.Link,
            _ => TextFormat.None,
        };
        if (o.Contains("font-weight:bold") || o.Contains("font-weight: bold")) f |= TextFormat.Bold;
        if (o.Contains("font-style:italic") || o.Contains("font-style: italic")) f |= TextFormat.Italic;
        return f;
    }
}
