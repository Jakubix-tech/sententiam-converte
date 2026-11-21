using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein gefundener EU-Rechtsakt in der Auswahl: Titel in beiden Sprachen, CELEX, Art.</summary>
public sealed partial class EuActRow(EuAct act) : ObservableObject
{
    public EuAct Act { get; } = act;
    [ObservableProperty] public partial bool IsChecked { get; set; }
    public string Title => Act.Title;
    public string TargetTitle => Act.TargetTitle;
    public string Info => $"CELEX {Act.Celex} · " + L.T(Act.Type switch { 'R' => "Verordnung", 'L' => "Richtlinie", 'D' => "Beschluss", _ => "Rechtsakt" });
}

/// <summary>
/// Thema → TM: Rechtsakte der EU zu einem Thema suchen, auswählen und aus ihren amtlichen Sprachfassungen eine TM
/// aufbauen (Satz für Satz gepaart, mit CELEX als Herkunft). Die TM wird an das Sprachpaar gebunden und gilt damit in
/// jedem Projekt mit diesen Sprachen.
/// </summary>
public partial class KnowledgeViewModel
{
    private CancellationTokenSource? _topicTmCts;

    [ObservableProperty] public partial string TopicTmTopic { get; set; } = "";
    [ObservableProperty] public partial string TopicTmSource { get; set; } = "de-DE";
    [ObservableProperty] public partial string TopicTmTarget { get; set; } = "en-GB";
    [ObservableProperty] public partial string TopicTmName { get; set; } = "";
    [ObservableProperty] public partial bool IsTopicTmBusy { get; set; }
    [ObservableProperty] public partial string TopicTmLog { get; set; } = "";
    [ObservableProperty] public partial bool HasTopicActs { get; set; }

    public ObservableCollection<EuActRow> TopicActs { get; } = new();

    /// <summary>Sprachen aus dem offenen Projekt vorschlagen.</summary>
    private void SuggestTopicTmLanguages()
    {
        if (_main.Project is not { } p) return;
        TopicTmSource = p.SourceLanguage;
        if (p.TargetLanguages.FirstOrDefault() is { } t) TopicTmTarget = t;
        if (TopicTmTopic.Length == 0) TopicTmTopic = p.Topic;
    }

    [RelayCommand]
    private async Task FindTopicActs()
    {
        if (IsTopicTmBusy) return;
        IsTopicTmBusy = true;
        TopicTmLog = "";
        TopicActs.Clear();
        HasTopicActs = false;
        var cts = _topicTmCts = new CancellationTokenSource();
        try
        {
            var acts = await EuLawTm.FindAsync(TopicTmTopic.Trim(), TopicTmSource.Trim(), TopicTmTarget.Trim(), 20, cts.Token);
            foreach (var (act, i) in acts.Select((a, i) => (a, i)))
                TopicActs.Add(new EuActRow(act) { IsChecked = i < 3 });                // die drei passendsten vorausgewählt
            HasTopicActs = TopicActs.Count > 0;
            if (TopicTmName.Trim().Length == 0 || TopicTmName.StartsWith("TM ", StringComparison.Ordinal))
                TopicTmName = $"TM {FirstAlternative(TopicTmTopic)} (EU-Recht) {TopicTmSource.Trim()}–{TopicTmTarget.Trim()}";
            TopicTmLog = acts.Count == 0
                ? "Kein Rechtsakt mit diesem Thema im Titel – ein anderes Wort oder den Wortstamm versuchen (mehrere mit Komma trennen)."
                : $"{acts.Count} Rechtsakte gefunden – die passenden anhaken und „TM aufbauen“.";
        }
        catch (OperationCanceledException) { TopicTmLog = "Abgebrochen."; }
        catch (Exception ex) when (ex is AiException or HttpRequestException or System.Text.Json.JsonException)
        {
            TopicTmLog = "Fehler: " + ex.Message;
        }
        finally { IsTopicTmBusy = false; }
    }

    [RelayCommand]
    private async Task BuildTopicTm()
    {
        if (IsTopicTmBusy) return;
        var chosen = TopicActs.Where(a => a.IsChecked).Select(a => a.Act).ToList();
        if (chosen.Count == 0) { TopicTmLog = "Bitte mindestens einen Rechtsakt anhaken."; return; }
        var src = TopicTmSource.Trim();
        var tgt = TopicTmTarget.Trim();
        var name = TopicTmName.Trim().Length > 0 ? TopicTmName.Trim() : $"TM EU-Recht {src}–{tgt}";
        IsTopicTmBusy = true;
        TopicTmLog = "";
        var cts = _topicTmCts = new CancellationTokenSource();
        TranslationMemory? own = null;
        try
        {
            var path = DatabaseCatalog.SuggestedPath(ResourceKind.Tm, name);
            var p = _main.Project;
            var fits = p != null && Languages.Matches(p.SourceLanguage, src) && p.TargetLanguages.Any(t => Languages.Matches(t, tgt));
            // an die Sprachen binden: gilt in jedem Projekt mit diesem Sprachpaar (beim offenen Projekt gleich angebunden)
            DatabaseCatalog.Create(new DatabaseRecommendation(ResourceKind.Tm, name, path, [src, tgt], ""), fits ? p : null);
            var tm = p?.Tms.OfType<TranslationMemory>().FirstOrDefault(t => SamePath(t.Location, path)) ?? (own = new TranslationMemory(path));
            var result = await EuLawTm.BuildAsync(chosen, src, tgt, tm, new LogLines(m => TopicTmLog += m + "\n"), cts.Token);
            TopicTmLog += $"Fertig: {result.Added} neue Satzpaare aus {result.Acts.Count(a => a.Problem == null)} Rechtsakten in „{name}“ (Quelle: EUR-Lex, © Europäische Union)." + "\n";
            _main.Status = L.T($"„{name}“: {result.Added} neue Satzpaare aus dem EU-Recht.");
            _main.Resources.Refresh();
            _main.Resources.RefreshDatabases();
        }
        catch (OperationCanceledException) { TopicTmLog += "Abgebrochen – was schon gepaart war, ist in der TM." + "\n"; }
        catch (Exception ex) when (ex is AiException or HttpRequestException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            TopicTmLog += "Fehler: " + ex.Message + "\n";
        }
        finally
        {
            own?.Dispose();
            IsTopicTmBusy = false;
        }
    }

    [RelayCommand]
    private void CancelTopicTm() => _topicTmCts?.Cancel();

    [RelayCommand]
    private async Task OpenTopicAct(EuActRow? row)
    {
        if (row != null && AppServices.Platform != null) await AppServices.Platform.OpenUrlAsync(row.Act.Url(TopicTmSource.Trim()));
    }

    /// <summary>Meldungen sofort und in ihrer Reihenfolge ins Protokoll (Progress&lt;T&gt; stellt sie hinten an).</summary>
    private sealed class LogLines(Action<string> append) : IProgress<string>
    {
        public void Report(string value)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) append(value);
            else Avalonia.Threading.Dispatcher.UIThread.Post(() => append(value));
        }
    }

    private static string FirstAlternative(string topic) =>
        topic.Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0) ?? "EU-Recht";
}
