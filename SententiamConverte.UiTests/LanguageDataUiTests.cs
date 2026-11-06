using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Resources;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>„Freie Sprachdaten herunterladen“: suchen, laden, importieren, anbinden – gegen einen lokalen Prüfserver.</summary>
public class LanguageDataUiTests
{
    private static async Task Run(IRelayCommand command)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(null);
        else command.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task FreeLanguageDataIsDownloadedAndAttached()
    {
        // Prüfserver: OPUS-Katalog für de-en und ein kleines TMX
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var baseUrl = $"http://localhost:{port}/";
        var tmx = new MemoryStream();
        using (var gz = new GZipStream(tmx, CompressionLevel.Fastest, true))
            gz.Write(Encoding.UTF8.GetBytes("""<tmx version="1.4"><header srclang="de"/><body><tu><tuv xml:lang="de"><seg>Die Schraube anziehen.</seg></tuv><tuv xml:lang="en"><seg>Tighten the screw.</seg></tuv></tu></body></tmx>"""));
        using var server = new HttpListener();
        server.Prefixes.Add(baseUrl);
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var bytes = ctx.Request.Url!.AbsolutePath.EndsWith(".tmx.gz") ? tmx.ToArray()
                    : Encoding.UTF8.GetBytes(ctx.Request.Url.Query.Contains("source=de")
                        ? $$"""{"corpora":[{"corpus":"ECB","version":"v1","alignment_pairs":1,"size":1,"url":"{{baseUrl}}ecb.tmx.gz"}]}"""
                        : """{"corpora":[]}""");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var oldApi = LanguageData.OpusApi;
        var oldFolder = AppServices.Ui.LanguageDataFolder;
        LanguageData.OpusApi = baseUrl + "opusapi/";
        AppServices.Ui.LanguageDataFolder = Path.Combine(Path.GetTempPath(), "sc-sprachdaten-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = new FakePlatform();
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Sprachdaten";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            vm.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();

            var res = vm.Resources;
            Assert.Equal("en-GB", res.DataTargetLanguage);
            await Run(res.SearchDataSetsCommand);
            Assert.Equal(["ECB", "IATE"], res.DataSets.Select(d => d.Name).Order());
            res.SelectedDataSet = res.DataSets.Single(d => d.Name == "ECB");
            Assert.Contains("Europäischen Zentralbank", res.DataInfo);

            await Run(res.InstallDataSetCommand);
            Assert.Contains("1 Einträge übernommen", res.DataStatus);
            var tm = Assert.Single(vm.Project!.Tms, t => t.Name == "ECB de-en");
            Assert.Equal(1, tm.Count());
            Assert.Contains(res.Attached, a => a.Name.Contains("ECB de-en"));

            var card = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Freie Sprachdaten herunterladen");
            Assert.True(card.IsEffectivelyVisible);
            card.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "30-freie-sprachdaten.png"));
        }
        finally
        {
            LanguageData.OpusApi = oldApi;
            AppServices.Ui.LanguageDataFolder = oldFolder;
            server.Stop();
        }
    }
}
