using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Dateipaar in der Liste: Original ⇄ Übersetzung, Sprachen, Ergebnis.</summary>
public partial class FilePairRow(TranslatedFilePair pair) : ObservableObject
{
    public TranslatedFilePair Pair { get; } = pair;
    public string SourceName => Pair.Source.Name;
    public string TargetName => Pair.Target?.Name ?? L.T("(in derselben Datei)");
    public string How => L.T(Pair.How);

    public string SourceLanguage
    {
        get => Pair.SourceLanguage;
        set { Pair.SourceLanguage = value.Trim(); OnPropertyChanged(); }
    }

    public string TargetLanguage
    {
        get => Pair.TargetLanguage;
        set { Pair.TargetLanguage = value.Trim(); OnPropertyChanged(); }
    }

    /// <summary>Nach dem Übernehmen: die Zuordnung (zum Prüfen im Alignment-Editor).</summary>
    public AlignmentSession? Session { get; set; }

    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial bool HasDoubts { get; set; }

    public bool CanSwap => !Pair.IsBilingual;

    public void Swap()
    {
        Pair.Swap();
        Session = null;
        Result = "";
        HasDoubts = false;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Ein vorgeschlagener Fachbegriff; Zielbegriff kann vor der Übernahme berichtigt werden.</summary>
public partial class TermSuggestionRow(TermSuggestion s, string sourceLanguage, string targetLanguage) : ObservableObject
{
    public string SourceLanguage { get; } = sourceLanguage;
    public string TargetLanguage { get; } = targetLanguage;
    public string Source { get; } = s.Source;
    [ObservableProperty] public partial string Target { get; set; } = s.Target;
    [ObservableProperty] public partial bool Selected { get; set; } = true;            // jeder Vorschlag ist in den Sätzen belegt
    public string Count => $"{s.Count}×";
    public string Example => s.Example;
    public string Languages => $"{SourceLanguage} → {TargetLanguage}";
}

/// <summary>
/// „Alte Übersetzungen übernehmen“: Dateien wählen (alle auf einmal), das Programm ordnet Original und Übersetzung
/// einander zu, richtet die Sätze aus (auf Wunsch mit KI), schreibt die sicheren Paare ins TM und schlägt Fachbegriffe
/// für die Terminologie vor. Unsichere Paare lassen sich im Alignment-Editor prüfen.
/// </summary>
public partial class ImportTranslationsViewModel(MainViewModel main) : ViewModelBase
{
    private CancellationTokenSource? _cts;
    private readonly List<ImportFile> _files = new();

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Info { get; set; } = "";
    [ObservableProperty] public partial string Unpaired { get; set; } = "";
    [ObservableProperty] public partial string SourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string TargetLanguages { get; set; } = "en-GB";

    /// <summary>In die gemeinsame TM bzw. Terminologie des Sprachpaars (gilt in allen Projekten) – sonst ins Projekt.</summary>
    [ObservableProperty] public partial bool ToShared { get; set; } = true;

    public bool ToProject
    {
        get => !ToShared;
        set => ToShared = !value;
    }

    partial void OnToSharedChanged(bool value) => OnPropertyChanged(nameof(ToProject));
    [ObservableProperty] public partial bool UseAi { get; set; }
    [ObservableProperty] public partial bool SuggestTerms { get; set; }
    [ObservableProperty] public partial bool KeepTags { get; set; } = true;
    [ObservableProperty] public partial string TermInfo { get; set; } = "";

    public ObservableCollection<FilePairRow> Pairs { get; } = new();
    public ObservableCollection<TermSuggestionRow> Terms { get; } = new();

    public bool HasProject => main.Project != null;
    public bool HasPairs => Pairs.Count > 0;
    public bool HasTerms => Terms.Count > 0;

    /// <summary>Eine KI ist eingerichtet und bereit (eingebaute nur, wenn heruntergeladen).</summary>
    public bool AiReady
    {
        get
        {
            var ai = AppServices.Ai;
            if (ai.Get(ai.TranslateProviderId) is not { } p) return false;
            return p.Kind != AiProviderKind.BuiltIn || BuiltInAi.Model(p.Model) is { } m && BuiltInAi.IsInstalled(m);
        }
    }

    public string AiNote => AiReady
        ? L.T("Die KI prüft jedes Satzpaar und richtet unsichere Abschnitte nach dem Sinn neu aus; ohne KI zählen Satzlänge und Zahlen.")
        : L.T("Ohne KI werden Sätze nach Länge und Zahlen zugeordnet – das reicht bei sauberen Übersetzungen. Für KI-Prüfung und Fachbegriffe zuerst unter „KI einrichten“ eine KI wählen.");

    public string PrivacyNote => PrivacyContext.Current is { Enabled: true, KeepOutOfTm: true }
        ? L.T("🔒 Vertraulich: Namen, Datum, Aktenzeichen … kommen nur als Platzhalter ins TM und an eine Online-KI.")
        : "";

    [RelayCommand]
    public void Open()
    {
        if (main.Project is { } p)
        {
            SourceLanguage = p.SourceLanguage;
            TargetLanguages = string.Join(", ", p.TargetLanguages);
        }
        else ToShared = true;
        UseAi = SuggestTerms = AiReady;
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(AiReady));
        OnPropertyChanged(nameof(AiNote));
        OnPropertyChanged(nameof(PrivacyNote));
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    private IReadOnlyList<string> Targets => TargetLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);

    [RelayCommand]
    private async Task PickFiles()
    {
        var patterns = FileFilters.SupportedExtensions.Select(e => "*" + e).ToList();
        var picked = await AppServices.Platform!.PickFilesAsync("Original und Übersetzung wählen (auch mehrere Paare)", patterns, true);
        if (picked.Count == 0) return;
        foreach (var f in picked)
        {
            await using var s = await f.OpenRead();
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            _files.RemoveAll(x => x.Name == f.Name);
            _files.Add(new ImportFile(f.Name, ms.ToArray()));
        }
        await Repair();
    }

    /// <summary>Für Tests und die Schnittstelle: Dateien direkt übergeben.</summary>
    public async Task AddFiles(IEnumerable<ImportFile> files)
    {
        foreach (var f in files)
        {
            _files.RemoveAll(x => x.Name == f.Name);
            _files.Add(f);
        }
        await Repair();
    }

    /// <summary>Alle gewählten Dateien (neu) einander zuordnen.</summary>
    [RelayCommand]
    private async Task Repair()
    {
        var source = SourceLanguage.Trim();
        var targets = Targets;
        var files = _files.ToList();
        var result = await Task.Run(() => TranslatedFiles.Pair(files, source, targets));
        Pairs.Clear();
        foreach (var p in result.Pairs) Pairs.Add(new FilePairRow(p));
        Unpaired = result.Unpaired.Count == 0 ? "" : $"Ohne Gegenstück: {string.Join(", ", result.Unpaired.Select(f => f.Name))} – bitte die passende Datei dazu wählen.";
        Info = Pairs.Count == 0 ? "" : $"{Pairs.Count} Paare erkannt. Bitte kurz prüfen: links das Original, rechts die Übersetzung (sonst ⇄ tauschen).";
        OnPropertyChanged(nameof(HasPairs));
    }

    [RelayCommand]
    private void Clear()
    {
        _files.Clear();
        Pairs.Clear();
        Terms.Clear();
        Unpaired = Info = TermInfo = "";
        OnPropertyChanged(nameof(HasPairs));
        OnPropertyChanged(nameof(HasTerms));
    }

    [RelayCommand]
    private void Swap(FilePairRow? row) => row?.Swap();

    [RelayCommand]
    private void Remove(FilePairRow? row)
    {
        if (row == null) return;
        Pairs.Remove(row);
        OnPropertyChanged(nameof(HasPairs));
    }

    /// <summary>Unsichere Paare eines Dateipaars im Alignment-Editor prüfen; danach geht es hier weiter.</summary>
    [RelayCommand]
    private void Review(FilePairRow? row)
    {
        if (row?.Session == null) return;
        IsOpen = false;
        main.Resources.Alignment.Load(row.Session, row.SourceName, row.Pair.Target?.Name ?? row.SourceName, () => IsOpen = true);
    }

    private IChatModel? Model() => AiReady ? AgentContext.From(AppServices.Ai, AppServices.Ai.TranslateProviderId).Model : null;

    /// <summary>Alles in einem Schritt: ausrichten, (KI prüft), ins TM, Fachbegriffe vorschlagen.</summary>
    [RelayCommand]
    private Task Run() => Busy(async ct =>
    {
        if (Pairs.Count == 0) { Info = "Bitte zuerst Dateien wählen."; return; }
        if (!ToShared && main.Project == null) { Info = "Ohne offenes Projekt bitte „gemeinsame TM des Sprachpaars“ wählen."; return; }
        Terms.Clear();
        TermInfo = "";
        OnPropertyChanged(nameof(HasTerms));
        var model = UseAi || SuggestTerms ? Model() : null;
        var privacy = main.Project?.Privacy ?? PrivacyContext.Current;
        int total = 0, doubtful = 0;
        var tmNames = new HashSet<string>();
        foreach (var row in Pairs.ToList())
        {
            ct.ThrowIfCancellationRequested();
            row.Result = "wird ausgerichtet …";
            try
            {
                var pair = row.Pair;
                var session = await Task.Run(() => TranslatedFiles.Align(pair), ct);
                if (UseAi && model != null && !pair.IsBilingual)
                {
                    row.Result = "KI prüft die Zuordnung …";
                    var finished = false;                      // späte Fortschrittsmeldungen nicht über das Ergebnis schreiben
                    await new AiAligner(model, new Progress<string>(m => { if (!finished) row.Result = m; })).AutoAsync(session, ct);
                    finished = true;
                }
                row.Session = session;
                var (tmName, n) = WriteTm(session, pair.Source.Name, privacy);
                tmNames.Add(tmName);
                var unsure = session.Pairs.Count(p => !(p.Accepted && p.IsComplete));
                total += n;
                doubtful += unsure;
                row.HasDoubts = unsure > 0 && !pair.IsBilingual;
                row.Result = unsure == 0 ? $"{n} Satzpaare übernommen" : $"{n} Satzpaare übernommen, {unsure} unsicher (nicht übernommen)";
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException or System.Xml.XmlException)
            {
                row.Result = "Fehler: " + ex.Message;
            }
        }
        main.Resources.Refresh();
        main.Resources.RefreshDatabases();
        Info = $"{total} Satzpaare in {string.Join(", ", tmNames)} übernommen." +
               (doubtful > 0 ? $" {doubtful} unsichere Paare wurden ausgelassen – mit „Prüfen“ im Alignment-Editor ansehen." : "");
        main.Status = Info;

        if (SuggestTerms && model != null) await SuggestAsync(model, ct);
        else if (SuggestTerms) TermInfo = "Für Fachbegriff-Vorschläge wird eine KI gebraucht (Seite „KI einrichten“).";
    });

    /// <summary>Ins Projekt-TM oder in die gemeinsame TM des Sprachpaars schreiben.</summary>
    private (string Name, int Count) WriteTm(AlignmentSession session, string origin, PrivacySettings privacy)
    {
        var p = main.Project;
        if (!ToShared && p != null)
            return (L.T("Projekt-TM"), TranslatedFiles.ImportToTm(session, p.ProjectTm, origin, KeepTags, privacy));
        var (name, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Tm, session.SourceLanguage, session.TargetLanguage, p);
        if (p?.Tms.FirstOrDefault(t => !t.IsRemote && SamePath(t.Location, path)) is { } open)
            return (name, TranslatedFiles.ImportToTm(session, open, origin, KeepTags, privacy));
        using var tm = new TranslationMemory(path);
        return (name, TranslatedFiles.ImportToTm(session, tm, origin, KeepTags, privacy));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private async Task SuggestAsync(IChatModel model, CancellationToken ct)
    {
        var finished = false;
        var suggester = new TermSuggester(model, new Progress<string>(m => { if (!finished) TermInfo = m; }));
        foreach (var group in Pairs.Where(r => r.Session != null).GroupBy(r => (r.Pair.SourceLanguage, r.Pair.TargetLanguage)))
        {
            var (src, tgt) = group.Key;
            var sentences = group.SelectMany(r => r.Session!.Accepted).Select(p => (p.SourceText, p.TargetText)).ToList();
            var known = KnownTerms(src, tgt);
            try
            {
                var found = await suggester.SuggestAsync(sentences, src, tgt, main.Project?.Topic, known, ct);
                foreach (var t in found) Terms.Add(new TermSuggestionRow(t, src, tgt));
            }
            finally { DisposeKnown(); }
        }
        finished = true;
        OnPropertyChanged(nameof(HasTerms));
        TermInfo = Terms.Count == 0
            ? "Keine neuen Fachbegriffe gefunden."
            : $"{Terms.Count} Fachbegriffe gefunden – jeder ist in den Sätzen belegt. Bitte prüfen, Zielbegriff bei Bedarf berichtigen, dann übernehmen.";
    }

    private readonly List<ITermbase> _opened = new();

    private void DisposeKnown()
    {
        foreach (var t in _opened) t.Dispose();
        _opened.Clear();
    }

    /// <summary>Was schon in einer Terminologie für das Sprachpaar steht, wird nicht noch einmal vorgeschlagen.</summary>
    private Func<string, bool> KnownTerms(string src, string tgt)
    {
        var termbases = new List<ITermbase>();
        if (main.Project is { } p) termbases.AddRange(p.TermbasesFor(tgt));
        foreach (var r in LanguageResources.For(src, [tgt]).Where(r => r.Kind == ResourceKind.Termbase && !r.IsRemote && File.Exists(r.Location)))
            if (!termbases.Any(t => !t.IsRemote && SamePath(t.Location, r.Location)))
            {
                var tb = new Termbase(r.Location);
                _opened.Add(tb);
                termbases.Add(tb);
            }
        return term => termbases.Any(tb => tb.Lookup(src, term, 5)
            .Any(c => c.Terms.Any(t => Languages.Matches(t.Language, src) && string.Equals(t.Text, term, StringComparison.CurrentCultureIgnoreCase))));
    }

    [RelayCommand]
    private void SelectAllTerms()
    {
        var all = Terms.Any(t => !t.Selected);
        foreach (var t in Terms) t.Selected = all;
    }

    /// <summary>Die ausgewählten Begriffe in die Terminologie des Sprachpaars (bzw. des Projekts) übernehmen.</summary>
    [RelayCommand]
    private void AddTerms()
    {
        var chosen = Terms.Where(t => t.Selected && t.Target.Trim().Length > 0).ToList();
        if (chosen.Count == 0) { TermInfo = "Bitte Begriffe auswählen."; return; }
        var names = new HashSet<string>();
        foreach (var group in chosen.GroupBy(t => (t.SourceLanguage, t.TargetLanguage)))
        {
            var (src, tgt) = group.Key;
            var concepts = group.Select(t => ((IReadOnlyList<(string, string, TermStatus)>)
                [(src, t.Source, TermStatus.Preferred), (tgt, t.Target.Trim(), TermStatus.Preferred)], (string?)null, main.Project?.Topic, (string?)"Alte Übersetzung"));
            if (!ToShared && main.Project is { } p)
            {
                p.ProjectTermbase.AddConcepts(concepts);
                names.Add(L.T("Projekt-Terminologie"));
                continue;
            }
            var (name, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, src, tgt, main.Project);
            if (main.Project?.Termbases.OfType<Termbase>().FirstOrDefault(t => SamePath(t.Path, path)) is { } open)
                open.AddConcepts(concepts);
            else
                using (var tb = new Termbase(path)) tb.AddConcepts(concepts);
            names.Add(name);
        }
        foreach (var t in chosen) Terms.Remove(t);
        OnPropertyChanged(nameof(HasTerms));
        TermInfo = $"{chosen.Count} Begriffe in {string.Join(", ", names)} übernommen.";
        main.Status = TermInfo;
        main.Resources.Refresh();
        main.Resources.RefreshDatabases();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task Busy(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Info = "Abgebrochen."; }
        catch (Exception ex) when (ex is AiException or IOException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            Info = "Fehler: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
