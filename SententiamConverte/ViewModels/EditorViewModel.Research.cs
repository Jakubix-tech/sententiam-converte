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
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein gefundener Ausdruck zum Anhaken.</summary>
public sealed partial class ResearchItemRow(ResearchItem item) : ObservableObject
{
    public ResearchItem Item { get; } = item;
    // leichte Ausdrücke nicht vorab anhaken – die Liste ist jetzt vollständig und soll nicht stundenlang recherchieren
    [ObservableProperty] public partial bool IsChecked { get; set; } = item.Difficulty != ResearchDifficulty.Easy || item.Category == EditorViewModel.OwnCategory;
    public string Expression => Item.Expression;
    public string Info => IsOwn
        ? $"{EditorViewModel.CategoryName(Item.Category)} – {Item.Reason}"                // noch nicht eingeschätzt
        : $"{EditorViewModel.CategoryName(Item.Category)} · {EditorViewModel.DifficultyName(Item.Difficulty)} – {L.T(Item.Reason)}";

    /// <summary>Vom Übersetzer selbst hinzugefügt (bleibt beim erneuten Durchsehen stehen).</summary>
    public bool IsOwn => Item.Category == EditorViewModel.OwnCategory;
}

/// <summary>Ein Beleg in der Anzeige: Nummer, Sprache, Seite, Adresse und das wörtliche Zitat.</summary>
public sealed class DossierSourceRow(Core.Agents.SourceRef source, int number, string? label = null)
{
    public Core.Agents.SourceRef Source { get; } = source;
    public string Label => label ?? $"[{number}] {Source.Language} · {Source.Title} ({Host})";
    public string Host => Uri.TryCreate(Source.Url, UriKind.Absolute, out var u) ? u.IsFile ? L.T("eigene Fachtexte") : u.Host : Source.Url;

    /// <summary>Stand der Quelle und wann sie gelesen wurde – Inhalte im Netz und Gesetze ändern sich.</summary>
    public string Dated => string.Join(" · ", new[]
    {
        Source.Version is { } v ? L.T(v) : null,
        Source.Retrieved is { } r ? L.T($"abgerufen am {r.ToLocalTime():dd.MM.yyyy}") : null,
    }.Where(x => !string.IsNullOrEmpty(x)));

    public bool HasDated => Dated.Length > 0;
    public string Url => Source.Url;
    public string Quote => Source.Quote is { Length: > 0 } q ? $"„{q}“" + (Source.Term is { Length: > 0 } t ? $" – {t}" : "") : "";
    public bool HasQuote => Quote.Length > 0;
}

/// <summary>Ein Begriff des Begriffsfelds in der Anzeige: Benennung → Entsprechung, Merkmale, ob belegt; der gemeinte hervorgehoben.</summary>
public sealed class ConceptMemberRow(ConceptFieldMember member, bool meant)
{
    public ConceptFieldMember Member { get; } = member;
    public bool Meant { get; } = meant;
    public string Terms => $"{Member.SourceTerm}  →  {Member.TargetTerm ?? "?"}" +
                           (Member.TargetAlternatives.Count > 0 ? $"  ({L.T("auch")}: {string.Join(", ", Member.TargetAlternatives.Take(3))})" : "");
    public string Features => string.Join("; ", Member.Features);
    public string Evidence => Member.TargetEvidence > 0
        ? L.T($"belegt in {Member.TargetEvidence} Quellen") + (Member.LinkedByWikipedia ? " · " + L.T("Wikipedia verknüpft") : "")
        : L.T("nicht belegt");
    public string Marker => Meant ? "▶" : Member.IsQueried ? "•" : "";
    public Avalonia.Media.FontWeight Weight => Meant ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
}

/// <summary>Ein Dossier in der Anzeige.</summary>
public sealed class DossierRow(TermDossier dossier, EditorViewModel owner, bool expanded = true)
{
    public TermDossier Dossier { get; } = dossier;
    public EditorViewModel Owner { get; } = owner;

    /// <summary>Aufgeklappt (beim Klick auf ein Segment) oder nur die Kopfzeile (in der Liste aller Dossiers).</summary>
    public bool IsExpanded { get; set; } = expanded;

