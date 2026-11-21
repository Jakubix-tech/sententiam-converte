using System.Globalization;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

/// <summary>Was in einer bestätigten Übersetzung aus der empfohlenen Benennung wurde.</summary>
public enum TermUseKind
{
    /// <summary>Die Empfehlung steht da – wörtlich, gebeugt, umgestellt oder verkürzt.</summary>
    Kept,
    /// <summary>Ein anderer recherchierter Kandidat steht an ihrer Stelle.</summary>
    OtherCandidate,
    /// <summary>Eine eigene Benennung des Übersetzers, die die Recherche nicht vorschlug.</summary>
    OwnTerm,
    /// <summary>Ohne eigene Benennung wiedergegeben (umschrieben, Pronomen, ausgelassen): sagt nichts über die Benennung.</summary>
    Paraphrased,
}

/// <param name="Rendering">Was in der Übersetzung für den Ausdruck steht (wenn bekannt).</param>
/// <param name="How">„wörtlich“, „gebeugt“ oder „KI-Zuordnung“.</param>
public sealed record TermUse(TermUseKind Kind, string? Rendering, string How)
{
    /// <summary>Für die Rückmeldung: übernommen (true), ersetzt (false) oder keine (null).</summary>
    public bool? Accepted => Kind switch
    {
        TermUseKind.Kept => true,
        TermUseKind.OtherCandidate or TermUseKind.OwnTerm => false,
        _ => null,
    };
}

/// <summary>
/// Wurde die empfohlene Benennung beim Bestätigen übernommen? In drei Stufen, von sicher zu aufwendig: wörtlich (wie
/// bisher), dann tolerant – gebeugt, mit Umlaut oder in anderer Wortstellung („uszczelnienia mechanicznego“ für
/// „uszczelnienie mechaniczne“, „Laufräder“ für „Laufrad“) –, und erst wenn beides nichts findet, weil der Satz
/// umformuliert wurde, fragt die KI, welche Wörter der Übersetzung den Ausdruck wiedergeben. Deren Antwort zählt nur,
/// wenn sie sich am Text bestätigen lässt: Die genannten Wörter müssen in der Übersetzung stehen, und „die Empfehlung“
/// muss ihr auch ähneln. Was nur umschrieben ist, gibt keine Rückmeldung.
/// </summary>
public static class RecommendationUse
{
    /// <summary>Die Benennung steht in <paramref name="text"/> – auch gebeugt und in anderer Wortstellung.</summary>
    public static bool Appears(string text, string term) => Match(text, term) != null;

    /// <summary>Wörtlich: „wörtlich“; tolerant: „gebeugt“; sonst null.</summary>
    private static string? Match(string text, string term)
    {
        term = term.Trim();
        if (term.Length < 2 || text.Trim().Length == 0) return null;
        if (DocumentResearch.Contains(text, term)) return "wörtlich";
        var want = Words(term);
        var have = Words(text);
        if (want.Count == 0 || want.Count > 8 || have.Count == 0) return null;
        // jedes Wort der Benennung als eigenes Wort desselben Stamms, nah beieinander (umgestellt erlaubt)
        var window = want.Count + 3;
        for (var start = 0; start < have.Count; start++)
        {
            var used = new HashSet<int>();
            var all = true;
            foreach (var w in want)
            {
                var hit = Enumerable.Range(start, Math.Min(window, have.Count - start)).FirstOrDefault(i => !used.Contains(i) && SameStem(w, have[i]), -1);
                if (hit < 0) { all = false; break; }
                used.Add(hit);
            }
            if (all && used.Contains(start)) return "gebeugt";
        }
        return null;
    }

