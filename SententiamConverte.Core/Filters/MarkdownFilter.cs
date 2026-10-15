using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

/// <summary>
/// Markdown: Absätze (auch über mehrere Zeilen), Überschriften, Listen, Zitate und Tabellenzellen.
/// Codeblöcke bleiben unberührt; **fett**, *kursiv*, `Code`, [Links](url) und Bilder werden zu Tags.
/// </summary>
public sealed partial class MarkdownFilter : IFileFilter
{
    public string Id => "md";
    public string Name => "Markdown";
    public IReadOnlyList<string> Extensions { get; } = [".md", ".markdown"];

    [GeneratedRegex(@"^(\s*(?:#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s?)*)(.*?)(\s*)$")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"(`+)(.+?)\1|!\[[^\]]*\]\([^)]*\)|\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\)|(?<b>\*\*|__)(?<bt>.+?)\k<b>|(?<i>[*_])(?<it>[^*_\s][^*_]*?)\k<i>|<[^>]+>")]
    private static partial Regex InlineRegex();

    private sealed record Unit(string Locator, int FirstLine, int LastLine, int Cell, string Prefix, string Text, string Suffix);

    private static List<Unit> Units(string[] lines)
    {
        var units = new List<Unit>();
        var inFence = false;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) { inFence = !inFence; i++; continue; }
            if (inFence || line.Trim().Length == 0 || line.StartsWith("    ") || line.StartsWith('\t') || TableRule().IsMatch(line)) { i++; continue; }

            if (TableRow().IsMatch(line))
            {
                var cells = line.Split('|');
                for (var c = 1; c < cells.Length - 1; c++)
                    if (cells[c].Trim().Any(char.IsLetter))
                        units.Add(new Unit($"{i}:{c}", i, i, c, "", cells[c].Trim(), ""));
                i++;
                continue;
            }

            var m = LinePrefix().Match(line);
            var prefix = m.Groups[1].Value;
            var text = new StringBuilder(m.Groups[2].Value);
            var start = i;
            // Fließtext ohne Präfix über Folgezeilen fortsetzen
            var isBlockStart = prefix.Trim().StartsWith('#');
            while (!isBlockStart && i + 1 < lines.Length && lines[i + 1].Trim().Length > 0
                   && LinePrefix().Match(lines[i + 1]).Groups[1].Value.Length == 0
                   && !TableRow().IsMatch(lines[i + 1]) && !lines[i + 1].TrimStart().StartsWith("```"))
            {
                i++;
                text.Append(' ').Append(lines[i].Trim());
            }
            if (text.ToString().Any(char.IsLetter))
                units.Add(new Unit($"{start}-{i}", start, i, -1, prefix, text.ToString(), ""));
            i++;
        }
        return units;
    }

    private static string ToTagged(string text, Dictionary<int, string> tagData) =>
        InlineRegex().Replace(text, m =>
        {
            if (m.Groups["lt"].Success)
            {
                var id = Markup.Pair(tagData, "[", $"]({m.Groups["lu"].Value})");
                return TagText.Start(id) + ToTagged(m.Groups["lt"].Value, tagData) + TagText.End(id);
            }
            if (m.Groups["bt"].Success)
            {
                var id = Markup.Pair(tagData, m.Groups["b"].Value, m.Groups["b"].Value);
                return TagText.Start(id) + ToTagged(m.Groups["bt"].Value, tagData) + TagText.End(id);
            }
            if (m.Groups["it"].Success)
            {
                var id = Markup.Pair(tagData, m.Groups["i"].Value, m.Groups["i"].Value);
                return TagText.Start(id) + ToTagged(m.Groups["it"].Value, tagData) + TagText.End(id);
            }
            return Markup.Raw(tagData, m.Value);
        });

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var lines = new StreamReader(input, Encoding.UTF8).ReadToEnd().Replace("\r\n", "\n").Split('\n');
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        foreach (var u in Units(lines))
        {
            var para = new Paragraph { Locator = u.Locator, Context = u.Prefix.Trim().Length > 0 ? u.Prefix.Trim() : null };
            var text = ToTagged(u.Text, para.TagData);
            if (!Segmenter.HasTranslatableText(text)) continue;
            segmenter.Segment(para, text);
            doc.Paragraphs.Add(para);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var raw = new StreamReader(original, Encoding.UTF8).ReadToEnd();
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        var units = Units(lines.ToArray());
        var removed = new HashSet<int>();

        foreach (var u in units)
        {
            if (!byLocator.TryGetValue(u.Locator, out var p)) continue;
            var text = Markup.Unpack(p.JoinTarget().Trim(), p.TagData);
            if (u.Cell >= 0)
            {
                var cells = lines[u.FirstLine].Split('|');
                cells[u.Cell] = " " + text + " ";
                lines[u.FirstLine] = string.Join('|', cells);
            }
            else
            {
                lines[u.FirstLine] = u.Prefix + text;
                for (var l = u.FirstLine + 1; l <= u.LastLine; l++) removed.Add(l);
            }
        }
        var result = lines.Where((_, idx) => !removed.Contains(idx));
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(string.Join(newline, result));
    }
}
