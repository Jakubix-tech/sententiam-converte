using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine erkannte geschützte Angabe in der Übersicht; abgehakt = doch nicht schützen.</summary>
public sealed partial class DetectedRow(SensitiveKind kind, string value, int count) : ObservableObject
{
    public SensitiveKind Kind { get; } = kind;
    public string Value { get; } = value;
    public int Count { get; } = count;
    public string KindName => PrivacyViewModel.KindName(Kind);
    public string Text => $"{Value}  ({Count}×)";

    /// <summary>Angehakt = schützen; die Schutzliste lässt sich hier nicht abwählen (dort austragen).</summary>
    [ObservableProperty] public partial bool IsSensitive { get; set; } = true;
    public bool CanUnprotect => Kind != SensitiveKind.Term;
}

/// <summary>Ein gefundener Name zum Prüfen: angehakt = in die Schutzliste.</summary>
public sealed partial class NameCandidateRow(NameCandidate candidate) : ObservableObject
{
    public string Name => candidate.Name;
    public string KindName => L.T(candidate.Kind switch
    {
        NameKind.Person => "Person",
        NameKind.Address => "Anschrift",
        NameKind.Place => "Ort",
        NameKind.Company => "Firma",
        NameKind.OwnBrand => "eigene Marke/Projekt",
        NameKind.Product => "Produkt/Software",
        NameKind.Institution => "Einrichtung",
        NameKind.Country => "Land",
        _ => "Sonstiges",
    });
    public string Reason => candidate.Reason;
    [ObservableProperty] public partial bool IsChecked { get; set; } = candidate.Protect;
}

/// <summary>
/// Datenschutz des offenen Projekts (ohne Projekt: Voreinstellung für Recherchen und neue Projekte): was geschützt wird,
/// Schutzliste, Namen mit lokaler KI finden, vorhandene TMs bereinigen.
/// </summary>
public partial class PrivacyViewModel(MainViewModel main) : ViewModelBase
{
    private PrivacySettings S => main.Project?.Privacy ?? AppServices.Ai.Privacy;

    private void Save()
    {
        if (main.Project is { } p) p.Save();
        else AppServices.Ai.Save();
        PrivacyContext.Current = S;
        main.Editor.RefreshSensitive();                                  // 🔒 an den Segmenten neu setzen
    }

    public static string KindName(SensitiveKind kind) => L.T(kind switch
    {
        SensitiveKind.Name => "Name",
        SensitiveKind.Date => "Datum",
        SensitiveKind.Case => "Aktenzeichen",
        SensitiveKind.Email => "E-Mail",
        SensitiveKind.Phone => "Telefon",
        SensitiveKind.Iban => "IBAN",
        SensitiveKind.Address => "Anschrift",
        _ => "Schutzliste",
    });

    /// <summary>Nicht schützen: Werte, die die Erkennung findet, die aber nicht sensibel sind – je Zeile einer.</summary>
    public string NotSensitiveText
    {
        get => string.Join("\n", S.NotSensitive);
        set
        {
            S.NotSensitive = value.Replace("\r", "").Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            Save();
            OnPropertyChanged();
        }
    }

    public bool HasDetected => Detected.Count > 0;

    /// <summary>Abgehakte erkannte Angaben in „Nicht schützen“ übernehmen (danach werden sie nicht mehr ersetzt).</summary>
    [RelayCommand]
    private async Task ApplyDetected()
    {
        var off = Detected.Where(d => !d.IsSensitive && d.CanUnprotect).Select(d => d.Value).ToList();
        if (off.Count == 0) { Info = "Nichts abgehakt – zum Ausnehmen eine erkannte Angabe abhaken."; return; }
        S.NotSensitive = S.NotSensitive.Concat(off).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
        OnPropertyChanged(nameof(NotSensitiveText));
        await ShowDetected();
        Info = L.T($"{off.Count} Angaben werden nicht mehr geschützt (unter „Nicht schützen“ wieder austragen).");
    }

    /// <summary>Nach Projektwechsel: alles neu anzeigen und für die KI gelten lassen.</summary>
    public void Refresh()
    {
        PrivacyContext.Current = S;
        Detected.Clear();
        Info = "";
        OnPropertyChanged(string.Empty);
    }

    public string Title => main.Project != null ? "Datenschutz für dieses Projekt" : "Datenschutz (ohne Projekt; Vorlage für neue Projekte)";

