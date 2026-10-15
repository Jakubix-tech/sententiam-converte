using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;
using W = DocumentFormat.OpenXml.Wordprocessing;

[assembly: AvaloniaTestApplication(typeof(SententiamConverte.UiTests.TestAppBuilder))]

namespace SententiamConverte.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class TestSetup
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "sc-ui-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Einstellungen und Projekte in einen Temp-Ordner umleiten, bevor irgendetwas sie liest.</summary>
    [ModuleInitializer]
    public static void Init()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", Home);
        Environment.SetEnvironmentVariable("SENTENTIAM_LANG", "de");
    }

    public static string ScreenshotDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>Ersetzt die Dateidialoge: liefert vorbereitete Dateien und fängt gespeicherte Dateien auf.</summary>
public sealed class FakePlatform : IPlatformServices
{
    public List<(string Name, byte[] Content)> FilesToPick { get; } = new();
    public Dictionary<string, MemoryStream> Saved { get; } = new();

    public Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> patterns, bool multiple) =>
        Task.FromResult<IReadOnlyList<PickedFile>>(FilesToPick
            .Select(f => new PickedFile(f.Name, () => Task.FromResult<Stream>(new MemoryStream(f.Content)))).ToList());

    public Task<Stream?> SaveFileAsync(string title, string suggestedName)
    {
        var ms = new MemoryStream();
        Saved[suggestedName] = ms;
        return Task.FromResult<Stream?>(ms);
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public List<string> Opened { get; } = new();
    public Task<bool> OpenFileAsync(string path) { Opened.Add(path); return Task.FromResult(true); }
}