    public bool HasPrevious => Dossier.Previous != null;
    public bool HasOrigin => Dossier.Origin != null;
    public string GradeText => L.T(DossierStore.GradeText(Dossier));
    public string OriginText => Dossier.Origin is { } o ? L.T($"Aus dem Recherche-Gedächtnis: {o}") : "";
    public string PreviousInfo => Dossier.Previous is { } p
        ? L.T($"Frühere Fassung vom {p.Created.ToLocalTime():dd.MM.yyyy HH:mm}: {p.Recommended ?? "?"}") : "";
    public string Title => $"{Dossier.Expression}  →  {Dossier.Recommended ?? "?"}";
    public string Languages => $"{LanguageNames(Dossier.SourceLanguage)} → {LanguageNames(Dossier.TargetLanguage)}";
    public string Difficulty => EditorViewModel.DifficultyName(Dossier.Difficulty);
    public string DifficultyColor => Dossier.Difficulty switch
    {
        ResearchDifficulty.Hard => "#B91C1C",
        ResearchDifficulty.Medium => "#B45309",
        _ => "#15803D",
    };

    public string Text
    {
        get
        {
            var d = Dossier;
            var lines = new List<string>
            {
                L.T("Bedeutung") + ": " + d.Meaning,
            };
            if (d.KeyFeatures.Count > 0) lines.Add(L.T("Merkmale") + ": " + string.Join("; ", d.KeyFeatures));
            foreach (var c in d.Candidates)
                lines.Add($"• {c.Term} ({L.T(c.Equivalence switch { Equivalence.Full => "volle Entsprechung", Equivalence.Partial => "teilweise", _ => "keine Entsprechung" })})" +
                          (c.Evidence.Count > 0 ? " [" + string.Join(", ", c.Evidence) + "]" : d.Sources.Count > 0 ? " – " + L.T("ohne Beleg") : "") +
                          (c.SuitableWhen is { Length: > 0 } w ? " – " + L.T("passt") + ": " + w : "") +
                          (c.UnsuitableWhen is { Length: > 0 } u ? " – " + L.T("nicht") + ": " + u : ""));
            if (d.Strategy is { Length: > 0 }) lines.Add(L.T("Empfehlung") + $": {d.Recommended} – {d.Strategy}" + (d.Rendering is { Length: > 0 } r ? $" („{r}“)" : ""));
            if (d.Reasoning is { Length: > 0 }) lines.Add(d.Reasoning);
            foreach (var w in d.Warnings) lines.Add("⚠ " + w);
            lines.Add(L.T("Schwierigkeit") + ": " + EditorViewModel.DifficultyName(d.Difficulty) + " – " +
                      string.Join(" ", new[] { L.Lines(d.DifficultyNote), d.DifficultyReason }.Where(s => s.Length > 0)));
            return string.Join("\n", lines);
        }
    }

    // ---------- Begriffsfeld ----------

    public bool HasConceptField => Dossier.ConceptField is { Members.Count: > 0 };

    public string ConceptFieldHeader => Dossier.ConceptField is { } f
        ? L.T("Begriffsfeld") + $": {f.Umbrella}" + (f.UmbrellaTarget is { Length: > 0 } t ? $" → {t}" : "") : "";

    public List<ConceptMemberRow> ConceptMembers => Dossier.ConceptField is { } f
        ? f.Members.Select(m => new ConceptMemberRow(m, m == f.MeantMember)).ToList() : [];

    public string ConceptHowToTell => Dossier.ConceptField is { HowToTell.Count: > 0 } f ? string.Join("\n", f.HowToTell.Select(h => "• " + h)) : "";
    public bool HasConceptHowToTell => ConceptHowToTell.Length > 0;
    public string ConceptForThisContext => Dossier.ConceptField?.ForThisContext ?? "";
    public bool HasConceptForThisContext => ConceptForThisContext.Length > 0;
    public string ConceptAskTheClient => Dossier.ConceptField?.AskTheClient ?? "";
    public bool HasConceptAskTheClient => ConceptAskTheClient.Length > 0;

    public List<DossierSourceRow> Sources => Dossier.Sources.Select((s, i) => new DossierSourceRow(s, s.Number > 0 ? s.Number : i + 1)).ToList();
    public bool HasSources => Dossier.Sources.Count > 0;

