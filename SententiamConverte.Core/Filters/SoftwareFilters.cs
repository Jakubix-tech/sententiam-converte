using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Filters;

internal static class SimpleUnits
{
    /// <summary>Ein Text als Absatz; Software-Platzhalter werden geschützt.</summary>
    public static Paragraph? Make(string locator, string text, string? context, Segmenter segmenter, string? existingTarget = null, bool fuzzy = false)
    {
        var para = new Paragraph { Locator = locator, Context = context };
        var tagged = Markup.Protect(text, para.TagData);
        if (!Segmenter.HasTranslatableText(tagged)) return null;
        // Software-Texte nicht in Sätze zerlegen: ein Eintrag = ein Segment
        para.Segments = [new Segment { Source = tagged, TrailingWhitespace = "" }];
        if (!string.IsNullOrEmpty(existingTarget))
        {
            var tmp = new Dictionary<int, string>(para.TagData);
            var t = Markup.Protect(existingTarget, tmp);
            // dieselben Platzhalter auf dieselben Nummern abbilden
            foreach (var kv in tmp.Where(kv => !para.TagData.ContainsKey(kv.Key)))
            {
                var same = para.TagData.FirstOrDefault(x => x.Value == kv.Value);
                if (same.Key != 0) t = t.Replace(TagText.Placeholder(kv.Key), TagText.Placeholder(same.Key));
            }
            para.Segments[0].Target = t;
            para.Segments[0].Status = fuzzy ? SegmentStatus.Draft : SegmentStatus.Translated;
        }
        return para;
    }

    public static string Text(Paragraph p) => Markup.Unpack(p.JoinTarget(), p.TagData);
}

/// <summary>.NET-Ressourcen (.resx): Texte in data/value, Kommentar als Kontext.</summary>
public sealed class ResxFilter : IFileFilter
{
    public string Id => "resx";
    public string Name => ".NET-Ressourcen (RESX)";
    public IReadOnlyList<string> Extensions { get; } = [".resx"];

    private static IEnumerable<XElement> Entries(XDocument x) =>
        x.Root!.Elements("data").Where(d =>
            d.Attribute("type") == null && d.Attribute("mimetype") == null &&
            ((string?)d.Attribute("name"))?.StartsWith(">>") == false && ((string?)d.Attribute("name"))?.StartsWith("$") == false &&
            d.Element("value") != null);

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var x = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        foreach (var d in Entries(x))
        {
            var name = (string)d.Attribute("name")!;
            var comment = d.Element("comment")?.Value;
            if (SimpleUnits.Make(name, d.Element("value")!.Value, comment is { Length: > 0 } ? $"{name} – {comment}" : name, segmenter) is { } p)
                doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var x = XDocument.Load(original, LoadOptions.PreserveWhitespace);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        foreach (var d in Entries(x))
            if (byLocator.TryGetValue((string)d.Attribute("name")!, out var p))
                d.Element("value")!.Value = SimpleUnits.Text(p);
        x.Save(output);
    }

    public string TargetFileName(string sourceFileName, string targetLanguage) =>
        Path.GetFileNameWithoutExtension(sourceFileName) + "." + targetLanguage + ".resx";
}

/// <summary>JSON-Sprachdateien: alle Zeichenketten-Werte (Schlüssel bleiben).</summary>
public sealed partial class JsonFilter : IFileFilter
{
    public string Id => "json";
    public string Name => "JSON";
    public IReadOnlyList<string> Extensions { get; } = [".json"];

