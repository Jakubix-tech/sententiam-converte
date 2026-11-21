using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Eintrag im Protokoll des Datenausgangs.</summary>
public sealed class OutboundRow(OutboundEntry entry)
{
    public OutboundEntry Entry { get; } = entry;
    public string Time => Entry.When.ToString("HH:mm:ss");
    public string Service => Entry.Service;
    public string KindName => OutboundViewModel.KindName(Entry.Kind);
    public string Size => $"{Entry.Characters:N0} " + L.T("Zeichen");

    public string DecisionName => L.T(Entry.Decision switch
    {
        OutboundDecision.Local => "auf diesem Rechner",
        OutboundDecision.Declined => "nicht gesendet",
        OutboundDecision.Blocked => "gesperrt",
        _ => "gesendet",
    });

    public bool LeftComputer => Entry.Decision == OutboundDecision.Sent;
}

/// <summary>
/// Datenausgang: zeigt, was den Rechner verlassen hat (mit genauem Inhalt), fragt auf Wunsch vor dem Senden und
/// sperrt Dienste. Die Einstellungen liegen bei den KI-Einstellungen.
/// </summary>
public partial class OutboundViewModel : ViewModelBase
{
    private TaskCompletionSource<OutboundAnswer>? _pending;

    public OutboundViewModel()
    {
        Outbound.Settings = AppServices.Ai.Outbound;
        Outbound.Approver = AskAsync;
        Outbound.Logged += e => Dispatcher.UIThread.Post(() => Add(e));
        Outbound.SettingsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            AppServices.Ai.Save();
            RefreshBlocked();
        });
        foreach (var e in Outbound.Entries.Reverse()) Rows.Add(new OutboundRow(e));
        Refilter();
        RefreshBlocked();
    }

    public static string KindName(OutboundKind kind) => L.T(kind switch
    {
        OutboundKind.MachineTranslation => "Maschinelle Übersetzung",
        OutboundKind.Dictation => "Diktat",
        OutboundKind.Web => "Suche und Online-Quellen",
        _ => "KI",
    });

    // ---------- Protokoll ----------

    public ObservableCollection<OutboundRow> Rows { get; } = new();

    [ObservableProperty] public partial OutboundRow? Selected { get; set; }

    /// <summary>Nur zeigen, was den Rechner wirklich verlassen hat (ohne lokale KI).</summary>
    [ObservableProperty] public partial bool OnlyOnline { get; set; } = true;

    public ObservableCollection<OutboundRow> Visible { get; } = new();

    public string Summary
    {
        get
        {
            var sent = Rows.Where(r => r.LeftComputer).ToList();
            return sent.Count == 0
                ? L.T("Seit dem Start wurde nichts ins Netz gesendet.")
                : L.T($"Seit dem Start gesendet – Anfragen: {sent.Count}, Dienste: {sent.Select(r => r.Service).Distinct().Count()}, Zeichen: {sent.Sum(r => r.Entry.Characters):N0}.");
        }
    }

    partial void OnOnlyOnlineChanged(bool value) => Refilter();

    private void Add(OutboundEntry e)
    {
        var row = new OutboundRow(e);
        Rows.Insert(0, row);
        while (Rows.Count > 500) Rows.RemoveAt(Rows.Count - 1);
        if (!OnlyOnline || e.Decision != OutboundDecision.Local) Visible.Insert(0, row);
        while (Visible.Count > 500) Visible.RemoveAt(Visible.Count - 1);
        OnPropertyChanged(nameof(Summary));
    }

    private void Refilter()
    {
        Visible.Clear();
        foreach (var r in Rows.Where(r => !OnlyOnline || r.Entry.Decision != OutboundDecision.Local)) Visible.Add(r);
    }

    [RelayCommand]
    private void ClearLog()
    {
        Outbound.Clear();
        Rows.Clear();
        Visible.Clear();
        Selected = null;
        OnPropertyChanged(nameof(Summary));
    }

    // ---------- Einstellungen ----------

    public string[] ModeNames { get; } =
    [
        L.T("Nur festhalten, was gesendet wird"),
        L.T("Beim ersten Senden an einen Dienst fragen"),
        L.T("Vor jeder Anfrage fragen"),
    ];

    public int ModeIndex
    {
        get => (int)Outbound.Settings.Mode;
        set
        {
            if (value < 0 || value == (int)Outbound.Settings.Mode) return;
            Outbound.Settings.Mode = (OutboundMode)value;
            Outbound.ResetSession();
            AppServices.Ai.Save();
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> Blocked { get; } = new();

    [ObservableProperty] public partial string? SelectedBlocked { get; set; }

    private void RefreshBlocked()
    {
        Blocked.Clear();
        foreach (var b in Outbound.Settings.Blocked.Order()) Blocked.Add(b);
    }

    /// <summary>Den Dienst des gewählten Eintrags sperren.</summary>
    [RelayCommand]
    private void BlockSelected()
    {
        if (Selected is not { } row || row.Entry.Local || Outbound.IsBlocked(row.Service)) return;
        Outbound.Settings.Blocked.Add(row.Service);
        AppServices.Ai.Save();
        RefreshBlocked();
    }

    [RelayCommand]
    private void Unblock()
    {
        if (SelectedBlocked is not { } service) return;
        Outbound.Settings.Blocked.RemoveAll(b => b.Equals(service, StringComparison.OrdinalIgnoreCase));
        AppServices.Ai.Save();
        RefreshBlocked();
    }

    // ---------- Rückfrage vor dem Senden ----------

    [ObservableProperty] public partial OutboundRequest? Pending { get; set; }

    public bool IsAsking => Pending != null;
    public string PendingTitle => Pending == null ? "" : L.T($"Daten an {Pending.Service} senden?");
    public string PendingKind => Pending == null ? "" : KindName(Pending.Kind);
    public string PendingContent => Pending?.Content ?? "";

    partial void OnPendingChanged(OutboundRequest? value)
    {
        OnPropertyChanged(nameof(IsAsking));
        OnPropertyChanged(nameof(PendingTitle));
        OnPropertyChanged(nameof(PendingKind));
        OnPropertyChanged(nameof(PendingContent));
    }

    private Task<OutboundAnswer> AskAsync(OutboundRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<OutboundAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetResult(OutboundAnswer.Decline));
        Dispatcher.UIThread.Post(() =>
        {
            _pending = tcs;
            Pending = request;
        });
        return tcs.Task.ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() => { if (_pending == tcs) { _pending = null; Pending = null; } });
            return t.Result;
        }, TaskScheduler.Default);
    }

    private void Answer(OutboundAnswer answer)
    {
        var tcs = _pending;
        _pending = null;
        Pending = null;
        tcs?.TrySetResult(answer);
    }

    [RelayCommand] private void Send() => Answer(OutboundAnswer.Send);
    [RelayCommand] private void SendAlways() => Answer(OutboundAnswer.AlwaysThisService);
    [RelayCommand] private void Decline() => Answer(OutboundAnswer.Decline);
    [RelayCommand] private void BlockService() => Answer(OutboundAnswer.BlockService);

    // ---------- Ablaufprotokoll (Datei) ----------

    [ObservableProperty] public partial bool ActivityLogEnabled { get; set; } = AppServices.Ai.ActivityLog.Enabled;
    [ObservableProperty] public partial string ActivityLogStatus { get; set; } = "";
    public string ActivityLogFolder => Core.Diagnostics.ActivityLog.Folder;

    partial void OnActivityLogEnabledChanged(bool value)
    {
        AppServices.Ai.ActivityLog.Enabled = value;
        Core.Diagnostics.ActivityLog.Apply(AppServices.Ai.ActivityLog);
        AppServices.Ai.Save();
        Core.Diagnostics.ActivityLog.Write("Programm", "Ablaufprotokoll eingeschaltet");     // schreibt nur, wenn eingeschaltet
    }

    [RelayCommand]
    private async Task OpenActivityLog()
    {
        System.IO.Directory.CreateDirectory(ActivityLogFolder);
        if (AppServices.Platform != null) await AppServices.Platform.OpenFileAsync(ActivityLogFolder);
    }

    [RelayCommand]
    private void DeleteActivityLog()
    {
        Core.Diagnostics.ActivityLog.DeleteAll();
        ActivityLogStatus = "Alle Protokolle gelöscht.";
    }
}
