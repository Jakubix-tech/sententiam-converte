using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Ai.LocalMt;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// „KI einrichten“ in wenigen Klicks: die eingebaute KI (offenes Modell auf diesem Rechner, wird heruntergeladen),
/// kostenlos über OpenCode (wird bei Bedarf ebenfalls heruntergeladen), ein lokaler Server (wird automatisch gefunden)
/// oder die kostenlose Stufe eines Cloud-Anbieters. Jeder Weg legt einen Zugang an, nutzt ihn auf Wunsch für alle
/// Aufgaben und testet ihn sofort.
/// </summary>
/// <summary>Ein kostenloses Modell in der Auswahl für den Verbund.</summary>
public partial class PoolCandidateViewModel(PoolCandidate candidate) : ObservableObject
{
    public PoolCandidate Candidate { get; } = candidate;
    public string Service => Candidate.Service;
    public string Model => Candidate.Config.Model;
    public string? Note => Candidate.Note;
    public string Key => Candidate.Config.Kind + "|" + Candidate.Config.BaseUrl.TrimEnd('/') + "|" + Candidate.Config.Model;
    [ObservableProperty] public partial bool IsChecked { get; set; } = candidate.Recommended;
    [ObservableProperty] public partial string Result { get; set; } = "";
    public bool Works { get; set; }
}

