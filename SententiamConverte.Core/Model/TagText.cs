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
