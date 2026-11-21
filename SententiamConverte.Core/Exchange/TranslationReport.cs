using System.Text;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Exchange;

/// <summary>Ein Segment im Bericht: Satz, Übersetzung, woher sie stammt, Recherche zu seinen Teilen, Netzprüfung.</summary>
public sealed record ReportSegment(int Id, string Source, string Target, TranslationOrigin Origin, int? MatchPercent,
    IReadOnlyList<TermDossier> Research, TranslationWebCheckResult? Check = null, string? Before = null);

/// <summary>
/// Übersetzungsbericht als HTML-Seite: welche Sätze und Ausdrücke wie übersetzt wurden und warum – mit Bedeutung,
/// Begründung, Netzprüfung (Zahlen, Urteil) und Belegen, die im Browser direkt zur zitierten Stelle springen. Texte der
/// Oberfläche kommen über <paramref name="t"/> (übersetzt in die Sprache der Oberfläche).
/// </summary>
public static class TranslationReport
{
    public static string Html(string title, string sourceLanguage, string targetLanguage, IReadOnlyList<ReportSegment> segments,
        string? engine = null, Func<string, string>? t = null)
    {
        t ??= s => s;
        // nur das Nötige maskieren – Umlaute und Sonderzeichen bleiben lesbar (die Seite ist UTF-8)
        static string E(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        string Link(string url, string? quote, string label) =>
            $"<a href=\"{E(SourceLink.WithTextFragment(url, quote))}\" target=\"_blank\">{E(label)}</a>";
        string Origin(ReportSegment s) => t(s.Origin switch
        {
            TranslationOrigin.TranslationMemory => s.MatchPercent is { } m ? $"TM-Treffer {m} %" : "TM-Treffer",
            TranslationOrigin.Ai => "KI",
            TranslationOrigin.Manual => "von Hand",
            TranslationOrigin.AutoPropagated => "automatisch übernommen",
            TranslationOrigin.Source => "Ausgangstext übernommen",
            _ => "offen",
        });
        string Verdict(PhraseVerdict v) => t(v switch
        {
            PhraseVerdict.Fine => "passt",
            PhraseVerdict.Questionable => "fraglich",
            PhraseVerdict.Wrong => "falsch",
            _ => "unklar",
        });

        var dossiers = segments.SelectMany(s => s.Research).GroupBy(d => (d.Expression.ToLowerInvariant(), d.TargetLanguage))
            .Select(g => g.OrderByDescending(d => d.Created).First()).OrderBy(d => d.Expression, StringComparer.CurrentCultureIgnoreCase).ToList();
        var checkedCount = segments.Count(s => s.Check != null);
        var improved = segments.Count(s => s.Before != null);

        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{E(title)}}</title>
            <style>
            body{font-family:system-ui,Segoe UI,sans-serif;max-width:1000px;margin:24px auto;padding:0 16px;color:#1f2937;line-height:1.45}
            h1{font-size:22px}h2{font-size:18px;margin-top:28px;border-bottom:1px solid #e5e7eb}
            .seg{border:1px solid #e5e7eb;border-radius:6px;padding:10px 12px;margin:10px 0}
            .src{color:#374151}.tgt{font-weight:600}.muted{color:#6b7280;font-size:13px}
            .ok{color:#15803d}.warn{color:#b45309}.bad{color:#b91c1c}
            table{border-collapse:collapse;width:100%;font-size:14px}td,th{border-bottom:1px solid #e5e7eb;padding:6px;text-align:left;vertical-align:top}
            del{color:#b91c1c}ins{color:#15803d;text-decoration:none}
            </style></head><body>
            <h1>{{E(title)}}</h1>
            <p class="muted">{{E(sourceLanguage)}} → {{E(targetLanguage)}} · {{E(DateTime.Now.ToString("g"))}}{{(engine != null ? " · " + E(engine) : "")}}</p>
            <p>{{E(t($"{segments.Count} Segmente, {dossiers.Count} recherchierte Ausdrücke, {checkedCount} Übersetzungen im Netz geprüft, {improved} verbessert."))}}</p>
            """);

        if (dossiers.Count > 0)
        {
            sb.Append($"<h2>{E(t("Ausdrücke und Begriffe"))}</h2><table><tr><th>{E(t("Ausdruck"))}</th><th>{E(t("Übersetzung"))}</th><th>{E(t("Warum"))}</th><th>{E(t("Belege"))}</th></tr>");
            foreach (var d in dossiers)
            {
                var evidenced = DossierStore.IsEvidenced(d);
                sb.Append("<tr><td>").Append(E(d.Expression)).Append("</td><td><b>").Append(E(d.Recommended ?? "?")).Append("</b><br><span class=\"")
                  .Append(evidenced ? "ok" : "warn").Append("\">").Append(E(t(evidenced ? "belegt" : "nicht belegt"))).Append("</span></td><td>");
                if (d.Meaning is { Length: > 0 }) sb.Append(E(t("Bedeutung"))).Append(": ").Append(E(d.Meaning)).Append("<br>");
                if (d.Strategy is { Length: > 0 }) sb.Append(E(t("Strategie"))).Append(": ").Append(E(d.Strategy)).Append("<br>");
                if (d.Reasoning is { Length: > 0 }) sb.Append(E(d.Reasoning));
                sb.Append("</td><td>");
                foreach (var s in d.Sources.Take(4))
                    sb.Append(s.Number > 0 ? $"[{s.Number}] " : "").Append(Link(s.Url, s.Quote, s.Title.Length > 0 ? s.Title : s.Url))
                      .Append(s.Quote is { Length: > 0 } q ? $"<br><span class=\"muted\">„{E(q)}“</span>" : "").Append("<br>");
                sb.Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append($"<h2>{E(t("Sätze"))}</h2>");
        foreach (var s in segments)
        {
            sb.Append($"<div class=\"seg\"><div class=\"muted\">{E(t($"Segment {s.Id}"))} · {E(Origin(s))}</div>");
            sb.Append($"<div class=\"src\">{E(s.Source)}</div><div class=\"tgt\">{E(s.Target.Length > 0 ? s.Target : t("(noch nicht übersetzt)"))}</div>");
            if (s.Before != null)
                sb.Append($"<div>{E(t("Verbessert"))}: <del>{E(s.Before)}</del> → <ins>{E(s.Target)}</ins></div>");
            if (s.Research.Count > 0)
            {
                sb.Append("<div class=\"muted\">").Append(E(t("Recherche"))).Append(": ");
                sb.Append(string.Join("; ", s.Research.Select(d => $"{E(d.Expression)} → {E(d.Recommended ?? "?")}" +
                    $" ({E(t(DossierStore.IsEvidenced(d) ? "belegt" : "nicht belegt"))})")));
                sb.Append("</div>");
            }
            if (s.Check is { } c)
            {
                sb.Append("<div>").Append(E(t("Im Netz geprüft"))).Append(c.Summary.Length > 0 ? " – " + E(c.Summary) : "").Append("<ul>");
                foreach (var p in c.Phrases)
                {
                    var css = p.Verdict switch { PhraseVerdict.Fine => "ok", PhraseVerdict.Wrong => "bad", _ => "warn" };
                    sb.Append($"<li><span class=\"{css}\">„{E(p.Phrase)}“: {E(Verdict(p.Verdict))}</span>");
                    if (p.Recommended is { Length: > 0 } rec && !rec.Equals(p.Phrase, StringComparison.OrdinalIgnoreCase))
                        sb.Append($" → „{E(rec)}“");
                    if (p.Explanation.Length > 0) sb.Append($"<br>{E(p.Explanation)}");
                    foreach (var v in p.Variants)
                    {
                        sb.Append($"<br><span class=\"muted\">„{E(v.Variant)}“: {E(TranslationWebCheck.Describe(v))}</span>");
                        foreach (var ex in v.Examples.Take(2))
                            sb.Append(" · ").Append(Link(ex.Url, ex.Sentence, ex.Title.Length > 0 ? ex.Title : ex.Url));
                    }
                    sb.Append("</li>");
                }
                sb.Append("</ul></div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
