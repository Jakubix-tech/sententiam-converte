using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Filters;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// „Erste Schritte“ für neue Benutzer im Reiter Projekt: 1. KI wählen (kostenlos mit einem Klick), 2. Projekt mit
/// Sprachen und Dateien anlegen (vertraulich ja/nein, gemeinsame Datenbanken gleich mit), 3. alte Übersetzungen
/// übernehmen (freiwillig), 4. übersetzen. Jeder Schritt zeigt, ob er erledigt ist; ausblenden geht jederzeit.
/// </summary>
public partial class GettingStartedViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly List<PickedFile> _files = new();

    public GettingStartedViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += OnMainChanged;
        main.Files.CollectionChanged += (_, _) => Refresh();
    }

    private AiSetupViewModel Setup => _main.Settings.Setup;
    private bool _subscribed;

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Project) or nameof(MainViewModel.SelectedTab)) Refresh();
    }

    public void Refresh()
    {
        if (!_subscribed && _main.Settings != null)
        {
            Setup.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AiSetupViewModel.PoolStatus) or nameof(AiSetupViewModel.IsPoolBusy)) Refresh();
            };
            _subscribed = true;
        }
        OnPropertyChanged(string.Empty);
    }

    public bool IsVisible => !AppServices.Ui.HideGettingStarted;

    // ---------- 1. KI ----------

    private AiProviderConfig? Provider => AppServices.Ai.Get(AppServices.Ai.TranslateProviderId);

    public bool AiDone => Provider is { } p && (p.Kind != AiProviderKind.BuiltIn || BuiltInAi.Model(p.Model) is { } m && BuiltInAi.IsInstalled(m));
    public bool AiBusy => _main.Settings != null && Setup.IsPoolBusy;

    public string AiStatus => AiBusy ? Setup.PoolStatus
        : AiDone ? $"✔ {Provider!.Name}" + (Setup.PoolStatus.Length > 0 ? "\n" + Setup.PoolStatus : "")
        : "Noch keine KI gewählt. Am einfachsten: die kostenlosen Online-Modelle – mehrere arbeiten gleichzeitig, ohne Konto. Für vertrauliche Texte gibt es die eingebaute KI, die offline auf diesem Rechner läuft.";

    [RelayCommand]
    private async Task UseFreeAi()
    {
        Setup.UseForAll = true;
        await Setup.UseFreePoolCommand.ExecuteAsync(null);
        _main.Editor.CheckAi();
        Refresh();
    }

    [RelayCommand]
    private void OpenAiPage() => _main.SelectedTab = MainViewModel.AiTab;

    // ---------- 2. Projekt ----------

    public bool ProjectDone => _main.Project != null;

    public string ProjectStatus => _main.Project is { } p
        ? $"✔ „{p.Name}“ ({p.SourceLanguage} → {string.Join(", ", p.TargetLanguages)}) · {_main.Files.Count} Datei(en)"
        : "";

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Source { get; set; } = "de-DE";
    [ObservableProperty] public partial string Targets { get; set; } = "en-GB";
    [ObservableProperty] public partial string Topic { get; set; } = "";
    [ObservableProperty] public partial bool Confidential { get; set; }
    [ObservableProperty] public partial bool CreateDatabases { get; set; } = true;
    public ObservableCollection<string> FileNames { get; } = new();
    public ObservableCollection<string> LanguageCodes => _main.LanguageCodes;

    [RelayCommand]
    private async Task PickFiles()
    {
        var patterns = FileFilters.SupportedExtensions.Select(e => "*" + e).ToList();
        var picked = await AppServices.Platform!.PickFilesAsync("Dateien zum Übersetzen wählen", patterns, true);
        foreach (var f in picked.Where(f => _files.All(x => x.Name != f.Name)))
        {
            _files.Add(f);
            FileNames.Add(f.Name);
        }
        if (Name.Trim().Length == 0 && _files.Count > 0) Name = System.IO.Path.GetFileNameWithoutExtension(_files[0].Name);
    }

    /// <summary>Alles in einem Schritt: Projekt anlegen, Datenschutz, Dateien, gemeinsame Datenbanken.</summary>
    [RelayCommand]
    private async Task CreateProject()
    {
        if (Name.Trim().Length == 0) { _main.Status = "Bitte einen Projektnamen eingeben (oder zuerst Dateien wählen)."; return; }
        _main.NewName = Name.Trim();
        _main.NewSource = Source.Trim();
        _main.NewTargets = Targets;
        _main.NewTopic = Topic.Trim();
        _main.NewOnServer = false;
        await _main.CreateProjectCommand.ExecuteAsync(null);
        if (_main.Project is not { } p || p.Name != Name.Trim()) return;
        p.Privacy.Asked = true;                                  // hier schon beantwortet
        if (Confidential) _main.Privacy.Enabled = true;
        else p.Save();
        if (CreateDatabases) _main.Resources.CreateAllRecommendedCommand.Execute(null);
        if (_files.Count > 0) await _main.AddPickedFilesAsync(_files.ToList());
        _files.Clear();
        FileNames.Clear();
        Name = Topic = "";
        _main.Status = _main.Files.Count > 0
            ? $"Projekt „{p.Name}“ mit {_main.Files.Count} Datei(en) angelegt. Weiter mit Schritt 3 oder 4."
            : $"Projekt „{p.Name}“ angelegt. Jetzt Dateien hinzufügen.";
        Refresh();
    }

    [RelayCommand]
    private Task AddFiles() => _main.AddFilesCommand.ExecuteAsync(null);

    // ---------- 3. alte Übersetzungen ----------

    [RelayCommand]
    private void ImportOld() => _main.OpenImportTranslationsCommand.Execute(null);

    // ---------- 4. übersetzen ----------

    public bool CanTranslate => _main.Files.Count > 0;

    [RelayCommand]
    private async Task Translate()
    {
        if ((_main.Files.FirstOrDefault(f => f.Percent < 100) ?? _main.Files.FirstOrDefault()) is not { } row) return;
        await _main.OpenInEditorCommand.ExecuteAsync(row);
    }

    // ---------- ein- und ausblenden ----------

    public string Progress => $"{new[] { AiDone, ProjectDone, CanTranslate }.Count(x => x)} von 3 erledigt";

    [RelayCommand]
    private void Hide()
    {
        AppServices.Ui.HideGettingStarted = true;
        AppServices.Ui.Save();
        OnPropertyChanged(nameof(IsVisible));
    }

    [RelayCommand]
    public void Show()
    {
        AppServices.Ui.HideGettingStarted = false;
        AppServices.Ui.Save();
        _main.SelectedTab = 0;
        Refresh();
    }
}
