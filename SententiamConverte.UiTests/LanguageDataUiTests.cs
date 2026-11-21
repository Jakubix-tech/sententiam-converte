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
using SententiamConverte.Core.Remote;
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
        using var home = TestSetup.OwnDataFolder();                        // bindet an Sprachen (gemeinsam)
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
                        ? $$"""
                            {"corpora":[{"corpus":"ECB","version":"v1","alignment_pairs":1,"size":1,"url":"{{baseUrl}}ecb.tmx.gz"},
                             {"corpus":"EMEA","version":"v3","alignment_pairs":1000,"size":200,"url":"{{baseUrl}}emea.tmx.gz"},
                             {"corpus":"OpenSubtitles","version":"v2018","alignment_pairs":9000000,"size":900000,"url":"{{baseUrl}}os.tmx.gz"}]}
                            """
                        : """{"corpora":[]}""");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        var oldApi = LanguageData.OpusApi;
        var oldFolder = AppServices.Ui.LanguageDataFolder;
        LanguageData.OpusApi = baseUrl + "opusapi/";
        AppServices.Ui.LanguageDataFolder = Path.Combine(TestSetup.Home, "sprachdaten-" + Guid.NewGuid().ToString("N")[..8]);
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
            Assert.Equal(("de-DE", "en-GB"), (res.DataSourceLanguage, res.DataTargetLanguage));    // Sprachen des Projekts vorgeschlagen
            await Run(res.SearchDataSetsCommand);
            Assert.Equal(["ECB", "EMEA", "IATE"], res.DataSets.Select(d => d.Name).Order());        // OpenSubtitles nur unter „Alle anzeigen“
            Assert.Contains("3 von 4", res.DataCountText);

            // filtern: Suchtext, Fachgebiet, Art, Lizenz
            res.DataFilterText = "Zentralbank";
            Assert.Equal(["ECB"], res.DataSets.Select(d => d.Name));
            res.DataFilterText = "";
            res.DataCategoryIndex = 1 + LanguageData.Categories.ToList().IndexOf("Medizin und Gesundheit");
            Assert.Equal(["EMEA"], res.DataSets.Select(d => d.Name));
            res.DataCategoryIndex = 0;
            res.DataKindIndex = 2;                                                               // nur Terminologie
            Assert.Equal(["IATE"], res.DataSets.Select(d => d.Name));
            res.DataKindIndex = 0;
            res.ShowAllDataSets = true;
            Assert.Contains(res.DataSets, d => d.Name == "OpenSubtitles");
            res.DataCommercialOnly = true;                                                       // „nur für Forschung“ fällt weg
            Assert.DoesNotContain(res.DataSets, d => d.Name == "OpenSubtitles");
            res.DataSortIndex = 1;                                                               // größte zuerst
            Assert.Equal("EMEA", res.DataSets.First(d => d.Kind == ResourceKind.Tm).Name);
            res.DataCommercialOnly = false;
            res.ShowAllDataSets = false;
            res.DataSortIndex = 0;
            vm.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Freie Sprachdaten herunterladen").BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "48-sprachdaten-filter.png"));
            res.SelectedDataSet = res.DataSets.Single(d => d.Name == "ECB");
            Assert.Contains("Europäischen Zentralbank", res.DataInfo);

            await Run(res.InstallDataSetCommand);
            Assert.Contains("1 Einträge übernommen", res.DataStatus);
            var tm = Assert.Single(vm.Project!.Tms, t => t.Name == "ECB de-en");
            Assert.Equal(1, tm.Count());
            Assert.Contains(res.Attached, a => a.Name.Contains("ECB de-en"));
            Assert.Contains(LanguageResources.Load(), r => r.Location.Contains("ECB de-en"));   // gilt in jedem Projekt de → en

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
