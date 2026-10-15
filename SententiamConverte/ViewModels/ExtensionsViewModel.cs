using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>Eine installierte Erweiterung in der Liste.</summary>
public sealed partial class ExtensionRow : ObservableObject
{
    public InstalledExtension Extension { get; }
    public ExtensionRow(InstalledExtension extension) => Extension = extension;

    public string Id => Extension.Manifest.Id;
    public string Name => Extension.Manifest.Name.Length > 0 ? Extension.Manifest.Name : Extension.Manifest.Id;
    public string Header => $"{Name} {Extension.Manifest.Version}";
    public string Author => "Von: " + (Extension.Manifest.Author ?? "unbekannt");
    public string Description => Extension.Manifest.Description ?? "";
    public bool Enabled => Extension.Manifest.Enabled;
    public bool HasError => Extension.Error != null;

    public string State => Extension.Error != null ? "Fehler beim Laden: " + Extension.Error
        : Extension.Loaded && Extension.Manifest.Enabled ? "Eingeschaltet"
        : Extension.Loaded ? "Ausgeschaltet – bis zum Neustart noch aktiv"
        : Extension.Manifest.Enabled ? "Eingeschaltet – wird beim nächsten Start geladen"
        : "Ausgeschaltet";

    public string Contributions => Extension.Contributions.Count == 0 ? "(wird nach dem Einschalten angezeigt)" : string.Join("\n", Extension.Contributions);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Ein Befehl einer eingeschalteten Erweiterung.</summary>
public sealed record CommandRow(IExtensionCommand Command, string ExtensionName)
{
    public string Name => Command.Name;
    public string Scope => Command.Scope switch
    {
        CommandScope.Segment => "für das gewählte Segment",
        CommandScope.Document => "für das offene Dokument",
        CommandScope.Project => "für das Projekt",
        _ => "allgemein",
    };
    public string Info => $"{Scope} · {ExtensionName}";
    public string Description => Command.Description ?? "";
}

/// <summary>Reiter „Erweiterungen“: installieren, ein-/ausschalten, entfernen und Befehle ausführen.</summary>
public partial class ExtensionsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public ExtensionsViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    private static ExtensionManager Manager => AppServices.Extensions;

    public ObservableCollection<ExtensionRow> Items { get; } = new();
    public ObservableCollection<CommandRow> Commands { get; } = new();
    public string Folder => Manager.Folder;