public partial class AiSetupViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;

    public AiSetupViewModel(SettingsViewModel settings)
    {
        _settings = settings;
        IsOpenCodeInstalled = OpenCodeServer.IsInstalled;
        SelectedService = Services.FirstOrDefault();
        SelectedBuiltInModel = BuiltInModels.FirstOrDefault();
        Download.Completed += RefreshBuiltIn;
        Download.Completed += RefreshLocalMt;
        RefreshLocalMt();
    }

    [ObservableProperty] public partial bool UseForAll { get; set; } = true;
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    // ---------- Eingebaute KI ----------

    public AiDownloadService Download => AppServices.AiDownload;
    public bool IsBuiltInSupported => BuiltInAi.IsSupported;
    public System.Collections.Generic.IReadOnlyList<BuiltInModel> BuiltInModels => BuiltInAi.Models;
    [ObservableProperty] public partial BuiltInModel? SelectedBuiltInModel { get; set; }
    public string BuiltInFolder => BuiltInAi.Folder;
    public bool IsSelectedInstalled => SelectedBuiltInModel is { } m && BuiltInAi.IsInstalled(m);
    public string BuiltInButton => IsSelectedInstalled ? "Verwenden" : "Herunterladen und verwenden";

    public string BuiltInStatus
    {
        get
        {
            if (SelectedBuiltInModel is not { } m) return "";
            if (BuiltInAi.IsInstalled(m)) return "Heruntergeladen – bereit.";
            var free = BuiltInAi.FreeSpace() is { } f ? BuiltInAi.FormatSize(f) : "?";
            return $"Einmaliger Download: {BuiltInAi.FormatSize(BuiltInAi.MissingBytes(m))} – frei auf dem Laufwerk: {free}";
        }
    }

    partial void OnSelectedBuiltInModelChanged(BuiltInModel? value) => RefreshBuiltIn();

    private void RefreshBuiltIn()
    {
        OnPropertyChanged(nameof(BuiltInFolder));
        OnPropertyChanged(nameof(BuiltInStatus));
        OnPropertyChanged(nameof(IsSelectedInstalled));
        OnPropertyChanged(nameof(BuiltInButton));
        IsOpenCodeInstalled = OpenCodeServer.IsInstalled;
    }

    [RelayCommand]
    private async Task ChooseBuiltInFolder()
    {
        if (AppServices.Platform == null || Download.IsRunning) return;
        var folder = await AppServices.Platform.PickFolderAsync("Ordner für die eingebaute KI (Modelle sind mehrere GB groß)");
        if (folder == null) return;
        BuiltInAi.Stop();
        AppServices.Ai.BuiltInFolder = folder;
        BuiltInAi.Folder = folder;
        _settings.SaveCommand.Execute(null);
        RefreshBuiltIn();
    }

    [RelayCommand]
    private async Task UseBuiltIn()
    {
        if (SelectedBuiltInModel is not { } m) return;
        if (!BuiltInAi.IsInstalled(m))
        {
            Result = "";
            if (!await Download.InstallAsync(m)) return;
        }
        RefreshBuiltIn();
        await Use(AiSettings.BuiltInPreset(m));
    }

    [RelayCommand]
    private void CancelDownload() => Download.Cancel();

    [RelayCommand]
    private void RemoveBuiltIn()
    {
        if (SelectedBuiltInModel is not { } m || Download.IsRunning) return;
        BuiltInAi.Remove(m);
        Result = $"{m.Name} wurde gelöscht.";
        RefreshBuiltIn();
    }

    // ---------- OpenCode ----------

    [ObservableProperty] public partial bool IsOpenCodeInstalled { get; set; }
    public bool CanDownloadOpenCode => !IsOpenCodeInstalled && OpenCodeInstaller.IsSupported;

    partial void OnIsOpenCodeInstalledChanged(bool value) => OnPropertyChanged(nameof(CanDownloadOpenCode));

    [RelayCommand]
    private async Task DownloadOpenCode()
    {
        if (!await Download.InstallOpenCodeAsync()) return;
        IsOpenCodeInstalled = OpenCodeServer.IsInstalled;
        await LoadOpenCodeModels();
    }
    public ObservableCollection<OpenCodeModel> OpenCodeModels { get; } = new();
    [ObservableProperty] public partial OpenCodeModel? SelectedOpenCodeModel { get; set; }
    public string OpenCodeNote => SelectedOpenCodeModel?.DataNote is { } note ? "Datennutzung: " + note : "";

    /// <summary>Hinweis, solange das gemeinsame Tageskontingent der kostenlosen OpenCode-Modelle aufgebraucht ist.</summary>
    public string OpenCodeQuotaNote => OpenCodeChatModel.FreeQuotaResetsAt is { } t && t > DateTime.Now
        ? $"Das kostenlose Kontingent von OpenCode ist aufgebraucht (für alle Modelle gemeinsam) – wieder ab {t:g}. Bis dahin oben „Kostenlos – mehrere Modelle gleichzeitig“ verwenden."
        : "";

    partial void OnSelectedOpenCodeModelChanged(OpenCodeModel? value) => OnPropertyChanged(nameof(OpenCodeNote));

    [RelayCommand]
    private Task LoadOpenCodeModels() => Busy("Frage OpenCode nach den Modellen …", async () =>
    {
        var models = await OpenCodeServer.ModelsAsync(new AiProviderConfig { Kind = AiProviderKind.OpenCode });
        OnPropertyChanged(nameof(OpenCodeQuotaNote));
        OpenCodeModels.Clear();
        foreach (var m in models) OpenCodeModels.Add(m);
        SelectedOpenCodeModel = OpenCodeModels.FirstOrDefault();
        Result = $"{models.Count(m => m.Free)} kostenlose von {models.Count} Modellen gefunden. Die kostenlosen OpenCode-Modelle haben ein Tageskontingent.";
    });

    [RelayCommand]
    private Task UseOpenCode() => SelectedOpenCodeModel is not { } m ? Task.CompletedTask : Use(new AiProviderConfig
    {
        Name = "OpenCode: " + m.ModelId, Kind = AiProviderKind.OpenCode, BaseUrl = "", Model = m.Id, MaxCharsPerPage = 4000,
    });

    // ---------- Lokal ----------

    public ObservableCollection<LocalAiServer> LocalServers { get; } = new();
    [ObservableProperty] public partial LocalAiServer? SelectedLocalServer { get; set; }
    [ObservableProperty] public partial string? SelectedLocalModel { get; set; }
    public ObservableCollection<string> LocalModels { get; } = new();

    partial void OnSelectedLocalServerChanged(LocalAiServer? value)
    {
        LocalModels.Clear();
        foreach (var m in value?.Models ?? []) LocalModels.Add(m);
        SelectedLocalModel = AiDiscovery.SuggestModel(LocalModels);
    }

    [RelayCommand]
    private Task DetectLocal() => Busy("Suche KI-Server auf diesem Rechner …", async () =>
    {
        var found = await AiDiscovery.DetectLocalAsync();
        LocalServers.Clear();
        foreach (var s in found) LocalServers.Add(s);
        SelectedLocalServer = LocalServers.FirstOrDefault();
        Result = found.Count == 0
            ? "Kein lokaler KI-Server gefunden. Ollama oder LM Studio installieren, ein Modell laden und erneut suchen."
            : $"Gefunden: {string.Join(", ", found.Select(f => f.Name))}.";
    });

    [RelayCommand]
    private Task UseLocal() => SelectedLocalServer is not { } s || SelectedLocalModel is not { } model ? Task.CompletedTask : Use(new AiProviderConfig
    {
        Name = $"Lokal: {s.Name} ({model})", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = s.BaseUrl, Model = model,
        Temperature = 0.2, MaxCharsPerPage = 3000,
    });

    // ---------- Kostenlose Cloud-Stufen ----------

    public System.Collections.Generic.IReadOnlyList<FreeAiService> Services => AiDiscovery.FreeServices;
    [ObservableProperty] public partial FreeAiService? SelectedService { get; set; }
    [ObservableProperty] public partial string ServiceKey { get; set; } = "";
    [ObservableProperty] public partial string? SelectedServiceModel { get; set; }
    public ObservableCollection<string> ServiceModels { get; } = new();

    public string ServiceKeyHint => SelectedService is { NeedsKey: false } ? "Schlüssel (freiwillig, hebt die Grenzen an)" : "Schlüssel einfügen";

    partial void OnSelectedServiceChanged(FreeAiService? value)
    {
        ServiceModels.Clear();
        SelectedServiceModel = null;
        OnPropertyChanged(nameof(ServiceKeyHint));
    }

    [RelayCommand]
    private async Task OpenKeyPage()
    {
        if (SelectedService != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(SelectedService.KeyUrl);
    }

    [RelayCommand]
    private Task ConnectService() => SelectedService is not { } svc ? Task.CompletedTask : Busy($"Verbinde mit {svc.Name} …", async () =>
    {
        if (svc.NeedsKey && ServiceKey.Trim().Length == 0) throw new AiException("Bitte zuerst den Schlüssel einfügen („Schlüssel holen“ öffnet die Seite des Anbieters).");
        var all = await AiDiscovery.ModelsAsync(svc.BaseUrl, ServiceKey.Trim());
        var free = all.Where(svc.IsFreeModel).ToList();
        ServiceModels.Clear();
        foreach (var m in free.Count > 0 ? free : all) ServiceModels.Add(m);
        SelectedServiceModel = AiDiscovery.SuggestModel(ServiceModels, svc.Preferred);
        Result = $"Verbunden: {ServiceModels.Count} Modell(e) verfügbar.";
        // Mit Schlüssel verbundene Dienste (z. B. NVIDIA, Groq) stehen danach auch für den Verbund zur Wahl
        if (ServiceKey.Trim().Length > 0)
        {
            _keys[svc.Id] = ServiceKey.Trim();
            if (HasPoolCandidates && !IsPoolBusy) await LoadCandidatesAsync();
        }
    });

    [RelayCommand]
    private async Task UseService()
    {
        if (SelectedService is not { } svc) return;
        if (SelectedServiceModel == null) await ConnectService();          // „Verwenden“ ohne „Verbinden“: erst verbinden
        if (SelectedServiceModel is not { } model) return;                  // Fehler steht schon in Result
        await Use(new AiProviderConfig
        {
            Name = $"{svc.Name.Split(" (")[0]}: {model}", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = svc.BaseUrl, ApiKey = ServiceKey.Trim(),
            Model = model, Temperature = 0.2, MaxCharsPerPage = 6000, TimeoutSeconds = AiDiscovery.OnlineTimeoutSeconds,
        });
    }

    // ---------- Diktat: Spracherkennung ----------

    public System.Collections.Generic.IReadOnlyList<TranscriptionPreset> TranscriptionPresets => Transcriber.Presets;
    [ObservableProperty] public partial TranscriptionPreset? SelectedTranscription { get; set; }
    [ObservableProperty] public partial string TranscriptionUrl { get; set; } = AppServices.Ai.Transcription.BaseUrl;
    [ObservableProperty] public partial string TranscriptionKey { get; set; } = AppServices.Ai.Transcription.ApiKey;
    [ObservableProperty] public partial string TranscriptionModel { get; set; } = AppServices.Ai.Transcription.Model;
    [ObservableProperty] public partial string TranscriptionStatus { get; set; } = "";

    public string RecorderHint => AppServices.Recorder is { IsAvailable: false } r ? r.Hint
        : AppServices.Recorder == null ? "Auf diesem Gerät die Spracheingabe der Bildschirmtastatur verwenden." : "";

    partial void OnSelectedTranscriptionChanged(TranscriptionPreset? value)
    {
        if (value == null) return;
        TranscriptionUrl = value.BaseUrl;
        TranscriptionModel = value.Model;
    }

    [RelayCommand]
    private async Task OpenTranscriptionKeyPage()
    {
        if (SelectedTranscription != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(SelectedTranscription.KeyUrl);
    }

    [RelayCommand]
    private void SaveTranscription()
    {
        AppServices.Ai.Transcription = new TranscriptionConfig { BaseUrl = TranscriptionUrl.Trim(), ApiKey = TranscriptionKey.Trim(), Model = TranscriptionModel.Trim() };
        AppServices.Ai.Save();
        TranscriptionStatus = AppServices.Ai.Transcription.IsConfigured
            ? "Gespeichert. Im Editor mit „🎤 Diktat“ oder Strg+Umschalt+D diktieren." + (AppServices.Ai.Transcription.IsLocal ? "" : " Die Aufnahme geht an den gewählten Dienst.")
            : "Diktat ausgeschaltet.";
    }

    // ---------- Klassische maschinelle Übersetzung ----------

    public System.Collections.Generic.IReadOnlyList<MtService> MtServices => MachineTranslationModel.Services;
    [ObservableProperty] public partial MtService? SelectedMt { get; set; } = MachineTranslationModel.Services[0];
    [ObservableProperty] public partial string MtKey { get; set; } = "";
    [ObservableProperty] public partial string MtRegion { get; set; } = "";
    [ObservableProperty] public partial string MtUrl { get; set; } = "";

    [RelayCommand]
    private async Task OpenMtKeyPage()
    {
        if (SelectedMt != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(SelectedMt.KeyUrl);
    }

    // ---------- Offline auf diesem Rechner (OPUS-MT) ----------

    public bool IsLocalMt => SelectedMt?.Id == "lokal";
    partial void OnSelectedMtChanged(MtService? value)
    {
        OnPropertyChanged(nameof(IsLocalMt));
        if (value?.Id == "lokal" && AppServices.Main?.Project is { } project)
        {
            MtFrom = project.SourceLanguage;
            MtTo = project.TargetLanguages.FirstOrDefault() ?? MtTo;
        }
        RefreshLocalMt();
    }

    /// <summary>Richtung, für die ein Modell geladen wird – vorbelegt mit den Sprachen des offenen Projekts.</summary>
    [ObservableProperty] public partial string MtFrom { get; set; } = AppServices.Main?.Project?.SourceLanguage ?? "de-DE";
    [ObservableProperty] public partial string MtTo { get; set; } = AppServices.Main?.Project?.TargetLanguages.FirstOrDefault() ?? "en-GB";
    partial void OnMtFromChanged(string value) => RefreshLocalMt();
    partial void OnMtToChanged(string value) => RefreshLocalMt();

    public ObservableCollection<LocalMtModel> LocalMtInstalled { get; } = new();
    public bool HasLocalMtInstalled => LocalMtInstalled.Count > 0;
    public string LocalMtFolder => LocalTranslation.Folder;
    private LocalMtRoute? LocalRoute => LocalTranslation.Route(MtFrom, MtTo);
    public bool CanDownloadLocalMt => LocalRoute is { IsInstalled: false };

    public string LocalMtStatus
    {
        get
        {
            if (LocalRoute is not { } route) return "Für diese Richtung gibt es kein Modell.";
            var models = string.Join(" + ", route.Models.Select(m => $"{m.Id} ({LicenseName(m.License)})"));
            var way = route.ViaEnglish ? "Über Englisch (zwei Modelle, schwächer)" : "Direkt";
            if (route.IsInstalled) return $"{way}: {models} – geladen, bereit.";
            var free = BuiltInAi.FreeSpace(LocalTranslation.Folder) is { } f ? BuiltInAi.FormatSize(f) : "?";
            return $"{way}: {models} – einmaliger Download {BuiltInAi.FormatSize(route.MissingBytes)}, frei auf dem Laufwerk: {free}";
        }
    }

    private static string LicenseName(string license) => license switch { "apache-2.0" => "Apache-2.0", "cc-by-4.0" => "CC-BY-4.0", _ => license };

    private void RefreshLocalMt()
    {
        LocalMtInstalled.Clear();
        foreach (var m in LocalTranslation.Installed) LocalMtInstalled.Add(m);
        OnPropertyChanged(nameof(HasLocalMtInstalled));
        OnPropertyChanged(nameof(LocalMtStatus));
        OnPropertyChanged(nameof(CanDownloadLocalMt));
        OnPropertyChanged(nameof(LocalMtFolder));
    }

    [RelayCommand]
    private async Task DownloadLocalMt()
    {
        if (LocalRoute is not { IsInstalled: false } route) return;
        await Download.Run($"Übersetzungsmodell {Core.Model.Languages.DisplayName(MtFrom)} → {Core.Model.Languages.DisplayName(MtTo)}",
            (p, ct) => LocalTranslation.InstallAsync(route, p, ct));
        RefreshLocalMt();
    }

    [RelayCommand]
    private void RemoveLocalMt(LocalMtModel? model)
    {
        if (model == null || Download.IsRunning) return;
        LocalTranslation.Remove(model);
        RefreshLocalMt();
    }

    /// <summary>Ein kurzer Testsatz in der Ausgangssprache (sonst wird nur eingerichtet).</summary>
    private static string? Sample(string language) => LocalTranslation.Code(language) switch
    {
        "de" => "Schalten Sie das Gerät vor der Reinigung aus.",
        "en" => "Switch off the device before cleaning.",
        "fr" => "Éteignez l'appareil avant de le nettoyer.",
        "pl" => "Przed czyszczeniem wyłącz urządzenie.",
        "es" => "Apague el aparato antes de limpiarlo.",
        "it" => "Spegnere l'apparecchio prima della pulizia.",
        _ => null,
    };

    /// <summary>MÜ-Dienst einrichten und kurz testen; er wird nur fürs Übersetzen gewählt, nie für Recherche oder Prüfung.</summary>
    [RelayCommand]
    private Task UseMt() => SelectedMt is not { } svc ? Task.CompletedTask : Busy($"Teste {svc.Name} …", async () =>
    {
        if (svc.NeedsKey && MtKey.Trim().Length == 0) throw new AiException("Bitte zuerst den Schlüssel einfügen („Schlüssel holen“ öffnet die Seite des Anbieters).");
        var config = new AiProviderConfig
        {
            Name = svc.Name, Kind = AiProviderKind.MachineTranslation, Model = svc.Id, ApiKey = MtKey.Trim(), Region = MtRegion.Trim(),
            BaseUrl = svc.NeedsUrl ? (MtUrl.Trim().Length > 0 ? MtUrl.Trim() : svc.DefaultUrl) : "", TimeoutSeconds = 60,
        };
        var (sample, from, to) = ("Switch off the device before cleaning.", "en-GB", "de-DE");
        if (svc.Id == "lokal")
        {
            if (LocalRoute is not { } route) throw new AiException("Für diese Richtung gibt es kein Modell – andere Sprachen wählen.");
            if (!route.IsInstalled && !await Download.Run($"Übersetzungsmodell {Core.Model.Languages.DisplayName(MtFrom)} → {Core.Model.Languages.DisplayName(MtTo)}",
                    (p, ct) => LocalTranslation.InstallAsync(route, p, ct)))
                return;
            RefreshLocalMt();
            (sample, from, to) = (Sample(MtFrom) ?? "", MtFrom, MtTo);
        }
        var started = DateTime.Now;
        var answer = sample.Length == 0 ? "" : await new MachineTranslationModel(config).TranslateAsync(sample, from, to);
        var existing = _settings.Providers.FirstOrDefault(p => p.Name == config.Name);
        if (existing != null)
        {
            config.Id = existing.Id;
            _settings.Providers.Remove(existing);
        }
        var vm = new ProviderViewModel(config);
        _settings.Providers.Add(vm);
        _settings.TranslateProvider = vm;                    // nur Übersetzen – Recherche und Prüfung bleiben beim Sprachmodell
        _settings.SaveCommand.Execute(null);
        Result = answer.Length == 0
            ? $"{svc.Name} eingerichtet und für das Übersetzen gewählt."
            : $"{svc.Name} eingerichtet und für das Übersetzen gewählt ({(DateTime.Now - started).TotalSeconds:0.0} s): „{answer}“";
    });

    // ---------- Kostenlos: mehrere Modelle gleichzeitig ----------

    public ObservableCollection<PoolCandidateViewModel> PoolCandidates { get; } = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _keys = new();
    [ObservableProperty] public partial string PoolStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsPoolBusy { get; set; }
    public bool HasPoolCandidates => PoolCandidates.Count > 0;

    /// <summary>Name des Verbunds in der Zugangsliste (ein erneutes Einrichten ersetzt ihn).</summary>
    public const string PoolName = "Kostenlos: mehrere Modelle gleichzeitig";

    [RelayCommand]
    private async Task FindFreeModels()
    {
        if (IsPoolBusy) return;
        IsPoolBusy = true;
        PoolStatus = "Suche kostenlose Modelle …";
        try { await LoadCandidatesAsync(); }
        finally { IsPoolBusy = false; }
    }

    private async Task LoadCandidatesAsync()
    {
        var configured = _settings.Providers.Select(p => p.Config).Where(p => p.Kind != AiProviderKind.Pool).ToList();
        var found = await AiDiscovery.FreeModelsAsync(configured, _keys);
        var previous = PoolCandidates.Where(c => c.IsChecked).Select(c => c.Key).ToHashSet();
        var keepChoice = previous.Count > 0;
        PoolCandidates.Clear();
        void Add(PoolCandidate c)
        {
            var vm = new PoolCandidateViewModel(c);
            if (PoolCandidates.Any(x => x.Key == vm.Key)) return;
            if (keepChoice) vm.IsChecked = previous.Contains(vm.Key);
            PoolCandidates.Add(vm);
        }
        // eingebaute KI, wenn schon geladen: ohne Grenzen und die Texte bleiben auf dem Rechner
        foreach (var m in BuiltInAi.Models.Where(m => BuiltInAi.IsSupported && BuiltInAi.IsInstalled(m)))
            Add(new PoolCandidate("Eingebaute KI", AiSettings.BuiltInPreset(m), m == BuiltInAi.Models[0], "Läuft auf diesem Rechner, ohne Grenzen."));
        foreach (var c in found) Add(c);
        // schon eingerichtete kostenlose Zugänge (OpenCode, lokale Server) zum Zuschalten
        foreach (var p in configured.Where(p => p.Kind == AiProviderKind.OpenCode || (p.Kind == AiProviderKind.OpenAiCompatible && p.IsLocal)))
            Add(new PoolCandidate(p.Kind == AiProviderKind.OpenCode ? "OpenCode" : "Lokal", p.Clone(), false,
                p.Kind == AiProviderKind.OpenCode ? "Tageskontingent wird mit der eigenen OpenCode-Nutzung geteilt." : "Nur, wenn der Server läuft."));
        OnPropertyChanged(nameof(HasPoolCandidates));
        PoolStatus = found.Count == 0
            ? "Keine kostenlosen Online-Modelle erreichbar – Internetverbindung prüfen."
            : $"{PoolCandidates.Count} kostenlose Modelle gefunden; die empfohlenen sind angehakt.";
    }

    /// <summary>
    /// Die angehakten Modelle gleichzeitig kurz prüfen; wer antwortet, kommt in den Verbund. Der Verbund wird
    /// gespeichert und (auf Wunsch) für alle Aufgaben gewählt. Ohne Liste werden die empfohlenen Modelle genommen.
    /// </summary>
    [RelayCommand]
    private async Task UseFreePool()
    {
        if (IsPoolBusy) return;
        IsPoolBusy = true;
        try
        {
            if (PoolCandidates.Count == 0)
            {
                PoolStatus = "Suche kostenlose Modelle …";
                await LoadCandidatesAsync();
            }
            var chosen = PoolCandidates.Where(c => c.IsChecked).ToList();
            if (chosen.Count == 0)
            {
                PoolStatus = "Bitte mindestens ein Modell anhaken.";
                return;
            }
            PoolStatus = $"Prüfe {chosen.Count} Modelle gleichzeitig …";
            foreach (var c in chosen) c.Result = "…";
            await Task.WhenAll(chosen.Select(TestAsync));
            var working = chosen.Where(c => c.Works).Select(c => c.Candidate.Config.Clone()).ToList();
            var failed = chosen.Where(c => !c.Works).ToList();
            if (working.Count == 0)
            {
                PoolStatus = "Kein angehaktes Modell hat geantwortet – später erneut versuchen oder andere Modelle anhaken.";
                return;
            }
            var pool = new AiProviderConfig
            {
                Name = PoolName, Kind = AiProviderKind.Pool, Members = working, Temperature = 0.2,
                MaxCharsPerPage = working.Min(m => m.MaxCharsPerPage),
            };
            Register(pool);
            var names = string.Join(", ", failed.Select(f => f.Model));
            PoolStatus = (UseForAll
                    ? $"✔ Verbund aus {working.Count} Modellen eingerichtet und für alle Aufgaben gewählt. Stapelübersetzungen laufen auf allen gleichzeitig; ist eines ausgelastet, übernimmt ein anderes."
                    : $"✔ Verbund aus {working.Count} Modellen eingerichtet. Stapelübersetzungen laufen auf allen gleichzeitig; ist eines ausgelastet, übernimmt ein anderes.")
                + (failed.Count > 0 ? $"\nNicht aufgenommen (keine Antwort): {names}." : "");
        }
        finally { IsPoolBusy = false; }
    }

    private static async Task TestAsync(PoolCandidateViewModel c)
    {
        var config = c.Candidate.Config.Clone();
        config.FailFast = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer = await ChatModelFactory.Create(config).AskAsync("Answer only with the translation.",
                "Translate into German: \"Switch off the device before cleaning.\"", cts.Token);
            c.Works = answer.Trim().Length > 0;
            c.Result = c.Works ? $"✔ {watch.Elapsed.TotalSeconds:0.0} s" : "✖ leere Antwort";
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            c.Works = false;
            c.Result = ex is OperationCanceledException ? "✖ keine Antwort in 90 s" : "✖ " + ex.Message;
        }
    }

    // ---------- gemeinsam ----------

    /// <summary>Zugang anlegen (gleichnamigen ersetzen), auf Wunsch für alle Aufgaben wählen, speichern, kurz testen.</summary>
    private Task Use(AiProviderConfig config) => Busy("Richte ein und teste …", async () =>
    {
        Register(config);
        var started = DateTime.Now;
        try
        {
            var answer = await ChatModelFactory.Create(config).AskAsync("Answer only with the translation.", "Translate into German: \"Switch off the device before cleaning.\"");
            Result = $"Eingerichtet und getestet ({(DateTime.Now - started).TotalSeconds:0.0} s): „{answer.Trim()}“";
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Result = $"Eingerichtet, aber der Test schlug fehl: {ex.Message}";
            OnPropertyChanged(nameof(OpenCodeQuotaNote));
        }
    });

    /// <summary>Zugang anlegen (gleichnamigen ersetzen), auf Wunsch für alle Aufgaben wählen und speichern.</summary>
    private void Register(AiProviderConfig config)
    {
        var existing = _settings.Providers.FirstOrDefault(p => p.Name == config.Name);
        if (existing != null)
        {
            config.Id = existing.Id;
            _settings.Providers.Remove(existing);
        }
        var vm = new ProviderViewModel(config);
        _settings.Providers.Add(vm);
        _settings.Selected = vm;
        if (UseForAll || _settings.TranslateProvider == null)
        {
            _settings.TranslateProvider = vm;
            _settings.ResearchProvider = vm;
            _settings.EvaluateProvider = vm;
        }
        _settings.SaveCommand.Execute(null);
    }

    private async Task Busy(string message, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        Result = message;
        try { await work(); }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Result = "Fehler: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
