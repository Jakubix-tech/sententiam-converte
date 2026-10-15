using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using System.Collections.Generic;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public partial class CandidateRowViewModel : ObservableObject
{
    public required TermCandidate Candidate { get; init; }
    [ObservableProperty] public partial bool Selected { get; set; } = true;

    public string Terms => string.Join("  ·  ", Candidate.Terms.Select(kv => $"{kv.Key}: {kv.Value}"));
    public string Definition => Candidate.Definition ?? "";
    public string Confidence => $"{Candidate.Confidence * 100:0} %";
    public string Evidence => string.Join("\n", Candidate.Evidence.Select(e => $"[{e.Language}] „{e.Quote}“ – {e.Url}"));
}

public partial class LanguageChoice : ObservableObject
{
    public required string Code { get; init; }
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>Bereich „Agenten“: Themenrecherche, Usus-Prüfung, Bewertung von Übersetzungen.</summary>
public partial class AgentsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public AgentsViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Log { get; set; } = "";

    // Recherche
    [ObservableProperty] public partial string Topic { get; set; } = "";
    [ObservableProperty] public partial string SourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string ExtraLanguages { get; set; } = "";
    [ObservableProperty] public partial string SeedTerms { get; set; } = "";
    [ObservableProperty] public partial int PagesPerLanguage { get; set; } = 3;
    [ObservableProperty] public partial string ResearchSummary { get; set; } = "";
    public ObservableCollection<LanguageChoice> TargetChoices { get; } = new();
    public ObservableCollection<CandidateRowViewModel> Candidates { get; } = new();
    public ObservableCollection<SourceRef> Sources { get; } = new();

    // Fachquellen
    public IEnumerable<string> ProfileNames => new[] { "(keins)" }.Concat(AppServices.Ai.SourceProfiles.Select(p => p.Name));
    [ObservableProperty] public partial string? ProfileName { get; set; } = "(keins)";
    private SourceProfile? Profile => AppServices.Ai.Profile(ProfileName is "(keins)" ? null : ProfileName);

    // Begriffsvergleich
    [ObservableProperty] public partial string CompareTerm { get; set; } = "";
    [ObservableProperty] public partial string CompareContext { get; set; } = "";
    [ObservableProperty] public partial string CompareCandidates { get; set; } = "";
    [ObservableProperty] public partial string CompareSourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string CompareTargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial string CompareResult { get; set; } = "";
    [ObservableProperty] public partial bool HasCompareResult { get; set; }
    private TermComparisonResult? _comparison;

    // Usus
    [ObservableProperty] public partial string UsageExpression { get; set; } = "";
    [ObservableProperty] public partial string UsageLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string UsageAlternatives { get; set; } = "";
    [ObservableProperty] public partial string UsageResult { get; set; } = "";

    // Bewertung
    [ObservableProperty] public partial string EvalSource { get; set; } = "";
    [ObservableProperty] public partial string EvalCandidates { get; set; } = "";
    [ObservableProperty] public partial string EvalTargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial bool EvalCheckUsage { get; set; } = true;
    [ObservableProperty] public partial string EvalResult { get; set; } = "";

    public void OnProjectChanged()
    {
        var p = _main.Project;
        if (p == null) return;
        Topic = p.Topic;
        SourceLanguage = p.SourceLanguage;
        TargetChoices.Clear();
        foreach (var l in p.TargetLanguages) TargetChoices.Add(new LanguageChoice { Code = l, IsChecked = true });
        UsageLanguage = p.TargetLanguages.FirstOrDefault() ?? p.SourceLanguage;
        EvalTargetLanguage = UsageLanguage;
        CompareSourceLanguage = p.SourceLanguage;
        CompareTargetLanguage = UsageLanguage;
        OnPropertyChanged(nameof(ProfileNames));
        ProfileName = p.SourceProfile ?? "(keins)";
    }

    private AgentContext Context(string? providerId) =>
        AgentContext.From(AppServices.Ai, providerId, new Progress<string>(m => Log += m + "\n"));

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (IsRunning) return;
        IsRunning = true;
        Log = "";
        _cts = new CancellationTokenSource();
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { Log += "Abgebrochen.\n"; }
        catch (Exception ex) { Log += "Fehler: " + ex.Message + "\n"; }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ---------- Recherche ----------

