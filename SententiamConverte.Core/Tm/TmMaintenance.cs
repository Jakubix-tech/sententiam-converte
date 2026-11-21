using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tm;

/// <summary>Eine geplante Änderung beim Suchen und Ersetzen (für die Vorschau).</summary>
public sealed record TmReplacement(TmUnit Unit, string Source, string Target);

/// <summary>Ergebnis der Bereinigung.</summary>
public sealed record TmCleanResult(int Duplicates, int EmptyTargets, int Identical);

/// <summary>
/// TM-Pflege über viele Einträge: Suchen und Ersetzen (auch mit regulären Ausdrücken, nur in Quelle oder Ziel),
/// Dubletten und unbrauchbare Einträge entfernen, uneinheitliche Übersetzungen finden. Tags bleiben unberührt.
/// </summary>
public static class TmMaintenance
{
    private static Regex Pattern(string find, bool matchCase, bool regex, bool wholeWords)
    {
        var pattern = regex ? find : Regex.Escape(find);
        if (wholeWords) pattern = $@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])";
        return new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>Text ersetzen, aber nie in Tags (⟨1⟩ …) hinein.</summary>
    private static string ReplaceOutsideTags(string text, Regex rx, string replacement)
    {
        if (!text.Contains(TagText.Open)) return rx.Replace(text, replacement);
        return string.Concat(TagText.Split(text).Select(p => p.Tag is { } t ? text.Substring(t.Index, t.Length) : rx.Replace(p.Text!, replacement)));
    }

    /// <summary>Was ersetzt würde (höchstens <paramref name="max"/> Einträge).</summary>
    public static List<TmReplacement> Preview(ITranslationMemory tm, string find, string replace, bool inSource, bool inTarget,
        bool matchCase = false, bool regex = false, bool wholeWords = false, int max = int.MaxValue)
    {
        if (find.Length == 0 || (!inSource && !inTarget)) return [];
        var rx = Pattern(find, matchCase, regex, wholeWords);
        var list = new List<TmReplacement>();
        foreach (var u in tm.All())
        {
            var source = inSource ? ReplaceOutsideTags(u.Source, rx, replace) : u.Source;
            var target = inTarget ? ReplaceOutsideTags(u.Target, rx, replace) : u.Target;
            if (source == u.Source && target == u.Target) continue;
            list.Add(new TmReplacement(u, source, target));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>Alle Treffer ersetzen; liefert die Zahl geänderter Einträge.</summary>
    public static int ReplaceAll(ITranslationMemory tm, string find, string replace, bool inSource, bool inTarget,
        bool matchCase = false, bool regex = false, bool wholeWords = false)
    {
        var changes = Preview(tm, find, replace, inSource, inTarget, matchCase, regex, wholeWords);
        foreach (var c in changes) tm.Update(c.Unit.Id, c.Source, c.Target);
        return changes.Count;
    }

    private static string Key(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    /// <summary>
    /// Bereinigen: gleiche Einträge (Quelle und Ziel, Leerraum egal) in den neuesten zusammenführen; auf Wunsch
    /// auch Einträge ohne Übersetzung und solche, deren Ziel nur die Quelle wiederholt (außer Zahlen, Namen u. Ä.).
    /// </summary>
    public static TmCleanResult Clean(ITranslationMemory tm, bool duplicates = true, bool emptyTargets = true, bool identical = false)
    {
        var all = tm.All().ToList();
        var delete = new HashSet<long>();
        int dup = 0, empty = 0, same = 0;
        if (emptyTargets)
            foreach (var u in all.Where(u => TagText.StripTags(u.Target).Trim().Length == 0))
                if (delete.Add(u.Id)) empty++;
        if (identical)
            foreach (var u in all.Where(u => u.Source == u.Target && TagText.StripTags(u.Source).Count(char.IsLetter) > 3
                                              && TagText.StripTags(u.Source).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1))
                if (delete.Add(u.Id)) same++;
        foreach (var id in delete) tm.Delete(id);
        // Dubletten (nur Leerraum verschieden) in den neuesten Eintrag zusammenführen – ihre Satzzusammenhänge bleiben
        if (duplicates)
            foreach (var g in all.Where(u => !delete.Contains(u.Id))
                         .GroupBy(u => (Languages.Primary(u.SourceLanguage), Languages.Primary(u.TargetLanguage), Key(u.Source), Key(u.Target))))
            {
                var keep = g.OrderByDescending(u => u.Modified).ThenByDescending(u => u.Id).First();
                foreach (var old in g.Where(u => u.Id != keep.Id))
                {
                    tm.Update(old.Id, keep.Source, keep.Target);
                    dup++;
                }
            }
        return new TmCleanResult(dup, empty, same);
    }

    /// <summary>Gleiche Quelle, verschiedene Übersetzungen – zum Prüfen und Vereinheitlichen.</summary>
    public static List<(string Source, List<TmUnit> Units)> Inconsistent(ITranslationMemory tm, int max = 200) =>
        tm.All().GroupBy(u => (Languages.Primary(u.TargetLanguage), Key(u.Source)))
            .Where(g => g.Select(u => Key(u.Target)).Distinct().Count() > 1)
            .Take(max)
            .Select(g => (g.First().Source, g.OrderByDescending(u => u.Modified).ToList()))
            .ToList();
}