    [GeneratedRegex(@"^(https?://|mailto:|www\.)\S+$|^[\w.\-]+@[\w.\-]+$|^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex NotText();

    private static IEnumerable<(string Path, JsonValue Value)> Strings(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) foreach (var r in Strings(kv.Value, path + "/" + kv.Key)) yield return r;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) foreach (var r in Strings(a[i], path + "/" + i)) yield return r;
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                yield return (path, v);
                break;
        }
    }

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var root = JsonNode.Parse(input, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        foreach (var (path, value) in Strings(root, ""))
        {
            var s = value.GetValue<string>();
            if (NotText().IsMatch(s.Trim())) continue;
            if (SimpleUnits.Make(path, s, path.TrimStart('/'), segmenter) is { } p) doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        foreach (var (path, value) in Strings(root, "").ToList())
            if (byLocator.TryGetValue(path, out var p))
                value.ReplaceWith(JsonValue.Create(SimpleUnits.Text(p)));
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        root!.WriteTo(writer);
    }
}

/// <summary>gettext (.po/.pot): msgid → msgstr, inkl. Pluralformen, Kontext und Kommentare.</summary>
public sealed partial class PoFilter : IFileFilter
{
    public string Id => "po";
    public string Name => "gettext (PO)";
    public IReadOnlyList<string> Extensions { get; } = [".po", ".pot"];

    public string TargetFileName(string sourceFileName, string targetLanguage) =>
        Path.GetFileNameWithoutExtension(sourceFileName) + "." + targetLanguage.Replace('-', '_') + ".po";

    private sealed class Entry
    {
        public List<string> Lines = new();
        public string? Context, Id, Plural;
        public Dictionary<int, string> Str = new();
        public bool Fuzzy;
        public List<string> Comments = new();
    }

    [GeneratedRegex(@"^(msgctxt|msgid_plural|msgid|msgstr(?:\[(\d+)\])?)\s+""(.*)""\s*$")]
    private static partial Regex KeyLine();

    private static string Unquote(string s) => Regex.Unescape(s);
    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static List<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            if (block.Trim().Length == 0) continue;
            var e = new Entry { Lines = block.Split('\n').ToList() };
            string? current = null;
            int idx = 0;
            foreach (var line in e.Lines)
            {
                if (line.StartsWith("#,") && line.Contains("fuzzy")) e.Fuzzy = true;
                if (line.StartsWith("#.") || line.StartsWith("# ")) e.Comments.Add(line[2..].Trim());
                var m = KeyLine().Match(line);
                if (m.Success)
                {
                    current = m.Groups[1].Value.StartsWith("msgstr") ? "msgstr" : m.Groups[1].Value;
                    idx = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                    Append(e, current, idx, Unquote(m.Groups[3].Value));
                }
                else if (line.StartsWith('"') && current != null)
                    Append(e, current, idx, Unquote(line.Trim().Trim('"')));
            }
            entries.Add(e);
        }
        return entries;
    }

    private static void Append(Entry e, string key, int idx, string value)
    {
        switch (key)
        {
            case "msgctxt": e.Context = (e.Context ?? "") + value; break;
            case "msgid": e.Id = (e.Id ?? "") + value; break;
            case "msgid_plural": e.Plural = (e.Plural ?? "") + value; break;
            default: e.Str[idx] = e.Str.GetValueOrDefault(idx, "") + value; break;
        }
    }

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var entries = Parse(new StreamReader(input, Encoding.UTF8).ReadToEnd());
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (string.IsNullOrEmpty(e.Id)) continue;     // Kopfeintrag
            var ctx = string.Join(" · ", new[] { e.Context }.Concat(e.Comments).Where(c => !string.IsNullOrEmpty(c)));
            if (SimpleUnits.Make($"{i}:0", e.Id, ctx, segmenter, e.Str.GetValueOrDefault(0), e.Fuzzy) is { } p)
            {
                if (e.Plural != null && SimpleUnits.Make($"{i}:1", e.Plural, ctx + " (Plural)", segmenter, e.Str.GetValueOrDefault(1), e.Fuzzy) is { } pl)
                {
                    doc.Paragraphs.Add(p);
                    doc.Paragraphs.Add(pl);
                }
                else doc.Paragraphs.Add(p);
            }
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var text = new StreamReader(original, Encoding.UTF8).ReadToEnd();
        var entries = Parse(text);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var hasSingular = byLocator.TryGetValue($"{i}:0", out var p0);
            var hasPlural = byLocator.TryGetValue($"{i}:1", out var p1);
            if (!hasSingular)
            {
                sb.Append(string.Join("\n", e.Lines)).Append("\n\n");
                continue;
            }
            var confirmed = p0!.Segments.All(s => s.IsConfirmed) && (!hasPlural || p1!.Segments.All(s => s.IsConfirmed));
            var kept = new List<string>();
            var inStr = false;
            foreach (var line in e.Lines)
            {
                if (line.StartsWith("msgstr")) { inStr = true; continue; }
                if (inStr && line.StartsWith('"')) continue;
                inStr = false;
                if (line.StartsWith("#,") && confirmed)
                {
                    var flags = line[2..].Split(',').Select(f => f.Trim()).Where(f => f != "fuzzy" && f.Length > 0).ToList();
                    if (flags.Count > 0) kept.Add("#, " + string.Join(", ", flags));
                    continue;
                }
                kept.Add(line);
            }
            var singular = p0.Segments.Any(s => s.Target.Length > 0) ? SimpleUnits.Text(p0) : "";
            if (e.Plural == null)
                kept.Add("msgstr " + Quote(singular));
            else
            {
                var plural = hasPlural && p1!.Segments.Any(s => s.Target.Length > 0) ? SimpleUnits.Text(p1) : "";
                var forms = Math.Max(2, e.Str.Count);
                kept.Add("msgstr[0] " + Quote(singular));
                for (var f = 1; f < forms; f++) kept.Add($"msgstr[{f}] " + Quote(plural));
            }
            sb.Append(string.Join("\n", kept)).Append("\n\n");
        }
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(sb.ToString().TrimEnd('\n') + "\n");
    }
}