    public bool Enabled { get => S.Enabled; set { S.Enabled = value; if (value) Suggestion = ""; Save(); OnPropertyChanged(); } }
    public bool AlsoLocalAi { get => S.AlsoLocalAi; set { S.AlsoLocalAi = value; Save(); OnPropertyChanged(); } }
    public bool KeepOutOfTm { get => S.KeepOutOfTm; set { S.KeepOutOfTm = value; Save(); OnPropertyChanged(); } }
    public bool Names { get => S.Names; set { S.Names = value; Save(); OnPropertyChanged(); } }
    public bool Dates { get => S.Dates; set { S.Dates = value; Save(); OnPropertyChanged(); } }
    public bool CaseNumbers { get => S.CaseNumbers; set { S.CaseNumbers = value; Save(); OnPropertyChanged(); } }
    public bool Emails { get => S.Emails; set { S.Emails = value; Save(); OnPropertyChanged(); } }
    public bool Phones { get => S.Phones; set { S.Phones = value; Save(); OnPropertyChanged(); } }
    public bool Iban { get => S.Iban; set { S.Iban = value; Save(); OnPropertyChanged(); } }
    public bool Addresses { get => S.Addresses; set { S.Addresses = value; Save(); OnPropertyChanged(); } }
    public bool ShareResearch { get => S.ShareResearch; set { S.ShareResearch = value; Save(); OnPropertyChanged(); } }

