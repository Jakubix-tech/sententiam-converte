using SententiamConverte.Core.Model;
using SententiamConverte.Core.Tm;

using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Analysis;

/// <summary>Trefferstufen der Analyse (branchenübliche Einteilung).</summary>
public enum MatchBand
{
    PerfectMatch,   // 100 % mit gleichem Kontext
    Repetition,
    Exact,          // 100 %
    Fuzzy95,
    Fuzzy85,
    Fuzzy75,
    Fuzzy50,
    NoMatch,
    Locked,
}

public sealed class BandCount
{
    public int Segments { get; set; }
    public int Words { get; set; }
    public int Characters { get; set; }
}

public sealed class AnalysisResult
{
    public Dictionary<MatchBand, BandCount> Bands { get; } =
        Enum.GetValues<MatchBand>().ToDictionary(b => b, _ => new BandCount());

    public int TotalWords => Bands.Values.Sum(b => b.Words);
    public int TotalSegments => Bands.Values.Sum(b => b.Segments);

    public static string BandName(MatchBand b) => b switch
    {
        MatchBand.PerfectMatch => "Kontexttreffer",
        MatchBand.Repetition => "Wiederholungen",
        MatchBand.Exact => "100 %",
        MatchBand.Fuzzy95 => "95–99 %",
        MatchBand.Fuzzy85 => "85–94 %",
        MatchBand.Fuzzy75 => "75–84 %",
        MatchBand.Fuzzy50 => "50–74 %",
        MatchBand.NoMatch => "Kein Treffer",
        MatchBand.Locked => "Gesperrt",
        _ => b.ToString(),
    };
}

public static class Analyzer
{
    public static int CountWords(string text) =>
        FuzzyMatcher.Words(text).Count();

    public static AnalysisResult Analyze(IEnumerable<BilingualDocument> documents, IReadOnlyList<ITranslationMemory> tms)
    {
        var result = new AnalysisResult();
        var seen = new HashSet<string>();
        foreach (var doc in documents)
        {
            var segs = doc.Segments.ToList();
            for (var i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                var words = CountWords(s.Source);
                var chars = TagText.StripTags(s.Source).Length;
                MatchBand band;
                if (s.Locked) band = MatchBand.Locked;
                else if (!seen.Add(s.Source)) band = MatchBand.Repetition;
                else
                {
                    var best = tms.SelectMany(tm => Projects.Workflow.Safe(() => tm.Search(doc.SourceLanguage, doc.TargetLanguage, s.Source, 50, 3)))
                                  .OrderByDescending(m => m.Score).FirstOrDefault();
                    band = best?.Score switch
                    {
                        null => MatchBand.NoMatch,
                        100 when i > 0 && IsContextMatch(tms, doc, segs[i - 1]) => MatchBand.PerfectMatch,
                        100 => MatchBand.Exact,
                        >= 95 => MatchBand.Fuzzy95,
                        >= 85 => MatchBand.Fuzzy85,
                        >= 75 => MatchBand.Fuzzy75,
                        >= 50 => MatchBand.Fuzzy50,
                        _ => MatchBand.NoMatch,
                    };
                }
                var c = result.Bands[band];
                c.Segments++;
                c.Words += words;
                c.Characters += chars;
            }
        }
        return result;
    }

    private static bool IsContextMatch(IReadOnlyList<ITranslationMemory> tms, BilingualDocument doc, Segment previous) =>
        tms.Any(tm => Projects.Workflow.Safe(() => tm.Search(doc.SourceLanguage, doc.TargetLanguage, previous.Source, 100, 1)).Count > 0);
}
