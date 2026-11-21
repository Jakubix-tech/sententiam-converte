using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Abschnitt der Erklärung einer Markierung: Überschrift und Text.</summary>
public sealed record MarkedSection(string Label, string Text);

/// <summary>
/// Markierung erklären: Wer im Reiter Recherche Text im Ausgangstext oder in der Übersetzung markiert, sieht oben in
/// „Dieses Segment“, was die Wörter im Satz bedeuten, ihre Grammatik und sprachwissenschaftliche Einordnung, die
/// entsprechende Stelle in der anderen Sprache mit Einschätzung und Alternativen – und was die Recherche dazu schon belegt
/// hat. Ohne Markierung bleibt die gewohnte Ansicht. Die Erklärung ist eine Einschätzung der KI ohne eigene Quellen;
/// „Mit Belegen recherchieren“ startet die Recherche dazu.
/// </summary>
public partial class EditorViewModel
{
    [ObservableProperty] public partial bool HasMarkedText { get; set; }
    [ObservableProperty] public partial bool IsExplainingMarked { get; set; }
    [ObservableProperty] public partial string MarkedHeader { get; set; } = "";
    [ObservableProperty] public partial string MarkedStatus { get; set; } = "";
    public ObservableCollection<MarkedSection> MarkedSections { get; } = new();

    /// <summary>Was „Mit Belegen recherchieren“ sucht: die Markierung im Ausgangstext bzw. die Gegenstelle einer Markierung in der Übersetzung.</summary>
    private string? _markedResearchTerm;
    private CancellationTokenSource? _markCts;
    private SegmentViewModel? _markedSegment;
    private readonly Dictionary<(int Id, bool InTarget, string Text, string Target), SelectionExplanation> _markCache = new();

