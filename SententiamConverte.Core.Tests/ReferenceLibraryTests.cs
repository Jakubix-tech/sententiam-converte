using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Resources;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Eigene Fachtexte: Ordner mit PDFs und Dokumenten einlesen (mit Seite, Sprache und Stand), darin Wortfolgen finden
/// und zählen, nur Geändertes neu lesen – und die Recherche bekommt daraus Belege und Häufigkeiten.
/// </summary>
public sealed class ReferenceLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        ReferenceLibrary.CloseShared();
        _dir.Dispose();
    }

    private static byte[] Pdf(params string[][] pages)
    {
        using var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = b.AddPage(PageSize.A4);
            var y = 780;
            foreach (var line in lines)
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 40;                                                       // eigener Absatz je Zeile
            }
        }
        return b.Build();
    }

    private string Folder()
    {
        var folder = _dir.File("texte");
        Directory.CreateDirectory(Path.Combine(folder, "unter"));
        File.WriteAllBytes(Path.Combine(folder, "Pumpenhandbuch.pdf"), Pdf(
            ["Operating manual for centrifugal pumps. Edition 2019-03", "This manual describes installation and operation of the pump."],
            ["Before replacing the mechanical seal, switch off the pump and close both valves.", "Mechanical seals must be checked every 500 operating hours."]));
        File.WriteAllText(Path.Combine(folder, "unter", "Wartung.txt"),
            "Stand: 01.03.2021\n\nVor dem Austausch der Gleitringdichtung ist die Pumpe auszuschalten. Die Gleitringdichtungen werden alle 500 Stunden geprüft.\n\nDer Kühlkreislauf wird jährlich gespült und das Kühlmittel erneuert, damit die Anlage zuverlässig läuft.");
        File.WriteAllBytes(Path.Combine(folder, "Scan.pdf"), Pdf(Array.Empty<string>()));   // eine Seite ohne Text
        File.WriteAllText(Path.Combine(folder, "Notizen.xyz"), "wird nicht gelesen");
        return folder;
    }

    [Fact]
    public async Task ReadsFoldersFindsAndCountsPhrasesWithPageAndVersion()
    {
        var folder = Folder();
        using var library = new ReferenceLibrary(_dir.File("index.scidx"));
        var scan = await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);
        Assert.Equal((3, 0, 0, 0), (scan.Added, scan.Updated, scan.Removed, scan.Unchanged));
        Assert.Single(scan.Problems);                                          // Scan.pdf: kein Text

        var files = library.Files().ToDictionary(f => Path.GetFileName(f.Path));
        Assert.Equal(("en", 2, "Edition 2019-03"), (files["Pumpenhandbuch.pdf"].Language, files["Pumpenhandbuch.pdf"].Pages, files["Pumpenhandbuch.pdf"].Version));
        Assert.Equal(new DateTime(2019, 3, 1), files["Pumpenhandbuch.pdf"].Date);
        Assert.Equal(("de", "Stand: 01.03.2021"), (files["Wartung.txt"].Language, files["Wartung.txt"].Version));
        Assert.NotNull(files["Scan.pdf"].Problem);

        var hit = Assert.Single(library.FindPhrase("mechanical seal", "en-GB", 1));
        Assert.Equal(2, hit.Page);
        Assert.Equal("Before replacing the mechanical seal, switch off the pump and close both valves.", hit.Quote);
        Assert.EndsWith("Pumpenhandbuch.pdf#page=2", hit.Uri);
        Assert.Equal("Edition 2019-03", hit.Version);

        Assert.Equal(2, library.CountPhrase("mechanical seal", "en"));        // „seals“ zählt mit (Endung)
        Assert.Equal(2, library.CountPhrase("Gleitringdichtung", "de-DE"));   // Ein- und Mehrzahl im selben Abschnitt
        Assert.Equal(0, library.CountPhrase("Gleitringdichtung", "en"));
        Assert.Equal(0, library.CountPhrase("sliding ring seal", "en"));
        Assert.Empty(library.FindPhrase("Kreiselpumpe", null));
    }

    [Fact]
    public async Task RescanReadsOnlyChangesAndKeepsFilesOfMissingDrives()
    {
        var folder = Folder();
        using var library = new ReferenceLibrary(_dir.File("index.scidx"));
        await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);

        var again = await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 0, 0, 3), (again.Added, again.Updated, again.Removed, again.Unchanged));

        var txt = Path.Combine(folder, "unter", "Wartung.txt");
        File.WriteAllText(txt, "Die Wellendichtung wird geprüft, bevor die Anlage wieder anläuft und der Betrieb beginnt.");
        File.SetLastWriteTimeUtc(txt, DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(folder, "Scan.pdf"));
        var changed = await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);
        Assert.Equal((0, 1, 1, 1), (changed.Added, changed.Updated, changed.Removed, changed.Unchanged));
        Assert.Equal(0, library.CountPhrase("Gleitringdichtung", "de"));
        Assert.Equal(1, library.CountPhrase("Wellendichtung", "de"));

        // Ordner gerade nicht erreichbar (Laufwerk abgezogen): seine Dateien bleiben im Index
        Directory.Move(folder, folder + "-weg");
        var missing = await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, missing.Removed);
        Assert.Equal(2, library.Files().Count);

        // Ordner aus der Liste genommen: seine Dateien verschwinden
        var dropped = await library.ScanAsync([], ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, dropped.Removed);
        Assert.Empty(library.Files());
    }

    [Fact]
    public void StatedVersionsAndSentences()
    {
        Assert.Equal("Stand: 01.03.2021", ReferenceLibrary.StatedDate("Merkblatt. Stand: 01.03.2021. Inhalt …")?.Version);
        Assert.Equal(new DateTime(2011, 3, 1), ReferenceLibrary.StatedDate("Nach DIN EN ISO 12100:2011-03 ist die Risikobeurteilung …")?.Date);
        Assert.Equal("Wydanie 2018", ReferenceLibrary.StatedDate("Instrukcja obsługi. Wydanie 2018")?.Version);
        Assert.Null(ReferenceLibrary.StatedDate("Die Pumpe fördert 2019 Liter."));
        Assert.Equal("Die Pumpe läuft.", ReferenceLibrary.Sentence("Erster Satz. Die Pumpe läuft. Dritter Satz.", "Pumpe"));
        Assert.Null(ReferenceLibrary.Sentence("Die Pumpen laufen.", "Pumpe läuft"));
    }

    /// <summary>Die Recherche belegt mit den eigenen Fachtexten und sieht, wie oft jeder Kandidat dort vorkommt.</summary>
    [Fact]
    public async Task ResearchUsesOwnTextsAsEvidenceAndFrequency()
    {
        var folder = Folder();
        using var library = new ReferenceLibrary(_dir.File("index.scidx"));
        await library.ScanAsync([folder], ct: TestContext.Current.CancellationToken);
        var model = new ScriptModel();
        var ctx = new AgentContext { Model = model, Search = new NoSearch(), Library = library };
        var r = await new TermComparisonAgent(ctx).RunAsync(new TermQuery("Gleitringdichtung", "de-DE", "en-GB",
            "Schalten Sie die Pumpe aus, bevor Sie die Gleitringdichtung austauschen."), 2, TestContext.Current.CancellationToken);

        Assert.Contains(r.Evidence, e => e is { Local: true, Language: "de-DE" } && e.Quote.StartsWith("Vor dem Austausch der Gleitringdichtung"));
        var own = Assert.Single(r.Evidence, e => e is { Local: true, Language: "en-GB" });
        Assert.Equal(("mechanical seal", "Pumpenhandbuch, S. 2", "Edition 2019-03"), (own.Term, own.Title, own.Version));
        var comparison = model.Prompts[^1];
        Assert.Contains("own reference texts (Englisch", comparison);
        Assert.Contains("sliding ring seal: 0, mechanical seal: 2", comparison);

        // im Dossier: Stand der eigenen Fachtexte, Abrufdatum nur bei Seiten aus dem Netz
        var dossier = await new DocumentResearch(ctx).ResearchAsync(new ResearchItem { Expression = "Gleitringdichtung", Context = "…" }, "de-DE", "en-GB", null,
            ct: TestContext.Current.CancellationToken);
        Assert.All(dossier.Sources.Where(s => s.Url.StartsWith("file:")), s => Assert.Null(s.Retrieved));
        Assert.Contains(dossier.Sources, s => s.Version == "Edition 2019-03");
    }

    private sealed class ScriptModel : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("Explain what the term means IN THIS CONTEXT"))
                return Task.FromResult("""{"meaning": "Dichtung an Wellen", "candidates": [{"term": "sliding ring seal", "type": "formal"}, {"term": "mechanical seal", "type": "functional"}], "queries": {}}""");
            if (p.Contains("Find up to 4 passages")) return Task.FromResult("""{"items": []}""");
            if (p.Contains("where")) return Task.FromResult("""{"sites": []}""");
            return Task.FromResult("""{"source": {"term": "Gleitringdichtung"}, "candidates": [{"term": "mechanical seal", "equivalence": "Full", "evidence": [2]}], "recommendation": {"term": "mechanical seal", "strategy": "funktionale Entsprechung"}, "warnings": []}""");
        }
    }

    private sealed class NoSearch : ISearchProvider
    {
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) => Task.FromResult(new List<SearchResult>());
        public Task<List<SearchResult>> SearchInSitesAsync(string q, string l, IReadOnlyList<string> s, int m, CancellationToken ct = default) => Task.FromResult(new List<SearchResult>());
    }
}
