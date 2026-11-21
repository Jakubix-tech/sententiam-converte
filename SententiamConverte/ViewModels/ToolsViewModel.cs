using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Controls;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Hilfsmittel in der Liste (Texte in der Sprache der Oberfläche).</summary>
public sealed class ToolRow(TranslatorTool tool, ToolsViewModel owner, ToolSuggestion? suggestion = null)
{
    public TranslatorTool Tool { get; } = tool;
    public ToolsViewModel Owner { get; } = owner;
    public string Name => Tool.Name;
    public string Description => L.T(Tool.Description);
    public string Provider => Tool.Provider;
    public string License => L.T(Tool.License);

    public string KindName => ToolsViewModel.KindName(Tool.Kind);

    public string AccessName => L.T(Tool.Access switch
    {
        ToolAccess.Integrated => "im Programm eingebaut",
        ToolAccess.Download => "als Datenbank ladbar",
        _ => "im Browser",
    });

    public string LanguagesText => Tool.ForAllLanguages ? L.T("alle Sprachen") : string.Join(", ", Tool.Languages.Select(LanguageOption.PrimaryName));

    /// <summary>Warum es zum Projekt passt.</summary>
    public string Reason => suggestion == null ? "" : string.Join(" · ",
        new[] { suggestion.Languages.Count == 0 ? L.T("alle Sprachen") : string.Join(", ", suggestion.Languages.Select(LanguageOption.PrimaryName)) }
            .Concat(suggestion.Topic == null ? [] : [L.T(suggestion.Topic)]));

    public bool CanDownload => Tool.Access == ToolAccess.Download;
}

/// <summary>
/// Hilfsmittel für Übersetzer: Katalog mit Filtern, Vorschläge für das offene Projekt und Nachschlagen eines Begriffs.
/// Seiten öffnet der Browser; der Aufruf erscheint im Datenausgang.
/// </summary>
public partial class ToolsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public ToolsViewModel(MainViewModel main)
    {
        _main = main;
        ApplyFilter();
    }

    public static string KindName(ToolKind kind) => L.T(kind switch
    {
        ToolKind.Termbase => "Termbank",
        ToolKind.Dictionary => "Wörterbuch",
        ToolKind.Corpus => "Korpus und Konkordanz",
        ToolKind.Law => "Rechtsquelle",
        ToolKind.Style => "Stil und Sprachberatung",
        _ => "Nachschlagewerk",
    });

    // ---------- Katalog ----------

    public string[] KindNames { get; } =
    [
        L.T("Alle Arten"), L.T("Termbanken"), L.T("Wörterbücher"), L.T("Korpora und Konkordanz"), L.T("Rechtsquellen"),
        L.T("Stil und Sprachberatung"), L.T("Nachschlagewerke"),
    ];

    [ObservableProperty] public partial int KindIndex { get; set; }

    /// <summary>Sprache (Code); leer = alle.</summary>
    [ObservableProperty] public partial string? LanguageFilter { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; } = "";

    public ObservableCollection<ToolRow> Rows { get; } = new();

    public string CountText => L.T($"{Rows.Count} von {TranslatorTools.All.Count} Hilfsmitteln");

    partial void OnKindIndexChanged(int value) => ApplyFilter();
    partial void OnLanguageFilterChanged(string? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var languages = (LanguageFilter ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = SearchText.Trim();
        Rows.Clear();
        foreach (var tool in TranslatorTools.All)
        {
            if (KindIndex > 0 && (int)tool.Kind != KindIndex - 1) continue;
            if (languages.Length > 0 && !languages.Any(tool.Covers)) continue;
            var row = new ToolRow(tool, this);
            if (text.Length > 0 && !(row.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                     || row.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                     || row.Provider.Contains(text, StringComparison.CurrentCultureIgnoreCase))) continue;
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(CountText));
    }

    // ---------- Vorschläge für das Projekt ----------

    public ObservableCollection<ToolRow> Suggestions { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    public string SuggestionsTitle => _main.Project is { } p
        ? L.T($"Für „{p.Name}“ empfohlen")
        : L.T("Für Ihr Projekt empfohlen");

    /// <summary>Nach dem Öffnen eines Projekts oder einer Änderung von Sprachen und Fachgebiet.</summary>
    public void RefreshSuggestions()
    {
        Suggestions.Clear();
        if (_main.Project is { } p)
            foreach (var s in TranslatorTools.Suggest(p.SourceLanguage, p.TargetLanguages, _main.ProjectTopic ?? p.Topic, 10))
                Suggestions.Add(new ToolRow(s.Tool, this, s));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsTitle));
        if (_main.Project is { } project && string.IsNullOrEmpty(TermLanguage)) TermLanguage = project.SourceLanguage;
    }

    // ---------- Nachschlagen ----------

    [ObservableProperty] public partial string Term { get; set; } = "";

    /// <summary>Sprache des Begriffs (Code).</summary>
    [ObservableProperty] public partial string? TermLanguage { get; set; }

    public ObservableCollection<ToolRow> LookupTools { get; } = new();

    partial void OnTermLanguageChanged(string? value)
    {
        LookupTools.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var t in TranslatorTools.All.Where(t => t.SearchUrl != null && t.Covers(value))) LookupTools.Add(new ToolRow(t, this));
    }

    /// <summary>Aus dem Editor: markierter Text, Sprache der Spalte.</summary>
    public void Prepare(string term, string language)
    {
        Term = term.Trim();
        TermLanguage = language;
    }

    /// <summary>Die andere Sprache des Projekts (für Wörterbücher mit Sprachpaar).</summary>
    private string? OtherLanguage(string language) => _main.Project is { } p
        ? Core.Model.Languages.Matches(language, p.SourceLanguage) ? p.TargetLanguages.FirstOrDefault() : p.SourceLanguage
        : null;

    [RelayCommand]
    private Task LookUp(ToolRow? row)
    {
        if (row == null) return Task.CompletedTask;
        if (Term.Trim().Length == 0)
        {
            _main.Status = "Bitte zuerst einen Begriff eingeben.";
            return Task.CompletedTask;
        }
        var language = TermLanguage ?? _main.Project?.SourceLanguage ?? "de";
        return OpenUrl(row.Tool.LookupUrl(Term, language, OtherLanguage(language)));
    }

    [RelayCommand]
    private Task Open(ToolRow? row) => row == null ? Task.CompletedTask : OpenUrl(row.Tool.Url);

    /// <summary>Ladbare Daten: zu „TM &amp; Terminologie“ → Freie Sprachdaten.</summary>
    [RelayCommand]
    private void Download(ToolRow? row)
    {
        if (row == null) return;
        _main.SelectedTab = 2;
        _main.Status = L.T($"„{row.Name}“: unter „Freie Sprachdaten herunterladen“ das Sprachpaar wählen und laden.");
    }

    private async Task OpenUrl(string url)
    {
        try
        {
            // Der Browser öffnet die Seite; trotzdem im Datenausgang zeigen (und bei Sperre nicht öffnen)
            await Outbound.CheckAsync(new Uri(url).Host, OutboundKind.Web, L.T("Im Browser öffnen:") + " " + url, false);
            if (AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(url);
        }
        catch (OutboundDeclinedException ex)
        {
            _main.Status = ex.Message;
        }
    }
}
