using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Terminology;

/// <summary>Ein Begriffskandidat aus den Ausgangstexten.</summary>
public sealed record ExtractedTerm(string Term, int Count, string Example, double Score);

/// <summary>
/// Fachbegriffe vor dem Übersetzen aus den Projektdateien gewinnen (einsprachig): Wortgruppen aus ein bis vier
/// Wörtern, die mehrfach vorkommen und nicht mit Funktionswörtern beginnen oder enden; im Deutschen zählen
/// großgeschriebene Wörter mitten im Satz (Substantive) und lange Komposita besonders. Kürzere Kandidaten, die nur
/// als Teil eines längeren vorkommen, fallen weg. Auf Wunsch wählt die KI daraus die Fachbegriffe aus und schlägt
/// die übliche Übersetzung vor – zum Prüfen, bevor sie in die Terminologie gehen.
/// </summary>
public static partial class TermExtractor
{
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["de"] = Set("der die das den dem des ein eine einer eines einem einen und oder aber auch als am an auf aus bei bis da damit dann dass daß dazu durch für gegen hat haben hier ihr ihre im in ins ist ja kann können muss müssen nach nicht noch nur ob oder ohne sich sie sind so soll sollen über um und uns unter vom von vor war wenn werden wird wie wir mit zu zum zur zwischen diese dieser dieses diesem diesen jede jeder jedes alle allen sowie bzw ggf z b dabei sein seine seiner seinem seinen kein keine keiner mehr sehr wurde wurden worden einem einer welche welcher welches dort hier diese sowohl"),
        ["en"] = Set("the a an and or but also as at by for from in into of on onto to with without is are was were be been being has have had do does did not no it its this that these those there here which who whom whose what when where why how all any each every some such than then so if may must shall should can could will would only very more most other same your you we our they their he she his her them us i me my about after before between during over under up down out off again further once both few own nor too just per via"),
        ["pl"] = Set("i w we na z ze do od po o u za przez dla przy pod nad przed między jak jest są być był była było może musi oraz lub albo ani ale że który która które którego której którym tego ta to te ten tym tej jego jej ich się nie tak też już jeszcze czy gdy gdzie co kto wszystkie każdy każda każde inne innych tylko także bardzo"),
        ["fr"] = Set("le la les l un une des du de d et ou mais aussi en au aux par pour sur dans avec sans sous entre est sont était être a ont avoir ce cet cette ces qui que quoi dont où il elle ils elles on se sa son ses leur leurs ne pas plus très tout tous toute toutes chaque autre même si comme"),
        ["es"] = Set("el la los las un una unos unas y o pero también en a al de del por para con sin sobre entre es son era ser ha han haber este esta estos estas ese esa que quien cuyo donde se su sus le les no más muy todo todos toda todas cada otro mismo si como"),
        ["it"] = Set("il lo la i gli le un uno una e o ma anche in a al alla ai agli alle di del della dei degli delle da dal dalla per con su tra fra è sono era essere ha hanno avere questo questa questi queste che chi cui dove si suo sua suoi loro non più molto tutto tutti ogni altro stesso se come"),
        ["nl"] = Set("de het een en of maar ook in op aan bij van voor met zonder over onder tussen is zijn was waren worden wordt heeft hebben dit dat deze die wie wat waar hij zij ze zich zijn haar hun niet geen meer zeer alle elke andere als zo"),
        ["pt"] = Set("o a os as um uma uns umas e ou mas também em no na nos nas de do da dos das por para com sem sobre entre é são era ser tem têm ter este esta estes estas que quem cujo onde se seu sua seus não mais muito todo todos cada outro mesmo como"),
    };

    private static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> StopsFor(string language) =>
        StopWords.TryGetValue(Languages.Primary(language), out var s) ? s : StopWords["en"];

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}\-’']*|\p{N}+")]
    private static partial Regex WordRegex();

    private sealed class Tally
    {
        public int Count;
        public Dictionary<string, int> Forms { get; } = new();
        public string Example = "";
        public int Words;
        public bool Noun;
    }

    /// <summary>Kandidaten nach Gewicht, höchstens <paramref name="max"/>; <paramref name="known"/>: schon in der Terminologie.</summary>
    public static List<ExtractedTerm> Candidates(IEnumerable<string> sentences, string language, int max = 300, Func<string, bool>? known = null)
    {
        var stops = StopsFor(language);
        var german = Languages.Primary(language) == "de";
        var tallies = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sentences.Distinct())
        {
            var sentence = TagText.StripTags(raw).Trim();
            if (sentence.Length == 0) continue;
            var words = WordRegex().Matches(sentence).Select(m => m.Value).ToList();
            for (var i = 0; i < words.Count; i++)
                for (var n = 1; n <= 4 && i + n <= words.Count; n++)
                {
                    var part = words.Skip(i).Take(n).ToList();
                    if (stops.Contains(part[0]) || stops.Contains(part[^1]) || part.Any(w => w.All(char.IsDigit) || w.Length < 2)) continue;
                    // nicht über Satzzeichen hinweg
                    var phrase = string.Join(" ", part);
                    if (n > 1 && !sentence.Contains(phrase, StringComparison.Ordinal)) continue;
                    var noun = german && part.Any(w => char.IsUpper(w[0])) && (i > 0 || n > 1 || words.Count == 1);
                    if (n == 1 && (part[0].Length < 4 || (german ? !char.IsUpper(part[0][0]) : part[0].Length < 5))) continue;
                    if (!tallies.TryGetValue(phrase, out var t)) tallies[phrase] = t = new Tally { Words = n };
                    t.Count++;
                    t.Noun |= noun;
                    t.Forms[phrase] = t.Forms.GetValueOrDefault(phrase) + 1;
                    if (t.Example.Length == 0 || sentence.Length < t.Example.Length) t.Example = sentence;
                }
        }
        var list = tallies
            .Where(kv => kv.Value.Count >= 2 || (german && kv.Value.Words == 1 && kv.Key.Length >= 12 && kv.Value.Noun))
            .Select(kv =>
            {
                var t = kv.Value;
                var score = t.Count * (1 + 0.6 * (t.Words - 1)) * (t.Noun ? 1.5 : 1) * (kv.Key.Length >= 12 ? 1.3 : 1);
                return new ExtractedTerm(t.Forms.MaxBy(f => f.Value).Key, t.Count, t.Example, score);
            })
            .Where(t => known?.Invoke(t.Term) != true)
            .ToList();
        // kürzere Kandidaten weglassen, die (fast) nur als Teil eines längeren vorkommen
        var longer = list.Where(t => t.Term.Contains(' ')).ToList();
        list = list.Where(t => !longer.Any(l => l != t && l.Term.Length > t.Term.Length && l.Count >= t.Count * 0.8
                                                 && Regex.IsMatch(l.Term, $@"(?<!\p{{L}}){Regex.Escape(t.Term)}(?!\p{{L}})", RegexOptions.IgnoreCase)))
            .ToList();
        return list.OrderByDescending(t => t.Score).ThenBy(t => t.Term, StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
    }

    /// <summary>
    /// Die KI wählt aus den Kandidaten die Fachbegriffe aus (Grundform) und schlägt die übliche Übersetzung vor.
    /// Häufigkeit und Beispiel kommen aus den Texten; nur Begriffe, die dort tatsächlich stehen.
    /// </summary>
    public static async Task<List<TermSuggestion>> RefineAsync(IChatModel model, IReadOnlyList<ExtractedTerm> candidates, IReadOnlyList<string> sentences,
        string sourceLanguage, string targetLanguage, string? topic, CancellationToken ct = default)
    {
        var plain = sentences.Select(s => TagText.StripTags(s)).ToList();
        var sb = new StringBuilder();
        foreach (var c in candidates.Take(150)) sb.AppendLine($"- {c.Term} ({c.Count}×)");
        var reply = await model.AskAsync(
            "You are a terminologist preparing a termbase before a translation job. Output only JSON.",
            $$"""
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject field: " + topic + "\n")}}Candidate terms from the source texts ({{sourceLanguage}}), with frequency:
            {{sb}}
            Pick the specialist terms worth adding to a termbase: domain-specific nouns and noun phrases, technical, legal or
            official terms – not general vocabulary, not names of persons, not placeholders like [NAME_1]. Write each in its
            dictionary form exactly as used in the field, and give its established translation into {{Languages.DisplayName(targetLanguage)}} ({{targetLanguage}}).
            Return at most 80: {"terms": [{"s": "source term", "t": "target term"}]}
            """, ct);
        if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["terms"] is not JsonArray arr) return [];
        var result = new List<TermSuggestion>();
        foreach (var node in arr)
        {
            var s = ((string?)node?["s"])?.Trim();
            var t = ((string?)node?["t"])?.Trim() ?? "";
            if (string.IsNullOrEmpty(s) || s.Length > 80 || SensitiveData.PlaceholderRegex().IsMatch(s) || SensitiveData.PlaceholderRegex().IsMatch(t)) continue;
            if (result.Any(r => r.Source.Equals(s, StringComparison.CurrentCultureIgnoreCase))) continue;
            var hits = plain.Where(p => TermSuggester.ContainsInflected(p, s)).ToList();
            if (hits.Count == 0) continue;                                           // steht nicht in den Texten
            result.Add(new TermSuggestion(TermSuggester.NormalizeCase(s, hits, sourceLanguage), t, hits.Count, hits.MinBy(h => h.Length)!));
        }
        return result.OrderByDescending(r => r.Count).ToList();
    }
}
