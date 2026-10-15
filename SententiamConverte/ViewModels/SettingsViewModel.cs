using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Bearbeitbare Kopie eines KI-Zugangs.</summary>
public partial class ProviderViewModel : ObservableObject
{
    public AiProviderConfig Config { get; }
    public ProviderViewModel(AiProviderConfig config) => Config = config;

    public string Id => Config.Id;
    public string Name { get => Config.Name; set { Config.Name = value; OnPropertyChanged(); } }
    public int KindIndex { get => (int)Config.Kind; set { Config.Kind = (AiProviderKind)value; OnPropertyChanged(); } }
    public string BaseUrl { get => Config.BaseUrl; set { Config.BaseUrl = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrivacyNote)); } }

    public string PrivacyNote => Config.IsLocal
        ? "Lokal: Die Texte bleiben auf diesem Rechner bzw. im eigenen Netz."
        : "Cloud: Die Texte werden an den Anbieter übertragen. Vertraulichkeitsvereinbarungen mit Kunden und die DSGVO beachten; bei vertraulichen Aufträgen einen lokalen Zugang verwenden.";
    public string ApiKey { get => Config.ApiKey; set { Config.ApiKey = value; OnPropertyChanged(); } }
    public string Model { get => Config.Model; set { Config.Model = value; OnPropertyChanged(); } }
    public string Temperature
    {
        get => Config.Temperature?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        set
        {
            Config.Temperature = double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
            OnPropertyChanged();
        }
    }
    public int MaxTokens { get => Config.MaxTokens; set { Config.MaxTokens = value; OnPropertyChanged(); } }
    public int MaxCharsPerPage { get => Config.MaxCharsPerPage; set { Config.MaxCharsPerPage = value; OnPropertyChanged(); } }

    public override string ToString() => Name;
}

