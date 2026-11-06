using System.Text;
using System.Text.RegularExpressions;

namespace SententiamConverte.Core.Model;

/// <summary>
/// Tags im Segmenttext: ⟨1⟩…⟨/1⟩ für Formatierungspaare, ⟨2/⟩ für Platzhalter
/// (Bilder, Felder, Zeilenumbrüche). Die Klammern U+27E8/U+27E9 kommen in normalem Text praktisch nie vor,
/// lassen sich aber in jedem Textfeld anzeigen und bearbeiten.
/// </summary>
public static partial class TagText
{
    public const char Open = '⟨';
    public const char Close = '⟩';

    public enum TagKind { Start, End, Placeholder }

    public readonly record struct Tag(TagKind Kind, int Id, int Index, int Length);

    /// <summary>Ein Stück Segmenttext: entweder Text oder ein Tag.</summary>
    public readonly record struct Part(string? Text, Tag? Tag);

    [GeneratedRegex("⟨(/?)(\\d+)(/?)⟩")]
    private static partial Regex TagRegex();

    public static string Start(int id) => $"{Open}{id}{Close}";
    public static string End(int id) => $"{Open}/{id}{Close}";
    public static string Placeholder(int id) => $"{Open}{id}/{Close}";

    public static IEnumerable<Tag> Tags(string text)
    {
        foreach (Match m in TagRegex().Matches(text))
        {
            var kind = m.Groups[1].Length > 0 ? TagKind.End : m.Groups[3].Length > 0 ? TagKind.Placeholder : TagKind.Start;
            yield return new Tag(kind, int.Parse(m.Groups[2].Value), m.Index, m.Length);
        }
    }

    public static List<Part> Split(string text)
    {
        var parts = new List<Part>();
        var pos = 0;
        foreach (var tag in Tags(text))
        {
            if (tag.Index > pos) parts.Add(new Part(text[pos..tag.Index], null));
            parts.Add(new Part(null, tag));
            pos = tag.Index + tag.Length;
        }
        if (pos < text.Length) parts.Add(new Part(text[pos..], null));
        return parts;
    }

    public static string StripTags(string text) => TagRegex().Replace(text, "");

    /// <summary>Tags als vergleichbare Signatur, z. B. "S1 E1 P2".</summary>
    public static string TagSignature(string text) =>
        string.Join(' ', Tags(text).Select(t => $"{t.Kind.ToString()[0]}{t.Id}").OrderBy(x => x));

    /// <summary>Prüft, ob jedes Start-Tag ein passendes End-Tag hat und richtig verschachtelt ist.</summary>
    public static bool IsBalanced(string text)
    {
        var stack = new Stack<int>();
        foreach (var t in Tags(text))
        {
            if (t.Kind == TagKind.Start) stack.Push(t.Id);
            else if (t.Kind == TagKind.End && (stack.Count == 0 || stack.Pop() != t.Id)) return false;
        }
        return stack.Count == 0;
    }

    /// <summary>Tiefe der offenen Tag-Paare an jeder Zeichenposition (für die Segmentierung).</summary>
    public static int[] OpenDepth(string text)
    {
        var depth = new int[text.Length + 1];
        var d = 0;
        var tags = Tags(text).ToList();
        var ti = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            while (ti < tags.Count && tags[ti].Index + tags[ti].Length == i)
            {
                if (tags[ti].Kind == TagKind.Start) d++;
                else if (tags[ti].Kind == TagKind.End) d--;
                ti++;
            }
            depth[i] = d;
        }
        return depth;
    }

    /// <summary>Tags, die eine KI leicht verändert zurückgibt: „⟨/1>“, „&lt;/1⟩“, „⟨ 1 ⟩“.</summary>
    [GeneratedRegex("[⟨<]\\s*(/?)\\s*(\\d+)\\s*(/?)\\s*[⟩>]")]
    private static partial Regex LooseTagRegex();

    /// <summary>
    /// Räumt die Tags einer KI-Übersetzung auf: verformte Tags werden repariert, Tags, die die Quelle nicht hat
    /// (oder öfter als die Quelle), entfernt – samt des Leerraums, den sie hinterlassen –, fehlende ergänzt.
    /// Kleine Modelle erfinden gern Tags, wo die Quelle keine hat.
    /// </summary>
    public static string Repair(string source, string target)
    {
        target = LooseTagRegex().Replace(target, m =>
            m.Value.Contains('<') && !m.Value.Contains('⟨') && !m.Value.Contains('⟩')
                ? m.Value                                                    // echtes HTML/XML im Text, kein Tag
                : $"{Open}{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}{Close}");
        var allowed = Tags(source).GroupBy(t => (t.Kind, t.Id)).ToDictionary(g => g.Key, g => g.Count());
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var t in Tags(target).ToList())
        {
            sb.Append(target, pos, t.Index - pos);
            pos = t.Index + t.Length;
            if (allowed.TryGetValue((t.Kind, t.Id), out var left) && left > 0)
            {
                allowed[(t.Kind, t.Id)] = left - 1;
                sb.Append(target, t.Index, t.Length);
                continue;
            }
            // überzähliges Tag weglassen; „Wort ⟨1⟩ Wort“ → „Wort Wort“, „Wort ⟨2/⟩.“ → „Wort.“
            var spaceBefore = sb.Length > 0 && sb[^1] == ' ';
            var next = pos < target.Length ? target[pos] : '\0';
            if (spaceBefore && (next == ' ' || next == '\0' || char.IsPunctuation(next) && next is not ('(' or '[' or '„' or '“' or '"')))
                sb.Length--;
        }
        sb.Append(target, pos, target.Length - pos);
        return EnsureTags(source, sb.ToString().Trim());
    }

    /// <summary>
    /// Übernimmt die Tags aus <paramref name="source"/> in einen Text ohne Tags:
    /// Platzhalter ans Ende, Paare um den ganzen Text. Notlösung, wenn eine KI Tags verliert.
    /// </summary>
    public static string EnsureTags(string source, string target)
    {
        var missing = Tags(source).Where(t => !target.Contains(source.Substring(t.Index, t.Length))).ToList();
        if (missing.Count == 0) return target;
        var sb = new StringBuilder();
        foreach (var t in missing.Where(t => t.Kind == TagKind.Start)) sb.Append(Start(t.Id));
        sb.Append(target);
        foreach (var t in missing.Where(t => t.Kind == TagKind.End)) sb.Append(End(t.Id));
        foreach (var t in missing.Where(t => t.Kind == TagKind.Placeholder)) sb.Append(Placeholder(t.Id));
        return sb.ToString();
    }
}
