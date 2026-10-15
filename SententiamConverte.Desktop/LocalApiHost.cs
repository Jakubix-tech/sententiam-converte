using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SententiamConverte.Api;
using SententiamConverte.Services;

namespace SententiamConverte.Desktop;

/// <summary>
/// Die REST-API in der Desktop-App: nur auf diesem Rechner erreichbar (127.0.0.1), geschützt mit Zugangsschlüssel.
/// Sie nutzt dieselbe interne Schnittstelle wie die Erweiterungen (<see cref="AppSdk"/>) – also dasselbe Projekt wie
/// das Fenster; Änderungen erscheinen sofort im Editor.
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
            Ai = () => AppServices.Ai,
            Authorize = t => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t), expected)
                ? new ApiUser(Environment.UserName, CanWrite: true, IsAdmin: true)
                : null,
        }, AppSdk.Service);
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
}
