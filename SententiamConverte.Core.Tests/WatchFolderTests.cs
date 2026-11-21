using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Tests;

/// <summary>Überwachte Ordner: neue Dateien automatisch ins Projekt, vorübersetzen, Zieldateien ausgeben.</summary>
[Collection("BuiltInAi")]
public sealed class WatchFolderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    public WatchFolderTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _dir.File("home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public async Task NewFilesArePretranslatedAndTheTargetsWrittenOut()
    {
        using var project = TranslationProject.Create(_dir.File("projekt"), "Eingang", "de-DE", ["en-GB", "pl-PL"]);
        project.ProjectTm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Switch off the device.");
        var inbox = Directory.CreateDirectory(_dir.File("eingang")).FullName;
        var file = Path.Combine(inbox, "hinweis.txt");
        await File.WriteAllTextAsync(file, "Gerät ausschalten.\n\nStecker ziehen.", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));
        await File.WriteAllTextAsync(Path.Combine(inbox, "notiz.xyz"), "nicht unterstützt", TestContext.Current.CancellationToken);

        var rule = new WatchRule { Folder = inbox, ProjectFolder = project.Folder };
        WatchRules.Save([rule]);
        Assert.Equal(inbox, Assert.Single(WatchRules.Load()).Folder);

        var pending = WatchProcessor.PendingFiles(rule);
        Assert.Equal(file, Assert.Single(pending));                           // .xyz wird nicht angefasst
        var result = await WatchProcessor.ProcessAsync(rule, file, project, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Segments);                                      // 2 Segmente × 2 Zielsprachen
        Assert.Equal(1, result.FromTm);
        Assert.Equal(2, result.Outputs.Count);
        var en = Path.Combine(inbox, "fertig", "en-GB", project.TargetFileName(project.Files[0], "en-GB"));
        Assert.Contains("Switch off the device.", await File.ReadAllTextAsync(en, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(inbox, "erledigt", "hinweis.txt")));
        Assert.False(File.Exists(file));
        Assert.Empty(WatchProcessor.PendingFiles(rule));
        Assert.Single(project.Files);
    }
}