    /// <summary>
    /// Dasselbe Wort in anderer Form: gleich, oder ab vier Buchstaben derselbe Anfang bis auf höchstens zwei Buchstaben
    /// des kürzeren Wortes, und höchstens vier Buchstaben Endung („seal“/„seals“, „mechaniczne“/„mechanicznego“).
    /// Kurze Wörter müssen gleich sein.
    /// </summary>
    internal static bool SameStem(string a, string b)
    {
        if (a == b) return true;
        var min = Math.Min(a.Length, b.Length);
        if (min < 4) return false;
        var p = 0;
        while (p < min && a[p] == b[p]) p++;
        return p >= 4 && p >= min - 2 && Math.Max(a.Length, b.Length) - p <= 4;
    }

    /// <summary>Wörter klein, ohne Akzente und Umlautpunkte.</summary>
    internal static List<string> Words(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Replace("ß", "ss").Replace("ł", "l").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static IEnumerable<string> OtherCandidates(TermDossier d) =>
        d.Candidates.Select(c => c.Term.Trim()).Where(t => t.Length > 1 && !t.Equals(d.Recommended?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ohne KI: Empfehlung oder ein anderer Kandidat steht da (wörtlich oder gebeugt), sonst null (umformuliert – dafür
    /// <see cref="AskAsync"/>). Enthält ein anderer Kandidat die Empfehlung und steht er da („mechanical seal“ statt
    /// „seal“), wurde er gewählt.
    /// </summary>
    public static TermUse? Quick(string target, TermDossier d)
    {
        var recommended = d.Recommended?.Trim() ?? "";
        if (recommended.Length < 2) return null;
        var others = OtherCandidates(d).Select(c => (Term: c, How: Match(target, c))).Where(c => c.How != null).ToList();
        if (others.FirstOrDefault(c => c.Term.Length > recommended.Length && Appears(c.Term, recommended)) is { How: { } longer } wider)
            return new TermUse(TermUseKind.OtherCandidate, wider.Term, longer);
        if (Match(target, recommended) is { } how) return new TermUse(TermUseKind.Kept, recommended, how);
        return others.Count > 0 ? new TermUse(TermUseKind.OtherCandidate, others[0].Term, others[0].How!) : null;
    }

    /// <summary>
    /// Die Antwort der KI am Text prüfen: Die Wiedergabe muss in der Übersetzung stehen. „Empfehlung“ zählt nur, wenn die
    /// Wiedergabe ihr ähnelt (ein Wort desselben Stamms); ein genannter Kandidat muss einer der Kandidaten sein; eine
    /// eigene Benennung, die die Empfehlung doch enthält, ist übernommen. Sonst null – lieber keine Rückmeldung als eine
    /// geratene.
    /// </summary>
    public static TermUse? FromAnswer(string target, TermDossier d, string? rendering, string? relation, string? candidate)
    {
        const string How = "KI-Zuordnung";
        var recommended = d.Recommended?.Trim() ?? "";
        var r = rendering?.Trim().Trim('"', '„', '“', '”', '«', '»', '\'') ?? "";
        switch (relation?.Trim().ToLowerInvariant())
        {
            case "paraphrased" or "omitted":
                return new TermUse(TermUseKind.Paraphrased, r.Length > 0 ? r : null, How);
            case "recommended" or "candidate" or "other":
                break;
            default:
                return null;
        }
        if (r.Length < 2 || !DocumentResearch.Contains(target, r)) return null;
        if (Appears(r, recommended)) return new TermUse(TermUseKind.Kept, r, How);
        var named = OtherCandidates(d).FirstOrDefault(c => Appears(r, c) || candidate is { Length: > 1 } && c.Equals(candidate.Trim(), StringComparison.OrdinalIgnoreCase) && SharesStem(r, c));
        switch (relation.Trim().ToLowerInvariant())
        {
            case "recommended":
                // verkürzt („seal“ für „mechanical seal“): übernommen, wenn wenigstens ein Wort dazu passt
                return SharesStem(r, recommended) ? new TermUse(TermUseKind.Kept, r, How) : null;
            case "candidate":
                return named != null ? new TermUse(TermUseKind.OtherCandidate, r, How) : null;
            default:
                return named != null ? new TermUse(TermUseKind.OtherCandidate, r, How) : new TermUse(TermUseKind.OwnTerm, r, How);
        }
    }

    private static bool SharesStem(string a, string b)
    {
        var wb = Words(b);
        return Words(a).Any(x => wb.Any(y => SameStem(x, y)));
    }

    private sealed record Item(string? Expression, string? Rendering, string? Relation, string? Candidate);
    private sealed record Answer(List<Item>? Items);

    /// <summary>
    /// Für umformulierte Sätze: Die KI sagt je Ausdruck, welche Wörter der bestätigten Übersetzung ihn wiedergeben und wie
    /// sie zu den Kandidaten stehen – ein Aufruf für alle Ausdrücke des Segments. Geprüft mit <see cref="FromAnswer"/>;
    /// was sich nicht bestätigen lässt, fehlt im Ergebnis.
    /// </summary>
    public static async Task<Dictionary<TermDossier, TermUse>> AskAsync(AgentContext ctx, string source, string target, IReadOnlyList<TermDossier> dossiers,
        string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        var result = new Dictionary<TermDossier, TermUse>();
        var list = dossiers.Where(d => d.Recommended is { Length: > 1 }).Take(8).ToList();
        if (list.Count == 0) return result;
        var source0 = TagText.StripTags(source).Trim();
        var target0 = TagText.StripTags(target).Trim();
        var expressions = string.Join("\n", list.Select(d =>
            $"- \"{d.Expression}\": recommended \"{d.Recommended}\"" +
            (OtherCandidates(d).Take(5).ToList() is { Count: > 0 } o ? "; other candidates: " + string.Join(", ", o.Select(c => $"\"{c}\"")) : "")));
        var answer = JsonExtract.Parse<Answer>(await ctx.Model.AskAsync(
            "You are an experienced translator and reviewer. You compare precisely. Output only JSON.",
            $$"""
            Source text ({{Languages.DisplayName(sourceLanguage)}}): {{source0}}
            Confirmed translation ({{Languages.DisplayName(targetLanguage)}}): {{target0}}

            The translator confirmed this translation. For these source expressions, research had suggested target terms:
            {{expressions}}

            For each expression, find the words of the confirmed translation that render it and copy them EXACTLY as they
            stand there (in whatever inflected form). Then say how they relate to the suggested terms:
            - "recommended": it is the recommended term (inflected, reordered or shortened is fine)
            - "candidate": it is one of the other candidates (name which in "candidate")
            - "other": a different term the translator chose
            - "paraphrased": the meaning is there, but without a term of its own (rephrased, pronoun, merged into other words)
            - "omitted": the expression is not rendered
            Return: {"items": [{"expression": "...", "rendering": "...", "relation": "recommended|candidate|other|paraphrased|omitted", "candidate": "..."}]}
            """, ct));
        foreach (var item in answer.Items ?? [])
        {
            var d = list.FirstOrDefault(x => x.Expression.Trim().Equals(item.Expression?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (d == null || result.ContainsKey(d)) continue;
            if (FromAnswer(target0, d, item.Rendering, item.Relation, item.Candidate) is { } use) result[d] = use;
        }
        return result;
    }

    /// <summary>„übernommen (gebeugt)“, „ersetzt durch „face seal““, „eigene Benennung „seal unit““, „umschrieben“.</summary>
    public static string Describe(TermUse use) => use.Kind switch
    {
        TermUseKind.Kept => $"übernommen ({use.How})",
        TermUseKind.OtherCandidate => $"ersetzt durch „{use.Rendering}“ ({use.How})",
        TermUseKind.OwnTerm => $"ersetzt durch eigene Benennung „{use.Rendering}“ ({use.How})",
        _ => $"umschrieben – zählt nicht ({use.How})",
    };
}