    /// <summary>Wartezeit, bis die Markierung als fertig gilt (beim Ziehen mit der Maus ändert sie sich laufend).</summary>
    public static TimeSpan MarkDelay { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Für Tests: statt der KI eine Erklärung liefern (Markierung, in der Übersetzung?, Quelle, Übersetzung).</summary>
    public static Func<string, bool, string, string, CancellationToken, Task<SelectionExplanation>>? ExplainOverride { get; set; }

    /// <summary>
    /// Die Ansicht meldet eine Markierung in Quelle oder Ziel. Nur bei offenem Reiter Recherche; leere Markierung: zurück zur
    /// gewohnten Ansicht.
    /// </summary>
    public void MarkText(SegmentViewModel seg, bool inTarget, string text)
    {
        _markCts?.Cancel();
        text = TagText.StripTags(text).Trim();
        if (text.Length < 2 || !text.Any(char.IsLetter))
        {
            ClearMarked();
            return;
        }
        if (SidePanelTab != ResearchTab || _doc == null || Project == null) return;
        var cts = _markCts = new CancellationTokenSource();
        _ = ExplainMarkedAsync(seg, inTarget, text, cts.Token);
    }

    /// <summary>Gewohnte Ansicht: keine Erklärung einer Markierung.</summary>
    private void ClearMarked()
    {
        _markCts?.Cancel();
        _markedSegment = null;
        _markedResearchTerm = null;
        HasMarkedText = false;
        IsExplainingMarked = false;
        MarkedSections.Clear();
        MarkedHeader = MarkedStatus = "";
    }

    private async Task ExplainMarkedAsync(SegmentViewModel seg, bool inTarget, string text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(MarkDelay, ct);                              // erst, wenn die Markierung steht
            if (_doc == null || Project == null) return;
            var doc = _doc;
            var project = Project;
            var to = ResearchTo ?? doc.TargetLanguage;
            var from = ResearchFrom ?? doc.SourceLanguage;
            var target = seg.Model.Target ?? "";
            _markedSegment = seg;
            HasMarkedText = true;
            MarkedHeader = inTarget ? L.T($"Markierung in der Übersetzung (Segment {seg.Id}): „{text}“") : L.T($"Markierung im Ausgangstext (Segment {seg.Id}): „{text}“");
            MarkedSections.Clear();

            // was die Recherche zu diesen Wörtern schon hat: Ausdrücke, die in der Markierung stehen oder sie enthalten
            // (bei einer Markierung in der Übersetzung: deren empfohlene Entsprechung)
            bool Overlaps(string? a) => a is { Length: > 1 } && (DocumentResearch.Contains(text, a) || DocumentResearch.Contains(a, text));
            var research = _dossiers.Where(d => Languages.Matches(d.TargetLanguage, to) && (inTarget ? Overlaps(d.Recommended) : Overlaps(d.Expression)))
                .DistinctBy(d => d.Expression.ToLowerInvariant()).ToList();

            var key = (seg.Id, inTarget, text, target);
            if (!_markCache.TryGetValue(key, out var explanation))
            {
                IsExplainingMarked = true;
                MarkedStatus = L.T("Die KI erklärt die Markierung …");
                explanation = ExplainOverride != null ? await ExplainOverride(text, inTarget, seg.Source, target, ct)
                    : await new SelectionExplainer(CreateContext(AppServices.Ai.ResearchProviderId)).ExplainAsync(text, inTarget, seg.Source, target,
                        from, to, project.Topic, research, await Task.Run(() => Workflow.Terms(project, doc, seg.Model), ct), ct);
                _markCache[key] = explanation;
            }
            if (ct.IsCancellationRequested) return;

            void Add(string label, string? content)
            {
                if (!string.IsNullOrWhiteSpace(content)) MarkedSections.Add(new MarkedSection(L.T(label), content.Trim()));
            }
            Add("Bedeutung im Satz", explanation.Meaning);
            Add("Grammatik", explanation.Grammar);
            Add("Sprachwissenschaftlich", explanation.Linguistics);
            Add(inTarget ? "Im Ausgangstext" : "In der Übersetzung", explanation.Counterpart is { } c ? $"„{c}“" : null);
            Add("Einschätzung", explanation.Assessment);
            Add("Alternativen", explanation.Alternatives.Count > 0 ? string.Join("\n", explanation.Alternatives.Select(a => "• " + a)) : null);
            Add("Aus der Recherche", research.Count > 0 ? string.Join("\n", research.Select(d =>
                $"• {d.Expression} → {d.Recommended ?? "?"} (" + L.T(DossierStore.GradeText(d)) + ")" +
                (string.IsNullOrWhiteSpace(d.Meaning) ? "" : "\n  " + AiTranslator.Short(d.Meaning, 240)) +
                (d.Sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Quote)) is { } s ? "\n  " + L.T("Quelle") + $": {s.Title} – {Core.Ai.SourceLink.WithTextFragment(s.Url, s.Quote)}" : ""))) : null);
            _markedResearchTerm = inTarget ? explanation.Counterpart : text;
            MarkedStatus = L.T("Einschätzung der KI ohne eigene Quellen – „Mit Belegen recherchieren“ sucht Belege im Netz.");
            OnPropertyChanged(nameof(CanResearchMarked));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) MarkedStatus = L.T("Erklärung nicht möglich: ") + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsExplainingMarked = false;
        }
    }

    public bool CanResearchMarked => _markedResearchTerm is { Length: > 1 };

    /// <summary>Die Markierung (bzw. ihre Gegenstelle im Ausgangstext) mit Belegen recherchieren – wie Strg+Umschalt+R.</summary>
    [RelayCommand]
    private Task ResearchMarked() => _markedResearchTerm is { Length: > 1 } term ? ShowOrResearchAsync(term) : Task.CompletedTask;

    /// <summary>Anderes Segment gewählt: die Erklärung gehört zum vorigen.</summary>
    private void ClearMarkedFor(SegmentViewModel? selected)
    {
        if (_markedSegment != null && _markedSegment != selected) ClearMarked();
    }
}
