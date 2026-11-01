using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Controls;
using SententiamConverte.Core.Ai;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Editor wie eine Textverarbeitung: Rechtschreibung beim Tippen, Formatierung, Rückgängig – und „KI einrichten“.</summary>
public class EditorUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Pump(int ms = 400)
    {
        for (var i = 0; i < ms / 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static SegmentTextBox TargetBox(MainWindow window, SegmentViewModel seg) =>
        window.GetVisualDescendants().OfType<SegmentTextBox>().First(b => b.SourceText != null && ReferenceEquals(b.DataContext, seg));

    [AvaloniaFact]
    public async Task TypingShowsSpellingErrorsAndFormattingLikeAWordProcessor()
    {
        // Kleines Hunspell-Wörterbuch für en-US (die anderen Tests nutzen en-GB ohne Wörterbuch)
        var folder = AppServices.Spell.Folder;
        File.WriteAllText(Path.Combine(folder, "en-US.aff"), "SET UTF-8\nTRY esianrtolcdugmphbyfvkwz\n");
        File.WriteAllText(Path.Combine(folder, "en-US.dic"), "12\nOperating\ninstructions\nTighten\nthe\nscrew\nto\nThen\nswitch\non\ndevice\nNm\nSwitch\n");

        var platform = new FakePlatform();
        platform.FilesToPick.Add(("anleitung.docx", UiFlowTests.SampleDocx()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;

        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Rechtschreibung";
        vm.NewTargets = "en-US";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;
        Assert.False(editor.DictionaryMissing);
        await Pump();

        // Tippen: Fehler wird sofort rot unterstrichen, Vorschläge kommen aus dem Wörterbuch
        editor.Selected = editor.Segments[0];
        await Pump(100);
        var box = TargetBox(window, editor.Segments[0]);
        box.TextArea.Focus();
        window.KeyTextInput("Operating instrucsions");
        await Pump();
        Assert.Equal("Operating instrucsions", editor.Segments[0].Target);
        var error = Assert.Single(box.SpellingErrors);
        Assert.Equal(("instrucsions", 10), (error.Word, error.Index));
        Assert.Contains("instructions", AppServices.Spell.Suggest("instrucsions", "en-US"));

        // Korrigieren: Unterstreichung verschwindet; Rückgängig und Wiederholen wie gewohnt
        box.Document.Replace(10, 12, "instructions");
        await Pump();
        Assert.Empty(box.SpellingErrors);
        box.Undo();
        Assert.Equal("Operating instrucsions", editor.Segments[0].Target);
        box.Redo();
        Assert.Equal("Operating instructions", editor.Segments[0].Target);

        // Strg+B setzt die fette Formatierung der Quelle (⟨1⟩…⟨/1⟩) um das, was man danach tippt
        editor.Selected = editor.Segments[1];
        await Pump(100);
        var box2 = TargetBox(window, editor.Segments[1]);
        box2.TextArea.Focus();
        window.KeyTextInput("Tighten the screw to ");
        window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
        window.KeyTextInput("5 Nm");
        await Pump(100);
        Assert.Equal("Tighten the screw to ⟨1⟩5 Nm⟨/1⟩", editor.Segments[1].Target);
        Assert.Null(editor.Segments[1].MissingTags);

        // Für das Bildschirmfoto ein Fehler im dritten Segment
        editor.Selected = editor.Segments[2];
        await Pump(100);
        var box3 = TargetBox(window, editor.Segments[2]);
        box3.TextArea.Focus();
        window.KeyTextInput("Then switch on the devise.");
        await Pump();
        Assert.Contains(box3.SpellingErrors, e => e.Word == "devise");
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "25-editor-rechtschreibung.png"));

        // Dunkles Design (folgt sonst dem System)
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        await Pump(100);
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "27-editor-dunkel.png"));
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        editor.Close();
    }

    [AvaloniaFact]
    public async Task AiSetupUsesALocalServerForEverything()
    {
        // Ein lokaler „KI-Server“ mit OpenAI-Schnittstelle
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await server.GetContextAsync(); } catch (Exception) { return; }
                var json = ctx.Request.Url!.AbsolutePath.EndsWith("/models")
                    ? """{"data":[{"id":"embed-small"},{"id":"qwen3-14b"}]}"""
                    : """{"choices":[{"message":{"role":"assistant","content":"Vor der Reinigung das Gerät ausschalten."}}]}""";
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var before = AppServices.Ai;
        try
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            vm.SelectedTab = 5;
            Dispatcher.UIThread.RunJobs();

            var setup = vm.Settings.Setup;
            var models = await AiDiscovery.ModelsAsync($"http://127.0.0.1:{port}/v1", null);
            Assert.Equal(["qwen3-14b"], models);                          // Einbettungsmodelle werden ausgeblendet
            setup.LocalServers.Add(new LocalAiServer("Testserver", $"http://127.0.0.1:{port}/v1", models));
            setup.SelectedLocalServer = setup.LocalServers[0];
            Assert.Equal("qwen3-14b", setup.SelectedLocalModel);

            await Run(setup.UseLocalCommand);
            Assert.StartsWith("Eingerichtet und getestet", setup.Result);
            Assert.Contains("Vor der Reinigung das Gerät ausschalten.", setup.Result);
            var provider = AppServices.Ai.Get(AppServices.Ai.TranslateProviderId)!;
            Assert.Equal($"http://127.0.0.1:{port}/v1", provider.BaseUrl);
            Assert.Equal(provider.Id, AppServices.Ai.ResearchProviderId);
            Assert.Equal(provider.Id, AppServices.Ai.EvaluateProviderId);
            Assert.True(provider.IsLocal);
            window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(t => t.Text == "KI einrichten")
                .FindAncestorOfType<Avalonia.Controls.Border>()!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "26-ki-einrichten.png"));
        }
        finally
        {
            before.Save();
            AppServices.Ai = before;
            server.Stop();
        }
    }
}
