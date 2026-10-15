using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;

namespace Beispiel.Erweiterung;

/// <summary>QA-Prüfung: Ausrufezeichen im Ziel, das in der Quelle nicht steht.</summary>
public sealed class ExclamationCheck : IQaCheck
{
    public string Name => "Ausrufezeichen";

    public IEnumerable<QaIssue> Check(Segment segment, BilingualDocument document)
    {
        if (segment.Target.Contains('!') && !segment.Source.Contains('!') && !segment.Source.Contains('¡'))
            yield return new QaIssue(segment.Id, QaSeverity.Warning, Name, "Ausrufezeichen im Ziel, aber nicht in der Quelle.");
    }
}

/// <summary>Befehl für das ganze Dokument: gerade Anführungszeichen durch die der Zielsprache ersetzen.</summary>
public sealed class TypographicQuotesCommand : IExtensionCommand
{
    public string Id => "beispiel.anfuehrungszeichen";
    public string Name => "Typografische Anführungszeichen";
    public string? Description => "Ersetzt gerade Anführungszeichen \"…\" im Zieltext durch die der Zielsprache (z. B. „…“ im Deutschen, “…” im Englischen, « … » im Französischen).";
    public CommandScope Scope => CommandScope.Document;

    public Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var doc = context.Document ?? throw new InvalidOperationException("Bitte zuerst ein Dokument im Editor öffnen.");
        var (open, close) = QuotesFor(doc.TargetLanguage);
        var changed = 0;
        foreach (var s in doc.Segments.Where(s => !s.Locked && s.Target.Contains('"')))
        {
            s.Target = Replace(s.Target, open, close);
            s.Modified = DateTime.UtcNow;
            changed++;
        }
        context.Log?.Report($"Zielsprache {doc.TargetLanguage}: {open}…{close}");
        return Task.FromResult(new ExtensionResult($"{changed} Segment(e) angepasst.", DocumentChanged: changed > 0));
    }

    internal static (string Open, string Close) QuotesFor(string language) => language.Split('-')[0].ToLowerInvariant() switch
    {
        "de" or "cs" or "sk" or "sl" or "lt" => ("„", "“"),
        "pl" or "hu" or "ro" or "nl" or "hr" or "bg" => ("„", "”"),
        "fr" => ("«\u00A0", "\u00A0»"),
        "it" or "es" or "pt" or "ru" or "uk" or "el" => ("«", "»"),
        "sv" or "fi" => ("”", "”"),
        _ => ("“", "”"),
    };

    /// <summary>Öffnend am Anfang, nach Leerraum oder Klammer; sonst schließend.</summary>
    internal static string Replace(string text, string open, string close)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') { sb.Append(text[i]); continue; }
            var opening = i == 0 || char.IsWhiteSpace(text[i - 1]) || "([{⟩–—/".Contains(text[i - 1]);
            sb.Append(opening ? open : close);
        }
        return sb.ToString();
    }
}

/// <summary>Befehl für ein Segment: Wörter und Zeichen zählen.</summary>
public sealed class SegmentStatisticsCommand : IExtensionCommand
{
    public string Id => "beispiel.statistik";
    public string Name => "Segment-Statistik";
    public string? Description => "Zählt Wörter und Zeichen von Quelle und Ziel des gewählten Segments.";
    public CommandScope Scope => CommandScope.Segment;

    public Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var s = context.Segment ?? throw new InvalidOperationException("Bitte im Editor ein Segment wählen.");
        var source = TagText.StripTags(s.Source);
        var target = TagText.StripTags(s.Target);
        static string Words(string t)
        {
            var n = t.Split((char[])[' ', '\t', '\n', (char)0xA0], StringSplitOptions.RemoveEmptyEntries).Length;
            return n == 1 ? "1 Wort" : $"{n} Wörter";
        }
        var ratio = source.Length == 0 ? 0 : 100.0 * target.Length / source.Length;
        return Task.FromResult(new ExtensionResult(
            $"Segment {s.Id}: Quelle {Words(source)} / {source.Length} Zeichen · Ziel {Words(target)} / {target.Length} Zeichen ({ratio:0} % der Quelllänge)"));
    }
}

/// <summary>Befehl mit KI: schlägt für das Segment eine andere Übersetzung vor. Argument „stil“, z. B. stil=förmlich.</summary>
public sealed class AlternativeCommand : IExtensionCommand
{
    public string Id => "beispiel.alternative";
    public string Name => "Alternative vorschlagen (KI)";
    public string? Description => "Fragt das eingestellte Sprachmodell nach einer weiteren Übersetzung des Segments. Argument: stil=… (z. B. förmlich, knapp).";
    public CommandScope Scope => CommandScope.Segment;

    public async Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var s = context.Segment ?? throw new InvalidOperationException("Bitte im Editor ein Segment wählen.");
        var doc = context.Document ?? throw new InvalidOperationException("Bitte zuerst ein Dokument öffnen.");
        var model = context.ChatModel ?? throw new InvalidOperationException("Kein KI-Zugang eingerichtet (Einstellungen → KI).");
        var style = context.Arguments.TryGetValue("stil", out var st) && st.Trim().Length > 0 ? st.Trim() : "natürlich und gut lesbar";
        context.Log?.Report($"Frage {model.DisplayName} …");
        var answer = await model.AskAsync(
            "Du bist Fachübersetzer. Antworte nur mit der Übersetzung, ohne Erklärung. Platzhalter wie ⟨1⟩ und ⟨/1⟩ unverändert übernehmen.",
            $"Übersetze von {doc.SourceLanguage} nach {doc.TargetLanguage}. Stil: {style}.\nQuelle: {s.Source}\nBisherige Übersetzung: {s.Target}",
            ct);
        return new ExtensionResult("Vorschlag: " + answer.Trim());
    }
}