    [ObservableProperty] public partial ExtensionRow? Selected { get; set; }
    [ObservableProperty] public partial CommandRow? SelectedCommand { get; set; }
    [ObservableProperty] public partial string Arguments { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool ConfirmEnable { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }

    /// <summary>Worauf ein Befehl wirken würde – das, was im Editor gerade offen bzw. gewählt ist.</summary>
    public string ContextInfo
    {
        get
        {
            var ed = _main.Editor;
            if (_main.Project == null) return "Kein Projekt geöffnet.";
            if (ed.CurrentFile == null || ed.CurrentDocument == null) return $"Projekt „{_main.Project.Name}“ · kein Dokument im Editor geöffnet.";
            var seg = ed.Selected != null ? $" · Segment {ed.Selected.Model.Id}" : "";
            return $"Projekt „{_main.Project.Name}“ · {ed.CurrentFile.OriginalName} ({ed.CurrentDocument.TargetLanguage}){seg}";
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        var selectedId = Selected?.Id;
        Items.Clear();
        foreach (var e in Manager.Extensions) Items.Add(new ExtensionRow(e));
        Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Items.FirstOrDefault();
        Commands.Clear();
        foreach (var e in Manager.Extensions.Where(e => e.Loaded))
            foreach (var c in e.Commands) Commands.Add(new CommandRow(c, e.Manifest.Name));
        SelectedCommand ??= Commands.FirstOrDefault();
        OnPropertyChanged(nameof(ContextInfo));
    }

    partial void OnSelectedChanged(ExtensionRow? value) => ConfirmEnable = false;

    [RelayCommand]
    private async Task Install()
    {
        if (AppServices.Platform == null) return;
        var files = await AppServices.Platform.PickFilesAsync("Erweiterung installieren", ["*.scext", "*.zip", "*.dll"], false);
        if (files.Count == 0) return;
        var picked = files[0];
        var temp = Path.Combine(Path.GetTempPath(), "sc-ext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, Path.GetFileName(picked.Name));
            await using (var source = await picked.OpenRead())
            await using (var target = File.Create(path))
                await source.CopyToAsync(target);
            var ext = Manager.Install(path);
            Refresh();
            Selected = Items.FirstOrDefault(i => i.Id == ext.Manifest.Id);
            Message = $"„{Selected?.Name}“ installiert. Sie ist ausgeschaltet, bis Sie sie bewusst einschalten.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Message = "Installation fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    /// <summary>Erst nach Bestätigung: Erweiterungen laufen mit den Rechten des Benutzers.</summary>
    [RelayCommand]
    private void RequestEnable()
    {
        if (Selected is { Enabled: false }) ConfirmEnable = true;
    }

    [RelayCommand]
    private void CancelEnable() => ConfirmEnable = false;

    [RelayCommand]
    private void Enable()
    {
        if (Selected == null) return;
        ConfirmEnable = false;
        Manager.SetEnabled(Selected.Id, true);
        var row = Selected;
        Refresh();
        Message = row.HasError ? "Die Erweiterung ließ sich nicht laden: " + row.Extension.Error : $"„{row.Name}“ ist eingeschaltet.";
    }

    [RelayCommand]
    private void Disable()
    {
        if (Selected == null) return;
        Manager.SetEnabled(Selected.Id, false);
        Refresh();
        Message = "Ausgeschaltet. Vollständig wirksam nach einem Neustart des Programms.";
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected == null) return;
        var name = Selected.Name;
        Manager.Remove(Selected.Id);
        Refresh();
        Message = $"„{name}“ entfernt. Bereits geladene Teile bleiben bis zum Neustart aktiv.";
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (AppServices.Platform != null) await AppServices.Platform.OpenFileAsync(Folder);
    }

    [RelayCommand]
    private async Task RunCommand()
    {
        if (SelectedCommand == null || IsRunning) return;
        var cmd = SelectedCommand.Command;
        var ed = _main.Editor;
        var missing = cmd.Scope switch
        {
            CommandScope.Project when _main.Project == null => "Bitte zuerst ein Projekt öffnen.",
            CommandScope.Document when ed.CurrentDocument == null => "Bitte zuerst ein Dokument im Editor öffnen.",
            CommandScope.Segment when ed.Selected == null => "Bitte im Editor ein Segment wählen.",
            _ => null,
        };
        OnPropertyChanged(nameof(ContextInfo));
        if (missing != null) { Output = missing; return; }

        var args = Arguments.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());
        var ai = AppServices.Ai;
        IChatModel? model = null;
        ISearchProvider? search = null;
        try
        {
            model = ai.Get(ai.TranslateProviderId) is { } provider ? ChatModelFactory.Create(provider) : null;
            search = SearchProviderFactory.Create(ai);
        }
        catch (Exception ex) when (ex is AiException or ArgumentException or InvalidOperationException) { }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        Output = $"▶ {cmd.Name}\n";
        try
        {
            var context = new ExtensionContext
            {
                Project = _main.Project,
                File = ed.CurrentFile,
                Document = ed.CurrentDocument,
                Segment = ed.Selected?.Model,
                Arguments = args,
                ChatModel = model,
                Search = search,
                Log = new Progress<string>(m => Output += m + "\n"),
            };
            var result = await cmd.RunAsync(context, _cts.Token);
            Output += result.Message;
            if (result.DocumentChanged)
            {
                ed.SaveIfOpen();
                ed.Reload();
                _main.RefreshFiles();
            }
        }
        catch (OperationCanceledException) { Output += "Abgebrochen."; }
        catch (Exception ex) { Output += "Fehler: " + ex.Message; }
        finally
        {
            IsRunning = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
