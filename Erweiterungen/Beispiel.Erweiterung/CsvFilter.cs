using System.Globalization;
using System.Text;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace Beispiel.Erweiterung;

/// <summary>
/// Dateiformat: CSV-Tabelle. Die erste Spalte wird übersetzt, die Übersetzung landet in der zweiten Spalte.
/// Trennzeichen (Semikolon, Komma, Tabulator), Anführungszeichen, Zeilenenden und BOM bleiben erhalten.
/// </summary>
public sealed class CsvFilter : IFileFilter
{
    public string Id => "beispiel.csv";
    public string Name => "CSV-Tabelle (Spalte 1 → Spalte 2)";
    public IReadOnlyList<string> Extensions { get; } = [".csv"];

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var csv = CsvText.Parse(input);
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        for (var i = 0; i < csv.Rows.Count; i++)
        {
            if (csv.Rows[i].Count == 0 || !Segmenter.HasTranslatableText(csv.Rows[i][0])) continue;
            var p = new Paragraph { Locator = i.ToString(CultureInfo.InvariantCulture) };
            segmenter.Segment(p, csv.Rows[i][0]);
            doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        var csv = CsvText.Parse(original);
        foreach (var p in document.Paragraphs)
        {
            var row = csv.Rows[int.Parse(p.Locator, CultureInfo.InvariantCulture)];
            var target = TagText.StripTags(p.JoinTarget());
            if (row.Count < 2) row.Add(target);
            else row[1] = target;
        }
        csv.Write(output);
    }
}

/// <summary>Kleiner CSV-Leser und -Schreiber nach RFC 4180.</summary>
internal sealed class CsvText
{
    public List<List<string>> Rows { get; } = new();
    public char Separator { get; private init; } = ',';
    public string NewLine { get; private init; } = "\n";
    public bool TrailingNewLine { get; private init; }
    public bool Bom { get; private init; }

    public static CsvText Parse(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var firstLine = text.Split('\n')[0];
        var separator = new[] { ';', ',', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();
        var csv = new CsvText
        {
            Separator = separator,
            NewLine = text.Contains("\r\n") ? "\r\n" : "\n",
            TrailingNewLine = text.EndsWith('\n'),
            Bom = bom,
        };

        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                csv.Rows.Add(row);
                row = new List<string>();
            }
            else if (c != '\r') field.Append(c);
        }
        if (!csv.TrailingNewLine)
        {
            row.Add(field.ToString());
            csv.Rows.Add(row);
        }
        return csv;
    }

    public void Write(Stream output)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(Bom), leaveOpen: true);
        writer.Write(string.Join(NewLine, Rows.Select(r => string.Join(Separator, r.Select(Quote)))));
        if (TrailingNewLine) writer.Write(NewLine);
    }

    private string Quote(string field) =>
        field.IndexOfAny([Separator, '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}
