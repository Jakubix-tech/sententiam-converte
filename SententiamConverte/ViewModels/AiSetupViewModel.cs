using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// „KI einrichten“ in wenigen Klicks: kostenlos über die eigene OpenCode-Installation, ein lokaler Server
/// (wird automatisch gefunden) oder die kostenlose Stufe eines Cloud-Anbieters. Jeder Weg legt einen Zugang an,
/// nutzt ihn auf Wunsch für alle Aufgaben und testet ihn sofort.
/// </summary>
public partial class AiSetupViewModel : ViewModelBase
{
    private readonly SettingsViewModel _settings;

    public AiSetupViewModel(SettingsViewModel settings)
    {
        _settings = settings;
        IsOpenCodeInstalled = OpenCodeServer.IsInstalled;
        SelectedService = Services.FirstOrDefault();
    }

    [ObservableProperty] public partial bool UseForAll { get; set; } = true;
    [ObservableProperty] public partial string Result { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    // ---------- OpenCode ----------

    public bool IsOpenCodeInstalled { get; }
    public ObservableCollection<OpenCodeModel> OpenCodeModels { get; } = new();
    [ObservableProperty] public partial OpenCodeModel? SelectedOpenCodeModel { get; set; }
    public string OpenCodeNote => SelectedOpenCodeModel?.DataNote is { } note ? "Datennutzung: " + note : "";

    partial void OnSelectedOpenCodeModelChanged(OpenCodeModel? value) => OnPropertyChanged(nameof(OpenCodeNote));

    [RelayCommand]
    private Task LoadOpenCodeModels() => Busy("Frage OpenCode nach den Modellen …", async () =>
    {
        var models = await OpenCodeServer.ModelsAsync(new AiProviderConfig { Kind = AiProviderKind.OpenCode });
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

    partial void OnSelectedServiceChanged(FreeAiService? value)
    {
        ServiceModels.Clear();
        SelectedServiceModel = null;
    }

    [RelayCommand]
    private async Task OpenKeyPage()
    {
        if (SelectedService != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(SelectedService.KeyUrl);
    }

    [RelayCommand]
    private Task ConnectService() => SelectedService is not { } svc ? Task.CompletedTask : Busy($"Verbinde mit {svc.Name} …", async () =>
    {
        if (ServiceKey.Trim().Length == 0) throw new AiException("Bitte zuerst den Schlüssel einfügen („Schlüssel holen“ öffnet die Seite des Anbieters).");
        var all = await AiDiscovery.ModelsAsync(svc.BaseUrl, ServiceKey.Trim());
        var free = all.Where(svc.IsFreeModel).ToList();
        ServiceModels.Clear();
        foreach (var m in free.Count > 0 ? free : all) ServiceModels.Add(m);
        SelectedServiceModel = AiDiscovery.SuggestModel(ServiceModels);
        Result = $"Verbunden: {ServiceModels.Count} Modell(e) verfügbar.";
    });

    [RelayCommand]
    private Task UseService() => SelectedService is not { } svc || SelectedServiceModel is not { } model ? Task.CompletedTask : Use(new AiProviderConfig
    {
        Name = $"{svc.Name.Split(" (")[0]}: {model}", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = svc.BaseUrl, ApiKey = ServiceKey.Trim(),
        Model = model, Temperature = 0.2, MaxCharsPerPage = 6000,
    });

    // ---------- gemeinsam ----------

    /// <summary>Zugang anlegen (gleichnamigen ersetzen), auf Wunsch für alle Aufgaben wählen, speichern, kurz testen.</summary>
    private Task Use(AiProviderConfig config) => Busy("Richte ein und teste …", async () =>
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
        var started = DateTime.Now;
        try
        {
            var answer = await ChatModelFactory.Create(config).AskAsync("Answer only with the translation.", "Translate into German: \"Switch off the device before cleaning.\"");
            Result = $"Eingerichtet und getestet ({(DateTime.Now - started).TotalSeconds:0.0} s): „{answer.Trim()}“";
        }
        catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Result = $"Eingerichtet, aber der Test schlug fehl: {ex.Message}";
        }
    });

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
