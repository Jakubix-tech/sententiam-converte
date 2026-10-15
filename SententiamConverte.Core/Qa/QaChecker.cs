using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;

using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Qa;

public enum QaSeverity { Info, Warning, Error }

public sealed record QaIssue(int SegmentId, QaSeverity Severity, string Check, string Message);

/// <summary>Welche Prüfungen laufen.</summary>
public sealed class QaSettings
{
    public bool EmptyTarget { get; set; } = true;
    public bool TargetEqualsSource { get; set; } = true;
    public bool Tags { get; set; } = true;
    public bool Numbers { get; set; } = true;
    public bool Whitespace { get; set; } = true;
    public bool EndPunctuation { get; set; } = true;
    public bool Brackets { get; set; } = true;
    public bool Terminology { get; set; } = true;
    public bool RepeatedWords { get; set; } = true;
    public bool InconsistentTranslations { get; set; } = true;
    public bool Length { get; set; } = true;
    public double MaxLengthRatio { get; set; } = 1.6;
    public List<string> ForbiddenWords { get; set; } = new();
    public bool Spelling { get; set; } = true;

    /// <summary>Eigene Prüfregeln mit regulären Ausdrücken.</summary>
    public List<RegexRule> RegexRules { get; set; } = new();
}

/// <summary>
/// Eigene QA-Regel. Ohne <see cref="SourcePattern"/>: Meldung, wenn <see cref="TargetPattern"/> im Ziel vorkommt.
/// Mit SourcePattern: Meldung, wenn die Quelle passt, das Ziel aber nicht (z. B. „© in der Quelle → © im Ziel“).
/// </summary>
public sealed class RegexRule
{
    public string Name { get; set; } = "";
    public string? SourcePattern { get; set; }
    public string TargetPattern { get; set; } = "";
    public QaSeverity Severity { get; set; } = QaSeverity.Warning;
    public bool IgnoreCase { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public static partial class QaChecker
{
    private static readonly List<Extensions.IQaCheck> ExtraChecks = new();

    /// <summary>Zusätzliche Prüfung anmelden (Erweiterungen).</summary>
    public static void Register(Extensions.IQaCheck check)
    {
        lock (ExtraChecks)
        {
            ExtraChecks.RemoveAll(c => c.Name == check.Name);       // erneutes Laden ersetzt statt verdoppelt
            ExtraChecks.Add(check);
        }
    }

    [GeneratedRegex(@"\d+(?:[.,  ]\d{3})*(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\b(\p{L}+)\s+\1\b", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedWordRegex();

    public static List<QaIssue> Check(BilingualDocument doc, QaSettings settings, ITermbase? termbase = null, SpellChecker? spell = null)
    {
        var issues = new List<QaIssue>();
        var ignore = termbase == null ? null : TermsOf(termbase, doc.TargetLanguage);
        List<Extensions.IQaCheck> extra;
        lock (ExtraChecks) extra = ExtraChecks.ToList();
        foreach (var s in doc.Segments)
        {
            issues.AddRange(CheckSegment(s, doc.SourceLanguage, doc.TargetLanguage, settings, termbase));
            if (!s.Locked)
                foreach (var check in extra)
                {
                    try { issues.AddRange(check.Check(s, doc)); }
                    catch (Exception ex) { issues.Add(new QaIssue(s.Id, QaSeverity.Info, check.Name, "Prüfung fehlgeschlagen: " + ex.Message)); }
                }
            if (settings.Spelling && spell != null && !s.Locked && s.Target.Length > 0)
                foreach (var e in spell.Check(s.Target, doc.TargetLanguage, ignore, withSuggestions: false))
                    issues.Add(new QaIssue(s.Id, QaSeverity.Warning, "Rechtschreibung", $"„{e.Word}“ ist nicht im Wörterbuch."));
        }

        if (settings.InconsistentTranslations)
        {
            foreach (var g in doc.Segments.Where(s => !s.Locked && s.Target.Length > 0).GroupBy(s => s.Source))
            {
                var variants = g.Select(s => s.Target).Distinct().ToList();
                if (variants.Count > 1)
                    foreach (var s in g.Skip(1))
                        issues.Add(new QaIssue(s.Id, QaSeverity.Warning, "Konsistenz",
                            $"Gleiche Quelle wie Segment {g.First().Id}, aber andere Übersetzung."));
            }
        }
        return issues;
    }

    public static IEnumerable<QaIssue> CheckSegment(Segment s, string srcLang, string tgtLang, QaSettings st, ITermbase? tb)
    {
        if (s.Locked) yield break;
        var src = s.Source;
        var tgt = s.Target;

        if (tgt.Trim().Length == 0)
        {
            if (st.EmptyTarget) yield return new(s.Id, QaSeverity.Error, "Leer", "Segment ist nicht übersetzt.");
            yield break;
        }

        if (st.TargetEqualsSource && tgt == src && Segmentation.Segmenter.HasTranslatableText(src)
            && TagText.StripTags(src).Split(' ').Length > 1)
            yield return new(s.Id, QaSeverity.Warning, "Unübersetzt", "Ziel ist identisch mit der Quelle.");

        if (st.Tags)
        {
            if (TagText.TagSignature(src) != TagText.TagSignature(tgt))
                yield return new(s.Id, QaSeverity.Error, "Tags", "Tags in Quelle und Ziel stimmen nicht überein.");
            else if (!TagText.IsBalanced(tgt))
                yield return new(s.Id, QaSeverity.Error, "Tags", "Tags im Ziel sind falsch verschachtelt.");
        }

        if (st.Numbers)
        {
            var a = Numbers(src);
            var b = Numbers(tgt);
            var missing = a.Except(b).ToList();
            var extra = b.Except(a).ToList();
            if (missing.Count > 0) yield return new(s.Id, QaSeverity.Error, "Zahlen", "Fehlt im Ziel: " + string.Join(", ", missing));
            if (extra.Count > 0) yield return new(s.Id, QaSeverity.Warning, "Zahlen", "Nur im Ziel: " + string.Join(", ", extra));
        }

        if (st.Whitespace)
        {
            var plain = TagText.StripTags(tgt);
            if (plain.Contains("  ")) yield return new(s.Id, QaSeverity.Warning, "Leerzeichen", "Doppeltes Leerzeichen im Ziel.");
            if (plain != plain.Trim()) yield return new(s.Id, QaSeverity.Warning, "Leerzeichen", "Leerzeichen am Anfang oder Ende des Ziels.");
            if (Regex.IsMatch(plain, @"\s[,.;:!?](?!\d)") && !Languages.Matches(tgtLang, "fr"))
                yield return new(s.Id, QaSeverity.Info, "Leerzeichen", "Leerzeichen vor Satzzeichen.");
        }

        if (st.EndPunctuation)
        {
            var se = EndPunct(src);
            var te = EndPunct(tgt);
            if (se != te) yield return new(s.Id, QaSeverity.Warning, "Satzzeichen", $"Satzende verschieden: „{se}“ / „{te}“.");
        }

        if (st.Brackets)
        {
            foreach (var (o, c) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
                if (tgt.Count(ch => ch == o) != tgt.Count(ch => ch == c))
                    yield return new(s.Id, QaSeverity.Warning, "Klammern", $"Klammern {o}{c} nicht paarig.");
        }

        if (st.RepeatedWords)
            foreach (Match m in RepeatedWordRegex().Matches(TagText.StripTags(tgt)))
                yield return new(s.Id, QaSeverity.Info, "Wortdopplung", $"„{m.Value}“");

        if (st.Length)
        {
            var ls = TagText.StripTags(src).Length;
            var lt = TagText.StripTags(tgt).Length;
            if (ls >= 20 && lt > ls * st.MaxLengthRatio)
                yield return new(s.Id, QaSeverity.Info, "Länge", $"Ziel ist {lt * 100 / ls} % so lang wie die Quelle.");
            if (ls >= 20 && lt < ls / (st.MaxLengthRatio * 1.5))
                yield return new(s.Id, QaSeverity.Warning, "Länge", $"Ziel ist nur {lt * 100 / ls} % so lang wie die Quelle.");
        }

        foreach (var rule in st.RegexRules.Where(r => r.Enabled && r.TargetPattern.Length > 0))
        {
            var opts = rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            bool hit;
            try
            {
                var targetMatch = Regex.IsMatch(TagText.StripTags(tgt), rule.TargetPattern, opts, TimeSpan.FromMilliseconds(200));
                hit = string.IsNullOrEmpty(rule.SourcePattern)
                    ? targetMatch
                    : Regex.IsMatch(TagText.StripTags(src), rule.SourcePattern, opts, TimeSpan.FromMilliseconds(200)) && !targetMatch;
            }
            catch (ArgumentException) { hit = false; }
            catch (RegexMatchTimeoutException) { hit = false; }
            if (hit) yield return new(s.Id, rule.Severity, "Regel", rule.Name.Length > 0 ? rule.Name : rule.TargetPattern);
        }

        foreach (var w in st.ForbiddenWords.Where(w => w.Length > 0))
            if (Regex.IsMatch(tgt, $@"(?<!\p{{L}}){Regex.Escape(w)}(?!\p{{L}})", RegexOptions.IgnoreCase))
                yield return new(s.Id, QaSeverity.Error, "Verboten", $"Unerwünschtes Wort „{w}“.");

        if (st.Terminology && tb != null)
        {
            var tgtPlain = TagText.StripTags(tgt);
            foreach (var hit in tb.Recognize(src, srcLang, tgtLang))
            {
                var allowed = hit.Translations.Where(t => t.Status != TermStatus.Forbidden).ToList();
                if (allowed.Count > 0 && !allowed.Any(t => Termbase.FindTerm(tgtPlain, t.Text).Any()))
                    yield return new(s.Id, QaSeverity.Warning, "Terminologie",
                        $"„{hit.SourceTerm.Text}“ sollte mit „{allowed[0].Text}“ übersetzt werden.");
                foreach (var bad in hit.Translations.Where(t => t.Status == TermStatus.Forbidden))
                    if (Termbase.FindTerm(tgtPlain, bad.Text).Any())
                        yield return new(s.Id, QaSeverity.Error, "Terminologie", $"Verbotene Benennung „{bad.Text}“ verwendet.");
            }
        }
    }

    private static HashSet<string> TermsOf(ITermbase tb, string language) =>
        tb.AllConcepts().SelectMany(c => c.Terms).Where(t => Languages.Matches(t.Language, language))
            .SelectMany(t => t.Text.Split(' ', '-')).Where(w => w.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Zahlen normalisiert (Tausender- und Dezimaltrenner vereinheitlicht), damit 1.000,5 und 1,000.5 gleich sind.</summary>
    internal static List<string> Numbers(string text) =>
        NumberRegex().Matches(TagText.StripTags(text))
            .Select(m => Regex.Replace(m.Value, @"[.,  ](?=\d{3}(\D|$))", ""))
            .Select(v => v.Replace(',', '.'))
            .ToList();

    private static string EndPunct(string text)
    {
        var t = TagText.StripTags(text).TrimEnd();
        var c = t.Length > 0 ? t[^1] : ' ';
        return c switch
        {
            '.' or '!' or '?' or ':' or ';' or '…' => c.ToString(),
            '。' => ".",
            '！' => "!",
            '？' => "?",
            _ => "",
        };
    }
}