    /// <summary>Fundorte, die die KI für diese Art Information nennt (öffnen die Startseite).</summary>
    public List<DossierSourceRow> WhereToLook => Dossier.WhereToLook
        .Select((h, i) => new DossierSourceRow(new Core.Agents.SourceRef(h.Language, h.What, "https://" + h.Domain), i + 1, $"{h.Domain} – {h.What}")).ToList();
    public bool HasWhereToLook => Dossier.WhereToLook.Count > 0;
    public string SourcesHeader => L.T($"Belege ({Dossier.Sources.Count}) – Klick öffnet die Seite an der zitierten Stelle");
    public bool HasRendering => !string.IsNullOrWhiteSpace(Dossier.Rendering);

    /// <summary>Ob die Übersetzung die Empfehlung von selbst bekommt (nur, wenn eine Quelle der Zielsprache sie wörtlich enthält).</summary>
    public bool IsEvidenced => DossierStore.IsEvidenced(Dossier);

    public string EvidenceNote => string.IsNullOrWhiteSpace(Dossier.Recommended) ? ""
        : IsEvidenced ? L.T("✓ Empfehlung in einer Quelle der Zielsprache belegt – die KI-Übersetzung bekommt sie mit.")
        : L.T("Empfehlung in keiner Quelle der Zielsprache belegt – geht nicht von selbst in die KI-Übersetzung. Wenn sie stimmt: → Terminologie.");

    public bool HasEvidenceNote => EvidenceNote.Length > 0;

    /// <summary>Grün für belegte, Bernstein für unbelegte Empfehlungen (wie der Hinweis „keine Belege“).</summary>
    public static Avalonia.Data.Converters.IValueConverter EvidenceBrush { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush>(ok => Avalonia.Media.Brush.Parse(ok ? "#15803D" : "#B45309"));

    /// <summary>Womit das Dossier entstand: KI (im Verbund die Modelle, die geantwortet haben), Websuche, Zeitpunkt.</summary>
    public string Provenance => (Dossier.Model is { Length: > 0 } model
        ? L.T($"Erstellt mit: {model} · Suche: {L.T(Dossier.Search ?? "keine Websuche")} · {Dossier.Created.ToLocalTime():dd.MM.yyyy HH:mm}")
        : L.T($"Erstellt am {Dossier.Created.ToLocalTime():dd.MM.yyyy HH:mm} (älteres Dossier – Modell nicht festgehalten)"))
        + (Dossier.Rounds > 0 ? " · " + L.T($"{Dossier.Rounds} Suchrunden, {Dossier.PagesRead} Seiten gelesen") : "")
        + (Dossier.Conclusive switch { true => ", " + L.T("eindeutig belegt"), false => ", " + L.T("nicht eindeutig belegt"), _ => "" });

    private static string LanguageNames(string code)
    {
        var name = Controls.LanguageOption.For(code).Name;
        return code.Contains('-') ? $"{name}" : name;
    }
}

/// <summary>
/// Recherche zum Dokument im Editor: Ausdrücke finden, Dossiers erstellen (Sprache und Land auf beiden Seiten) und beim
/// Klicken auf ein Segment oder eine Markierung das passende Wissen zeigen.
/// </summary>
public partial class EditorViewModel
{
    private List<TermDossier> _dossiers = new();
    private CancellationTokenSource? _researchCts;

    /// <summary>Ausgangssprache mit Land (Vorgabe: die des Dokuments).</summary>
    [ObservableProperty] public partial string? ResearchFrom { get; set; }

    /// <summary>Zielsprache mit Land (Vorgabe: die des Dokuments) – z. B. es-MX statt es-ES.</summary>
    [ObservableProperty] public partial string? ResearchTo { get; set; }

    [ObservableProperty] public partial bool IsResearching { get; set; }
    [ObservableProperty] public partial string ResearchStatus { get; set; } = "";

    /// <summary>Alle Dossiers zeigen statt nur die zum gewählten Segment.</summary>
    [ObservableProperty] public partial bool ShowAllDossiers { get; set; }

    public ObservableCollection<ResearchItemRow> ResearchItems { get; } = new();
    public ObservableCollection<DossierRow> Dossiers { get; } = new();
    public bool HasResearchItems => ResearchItems.Count > 0;

    partial void OnShowAllDossiersChanged(bool value)
    {
        if (value && Project != null) _dossiers = DossierStore.Load(Project);    // auch, was inzwischen dazukam
        RefreshDossiers();
    }

    public static string DifficultyName(ResearchDifficulty d) => L.T(d switch
    {
        ResearchDifficulty.Hard => "schwierig",
        ResearchDifficulty.Medium => "mittel",
        _ => "leicht zu übertragen",
    });

