using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SententiamConverte.Api;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;

namespace SententiamConverte.Desktop;

/// <summary>
/// Die REST-API in der Desktop-App: nur auf diesem Rechner erreichbar (127.0.0.1), geschützt mit Zugangsschlüssel.
/// Sie arbeitet mit demselben Projekt wie das Fenster; Änderungen erscheinen sofort im Editor.
/// </summary>
public sealed class LocalApiHost : ILocalApiHost
{
    private WebApplication? _app;

    public string? Url { get; private set; }

    public async Task StartAsync(int port, string token)
    {
        await StopAsync();
        var expected = Encoding.UTF8.GetBytes(token);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSententiamApi(new ApiOptions
        {
            ProjectsRoot = AppServices.Ui.ProjectsRoot,
            Authorize = t => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t), expected)
                ? new ApiUser(Environment.UserName, CanWrite: true, IsAdmin: true)
                : null,
            Ai = () => AppServices.Ai,
            OpenProject = OpenInWindow,
            BeforeProjectAccess = p =>
            {
                // Was im Editor steht, zuerst speichern – sonst arbeitet die API mit einem älteren Stand
                if (AppServices.Main is { } main && ReferenceEquals(main.Project, p))
                    Dispatcher.UIThread.InvokeAsync(main.Editor.SaveIfOpen).GetTask().Wait(TimeSpan.FromSeconds(10));
            },
            DocumentChanged = (p, fileId, lang) => Dispatcher.UIThread.Post(() =>
            {
                if (AppServices.Main is not { } main || !ReferenceEquals(main.Project, p)) return;
                if (main.Editor.CurrentFile?.Id == fileId && main.Editor.CurrentDocument?.TargetLanguage == lang) main.Editor.Reload();
                main.RefreshFiles();
            }),
            Extensions = AppServices.Extensions,
        });
        var app = builder.Build();
        app.MapSententiamApi();
        await app.StartAsync();
        _app = app;
        Url = $"http://127.0.0.1:{port}";
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        Url = null;
    }

    /// <summary>Das im Fenster geöffnete Projekt, wenn der Name passt (Ordner unter dem Projektordner oder Projektname).</summary>
    private static TranslationProject? OpenInWindow(string name)
    {
        if (AppServices.Main?.Project is not { } p) return null;
        var folder = Path.GetFullPath(Path.Combine(AppServices.Ui.ProjectsRoot, name));
        return string.Equals(Path.GetFullPath(p.Folder).TrimEnd(Path.DirectorySeparatorChar), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            ? p
            : null;
    }
}
