using System.Net;
using System.Net.Sockets;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Builder;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Sdk;
using SententiamConverte.Sdk;
using SententiamConverte.Server;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Die neuen Funktionen der Programmierschnittstelle (Lektorat, Fehlerbewertung, Zahlen, Angebot, Vorlagen, TM-Pflege,
/// Fachbegriffe, alte Übersetzungen, Datenschutz, Arbeitsschritte, Eingangsordner) – intern und über den .NET-Client
/// gegen einen echten Teamserver.
/// </summary>
[Collection("BuiltInAi")]
public sealed class SdkNewFunctionsTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    public SdkNewFunctionsTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _home.File("home"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _home.Dispose();
    }

    private static byte[] Text(params string[] paragraphs) => Encoding.UTF8.GetBytes(string.Join("\n\n", paragraphs) + "\n");

    /// <summary>Ein Prüfer ändert in der Prüfdatei die Übersetzung von Segment <paramref name="id"/>.</summary>
    private static byte[] Corrected(byte[] review, int id, string text)
    {
        var ms = new MemoryStream();
        ms.Write(review);
        using (var word = WordprocessingDocument.Open(ms, true))
        {
            var row = word.MainDocumentPart!.Document.Body!.Elements<W.Table>().First().Elements<W.TableRow>()
                .First(r => r.Elements<W.TableCell>().First().InnerText == id.ToString());
            var p = row.Elements<W.TableCell>().ElementAt(2).Elements<W.Paragraph>().First();
            p.RemoveAllChildren<W.Run>();
            p.Append(new W.Run(new W.Text(text)));
        }
        return ms.ToArray();
    }

    private async Task Scenario(ISententiam sc, string name, string inbox, bool admin)
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("2.1", (await sc.InfoAsync(ct)).Version);
        await sc.CreateProjectAsync(new ProjectCreateDto(name, "de-DE", ["en-GB"], "Mietrecht"), ct);
        var file = await sc.AddFileAsync(name, new FileContent("vertrag.txt", Text(
            "Die Miete beträgt 1.234,50 Euro.", "Herr Müller unterschreibt den Mietvertrag.", "Der Mietvertrag endet.", "Die Kaution des Mietvertrags.")), ct);
        var segs = await sc.SegmentsAsync(name, file.Id, "en-GB", ct);

        // Zahlen in die Schreibweise der Zielsprache
        await sc.UpdateSegmentAsync(name, file.Id, "en-GB", segs[0].Id, new SegmentUpdateDto("The rent is 1.234,50 euros."), ct);
        Assert.Equal(1, await sc.LocalizeNumbersAsync(name, ct));
        Assert.Equal("The rent is 1,234.50 euros.", (await sc.GetSegmentAsync(name, file.Id, "en-GB", segs[0].Id, ct)).Target);

        // Lektorat: Prüfdatei hin und zurück, Änderung ablehnen
        await sc.UpdateSegmentAsync(name, file.Id, "en-GB", segs[2].Id, new SegmentUpdateDto("The tenancy agreement ends."), ct);
        await sc.ConfirmSegmentAsync(name, file.Id, "en-GB", segs[2].Id, ct: ct);
        var review = await sc.ReviewFileAsync(name, file.Id, "en-GB", ct);
        Assert.EndsWith("_Pruefung.docx", review.Name);
        var imported = await sc.ImportReviewAsync(name, file.Id, "en-GB",
            new ReviewImportDto(new FileContent(review.Name, Corrected(review.Content, segs[2].Id, "The lease ends."))), ct);
        Assert.Equal(1, imported.Changed);
        var changed = await sc.GetSegmentAsync(name, file.Id, "en-GB", segs[2].Id, ct);
        Assert.Equal(("The lease ends.", "The tenancy agreement ends."), (changed.Target, changed.ReviewBaseline));
        Assert.Equal("The tenancy agreement ends.", (await sc.RejectChangeAsync(name, file.Id, "en-GB", segs[2].Id, ct)).Target);

        // Fehlerbewertung
        var withError = await sc.AddErrorAsync(name, file.Id, "en-GB", segs[0].Id, new LqaErrorDto("Sprache: Zeichensetzung", LqaSeverity.Minor, "Komma"), ct);
        Assert.Single(withError.Errors!);
        Assert.Equal(1, (await sc.EvaluationAsync(name, ct: ct)).Errors);
        Assert.Equal(1, (await sc.EvaluationAsync(name, file.Id, "en-GB", ct)).Points);
        Assert.NotEmpty((await sc.EvaluationReportAsync(name, file.Id, "en-GB", ct)).Content);
        Assert.Null((await sc.RemoveErrorAsync(name, file.Id, "en-GB", segs[0].Id, 0, ct)).Errors);

        // Pseudoübersetzung, Angebot
        Assert.Contains("[", Encoding.UTF8.GetString((await sc.PseudoTranslationAsync(name, file.Id, "en-GB", ct)).Content));
        var quote = await sc.QuoteAsync(name, new RateSettings { Price = 0.10m, VatPercent = 19 }, ct);
        Assert.True(quote.Net > 0 && quote.Total > quote.Net);
        Assert.Equal(quote.Net, (await sc.QuoteAsync(name, ct: ct)).Net);                      // Preise im Projekt gespeichert
        Assert.EndsWith("_Angebot.docx", (await sc.QuoteDocumentAsync(name, ct)).Name);

        // Vorlagen und QA-Profile
        await sc.SetQaSettingsAsync(name, new QaSettings { MaxChars = 77 }, ct);
        await sc.SaveTemplateAsync(name, name + " Vorlage", ct);
        Assert.Contains(await sc.TemplatesAsync(ct), t => t.Name == name + " Vorlage");
        var second = await sc.CreateProjectAsync(new ProjectCreateDto(name + "2", "de-DE", ["en-GB"], Template: name + " Vorlage"), ct);
        Assert.Equal("Mietrecht", second.Topic);
        await sc.SaveQaProfileAsync(name, name + " QA", ct);
        Assert.Equal(77, (await sc.ApplyQaProfileAsync(name + "2", name + " QA", ct)).MaxChars);

        // TM-Pflege
        // (die Prüfdatei hat „The lease ends.“ bestätigt – im selben Zusammenhang ersetzt das die frühere Fassung im TM)
        var preview = await sc.ReplaceInTmAsync(name, new TmReplaceDto("lease", "tenancy", Preview: true), ct);
        Assert.Equal(1, preview.Count);
        Assert.Contains(await sc.TmUnitsAsync(name, ct: ct), u => u.Target == "The lease ends.");                // Vorschau ändert nichts
        Assert.Equal(1, (await sc.ReplaceInTmAsync(name, new TmReplaceDto("lease", "tenancy"), ct)).Count);
        Assert.Contains(await sc.TmUnitsAsync(name, ct: ct), u => u.Target == "The tenancy ends.");
        Assert.Equal(0, (await sc.CleanTmAsync(name, new TmCleanDto(), ct)).Duplicates);
        Assert.Empty(await sc.InconsistentTmAsync(name, ct));

        // Fachbegriffe aus den Dateien (ohne KI: Kandidaten nach Häufigkeit)
        Assert.Contains(await sc.ExtractTermsAsync(name, new ExtractTermsDto("en-GB", UseAi: false), ct), t => t.Source == "Mietvertrag");

        // Alte Übersetzungen
        var old = await sc.ImportTranslationsAsync(name, new ImportTranslationsDto(
            [new FileContent("brief_DE.txt", Text("Sehr geehrte Damen und Herren.", "Mit freundlichen Grüßen.")),
             new FileContent("brief_EN.txt", Text("Dear Sir or Madam.", "Yours faithfully."))], Shared: false), ct);
        var pair = Assert.Single(old.Pairs);
        Assert.Equal((2, "Projekt-TM"), (pair.Imported, pair.Tm));

        // Datenschutz
        await sc.SetPrivacyAsync(name, new Privacy.PrivacySettings { Enabled = true }, ct);
        Assert.Contains(await sc.DetectSensitiveAsync(name, ct), d => d.Value == "Müller");

        // Arbeitsschritte: ohne Zuweisung frei; Verwaltung weist zu
        var step = Assert.Single(await sc.WorkflowAsync(name, ct));
        Assert.Equal(WorkflowStep.Translation, step.Step.Step);
        if (admin)
        {
            Assert.Equal("Bartek", (await sc.SetWorkflowAsync(name, file.Id, "en-GB", new WorkflowUpdate(Translator: "Bartek"), ct)).Translator);
            await sc.SetWorkflowAsync(name, file.Id, "en-GB", new WorkflowUpdate(Translator: ""), ct);
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sc.SetWorkflowAsync(name, file.Id, "en-GB", new WorkflowUpdate(Translator: "Bartek"), ct));

        // Eingangsordner (nur Verwaltung)
        Directory.CreateDirectory(inbox);
        var newFile = Path.Combine(inbox, "neu.txt");
        await File.WriteAllTextAsync(newFile, "Die Kaution des Mietvertrags.", ct);
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddMinutes(-1));
        if (admin)
        {
            var processed = Assert.Single(await sc.ProcessFolderAsync(name, new WatchRunDto(inbox), ct));
            Assert.Equal("neu.txt", processed.File);
            Assert.Contains(await sc.FilesAsync(name, ct), f => f.Name == "neu.txt");
        }
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sc.ProcessFolderAsync(name, new WatchRunDto(inbox), ct));
    }

    [Fact]
    public async Task InternalImplementation()
    {
        var service = new SententiamService(new SdkOptions { ProjectsRoot = _home.File("projekte"), Ai = () => new AiSettings() }).ForUser("Anna", admin: true);
        await Scenario(service, "NeuIntern", _home.File("eingang-intern"), admin: true);
    }

    [Fact]
    public async Task ClientAgainstTeamServerWithWorkflowChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = _home.File("server");
        var users = new UserStore(data);
        var admin = users.Add("admin", Role.Admin);
        var anna = users.Add("Anna", Role.Editor);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        l.Stop();
        await using WebApplication app = ServerApp.Build(["--urls", url], data);
        await app.StartAsync(ct);

        using var adminClient = new SententiamClient(url, admin);
        using var annaClient = new SententiamClient(url, anna);
        await Scenario(adminClient, "NeuAdmin", _home.File("eingang-admin"), admin: true);
        await Scenario(annaClient, "NeuAnna", _home.File("eingang-anna"), admin: false);

        // Arbeitsschritte gelten auch für die REST-API: Anna ist nicht an der Reihe
        var file = (await annaClient.FilesAsync("NeuAnna", ct))[0];
        await adminClient.SetWorkflowAsync("NeuAnna", file.Id, "en-GB", new WorkflowUpdate(Translator: "Bartek"), ct);
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            annaClient.UpdateSegmentAsync("NeuAnna", file.Id, "en-GB", 1, new SegmentUpdateDto("x"), ct));
        Assert.Contains("Bartek", denied.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => annaClient.ConfirmSegmentAsync("NeuAnna", file.Id, "en-GB", 1, ct: ct));
        Assert.Empty((await annaClient.PretranslateAsync("NeuAnna", ct: ct)).Where(r => r.File == file.Name));   // übersprungen
        Assert.NotNull(await adminClient.UpdateSegmentAsync("NeuAnna", file.Id, "en-GB", 1, new SegmentUpdateDto("ok"), ct));   // Verwaltung darf
        await adminClient.SetWorkflowAsync("NeuAnna", file.Id, "en-GB", new WorkflowUpdate(Translator: "Anna"), ct);
        Assert.NotNull(await annaClient.UpdateSegmentAsync("NeuAnna", file.Id, "en-GB", 1, new SegmentUpdateDto("mine"), ct));
    }
}
