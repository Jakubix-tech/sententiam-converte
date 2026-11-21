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
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Zeile im Alignment-Editor; Textänderungen gehen direkt in die Sitzung.</summary>
public partial class AlignPairRow : ObservableObject
{
    private readonly Action<int, bool, string> _setText;
    private readonly Action<int, bool> _confirm;

    public AlignPairRow(AlignedPair pair, int index, Action<int, bool, string> setText, Action<int, bool> confirm)
    {
        Pair = pair;
        Index = index;
        _setText = setText;
        _confirm = confirm;
    }

    public AlignedPair Pair { get; }
    public int Index { get; }
    public int Number => Index + 1;

    /// <summary>Jede Zeile ein Satz; ein Zeilenumbruch teilt einen Satz.</summary>
    public string SourceText
    {
        get => string.Join("\n", Pair.Source);
        set { _setText(Index, true, value); OnPropertyChanged(); OnPropertyChanged(nameof(Quality)); }
    }

    public string TargetText
    {
        get => string.Join("\n", Pair.Target);
        set { _setText(Index, false, value); OnPropertyChanged(); OnPropertyChanged(nameof(Quality)); }
    }

    public bool Accepted
    {
        get => Pair.Accepted;
        set { Pair.Accepted = value && Pair.IsComplete; OnPropertyChanged(); }
    }

    public bool Confirmed
    {
        get => Pair.Confirmed;
        set { _confirm(Index, value); OnPropertyChanged(); OnPropertyChanged(nameof(Accepted)); OnPropertyChanged(nameof(IsUncertain)); }
    }

    public bool IsUncertain => AlignmentSession.IsUncertain(Pair);

    public string Quality =>
        (Pair.IsComplete ? $"{Pair.Confidence * 100:0} %" : "–") + (Pair.AiScore is { } s ? $" · KI {s * 100:0} %" : "");

    public string? Note => Pair.AiNote;
}

/// <summary>Eine TM, in die das Alignment übernommen werden kann.</summary>
public sealed record TmChoice(string Name, ITranslationMemory? Instance, string? Path)
{
    public override string ToString() => Name;
}

