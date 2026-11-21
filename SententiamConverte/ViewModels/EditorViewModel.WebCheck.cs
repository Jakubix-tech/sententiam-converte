using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Übersetzung im Netz der Zielsprache prüfen: Wendungen zählen, im Zusammenhang bewerten, bessere Fassung anbieten.</summary>
public partial class EditorViewModel
{
    [RelayCommand]
    private async Task CheckTranslationOnWeb()
    {
        if (_doc == null || Selected is not { } seg || IsAiRunning) return;
        SidePanelTab = 7;
        BeginAiResult(seg);
        if (seg.Target.Trim().Length == 0)
        {
            AiResult = "Das Segment hat noch keine Übersetzung.";
            return;
        }
        IsAiRunning = true;
        AiLog = "";
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var r = await new TranslationWebCheck(ctx).RunAsync(seg.Source, seg.Target, _doc.SourceLanguage, _doc.TargetLanguage, Project?.Topic);
            ShowAiResult(seg, FormatWebCheck(r, _doc.TargetLanguage), r.Improved is { } better ? [better] : null);
        }
        catch (Exception ex) { ShowAiResult(seg, "Fehler: " + ex.Message); }
        finally { IsAiRunning = false; }
    }

    private static string VerdictName(PhraseVerdict v) => L.T(v switch
    {
        PhraseVerdict.Fine => "passt",
        PhraseVerdict.Questionable => "fraglich",
        PhraseVerdict.Wrong => "falsch",
        _ => "unklar",
    });

    private static string Counts(PhraseCount c)
    {
        var parts = new[]
        {
            c.WikipediaArticles is { } a ? L.T($"Wikipedia: {a:N0} Artikel") : null,
            c.WebHits is { } h ? L.T($"Web: {h} von {c.WebChecked} Treffern") : null,
        }.Where(s => s != null).ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : L.T("nicht gezählt");
    }

    /// <summary>Ergebnis lesbar: je Wendung Urteil, Zahlen je Variante, Empfehlung mit Begründung und die Belege mit Adresse.</summary>
    public static string FormatWebCheck(TranslationWebCheckResult r, string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("Im Netz geprüft") + (r.Summary.Length > 0 ? " – " + r.Summary : ""));
        sb.AppendLine(L.T($"Gezählt wurde die genaue Wortfolge in Texten der Zielsprache ({targetLanguage}): {r.Search}. Modell: {r.Model}."));
        foreach (var p in r.Phrases)
        {
            sb.AppendLine();
            sb.AppendLine($"{TranslationWebCheck.VerdictMark(p.Verdict)} „{p.Phrase}“" + (p.SourcePart.Length > 0 ? $" ← „{p.SourcePart}“" : "") + $" – {VerdictName(p.Verdict)}");
            foreach (var c in p.Variants) sb.AppendLine($"   „{c.Variant}“: {Counts(c)}");
            if (p.Recommended is { Length: > 0 } rec && !rec.Equals(p.Phrase, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("   → " + L.T("Empfehlung") + $": „{rec}“");
            if (p.Explanation.Length > 0) sb.AppendLine("   " + p.Explanation);
            foreach (var e in p.Variants.SelectMany(v => v.Examples.Select(x => (v.Variant, x))).DistinctBy(x => x.x.Url).Take(4))
                sb.AppendLine($"   [{e.Variant}] {e.x.Title} – {e.x.Url}");
        }
        if (r.Improved != null)
        {
            sb.AppendLine();
            sb.AppendLine(L.T("Verbesserte Fassung unten – mit „übernehmen“ als Entwurf einsetzen."));
        }
        return sb.ToString().TrimEnd();
    }
}