    public static string CategoryName(string category) => L.T(category.ToLowerInvariant() switch
    {
        "term" => "Fachbegriff",
        "legal" => "Rechtsbegriff",
        "realia" => "Realie",
        "idiom" => "Redewendung",
        "abbreviation" => "Abkürzung",
        "institution" => "Einrichtung",
        OwnCategory => "Eigener Begriff",
        _ => "Ausdruck",
    });

    /// <summary>Kategorie der Ausdrücke, die der Übersetzer selbst einträgt.</summary>
    public const string OwnCategory = "own";

    /// <summary>Eingabefeld für eigene Begriffe (mehrere mit Semikolon oder je Zeile).</summary>
    [ObservableProperty] public partial string OwnResearchTerms { get; set; } = "";

    /// <summary>
    /// Eigene Begriffe in die Liste aufnehmen (angehakt, oben): auch Ausdrücke, die die KI nicht vorgeschlagen hat oder die
    /// gar nicht im Dokument stehen. Recherchiert wird mit „Angehakte recherchieren“, wie die gefundenen.
    /// </summary>
    [RelayCommand]
    private void AddOwnResearchTerms()
    {
        var terms = OwnResearchTerms.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (terms.Count == 0) return;
        var added = 0;
        foreach (var term in terms.AsEnumerable().Reverse())
        {
            if (ResearchItems.FirstOrDefault(r => r.Expression.Equals(term, StringComparison.CurrentCultureIgnoreCase)) is { } existing)
            {
                existing.IsChecked = true;
                continue;
            }
            var segments = _all.Where(s => DocumentResearch.Contains(TagText.StripTags(s.Source), term)).ToList();
            ResearchItems.Insert(0, new ResearchItemRow(new ResearchItem
            {
                Expression = term, Category = OwnCategory, SegmentIds = segments.Select(s => s.Id).ToList(),
                Context = segments.FirstOrDefault() is { } first ? TagText.StripTags(first.Source) : "",
                Reason = segments.Count == 0 ? L.T("selbst hinzugefügt, kommt im Dokument nicht vor") : L.T($"selbst hinzugefügt, {segments.Count}× im Dokument"),
            }));
            added++;
        }
        OwnResearchTerms = "";
        OnPropertyChanged(nameof(HasResearchItems));
        ResearchStatus = L.T($"{added} eigene Begriffe hinzugefügt – mit „Angehakte recherchieren“ erstellen.");
    }

    /// <summary>Womit recherchiert wird: KI-Zugang für die Recherche (im Verbund dessen Modelle) und Websuche.</summary>
    public string ResearchEngine
    {
        get
        {
            var ai = AppServices.Ai;
            var p = ai.Get(ai.ResearchProviderId);
            var model = p == null ? L.T("keine KI eingerichtet")
                : p.Kind == Core.Ai.AiProviderKind.Pool ? $"{p.Name}: {string.Join(", ", (p.Members ?? []).Select(m => m.Model))}"
                : p.Model.Length > 0 && p.Kind != Core.Ai.AiProviderKind.BuiltIn ? $"{p.Name} ({p.Model})" : p.Name;
            var search = Core.Ai.SearchProviderFactory.NameFor(ai);
            return L.T($"Recherche mit: {model} · Suche: {L.T(search)}") +
                   (search == "Wikipedia" ? " " + L.T("(nur Wikipedia – für Fachseiten unter „KI einrichten“ → „Websuche für die Agenten“ einen Suchdienst einrichten)") : "");
        }
    }

    /// <summary>Gefundene Ausdrücke je Ausgangssatz (beim Projekt gespeichert), für die Zielsprache des offenen Dokuments.</summary>
    private Dictionary<string, SegmentScan> _scanIndex = new();