/// <summary>
/// Alignment-Editor: alte Übersetzungen Satz für Satz zuordnen – automatisch nach Länge und Zahlen, auf Wunsch mit
/// KI, und von Hand (verbinden, teilen, Sätze verschieben, Text bearbeiten, bestätigen) – und in eine TM übernehmen.
/// </summary>
public partial class AlignmentViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private AlignmentSession? _session;
    private PickedFile? _source, _target;
    private CancellationTokenSource? _cts;

    public AlignmentViewModel(MainViewModel main) => _main = main;

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string SourceName { get; set; } = "";
    [ObservableProperty] public partial string TargetName { get; set; } = "";
    [ObservableProperty] public partial string SourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string TargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial bool KeepTags { get; set; } = true;
    [ObservableProperty] public partial string Info { get; set; } = "";
    [ObservableProperty] public partial AlignPairRow? Selected { get; set; }
    [ObservableProperty] public partial TmChoice? TargetTm { get; set; }

    public ObservableCollection<AlignPairRow> Rows { get; } = new();
    public ObservableCollection<TmChoice> Tms { get; } = new();

    [RelayCommand]
    private void Open()
    {
        if (_main.Project is { } p)
        {
            SourceLanguage = p.SourceLanguage;
            if (!p.TargetLanguages.Contains(TargetLanguage)) TargetLanguage = p.TargetLanguages.FirstOrDefault() ?? TargetLanguage;
        }
        RefreshTms();
        IsOpen = true;
    }

    private Action? _onClose;

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        var back = _onClose;
        _onClose = null;
        back?.Invoke();
    }

    /// <summary>Eine fertige Zuordnung prüfen (z. B. aus „Alte Übersetzungen übernehmen“); <paramref name="onClose"/>: wohin danach.</summary>
    public void Load(AlignmentSession session, string sourceName, string targetName, Action? onClose = null)
    {
        _session = session;
        _source = _target = null;
        SourceName = sourceName;
        TargetName = targetName;
        SourceLanguage = session.SourceLanguage;
        TargetLanguage = session.TargetLanguage;
        _onClose = onClose;
        Rebuild();
        RefreshTms();
        Selected = Rows.FirstOrDefault(r => r.IsUncertain);
        IsOpen = true;
    }

    partial void OnTargetLanguageChanged(string value) => RefreshTms();

    /// <summary>Ziel-TMs: die des Projekts (samt sprachbezogenen), ohne Projekt alle TMs im Benutzerordner und für Sprachen.</summary>
    private void RefreshTms()
    {
        var selected = TargetTm?.Name;
        Tms.Clear();
        if (_main.Project is { } p)
            foreach (var tm in p.Tms.Where(t => p.RefOf(t) is not { } r || r.AppliesTo(TargetLanguage)))
                Tms.Add(new TmChoice(tm == p.ProjectTm ? L.T("Projekt-TM") : tm.IsRemote ? tm.Name : Path.GetFileNameWithoutExtension(tm.Location), tm, null));
        else
            foreach (var d in DatabaseCatalog.All([]).Where(d => d.Kind == ResourceKind.Tm))
                Tms.Add(new TmChoice(d.Name, null, d.Path));
        TargetTm = Tms.FirstOrDefault(t => t.Name == selected)
                   ?? Tms.FirstOrDefault(t => t.Name.Contains(SourceLanguage + "–" + TargetLanguage)) ?? Tms.FirstOrDefault();
    }

    [RelayCommand]
    private void NewPairTm()
    {
        var name = $"TM {SourceLanguage}–{TargetLanguage}";
        var r = new DatabaseRecommendation(ResourceKind.Tm, name, DatabaseCatalog.SuggestedPath(ResourceKind.Tm, name), [SourceLanguage, TargetLanguage], "");
        DatabaseCatalog.Create(r, _main.Project);
        RefreshTms();
        TargetTm = Tms.FirstOrDefault(t => t.Name == name || t.Path == r.Path) ?? TargetTm;
        _main.Status = $"„{name}“ angelegt: {r.Path}";
    }

    [RelayCommand]
    private async Task PickSource()
    {
        var f = await AppServices.Platform!.PickFilesAsync("Ausgangstext wählen", FileFilters.SupportedExtensions.Select(e => "*" + e).ToList(), false);
        if (f.Count > 0) { _source = f[0]; SourceName = f[0].Name; }
    }

    [RelayCommand]
    private async Task PickTarget()
    {
        var f = await AppServices.Platform!.PickFilesAsync("Übersetzung wählen", FileFilters.SupportedExtensions.Select(e => "*" + e).ToList(), false);
        if (f.Count > 0) { _target = f[0]; TargetName = f[0].Name; }
    }

    /// <summary>Sätze eines Dokuments; die Formatierung steht als Tags darin (⟨1⟩…⟨/1⟩).</summary>
    private static async Task<List<string>> Sentences(PickedFile file, string language)
    {
        var filter = FileFilters.ForFile(file.Name) ?? throw new NotSupportedException($"{file.Name}: Dateityp wird nicht unterstützt.");
        var ms = new MemoryStream();
        await using (var s = await file.OpenRead()) await s.CopyToAsync(ms);
        ms.Position = 0;
        var doc = await Task.Run(() => filter.Read(ms, file.Name, language, language, new Segmenter(language)));
        return doc.Segments.Where(x => !x.Locked).Select(x => x.Source).ToList();
    }

    [RelayCommand]
    private Task Align() => Busy(async _ =>
    {
        if (_source == null || _target == null) { Info = "Bitte Ausgangstext und Übersetzung wählen."; return; }
        var src = await Sentences(_source, SourceLanguage.Trim());
        var tgt = await Sentences(_target, TargetLanguage.Trim());
        _session = await Task.Run(() => new AlignmentSession(SourceLanguage.Trim(), TargetLanguage.Trim(), src, tgt));
        Rebuild();
    });

    /// <summary>Zum Testen und für die Schnittstelle: mit fertigen Satzlisten beginnen.</summary>
    public void Start(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        _session = new AlignmentSession(SourceLanguage.Trim(), TargetLanguage.Trim(), source, target);
        Rebuild();
    }

    public AlignmentSession? Session => _session;

    private void Rebuild()
    {
        var selected = Selected?.Index;
        Rows.Clear();
        if (_session == null) return;
        for (var i = 0; i < _session.Pairs.Count; i++)
            Rows.Add(new AlignPairRow(_session.Pairs[i], i, (k, source, text) => { _session.SetText(k, source, text); UpdateInfo(); },
                (k, confirmed) => { _session.Confirm(k, confirmed); UpdateInfo(); }));
        Selected = selected is { } s && s < Rows.Count ? Rows[s] : null;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_session == null) return;
        var pairs = _session.Pairs;
        Info = $"{pairs.Count} Paare · {pairs.Count(p => p.Accepted && p.IsComplete)} ausgewählt · {pairs.Count(p => p.Confirmed)} bestätigt · {pairs.Count(AlignmentSession.IsUncertain)} unsicher";
    }

    private void Edit(Action<AlignmentSession> change)
    {
        if (_session == null) return;
        change(_session);
        Rebuild();
    }

    [RelayCommand] private void MergeWithNext(AlignPairRow? row) => Edit(s => s.MergeWithNext((row ?? Selected)?.Index ?? -1));
    [RelayCommand] private void Split(AlignPairRow? row) => Edit(s => s.Split((row ?? Selected)?.Index ?? -1));
    [RelayCommand] private void SourceUp(AlignPairRow? row) => Edit(s => s.MoveUp((row ?? Selected)?.Index ?? -1, true));
    [RelayCommand] private void SourceDown(AlignPairRow? row) => Edit(s => s.MoveDown((row ?? Selected)?.Index ?? -1, true));
    [RelayCommand] private void TargetUp(AlignPairRow? row) => Edit(s => s.MoveUp((row ?? Selected)?.Index ?? -1, false));
    [RelayCommand] private void TargetDown(AlignPairRow? row) => Edit(s => s.MoveDown((row ?? Selected)?.Index ?? -1, false));

    /// <summary>Neu nach Länge und Zahlen ausrichten; bestätigte Paare bleiben als Anker stehen.</summary>
    [RelayCommand] private void Realign() => Edit(s => s.Realign());

    private AiAligner Ai(CancellationToken ct) =>
        new(AgentContext.From(AppServices.Ai, AppServices.Ai.TranslateProviderId).Model, new Progress<string>(m => Info = m));

    [RelayCommand]
    private Task AiAuto() => Busy(async ct =>
    {
        if (_session == null) { Info = "Bitte zuerst ausrichten."; return; }
        var (fixedRuns, doubtful) = await Ai(ct).AutoAsync(_session, ct);
        Rebuild();
        _main.Status = $"KI-Alignment: {fixedRuns} Abschnitte neu ausgerichtet, {doubtful} Paare bleiben zweifelhaft (nicht ausgewählt).";
    });

    [RelayCommand]
    private Task AiCheck() => Busy(async ct =>
    {
        if (_session == null) { Info = "Bitte zuerst ausrichten."; return; }
        var doubtful = await Ai(ct).CheckAsync(_session, ct);
        Rebuild();
        _main.Status = $"KI-Prüfung: {doubtful} Paare zweifelhaft (nicht ausgewählt).";
    });

    [RelayCommand]
    private Task AiFix() => Busy(async ct =>
    {
        if (_session == null) { Info = "Bitte zuerst ausrichten."; return; }
        var n = await Ai(ct).FixUncertainAsync(_session, ct);
        Rebuild();
        _main.Status = $"KI hat {n} unsichere Abschnitte neu ausgerichtet.";
    });

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Die ausgewählten Paare in die gewählte TM übernehmen – mit angeglichenen Formatierungs-Tags oder ohne.</summary>
    [RelayCommand]
    private void Import()
    {
        if (_session == null) return;
        if (TargetTm is not { } choice) { Info = "Bitte eine Ziel-TM wählen oder eine neue anlegen."; return; }
        var own = choice.Instance == null && choice.Path != null ? new TranslationMemory(choice.Path) : null;
        try
        {
            var tm = choice.Instance ?? own!;
            var n = 0;
            foreach (var p in _session.Accepted.ToList())
            {
                var source = KeepTags ? p.SourceText : TagText.StripTags(p.SourceText);
                var target = KeepTags ? AlignmentSession.MatchTags(p.SourceText, p.TargetText) : TagText.StripTags(p.TargetText);
                var privacy = Core.Privacy.PrivacyContext.Current;
                if (privacy.Enabled && privacy.KeepOutOfTm) (source, target) = Core.Privacy.PrivacyScanner.MaskPair(source, target, privacy);
                tm.Add(_session.SourceLanguage, _session.TargetLanguage, source, target, "Alignment");
                n++;
            }
            _main.Status = $"{n} Paare in „{choice.Name}“ übernommen.";
            Info = _main.Status;
            _main.Resources.Refresh();
        }
        finally { own?.Dispose(); }
    }

    private async Task Busy(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Info = "Abgebrochen."; }
        catch (Exception ex) when (ex is Core.Ai.AiException or NotSupportedException or IOException or InvalidOperationException
                                   or System.Net.Http.HttpRequestException)
        {
            Info = "Fehler: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
