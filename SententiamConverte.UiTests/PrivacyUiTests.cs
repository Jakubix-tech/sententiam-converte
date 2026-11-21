using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.UiTests;

/// <summary>Datenschutz im Projekt: die KI sieht nur Platzhalter, das TM speichert nur Platzhalter, Treffer bekommen die echten Werte.</summary>
public class PrivacyUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Gefundene Namen prüfen: angehakte in die Schutzliste, abgehakte heraus (auch frühere), andere bleiben.</summary>
    [AvaloniaFact]
    public async Task CheckedNamesAreProtectedAndUncheckedReleased()
    {
        using var home = TestSetup.OwnDataFolder();
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var privacy = vm.Privacy;
        privacy.TermsText = "Anna Beispiel\nPolen\nAlter Eintrag";
        foreach (var c in new[]
                 {
                     new NameCandidate("Anna Beispiel", NameKind.Person, true, "Name"),
                     new NameCandidate("Polen", NameKind.Country, false, "Land, wird übersetzt"),
                     new NameCandidate("Beispiel GmbH", NameKind.Company, true, "kleiner Arbeitgeber"),
                     new NameCandidate("MariaDB", NameKind.Product, false, "Software"),
                 })
            privacy.NameCandidates.Add(new NameCandidateRow(c));
        Assert.Equal(["Person", "Land", "Firma", "Produkt/Software"], privacy.NameCandidates.Select(r => r.KindName));
        Assert.Equal([true, false, true, false], privacy.NameCandidates.Select(r => r.IsChecked));

        await Run(privacy.ApplyNameCandidatesCommand);
        Assert.Equal("Anna Beispiel\nAlter Eintrag\nBeispiel GmbH", privacy.TermsText);
        Assert.False(privacy.HasNameCandidates);
        Assert.StartsWith("Schutzliste: 2 Namen geschützt, 2 nicht", privacy.Info);
    }

    private static async Task Pump(int ms = 400)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static byte[] Contract()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Herr Müller unterschreibt am 12.03.2024."))),
                new W.Paragraph(new W.Run(new W.Text("Herr Schmidt unterschreibt am 01.04.2025.")))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    [AvaloniaFact]
    public async Task TheAiAndTheTmOnlySeePlaceholders()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var bodies = new ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                bodies.Enqueue(body);
                // eine kleine „KI“: übersetzt den (maskierten) Satz nach „Text:“ und behält die Platzhalter bei
                var prompt = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "";
                var text = prompt.Contains("Text:") ? prompt[(prompt.LastIndexOf("Text:", StringComparison.Ordinal) + 5)..].Trim() : prompt;
                var answer = text.Replace("Herr ", "Mr ").Replace(" unterschreibt am ", " signs on ");
                var bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = answer },
                    }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var before = AppServices.Ai;
        var beforePrivacy = PrivacyContext.Current;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var platform = new FakePlatform();
            platform.FilesToPick.Add(("vertrag.docx", Contract()));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Vertraulich " + Guid.NewGuid().ToString("N")[..4];
            vm.NewTargets = "en-GB";
            await Run(vm.CreateProjectCommand);

            await Run(vm.Privacy.ShowDetectedCommand);                       // noch keine Dateien
            await Run(vm.AddFilesCommand);
            Assert.Contains("Müller", vm.Privacy.Suggestion);                  // Namen in der Datei → „Vertraulich“ wird vorgeschlagen
            vm.SelectedTab = 0;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<PrivacyPanel>().Single().BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "37-datenschutz-vorschlag.png"));
            await Run(vm.Privacy.AcceptSuggestionCommand);
            Assert.Empty(vm.Privacy.Suggestion);
            vm.Privacy.AlsoLocalAi = true;                                   // der Prüfserver läuft auf diesem Rechner
            Assert.Same(vm.Project!.Privacy, PrivacyContext.Current);
            Assert.True(vm.Project.Privacy.Enabled);
            Assert.True(vm.Project.Privacy.Asked);
            await Run(vm.Privacy.ShowDetectedCommand);
            Assert.Contains(vm.Privacy.Detected, d => d.Value == "Müller" && d.Kind == SensitiveKind.Name);
            Assert.Contains(vm.Privacy.Detected, d => d.Value == "12.03.2024");
            vm.SelectedTab = 0;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<PrivacyPanel>().Single().BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "38-datenschutz.png"));

            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            await Pump();
            var editor = vm.Editor;
            var first = editor.Segments.First(s => s.Source.Contains("Müller"));
            // 🔒 am Segment: welche Angaben geschützt werden und welchen Tag sie bekommen
            Assert.True(first.HasSensitive);
            Assert.Contains("Name „Müller“ → [NAME_", first.SensitiveInfo);
            Assert.Contains("Datum „12.03.2024“ → [DATE_", first.SensitiveInfo);
            editor.Selected = first;
            await Run(editor.AiTranslateCommand);
            Assert.Equal("Mr Müller signs on 12/03/2024.", first.Target);                  // echte Werte zurück, Datum englisch geschrieben
            Assert.NotEmpty(bodies);
            Assert.DoesNotContain(bodies, b => b.Contains("Müller") || b.Contains("12.03.2024"));   // die KI sah nur Platzhalter
            Assert.Contains("Datenschutz", editor.AiLog);

            await Run(editor.ConfirmAndNextCommand);
            var unit = Assert.Single(vm.Project.ProjectTm.All());
            Assert.Equal("Mr [NAME_1] signs on [DATE_1].", unit.Target);                    // im TM nur Platzhalter

            var second = editor.Segments.First(s => s.Source.Contains("Schmidt"));
            editor.Selected = second;
            await Pump(300);
            Assert.Contains(editor.Matches, m => m.Target == "Mr Schmidt signs on 01/04/2025.");   // Treffer mit eigenen Werten

            editor.Protect("Bauhaus GmbH");
            Assert.Contains("Bauhaus GmbH", vm.Privacy.TermsText);
            Assert.Contains("Bauhaus GmbH", vm.Project.Privacy.Terms);

            // doch nicht sensibel: in „Erkannte Angaben“ abhaken – wird nicht mehr ersetzt, das 🔒 nennt es nicht mehr
            await Run(vm.Privacy.ShowDetectedCommand);
            vm.Privacy.Detected.First(d => d.Value == "12.03.2024").IsSensitive = false;
            await Run(vm.Privacy.ApplyDetectedCommand);
            Assert.Contains("12.03.2024", vm.Project.Privacy.NotSensitive);
            Assert.Contains("12.03.2024", vm.Privacy.NotSensitiveText);
            Assert.DoesNotContain(vm.Privacy.Detected, d => d.Value == "12.03.2024");
            Assert.DoesNotContain("12.03.2024", first.SensitiveInfo);
            Assert.Contains("Müller", first.SensitiveInfo);
            editor.Close();
        }
        finally
        {
            AppServices.Ai = before;
            PrivacyContext.Current = beforePrivacy;
            server.Stop();
        }
    }
}
