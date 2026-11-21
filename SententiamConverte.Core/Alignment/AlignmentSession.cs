using System.Text;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Alignment;

/// <summary>
/// Ein Alignment in Arbeit: Paare aus Ausgangs- und Zielsätzen, die man von Hand korrigiert (verbinden, teilen,
/// Sätze verschieben, Text bearbeiten und neu segmentieren, bestätigen) und neu ausrichten lässt – bestätigte Paare
/// bleiben dabei als feste Anker stehen.
/// </summary>
public sealed class AlignmentSession
{
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }
    public List<AlignedPair> Pairs { get; }

    public AlignmentSession(string sourceLanguage, string targetLanguage, IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Pairs = Aligner.Align(source, target);
    }

    private AlignmentSession(string sourceLanguage, string targetLanguage, List<AlignedPair> pairs)
    {
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        Pairs = pairs;
    }

    /// <summary>Fertige Paare (z. B. aus einer zweisprachigen Datei): sicher und bestätigt.</summary>
    public static AlignmentSession FromPairs(string sourceLanguage, string targetLanguage, IEnumerable<(string Source, string Target)> pairs) =>
        new(sourceLanguage, targetLanguage, pairs.Select(p => new AlignedPair
        {
            Source = [p.Source], Target = [p.Target], Confidence = 1, Accepted = true, Confirmed = true,
        }).ToList());

    private static List<string> Side(AlignedPair p, bool source) => source ? p.Source : p.Target;

    /// <summary>Paar <paramref name="i"/> mit dem nächsten verbinden (z. B. zwei Ausgangssätze, eine Übersetzung).</summary>
    public void MergeWithNext(int i)
    {
        if (i < 0 || i + 1 >= Pairs.Count) return;
        var next = Pairs[i + 1];
        Pairs[i].Source.AddRange(next.Source);
        Pairs[i].Target.AddRange(next.Target);
        Pairs[i].Confirmed = false;
        Pairs.RemoveAt(i + 1);
        Touch(Pairs[i]);
    }

    /// <summary>Ein Paar mit mehreren Sätzen in einzelne Paare teilen: erster mit erstem, zweiter mit zweitem Satz …</summary>
    public void Split(int i)
    {
        if (i < 0 || i >= Pairs.Count) return;
        var p = Pairs[i];
        var n = Math.Max(p.Source.Count, p.Target.Count);
        if (n <= 1) return;
        var parts = Enumerable.Range(0, n).Select(k => new AlignedPair
        {
            Source = k < p.Source.Count ? [p.Source[k]] : [],
            Target = k < p.Target.Count ? [p.Target[k]] : [],
        }).ToList();
        foreach (var part in parts) Touch(part);
        Pairs.RemoveAt(i);
        Pairs.InsertRange(i, parts);
    }

    /// <summary>Den ersten Satz einer Seite ins vorige Paar schieben.</summary>
    public void MoveUp(int i, bool source)
    {
        if (i <= 0 || i >= Pairs.Count || Side(Pairs[i], source).Count == 0) return;
        var from = Side(Pairs[i], source);
        Side(Pairs[i - 1], source).Add(from[0]);
        from.RemoveAt(0);
        Touch(Pairs[i - 1]);
        Touch(Pairs[i]);
        RemoveEmpty();
    }

    /// <summary>Den letzten Satz einer Seite ins nächste Paar schieben (am Ende entsteht ein neues).</summary>
    public void MoveDown(int i, bool source)
    {
        if (i < 0 || i >= Pairs.Count || Side(Pairs[i], source).Count == 0) return;
        if (i + 1 == Pairs.Count) Pairs.Add(new AlignedPair());
        var from = Side(Pairs[i], source);
        Side(Pairs[i + 1], source).Insert(0, from[^1]);
        from.RemoveAt(from.Count - 1);
        Touch(Pairs[i]);
        Touch(Pairs[i + 1]);
        RemoveEmpty();
    }

    /// <summary>
    /// Text einer Seite bearbeiten. Jede Zeile ist ein Satz – ein Zeilenumbruch teilt einen Satz (neu segmentieren),
    /// das Entfernen eines Umbruchs verbindet zwei.
    /// </summary>
    public void SetText(int i, bool source, string text)
    {
        if (i < 0 || i >= Pairs.Count) return;
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var side = Side(Pairs[i], source);
        if (side.SequenceEqual(lines)) return;
        side.Clear();
        side.AddRange(lines);
        Touch(Pairs[i]);
    }

    public void Confirm(int i, bool confirmed = true)
    {
        if (i < 0 || i >= Pairs.Count) return;
        Pairs[i].Confirmed = confirmed && Pairs[i].IsComplete;
        if (Pairs[i].Confirmed) Pairs[i].Accepted = true;
    }

    public void RemoveEmpty() => Pairs.RemoveAll(p => p.Source.Count == 0 && p.Target.Count == 0);

    /// <summary>Nach einer Änderung von Hand: das Paar gilt als geprüft, eine KI-Bewertung ist überholt.</summary>
    private static void Touch(AlignedPair p)
    {
        p.AiScore = null;
        p.AiNote = null;
        p.Confidence = p.IsComplete ? Math.Max(p.Confidence, 0.5) : 0;
        p.Accepted = p.IsComplete;
    }

    /// <summary>Unsicher: weder bestätigt noch sicher zugeordnet, oder die KI hat Zweifel.</summary>
    public static bool IsUncertain(AlignedPair p) =>
        !p.Confirmed && (!p.IsComplete || p.Confidence < 0.5 || p.AiScore is < 0.6);

    /// <summary>
    /// Neu ausrichten: Bestätigte Paare bleiben als Anker stehen, die Sätze zwischen ihnen werden nach Länge und Zahlen
    /// neu zugeordnet.
    /// </summary>
    public void Realign()
    {
        var result = new List<AlignedPair>();
        var src = new List<string>();
        var tgt = new List<string>();
        void Flush()
        {
            if (src.Count + tgt.Count > 0) result.AddRange(Aligner.Align(src.ToList(), tgt.ToList()));
            src.Clear();
            tgt.Clear();
        }
        foreach (var p in Pairs)
        {
            if (p.Confirmed)
            {
                Flush();
                result.Add(p);
                continue;
            }
            src.AddRange(p.Source);
            tgt.AddRange(p.Target);
        }
        Flush();
        Pairs.Clear();
        Pairs.AddRange(result);
    }

    /// <summary>Die Abschnitte zwischen sicheren Paaren, die neu ausgerichtet werden sollten (Index von–bis, einschließlich).</summary>
    public List<(int From, int To)> UncertainRuns()
    {
        var runs = new List<(int, int)>();
        for (var i = 0; i < Pairs.Count; i++)
        {
            if (!IsUncertain(Pairs[i])) continue;
            var start = i;
            while (i + 1 < Pairs.Count && IsUncertain(Pairs[i + 1])) i++;
            // ein Nachbar auf jeder Seite dazu – Fehler verschieben sich oft dorthin
            var from = start > 0 && !Pairs[start - 1].Confirmed ? start - 1 : start;
            var to = i + 1 < Pairs.Count && !Pairs[i + 1].Confirmed ? i + 1 : i;
            if (runs.Count > 0 && from <= runs[^1].Item2) runs[^1] = (runs[^1].Item1, to);
            else runs.Add((from, to));
        }
        return runs;
    }

    /// <summary>Einen Abschnitt durch neu ausgerichtete Paare ersetzen.</summary>
    public void Replace(int from, int to, IEnumerable<AlignedPair> pairs)
    {
        Pairs.RemoveRange(from, to - from + 1);
        Pairs.InsertRange(from, pairs);
    }

    public IEnumerable<AlignedPair> Accepted => Pairs.Where(p => p.Accepted && p.IsComplete);

    /// <summary>
    /// Formatierungs-Tags der Übersetzung an die des Ausgangssatzes angleichen: Die Übersetzung stammt aus einem
    /// anderen Dokument und nummeriert ihre Tags anders. Zugeordnet wird in der Reihenfolge des Auftretens; was der
    /// Ausgangssatz nicht hat, fällt weg, was fehlt, wird ergänzt.
    /// </summary>
    public static string MatchTags(string source, string target)
    {
        var src = TagText.Tags(source).ToList();
        var tgt = TagText.Tags(target).ToList();
        if (tgt.Count == 0) return TagText.Repair(source, target);
        var map = new Dictionary<(bool Placeholder, int Id), int>();
        foreach (var placeholder in new[] { false, true })
        {
            var s = src.Where(t => (t.Kind == TagText.TagKind.Placeholder) == placeholder && t.Kind != TagText.TagKind.End).Select(t => t.Id).Distinct().ToList();
            var g = tgt.Where(t => (t.Kind == TagText.TagKind.Placeholder) == placeholder && t.Kind != TagText.TagKind.End).Select(t => t.Id).Distinct().ToList();
            for (var k = 0; k < Math.Min(s.Count, g.Count); k++) map[(placeholder, g[k])] = s[k];
        }
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var t in tgt)
        {
            sb.Append(target, pos, t.Index - pos);
            pos = t.Index + t.Length;
            if (!map.TryGetValue((t.Kind == TagText.TagKind.Placeholder, t.Id), out var id)) continue;
            sb.Append(t.Kind switch
            {
                TagText.TagKind.Start => TagText.Start(id),
                TagText.TagKind.End => TagText.End(id),
                _ => TagText.Placeholder(id),
            });
        }
        sb.Append(target, pos, target.Length - pos);
        return TagText.Repair(source, sb.ToString());
    }
}
