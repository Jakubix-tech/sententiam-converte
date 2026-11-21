using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Land in der Ergebniszeile einer Variante.</summary>
public sealed class PresenceCellRow(PresenceCell cell)
{
    public PresenceCell Cell { get; } = cell;
    public string Country => Cell.Country;
    public string Count => Cell.CountText;
    public string Detail => Cell.Error != null
        ? L.T("nicht verfügbar")
        : L.T($"von {Cell.Checked} geprüft, {Cell.OnCountryDomains} auf .{Cell.Country.ToLowerInvariant()}-Seiten");

    public string LevelName => L.T(Cell.Level switch
    {
        PresenceLevel.Common => "verbreitet",
        PresenceLevel.Present => "kommt vor",
        PresenceLevel.Rare => "selten",
        _ => Cell.Error != null ? "Fehler" : "nicht gefunden",
    });

    public string Color => Cell.Level switch
    {
        PresenceLevel.Common => "#15803D",
        PresenceLevel.Present => "#4D7C0F",
        PresenceLevel.Rare => "#B45309",
        _ => "#B91C1C",
    };
}

/// <summary>Eine Variante mit ihren Ländern und Beispielen.</summary>
public sealed class PresenceRow(string variant, List<PresenceCellRow> cells, ToolsViewModel owner)
{
    public string Variant { get; } = variant;
    public List<PresenceCellRow> Cells { get; } = cells;
    public ToolsViewModel Owner { get; } = owner;
    public List<UsageExample> Examples { get; } = cells.SelectMany(c => c.Cell.Examples.Take(2)).DistinctBy(e => e.Url).Take(6).ToList();
}

/// <summary>Verbreitung im Netz: Ausdruck und Varianten je Land prüfen (offizielle Such-Schnittstelle mit Länderfilter).</summary>
public partial class ToolsViewModel
{
    private CancellationTokenSource? _presenceCts;

    /// <summary>Ausdruck, Varianten mit „|“ getrennt.</summary>
    [ObservableProperty] public partial string PresenceQuery { get; set; } = "";

    [ObservableProperty] public partial string? PresenceLanguage { get; set; }

    /// <summary>Länder als Codes, mit Komma („DE, AT, CH“).</summary>
    [ObservableProperty] public partial string PresenceCountries { get; set; } = "";

    /// <summary>Seiten je Variante und Land (20 Treffer je Seite).</summary>
    [ObservableProperty] public partial int PresencePages { get; set; } = 2;

    [ObservableProperty] public partial bool IsCheckingPresence { get; set; }
    [ObservableProperty] public partial string PresenceStatus { get; set; } = "";

    public ObservableCollection<PresenceRow> PresenceResults { get; } = new();

    private IReadOnlyList<string> Variants => Core.Tm.PhraseCounter.Variants(PresenceQuery);
    private IReadOnlyList<string> Countries => PresenceCountries.Split(',', ';', ' ').Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length == 2).Distinct().ToList();

    public string PresenceCost => Variants.Count == 0 || Countries.Count == 0 ? ""
        : L.T($"{WebPresence.Requests(Variants.Count, Countries.Count, PresencePages)} Anfragen an die Suche");

    partial void OnPresenceQueryChanged(string value) => OnPropertyChanged(nameof(PresenceCost));
    partial void OnPresenceCountriesChanged(string value) => OnPropertyChanged(nameof(PresenceCost));
    partial void OnPresencePagesChanged(int value) => OnPropertyChanged(nameof(PresenceCost));

    partial void OnPresenceLanguageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) PresenceCountries = string.Join(", ", WebPresence.CountriesFor(value));
    }

    /// <summary>Aus dem Editor: markierte Wendung und Sprache der Spalte.</summary>
    public void PreparePresence(string phrase, string language)
    {
        PresenceQuery = phrase.Trim();
        PresenceLanguage = language;
    }

    [RelayCommand]
    private async Task CheckPresence()
    {
        if (IsCheckingPresence) return;
        if (Variants.Count == 0 || Countries.Count == 0 || string.IsNullOrWhiteSpace(PresenceLanguage))
        {
            PresenceStatus = L.T("Ausdruck, Sprache und Länder angeben.");
            return;
        }
        if (SearchProviderFactory.Create(AppServices.Ai) is not ICountrySearchProvider search)
        {
            PresenceStatus = L.T("Die Länderprüfung braucht eine Websuche mit Länderfilter: unter „KI einrichten“ → „Websuche für die Agenten“ Brave oder Tavily mit eigenem Schlüssel einrichten.");
            return;
        }
        IsCheckingPresence = true;
        PresenceResults.Clear();
        var cts = _presenceCts = new CancellationTokenSource();
        try
        {
            var cells = await WebPresence.CheckAsync(search, Variants, PresenceLanguage, Countries, PresencePages,
                new Progress<string>(m => PresenceStatus = L.T(m)), cts.Token);
            foreach (var g in cells.GroupBy(c => c.Variant))
                PresenceResults.Add(new PresenceRow(g.Key, g.Select(c => new PresenceCellRow(c)).ToList(), this));
            var errors = cells.Where(c => c.Error != null).Select(c => c.Error!).Distinct().ToList();
            PresenceStatus = errors.Count > 0
                ? L.T("Nicht alle Länder ließen sich abfragen: ") + string.Join("; ", errors.Take(2))
                : L.T("Gezählt sind Treffer, die die Wortfolge wörtlich enthalten – keine geschätzten Gesamtzahlen.");
        }
        catch (OperationCanceledException) { PresenceStatus = L.T("Abgebrochen."); }
        finally { IsCheckingPresence = false; }
    }

    [RelayCommand]
    private void CancelPresence() => _presenceCts?.Cancel();

    [RelayCommand]
    private Task OpenExample(UsageExample? example) => example == null ? Task.CompletedTask : OpenUrl(example.Url);
}
