using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public partial class TmRowViewModel : ObservableObject
{
    public required long Id { get; init; }
    public required string Info { get; init; }
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
}

/// <summary>Eine empfohlene Datenbank in der Übersicht.</summary>
public sealed record RecommendationRow(DatabaseRecommendation Recommendation)
{
    public string Title => $"{L.T(DatabaseCatalog.KindName(Recommendation.Kind))}: {Recommendation.Name}";
    public string Reason => L.T(Recommendation.Reason);
    public string Path => Recommendation.Path;
}

/// <summary>Eine Datenbank in der Übersicht aller Datenbanken.</summary>
public sealed record DatabaseRow(DatabaseInfo Info)
{
    public string Kind => L.T(DatabaseCatalog.KindName(Info.Kind));
    public string Name => Info.Name;
    public string Origin => L.T(Info.Origin);
    public string Entries => Info.Entries < 0 ? "?" : Info.Entries.ToString("N0");
    public string Path => Info.Path;
    public override string ToString() => $"{Kind}: {Name}";
}

public sealed record AttachedRow(ResourceRef Ref, bool ForLanguages = false)
{
    public string Kind => L.T(ResourceFactory.KindName(Ref.Kind));
    public string Name => Ref.DisplayName;
    public string Languages => L.T(Ref.LanguagesText) + (ForLanguages ? " · " + L.T("für alle Projekte mit diesen Sprachen") : "");
    public string WriteBack => Ref.WriteBack ? "schreibt zurück" : "nur lesen";
}

public sealed record ConceptRow(Concept Concept)
{
    public long Id => Concept.Id;
    public string Terms => string.Join("  ·  ", Concept.Terms.Select(t => $"{t.Language}: {(t.Status == TermStatus.Forbidden ? "✗ " : "")}{t.Text}"));
    public string Definition => Concept.Definition ?? "";
}

