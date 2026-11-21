using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Notiz unter der Antwort: woher, was, Quelle – zitierte hervorgehoben.</summary>
public sealed class KnowledgeNoteRow(KnowledgeNote note, bool cited)
{
    public KnowledgeNote Note { get; } = note;
    public string Header => $"[{Note.Number}] {L.T(Note.Origin)}" + (Note.Language is { Length: > 0 } l ? $" · {l}" : "") + $" – {Note.Title}";
    public string Text => Note.Text;
    public string? Quote => Note.Quote is { Length: > 0 } q ? $"„{q}“" : null;
    public bool HasQuote => Quote != null;
    public string Source => Note.SourceTitle is { Length: > 0 } t && Note.SourceUrl is { Length: > 0 } u && t != u ? $"{t} – {u}"
        : Note.SourceUrl ?? (Note.SourceTitle is { Length: > 0 } title ? title + " – " + L.T("ohne Link, nicht nachprüfbar") : L.T("ohne Quelle – nicht nachprüfbar"));
    public bool HasSource => !string.IsNullOrEmpty(Note.SourceUrl);
    public bool Cited { get; } = cited;
    public double Opacity => Cited ? 1 : 0.55;
}

/// <summary>
/// Fragen an das Wissen: Die KI antwortet nur aus den Einträgen der Wissensdatenbanken und den eigenen Fachtexten, mit
/// Quellen [n]. Fehlt etwas, recherchiert „Nachrecherchieren“ genau das und fragt danach noch einmal.
/// </summary>
public partial class KnowledgeViewModel
{
    private CancellationTokenSource? _askCts;
    private KnowledgeAnswer? _lastAnswer;

    [ObservableProperty] public partial string Question { get; set; } = "";
    [ObservableProperty] public partial bool IsAsking { get; set; }
    [ObservableProperty] public partial string AskStatus { get; set; } = "";
    [ObservableProperty] public partial string AnswerText { get; set; } = "";
    [ObservableProperty] public partial bool HasAnswer { get; set; }
    [ObservableProperty] public partial string MissingText { get; set; } = "";
    [ObservableProperty] public partial bool HasMissing { get; set; }

    /// <summary>Nur in der oben gewählten Wissensdatenbank suchen (sonst in allen angezeigten).</summary>
    [ObservableProperty] public partial bool AskOnlySelected { get; set; }

    /// <summary>Fehlt etwas im Wissen, das sagen und gleich im Netz nachrecherchieren (in die oben gewählte Datenbank).</summary>
    [ObservableProperty] public partial bool AutoResearchMissing { get; set; } = true;

    private bool _autoResearching;

    public ObservableCollection<KnowledgeNoteRow> AnswerNotes { get; } = new();

    [RelayCommand]
    private async Task AskKnowledge()
    {
        if (IsAsking || Question.Trim().Length == 0) return;
        if (Bases.Count == 0) OnProjectChanged();
        IsAsking = true;
        var cts = _askCts = new CancellationTokenSource();
        AskStatus = L.T("Suche im Wissen …");
        var autoSearch = false;
        try
        {
            var ctx = AgentContext.From(AppServices.Ai, AppServices.Ai.ResearchProviderId, new Progress<string>(m => AskStatus = m));
            IReadOnlyList<IKnowledgeBase> bases = AskOnlySelected && SelectedBase != null ? [SelectedBase] : Bases.ToList();
            var topic = _main.Project?.Topic is { Length: > 0 } t ? t : AgentTopic;
            var answer = _lastAnswer = await new KnowledgeQa(ctx).AskAsync(Question, bases, topic, ct: cts.Token);
            AnswerText = L.T(answer.Answer);
            HasAnswer = true;
            // klar sagen, wozu es noch nichts gibt – und dass gesucht wird
            autoSearch = answer.NotYetKnown != null && AutoResearchMissing && !_autoResearching && SelectedBase != null;
            MissingText = answer.NotYetKnown is { } notYet
                ? L.T(notYet) + " " + L.T(autoSearch ? "Ich suche jetzt danach …" : "„Nachrecherchieren“ sucht danach.")
                : answer.Missing is { Length: > 0 } m && m != answer.Question ? L.T("Fehlt im Wissen: ") + m : "";
            HasMissing = MissingText.Length > 0;
            AnswerNotes.Clear();
            foreach (var n in answer.Notes.OrderByDescending(n => answer.Cited.Contains(n.Number)).ThenBy(n => n.Number))
                AnswerNotes.Add(new KnowledgeNoteRow(n, answer.Cited.Contains(n.Number)));
            AskStatus = answer.Notes.Count == 0 ? L.T("Im Wissen steht dazu nichts – „Nachrecherchieren“ sucht es im Netz und legt es ab.")
                : answer.Found ? L.T($"Antwort aus {answer.Cited.Count} von {answer.Notes.Count} Notizen.")
                : L.T("Das Wissen beantwortet die Frage nicht vollständig – „Nachrecherchieren“ sucht das Fehlende.");
            // jede Angabe soll eine Quelle zum Nachprüfen haben
            if (answer.UncitedStatements > 0)
                AskStatus += " " + L.T($"Achtung: {answer.UncitedStatements} Aussagen ohne Quellenangabe – nicht aus dem Wissen belegt.");
            if (answer.CitedWithoutSource.Count > 0)
                AskStatus += " " + L.T($"Ohne Link, nicht nachprüfbar: {string.Join(", ", answer.CitedWithoutSource.Select(n => $"[{n}]"))}.");
        }
        catch (OperationCanceledException) { AskStatus = L.T("Abgebrochen."); }
        catch (AiException ex) { AskStatus = "KI: " + ex.Message; }
        finally { IsAsking = false; }

        if (!autoSearch) return;
        _autoResearching = true;
        try { await ResearchAndAskAgain(); }
        finally { _autoResearching = false; }
        if (_lastAnswer?.NotYetKnown is { } still)
        {
            MissingText = L.T("Auch nach der Suche habe ich dazu keine belegten Informationen gefunden:") + " " + L.T(still);
            HasMissing = true;
        }
    }

    [RelayCommand]
    private void CancelAsk() => _askCts?.Cancel();

    /// <summary>Das Fehlende mit dem Wissens-Agenten recherchieren (in die oben gewählte Datenbank) und danach neu fragen.</summary>
    [RelayCommand]
    private async Task ResearchAndAskAgain()
    {
        if (IsAsking || IsRunning || Question.Trim().Length == 0) return;
        if (SelectedBase == null)
        {
            AskStatus = L.T("Bitte oben eine Wissensdatenbank wählen, in die die Recherche schreibt.");
            return;
        }
        if (AgentTopic.Trim().Length == 0) AgentTopic = Question.Trim();
        AgentFocus = _lastAnswer?.UnknownTerms is { Count: > 0 } unknown ? string.Join(", ", unknown)
            : _lastAnswer?.Missing is { Length: > 0 } missing ? missing : Question.Trim();
        AskStatus = L.T($"Recherchiere „{AgentFocus}“ für „{SelectedBase.Name}“ …");
        await RunAgentCommand.ExecuteAsync(null);
        await AskKnowledge();
    }

    [RelayCommand]
    private async Task OpenNoteSource(KnowledgeNoteRow? row)
    {
        if (row?.Note.SourceUrl is { Length: > 0 } url && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(url);
    }
}