    [RelayCommand]
    private Task Research() => Run(async ct =>
    {
        if (Topic.Trim().Length == 0) { Log = "Bitte ein Thema angeben.\n"; return; }
        var targets = TargetChoices.Where(c => c.IsChecked).Select(c => c.Code)
            .Concat(ExtraLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Distinct().ToList();
        if (targets.Count == 0) { Log = "Bitte mindestens eine Zielsprache wählen.\n"; return; }
        var seeds = SeedTerms.Split(['\n', ';', ','], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        Candidates.Clear();
        Sources.Clear();
        ResearchSummary = "";
        var result = await new TermResearchAgent(Context(AppServices.Ai.ResearchProviderId))
            .RunAsync(Topic.Trim(), SourceLanguage.Trim(), targets, seeds, Math.Clamp(PagesPerLanguage, 1, 8), ct, Profile);
        foreach (var c in result.Candidates) Candidates.Add(new CandidateRowViewModel { Candidate = c, Selected = c.Confidence >= 0.6 });
        foreach (var s in result.Sources) Sources.Add(s);
        ResearchSummary = result.Summary;
    });

    /// <summary>Ausgewählte Begriffe aus dem Seed-Feld des Projekts übernehmen: unbekannte Wörter im aktuellen Dokument.</summary>
    [RelayCommand]
    private void SuggestSeedsFromProject()
    {
        var p = _main.Project;
        if (p == null || p.Files.Count == 0) return;
        var (file, lang) = p.Documents().First();
        var doc = p.LoadDocument(file, lang);
        // Häufige lange Wörter ohne Terminologie-Eintrag sind gute Kandidaten
        var known = p.ProjectTermbase.AllConcepts().SelectMany(c => c.Terms).Select(t => t.Text.ToLowerInvariant()).ToHashSet();
        var words = doc.Segments.SelectMany(s => Core.Tm.FuzzyMatcher.Words(s.Source))
            .Where(w => w.Length >= 8 && !known.Contains(w))
            .GroupBy(w => w).OrderByDescending(g => g.Count()).Take(15).Select(g => g.Key);
        SeedTerms = string.Join("\n", words);
    }

    [RelayCommand]
    private void AddSelectedToTermbase()
    {
        var p = _main.Project;
        if (p == null) { Log += "Kein Projekt geöffnet.\n"; return; }
        var n = 0;
        foreach (var c in Candidates.Where(c => c.Selected))
        {
            p.ProjectTermbase.AddConcept(
                c.Candidate.Terms.Select(kv => (kv.Key, kv.Value, TermStatus.Preferred)),
                c.Candidate.Definition, Topic, string.Join(" ", c.Candidate.Evidence.Select(e => e.Url).Distinct().Take(3)));
            n++;
        }
        Log += $"{n} Begriffe in die Projekt-Terminologie übernommen.\n";
        _main.Resources.Refresh();
    }

    // ---------- Begriffsvergleich ----------

    [RelayCommand]
    private Task Compare() => Run(async ct =>
    {
        if (CompareTerm.Trim().Length == 0) { Log = "Bitte einen Begriff angeben.\n"; return; }
        HasCompareResult = false;
        var p = _main.Project;
        var hints = CompareCandidates.Split(['\n', ';', ','], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        var known = p?.TermbasesFor(CompareTargetLanguage.Trim())
            .SelectMany(tb => tb.Recognize(CompareTerm, CompareSourceLanguage.Trim(), CompareTargetLanguage.Trim())).ToList();
        var query = new TermQuery(CompareTerm.Trim(), CompareSourceLanguage.Trim(), CompareTargetLanguage.Trim(),
            CompareContext.Trim().Length > 0 ? CompareContext.Trim() : null, Topic, Profile, hints, known);
        _comparison = await new TermComparisonAgent(Context(AppServices.Ai.ResearchProviderId)).RunAsync(query, Math.Clamp(PagesPerLanguage, 1, 6), ct);
        CompareResult = TermComparisonAgent.Format(_comparison);
        HasCompareResult = true;
    });

    [RelayCommand]
    private void SaveCompareToTermbase()
    {
        var p = _main.Project;
        if (p == null || _comparison == null) { Log += "Kein Projekt geöffnet.\n"; return; }
        var tb = p.SharedTermbasesToWrite(CompareTargetLanguage.Trim()).FirstOrDefault() ?? p.ProjectTermbase;
        TermComparisonAgent.SaveToTermbase(_comparison, tb, CompareSourceLanguage.Trim(), CompareTargetLanguage.Trim(), Topic);
        Log += $"In „{tb.Name}“ übernommen.\n";
        _main.Resources.Refresh();
    }

    [RelayCommand]
    private void SaveCompareToKnowledge()
    {
        var p = _main.Project;
        if (p == null || _comparison == null) { Log += "Kein Projekt geöffnet.\n"; return; }
        var kb = p.KnowledgeFor(CompareTargetLanguage.Trim()).FirstOrDefault(k => k.IsRemote) ?? p.ProjectKnowledge;
        TermComparisonAgent.SaveToKnowledge(_comparison, kb, CompareTargetLanguage.Trim(), Topic, Environment.UserName);
        Log += $"Als Wissen in „{kb.Name}“ gespeichert.\n";
    }

    // ---------- Usus ----------

    [RelayCommand]
    private Task CheckUsage() => Run(async ct =>
    {
        if (UsageExpression.Trim().Length == 0) return;
        var alts = UsageAlternatives.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).ToList();
        var r = await new UsageCheckAgent(Context(AppServices.Ai.ResearchProviderId))
            .RunAsync(UsageExpression, UsageLanguage.Trim(), Topic, alts, suggestAlternatives: alts.Count == 0, ct: ct);
        UsageResult = FormatUsage(r);
    });

    public static string FormatUsage(UsageResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"„{r.Expression}“ ({r.Language}): {UsageCheckAgent.VerdictName(r.Verdict).ToUpperInvariant()}");
        if (!string.IsNullOrWhiteSpace(r.Recommended) && r.Recommended != r.Expression) sb.AppendLine($"Empfehlung: „{r.Recommended}“");
        sb.AppendLine(r.Explanation);
        sb.AppendLine();
        foreach (var e in r.Evidence)
        {
            sb.AppendLine($"• „{e.Variant}“: {e.ExactHits} von {e.ResultsChecked} Suchtreffern wörtlich");
            foreach (var ex in e.Examples.Take(3)) sb.AppendLine($"    {ex.Snippet}\n    {ex.Url}");
        }
        return sb.ToString();
    }

    // ---------- Bewertung ----------

    [RelayCommand]
    private Task Evaluate() => Run(async ct =>
    {
        var lines = EvalCandidates.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (EvalSource.Trim().Length == 0 || lines.Count == 0) { Log = "Bitte Quelltext und mindestens eine Übersetzung angeben (eine pro Zeile).\n"; return; }
        var candidates = lines.Select((l, i) => new TranslationCandidate($"V{i + 1}", l)).ToList();
        var p = _main.Project;
        var terms = p?.TermbasesFor(EvalTargetLanguage).SelectMany(tb => tb.Recognize(EvalSource, p.SourceLanguage, EvalTargetLanguage)).ToList();
        var ctx = Context(AppServices.Ai.EvaluateProviderId);
        var r = await new EvaluationAgent(ctx).RunAsync(EvalSource, p?.SourceLanguage ?? SourceLanguage, EvalTargetLanguage.Trim(),
            candidates, Topic, terms, EvalCheckUsage && ctx.Search != null, ct);
        EvalResult = FormatEvaluation(r);
    });

    public static string FormatEvaluation(EvaluationResult r)
    {
        var sb = new StringBuilder();
        foreach (var s in r.Scores)
        {
            sb.AppendLine($"[{s.Label}] {s.Score}/100{(s.Label == r.BestLabel ? "  ★ beste" : "")}");
            sb.AppendLine($"    {s.Text}");
            sb.AppendLine($"    Richtigkeit {s.Accuracy} · Sprachfluss {s.Fluency} · Terminologie {s.Terminology} · Stil {s.Style}");
            if (s.Comment.Length > 0) sb.AppendLine($"    {s.Comment}");
            foreach (var i in s.Issues) sb.AppendLine($"    – {i}");
            sb.AppendLine();
        }
        if (r.Suggestion != null) sb.AppendLine($"Verbesserungsvorschlag:\n    {r.Suggestion}\n");
        if (r.Summary.Length > 0) sb.AppendLine(r.Summary);
        foreach (var u in r.UsageChecks)
        {
            sb.AppendLine();
            sb.Append(FormatUsage(u));
        }
        return sb.ToString();
    }
}
