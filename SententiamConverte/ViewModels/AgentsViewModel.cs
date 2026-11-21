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

/// <summary>
/// Bereich „Agenten“: Recherche, die die KI selbst steuert (mit Skills und Gedächtnis), Begriffsvergleich,
/// Themenrecherche, Usus-Prüfung, Bewertung von Übersetzungen.
/// </summary>
public partial class AgentsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public AgentsViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Log { get; set; } = "";

    // KI-gesteuerte Recherche
    [ObservableProperty] public partial string AgentTask { get; set; } = "";
    [ObservableProperty] public partial string AgentSourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string AgentTargetLanguages { get; set; } = "en-GB";
    [ObservableProperty] public partial int AgentMaxSteps { get; set; } = 10;
    [ObservableProperty] public partial bool AgentLearn { get; set; } = true;
    [ObservableProperty] public partial bool AgentRemember { get; set; } = true;
    [ObservableProperty] public partial string AgentAnswer { get; set; } = "";
    [ObservableProperty] public partial string AgentTranslation { get; set; } = "";
    [ObservableProperty] public partial string AgentNote { get; set; } = "";
    [ObservableProperty] public partial string AgentEvidence { get; set; } = "";
    [ObservableProperty] public partial string AgentInfo { get; set; } = "";
    public ObservableCollection<SourceRef> AgentSources { get; } = new();
    public ObservableCollection<ResearchStep> AgentSteps { get; } = new();

    // Skills und Gedächtnis
    private readonly Lazy<SkillLibrary> _skills = new(() => new SkillLibrary());
    public ObservableCollection<Skill> SkillList { get; } = new();
    [ObservableProperty] public partial Skill? SelectedSkill { get; set; }
    [ObservableProperty] public partial string SkillName { get; set; } = "";
    [ObservableProperty] public partial string SkillDescription { get; set; } = "";
    [ObservableProperty] public partial string SkillInstructions { get; set; } = "";
    public string SkillFolder => _skills.Value.Folder;
    public string MemoryInfo => _main.Project is { } p
        ? $"Gedächtnis: Projekt-Wissen ({p.ProjectKnowledge.Count()} Einträge) – dazu durchsucht die KI Projekt-TM, Terminologie und alle angebundenen Datenbanken."
        : "Gedächtnis: allgemeines KI-Gedächtnis (ohne geöffnetes Projekt).";

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
        OnPropertyChanged(nameof(MemoryInfo));
        var p = _main.Project;
        if (p == null) return;
        AgentSourceLanguage = p.SourceLanguage;
        AgentTargetLanguages = string.Join(", ", p.TargetLanguages);
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
        // Läuft weiter, wenn man den Reiter wechselt – die Statusleiste sagt, wann das Ergebnis da ist
        _main.Status = "Agent arbeitet – läuft weiter, auch wenn Sie den Reiter wechseln.";
        try
        {
            await action(_cts.Token);
            _main.Status = "Agent fertig – Ergebnis im Reiter „Agenten“.";
        }
        catch (OperationCanceledException)
        {
            Log += "Abgebrochen.\n";
            _main.Status = "Agent abgebrochen.";
        }
        catch (Exception ex)
        {
            Log += "Fehler: " + ex.Message + "\n";
            _main.Status = "Agent: Fehler – siehe Protokoll im Reiter „Agenten“.";
        }
        finally { IsRunning = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ---------- KI-gesteuerte Recherche ----------

    /// <summary>Allgemeines Gedächtnis, wenn kein Projekt geöffnet ist.</summary>
    public static string GlobalMemoryPath => Core.Resources.AgentResources.GlobalMemoryPath;

    /// <summary>
    /// Eigene Verbindungen zu den Datenbanken: mit Projekt dessen Datenbanken (samt sprachbezogenen), ohne Projekt das
    /// allgemeine KI-Gedächtnis und die sprachbezogenen Datenbanken der gewählten Sprachen. Der Agent läuft damit
    /// weiter, auch wenn man inzwischen ein anderes Projekt öffnet.
    /// </summary>
    private Core.Resources.AgentResources OpenResources(string source, IReadOnlyList<string> targets) =>
        _main.Project is { } p ? p.OpenForAgent() : Core.Resources.AgentResources.ForLanguages(source, targets, AppServices.Servers);

    private static KnowledgeMemory MemoryOf(Core.Resources.AgentResources res, string source, IReadOnlyList<string> targets) =>
        new(res.Knowledge[0], res.Knowledge, res.Termbases, res.Tms, source, targets);

    // ---------- In den Datenbanken nachsehen ----------

    [ObservableProperty] public partial string DbQuery { get; set; } = "";
    [ObservableProperty] public partial string DbResult { get; set; } = "";

    /// <summary>Sucht wie der Agent in TM, Terminologie, Wissen und Gedächtnis – z. B. ob es eine Formulierung schon gibt.</summary>
    [RelayCommand]
    private void SearchDatabases()
    {
        if (DbQuery.Trim().Length == 0) { DbResult = "Bitte einen Begriff oder eine Formulierung eingeben."; return; }
        var targets = AgentTargetLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        using var res = OpenResources(AgentSourceLanguage.Trim(), targets);
        var found = MemoryOf(res, AgentSourceLanguage.Trim(), targets).Search(DbQuery.Trim(), AgentSourceLanguage.Trim());
        DbResult = found.Length > 0 ? found : $"Nichts gefunden in {res.Knowledge.Count + res.Termbases.Count + res.Tms.Count} Datenbanken.";
    }

    /// <summary>Zeigt das Gedächtnis (Projekt-Wissen bzw. allgemeines KI-Gedächtnis) im Reiter „Wissen“.</summary>
    [RelayCommand]
    private void ShowMemory()
    {
        _main.Knowledge.ShowMemory();
        _main.SelectedTab = MainViewModel.KnowledgeTab;
    }

    [RelayCommand]
    private Task RunAgent() => Run(async ct =>
    {
        if (AgentTask.Trim().Length == 0) { Log = "Bitte eine Aufgabe oder Frage eingeben.\n"; return; }
        var targets = AgentTargetLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        if (targets.Count == 0) targets = [AgentSourceLanguage];
        AgentAnswer = "";
        AgentTranslation = "";
        AgentNote = "";
        AgentEvidence = "";
        AgentInfo = "";
        AgentSources.Clear();
        AgentSteps.Clear();
        var ctx = Context(AppServices.Ai.ResearchProviderId);
        var source = AgentSourceLanguage.Trim();
        using var resources = OpenResources(source, targets);
        {
            var memory = MemoryOf(resources, source, targets);
            Log += $"Modell: {ctx.Model.DisplayName} · Websuche: {ctx.Search?.Name ?? "keine (nur Wikipedia/Wiktionary)"}\n";
            var result = await new ResearchAgent(ctx, _skills.Value, memory)
                .RunAsync(AgentTask.Trim(), source, targets, Math.Clamp(AgentMaxSteps, 2, 30), AgentLearn, AgentRemember, ct);
            AgentAnswer = result.Answer;
            AgentTranslation = result.Translation ?? "";
            AgentNote = result.Note ?? "";
            AgentEvidence = string.Join("\n", (result.Evidence ?? []).Select(e => e.ToString()));
            foreach (var s in result.Sources) AgentSources.Add(s);
            foreach (var s in result.Steps) AgentSteps.Add(s);
            AgentInfo = $"{result.Steps.Count} Schritte" + (result.LearnedSkill != null ? $" · neuer Skill gelernt: {result.LearnedSkill.Name}" : "");
            if (result.LearnedSkill != null) RefreshSkills();
            OnPropertyChanged(nameof(MemoryInfo));
        }
    });

    // ---------- Skills ----------

    [RelayCommand]
    private void RefreshSkills()
    {
        var selected = SelectedSkill?.Name;
        SkillList.Clear();
        foreach (var s in _skills.Value.All()) SkillList.Add(s);
        SelectedSkill = SkillList.FirstOrDefault(s => s.Name == selected) ?? SkillList.FirstOrDefault();
    }

    partial void OnSelectedSkillChanged(Skill? value)
    {
        SkillName = value?.Name ?? "";
        SkillDescription = value?.Description ?? "";
        SkillInstructions = value?.Instructions ?? "";
    }

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        SkillName = "neuer-skill";
        SkillDescription = "Wofür dieser Skill gedacht ist (ein Satz).";
        SkillInstructions = "1. …\n2. …";
    }

    [RelayCommand]
    private void SaveSkill()
    {
        var name = SkillLibrary.NormalizeName(SkillName);
        if (name.Length < 3) { Log = "Bitte einen Namen mit mindestens drei Zeichen angeben.\n"; return; }
        var existing = _skills.Value.Get(name);
        // Eigene Änderungen gelten wie gelernte: mitgelieferte Skills bleiben als Vorlage erhalten, wenn man umbenennt
        _skills.Value.Save(new Skill(name, SkillDescription.Trim(), SkillInstructions.Trim(), existing?.Learned ?? true, existing?.Uses ?? 0,
            Version: existing?.Version ?? 0, Edited: existing is { Learned: false }));
        if (SelectedSkill != null && SkillLibrary.NormalizeName(SelectedSkill.Name) != name && SelectedSkill.Learned) _skills.Value.Delete(SelectedSkill.Name);
        RefreshSkills();
        SelectedSkill = SkillList.FirstOrDefault(s => s.Name == name);
        _main.Status = $"Skill „{name}“ gespeichert.";
    }

    [RelayCommand]
    private void DeleteSkill()
    {
        if (SelectedSkill is not { } s) return;
        _skills.Value.Delete(s.Name);
        RefreshSkills();
        _main.Status = s.Learned ? $"Skill „{s.Name}“ gelöscht." : $"Skill „{s.Name}“ gelöscht (mitgelieferte Skills kommen beim nächsten Start wieder).";
    }

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
        // zuerst die klare Aussage, dann jede Angabe mit Beleg und Link zum Nachprüfen
        var sb = new StringBuilder();
        sb.AppendLine($"„{r.Expression}“ ({r.Language}): {UsageCheckAgent.VerdictName(r.Verdict).ToUpperInvariant()}");
        sb.AppendLine(r.Statement);
        if (!string.IsNullOrWhiteSpace(r.Recommended) && r.Recommended != r.Expression) sb.AppendLine($"Empfehlung: „{r.Recommended}“");
        sb.AppendLine(r.Explanation);
        sb.AppendLine();
        foreach (var e in r.Evidence)
        {
            sb.AppendLine($"• „{e.Variant}“: {e.ExactHits} von {e.ResultsChecked} Suchtreffern wörtlich");
            if (e.Queries.Count > 1) sb.AppendLine($"    Gesucht: {string.Join(" · ", e.Queries)}");
            foreach (var ex in e.Examples)
            {
                sb.AppendLine(ex.Verified ? $"    [{ex.Number}] „{ex.Sentence}“" : $"    [{ex.Number}] Ausschnitt aus der Trefferliste: {ex.Snippet}");
                sb.AppendLine($"        {ex.Link}");
            }
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
