using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Agents;

/// <summary>Ergebnis der Anpassung: neue Übersetzung, ob die KI mehr geändert hat als nötig.</summary>
public sealed record RepairResult(string Target, bool ChangedTooMuch);

/// <summary>
/// Fuzzy-Treffer gezielt anpassen: Die KI bekommt den alten und den neuen Ausgangstext und die freigegebene Übersetzung
/// und ändert nur, was sich im Ausgangstext geändert hat – die übrige Formulierung des Kunden bleibt Wort für Wort.
/// Ändert die KI deutlich mehr als nötig, wird das gemeldet.
/// </summary>
public sealed class FuzzyRepair(AgentContext ctx)
{
    /// <summary>Ab diesem Treffer lohnt das Anpassen (darunter frei übersetzen).</summary>
    public const int MinScore = 70;

    public async Task<RepairResult> RepairAsync(string source, string sourceLanguage, string targetLanguage, TmMatch match,
        IReadOnlyList<TermHit>? terms = null, Projects.DocumentBrief? brief = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"An approved translation ({Languages.DisplayName(sourceLanguage)} → {Languages.DisplayName(targetLanguage)}) exists for a similar sentence.");
        sb.AppendLine("Adapt the approved translation to the new source sentence. Change ONLY the words that correspond to the differences;");
        sb.AppendLine("keep every other word, the word order and the punctuation of the approved translation exactly as it is.");
        if (terms is { Count: > 0 })
        {
            sb.AppendLine("Mandatory terminology:");
            foreach (var t in terms.Where(t => t.Translations.Count > 0))
                sb.AppendLine($"- {t.SourceTerm.Text} → {string.Join(" / ", t.Translations.Where(x => x.Status != TermStatus.Forbidden).Select(x => x.Text))}");
        }
        if (brief?.Instructions(targetLanguage) is { Length: > 0 } about)
        {
            sb.AppendLine("About the whole document:");
            sb.Append(about);
        }
        sb.AppendLine();
        sb.AppendLine($"Old source: {match.Unit.Source}");
        sb.AppendLine($"Approved translation: {match.Unit.Target}");
        sb.AppendLine($"New source: {source}");
        sb.AppendLine($"Differences in the source: {Differences(match.Unit.Source, source)}");
        sb.AppendLine();
        if (TagText.Tags(source).Any())
            sb.AppendLine("Formatting tags such as ⟨1⟩, ⟨/1⟩ and ⟨2/⟩ must appear in the result exactly as in the new source.");
        sb.AppendLine("Reply with the adapted translation only.");
        var answer = await ctx.Model.AskAsync("You are a professional translator who post-edits translation memory matches minimally.", sb.ToString(), ct);
        var target = TagText.Repair(source, answer.Trim().Trim('"', '„', '“').Trim());
        return new RepairResult(target, TooMuch(match.Unit.Source, source, match.Unit.Target, target));
    }

    /// <summary>„removed: …; added: …“ aus dem wortweisen Vergleich der Ausgangstexte.</summary>
    internal static string Differences(string oldSource, string newSource)
    {
        var parts = WordDiff.Compare(TagText.StripTags(oldSource), TagText.StripTags(newSource));
        var removed = string.Join(" … ", Runs(parts, WordDiff.Kind.Deleted));
        var added = string.Join(" … ", Runs(parts, WordDiff.Kind.Inserted));
        return string.Join("; ", new[] { removed.Length > 0 ? $"removed \"{removed}\"" : null, added.Length > 0 ? $"added \"{added}\"" : null }
            .Where(s => s != null)) is { Length: > 0 } d ? d : "only formatting or punctuation";
    }

    private static IEnumerable<string> Runs(List<WordDiff.Part> parts, WordDiff.Kind kind)
    {
        var run = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Kind == kind) run.Append(p.Text);
            else if (p.Kind == WordDiff.Kind.Same && run.Length > 0 && p.Text.Trim().Length > 0) { yield return run.ToString().Trim(); run.Clear(); }
        }
        if (run.Length > 0) yield return run.ToString().Trim();
    }

    /// <summary>Hat die KI in der Übersetzung deutlich mehr Wörter geändert, als sich im Ausgangstext geändert haben?</summary>
    internal static bool TooMuch(string oldSource, string newSource, string oldTarget, string newTarget)
    {
        static int Changed(string a, string b) => WordDiff.Compare(TagText.StripTags(a), TagText.StripTags(b))
            .Where(p => p.Kind != WordDiff.Kind.Same).Sum(p => p.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var sourceChange = Math.Max(1, Changed(oldSource, newSource));
        return Changed(oldTarget, newTarget) > sourceChange * 2 + 3;
    }
}
