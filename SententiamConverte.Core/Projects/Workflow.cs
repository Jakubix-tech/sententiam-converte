using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Projects;

/// <summary>Die Arbeitsschritte im Editor und in den Stapelaufgaben.</summary>
public static class Workflow
{
    /// <summary>Formate, deren Absatzkontext eine eindeutige ID ist (Ressourcenschlüssel, JSON-Pfad, PO-Eintrag).</summary>
    private static readonly HashSet<string> IdFormats = ["resx", "json", "po"];

    /// <summary>Zusammenhang eines Segments: vorheriger und nächster Satz, Stelle im Dokument.</summary>
    public static TmContext ContextOf(BilingualDocument doc, Segment segment)
    {
        var all = doc.Segments.ToList();
        return ContextOf(doc, all, all.IndexOf(segment));
    }

    /// <summary>Wie <see cref="ContextOf(BilingualDocument, Segment)"/>, mit vorab ermittelter Segmentliste (Stapelaufgaben).</summary>
    public static TmContext ContextOf(BilingualDocument doc, IReadOnlyList<Segment> all, int index)
    {
        if (index < 0) return new TmContext(null, null, null);
        var segment = all[index];
        var previous = index > 0 ? TmContext.Key(all[index - 1].Source) : "";      // "" = Dokumentanfang
        var next = index < all.Count - 1 ? TmContext.Key(all[index + 1].Source) : "";
        return new TmContext(previous, next, doc.ParagraphOf(segment)?.Context, IdFormats.Contains(doc.FilterId));
    }

    /// <summary>Alle TM-Treffer für ein Segment aus allen TMs des Projekts, beste zuerst; Kontexttreffer = 101 %.</summary>
    public static List<TmMatch> Lookup(TranslationProject project, BilingualDocument doc, Segment segment, int? minScore = null, TmContext? context = null)
    {
        context ??= ContextOf(doc, segment);
        return project.TmsFor(doc.TargetLanguage)
            .SelectMany(tm => Safe(() => tm.Search(doc.SourceLanguage, doc.TargetLanguage, segment.Source, minScore ?? project.LookupMinScore, 10, context)))
            .OrderByDescending(m => m.Score).ThenBy(m => m.TmName == "Projekt-TM" ? 0 : 1).ThenByDescending(m => m.Unit.Modified)
            .Take(10).ToList();
    }

    /// <summary>Setzt einen TM-Treffer ins Segment ein; abweichende Zahlen werden angepasst.</summary>
    public static void ApplyMatch(Segment segment, TmMatch match, string targetLanguage = "")
    {
        var target = match.Score < 100
            ? Placeables.AdaptNumbers(segment.Source, match.Unit.Source, match.Unit.Target, targetLanguage)
            : match.Unit.Target;
        segment.Target = TagText.EnsureTags(segment.Source, target);
        segment.MatchPercent = match.Score;
        segment.Origin = TranslationOrigin.TranslationMemory;
        segment.Status = SegmentStatus.Draft;
        segment.Modified = DateTime.UtcNow;
    }

    /// <summary>
    /// Bestätigt ein Segment (Strg+Eingabe): Status „Übersetzt“, Eintrag ins Projekt-TM
    /// und Übernahme in alle unbestätigten Segmente mit gleicher Quelle.
    /// </summary>
    public static int Confirm(TranslationProject project, BilingualDocument doc, Segment segment, SegmentStatus status = SegmentStatus.Translated)
    {
        if (segment.Target.Trim().Length == 0) return 0;
        // Änderungsverlauf: jede bestätigte Fassung, die sich von der letzten unterscheidet
        if (segment.History.LastOrDefault()?.Target != segment.Target)
            segment.History.Add(new SegmentRevision(DateTime.UtcNow, segment.Target, status, segment.Origin));
        segment.Status = status;
        segment.Modified = DateTime.UtcNow;
        if (segment.Origin == TranslationOrigin.None) segment.Origin = TranslationOrigin.Manual;
        // Mit Kontext speichern: so wird derselbe Satz später im gleichen Zusammenhang als 101 % erkannt
        var ctx = ContextOf(doc, segment);
        project.ProjectTm.Add(doc.SourceLanguage, doc.TargetLanguage, segment.Source, segment.Target, segment.Origin.ToString(),
            ctx.Structure, null, ctx.Previous, ctx.Next);
        // gemeinsame TMs (z. B. Teamserver) erhalten bestätigte Übersetzungen ebenfalls
        foreach (var shared in project.SharedTmsToWrite(doc.TargetLanguage))
            Safe(() =>
            {
                shared.Add(doc.SourceLanguage, doc.TargetLanguage, segment.Source, segment.Target, segment.Origin.ToString(),
                    ctx.Structure, null, ctx.Previous, ctx.Next);
                return new List<int>();
            });

        var propagated = 0;
        foreach (var other in doc.Segments)
        {
            if (ReferenceEquals(other, segment) || other.Source != segment.Source || other.IsConfirmed) continue;
            other.Target = segment.Target;
            other.Status = status;          // übernommene Segmente erhalten den Status des Originals
            other.Origin = TranslationOrigin.AutoPropagated;
            other.MatchPercent = 100;
            propagated++;
        }
        return propagated;
    }

