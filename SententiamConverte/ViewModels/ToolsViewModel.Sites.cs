using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine Seite der Quellen-Datenbank in der Anzeige.</summary>
public sealed class SiteRow(SiteEntry entry)
{
    public SiteEntry Entry { get; } = entry;
    public string Domain => Entry.Domain;
    public bool IsEvidence => Entry.Origin == SiteOrigin.Evidence;

    public string Info => string.Join(" · ", new[]
    {
        string.Join(", ", Entry.Languages),
        string.Join(", ", Entry.Topics),
        IsEvidence ? L.T($"{Entry.Uses}× Beleg") : L.T("von der KI vorgeschlagen, noch kein Beleg"),
        Entry.Suggestions > 0 && IsEvidence ? L.T($"{Entry.Suggestions}× vorgeschlagen") : "",
        Entry.LastUsed.ToLocalTime().ToString("dd.MM.yyyy"),
    }.Where(s => s.Length > 0));

    public string Note => Entry.Note ?? "";
}

/// <summary>Quellen-Datenbank: Seiten, die bei Recherchen Belege lieferten oder die die KI als Fundort nennt.</summary>
public partial class ToolsViewModel
{
    public ObservableCollection<SiteRow> Sites { get; } = new();
    [ObservableProperty] public partial string SiteFilter { get; set; } = "";
    [ObservableProperty] public partial string SitesSummary { get; set; } = "";
    partial void OnSiteFilterChanged(string value) => RefreshSites();

    /// <summary>Neu laden (beim Öffnen des Reiters – Recherchen ergänzen die Datenbank laufend).</summary>
    public void RefreshSites()
    {
        var all = SiteCatalog.Load();
        var filter = SiteFilter.Trim();
        Sites.Clear();
        foreach (var e in all.Where(e => filter.Length == 0
                                        || e.Domain.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                        || e.Topics.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                        || e.Languages.Any(l => l.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                                        || (e.Note ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.Origin).ThenByDescending(e => e.Uses).ThenByDescending(e => e.Suggestions).ThenBy(e => e.Domain))
            Sites.Add(new SiteRow(e));
        SitesSummary = all.Count == 0
            ? L.T("Noch leer – die Datenbank füllt sich mit jeder Recherche zum Dokument.")
            : L.T($"{all.Count} Seiten, davon {all.Count(e => e.Origin == SiteOrigin.Evidence)} mit Belegen.");
    }

    [RelayCommand]
    private async Task OpenSite(SiteRow? row)
    {
        if (row == null || AppServices.Platform == null) return;
        try
        {
            await Core.Privacy.Outbound.CheckAsync(row.Domain, Core.Privacy.OutboundKind.Web, L.T("Im Browser öffnen:") + " https://" + row.Domain, false);
            await AppServices.Platform.OpenUrlAsync("https://" + row.Domain);
        }
        catch (Core.Privacy.OutboundDeclinedException ex) { SitesSummary = ex.Message; }
    }

    [RelayCommand]
    private void RemoveSite(SiteRow? row)
    {
        if (row == null) return;
        SiteCatalog.Remove(row.Domain);
        RefreshSites();
    }
}