/// <summary>Untertitel (.srt, .vtt): Zeitcodes bleiben, Zeilenumbrüche im Untertitel werden Platzhalter.</summary>
public sealed class SubtitleFilter : IFileFilter
{
    public string Id => "srt";
    public string Name => "Untertitel (SRT/VTT)";
    public IReadOnlyList<string> Extensions { get; } = [".srt", ".vtt"];

    private static List<(List<string> Head, List<string> Text)> Blocks(string text)
    {
        var result = new List<(List<string>, List<string>)>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").Trim('\n'), @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            var timing = lines.FindIndex(l => l.Contains("-->"));
            if (timing < 0) { result.Add((lines, new List<string>())); continue; }
            result.Add((lines.Take(timing + 1).ToList(), lines.Skip(timing + 1).ToList()));
        }
        return result;
    }

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var blocks = Blocks(new StreamReader(input, Encoding.UTF8).ReadToEnd());
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Text.Count == 0) continue;
            var para = new Paragraph { Locator = i.ToString(), Context = blocks[i].Head.Last() };
            var sb = new StringBuilder();
            for (var l = 0; l < blocks[i].Text.Count; l++)
            {
                if (l > 0) sb.Append(Markup.Raw(para.TagData, "\n"));
                sb.Append(Markup.Protect(blocks[i].Text[l], para.TagData));
            }
            var text = sb.ToString();
            if (!Segmenter.HasTranslatableText(text)) continue;
            para.Segments = [new Segment { Source = text }];
            doc.Paragraphs.Add(para);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var raw = new StreamReader(original, Encoding.UTF8).ReadToEnd();
        var blocks = Blocks(raw);
        var byLocator = document.Paragraphs.ToDictionary(p => p.Locator);
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            sb.Append(string.Join("\n", blocks[i].Head));
            var text = byLocator.TryGetValue(i.ToString(), out var p) ? SimpleUnits.Text(p) : string.Join("\n", blocks[i].Text);
            if (text.Length > 0) sb.Append('\n').Append(text);
            sb.Append("\n\n");
        }
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(sb.ToString().TrimEnd('\n') + "\n");
    }
}
