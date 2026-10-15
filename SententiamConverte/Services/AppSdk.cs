using System;
using System.IO;
using Avalonia.Threading;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Sdk;

namespace SententiamConverte.Services;

/// <summary>
/// Die interne Programmierschnittstelle der App: eine Instanz für Erweiterungen und die lokale REST-API.
/// Sie arbeitet mit dem Projekt, das im Fenster offen ist – Offenes im Editor wird vorher gespeichert und
/// danach neu geladen.
/// </summary>
public static class AppSdk
{
    private static SententiamService? _service;

    public static SententiamService Service => _service ??= new SententiamService(new SdkOptions
    {
        ProjectsRoot = AppServices.Ui.ProjectsRoot,
        Ai = () => AppServices.Ai,
        OpenProject = OpenInWindow,
        BeforeProjectAccess = SaveEditor,
        DocumentChanged = ReloadEditor,
        Extensions = AppServices.Extensions,
    }, Environment.UserName);

    /// <summary>Das im Fenster geöffnete Projekt, wenn der Name passt (Ordner unter dem Projektordner oder Projektname).</summary>
    public static TranslationProject? OpenInWindow(string name)
    {
        if (AppServices.Main?.Project is not { } p) return null;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(AppServices.Ui.ProjectsRoot, name)));
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Folder)), folder, StringComparison.OrdinalIgnoreCase)
               || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            ? p
            : null;
    }

    /// <summary>Was im Editor steht, zuerst speichern – sonst arbeitet die Schnittstelle mit einem älteren Stand.</summary>
    public static void SaveEditor(TranslationProject project)
    {
        if (AppServices.Main is not { } main || !ReferenceEquals(main.Project, project)) return;
        if (Dispatcher.UIThread.CheckAccess()) main.Editor.SaveIfOpen();
        else Dispatcher.UIThread.InvokeAsync(main.Editor.SaveIfOpen).GetTask().Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Ein Dokument wurde über die Schnittstelle geändert: Editor und Dateiliste aktualisieren.</summary>
    public static void ReloadEditor(TranslationProject project, string fileId, string language) => Dispatcher.UIThread.Post(() =>
    {
        if (AppServices.Main is not { } main || !ReferenceEquals(main.Project, project)) return;
        if (main.Editor.CurrentFile?.Id == fileId && main.Editor.CurrentDocument?.TargetLanguage == language) main.Editor.Reload();
        main.RefreshFiles();
    });
}