/// <summary>Bereich „TM & Terminologie“.</summary>
public partial class ResourcesViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    public ResourcesViewModel(MainViewModel main)
    {
        _main = main;
        Alignment = new AlignmentViewModel(main);
        ImportTranslations = new ImportTranslationsViewModel(main);
        Harvest = new TermHarvestViewModel(main);
    }

    /// <summary>Alignment-Editor: alte Übersetzungen satzweise zuordnen und in eine TM übernehmen.</summary>
    public AlignmentViewModel Alignment { get; }

    /// <summary>Alte Übersetzungen übernehmen: Dateien wählen, zuordnen, ins TM, Fachbegriffe vorschlagen.</summary>
    public ImportTranslationsViewModel ImportTranslations { get; }

    /// <summary>Fachbegriffe vor dem Übersetzen aus den Projektdateien gewinnen.</summary>
    public TermHarvestViewModel Harvest { get; }

    public ObservableCollection<string> TmInfo { get; } = new();
    public ObservableCollection<string> TermbaseInfo { get; } = new();
    public ObservableCollection<ConceptRow> Concepts { get; } = new();

    [ObservableProperty] public partial string TermSearch { get; set; } = "";
    [ObservableProperty] public partial string NewSourceTerm { get; set; } = "";
    [ObservableProperty] public partial string NewTargetTerm { get; set; } = "";
    [ObservableProperty] public partial string NewTargetLanguage { get; set; } = "";
    [ObservableProperty] public partial string NewDefinition { get; set; } = "";
    [ObservableProperty] public partial bool NewTargetForbidden { get; set; }

    // Angebundene Ressourcen
    public ObservableCollection<AttachedRow> Attached { get; } = new();
    public ObservableCollection<string> RemoteNames { get; } = new();
    public IReadOnlyList<string> KindNames { get; } = new[] { "Translation Memory", "Terminologie", "Wissensdatenbank" }.Select(L.T).ToList();
    [ObservableProperty] public partial int AttachKind { get; set; }
    [ObservableProperty] public partial string? AttachServer { get; set; }
    [ObservableProperty] public partial string? AttachRemoteName { get; set; }
    [ObservableProperty] public partial string AttachLanguages { get; set; } = "";
    [ObservableProperty] public partial bool AttachWriteBack { get; set; } = true;

    // ---------- Datenbank-Übersicht ----------

    public ObservableCollection<RecommendationRow> Recommendations { get; } = new();
    public ObservableCollection<DatabaseRow> Databases { get; } = new();
    public ObservableCollection<DatabaseRow> MergeTargets { get; } = new();
    [ObservableProperty] public partial DatabaseRow? SelectedDatabase { get; set; }
    [ObservableProperty] public partial DatabaseRow? MergeTarget { get; set; }
    [ObservableProperty] public partial string DatabaseInfo { get; set; } = "";
    public string DatabaseFolder => DatabaseCatalog.Folder;

    public bool AutoCreateDatabases
    {
        get => AppServices.Ui.AutoCreateDatabases;
        set
        {
            AppServices.Ui.AutoCreateDatabases = value;
            AppServices.Ui.Save();
            OnPropertyChanged();
        }
    }

    partial void OnSelectedDatabaseChanged(DatabaseRow? value)
    {
        MergeTargets.Clear();
        if (value == null) return;
        foreach (var d in Databases.Where(d => d.Info.Kind == value.Info.Kind && d.Path != value.Path)) MergeTargets.Add(d);
        MergeTarget = MergeTargets.FirstOrDefault();
    }

    /// <summary>Nur die Empfehlungen (schnell, ohne Datenbanken zu öffnen).</summary>
    private void RefreshRecommendations()
    {
        Recommendations.Clear();
        if (_main.Project is { } p)
            foreach (var r in DatabaseCatalog.Recommend(p)) Recommendations.Add(new RecommendationRow(r));
        OnPropertyChanged(nameof(HasRecommendations));
    }

    [RelayCommand]
    public void RefreshDatabases()
    {
        RefreshRecommendations();
        var selected = SelectedDatabase?.Path;
        Databases.Clear();
        var folders = AppServices.Ui.RecentProjects.Where(f => _main.Project == null || !string.Equals(f, _main.Project.Folder, StringComparison.OrdinalIgnoreCase));
        foreach (var d in DatabaseCatalog.All(folders)) Databases.Add(new DatabaseRow(d));
        SelectedDatabase = Databases.FirstOrDefault(d => d.Path == selected) ?? Databases.FirstOrDefault();
        OnPropertyChanged(nameof(HasRecommendations));
    }

    public bool HasRecommendations => Recommendations.Count > 0;

    /// <summary>Text für die Meldung beim Öffnen eines Projekts (leer = nichts fehlt).</summary>
    public string MissingText => Recommendations.Count == 0 ? ""
        : $"Für dieses Projekt fehlen gemeinsame Datenbanken: {string.Join(", ", Recommendations.Select(r => r.Recommendation.Name))}. Ohne sie bleiben bestätigte Übersetzungen, Begriffe und Wissen nur in diesem Projekt.";

    /// <summary>Nach dem Öffnen eines Projekts: fehlende gemeinsame Datenbanken melden oder (auf Wunsch) gleich anlegen.</summary>
    public void CheckDatabases()
    {
        RefreshRecommendations();
        if (Recommendations.Count == 0 || !AutoCreateDatabases) return;
        CreateAllRecommended();
    }

    [RelayCommand]
    private void CreateRecommended(RecommendationRow? row)
    {
        if (row == null) return;
        DatabaseCatalog.Create(row.Recommendation, _main.Project);
        AfterDatabaseChange($"„{row.Recommendation.Name}“ angelegt: {row.Recommendation.Path}");
    }

    [RelayCommand]
    private void CreateAllRecommended()
    {
        var created = Recommendations.Select(r => r.Recommendation).ToList();
        if (created.Count == 0) return;
        foreach (var r in created) DatabaseCatalog.Create(r, _main.Project);
        AfterDatabaseChange($"Angelegt und an die Sprachen gebunden: {string.Join(", ", created.Select(r => r.Name))} – im Ordner {DatabaseCatalog.Folder}");
    }

    /// <summary>Eine Datenbank (auch die eines anderen Projekts) im aktuellen Projekt verwenden.</summary>
    [RelayCommand]
    private void UseInProject(DatabaseRow? row)
    {
        row ??= SelectedDatabase;
        if (row == null) return;
        if (_main.Project is not { } p) { DatabaseInfo = "Bitte zuerst ein Projekt öffnen."; return; }
        // Datenbanken anderer Projekte nur lesen – geschrieben wird weiter in deren Projekt
        p.Attach(new ResourceRef { Kind = row.Info.Kind, Location = row.Path, WriteBack = row.Info.ProjectFolder == null });
        AfterDatabaseChange($"„{row.Name}“ wird jetzt in diesem Projekt verwendet.");
    }

    /// <summary>Eine Datenbank für alle Projekte mit den Sprachen des aktuellen Projekts (bzw. den angegebenen) freigeben.</summary>
    [RelayCommand]
    private void ShareForLanguages(DatabaseRow? row)
    {
        row ??= SelectedDatabase;
        if (row == null) return;
        var reference = new ResourceRef { Kind = row.Info.Kind, Location = row.Path, Languages = ParseLanguages(), WriteBack = row.Info.ProjectFolder == null };
        if (_main.Project is { } p) p.AttachForLanguages(reference);
        else if (reference.Languages.Count == 0) { DatabaseInfo = "Ohne geöffnetes Projekt bitte die Sprachen angeben (Feld „Sprachen“ unter „Ressource anbinden“)."; return; }
        else LanguageResources.Add(reference);
        AfterDatabaseChange($"„{row.Name}“ gilt jetzt für alle Projekte mit {string.Join(", ", reference.Languages)}.");
    }

    [RelayCommand]
    private void MergeDatabases()
    {
        if (SelectedDatabase is not { } source || MergeTarget is not { } target) { DatabaseInfo = "Bitte Quelle und Ziel gleichen Typs wählen."; return; }
        try
        {
            var added = DatabaseCatalog.Merge(source.Path, target.Path);
            AfterDatabaseChange($"{added} Einträge aus „{source.Name}“ in „{target.Name}“ übernommen (schon vorhandene übersprungen).");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            DatabaseInfo = "Zusammenführen nicht möglich: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenDatabaseFolder(DatabaseRow? row)
    {
        var folder = row != null ? System.IO.Path.GetDirectoryName(row.Path)! : DatabaseCatalog.Folder;
        Directory.CreateDirectory(folder);
        if (AppServices.Platform != null) await AppServices.Platform.OpenFileAsync(folder);
    }

    private void AfterDatabaseChange(string message)
    {
        if (_main.Project is { } p) AfterAttach(p);
        RefreshDatabases();
        _main.Knowledge.OnProjectChanged();
        DatabaseInfo = message;
        _main.Status = message;
        _main.DatabaseHint = MissingText;
    }

    /// <summary>Nicht ans Projekt, sondern an die Sprachen binden: gilt dann in jedem Projekt mit diesen Sprachen.</summary>
    [ObservableProperty] public partial bool AttachForLanguages { get; set; }

    private void Bind(Core.Projects.TranslationProject p, ResourceRef r)
    {
        if (AttachForLanguages) p.AttachForLanguages(r);
        else p.Attach(r);
    }
    [ObservableProperty] public partial string ResourceErrors { get; set; } = "";
    public IEnumerable<string> ServerNames => AppServices.Servers.Servers.Select(s => s.Name);

    // TM-Pflege
    public ObservableCollection<TmRowViewModel> TmRows { get; } = new();
    [ObservableProperty] public partial string TmFilter { get; set; } = "";
    [ObservableProperty] public partial int TmPage { get; set; }
    private const int PageSize = 50;


    public void Refresh()
    {
        TmInfo.Clear();
        TermbaseInfo.Clear();
        RefreshLibrary();                                                // eigene Fachtexte gelten ohne Projekt
        var p = _main.Project;
        if (p == null) return;
        foreach (var tm in p.Tms) TmInfo.Add(SafeInfo(() => $"{tm.Name}: {tm.Count()} Einheiten  ({tm.Location})", tm.Name));
        foreach (var tb in p.Termbases) TermbaseInfo.Add(SafeInfo(() => $"{tb.Name}: {tb.ConceptCount()} Einträge  ({tb.Location})", tb.Name));
        Attached.Clear();
        foreach (var r in p.Resources) Attached.Add(new AttachedRow(r));
        foreach (var r in p.LanguageRefs) Attached.Add(new AttachedRow(r, true));
        ResourceErrors = string.Join("\n", p.ResourceErrors);
        OnPropertyChanged(nameof(ServerNames));
        if (NewTargetLanguage.Length == 0) NewTargetLanguage = p.TargetLanguages.FirstOrDefault() ?? "";
        // Sprachen der freien Sprachdaten: beim Projektwechsel die des Projekts vorschlagen, sonst die Wahl behalten
        if (_dataProject != p.Folder)
        {
            _dataProject = p.Folder;
            DataSourceLanguage = p.SourceLanguage;
            DataTargetLanguage = p.TargetLanguages.FirstOrDefault() ?? DataTargetLanguage;
        }
        SearchTerms();
        RefreshMaintTms();
        LoadTmPage();
    }

    partial void OnTermSearchChanged(string value) => SearchTerms();

    private void SearchTerms()
    {
        Concepts.Clear();
        var p = _main.Project;
        if (p == null) return;
        var list = TermSearch.Trim().Length == 0
            ? p.ProjectTermbase.AllConcepts().TakeLast(200)
            : p.Termbases.SelectMany(tb => p.TargetLanguages.Append(p.SourceLanguage).SelectMany(l => tb.Lookup(l, TermSearch)))
                .DistinctBy(c => c.Id);
        foreach (var c in list) Concepts.Add(new ConceptRow(c));
    }

    [RelayCommand]
    private void AddConcept()
    {
        var p = _main.Project;
        if (p == null || NewSourceTerm.Trim().Length == 0) return;
        p.ProjectTermbase.AddConcept([
            (p.SourceLanguage, NewSourceTerm.Trim(), TermStatus.Preferred),
            (NewTargetLanguage.Trim(), NewTargetTerm.Trim(), NewTargetForbidden ? TermStatus.Forbidden : TermStatus.Preferred)],
            NewDefinition.Trim().Length > 0 ? NewDefinition.Trim() : null, p.Topic);
        NewSourceTerm = NewTargetTerm = NewDefinition = "";
        NewTargetForbidden = false;
        Refresh();
    }

    [RelayCommand]
    private void DeleteConcept(ConceptRow? row)
    {
        if (row == null || _main.Project == null) return;
        _main.Project.ProjectTermbase.DeleteConcept(row.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportTmx()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("TMX importieren", ["*.tmx"], true);
        await _main.RunBusy("Importiere TMX …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                await using var s = await f.OpenRead();
                var buffer = new MemoryStream();
                await s.CopyToAsync(buffer);
                buffer.Position = 0;
                total += (await Task.Run(() => TmxIo.Import(buffer, p.ProjectTm))).Imported;
            }
            Refresh();
            _main.Status = $"{total} Übersetzungseinheiten ins Projekt-TM importiert.";
        });
    }

    [RelayCommand]
    private async Task ExportTmx()
    {
        var p = _main.Project;
        if (p == null) return;
        await using var output = await AppServices.Platform!.SaveFileAsync("TMX exportieren", p.Name + ".tmx");
        if (output == null) return;
        var buffer = new MemoryStream();
        var n = await Task.Run(() => TmxIo.Export(p.ProjectTm, buffer));
        buffer.Position = 0;
        await buffer.CopyToAsync(output);
        _main.Status = $"{n} Einheiten exportiert.";
    }

    [RelayCommand]
    private async Task ImportTbx()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("Terminologie importieren (TBX / Terminologie-XML)", ["*.tbx", "*.xml", "*.mtf"], true);
        await _main.RunBusy("Importiere Terminologie …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                await using var s = await f.OpenRead();
                var buffer = new MemoryStream();
                await s.CopyToAsync(buffer);
                buffer.Position = 0;
                total += await Task.Run(() => TbxIo.Import(buffer, p.ProjectTermbase));
            }
            Refresh();
            _main.Status = $"{total} Einträge importiert.";
        });
    }

    [RelayCommand]
    private async Task ExportTbx()
    {
        var p = _main.Project;
        if (p == null) return;
        await using var output = await AppServices.Platform!.SaveFileAsync("TBX exportieren", p.Name + ".tbx");
        if (output == null) return;
        var buffer = new MemoryStream();
        var n = await Task.Run(() => TbxIo.Export(p.ProjectTermbase, buffer));
        buffer.Position = 0;
        await buffer.CopyToAsync(output);
        _main.Status = $"{n} Einträge exportiert.";
    }

    private static string SafeInfo(Func<string> f, string name)
    {
        try { return f(); }
        catch (RemoteException ex) { return $"{name}: {ex.Message}"; }
    }

    // ---------- Ressourcen anbinden ----------

    partial void OnAttachServerChanged(string? value) => _ = LoadRemoteNames();
    partial void OnAttachKindChanged(int value) => _ = LoadRemoteNames();

    private async Task LoadRemoteNames()
    {
        RemoteNames.Clear();
        var account = AttachServer == null ? null : AppServices.Servers.Get(AttachServer);
        if (account == null) return;
        try
        {
            foreach (var r in (await ResourceFactory.Api(account).ResourcesAsync()).Where(r => (int)r.Kind == AttachKind))
                RemoteNames.Add(r.Name);
            AttachRemoteName = RemoteNames.FirstOrDefault();
        }
        catch (RemoteException ex) { _main.Status = ex.Message; }
    }

    private List<string> ParseLanguages() =>
        AttachLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    [RelayCommand]
    private void AttachRemote()
    {
        var p = _main.Project;
        if (p == null || AttachServer == null || string.IsNullOrEmpty(AttachRemoteName)) { _main.Status = "Bitte Server und Ressource wählen."; return; }
        p.Attach(new ResourceRef
        {
            Kind = (ResourceKind)AttachKind, Server = AttachServer, Location = AttachRemoteName,
            Languages = ParseLanguages(), WriteBack = AttachWriteBack,
        });
        AfterAttach(p);
    }

    [RelayCommand]
    private async Task AttachLocal()
    {
        var p = _main.Project;
        if (p == null) return;
        var ext = ResourceFactory.Extension((ResourceKind)AttachKind);
        var folder = await AppServices.Platform!.PickFolderAsync($"Ordner mit {ext}-Dateien wählen");
        if (folder == null) return;
        foreach (var f in Directory.GetFiles(folder, "*" + ext))
            Bind(p, new ResourceRef { Kind = (ResourceKind)AttachKind, Location = f, Languages = ParseLanguages(), WriteBack = AttachWriteBack });
        AfterAttach(p);
    }

    [RelayCommand]
    private void CreateLocal()
    {
        var p = _main.Project;
        if (p == null) return;
        var kind = (ResourceKind)AttachKind;
        var name = AttachRemoteName is { Length: > 0 } n ? n : "Gemeinsam";
        var path = Path.Combine(Core.Ai.AppPaths.DataRoot, "ressourcen", name + ResourceFactory.Extension(kind));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Datei anlegen, indem die Ressource einmal geöffnet wird
        if (!File.Exists(path))
        {
            ILanguageResource r = kind switch
            {
                ResourceKind.Tm => new TranslationMemory(path),
                ResourceKind.Termbase => new Core.Terminology.Termbase(path),
                _ => new Core.Knowledge.KnowledgeBase(path),
            };
            r.Dispose();
        }
        Bind(p, new ResourceRef { Kind = kind, Location = path, Languages = ParseLanguages(), WriteBack = AttachWriteBack });
        AfterAttach(p);
    }

    [RelayCommand]
    private void Detach(AttachedRow? row)
    {
        var p = _main.Project;
        if (p == null || row == null) return;
        p.Detach(row.Ref);
        AfterAttach(p);
    }

    private void AfterAttach(Core.Projects.TranslationProject p)
    {
        Refresh();
        _main.Knowledge.OnProjectChanged();
        _main.Status = p.ResourceErrors.Count > 0 ? "Nicht erreichbar: " + string.Join("; ", p.ResourceErrors) : "Ressourcen aktualisiert.";
    }

    // ---------- Freie Sprachdaten ----------

    private List<Core.Resources.LanguageDataSet> _allDataSets = new();
    private string? _dataProject;
    public ObservableCollection<Core.Resources.LanguageDataSet> DataSets { get; } = new();
    [ObservableProperty] public partial Core.Resources.LanguageDataSet? SelectedDataSet { get; set; }
    [ObservableProperty] public partial bool ShowAllDataSets { get; set; }

    /// <summary>Frei wählbares Sprachpaar (vorgeschlagen: das des Projekts; geht auch ohne Projekt).</summary>
    [ObservableProperty] public partial string? DataSourceLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string? DataTargetLanguage { get; set; } = "en-GB";
    [ObservableProperty] public partial string DataStatus { get; set; } = "";

    // Filter
    [ObservableProperty] public partial string DataFilterText { get; set; } = "";
    [ObservableProperty] public partial int DataKindIndex { get; set; }
    [ObservableProperty] public partial int DataCategoryIndex { get; set; }
    [ObservableProperty] public partial bool DataCommercialOnly { get; set; }
    [ObservableProperty] public partial int DataSortIndex { get; set; }
    [ObservableProperty] public partial string DataCountText { get; set; } = "";

    /// <summary>Für alle Projekte mit diesem Sprachpaar anbinden (sonst nur für das offene Projekt).</summary>
    [ObservableProperty] public partial bool DataForLanguages { get; set; } = true;

    public IReadOnlyList<string> DataKindNames { get; } = new[] { "Alle Arten", "Translation Memory", "Terminologie" }.Select(L.T).ToList();
    public IReadOnlyList<string> DataCategoryNames { get; } = new[] { "Alle Fachgebiete" }.Concat(Core.Resources.LanguageData.Categories).Select(L.T).ToList();
    public IReadOnlyList<string> DataSortNames { get; } = new[] { "Empfohlene zuerst", "Größte zuerst", "Kleinste zuerst", "Name" }.Select(L.T).ToList();
    public bool HasDataSets => _allDataSets.Count > 0;
    public AiDownloadService Download => AppServices.AiDownload;

    public string DataFolder => AppServices.Ui.LanguageDataFolder is { Length: > 0 } f ? f : Core.Resources.LanguageData.DefaultFolder;

    public string DataInfo
    {
        get
        {
            if (SelectedDataSet is not { } d) return "";
            var free = Core.Ai.BuiltInAi.FreeSpace(DataFolder) is { } f ? Core.Ai.BuiltInAi.FormatSize(f) : "?";
            return $"{d.Name}: {d.Description}. Lizenz: {d.License}. Platzbedarf etwa {d.DiskNeedText} (frei: {free}).";
        }
    }

    partial void OnSelectedDataSetChanged(Core.Resources.LanguageDataSet? value) => OnPropertyChanged(nameof(DataInfo));
    partial void OnShowAllDataSetsChanged(bool value) => FilterDataSets();
    partial void OnDataFilterTextChanged(string value) => FilterDataSets();
    partial void OnDataKindIndexChanged(int value) => FilterDataSets();
    partial void OnDataCategoryIndexChanged(int value) => FilterDataSets();
    partial void OnDataCommercialOnlyChanged(bool value) => FilterDataSets();
    partial void OnDataSortIndexChanged(int value) => FilterDataSets();

    /// <summary>Gefundene Datensätze nach Suchtext, Art, Fachgebiet, Lizenz und „alle anzeigen“ filtern und sortieren.</summary>
    private void FilterDataSets()
    {
        var selected = SelectedDataSet;
        var text = DataFilterText.Trim();
        var category = DataCategoryIndex > 0 ? Core.Resources.LanguageData.Categories[DataCategoryIndex - 1] : null;
        var shown = _allDataSets.Where(d => ShowAllDataSets || d.Recommended)
            .Where(d => text.Length == 0 || d.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                        || d.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                        || L.T(d.Description).Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Where(d => DataKindIndex == 0 || d.Kind == (DataKindIndex == 1 ? ResourceKind.Tm : ResourceKind.Termbase))
            .Where(d => category == null || d.Category == category)
            .Where(d => !DataCommercialOnly || d.CommercialUse);
        shown = DataSortIndex switch
        {
            1 => shown.OrderByDescending(d => d.Units).ThenByDescending(d => d.Size),
            2 => shown.OrderBy(d => d.Units == 0 ? long.MaxValue : d.Units),
            3 => shown.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => shown,                                                  // Reihenfolge der Suche: empfohlene zuerst
        };
        DataSets.Clear();
        foreach (var d in shown) DataSets.Add(d);
        SelectedDataSet = DataSets.Contains(selected!) ? selected : DataSets.FirstOrDefault();
        var hidden = _allDataSets.Count(d => !d.Recommended);
        DataCountText = _allDataSets.Count == 0 ? "" :
            $"{DataSets.Count} von {_allDataSets.Count} Datensätzen angezeigt" + (!ShowAllDataSets && hidden > 0 ? $" ({hidden} weitere unter „Alle anzeigen“)." : ".");
        OnPropertyChanged(nameof(HasDataSets));
    }

    [RelayCommand]
    private async Task OpenDataSetInfo(Core.Resources.LanguageDataSet? set)
    {
        set ??= SelectedDataSet;
        if (set != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(set.InfoUrl);
    }

    [RelayCommand]
    private async Task SearchDataSets()
    {
        var source = DataSourceLanguage?.Trim();
        var target = DataTargetLanguage?.Trim();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) { DataStatus = "Bitte Ausgangs- und Zielsprache wählen."; return; }
        DataStatus = $"Suche freie Sprachdaten für {source} → {target} …";
        try
        {
            _allDataSets = await Core.Resources.LanguageData.SearchAsync(source, target);
            FilterDataSets();
            DataStatus = $"{_allDataSets.Count(d => d.Recommended)} empfohlene von {_allDataSets.Count} Datensätzen für {source} → {target}.";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            DataStatus = "Fehler: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ChooseDataFolder()
    {
        if (AppServices.Platform == null) return;
        var folder = await AppServices.Platform.PickFolderAsync("Ordner für freie Sprachdaten (große Datenbanken)");
        if (folder == null) return;
        AppServices.Ui.LanguageDataFolder = folder;
        AppServices.Ui.Save();
        OnPropertyChanged(nameof(DataFolder));
        OnPropertyChanged(nameof(DataInfo));
    }

    /// <summary>
    /// Gewählten Datensatz herunterladen, nur das Sprachpaar übernehmen und anbinden: für alle Projekte mit diesen
    /// Sprachen (Vorgabe) oder nur für das offene Projekt. Ohne Projekt immer für die Sprachen.
    /// </summary>
    [RelayCommand]
    private async Task InstallDataSet()
    {
        var p = _main.Project;
        if (SelectedDataSet is not { } set || DataSourceLanguage is not { Length: > 0 } source || DataTargetLanguage is not { Length: > 0 } target) return;
        Core.Resources.LanguageDataResult? result = null;
        var ok = await AppServices.AiDownload.Run(set.Name, async (progress, ct) =>
            result = await Core.Resources.LanguageData.InstallAsync(set, source, target, DataFolder, progress, ct));
        if (!ok || result == null)
        {
            DataStatus = AppServices.AiDownload.Text;
            return;
        }
        var reference = new ResourceRef { Kind = set.Kind, Location = result.Path, WriteBack = false, Languages = [source, target] };
        if (p != null && !DataForLanguages) p.Attach(reference);
        else if (p != null) p.AttachForLanguages(reference);
        else LanguageResources.Add(reference);
        if (p != null) AfterAttach(p);
        RefreshDatabases();
        DataStatus = (p != null && !DataForLanguages
            ? $"{set.Name}: {result.Count:N0} Einträge übernommen und an das Projekt angebunden."
            : $"{set.Name}: {result.Count:N0} Einträge übernommen – gilt in jedem Projekt {source} → {target}.");
        _main.Status = DataStatus;
    }

    // ---------- TM-Pflege ----------

    /// <summary>Welche TM bearbeitet wird: Projekt-TM und die beschreibbaren angebundenen.</summary>
    public ObservableCollection<TmChoice> MaintTms { get; } = new();
    [ObservableProperty] public partial TmChoice? MaintTm { get; set; }
    private ITranslationMemory? CurrentTm => MaintTm?.Instance ?? _main.Project?.ProjectTm;

    private void RefreshMaintTms()
    {
        var selected = MaintTm?.Name;
        MaintTms.Clear();
        if (_main.Project is not { } p) return;
        foreach (var tm in p.Tms.Where(t => t == p.ProjectTm || p.RefOf(t)?.WriteBack == true))
            MaintTms.Add(new TmChoice(tm == p.ProjectTm ? L.T("Projekt-TM") : tm.IsRemote ? tm.Name : System.IO.Path.GetFileNameWithoutExtension(tm.Location), tm, null));
        MaintTm = MaintTms.FirstOrDefault(t => t.Name == selected) ?? MaintTms.FirstOrDefault();
    }

    partial void OnMaintTmChanged(TmChoice? value) { TmPage = 0; LoadTmPage(); }

    [ObservableProperty] public partial string FindText { get; set; } = "";
    [ObservableProperty] public partial string ReplaceText { get; set; } = "";
    [ObservableProperty] public partial bool ReplaceInSource { get; set; }
    [ObservableProperty] public partial bool ReplaceInTarget { get; set; } = true;
    [ObservableProperty] public partial bool ReplaceMatchCase { get; set; }
    [ObservableProperty] public partial bool ReplaceWholeWords { get; set; }
    [ObservableProperty] public partial bool ReplaceRegex { get; set; }
    [ObservableProperty] public partial string TmCareInfo { get; set; } = "";
    [ObservableProperty] public partial bool CleanDuplicates { get; set; } = true;
    [ObservableProperty] public partial bool CleanEmpty { get; set; } = true;
    [ObservableProperty] public partial bool CleanIdentical { get; set; }

    private void TmCare(Action<ITranslationMemory> work)
    {
        if (CurrentTm is not { } tm) return;
        try { work(tm); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { TmCareInfo = "Fehler: " + ex.Message; }
    }

    [RelayCommand]
    private void PreviewReplace() => TmCare(tm =>
    {
        var changes = TmMaintenance.Preview(tm, FindText, ReplaceText, ReplaceInSource, ReplaceInTarget, ReplaceMatchCase, ReplaceRegex, ReplaceWholeWords);
        TmRows.Clear();
        foreach (var c in changes.Take(PageSize))
            TmRows.Add(new TmRowViewModel { Id = c.Unit.Id, Source = c.Source, Target = c.Target, Info = L.T("Vorschau – vorher: ") + c.Unit.Target });
        TmCareInfo = changes.Count == 0 ? "Nichts gefunden." : $"{changes.Count} Einträge würden geändert (Vorschau unten; „Speichern“ übernimmt einzeln, „Alle ersetzen“ alle).";
    });

    [RelayCommand]
    private void ReplaceAll() => TmCare(tm =>
    {
        var n = TmMaintenance.ReplaceAll(tm, FindText, ReplaceText, ReplaceInSource, ReplaceInTarget, ReplaceMatchCase, ReplaceRegex, ReplaceWholeWords);
        TmCareInfo = $"{n} Einträge geändert.";
        _main.Status = TmCareInfo;
        LoadTmPage();
    });

    [RelayCommand]
    private void CleanTm() => TmCare(tm =>
    {
        var r = TmMaintenance.Clean(tm, CleanDuplicates, CleanEmpty, CleanIdentical);
        TmCareInfo = $"Bereinigt: {r.Duplicates} Dubletten, {r.EmptyTargets} ohne Übersetzung, {r.Identical} mit Ziel = Quelle gelöscht.";
        _main.Status = TmCareInfo;
        Refresh();
    });

    [RelayCommand]
    private void ShowInconsistent() => TmCare(tm =>
    {
        var groups = TmMaintenance.Inconsistent(tm, PageSize);
        TmRows.Clear();
        foreach (var (_, units) in groups)
            foreach (var u in units)
                TmRows.Add(new TmRowViewModel { Id = u.Id, Source = u.Source, Target = u.Target, Info = $"{L.T("uneinheitlich")}: {units.Count} · {u.Modified.ToLocalTime():g} · {u.Origin}" });
        TmCareInfo = groups.Count == 0 ? "Alle Ausgangssätze sind einheitlich übersetzt." : $"{groups.Count} Ausgangssätze mit verschiedenen Übersetzungen – vereinheitlichen oder überholte löschen.";
    });

    partial void OnTmFilterChanged(string value) { TmPage = 0; LoadTmPage(); }

    private void LoadTmPage()
    {
        TmRows.Clear();
        if (CurrentTm is not { } tm) return;
        foreach (var u in tm.Browse(TmFilter, TmPage * PageSize, PageSize))
            TmRows.Add(new TmRowViewModel
            {
                Id = u.Id, Source = u.Source, Target = u.Target,
                Info = $"{u.SourceLanguage} → {u.TargetLanguage} · {u.Modified.ToLocalTime():g} · {u.Origin}",
            });
    }

    [RelayCommand]
    private void NextTmPage() { if (TmRows.Count == PageSize) { TmPage++; LoadTmPage(); } }

    [RelayCommand]
    private void PreviousTmPage() { if (TmPage > 0) { TmPage--; LoadTmPage(); } }

    [RelayCommand]
    private void SaveTmRow(TmRowViewModel? row)
    {
        if (row == null || CurrentTm is not { } tm) return;
        tm.Update(row.Id, row.Source, row.Target);
        _main.Status = "TM-Eintrag gespeichert.";
    }

    [RelayCommand]
    private void DeleteTmRow(TmRowViewModel? row)
    {
        if (row == null || CurrentTm is not { } tm) return;
        tm.Delete(row.Id);
        TmRows.Remove(row);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportSdltm()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("TM-Datei (.sdltm) übernehmen", ["*.sdltm"], true);
        await _main.RunBusy("Übernehme TM …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdltm");
                await using (var s = await f.OpenRead())
                await using (var fs = File.Create(tmp))
                    await s.CopyToAsync(fs);
                try { total += await Task.Run(() => SdltmImporter.Import(tmp, p.ProjectTm)); }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(tmp); }
            }
            Refresh();
            _main.Status = $"{total} Übersetzungseinheiten übernommen.";
        });
    }

    // ---------- Alignment: eigener Editor (AlignmentViewModel) ----------

    /// <summary>Vorhandene TM-/Terminologie-Datei (z. B. ein Haupt-TM) zusätzlich einbinden – nur am Desktop mit echtem Pfad.</summary>
    [RelayCommand]
    private async Task AttachTm()
    {
        var p = _main.Project;
        if (p == null) return;
        var folder = await AppServices.Platform!.PickFolderAsync("Ordner mit .sctm/.sctb-Dateien wählen");
        if (folder == null) return;
        foreach (var f in Directory.GetFiles(folder, "*.sctm")) p.AttachTm(f);
        foreach (var f in Directory.GetFiles(folder, "*.sctb")) p.AttachTermbase(f);
        Refresh();
    }
}