    /// <summary>Schutzliste, je Zeile ein Begriff.</summary>
    public string TermsText
    {
        get => string.Join("\n", S.Terms);
        set
        {
            S.Terms = value.Replace("\r", "").Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial string Info { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Vorschlag, „Vertraulich“ einzuschalten, weil die Dateien persönliche Angaben enthalten (leer = keiner).</summary>
    [ObservableProperty] public partial string Suggestion { get; set; } = "";

    /// <summary>Nach dem Öffnen oder Hinzufügen von Dateien: enthalten sie Namen, Aktenzeichen …? Dann einmal nachfragen.</summary>
    public async Task SuggestAsync()
    {
        Suggestion = "";
        if (main.Project is not { } p || S.Enabled || S.Asked) return;
        // ein Datum oder eine Telefonnummer allein macht einen Text noch nicht vertraulich
        var probe = new PrivacySettings { Enabled = true, Dates = false, Phones = false, Terms = S.Terms.ToList() };
        try
        {
            var texts = await ProjectTexts();
            var rows = await Task.Run(() => PrivacyScanner.Detect(texts, probe));
            if (rows.Count == 0 || main.Project != p) return;
            Info = "";
            Detected.Clear();
            var names = rows.Count(r => r.Kind is SensitiveKind.Name or SensitiveKind.Term);
            var cases = rows.Count(r => r.Kind == SensitiveKind.Case);
            var examples = string.Join(", ", rows.OrderBy(r => r.Kind).Take(3).Select(r => r.Value));
            Suggestion = $"Die Dateien enthalten persönliche Angaben ({names} Namen, {cases} Aktenzeichen, {rows.Count - names - cases} sonstige, z. B. {examples}). " +
                         "Sollen sie nur als Platzhalter an eine Online-KI gehen und nicht ins TM?";
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException) { }
    }

    [RelayCommand]
    private void AcceptSuggestion()
    {
        S.Asked = true;
        Enabled = true;
        Suggestion = "";
        Info = "Vertraulich eingeschaltet: Namen, Datum, Aktenzeichen … gehen nur als Platzhalter an eine Online-KI und nicht ins TM.";
    }

    [RelayCommand]
    private void DeclineSuggestion()
    {
        S.Asked = true;
        Save();
        Suggestion = "";
    }
    public ObservableCollection<DetectedRow> Detected { get; } = new();

    /// <summary>Markierten Text in die Schutzliste aufnehmen.</summary>
    public void Protect(string text)
    {
        text = text.Trim();
        if (text.Length < 2 || S.Terms.Contains(text, StringComparer.OrdinalIgnoreCase)) return;
        S.Terms.Add(text);
        Save();
        OnPropertyChanged(nameof(TermsText));
    }

    private async Task<string[]> ProjectTexts() => main.Project is { } p
        ? await Task.Run(() => p.Documents().SelectMany(d => p.LoadDocument(d.File, d.Language).Segments)
            .SelectMany(s => new[] { s.Source, s.Target }).Where(t => t.Length > 0).Distinct().ToArray())
        : [];

    [RelayCommand]
    private async Task ShowDetected()
    {
        if (main.Project == null) { Info = "Bitte ein Projekt öffnen – dann zeigt das Programm, was in seinen Dateien geschützt würde."; return; }
        main.Editor.SaveIfOpen();
        var rows = PrivacyScanner.Detect(await ProjectTexts(), S);
        Detected.Clear();
        foreach (var r in rows) Detected.Add(new DetectedRow(r.Kind, r.Value, r.Count));
        OnPropertyChanged(nameof(HasDetected));
        Info = rows.Count == 0 ? "In den Dateien wurde nichts Schützenswertes gefunden." : $"{rows.Count} verschiedene Angaben würden geschützt.";
    }

    /// <summary>Namen mit einer KI auf diesem Rechner finden – der ungeschützte Text verlässt den Rechner dabei nicht.</summary>
    [RelayCommand]
    private async Task FindNamesLocally()
    {
        if (main.Project == null) { Info = "Bitte ein Projekt öffnen."; return; }
        var local = AppServices.Ai.Providers.FirstOrDefault(p => p.Kind == AiProviderKind.BuiltIn
                        && BuiltInAi.Models.FirstOrDefault(m => m.Id == p.Model) is { } m && BuiltInAi.IsInstalled(m))
                    ?? AppServices.Ai.Providers.FirstOrDefault(p => p.Kind == AiProviderKind.OpenAiCompatible && p.IsLocal);
        if (local == null)
        {
            Info = "Keine KI auf diesem Rechner eingerichtet (z. B. die kostenlose eingebaute KI unter „KI einrichten“). Ohne sie wirken nur die Muster und die Schutzliste.";
            return;
        }
        IsBusy = true;
        NameCandidates.Clear();
        OnPropertyChanged(nameof(HasNameCandidates));
        try
        {
            main.Editor.SaveIfOpen();
            var model = ChatModelFactory.CreateRaw(local);
            var texts = await ProjectTexts();
            var progress = new Progress<string>(m => Info = m);
            var names = await PrivacyScanner.FindNamesAsync(model, texts, progress);
            // gefundene und schon geschützte Namen gemeinsam einordnen – auch frühere Einträge lassen sich so wieder freigeben
            var all = names.Concat(S.Terms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0) { Info = "Keine Namen gefunden."; return; }
            var candidates = await PrivacyScanner.ClassifyNamesAsync(model, all, texts, AppServices.Ai.ExplanationLanguage, progress);
            foreach (var c in candidates.OrderByDescending(c => c.Protect).ThenBy(c => c.Kind).ThenBy(c => c.Name))
                NameCandidates.Add(new NameCandidateRow(c));
            OnPropertyChanged(nameof(HasNameCandidates));
            Info = L.T($"{candidates.Count} Namen eingeordnet ({local.Name}): {candidates.Count(c => c.Protect)} zum Schützen vorgeschlagen, " +
                       $"{candidates.Count(c => !c.Protect)} nicht (Produkte, Länder, Einrichtungen …). Bitte prüfen und übernehmen.");
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            Info = "Fehler: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Gefundene Namen zum Prüfen: angehakt = schützen.</summary>
    public ObservableCollection<NameCandidateRow> NameCandidates { get; } = new();
    public bool HasNameCandidates => NameCandidates.Count > 0;

    /// <summary>Angehakte in die Schutzliste, abgehakte aus ihr heraus (auch frühere Einträge); andere Einträge bleiben.</summary>
    [RelayCommand]
    private void ApplyNameCandidates()
    {
        var protect = NameCandidates.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        var release = NameCandidates.Where(r => !r.IsChecked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = S.Terms.Count;
        S.Terms = S.Terms.Where(t => !release.Contains(t)).Concat(protect).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
        OnPropertyChanged(nameof(TermsText));
        Info = L.T($"Schutzliste: {protect.Count} Namen geschützt, {release.Count} nicht – vorher {before}, jetzt {S.Terms.Count} Einträge.");
        NameCandidates.Clear();
        OnPropertyChanged(nameof(HasNameCandidates));
    }

    [RelayCommand]
    private void DiscardNameCandidates()
    {
        NameCandidates.Clear();
        OnPropertyChanged(nameof(HasNameCandidates));
        Info = "";
    }

    /// <summary>Vorhandene Einträge der beschreibbaren TMs des Projekts bereinigen.</summary>
    [RelayCommand]
    private void AnonymizeTms()
    {
        if (main.Project is not { } p) { Info = "Bitte ein Projekt öffnen."; return; }
        var total = 0;
        foreach (var tm in p.Tms.Where(t => !t.IsRemote && (t == p.ProjectTm || p.RefOf(t)?.WriteBack == true)))
            total += PrivacyScanner.Anonymize(tm, S);
        Info = $"{total} TM-Einträge anonymisiert.";
        main.Status = Info;
    }
}
