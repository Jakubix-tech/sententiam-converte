using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SententiamConverte.Core.Agents;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>
/// Recherche, die weiterläuft: Reiterwechsel unterbricht nichts, jedes Dossier erscheint sofort, und das gewählte Segment
/// zeigt bei offenem Recherche-Reiter seine Ausdrücke mit Bedeutung und Zielsprache – was fehlt, wird gleich recherchiert.
/// </summary>
public class ResearchQueueUiTests
{
    [AvaloniaFact]
    public async Task SegmentExpressionsAreResearchedAndKeepRunningAcrossTabs()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("dichtung.txt", "Die GLRD ist verschlissen.\nDer Spaltring sitzt am Laufrad.\n"u8.ToArray()));
        var release = new TaskCompletionSource();
        var researched = new List<string>();
        var oldFind = EditorViewModel.FindOverride;
        EditorViewModel.FindOverride = segments => Task.FromResult(segments[0].Source.Contains("GLRD")
            ? new List<ResearchItem>
            {
                new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Medium, SegmentIds = [1], Context = "Die GLRD ist verschlissen." },
            }
            : new List<ResearchItem>
            {
                new() { Expression = "Spaltring", Category = "term", Difficulty = ResearchDifficulty.Medium, SegmentIds = [2], Context = "Der Spaltring sitzt am Laufrad." },
            });
        EditorViewModel.ResearchOverride = async (item, from, to, ct) =>
        {
            await release.Task.WaitAsync(ct);                                     // läuft, bis der Test sie freigibt
            researched.Add(item.Expression);
            return new TermDossier
            {
                Expression = item.Expression, SourceLanguage = from, TargetLanguage = to, SegmentIds = item.SegmentIds,
                Meaning = item.Expression == "GLRD" ? "Gleitringdichtung: Dichtung an rotierenden Wellen." : "Verschleißring zwischen Laufrad und Gehäuse.",
                Recommended = item.Expression == "GLRD" ? "mechanical seal" : "wear ring",
            };
        };
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Dichtung " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;

            // Recherche-Reiter offen, Segment gewählt: Ausdruck gefunden, klar als „wird recherchiert“ gekennzeichnet
            editor.SidePanelTab = 8;
            editor.Selected = editor.Segments[0];
            await WaitFor(() => editor.IsResearching);
            Assert.Equal("Recherche ⏳", editor.ResearchTabHeader);
            var row = Assert.Single(editor.SegmentExpressions);
            Assert.Equal("GLRD  →  wird recherchiert …", row.Header);
            Assert.Equal("Zu „GLRD“ habe ich noch nichts – ich recherchiere jetzt.", editor.SegmentResearchNote);

            // Reiter wechseln und anderes Segment wählen: nichts wird abgebrochen
            editor.SidePanelTab = 0;
            editor.Selected = editor.Segments[1];                                  // Recherche-Reiter zu: dieses Segment wartet
            Dispatcher.UIThread.RunJobs();
            Assert.True(editor.IsResearching);
            release.SetResult();
            await WaitFor(() => !editor.IsResearching);
            Assert.Equal(["GLRD"], researched);
            Assert.Equal(0, editor.SidePanelTab);                                  // kein Sprung zurück in den Recherche-Reiter
            Assert.Equal("Recherche", editor.ResearchTabHeader);

            // zurück zum ersten Segment, Reiter wieder offen: Bedeutung und Zielsprache stehen da
            editor.Selected = editor.Segments[0];
            editor.SidePanelTab = 8;
            await WaitFor(() => !editor.IsResearching && !editor.IsScanning);
            row = Assert.Single(editor.SegmentExpressions);
            Assert.Equal(("GLRD  →  mechanical seal   [Güte D]", "Gleitringdichtung: Dichtung an rotierenden Wellen.", true), (row.Header, row.Meaning, row.IsResearched));   // ohne Beleg: Güte D
            Assert.Equal("", editor.SegmentResearchNote);
            Assert.Single(editor.Dossiers);

            // zweites Segment: wird jetzt (Reiter offen) gefunden und gleich recherchiert
            editor.Selected = editor.Segments[1];
            await WaitFor(() => researched.Count == 2 && !editor.IsResearching);
            Assert.Equal("Spaltring  →  wear ring   [Güte D]", Assert.Single(editor.SegmentExpressions).Header);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Spaltring  →  wear ring   [Güte D]");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "53-recherche-segment.png"));
        }
        finally
        {
            EditorViewModel.FindOverride = oldFind;
            EditorViewModel.ResearchOverride = null;
        }
    }

    [AvaloniaFact]
    public async Task TranslatingEmptySegmentsCanResearchEachSegmentFirst()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var prompts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                prompts.Enqueue((string?)System.Text.Json.Nodes.JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "");
                var bytes = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
                {
                    ["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        { ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "Translated." } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var platform = new FakePlatform();
        platform.FilesToPick.Add(("glrd.txt", "Die GLRD ist verschlissen.\nDer Spaltring sitzt am Laufrad.\nDie GLRD wird getauscht.\n"u8.ToArray()));
        var researched = new List<string>();
        var before = (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride);
        EditorViewModel.FindOverride = segments =>
        {
            var source = segments[0].Source;
            var list = new List<ResearchItem>();
            if (source.Contains("GLRD")) list.Add(new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Medium, SegmentIds = [1, 3] });
            if (source.Contains("Spaltring")) list.Add(new() { Expression = "Spaltring", Category = "term", Difficulty = ResearchDifficulty.Medium, SegmentIds = [2] });
            return Task.FromResult(list);
        };
        EditorViewModel.ResearchOverride = (item, from, to, ct) =>
        {
            researched.Add(item.Expression);
            return Task.FromResult(item.Expression == "GLRD"
                ? new TermDossier
                {
                    Expression = "GLRD", SourceLanguage = from, TargetLanguage = to, Recommended = "mechanical seal", Meaning = "Gleitringdichtung",
                    Sources = [new SourceRef("en-GB", "Seals", "https://seals.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." }],
                }
                : new TermDossier { Expression = "Spaltring", SourceLanguage = from, TargetLanguage = to, Recommended = "wear ring" });   // ohne Beleg
        };
        EditorViewModel.BatchResearchAnswerOverride = null;                   // der Dialog soll erscheinen
        try
        {
            var provider = new Core.Ai.AiProviderConfig { Name = "Prüf-KI", Kind = Core.Ai.AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new Core.Ai.AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "GLRD " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;

            // „Leere per KI“: erst die Frage
            var translating = editor.AiTranslateAllCommand.ExecuteAsync(null);
            await WaitFor(() => editor.IsAskingBatchResearch);
            Assert.Contains("übersetzt 3 Segmente", editor.BatchResearchQuestion);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Auch recherchieren?");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "54-leere-per-ki-recherche.png"));
            await Run(editor.BatchResearchYesCommand);
            await translating;
            Dispatcher.UIThread.RunJobs();

            // jedes Segment recherchiert – „GLRD“ steht zweimal, wird aber nur einmal gesucht
            Assert.Equal(["GLRD", "Spaltring"], researched);
            Assert.All(editor.Segments, s => Assert.Equal("Translated.", s.Model.Target));
            var translations = prompts.Where(p => p.Contains("Researched expressions") || p.Contains("Text:")).ToList();
            Assert.Contains(translations, p => p.Contains("Die GLRD ist verschlissen") && p.Contains("GLRD → mechanical seal"));
            Assert.DoesNotContain(translations, p => p.Contains("wear ring"));                     // unbelegt: geht nicht in die Übersetzung
            Assert.Contains("2 Ausdrücke recherchiert, 1 davon belegt", vm.Status);
            Assert.Equal(2, DossierStore.Load(vm.Project!).Count);

            // „Nein“: nur übersetzen, nichts recherchiert
            foreach (var s in editor.Segments) s.Model.Target = "";
            researched.Clear();
            var again = editor.AiTranslateAllCommand.ExecuteAsync(null);
            await WaitFor(() => editor.IsAskingBatchResearch);
            await Run(editor.BatchResearchNoCommand);
            await again;
            Assert.Empty(researched);

            // Neu geöffnet: die gefundenen Ausdrücke sind beim Projekt gespeichert und wieder da – nichts wird neu gesucht
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            Assert.Equal(["GLRD", "Spaltring"], editor.ResearchItems.Select(r => r.Expression).Order());

            // eine Recherche, die nie fertig wird, hält „Leere per KI“ nicht an; je Segment höchstens 4 Ausdrücke, die schwierigsten zuerst.
            // Dafür ohne gespeicherte Ausdrücke – als wäre das Dokument neu
            File.Delete(Path.Combine(vm.Project!.Folder, "recherche-ausdruecke.json"));
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            Assert.Empty(editor.ResearchItems);
            foreach (var s in editor.Segments) s.Model.Target = "";
            EditorViewModel.BatchStepLimit = TimeSpan.FromMilliseconds(300);
            EditorViewModel.BatchResearchBudget = TimeSpan.FromMilliseconds(100);         // die Schrittgrenze ist mindestens das doppelte Budget
            EditorViewModel.FindOverride = segments => Task.FromResult(Enumerable.Range(1, 6).Select(n => new ResearchItem
            {
                Expression = $"Wort{segments[0].Id}-{n}", Category = "term", SegmentIds = [segments[0].Id],
                Difficulty = n == 6 ? ResearchDifficulty.Hard : n == 1 ? ResearchDifficulty.Easy : ResearchDifficulty.Medium,
            }).ToList());
            EditorViewModel.ResearchOverride = async (item, from, to, ct) =>
            {
                researched.Add(item.Expression);
                if (item.Expression == "Wort2-6") await Task.Delay(Timeout.Infinite, ct);   // antwortet nie
                return new TermDossier { Expression = item.Expression, SourceLanguage = from, TargetLanguage = to };
            };
            // feste kleine Zeit statt der intelligenten Bewertung (die vergäbe Minuten): ohne Dialog, mit dem Budget oben
            EditorViewModel.BatchResearchAnswerOverride = true;
            await Run(editor.AiTranslateAllCommand);
            EditorViewModel.BatchResearchAnswerOverride = null;
            Assert.All(editor.Segments, s => Assert.Equal("Translated.", s.Model.Target));
            Assert.Equal(12, researched.Count);
            Assert.Contains("Wort2-6", researched);                                        // der schwierigste zuerst
            Assert.DoesNotContain("Wort1-1", researched);                                  // der leichteste fällt weg
            Assert.Contains("Recherche „Wort2-6“: dauerte länger als", editor.AiLog);

            // „Ausgewählte per KI“: nur die markierten (Strg/Umschalt+Klick), auch schon übersetzte; bestätigte bleiben
            EditorViewModel.BatchStepLimit = TimeSpan.FromMinutes(4);
            EditorViewModel.BatchResearchBudget = TimeSpan.FromMinutes(2);
            foreach (var s in editor.Segments) s.Model.Target = "alt";
            editor.Segments[2].Model.Status = Core.Model.SegmentStatus.Translated;           // bestätigt
            var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SegmentList");
            list.Selection.Clear();
            list.Selection.Select(0);
            list.Selection.Select(2);
            Assert.Equal([1, 3], editor.MarkedSegments.Select(s => s.Id).OrderBy(i => i));
            researched.Clear();
            EditorViewModel.BatchResearchAnswerOverride = false;
            await Run(editor.AiTranslateSelectedCommand);
            Assert.Equal(["Translated.", "alt", "alt"], editor.Segments.Select(s => s.Model.Target));
            Assert.Empty(researched);

            // ein einzelnes Segment: im Dialog die Zeit je Ausdruck wählen; recherchiert werden alle seine Ausdrücke
            list.Selection.Clear();
            list.Selection.Select(1);
            EditorViewModel.BatchResearchAnswerOverride = null;
            EditorViewModel.FindOverride = segments => Task.FromResult(Enumerable.Range(1, 6).Select(n => new ResearchItem
            {
                Expression = $"Neu{segments[0].Id}-{n}", Category = "term", SegmentIds = [segments[0].Id], Difficulty = ResearchDifficulty.Medium,
            }).ToList());
            File.Delete(Path.Combine(vm.Project!.Folder, "recherche-ausdruecke.json"));     // gefundene Ausdrücke vergessen (sonst gespeichert)
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            list.Selection.Clear();
            list.Selection.Select(1);
            var one = editor.AiTranslateSelectedCommand.ExecuteAsync(null);
            await WaitFor(() => editor.IsAskingBatchResearch);
            Assert.True(editor.BatchIsSingleSegment);
            Assert.Contains("übersetzt das gewählte Segment", editor.BatchResearchQuestion);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && c.ItemsSource == editor.ResearchTimeOptions);
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "56-ausgewaehlte-per-ki-zeit.png"));
            editor.SelectedResearchTime = editor.ResearchTimeOptions[^1];                  // ohne Zeitgrenze
            await Run(editor.BatchResearchYesCommand);
            await one;
            Assert.Equal(6, researched.Count);                                            // alle, nicht nur 4
            Assert.Equal("Translated.", editor.Segments[1].Model.Target);

            // ein neues Projekt: „GLRD“ ist im Recherche-Gedächtnis belegt – übernommen statt neu recherchiert
            researched.Clear();
            platform.FilesToPick.Add(("dicht.txt", "Die GLRD ist wieder dicht.\n"u8.ToArray()));
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Zweites " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files.First(f => f.Name.Contains("dicht")));
            editor = vm.Editor;
            EditorViewModel.FindOverride = segments => Task.FromResult(new List<ResearchItem>
                { new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Medium, SegmentIds = [segments[0].Id] } });
            EditorViewModel.BatchResearchAnswerOverride = true;
            await Run(editor.AiTranslateAllCommand);
            Assert.Empty(researched);
            var reused = DossierStore.Load(vm.Project!).Single(d => d.Expression == "GLRD");
            Assert.Equal("mechanical seal", reused.Recommended);
            Assert.StartsWith("GLRD ", reused.Origin);                                    // Herkunft: das erste Projekt
            Assert.Contains("aus dem Recherche-Gedächtnis übernommen", editor.AiLog);
            Assert.Contains(prompts, p => p.Contains("Die GLRD ist wieder dicht") && p.Contains("GLRD → mechanical seal"));
            editor.IsMemoryExpanded = true;
            Assert.Contains(editor.MemoryEntries, m => m.Header == "GLRD → mechanical seal");
        }
        finally
        {
            (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride) = before;
            EditorViewModel.ResearchOverride = null;
            EditorViewModel.BatchStepLimit = TimeSpan.FromMinutes(4);
            EditorViewModel.BatchResearchBudget = TimeSpan.FromMinutes(2);
        }
    }

    [AvaloniaFact]
    public async Task SmartResearchSkipsNamesDigsDeeperAndRunsInParallel()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var prompts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                prompts.Enqueue((string?)System.Text.Json.Nodes.JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "");
                var bytes = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
                {
                    ["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        { ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "Translated." } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var platform = new FakePlatform();
        platform.FilesToPick.Add(("smart.txt", "Die GLRD der Kreiselpumpe von Raindancer ist verschlissen.\nSie wird getauscht.\nHallo.\n"u8.ToArray()));
        var researched = new List<string>();
        var contexts = new List<string>();
        int active = 0, most = 0;
        var evidence = new SourceRef("en-GB", "Pumps", "https://pumps.example/") { Number = 1, Quote = "The mechanical seal of a centrifugal pump." };
        var before = (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride, EditorViewModel.BatchSmartOverride);
        EditorViewModel.FindOverride = segments => Task.FromResult(segments[0].Source.Contains("GLRD")
            ? new List<ResearchItem>
            {
                new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Hard, SegmentIds = [segments[0].Id] },
                new() { Expression = "Kreiselpumpe", Category = "term", Difficulty = ResearchDifficulty.Medium, SegmentIds = [segments[0].Id] },
                new() { Expression = "Raindancer", Category = "other", Difficulty = ResearchDifficulty.Medium, SegmentIds = [segments[0].Id] },
            }
            : []);                                                                          // „Hallo.“: nichts zu recherchieren
        EditorViewModel.TriageOverride = (segment, items, ct) => Task.FromResult(new Core.Agents.SegmentAssessment("hard", "Abkürzung und Produktname", 0, 1,
        [
            new("GLRD", Core.Agents.TriageAction.Research, 4, "Fachabkürzung"),
            new("Kreiselpumpe", Core.Agents.TriageAction.Research, 1, "bekannter Begriff"),
            new("Raindancer", Core.Agents.TriageAction.Keep, 0, "Produktname"),
        ]));
        EditorViewModel.ResearchOverride = async (item, from, to, ct) =>
        {
            researched.Add(item.Expression);
            contexts.Add(item.Context);
            most = Math.Max(most, ++active);
            await Task.Delay(150, ct);                                                      // so laufen zwei sichtbar gleichzeitig
            active--;
            return item.Expression == "GLRD" && researched.Count(x => x == "GLRD") == 1
                ? new TermDossier { Expression = "GLRD", SourceLanguage = from, TargetLanguage = to, Recommended = "seal" }       // erst unbelegt
                : new TermDossier { Expression = item.Expression, SourceLanguage = from, TargetLanguage = to, Conclusive = true,
                    Recommended = item.Expression == "GLRD" ? "mechanical seal" : "centrifugal pump", Sources = [evidence] };
        };
        var checks = new List<string>();
        EditorViewModel.SufficiencyOverride = (dossier, ct) =>
        {
            checks.Add(dossier.Expression);
            return Task.FromResult(new Core.Agents.SufficiencyVerdict(false, "ein Beleg in einem Pumpenhandbuch.", ""));
        };
        EditorViewModel.BatchResearchAnswerOverride = true;
        EditorViewModel.BatchSmartOverride = true;
        EditorViewModel.ParallelResearchOverride = 2;
        try
        {
            var provider = new Core.Ai.AiProviderConfig { Name = "Prüf-KI", Kind = Core.Ai.AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new Core.Ai.AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Smart " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files.First(f => f.Name.Contains("smart")));
            var editor = vm.Editor;

            await Run(editor.AiTranslateAllCommand);

            // der Produktname wird nicht recherchiert; GLRD (schwer) und Kreiselpumpe gleichzeitig, GLRD reicht erst nicht
            Assert.DoesNotContain("Raindancer", researched);
            Assert.Equal(2, most);
            Assert.Equal(2, researched.Count(x => x == "GLRD"));
            Assert.Single(researched, x => x == "Kreiselpumpe");
            Assert.Equal(["GLRD"], checks);                                                  // belegt und eindeutig: keine Prüfung nötig
            // Zusammenhang: das Segment und eines danach, wie die Bewertung es will
            Assert.Contains(contexts, c => c.Contains("ist verschlissen.") && c.Contains("Sie wird getauscht."));
            Assert.Contains("Bewertung: schwer", editor.AiLog);
            Assert.Contains("„Raindancer“ bleibt unverändert", editor.AiLog);
            Assert.Contains("„GLRD“: reicht noch nicht – es fehlt: ein Beleg in einem Pumpenhandbuch.", editor.AiLog);
            Assert.Contains("„GLRD“: jetzt belegt und eindeutig.", editor.AiLog);
            Assert.True(DossierStore.IsEvidenced(DossierStore.Load(vm.Project!).Single(d => d.Expression == "GLRD")));
            Assert.All(editor.Segments, s => Assert.Equal("Translated.", s.Model.Target));
            // der Produktname geht als „unverändert lassen“ in die Übersetzung – ohne Recherche, ohne Beleg
            Assert.Contains(prompts, p => p.Contains("- Raindancer → Raindancer; strategy: keep unchanged") && p.Contains("- GLRD → mechanical seal")
                                          && p.Contains("- Kreiselpumpe → centrifugal pump"));
            // die Bewertung steht am Segment (Kennzeichen mit Tooltip)
            Assert.Equal("hard", editor.Segments[0].AssessmentLevel);
            Assert.Contains("„Raindancer“ bleibt unverändert", editor.Segments[0].AssessmentInfo);
            Assert.False(editor.Segments[2].HasAssessment);                                 // „Hallo.“: nichts zu bewerten
            // und die Statistik lernt mit: Stufe, Dauer, belegt – je KI
            var stats = Core.Agents.ResearchStats.All();
            Assert.Equal(3, stats.Count);                                                   // GLRD zweimal, Kreiselpumpe einmal, keine für Raindancer
            Assert.All(stats, s => Assert.Equal("Prüf-KI", s.Model));
            Assert.Equal(2, stats.Count(s => s.Level == ResearchDifficulty.Hard));
        }
        finally
        {
            (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride, EditorViewModel.BatchSmartOverride) = before;
            EditorViewModel.ResearchOverride = null;
            EditorViewModel.TriageOverride = null;
            EditorViewModel.SufficiencyOverride = null;
            EditorViewModel.ParallelResearchOverride = null;
        }
    }

    [AvaloniaFact]
    public async Task ConfirmingTellsWhetherTheRecommendationWasKept()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("dichtung.txt", "Die GLRD ist verschlissen.\nDie GLRD wird getauscht.\nDie Pumpe läuft.\n"u8.ToArray()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Rückmeldung " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "de-DE";
        vm.NewTargets = "en-GB";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        DossierStore.Add(vm.Project!, new TermDossier
        {
            Expression = "GLRD", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = "mechanical seal",
            Candidates = [new() { Term = "mechanical seal" }, new() { Term = "face seal" }],
            Sources = [new SourceRef("en-GB", "Seals", "https://www.din.de/seals") { Number = 1, Quote = "A mechanical seal keeps the pump tight." },
                       new SourceRef("en-GB", "Pumps", "https://pumps.example/a") { Number = 2, Quote = "Replace the mechanical seal." }],
        });
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;
        Assert.Equal("", editor.FeedbackSummary);

        // Empfehlung behalten: übernommen
        editor.Selected = editor.Segments[0];
        editor.Segments[0].Model.Target = "The mechanical seal is worn.";
        await Run(editor.ConfirmAndNextCommand);
        // ein anderer Kandidat an ihrer Stelle: ersetzt
        editor.Selected = editor.Segments[1];
        editor.Segments[1].Model.Target = "The face seal is replaced.";
        await Run(editor.ConfirmAndNextCommand);
        // kein recherchierter Ausdruck: keine Rückmeldung
        editor.Selected = editor.Segments[2];
        editor.Segments[2].Model.Target = "The pump runs.";
        await Run(editor.ConfirmAndNextCommand);

        Assert.Equal([(1, true), (2, false)], Core.Agents.ResearchFeedback.All().Select(e => (int.Parse(e.Key.Split('|')[2]), e.Accepted)));
        Assert.Equal("Übernommen beim Bestätigen (je Gütestufe): A 1/2", editor.FeedbackSummary);
        var din = Core.Resources.SiteCatalog.Load().Single(e => e.Domain == "din.de");
        Assert.Equal((1, 1), (din.Accepted, din.Rejected));
        // die Gütestufe steht am Dossier
        editor.ShowAllDossiers = true;
        Assert.Contains(editor.Dossiers, d => d.GradeText == "Güte A – eindeutig belegt, mit starker Quelle");
    }

    [AvaloniaFact]
    public async Task RephrasedSentencesAreAssignedByTheAi()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("umformuliert.txt", "Die GLRD ist verschlissen.\nDie GLRD wird getauscht.\nDie GLRD tropft.\n"u8.ToArray()));
        var asked = new List<string>();
        EditorViewModel.TermUseOverride = (source, target, dossiers, ct) =>
        {
            asked.Add(target);
            var d = Assert.Single(dossiers);
            var use = target.Contains("seal unit") ? Core.Agents.RecommendationUse.FromAnswer(target, d, "seal unit", "other", null)
                : Core.Agents.RecommendationUse.FromAnswer(target, d, "It", "paraphrased", null);
            return Task.FromResult(new Dictionary<TermDossier, Core.Agents.TermUse> { [d] = use! });
        };
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Umformuliert " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            DossierStore.Add(vm.Project!, new TermDossier
            {
                Expression = "GLRD", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = "mechanical seal",
                Candidates = [new() { Term = "mechanical seal" }, new() { Term = "face seal" }],
                Sources = [new SourceRef("en-GB", "Seals", "https://www.din.de/seals") { Number = 1, Quote = "A mechanical seal keeps the pump tight." }],
            });
            await Run(vm.OpenInEditorCommand, vm.Files.First(f => f.Name.Contains("umformuliert")));
            var editor = vm.Editor;

            // eigene Benennung des Übersetzers: ersetzt; nur umschrieben: zählt nicht; gebeugt dastehend: ohne KI übernommen
            foreach (var (i, text) in new[] { (0, "The seal unit is worn."), (1, "It is replaced."), (2, "The mechanical seals drip.") })
            {
                editor.Selected = editor.Segments[i];
                editor.Segments[i].Model.Target = text;
                await Run(editor.ConfirmAndNextCommand);
            }
            await editor.FeedbackDone;
            Assert.Equal(["The seal unit is worn.", "It is replaced."], asked);                // Segment 3 brauchte die KI nicht
            Assert.Equal([(1, false, "seal unit", "KI-Zuordnung"), (3, true, "mechanical seal", "wörtlich")],
                Core.Agents.ResearchFeedback.All().Select(e => (int.Parse(e.Key.Split('|')[2]), e.Accepted, e.Chosen, e.How)));
            Assert.Contains("Rückmeldung Segment 1: „GLRD“ – ersetzt durch eigene Benennung „seal unit“ (KI-Zuordnung)", editor.AiLog);
            Assert.Contains("Rückmeldung Segment 2: „GLRD“ – umschrieben – zählt nicht (KI-Zuordnung)", editor.AiLog);
            Assert.Equal("Übernommen beim Bestätigen (je Gütestufe): C 1/2", editor.FeedbackSummary);

            // noch einmal bestätigt, unverändert: keine neue Frage an die KI
            editor.Selected = editor.Segments[0];
            await Run(editor.ConfirmAndNextCommand);
            await editor.FeedbackDone;
            Assert.Equal(2, asked.Count);
            // Segment 3 jetzt umformuliert: die frühere Rückmeldung passt nicht mehr und wird zurückgenommen
            editor.Selected = editor.Segments[2];
            editor.Segments[2].Model.Target = "It drips.";
            await Run(editor.ConfirmAndNextCommand);
            await editor.FeedbackDone;
            Assert.Equal([1], Core.Agents.ResearchFeedback.All().Select(e => int.Parse(e.Key.Split('|')[2])));
            Assert.Equal("Übernommen beim Bestätigen (je Gütestufe): C 0/1", editor.FeedbackSummary);
        }
        finally { EditorViewModel.TermUseOverride = null; }
    }

    [AvaloniaFact]
    public async Task MarkedTextIsExplainedInTheResearchTab()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("pumpe.txt", "Die GLRD muss wieder arbeitsfähig werden.\nDer Spaltring sitzt am Laufrad.\n"u8.ToArray()));
        var asked = new List<(string Text, bool InTarget)>();
        EditorViewModel.ExplainOverride = (text, inTarget, source, target, ct) =>
        {
            asked.Add((text, inTarget));
            return Task.FromResult(new Core.Agents.SelectionExplanation(
                "wieder einsetzbar sein", "Infinitiv „werden“ mit prädikativem Adjektiv, abhängig vom Modalverb „muss“", "feste Verbindung „arbeitsfähig werden“, sachlich",
                inTarget ? "wieder arbeitsfähig" : "back in working order", "trifft", ["operational again"]));
        };
        var before = EditorViewModel.MarkDelay;
        EditorViewModel.MarkDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Markierung " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            DossierStore.Add(vm.Project!, new TermDossier
            {
                Expression = "GLRD", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = "mechanical seal", Meaning = "Gleitringdichtung",
                Sources = [new SourceRef("en-GB", "Seals", "https://seals.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." }],
            });
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;
            editor.AutoResearchSegment = false;
            editor.Segments[0].Model.Target = "The mechanical seal must be back in working order.";
            editor.Segments[0].Refresh();
            editor.SidePanelTab = 8;                                                     // Reiter Recherche
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var boxes = window.GetVisualDescendants().OfType<Controls.SegmentTextBox>().Where(b => b.DataContext == editor.Segments[0]).ToList();
            var source = boxes.First(b => b.SourceText == null);
            var target = boxes.First(b => b.SourceText != null);

            // im Ausgangstext markiert: Bedeutung, Grammatik, Sprachwissenschaft, Gegenstelle – und was die Recherche hat
            source.TextArea.Focus();
            var at = source.Text.IndexOf("GLRD muss wieder arbeitsfähig", StringComparison.Ordinal);
            source.Select(at, "GLRD muss wieder arbeitsfähig".Length);
            await WaitFor(() => editor.MarkedSections.Count > 0);
            Assert.Equal(("GLRD muss wieder arbeitsfähig", false), asked.Single());
            Assert.Contains("Markierung im Ausgangstext (Segment 1)", editor.MarkedHeader);
            Assert.Equal(["Bedeutung im Satz", "Grammatik", "Sprachwissenschaftlich", "In der Übersetzung", "Einschätzung", "Alternativen", "Aus der Recherche"],
                editor.MarkedSections.Select(s => s.Label));
            Assert.Contains("GLRD → mechanical seal (Güte C – belegt, aber nicht eindeutig)", editor.MarkedSections.Last().Text);   // eine Quelle
            Assert.True(editor.CanResearchMarked);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Grammatik");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "58-markierung-erklaert.png"));

            // dieselbe Markierung noch einmal: aus dem Zwischenspeicher, keine neue Anfrage
            source.Select(0, 0);
            source.Select(at, "GLRD muss wieder arbeitsfähig".Length);
            await WaitFor(() => editor.MarkedSections.Count > 0 && !editor.IsExplainingMarked);
            Assert.Single(asked);

            // in der Übersetzung markiert
            target.TextArea.Focus();
            target.Select(target.Text.IndexOf("back in working order", StringComparison.Ordinal), "back in working order".Length);
            await WaitFor(() => editor.MarkedHeader.Contains("Übersetzung"));
            Assert.Equal(("back in working order", true), asked.Last());
            await WaitFor(() => editor.MarkedSections.Any(s => s.Label == "Im Ausgangstext"));

            // Markierung im Feld aufgehoben: wieder die gewohnte Ansicht
            target.Select(0, 0);
            await WaitFor(() => !editor.HasMarkedText);
            Assert.Empty(editor.MarkedSections);

            // ohne offenen Reiter Recherche: nichts
            editor.SidePanelTab = 0;
            source.TextArea.Focus();
            source.Select(0, 3);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.False(editor.HasMarkedText);
        }
        finally
        {
            EditorViewModel.ExplainOverride = null;
            EditorViewModel.MarkDelay = before;
        }
    }

    [AvaloniaFact]
    public async Task ImprovingWordingShowsFindingsPerSegmentAndInAReport()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("pumpe.txt", "Die GLRD ist verschlissen.\nDer Spaltring sitzt am Laufrad.\n"u8.ToArray()));
        var seen = new List<(string Target, int Research)>();
        EditorViewModel.WebCheckOverride = (source, target, research, ct) =>
        {
            seen.Add((target, research.Count));
            var example = new UsageExample("https://pumps.example/wear", "Pump parts", "the wear ring") { Sentence = "The wear ring protects the impeller.", Number = 1 };
            return Task.FromResult(target.Contains("gap ring")
                ? new TranslationWebCheckResult(target,
                    [new PhraseCheck("gap ring", "Spaltring", [new PhraseCount("gap ring", 0, 0, 10, []), new PhraseCount("wear ring", 120, 7, 10, [example])],
                        PhraseVerdict.Wrong, "wear ring", "„gap ring“ ist im Englischen nicht üblich, „wear ring“ schon.")],
                    "The wear ring sits on the impeller.", "Eine Wendung war falsch.", "Prüf-KI", "Wikipedia")
                : new TranslationWebCheckResult(target, [new PhraseCheck("mechanical seal", "GLRD", [new PhraseCount("mechanical seal", 900, 9, 10, [])],
                    PhraseVerdict.Fine, "mechanical seal", "Üblich.")], null, "Alles üblich.", "Prüf-KI", "Wikipedia"));
        };
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Pumpe " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            DossierStore.Add(vm.Project!, new TermDossier
            {
                Expression = "GLRD", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = "mechanical seal", Meaning = "Gleitringdichtung",
                Reasoning = "Fachbegriff der Pumpentechnik.",
                Sources = [new SourceRef("en-GB", "Seals", "https://seals.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." }],
            });
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;
            editor.Segments[0].Model.Target = "The mechanical seal is worn.";
            editor.Segments[1].Model.Target = "The gap ring sits on the impeller.";

            await Run(editor.ImproveWordingCommand, "all");

            Assert.Equal([("The mechanical seal is worn.", 1), ("The gap ring sits on the impeller.", 0)], seen);   // Recherche zu jedem Teil
            Assert.Equal("The wear ring sits on the impeller.", editor.Segments[1].Model.Target);
            Assert.Equal("The mechanical seal is worn.", editor.Segments[0].Model.Target);
            Assert.Equal("Formulierungen geprüft: 2 Segmente, 1 verbessert.", vm.Status);
            Assert.Equal(["Segment 1", "Segment 2"], editor.SegmentFindings.Select(f => f.Header));
            Assert.Contains("1 von 1 Wendungen fraglich – verbessert", editor.SegmentFindings[1].Summary);
            Assert.StartsWith("1 Ausdrücke recherchiert (1 belegt): GLRD → mechanical seal", editor.SegmentFindings[0].Summary);

            // angeklickt: Einzelheiten mit Erklärung und Belegen
            await Run(editor.OpenFindingCommand, editor.SegmentFindings[1]);
            Assert.Equal((editor.Segments[1], 7), (editor.Selected, editor.SidePanelTab));
            Assert.Contains("Verbessert: „The gap ring sits on the impeller.“ → „The wear ring sits on the impeller.“", editor.AiResult);
            Assert.Contains("„gap ring“ ist im Englischen nicht üblich", editor.AiResult);
            await Run(editor.OpenFindingCommand, editor.SegmentFindings[0]);
            Assert.Contains("GLRD → mechanical seal (belegt – ging in die Übersetzung ein)", editor.AiResult);
            Assert.Contains("Begründung: Fachbegriff der Pumpentechnik.", editor.AiResult);

            // Bericht: Sätze, Ausdrücke, Begründung, Belege mit Sprung-Link
            await Run(editor.CreateReportCommand);
            var html = File.ReadAllText(editor.LastReportPath!);
            Assert.Contains("Übersetzungsbericht – pumpe", html);
            Assert.Contains("<del>The gap ring sits on the impeller.</del>", html);
            Assert.Contains("mechanical seal", html);
            Assert.Contains("https://seals.example/#:~:text=A%20mechanical%20seal%20keeps%20the%20pump%20tight.", html);
            Assert.Contains("https://pumps.example/wear#:~:text=", html);

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "55-formulierungen-verbessern.png"));
        }
        finally
        {
            EditorViewModel.WebCheckOverride = null;
        }
    }

    [AvaloniaFact]
    public async Task AutomaticTranslationRunsAllStepsAndOpensTheReport()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var prompts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                prompts.Enqueue((string?)System.Text.Json.Nodes.JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "");
                var bytes = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
                {
                    ["choices"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                        { ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = "Translated." } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var platform = new FakePlatform();
        platform.FilesToPick.Add(("pumpe.txt", "Die GLRD ist verschlissen.\nDer Spaltring sitzt am Laufrad.\nDie GLRD wird getauscht.\n"u8.ToArray()));
        var researched = new List<string>();
        var evaluations = new List<int[]>();
        var before = (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride, EditorViewModel.BatchWebCheckOverride);
        EditorViewModel.FindOverride = segments =>
        {
            var source = segments[0].Source;
            var list = new List<ResearchItem>();
            if (source.Contains("GLRD")) list.Add(new() { Expression = "GLRD", Category = "abbreviation", Difficulty = ResearchDifficulty.Medium, SegmentIds = [1, 3] });
            if (source.Contains("Spaltring")) list.Add(new() { Expression = "Spaltring", Category = "term", Difficulty = ResearchDifficulty.Medium, SegmentIds = [2] });
            return Task.FromResult(list);
        };
        EditorViewModel.ResearchOverride = (item, from, to, ct) =>
        {
            researched.Add(item.Expression);
            var evidence = new SourceRef("en-GB", "Pumps", "https://pumps.example/") { Number = 1, Quote = "A mechanical seal and a wear ring keep the pump tight." };
            return Task.FromResult(item.Expression == "GLRD"
                // erst unbelegt („seal“), die Nachrecherche findet die belegte Fassung
                ? researched.Count(x => x == "GLRD") == 1
                    ? new TermDossier { Expression = "GLRD", SourceLanguage = from, TargetLanguage = to, Recommended = "seal", Rounds = 6, Conclusive = false }
                    : new TermDossier { Expression = "GLRD", SourceLanguage = from, TargetLanguage = to, Recommended = "mechanical seal", Sources = [evidence], Conclusive = true }
                : new TermDossier { Expression = "Spaltring", SourceLanguage = from, TargetLanguage = to, Recommended = "wear ring", Sources = [evidence], Conclusive = true });
        };
        EditorViewModel.EvaluateOverride = (segments, ct) =>
        {
            evaluations.Add(segments.Select(s => s.Id).ToArray());
            var found = new Dictionary<int, List<Core.Qa.LqaError>>();
            if (evaluations.Count == 1)                                                // das erste Lektorat bemängelt Segment 2
                found[2] = [new("Sprache: Unidiomatisch", Core.Qa.LqaSeverity.Minor, "„Translated“ → „Fitted“: passt besser zum Bauteil", Core.Agents.QualityEvaluator.Reviewer, DateTime.UtcNow)];
            return Task.FromResult(found);
        };
        EditorViewModel.BatchResearchAnswerOverride = true;
        EditorViewModel.BatchWebCheckOverride = false;
        try
        {
            var provider = new Core.Ai.AiProviderConfig { Name = "Prüf-KI", Kind = Core.Ai.AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new Core.Ai.AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Auto " + Guid.NewGuid().ToString("N")[..4];
            vm.NewSource = "de-DE";
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;

            await Run(editor.AutoTranslateCommand);
            Dispatcher.UIThread.RunJobs();

            // jeder Schritt lief
            Assert.Equal(7, editor.PipelineSteps.Count);
            Assert.All(editor.PipelineSteps, s => Assert.Equal("fertig", s.State));
            // übersetzt mit Recherche; die Nachrecherche machte „GLRD“ belegt – Segmente 1 und 3 neu übersetzt, jetzt mit der Fassung
            Assert.Equal(["GLRD", "Spaltring", "GLRD"], researched);
            Assert.Contains(prompts, p => p.Contains("Die GLRD wird getauscht") && p.Contains("GLRD → mechanical seal"));
            // die Verbesserung des Lektorats ist übernommen, danach wurden die geänderten Segmente erneut bewertet
            Assert.Equal(["Translated.", "Fitted.", "Translated."], editor.Segments.Select(s => s.Model.Target));
            Assert.Single(editor.Corrections);
            Assert.Equal([[1, 2, 3], [1, 2, 3]], evaluations);
            Assert.Empty(editor.ReportErrors);
            Assert.Empty(editor.FollowUps);                                               // nichts mehr unsicher
            Assert.Contains("1 Vorschläge übernommen", editor.PipelineSteps[5].Result);
            Assert.Contains("1 Ausdrücke gründlicher recherchiert, 1 belegt; 2 Segmente neu übersetzt", editor.PipelineSteps[4].Result);

            // am Ende: der Bericht ist offen, das Protokoll aufgeklappt
            Assert.True(editor.IsReportOpen);
            Assert.True(editor.IsLogExpanded);
            Assert.Contains("▶ KI-Lektorat", editor.AiLog);
            Assert.Contains("6 Runde(n), 0 Seiten – nicht belegt", editor.AiLog);                    // Dauer je Ausdruck im Protokoll
            Assert.Contains("⏱ „GLRD“: ", editor.AiLog);
            Assert.Contains("Nachrecherche „GLRD“: nicht belegt", editor.AiLog);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Gründlicher recherchieren?");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "57-automatisch-bericht.png"));

            // ohne Nachrecherche bleiben unsichere Ausdrücke im Bericht stehen – mit dem Grund
            editor.Segments[1].Model.Errors = [new("Terminologie: Falscher Fachbegriff", Core.Qa.LqaSeverity.Major, "„wear ring“ falsch", "Prüferin", DateTime.UtcNow)];
            var dossiers = DossierStore.Load(vm.Project!);
            dossiers.Single(d => d.Expression == "Spaltring").Sources.Clear();                // nicht mehr belegt
            DossierStore.Add(vm.Project!, dossiers.Single(d => d.Expression == "Spaltring"));
            await Run(editor.OpenReportCommand);
            var follow = Assert.Single(editor.FollowUps);
            Assert.Equal("Spaltring", follow.Candidate.Expression);
            Assert.Contains("nicht belegt", follow.Reasons);
            Assert.Contains("Lektorat: Terminologie: Falscher Fachbegriff in Segment 2 – betrifft diesen Ausdruck", follow.Reasons);
            var error = Assert.Single(editor.ReportErrors);
            Assert.False(error.CanApply);                                                  // ohne Vorschlag: von Hand
            await Run(editor.GoToReportErrorCommand, error);
            Assert.False(editor.IsReportOpen);
            Assert.Equal(2, editor.Selected?.Id);
        }
        finally
        {
            (AppServices.Ai, EditorViewModel.FindOverride, EditorViewModel.BatchResearchAnswerOverride, EditorViewModel.BatchWebCheckOverride) = before;
            EditorViewModel.ResearchOverride = null;
            EditorViewModel.EvaluateOverride = null;
        }
    }

    private static async Task Run(CommunityToolkit.Mvvm.Input.IRelayCommand command, object? parameter = null)
    {
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Bedingung nicht erreicht");
    }
}
