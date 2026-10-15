using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Alignment;

/// <summary>Ein ausgerichtetes Paar: ein oder mehrere Sätze links und rechts.</summary>
public sealed class AlignedPair
{
    public List<string> Source { get; init; } = new();
    public List<string> Target { get; init; } = new();
    /// <summary>0..1 – wie sicher die Zuordnung ist.</summary>
    public double Confidence { get; set; }
    public bool Accepted { get; set; } = true;

    public string SourceText => string.Join(" ", Source);
    public string TargetText => string.Join(" ", Target);
    public bool IsComplete => Source.Count > 0 && Target.Count > 0;
}

/// <summary>
/// Alignment (Ausrichtung) alter Übersetzungen nach Gale &amp; Church: Sätze werden über ihre Länge
/// einander zugeordnet (1:1, 1:2, 2:1, 2:2, 1:0, 0:1); gleiche Zahlen, Tags und Eigennamen stützen die Zuordnung.
/// </summary>
public static partial class Aligner
{
    private static readonly (int S, int T, double Prior)[] Moves =
    [
        (1, 1, 0.89), (1, 0, 0.005), (0, 1, 0.005), (2, 1, 0.045), (1, 2, 0.045), (2, 2, 0.011),
    ];

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Numbers();

    public static List<AlignedPair> Align(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        var sLen = source.Select(s => (double)Math.Max(1, TagText.StripTags(s).Length)).ToArray();
        var tLen = target.Select(t => (double)Math.Max(1, TagText.StripTags(t).Length)).ToArray();
        var ratio = tLen.Sum() / Math.Max(1, sLen.Sum());        // Längenverhältnis der Sprachen

        int n = source.Count, m = target.Count;
        var cost = new double[n + 1, m + 1];
        var back = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
                cost[i, j] = double.PositiveInfinity;
        cost[0, 0] = 0;

        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
            {
                if (double.IsPositiveInfinity(cost[i, j])) continue;
                for (var k = 0; k < Moves.Length; k++)
                {
                    var (ds, dt, prior) = Moves[k];
                    if (i + ds > n || j + dt > m) continue;
                    var ls = 0.0; for (var a = 0; a < ds; a++) ls += sLen[i + a];
                    var lt = 0.0; for (var b = 0; b < dt; b++) lt += tLen[j + b];
                    var c = -Math.Log(prior) + LengthCost(ls, lt, ratio)
                            + AnchorCost(source.Skip(i).Take(ds), target.Skip(j).Take(dt));
                    if (cost[i, j] + c < cost[i + ds, j + dt])
                    {
                        cost[i + ds, j + dt] = cost[i, j] + c;
                        back[i + ds, j + dt] = k;
                    }
                }
            }

        var pairs = new List<AlignedPair>();
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            var (ds, dt, _) = Moves[back[x, y]];
            var stepCost = cost[x, y] - cost[x - ds, y - dt];
            pairs.Add(new AlignedPair
            {
                Source = source.Skip(x - ds).Take(ds).ToList(),
                Target = target.Skip(y - dt).Take(dt).ToList(),
                Confidence = ds == 0 || dt == 0 ? 0 : Math.Clamp(Math.Exp(-(stepCost - 0.12) / 4), 0, 1),
            });
            x -= ds;
            y -= dt;
        }
        pairs.Reverse();
        foreach (var p in pairs) p.Accepted = p.IsComplete && p.Confidence >= 0.35;
        return pairs;
    }

    /// <summary>Gale-Church: wie gut passen die Längen bei normalverteiltem Längenverhältnis.</summary>
    private static double LengthCost(double ls, double lt, double ratio)
    {
        if (ls == 0 && lt == 0) return 0;
        var mean = (ls + lt / ratio) / 2;
        var delta = (lt - ls * ratio) / Math.Sqrt(6.8 * Math.Max(mean, 1));
        var p = 2 * (1 - NormalCdf(Math.Abs(delta)));
        return -Math.Log(Math.Max(p, 1e-12));
    }

    private static double AnchorCost(IEnumerable<string> s, IEnumerable<string> t)
    {
        var a = Numbers().Matches(string.Join(" ", s)).Select(m => m.Value.Replace(',', '.')).OrderBy(v => v).ToList();
        var b = Numbers().Matches(string.Join(" ", t)).Select(m => m.Value.Replace(',', '.')).OrderBy(v => v).ToList();
        if (a.Count == 0 && b.Count == 0) return 0;
        return a.SequenceEqual(b) ? -1.5 : 1.5;
    }

    private static double NormalCdf(double z)
    {
        // Abramowitz-Stegun 26.2.17
        var t = 1 / (1 + 0.2316419 * z);
        var d = 0.3989423 * Math.Exp(-z * z / 2);
        var p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return 1 - p;
    }
}