public partial class ServerAccountViewModel : ObservableObject
{
    public SententiamConverte.Core.Remote.ServerAccount Account { get; }
    public ServerAccountViewModel(SententiamConverte.Core.Remote.ServerAccount account) => Account = account;
    public string Name { get => Account.Name; set { Account.Name = value; OnPropertyChanged(); } }
    public string Url { get => Account.Url; set { Account.Url = value; OnPropertyChanged(); } }
    public string Token { get => Account.Token; set { Account.Token = value; OnPropertyChanged(); } }
    public override string ToString() => Name;
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Load();
    }

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public IReadOnlyList<AiProviderConfig> Presets { get; } = AiSettings.Presets();
    public IReadOnlyList<string> KindNames { get; } = ["OpenAI-kompatibel (lokal oder Cloud)", "Anthropic Claude"];
    public IReadOnlyList<string> SearchNames { get; } = ["Keine", "SearXNG (eigener Server)", "Brave Search (API-Schlüssel)", "Tavily (API-Schlüssel)", "Claude-Websuche"];

    [ObservableProperty] public partial ProviderViewModel? Selected { get; set; }
    [ObservableProperty] public partial AiProviderConfig? SelectedPreset { get; set; }
    [ObservableProperty] public partial ProviderViewModel? TranslateProvider { get; set; }
    [ObservableProperty] public partial ProviderViewModel? ResearchProvider { get; set; }
    [ObservableProperty] public partial ProviderViewModel? EvaluateProvider { get; set; }
    [ObservableProperty] public partial ProviderViewModel? SearchClaudeProvider { get; set; }
    [ObservableProperty] public partial int SearchKindIndex { get; set; }
    [ObservableProperty] public partial string SearchBaseUrl { get; set; } = "";
    [ObservableProperty] public partial string SearchApiKey { get; set; } = "";
    [ObservableProperty] public partial string ExplanationLanguage { get; set; } = "Deutsch";
    [ObservableProperty] public partial string TestResult { get; set; } = "";
    [ObservableProperty] public partial bool IsTesting { get; set; }

    // Teamserver
    public ObservableCollection<ServerAccountViewModel> ServerAccounts { get; } = new();
    [ObservableProperty] public partial ServerAccountViewModel? SelectedServer { get; set; }
    [ObservableProperty] public partial string ServerResult { get; set; } = "";
    public IReadOnlyList<string> ResourceKindNames { get; } = ["Translation Memory", "Terminologie", "Wissensdatenbank"];
    [ObservableProperty] public partial int NewResourceKind { get; set; }
    [ObservableProperty] public partial string NewResourceName { get; set; } = "";
    [ObservableProperty] public partial string NewResourceDescription { get; set; } = "";

    [RelayCommand]
    private void AddServer()
    {
        var vm = new ServerAccountViewModel(new SententiamConverte.Core.Remote.ServerAccount { Name = "Team", Url = "http://server:5280" });
        ServerAccounts.Add(vm);
        SelectedServer = vm;
    }

    [RelayCommand]
    private void RemoveServer()
    {
        if (SelectedServer == null) return;
        ServerAccounts.Remove(SelectedServer);
        SelectedServer = ServerAccounts.FirstOrDefault();
    }

    private void SaveServers()
    {
        var s = new SententiamConverte.Core.Remote.ServerSettings { Servers = ServerAccounts.Select(a => a.Account).ToList() };
        s.Save();
        AppServices.Servers = s;
        SententiamConverte.Core.Resources.ResourceFactory.ResetConnections();
    }

    [RelayCommand]
    private async Task TestServer()
    {
        if (SelectedServer == null) return;
        SaveServers();
        ServerResult = "Verbinde …";
        try
        {
            var api = SententiamConverte.Core.Resources.ResourceFactory.Api(SelectedServer.Account);
            var me = await api.WhoAmIAsync();
            var res = await api.ResourcesAsync();
            ServerResult = $"Angemeldet als {me.User} ({me.Role}). Ressourcen:\n" +
                string.Join("\n", res.Select(r => $"• {SententiamConverte.Core.Resources.ResourceFactory.KindName(r.Kind)} „{r.Name}“ – {r.Count} Einträge{(r.Description is { Length: > 0 } d ? " – " + d : "")}"));
        }
        catch (Exception ex) { ServerResult = ex.Message; }
    }

    [RelayCommand]
    private async Task CreateServerResource()
    {
        if (SelectedServer == null || NewResourceName.Trim().Length == 0) return;
        try
        {
            await SententiamConverte.Core.Resources.ResourceFactory.Api(SelectedServer.Account)
                .CreateResourceAsync((SententiamConverte.Core.Remote.ResourceKind)NewResourceKind, NewResourceName.Trim(), NewResourceDescription.Trim());
            ServerResult = $"„{NewResourceName.Trim()}“ angelegt.";
            NewResourceName = NewResourceDescription = "";
        }
        catch (Exception ex) { ServerResult = ex.Message; }
    }

    // Rechtschreibung
    public IReadOnlyList<string> DictionaryLanguages { get; } = SententiamConverte.Core.Qa.SpellChecker.DownloadableLanguages.OrderBy(l => l).ToList();
    [ObservableProperty] public partial string DictionaryLanguage { get; set; } = "de-DE";
    [ObservableProperty] public partial string DictionaryStatus { get; set; } = "";

    partial void OnDictionaryLanguageChanged(string value) => UpdateDictionaryStatus();

    private void UpdateDictionaryStatus() =>
        DictionaryStatus = AppServices.Spell.IsAvailable(DictionaryLanguage) ? "Wörterbuch vorhanden." : "Noch nicht geladen.";

    [RelayCommand]
    private async Task DownloadDictionary()
    {
        IsTesting = true;
        DictionaryStatus = "Lade …";
        try
        {
            await AppServices.Spell.DownloadAsync(DictionaryLanguage);
            DictionaryStatus = $"Wörterbuch {DictionaryLanguage} geladen.";
        }
        catch (Exception ex) { DictionaryStatus = "Fehler: " + ex.Message; }
        finally { IsTesting = false; }
    }

    private void Load()
    {
        var s = AppServices.Ai;
        Providers.Clear();
        foreach (var p in s.Providers) Providers.Add(new ProviderViewModel(p));
        ProviderViewModel? Find(string? id) => Providers.FirstOrDefault(p => p.Id == id) ?? Providers.FirstOrDefault();
        TranslateProvider = Find(s.TranslateProviderId);
        ResearchProvider = Find(s.ResearchProviderId);
        EvaluateProvider = Find(s.EvaluateProviderId);
        SearchClaudeProvider = Providers.FirstOrDefault(p => p.Id == s.Search.ClaudeProviderId) ?? Providers.FirstOrDefault(p => p.Config.Kind == AiProviderKind.Anthropic);
        SearchKindIndex = (int)s.Search.Kind;
        SearchBaseUrl = s.Search.BaseUrl;
        SearchApiKey = s.Search.ApiKey;
        ExplanationLanguage = s.ExplanationLanguage;
        Selected = Providers.FirstOrDefault();
        UpdateDictionaryStatus();
        ServerAccounts.Clear();
        foreach (var a in AppServices.Servers.Servers) ServerAccounts.Add(new ServerAccountViewModel(a));
        SelectedServer = ServerAccounts.FirstOrDefault();
    }

    [RelayCommand]
    private void AddFromPreset()
    {
        var preset = SelectedPreset ?? Presets[^1];
        var copy = new AiProviderConfig
        {
            Name = preset.Name, Kind = preset.Kind, BaseUrl = preset.BaseUrl, ApiKey = preset.ApiKey, Model = preset.Model,
            Temperature = preset.Temperature, MaxTokens = preset.MaxTokens, MaxCharsPerPage = preset.MaxCharsPerPage,
        };
        var vm = new ProviderViewModel(copy);
        Providers.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected == null) return;
        Providers.Remove(Selected);
        Selected = Providers.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        var s = new AiSettings
        {
            Providers = Providers.Select(p => p.Config).ToList(),
            TranslateProviderId = TranslateProvider?.Id,
            ResearchProviderId = ResearchProvider?.Id,
            EvaluateProviderId = EvaluateProvider?.Id,
            ExplanationLanguage = ExplanationLanguage,
            Search = new SearchConfig
            {
                Kind = (SearchProviderKind)SearchKindIndex,
                BaseUrl = SearchBaseUrl,
                ApiKey = SearchApiKey,
                ClaudeProviderId = SearchClaudeProvider?.Id,
            },
        };
        s.Save();
        AppServices.Ai = s;
        SaveServers();
        Load();
        _main.Status = "KI-Einstellungen gespeichert.";
    }

    [RelayCommand]
    private async Task TestSelected()
    {
        if (Selected == null) return;
        IsTesting = true;
        TestResult = "Teste …";
        try
        {
            var model = ChatModelFactory.Create(Selected.Config);
            var started = DateTime.Now;
            var answer = await model.AskAsync("Answer briefly.", "Translate into German: \"The screw must be tightened to 5 Nm.\"");
            TestResult = $"OK ({(DateTime.Now - started).TotalSeconds:0.0} s): {answer}";
        }
        catch (Exception ex) { TestResult = "Fehler: " + ex.Message; }
        finally { IsTesting = false; }
    }

    [RelayCommand]
    private async Task TestSearch()
    {
        IsTesting = true;
        TestResult = "Teste Websuche …";
        try
        {
            Save();
            var search = SearchProviderFactory.Create(AppServices.Ai) ?? throw new InvalidOperationException("Keine Websuche gewählt.");
            var results = await search.SearchAsync("\"Anzugsdrehmoment\"", "de-DE", 5);
            TestResult = $"{search.Name}: {results.Count} Treffer\n" + string.Join("\n", results.Select(r => "• " + r.Title + " – " + r.Url));
        }
        catch (Exception ex) { TestResult = "Fehler: " + ex.Message; }
        finally { IsTesting = false; }
    }
}