    /// <summary>
    /// Beim Öffnen eines Dokuments: Länder vorbelegen, gespeicherte Dossiers laden – und die Ausdrücke, die die KI in seinen
    /// Sätzen schon gefunden hat (auch in anderen Dokumenten des Projekts), samt Bewertung. So muss nach dem erneuten
    /// Öffnen nichts noch einmal gelesen oder recherchiert werden.
    /// </summary>
    private void LoadResearch()
    {
        OnPropertyChanged(nameof(ResearchEngine));
        ResearchFrom = _doc?.SourceLanguage;
        ResearchTo = _doc?.TargetLanguage;
        ResearchItems.Clear();
        _scannedSegments.Clear();                                // die Nummern gehören zum vorigen Dokument
        RefreshFeedbackSummary();
        _dossiers = Project == null ? new() : DossierStore.Load(Project);
        _scanIndex = Project == null || _doc == null ? new() : SegmentScanStore.Index(SegmentScanStore.Load(Project), _doc.TargetLanguage);
        if (_doc != null)
        {
            var (items, scanned) = SegmentScanStore.ForDocument(_scanIndex, _doc.Segments);
            foreach (var i in items.OrderByDescending(i => i.Difficulty)) ResearchItems.Add(new ResearchItemRow(i));
            _scannedSegments.UnionWith(scanned);
            foreach (var seg in _all)
                if (_scanIndex.TryGetValue(SegmentScanStore.KeyOf(seg.Source), out var scan) && scan.AssessmentLevel is { Length: > 0 } level)
                    seg.ShowAssessment(level, scan.AssessmentInfo ?? "");
        }
        OnPropertyChanged(nameof(HasResearchItems));
        RefreshDossiers();
    }

    /// <summary>
    /// Was die KI in einem Satz fand, beim Projekt festhalten – gilt auch für jeden gleichen Satz (in diesem und in anderen
    /// Dokumenten), der damit ebenfalls als gelesen zählt.
    /// </summary>
    private void RememberScan(SegmentViewModel seg, IReadOnlyList<ResearchItem> items, string targetLanguage) =>
        RememberScans([(seg.Source, items)], targetLanguage);

    private void RememberScans(IReadOnlyList<(string Source, IReadOnlyList<ResearchItem> Items)> scans, string targetLanguage)
    {
        if (Project == null || scans.Count == 0) return;
        foreach (var entry in SegmentScanStore.SaveMany(Project, scans.Select(x => (x.Source, (IEnumerable<ResearchItem>)x.Items)), targetLanguage))
            _scanIndex[entry.Source] = entry;
        var keys = scans.Select(x => SegmentScanStore.KeyOf(x.Source)).ToHashSet();
        var same = _all.Where(s => keys.Contains(SegmentScanStore.KeyOf(s.Source))).ToList();
        foreach (var s in same)
        {
            _scannedSegments.Add(s.Id);
            // die Ausdrücke stehen auch im gleichen Satz an anderer Stelle
            foreach (var item in scans.Where(x => SegmentScanStore.KeyOf(x.Source) == SegmentScanStore.KeyOf(s.Source)).SelectMany(x => x.Items))
            {
                var row = ResearchItems.FirstOrDefault(r => Same(r.Expression, item.Expression));
                if (row != null && !row.Item.SegmentIds.Contains(s.Id)) row.Item.SegmentIds.Add(s.Id);
            }
        }
    }

    /// <summary>Die Bewertung eines Satzes festhalten (Kennzeichen am Segment, auch nach dem erneuten Öffnen).</summary>
    private void RememberAssessment(SegmentViewModel seg, string targetLanguage)
    {
        if (Project == null || !seg.HasAssessment) return;
        var entry = SegmentScanStore.Save(Project, seg.Source, targetLanguage, null, seg.AssessmentLevel, seg.AssessmentInfo);
        _scanIndex[entry.Source] = entry;
    }

    /// <summary>Dossiers zum gewählten Segment (oder alle).</summary>
    private void RefreshDossiers(string? text = null)
    {
        Dossiers.Clear();
        if (_doc == null) return;
        var list = ShowAllDossiers
            ? _dossiers.Where(d => Languages.Matches(d.TargetLanguage, _doc.TargetLanguage)).OrderBy(d => d.Expression).ToList()
            : DossierStore.ForText(_dossiers, text ?? Selected?.Source ?? "", _doc.TargetLanguage);
        // in der Liste aller Dossiers zugeklappt (nur Kopfzeile), beim Segment aufgeklappt
        foreach (var d in list) Dossiers.Add(new DossierRow(d, this, expanded: !ShowAllDossiers || list.Count == 1));
        RefreshSegmentExpressions();
    }