    /// <summary>
    /// Vorübersetzung aus den TMs. Kontexttreffer (101 %) werden – je nach Projekteinstellung – gleich bestätigt,
    /// 100-%-Treffer auf Wunsch. Mehrdeutige Treffer (mehrere Übersetzungen, keine passt zum Kontext) nie.
    /// </summary>
    public static (int Applied, int Confirmed) Pretranslate(TranslationProject project, BilingualDocument doc, bool confirmExact = false, bool overwrite = false)
    {
        var applied = 0;
        var confirmed = 0;
        var all = doc.Segments.ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (s.Locked) { if (s.Target.Length == 0) s.Target = s.Source; continue; }
            if (s.IsConfirmed || (!overwrite && s.Target.Length > 0)) continue;
            var best = Lookup(project, doc, s, project.PretranslateMinScore, ContextOf(doc, all, i)).FirstOrDefault();
            if (best == null) continue;
            ApplyMatch(s, best, doc.TargetLanguage);
            applied++;
            if ((best.Score == 101 && project.ConfirmContextMatches) || (confirmExact && best.Score == 100))
            {
                s.Status = SegmentStatus.Translated;
                confirmed++;
            }
        }
        return (applied, confirmed);
    }

    public static void CopySourceToTarget(Segment segment)
    {
        segment.Target = segment.Source;
        segment.Origin = TranslationOrigin.Source;
        segment.Status = SegmentStatus.Draft;
    }

    /// <summary>Suchen und Ersetzen im Zieltext eines Dokuments.</summary>
    public static int ReplaceInTargets(BilingualDocument doc, string find, string replace, bool matchCase)
    {
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var n = 0;
        foreach (var s in doc.Segments.Where(s => s.Target.Contains(find, cmp)))
        {
            s.Target = s.Target.Replace(find, replace, cmp);
            if (s.IsConfirmed) s.Status = SegmentStatus.Draft;
            n++;
        }
        return n;
    }

    /// <summary>Terminologie aller für die Zielsprache angebundenen Terminologien.</summary>
    public static List<Terminology.TermHit> Terms(TranslationProject project, BilingualDocument doc, Segment segment) =>
        project.TermbasesFor(doc.TargetLanguage)
            .SelectMany(tb => Safe(() => tb.Recognize(segment.Source, doc.SourceLanguage, doc.TargetLanguage))).ToList();

    /// <summary>
    /// Passendes Wissen zum Segment: Stilregeln der Zielsprache gelten immer, dazu Einträge, die inhaltlich
    /// zum Segment passen (Quell- und Zielsprache), aus allen angebundenen Wissensdatenbanken.
    /// </summary>
    public static List<Knowledge.KnowledgeEntry> Knowledge(TranslationProject project, BilingualDocument doc, Segment segment, int max = 6)
    {
        var result = new List<Knowledge.KnowledgeEntry>();
        foreach (var kb in project.KnowledgeFor(doc.TargetLanguage))
            result.AddRange(Safe(() => kb.Browse(doc.TargetLanguage, null, 0, 100))
                .Where(e => e.Kind == Core.Knowledge.KnowledgeKind.Style).Take(5));
        var query = TagText.StripTags(segment.Source) + " " + project.Topic;
        foreach (var lang in new[] { doc.SourceLanguage, doc.TargetLanguage })
            foreach (var kb in project.KnowledgeFor(lang))
                result.AddRange(Safe(() => kb.Search(lang, query, max)));
        return result.DistinctBy(e => (e.Title, e.Content)).Take(max + 3).ToList();
    }

    /// <summary>Eine nicht erreichbare Teamressource darf die Arbeit nicht aufhalten.</summary>
    internal static List<T> Safe<T>(Func<List<T>> query)
    {
        try { return query(); }
        catch (Remote.RemoteException) { return []; }
    }

    // ---------- Fragmente und Vorschläge ----------

    public static List<FragmentMatch> Fragments(TranslationProject project, BilingualDocument doc, Segment segment) =>
        project.TmsFor(doc.TargetLanguage).SelectMany(tm => Safe(() => tm.Fragments(doc.SourceLanguage, doc.TargetLanguage, segment.Source)))
            .GroupBy(f => f.WordIndex).Select(g => g.OrderByDescending(f => f.WordCount).First())
            .OrderBy(f => f.WordIndex).ToList();

    /// <summary>
    /// AutoSuggest: Wörter und Wendungen, die beim Tippen im Ziel angeboten werden –
    /// Terminologie, Fragment-Treffer und Wortgruppen aus den besten TM-Treffern.
    /// </summary>
    public static List<string> Suggestions(TranslationProject project, BilingualDocument doc, Segment segment,
        IEnumerable<TmMatch>? matches = null, IEnumerable<FragmentMatch>? fragments = null)
    {
        var list = new List<string>();
        foreach (var hit in Terms(project, doc, segment))
                list.AddRange(hit.Translations.Where(t => t.Status != Terminology.TermStatus.Forbidden).Select(t => t.Text));
        foreach (var f in fragments ?? Fragments(project, doc, segment))
            list.Add(TagText.StripTags(f.Unit.Target).Trim().TrimEnd('.', ':', ';'));
        foreach (var m in (matches ?? []).Take(3))
        {
            var words = TagText.StripTags(m.Unit.Target).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '"', '(', ')')).Where(w => w.Length > 0).ToList();
            list.AddRange(words.Where(w => w.Length >= 5));
            for (var i = 0; i + 1 < words.Count; i++)
                if (words[i].Length >= 3 && words[i + 1].Length >= 3) list.Add(words[i] + " " + words[i + 1]);
        }
        return list.Where(s => s.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Segmente teilen und zusammenführen ----------

    /// <summary>XLIFF-Segmente gehören zur Datei (mrk/segment-IDs) und dürfen nicht neu geschnitten werden.</summary>
    public static bool CanResegment(BilingualDocument doc) => doc.FilterId != "xliff";

    /// <summary>Teilt ein Segment an einer Stelle im Quelltext. Nicht innerhalb eines Tag-Paars.</summary>
    public static Segment? Split(BilingualDocument doc, Segment segment, int sourceIndex)
    {
        var para = doc.ParagraphOf(segment);
        if (!CanResegment(doc)) return null;
        if (para == null || sourceIndex <= 0 || sourceIndex >= segment.Source.Length) return null;
        if (TagText.OpenDepth(segment.Source)[sourceIndex] != 0) return null;
        if (TagText.Tags(segment.Source).Any(t => sourceIndex > t.Index && sourceIndex < t.Index + t.Length)) return null;

        var left = segment.Source[..sourceIndex];
        var right = segment.Source[sourceIndex..];
        var ws = right.Length - right.TrimStart().Length;
        var second = new Segment
        {
            Source = right.TrimStart(),
            TrailingWhitespace = segment.TrailingWhitespace,
        };
        segment.Source = left.TrimEnd();
        segment.TrailingWhitespace = left[segment.Source.Length..] + right[..ws];
        segment.Target = "";
        segment.Status = SegmentStatus.NotTranslated;
        segment.Origin = TranslationOrigin.None;
        segment.MatchPercent = null;
        segment.Locked = !Segmenter.HasTranslatableText(segment.Source);
        second.Locked = !Segmenter.HasTranslatableText(second.Source);
        para.Segments.Insert(para.Segments.IndexOf(segment) + 1, second);
        doc.Renumber();
        return second;
    }

    /// <summary>Führt ein Segment mit dem folgenden desselben Absatzes zusammen.</summary>
    public static bool MergeWithNext(BilingualDocument doc, Segment segment)
    {
        var para = doc.ParagraphOf(segment);
        if (para == null || !CanResegment(doc)) return false;
        var i = para.Segments.IndexOf(segment);
        if (i < 0 || i + 1 >= para.Segments.Count) return false;
        var next = para.Segments[i + 1];
        var sep = segment.TrailingWhitespace;
        segment.Source = segment.Source + sep + next.Source;
        if (segment.Target.Length > 0 || next.Target.Length > 0)
            segment.Target = (segment.Target + (segment.Target.Length > 0 && next.Target.Length > 0 ? sep : "") + next.Target).Trim();
        segment.TrailingWhitespace = next.TrailingWhitespace;
        segment.Status = segment.Target.Length > 0 ? SegmentStatus.Draft : SegmentStatus.NotTranslated;
        segment.Locked = segment.Locked && next.Locked;
        para.Segments.RemoveAt(i + 1);
        doc.Renumber();
        return true;
    }
}