public class UiFlowTests
{
    internal static byte[] SampleDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Betriebsanleitung"))),
                new W.Paragraph(
                    new W.Run(new W.Text("Die Schraube mit ") { Space = SpaceProcessingModeValues.Preserve }),
                    new W.Run(new W.RunProperties(new W.Bold()), new W.Text("5 Nm")),
                    new W.Run(new W.Text(" anziehen. Danach das Gerät einschalten.") { Space = SpaceProcessingModeValues.Preserve })),
                new W.Paragraph(new W.Run(new W.Text("Danach das Gerät einschalten.")))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000, string? info = null)
    {
        var start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition(), "Bedingung nicht erfüllt. " + info);
    }

    private static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task FullTranslationFlow()
    {
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("anleitung.docx", SampleDocx()));

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;      // nach Show(), weil MainView sich beim Anzeigen selbst einträgt
        Shot(window, "01-start");

        // Projekt anlegen
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "UI-Test " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "de-DE";
        vm.NewTargets = "en-GB, pl-PL";
        vm.NewTopic = "Maschinenbau";
        Shot(window, "02-neues-projekt");
        await Run(vm.CreateProjectCommand);
        Assert.True(vm.HasProject, vm.Status);

        // Datei hinzufügen
        await Run(vm.AddFilesCommand);
        Assert.Equal(2, vm.Files.Count);
        Assert.True(vm.Files[0].Words > 0);
        Shot(window, "03-projekt");

        // Im Editor öffnen
        await Run(vm.OpenInEditorCommand, vm.Files.First(f => f.Language == "en-GB"));
        Assert.Equal(1, vm.SelectedTab);
        var editor = vm.Editor;
        Assert.Equal(4, editor.Segments.Count);
        Shot(window, "04-editor");

        // Erstes Segment übersetzen und bestätigen
        editor.Selected = editor.Segments[0];
        editor.Selected.Target = "Operating instructions";
        await Run(editor.ConfirmAndNextCommand);
        Assert.Equal("übersetzt", editor.Segments[0].StatusText);
        Assert.Same(editor.Segments[1], editor.Selected);

        // "Danach das Gerät einschalten." kommt zweimal vor → automatische Übernahme
        var repeated = editor.Segments.Where(s => s.Source == "Danach das Gerät einschalten.").ToList();
        Assert.Equal(2, repeated.Count);
        editor.Selected = repeated[0];
        repeated[0].Target = "Then switch on the device.";
        await Run(editor.ConfirmAndNextCommand);
        Assert.Equal("Then switch on the device.", repeated[1].Target);

        // Segment mit Tag
        var tagged = editor.Segments.First(s => s.Source.Contains('⟨'));
        editor.Selected = tagged;
        tagged.Target = "Tighten the screw to ⟨1⟩5 Nm⟨/1⟩.";
        await Run(editor.ConfirmAndNextCommand);

        // Teilen und wieder zusammenführen
        var first = editor.Segments[0];
        editor.Selected = first;
        var countBefore = editor.Segments.Count;
        editor.SplitAt(first.Source.IndexOf("anleitung", StringComparison.Ordinal));
        Assert.Equal(countBefore + 1, editor.Segments.Count);
        editor.Selected = editor.Segments.First(s => s.Model == first.Model);
        await Run(editor.MergeWithNextCommand);
        Assert.Equal(countBefore, editor.Segments.Count);
        Assert.Equal("Betriebsanleitung", editor.Segments[0].Source);
        editor.Segments[0].Target = "Operating instructions";
        editor.Selected = editor.Segments[0];
        await Run(editor.ConfirmAndNextCommand);

        // Kommentar
        editor.Selected = editor.Segments[0];
        editor.Comment = "Kundenwunsch: Titel so lassen";
        Assert.True(editor.Segments[0].HasComment);

        // QA mit eigener Regel
        vm.RegexRules.Add(new RegexRuleViewModel { Name = "Kein 'device'", TargetPattern = "device", IsError = true });
        await Run(vm.SaveQaSettingsCommand);
        await Run(editor.RunQaCommand);
        Assert.Contains(editor.QaIssues, q => q.Message == "Kein 'device'");
        Shot(window, "05-editor-qa");

        // Vorschau
        await Run(editor.PreviewCommand);
        Assert.Single(platform.Opened);
        Assert.True(File.Exists(platform.Opened[0]));

        // Terminologie
        vm.SelectedTab = 2;
        vm.Resources.NewSourceTerm = "Schraube";
        vm.Resources.NewTargetTerm = "screw";
        vm.Resources.NewTargetLanguage = "en-GB";
        await Run(vm.Resources.AddConceptCommand);
        Assert.Single(vm.Resources.Concepts);
        Shot(window, "06-ressourcen");

        // AutoSuggest: Terminologie und TM-Wörter werden beim Tippen angeboten
        vm.SelectedTab = 1;
        editor.Selected = editor.Segments.First(s => s.Source.Contains('⟨'));
        await WaitUntil(() => editor.SuggestionsFor("scr").Count > 0, info: vm.Status + " | Termini: " + editor.Terms.Count + " | Treffer: " + editor.Matches.Count);
        Assert.Contains("screw", editor.SuggestionsFor("scr"));
        Shot(window, "06b-editor-tm");

        // Fachquellen-Profil und Kontext-Option werden im Projekt gespeichert
        vm.ProjectProfile = "Recht";
        vm.ConfirmContextMatches = false;
        Assert.Equal("Recht", vm.Project!.SourceProfile);
        Assert.Equal("Recht", vm.CurrentProfile?.Name);
        Assert.Equal("Recht", vm.Agents.ProfileName);
        vm.ConfirmContextMatches = true;

        // Analyse & Zieldatei
        vm.SelectedTab = 0;
        await Run(vm.AnalyzeCommand);
        Assert.NotEmpty(vm.AnalysisRows);
        Shot(window, "07-analyse");
        await Run(vm.ExportTargetCommand, vm.Files.First(f => f.Language == "en-GB"));
        var saved = platform.Saved.Single(kv => kv.Key.EndsWith(".docx"));
        using (var word = WordprocessingDocument.Open(new MemoryStream(saved.Value.ToArray()), false))
        {
            var text = word.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Operating instructions", text);
            Assert.Contains("Tighten the screw to 5 Nm.", text);
            Assert.Contains("Then switch on the device.", text);
        }

        // Übrige Bereiche einmal anzeigen
        vm.SelectedTab = 3;
        Shot(window, "08-wissen");
        vm.SelectedTab = 4;
        Shot(window, "08b-agenten");
        vm.SelectedTab = 5;
        Shot(window, "09-einstellungen");
        Assert.DoesNotContain("Fehler", vm.Status);
    }

    [AvaloniaFact]
    public void NarrowLayoutForPhones()
    {
        var vm = new MainViewModel();
        var window = new Window { Content = new MainView { DataContext = vm }, Width = 400, Height = 800 };
        window.Show();
        vm.SelectedTab = 1;
        Shot(window, "10-telefon-editor");
        vm.SelectedTab = 4;
        Shot(window, "11-telefon-agenten");
    }
}
