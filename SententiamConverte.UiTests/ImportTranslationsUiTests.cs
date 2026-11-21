using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.UiTests;

/// <summary>Alte Übersetzungen übernehmen: Dateien wählen, automatisch zuordnen, ins TM, Fachbegriffe in die Terminologie.</summary>
public class ImportTranslationsUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    internal static byte[] Docx(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p))))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>Eine kleine „KI“: bestätigt jede Zuordnung und nennt Fachbegriffe (einer davon erfunden).</summary>
    internal static HttpListener FakeAi(out int port)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var system = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().First()?["content"] ?? "";
                var answer = system.Contains("terminologist")
                    ? """{"terms": [{"s": "Mietvertrag", "t": "tenancy agreement"}, {"s": "Kaution", "t": "deposit"}, {"s": "Mieter", "t": "lessee"}]}"""
                    : system.Contains("check sentence alignments")
                        ? """{"items": [{"i": 1, "score": 1}, {"i": 2, "score": 1}, {"i": 3, "score": 1}]}"""
                        : "{}";
                var bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = answer } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        return server;
    }

    [AvaloniaFact]
    public async Task TranslatedFilesArePairedImportedAndYieldTerms()
    {
        using var home = TestSetup.OwnDataFolder();                        // legt gemeinsame Datenbanken an
        using var server = FakeAi(out var port);
        var before = AppServices.Ai;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var platform = new FakePlatform();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Altbestand " + Guid.NewGuid().ToString("N")[..4];
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);

            await Run(vm.OpenImportTranslationsCommand);                                 // Knopf im Reiter Projekt
            var imp = vm.Resources.ImportTranslations;
            Assert.Equal(2, vm.SelectedTab);
            Assert.True(imp.IsOpen && imp.AiReady && imp.UseAi && imp.SuggestTerms && imp.ToShared);

            // Übersetzung zuerst, Original danach – zugeordnet wird trotzdem richtig
            platform.FilesToPick.Add(("Mietvertrag_EN.docx", Docx(
                "The tenant pays the rent by the third working day of the month.",
                "The deposit amounts to three months' rent.",
                "The tenancy agreement may be terminated with three months' notice.")));
            platform.FilesToPick.Add(("Mietvertrag_DE.docx", Docx(
                "Der Mieter zahlt die Miete bis zum dritten Werktag des Monats.",
                "Die Kaution beträgt drei Monatsmieten.",
                "Der Mietvertrag kann mit einer Frist von drei Monaten gekündigt werden.")));
            await Run(imp.PickFilesCommand);
            var row = Assert.Single(imp.Pairs);
            Assert.Equal(("Mietvertrag_DE.docx", "Mietvertrag_EN.docx", "en-GB"), (row.SourceName, row.TargetName, row.TargetLanguage));

            await Run(imp.RunCommand);
            Assert.Contains("3 Satzpaare übernommen", row.Result);
            Assert.False(row.HasDoubts);
            var tmPath = DatabaseCatalog.SuggestedPath(ResourceKind.Tm, "TM de-DE–en-GB");
            Assert.True(File.Exists(tmPath));
            Assert.Contains(vm.Project!.Tms, t => t.Location == tmPath);                // gilt sofort in diesem und jedem Projekt de→en
            Assert.Contains(vm.Project.Tms.First(t => t.Location == tmPath).All(),
                u => u.Source == "Die Kaution beträgt drei Monatsmieten." && u.Target == "The deposit amounts to three months' rent.");

            Assert.Contains(imp.Terms, t => t is { Source: "Mietvertrag", Target: "tenancy agreement" });
            Assert.DoesNotContain(imp.Terms, t => t.Source == "Mieter");                   // „lessee“ steht nirgends
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "39-alte-uebersetzungen.png"));

            Assert.All(imp.Terms, t => Assert.True(t.Selected));                        // belegte Vorschläge sind vorgewählt
            await Run(imp.AddTermsCommand);
            Assert.Empty(imp.Terms);
            var tb = vm.Project.Termbases.OfType<Termbase>().First(t => t.Name == "Terminologie de-DE–en-GB");
            Assert.Contains(tb.AllConcepts(), c => c.Terms.Any(t => t.Text == "tenancy agreement"));

            // Prüfen im Alignment-Editor und zurück
            await Run(imp.ReviewCommand, row);
            Assert.True(vm.Resources.Alignment.IsOpen);
            Assert.False(imp.IsOpen);
            Assert.Equal(3, vm.Resources.Alignment.Rows.Count);
            await Run(vm.Resources.Alignment.CloseCommand);
            Assert.True(imp.IsOpen);
        }
        finally
        {
            AppServices.Ai = before;
            server.Stop();
        }
    }
}
