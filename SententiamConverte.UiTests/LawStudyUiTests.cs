using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Wissen → Einarbeiten: ein Gesetz laden, Begriffe mit Fundstelle in Terminologie, Wissen und eigene Fachtexte.</summary>
public class LawStudyUiTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <dokumente>
          <norm><metadaten><jurabk>StGB</jurabk><langue>Strafgesetzbuch</langue>
            <standangabe><standtyp>Neuf</standtyp><standkommentar>Neugefasst durch Bek. v. 13.11.1998 I 3322</standkommentar></standangabe></metadaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 32</enbez><titel format="parat">Notwehr</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(2) Notwehr ist die Verteidigung, die erforderlich ist, um einen gegenwärtigen rechtswidrigen Angriff von sich oder einem anderen abzuwenden.</P></Content></text></textdaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 242</enbez><titel format="parat">Diebstahl</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(1) Wer eine fremde bewegliche Sache einem anderen in der Absicht wegnimmt, die Sache sich rechtswidrig zuzueignen, wird bestraft.</P></Content></text></textdaten></norm>
        </dokumente>
        """;

    private const string English = """
        <html><body>
        <p style="text-align: center; font-weight: bold"><a name="p0300"><!----></a>Section 32<br />Self-defence</p>
        <p style="text-align: center; font-weight: bold"><a name="p2200"><!----></a>Section 242<br />Theft</p>
        </body></html>
        """;

    [AvaloniaFact]
    public async Task StudiesALawIntoTermbaseKnowledgeAndOwnTexts()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                byte[] body;
                switch (ctx.Request.Url!.AbsolutePath)
                {
                    case "/stgb/xml.zip":
                        using (var ms = new MemoryStream())
                        {
                            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                            using (var w = new StreamWriter(zip.CreateEntry("BJNR001270871.xml").Open(), new UTF8Encoding(false)))
                                w.Write(Xml);
                            body = ms.ToArray();
                        }
                        break;
                    case "/englisch_stgb/englisch_stgb.html": body = Encoding.UTF8.GetBytes(English); break;
                    case "/robots.txt": body = "User-agent: *\nDisallow:\n"u8.ToArray(); break;
                    default: ctx.Response.StatusCode = 404; body = []; break;
                }
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        var oldBase = LawSources.GiiBase;
        LawSources.GiiBase = $"http://localhost:{port}";
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 1000 };
            window.Show();
            vm.SelectedTab = MainViewModel.KnowledgeTab;
            var k = vm.Knowledge;
            k.OnProjectChanged();
            Assert.Null(k.SelectedBase);                                            // frische Installation: noch keine Wissensdatenbank
            k.StudyAbbreviation = "StGB";
            k.StudyTargetLanguage = "en-GB";
            await k.StudyCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();                                           // Fortschrittsmeldungen kommen über den UI-Thread
            Assert.Equal(["§ 32 · Notwehr → Self-defence", "§ 242 · Diebstahl → Theft"], k.StudiedTerms.Select(r => r.Header));
            Assert.Equal("Legaldefinition", k.StudiedTerms[0].Kind);
            Assert.Contains("Fertig: 2 Begriffe in der Terminologie, 2 Einträge im Wissen, 2 Paragraphen durchsuchbar.", k.StudyLog);
            Assert.Equal("StGB eingearbeitet: 2 Begriffe in „Terminologie de-DE–en-GB“, 2 Einträge in „Wissen de-DE“, 2 Paragraphen in den eigenen Fachtexten.", vm.Status);
            var memory = k.SelectedBase!;                                           // die angelegte, an Deutsch gebundene Datenbank ist gewählt
            Assert.Equal("Wissen de-DE", memory.Name);

            // gemeinsame Terminologie des Sprachpaars: deutsch bevorzugt, englisch zugelassen, Fundstelle in der Definition
            var (_, path, created) = TranslatedFiles.SharedDatabase(ResourceKind.Termbase, "de-DE", "en-GB", null);
            Assert.False(created);
            using (var tb = new Termbase(path))
            {
                var concept = tb.Lookup("de-DE", "Diebstahl").Single(c => c.Terms.Any(t => t.Text == "Diebstahl"));
                Assert.Contains(concept.Terms, t => t is { Text: "Theft", Status: TermStatus.Admitted });
                Assert.Contains("(§ 242 StGB)", concept.Definition);
            }
            // Wissen: Legaldefinition mit Stand
            var entry = memory.Search("de-DE", "Notwehr").First(e => e.Title == "Notwehr");
            Assert.Equal(KnowledgeKind.Definition, entry.Kind);
            Assert.StartsWith("§ 32 StGB (Stand: Neugefasst", entry.SourceTitle);
            // eigene Fachtexte: das Gesetz steht als Quelle in der Liste, die Fundstelle nennt den Paragraphen
            var source = Assert.Single(vm.Resources.LibraryFiles);
            Assert.Equal(("StGB – Strafgesetzbuch", true), (source.Name, source.IsSource));
            var hit = Assert.Single(ReferenceLibrary.Shared(ReferenceLibrarySettings.Load().IndexPath).FindPhrase("bewegliche Sache", "de-DE"));
            Assert.Equal("StGB – Strafgesetzbuch, § 242 Diebstahl", hit.Label);

            // im Reiter „Einarbeiten“ sichtbar
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => Equals(i.Header, "Einarbeiten")));
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(i => Equals(i.Header, "Einarbeiten"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "§ 242 · Diebstahl → Theft");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "50-gesetz-einarbeiten.png"));

            // zweites Einarbeiten legt nichts doppelt an
            await k.StudyCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();                                           // Fortschrittsmeldungen kommen über den UI-Thread
            Assert.Contains("Fertig: 0 Begriffe in der Terminologie, 0 Einträge im Wissen", k.StudyLog);
        }
        finally
        {
            LawSources.GiiBase = oldBase;
            ReferenceLibrary.CloseShared();
        }
    }
}