    /// <summary>Die KI sieht das Dokument durch und schlägt Ausdrücke vor, die Recherche brauchen.</summary>
    [RelayCommand]
    private async Task FindResearchItems()
    {
        if (_doc == null || Project == null || IsScanning) return;
        IsScanning = true;
        // Eigene Begriffe bleiben stehen; nur die Vorschläge der KI werden ersetzt
        foreach (var row in ResearchItems.Where(r => !r.IsOwn).ToList()) ResearchItems.Remove(row);
        try
        {
            var ctx = CreateContext(AppServices.Ai.ResearchProviderId);
            var items = await new DocumentResearch(ctx).FindAsync(_doc, ResearchFrom ?? _doc.SourceLanguage, ResearchTo ?? _doc.TargetLanguage,
                _brief, Project.Topic);
            foreach (var i in items.OrderByDescending(i => i.Difficulty)
                         .Where(i => !ResearchItems.Any(r => Same(r.Expression, i.Expression))))
                ResearchItems.Add(new ResearchItemRow(i));
            // je Satz festhalten, auch die ohne Fund (dort gibt es nichts zu recherchieren)
            RememberScans(_doc.Segments.Where(s => TagText.StripTags(s.Source).Trim().Length > 0)
                .Select(s => (s.Source, (IReadOnlyList<ResearchItem>)items.Where(i => i.SegmentIds.Contains(s.Id)).ToList())).ToList(),
                ResearchTo ?? _doc.TargetLanguage);
            ResearchStatus = items.Count == 0 ? L.T("Keine Ausdrücke gefunden, die Recherche brauchen.")
                : L.T($"{items.Count} Ausdrücke gefunden – auswählen und recherchieren.");
        }
        catch (Exception ex) { ResearchStatus = "KI: " + ex.Message; }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasResearchItems));
            RefreshSegmentExpressions();
        }
    }

    /// <summary>Angehakte Ausdrücke nacheinander recherchieren (Begriffsvergleich mit Fachseiten beider Länder).</summary>
    [RelayCommand]
    private async Task ResearchCheckedItems()
    {
        var items = ResearchItems.Where(r => r.IsChecked).Select(r => r.Item).ToList();
        if (items.Count == 0) return;
        await ResearchAsync(items);
    }

    /// <summary>Aus dem Editor: ein Wort, eine Markierung, ein Satz oder das ganze Segment.</summary>
    public async Task ShowOrResearchAsync(string text)
    {
        if (_doc == null || Project == null || text.Trim().Length == 0) return;
        SidePanelTab = 8;
        ShowAllDossiers = false;
        RefreshDossiers(text);
        // Steht zum markierten Ausdruck schon ein Dossier, genügt die Anzeige; sonst recherchieren
        if (Dossiers.Any(d => DocumentResearch.Contains(d.Dossier.Expression, text.Trim()) || DocumentResearch.Contains(text, d.Dossier.Expression))
            && text.Trim().Split(' ').Length <= 6) return;
        var segments = _all.Where(s => DocumentResearch.Contains(TagText.StripTags(s.Source), text.Trim())).Select(s => s.Id).ToList();
        var context = Selected?.Source is { } src ? TagText.StripTags(src) : text;
        await ResearchAsync([new ResearchItem { Expression = text.Trim(), Context = context, Category = "term", SegmentIds = segments }]);
        RefreshDossiers(text);
    }

    /// <summary>
    /// Ausdrücke recherchieren: in die Warteschlange (ein einzelner Ausdruck vorn) – die Abarbeitung läuft weiter, auch wenn
    /// man den Reiter oder das Segment wechselt; fertig ist die zurückgegebene Aufgabe, wenn die Warteschlange leer ist.
    /// </summary>
    private Task ResearchAsync(IReadOnlyList<ResearchItem> items, string? from = null, string? to = null)
    {
        if (_doc == null || Project == null || items.Count == 0) return Task.CompletedTask;
        EnqueueResearch(items, front: items.Count == 1, from, to);
        return _researchWorker ?? Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelResearch() => _researchCts?.Cancel();

    [RelayCommand]
    private void DossierToTermbase(DossierRow? row)
    {
        if (row == null || _doc == null || Project == null) return;
        var tb = Project.SharedTermbasesToWrite(_doc.TargetLanguage).FirstOrDefault() ?? Project.ProjectTermbase;
        try
        {
            DossierStore.ToTermbase(row.Dossier, tb, Project.Topic);
            _main.Status = $"„{row.Dossier.Expression}“ → „{row.Dossier.Recommended}“ in „{tb.Name}“ übernommen.";
            _main.Resources.Refresh();
            if (Selected != null) OnSelectedChanged(Selected);
        }
        catch (Exception ex) { _main.Status = ex.Message; }
    }

    [RelayCommand]
    private void DossierToKnowledge(DossierRow? row)
    {
        if (row == null || _doc == null || Project == null) return;
        var kb = Project.KnowledgeFor(_doc.TargetLanguage).FirstOrDefault(k => k.IsRemote) ?? Project.ProjectKnowledge;
        try
        {
            DossierStore.ToKnowledge(row.Dossier, kb, Project.Topic, Environment.UserName);
            _main.Status = $"Dossier „{row.Dossier.Expression}“ als Wissen in „{kb.Name}“ gespeichert.";
        }
        catch (Exception ex) { _main.Status = ex.Message; }
    }

    /// <summary>
    /// Verwendungsbeispiele: die empfohlene Benennung und die anderen Kandidaten in der Zielsprache im Netz suchen – mit
    /// dem Satz von der Seite und einem Link dorthin; sagt klar, was üblich ist und was nicht.
    /// </summary>
    [RelayCommand]
    private Task DossierUsage(DossierRow? row)
    {
        if (row?.Dossier is not { Recommended: { Length: > 0 } recommended } d) return Task.CompletedTask;
        var others = d.Candidates.Select(c => c.Term).Where(t => t.Length > 0 && !t.Equals(recommended, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        return CheckUsageAsync(recommended, others.Count > 0 ? others : null, d.TargetLanguage);
    }

    /// <summary>Vorgeschlagene Formulierung an der Schreibmarke bzw. ans Ende der Übersetzung setzen.</summary>
    [RelayCommand]
    private void InsertRendering(DossierRow? row)
    {
        if (row?.Dossier.Rendering is not { Length: > 0 } text || Selected == null) return;
        InsertIntoTarget?.Invoke(text);
    }

    /// <summary>Den Ausdruck noch einmal recherchieren (vielleicht findet die KI jetzt bessere Belege); die alte Fassung bleibt erhalten.</summary>
    [RelayCommand]
    private Task ReresearchDossier(DossierRow? row)
    {
        if (row == null) return Task.CompletedTask;
        var d = row.Dossier;
        return ResearchAsync([new ResearchItem { Expression = d.Expression, Context = d.Context ?? "", Category = d.Category, SegmentIds = d.SegmentIds }],
            d.SourceLanguage, d.TargetLanguage);
    }

    [RelayCommand]
    private void RestorePreviousDossier(DossierRow? row)
    {
        if (row == null || Project == null) return;
        DossierStore.RestorePrevious(Project, row.Dossier);
        _dossiers = DossierStore.Load(Project);
        RefreshDossiers(ShowAllDossiers ? null : row.Dossier.Expression);
    }

    [RelayCommand]
    private void RemoveDossier(DossierRow? row)
    {
        if (row == null || Project == null) return;
        DossierStore.Remove(Project, row.Dossier);
        _dossiers = DossierStore.Load(Project);
        RefreshDossiers();
    }

    [RelayCommand]
    private async Task OpenDossierSource(Core.Agents.SourceRef? source)
    {
        if (source == null || AppServices.Platform == null) return;
        var url = WithTextFragment(source.Url, source.Quote);
        try
        {
            await Core.Privacy.Outbound.CheckAsync(new Uri(source.Url).Host, Core.Privacy.OutboundKind.Web, L.T("Im Browser öffnen:") + " " + url, false);
            await AppServices.Platform.OpenUrlAsync(url);
        }
        catch (Core.Privacy.OutboundDeclinedException ex) { _main.Status = ex.Message; }
    }

    /// <summary>
    /// Adresse mit Textfragment („#:~:text=Anfang,Ende“): Der Browser springt zur zitierten Stelle und hebt sie hervor.
    /// Anfang und Ende aus je vier Wörtern, damit kleine Abweichungen im Zitat (Leerraum, Satzzeichen dazwischen) nicht stören.
    /// </summary>
    public static string WithTextFragment(string url, string? quote) => Core.Ai.SourceLink.WithTextFragment(url, quote);

    /// <summary>Die Ansicht setzt Text an der Schreibmarke des Zielfelds ein.</summary>
    public Action<string>? InsertIntoTarget { get; set; }
}
