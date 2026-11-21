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
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Projects;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Dokument erst lesen: Steckbrief, einmalige Rückfrage zu Anrede und Sprecher, Vorgabe in jeder Übersetzung.</summary>
public class BriefUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        for (var i = 0; i < ms / 20 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ReadsTheDocumentAsksOnceAndTranslatesWithTheBrief()
    {
        using var home = TestSetup.OwnDataFolder();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var prompts = new ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var prompt = (string?)JsonNode.Parse(body)?["messages"]?.AsArray().Last()?["content"] ?? "";
                prompts.Enqueue(prompt);
                // Lesen: Brief an eine Person, Geschlecht unklar; Übersetzen: ein fester Satz
                var answer = prompt.Contains("Describe it for the translator")
                    ? """{"topic": "invitation", "text_type": "letter", "audience": "customer", "tone": "polite", "address": "unsure", "speaker": "man", "author": "Jan Kowalski", "names": ["Kowalski"]}"""
                    : "Zapraszamy.";
                var bytes = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = answer } }),
                }.ToJsonString());
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var before = AppServices.Ai;
        AppServices.Ui.ReadDocumentFirst = true;
        try
        {
            var provider = new AiProviderConfig { Name = "Prüf-KI", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "test" };
            AppServices.Ai = new AiSettings { Providers = [provider], TranslateProviderId = provider.Id, ResearchProviderId = provider.Id, EvaluateProviderId = provider.Id };
            var platform = new FakePlatform();
            platform.FilesToPick.Add(("einladung.txt", "Guten Tag,\nwir laden Sie herzlich ein.\nIch freue mich auf Ihren Besuch.\n"u8.ToArray()));
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            AppServices.Platform = platform;
            await Run(vm.BeginNewProjectCommand);
            vm.NewName = "Einladung " + Guid.NewGuid().ToString("N")[..4];
            vm.NewTargets = "pl-PL";
            await Run(vm.CreateProjectCommand);
            await Run(vm.AddFilesCommand);
            await Run(vm.OpenInEditorCommand, vm.Files[0]);
            var editor = vm.Editor;
            // beim Öffnen liest die KI im Hintergrund und füllt den Steckbrief – ohne Rückfrage
            await Until(() => editor.HasBrief && editor.BriefTextType.Length > 0);
            Assert.Equal(("invitation", "letter", "customer", "polite", "Kowalski"), (editor.BriefTopic, editor.BriefTextType, editor.BriefAudience, editor.BriefTone, editor.BriefNames));
            Assert.False(editor.IsAskingBrief);
            Assert.Contains(prompts, p => p.Contains("Describe it for the translator") && p.Contains("in Deutsch"));   // Angaben in der Erklärsprache

            // „Leere per KI“: schon gelesen – jetzt einmal fragen (Anrede unklar)
            var translating = editor.AiTranslateAllCommand.ExecuteAsync(null);
            await Until(() => editor.IsAskingBrief);
            Assert.True(editor.IsAskingBrief);
            Assert.Contains("letter", editor.BriefQuestionText);
            Assert.Contains(editor.AddressOptions, o => o.StartsWith("Pani – "));
            Assert.Equal((int)SpeakerForm.Male, editor.BriefSpeakerIndex);                      // erkannt: der Verfasser ist ein Mann
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Vor der Übersetzung: Anrede und Sprecher");
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "30-steckbrief-rueckfrage.png"));
            editor.BriefAddressIndex = (int)AddressForm.FormalPlural;                            // „Państwo“
            await Run(editor.AnswerBriefCommand);
            await translating;
            Dispatcher.UIThread.RunJobs();

            // Jede Übersetzung bekam die Vorgabe; der Steckbrief ist gespeichert und gilt als bestätigt
            var translations = prompts.Where(p => p.Contains("Text:")).ToList();
            Assert.Equal(3, translations.Count);
            Assert.All(translations, p => Assert.Contains("several people or a general audience", p));
            Assert.All(translations, p => Assert.Contains("masculine first-person forms", p));
            var brief = vm.Project!.Brief(vm.Project.Files.Single(), "pl-PL");
            Assert.Equal((AddressForm.FormalPlural, SpeakerForm.Male, true), (brief!.Address, brief.Speaker, brief.Confirmed));
            Assert.True(editor.HasBrief);

            // Zweiter Lauf: kein neues Lesen, keine Rückfrage
            var count = prompts.Count;
            editor.Segments[0].Model.Target = "";
            await Run(editor.AiTranslateAllCommand);
            Assert.False(editor.IsAskingBrief);
            Assert.Equal(count + 1, prompts.Count);

            // QA: „Pani“ passt nicht zur gewählten Anrede „Państwo“
            editor.Segments[1].Model.Target = "Zapraszamy Panią.";
            await Run(editor.RunQaCommand);
            Assert.Contains(editor.QaIssues, i => i.Issue.Check == "Anrede" && i.Issue.SegmentId == editor.Segments[1].Id);
        }
        finally
        {
            AppServices.Ai = before;
            AppServices.Ui.ReadDocumentFirst = false;
        }
    }
}
