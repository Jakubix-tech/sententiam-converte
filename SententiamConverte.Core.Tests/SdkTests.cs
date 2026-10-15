using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Sdk;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Sdk;
using SententiamConverte.Server;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Die Programmierschnittstelle ISententiam: derselbe Ablauf intern (wie in einer Erweiterung) und über den .NET-Client
/// gegen einen echten Teamserver – beide müssen sich gleich verhalten.
/// </summary>
public sealed class SdkTests
{
    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Ein typischer Arbeitsablauf, geschrieben nur gegen die Schnittstelle.</summary>
    private static async Task Scenario(ISententiam sc, string projectName)
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("2.0", (await sc.InfoAsync(ct)).Version);
        Assert.Contains(await sc.FormatsAsync(ct), f => f.Id == "idml");

        var project = await sc.CreateProjectAsync(new ProjectCreateDto(projectName, "de-DE", ["en-GB"], "Technik"), ct);
        Assert.Equal(projectName, project.Name);
        Assert.Contains(projectName, await sc.ProjectsAsync(ct));
        await Assert.ThrowsAsync<ArgumentException>(() => sc.CreateProjectAsync(new ProjectCreateDto(projectName, "de-DE", ["en-GB"]), ct));

        var file = await sc.AddFileAsync(projectName, new FileContent("anleitung.txt",
            Text("Das Gerät ausschalten.\n\nDen Stecker ziehen.\n\nDas Gerät ausschalten.\n")), ct);
        var segments = await sc.SegmentsAsync(projectName, file.Id, "en-GB", ct);
        Assert.Equal(3, segments.Count);

        // Übersetzen und bestätigen
        var updated = await sc.UpdateSegmentAsync(projectName, file.Id, "en-GB", segments[0].Id, new SegmentUpdateDto("Switch off the device."), ct);
        Assert.Equal(SegmentStatus.Draft, updated.Status);
        var confirmed = await sc.ConfirmSegmentAsync(projectName, file.Id, "en-GB", segments[0].Id, ct: ct);
        Assert.Equal(1, confirmed.Propagated);
        Assert.Equal("Switch off the device.", (await sc.GetSegmentAsync(projectName, "anleitung.txt", "en-GB", segments[2].Id, ct)).Target);

        // TM, Treffer, Terminologie
        var hits = await sc.SearchTmAsync(projectName, "Das Gerät ausschalten.", "de-DE", "en-GB", ct: ct);
        Assert.True(hits[0].Score >= 100);
        Assert.Equal("Switch off the device.", (await sc.MatchesAsync(projectName, file.Id, "en-GB", segments[2].Id, ct: ct))[0].Unit.Target);
        await sc.AddConceptAsync(projectName, new ConceptDto([new TermDto("de-DE", "Stecker", TermStatus.Preferred), new TermDto("en-GB", "plug", TermStatus.Preferred)]), ct);
        Assert.Contains(await sc.SegmentTermsAsync(projectName, file.Id, "en-GB", segments[1].Id, ct),
            t => t.Translations.Any(x => x.Text == "plug"));
        Assert.Single(await sc.SearchTermsAsync(projectName, "Stecker", "de-DE", ct));

        // Wissen: Autor ist, wer angemeldet ist
        await sc.AddKnowledgeAsync(projectName, new Knowledge.KnowledgeEntry
        {
            Language = "en-GB", Topic = "Technik", Kind = Knowledge.KnowledgeKind.Style, Title = "Imperative", Content = "Instructions use the imperative.",
        }, ct);
        Assert.Equal("Anna", (await sc.SearchKnowledgeAsync(projectName, "imperative", ct: ct))[0].Author);

        // Stapel, Analyse, Ergebnis
        await sc.UpdateSegmentAsync(projectName, file.Id, "en-GB", segments[1].Id, new SegmentUpdateDto("Pull out the plug.", SegmentStatus.Translated), ct);
        Assert.Single(await sc.PretranslateAsync(projectName, ct: ct));
        Assert.NotEmpty(await sc.AnalysisAsync(projectName, ct));
        var target = await sc.TargetFileAsync(projectName, file.Id, "en-GB", ct);
        Assert.Equal("anleitung_en-GB.txt", target.Name);
        Assert.Contains("Pull out the plug.", Encoding.UTF8.GetString(target.Content));
        Assert.Contains("Switch off the device.", Encoding.UTF8.GetString((await sc.ExportTmAsync(projectName, ct)).Content));

        // Fehler sind in beiden Fällen dieselben Ausnahmen
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sc.GetProjectAsync("gibt-es-nicht", ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sc.GetSegmentAsync(projectName, file.Id, "en-GB", 999, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => sc.MergeSegmentAsync(projectName, file.Id, "en-GB", segments[2].Id, ct));
    }

    [Fact]
    public async Task InternalImplementation()
    {
        using var dir = new TempDir();
        var service = new SententiamService(new SdkOptions { ProjectsRoot = dir.Path, Ai = () => new AiSettings() }, "Anna");
        await Scenario(service, "Intern");
    }

    [Fact]
    public async Task ClientAgainstTeamServer()
    {
        using var dir = new TempDir();
        var users = new UserStore(dir.Path);
        var anna = users.Add("Anna", Role.Editor);
        var gast = users.Add("Gast", Role.Reader);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        l.Stop();
        await using WebApplication app = ServerApp.Build(["--urls", url], dir.Path);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = new SententiamClient(url, anna);
        await Scenario(client, "Extern");

        // Rechte und Anmeldung
        using var reader = new SententiamClient(url, gast);
        Assert.NotEmpty(await reader.SegmentsAsync("Extern", "anleitung.txt", "en-GB", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.UpdateSegmentAsync("Extern", "anleitung.txt", "en-GB", 1, new SegmentUpdateDto("x"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.RemoveFileAsync("Extern", "anleitung.txt", TestContext.Current.CancellationToken));
        using var wrong = new SententiamClient(url, "falscher-schluessel");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.ProjectsAsync(TestContext.Current.CancellationToken));
    }
}
