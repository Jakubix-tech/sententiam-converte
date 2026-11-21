using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Agents;

/// <summary>Ein Ausdruck, der gründlicher recherchiert werden sollte – mit den Gründen und den Segmenten, in denen er steht.</summary>
public sealed record FollowUpCandidate(string Expression, IReadOnlyList<string> Reasons, IReadOnlyList<int> SegmentIds, int Weight, TermDossier Dossier);

/// <summary>
/// Woran man erkennt, dass ein Ausdruck mehr Recherche braucht. Jedes Zeichen zählt, die stärksten zuerst:
/// <list type="bullet">
/// <item>nicht belegt – keine Quelle der Zielsprache bestätigt die gewählte Übersetzung (sie ging deshalb nicht in die Übersetzung ein);</item>
/// <item>das Lektorat fand in einem Segment mit dem Ausdruck einen Fehler der Terminologie oder Bedeutung – erst recht, wenn die
/// Anmerkung den Ausdruck oder seine Übersetzung nennt;</item>
/// <item>die Netzprüfung fand die Wendung mit der gewählten Übersetzung fraglich;</item>
/// <item>nicht eindeutig – kein Kandidat in zwei unabhängigen Quellen;</item>
/// <item>das Zeitbudget war erreicht, die Recherche ist nicht zu Ende gelaufen;</item>
/// <item>und es wiegt schwerer, wenn der Ausdruck oft vorkommt: eine falsche Wahl wirkt sich mehrfach aus (allein kein Grund).</item>
/// </list>
/// </summary>
public static class ResearchFollowUp
{
    /// <summary>Kategorien des Lektorats, die auf einen falschen Begriff hindeuten.</summary>
    private static bool PointsToTerm(string category) =>
        category.StartsWith("Terminologie", StringComparison.Ordinal) || category == "Genauigkeit: Fehlübersetzung";

    /// <param name="doubtfulPhrases">Von der Netzprüfung als fraglich oder falsch eingestufte Wendungen je Segment.</param>
    public static List<FollowUpCandidate> Candidates(IReadOnlyList<TermDossier> dossiers, IReadOnlyList<Segment> segments, string targetLanguage,
        IEnumerable<(int SegmentId, string Phrase)>? doubtfulPhrases = null)
    {
        var doubtful = doubtfulPhrases?.ToList() ?? [];
        var result = new List<FollowUpCandidate>();
        foreach (var d in dossiers.Where(d => Languages.Matches(d.TargetLanguage, targetLanguage)).DistinctBy(d => d.Expression.ToLowerInvariant()))
        {
            var inSegments = segments.Where(s => DocumentResearch.Contains(TagText.StripTags(s.Source), d.Expression)).ToList();
            if (inSegments.Count == 0) continue;
            var reasons = new List<string>();
            var weight = 0;
            void Reason(int w, string text) { weight += w; reasons.Add(text); }

            if (!DossierStore.IsEvidenced(d))
                Reason(3, "nicht belegt: keine Quelle der Zielsprache bestätigt die Übersetzung");
            var rendered = d.Recommended?.Trim() is { Length: > 0 } r ? r : null;
            foreach (var s in inSegments)
                foreach (var e in (s.Errors ?? []).Where(e => PointsToTerm(e.Category)))
                {
                    var named = DocumentResearch.Contains(e.Note, d.Expression) || rendered != null && DocumentResearch.Contains(e.Note, rendered);
                    Reason(named ? 4 : 2, $"Lektorat: {e.Category} in Segment {s.Id}" + (named ? " – betrifft diesen Ausdruck" : ""));
                }
            if (rendered != null)
                foreach (var (id, phrase) in doubtful.Where(p => inSegments.Any(s => s.Id == p.SegmentId) && DocumentResearch.Contains(p.Phrase, rendered)))
                    Reason(3, $"Netzprüfung: „{phrase}“ in Segment {id} fraglich");
            if (d.Conclusive == false && d.Rounds > 0)
                Reason(2, "nicht eindeutig: kein Kandidat in zwei unabhängigen Quellen");
            if (d.Warnings.Any(w => w.StartsWith("Zeitbudget erreicht", StringComparison.Ordinal)))
                Reason(2, "Zeitbudget erreicht – nicht zu Ende gesucht");
            if (weight == 0) continue;                                   // Häufigkeit allein ist kein Grund
            if (inSegments.Count >= 3)
                Reason(1, $"kommt in {inSegments.Count} Segmenten vor – eine falsche Wahl wirkt sich mehrfach aus");
            result.Add(new FollowUpCandidate(d.Expression, reasons, inSegments.Select(s => s.Id).ToList(), weight, d));
        }
        return result.OrderByDescending(c => c.Weight).ThenByDescending(c => c.SegmentIds.Count).ToList();
    }

    /// <summary>
    /// Hat die Nachrecherche etwas geändert, das die Übersetzung betrifft? Ja, wenn das neue Dossier belegt ist und das alte
    /// nicht, oder wenn sich die belegte Empfehlung geändert hat.
    /// </summary>
    public static bool ChangesTranslation(TermDossier before, TermDossier after) =>
        DossierStore.IsEvidenced(after)
        && (!DossierStore.IsEvidenced(before) || !string.Equals(before.Recommended?.Trim(), after.Recommended?.Trim(), StringComparison.OrdinalIgnoreCase));
}
