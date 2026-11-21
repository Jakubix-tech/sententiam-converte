using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Freie Sprachdaten: Suche, Download und Import gegen einen lokalen Prüfserver, der OPUS und IATE nachbildet.</summary>
[Collection("BuiltInAi")]
public sealed class LanguageDataTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentQueue<string> _queries = new();
    private readonly string _base;
    private readonly string _oldApi = LanguageData.OpusApi, _oldIate = LanguageData.IateUrl, _oldTermium = LanguageData.TermiumBase;

    public LanguageDataTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
        LanguageData.OpusApi = _base + "opusapi/";
        LanguageData.IateUrl = _base + "iate.zip";
        LanguageData.TermiumBase = _base + "termium/";
    }

    public void Dispose()
    {
        LanguageData.OpusApi = _oldApi;
        LanguageData.IateUrl = _oldIate;
        LanguageData.TermiumBase = _oldTermium;
        _listener.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
            if (path == "opusapi/")
            {
                _queries.Enqueue(ctx.Request.Url.Query);
                path = "opusapi" + ctx.Request.Url.Query;
            }
            if (!_files.TryGetValue(path, out var data))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }
            ctx.Response.ContentLength64 = data.Length;
            await ctx.Response.OutputStream.WriteAsync(data);
            ctx.Response.Close();
        }
    }

    /// <summary>TMX in Gegenrichtung (Englisch → Deutsch), wie OPUS es für de-en liefert.</summary>
    private static byte[] TmxGz()
    {
        var tmx = new StringBuilder("""<?xml version="1.0" encoding="utf-8"?><!DOCTYPE tmx SYSTEM "tmx14.dtd"><tmx version="1.4"><header srclang="en" datatype="plaintext" segtype="sentence" adminlang="en" o-tmf="x" creationtool="x" creationtoolversion="1"/><body>""");
        for (var i = 0; i < 3000; i++)
            tmx.Append($"""<tu><tuv xml:lang="en"><seg>The central bank kept rate number {i} unchanged.</seg></tuv><tuv xml:lang="de"><seg>Die Zentralbank ließ den Zinssatz Nummer {i} unverändert.</seg></tuv></tu>""");
        tmx.Append("""<tu><tuv xml:lang="en"><seg>Only English.</seg></tuv></tu></body></tmx>""");
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(Encoding.UTF8.GetBytes(tmx.ToString()));
        return ms.ToArray();
    }

    private static byte[] IateZip()
    {
        const string tbx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <martif type="TBX"><text><body>
              <termEntry id="1"><descrip type="subjectField">Finanzen</descrip>
                <langSet xml:lang="de"><tig><term>Zentralbank</term><termNote type="termType">fullForm</termNote></tig><tig><term>Notenbank</term></tig></langSet>
                <langSet xml:lang="en"><tig><term>central bank</term></tig></langSet>
                <langSet xml:lang="fr"><tig><term>banque centrale</term></tig></langSet></termEntry>
              <termEntry id="2"><langSet xml:lang="fr"><tig><term>seulement français</term></tig></langSet>
                <langSet xml:lang="de"><tig><term>nur Deutsch</term></tig></langSet></termEntry>
              <termEntry id="3"><langSet xml:lang="de"><tig><term>Geldpolitik</term></tig></langSet>
                <langSet xml:lang="en"><tig><term>monetary policy</term><termNote type="administrativeStatus">obsolete</termNote></tig></langSet></termEntry>
            </body></text></martif>
            """;
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("IATE_export.tbx").Open());
            w.Write(tbx);
        }
        return ms.ToArray();
    }

    private void OfferCatalog()
    {
        // OPUS führt de-en nur in dieser Richtung; Zahlen teils als Text, ein Datensatz doppelt, ein Riesenkorpus
        _files["opusapi?source=en&target=de&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes("""{"corpora":[]}""");
        _files["opusapi?source=de&target=en&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes($$"""
            {"corpora":[
             {"corpus":"ECB","version":"v1","alignment_pairs":3000,"size":"120","url":"{{_base}}ecb.tmx.gz"},
             {"corpus":"ELRC-1077-EUIPO_law","version":"v1","alignment_pairs":"233849","size":29800,"url":"{{_base}}a.tmx.gz"},
             {"corpus":"ELRC-EUIPO_law","version":"v1","alignment_pairs":"233849","size":29800,"url":"{{_base}}b.tmx.gz"},
             {"corpus":"CCMatrix","version":"v1","alignment_pairs":247470736,"size":22985118,"url":"{{_base}}c.tmx.gz"},
             {"corpus":"Leer","version":"v1","alignment_pairs":"","size":"","url":"{{_base}}d.tmx.gz"}
            ]}
            """);
        _files["ecb.tmx.gz"] = TmxGz();
        _files["iate.zip"] = IateZip();
    }

    [Fact]
    public async Task SearchOffersRecommendedDataFirst()
    {
        OfferCatalog();
        var sets = await LanguageData.SearchAsync("en-GB", "de-DE", TestContext.Current.CancellationToken);
        Assert.Contains(_queries, q => q.Contains("source=en&target=de"));
        Assert.Contains(_queries, q => q.Contains("source=de&target=en"));       // Gegenrichtung, weil die erste leer war

        Assert.Equal(["IATE", "ELRC-EUIPO_law", "ECB", "CCMatrix"], sets.Select(s => s.Name));   // Doppelter und leerer fallen weg
        Assert.True(sets.Single(s => s.Name == "ECB").Recommended);
        Assert.False(sets.Single(s => s.Name == "CCMatrix").Recommended);        // aus dem Netz gesammelt, riesig
        Assert.Contains("EUIPO law", sets.Single(s => s.Name == "ELRC-EUIPO_law").Description);
        Assert.Equal(ResourceKind.Termbase, sets[0].Kind);
        Assert.Equal("de", LanguageData.OpusCode("de-DE"));
        Assert.Equal("pt_BR", LanguageData.OpusCode("pt-BR"));
    }

    [Fact]
    public async Task TranslationMemoryIsImportedInProjectDirection()
    {
        OfferCatalog();
        var set = (await LanguageData.SearchAsync("de-DE", "en-GB", TestContext.Current.CancellationToken)).Single(s => s.Name == "ECB");
        var result = await LanguageData.InstallAsync(set, "de-DE", "en-GB", _dir.Path, null, TestContext.Current.CancellationToken);

        Assert.Equal(3000, result.Count);
        Assert.EndsWith("ECB de-en.sctm", result.Path);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.gz"));                      // Download aufgeräumt
        using var tm = new TranslationMemory(result.Path);
        var hit = Assert.Single(tm.Search("de-DE", "en-GB", "Die Zentralbank ließ den Zinssatz Nummer 42 unverändert.", minScore: 100));
        Assert.Equal("The central bank kept rate number 42 unchanged.", hit.Unit.Target);
    }

    [Fact]
    public async Task IateIsReducedToTheProjectLanguages()
    {
        OfferCatalog();
        var iate = (await LanguageData.SearchAsync("de-DE", "en-GB", TestContext.Current.CancellationToken)).Single(s => s.Id == "iate");
        var result = await LanguageData.InstallAsync(iate, "de-DE", "en-GB", _dir.Path, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);                                              // Eintrag ohne Englisch fällt weg
        using var tb = new Termbase(result.Path);
        var concept = Assert.Single(tb.Lookup("de-DE", "Zentralbank"));
        Assert.Equal(["de:Notenbank", "de:Zentralbank", "en:central bank"], concept.Terms.Select(t => $"{t.Language}:{t.Text}").Order());
        Assert.Equal("Finanzen", concept.Domain);
        Assert.Equal(TermStatus.Forbidden, tb.Lookup("de-DE", "Geldpolitik").Single().Terms.Single(t => t.Language == "en").Status);
    }

    [Fact]
    public async Task NotEnoughSpaceOrEmptyDataGiveClearMessages()
    {
        OfferCatalog();
        var empty = new LanguageDataSet("opus-x", "X", "", "", "", _base + "ecb.tmx.gz", 1000, 10, ResourceKind.Tm, true);
        var ex = await Assert.ThrowsAsync<Core.Ai.AiException>(() => LanguageData.InstallAsync(empty, "pl-PL", "fr-FR", _dir.Path, null, TestContext.Current.CancellationToken));
        Assert.Contains("keine Einträge", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.sctm"));

        var giant = new LanguageDataSet("opus-y", "Y", "", "", "", _base + "ecb.tmx.gz", 1L << 50, 1L << 40, ResourceKind.Tm, false);
        ex = await Assert.ThrowsAsync<Core.Ai.AiException>(() => LanguageData.InstallAsync(giant, "de-DE", "en-GB", _dir.Path, null, TestContext.Current.CancellationToken));
        Assert.Contains("Zu wenig Speicherplatz", ex.Message);
    }

    /// <summary>TERMIUM-ZIP wie das Original: CSV mit Spalten je Sprache, Felder in Anführungszeichen mit Kommas und Umbrüchen.</summary>
    private static byte[] TermiumZip()
    {
        const string csv = "SUBJECT_EN,TERM_EN,TERM_EN_PARAMETER,ABBREVIATION_EN,ABBREVIATION_EN_PARAMETER,SYNONYMS_EN,SYNONYMS_EN_PARAMETERS,TEXTUAL_SUPPORT_1_EN,TEXTUAL_SUPPORT_2_EN,TEXTUAL_SUPPORT_3_EN,DOMAINE_FR,TERME_FR,TERME_FR_PARAMETRE,ABBREVIATION_FR,ABBREVIATION_FR_PARAMETRE,SYNONYMES_FR,SYNONYMES_FR_PARAMETRE,JUSTIFICATION_1_FR,JUSTIFICATION_2_FR,JUSTIFICATION_3_FR\r\n"
            + "\"Space Robotics\",\"adaptive robotics\",noun,AR,noun,\"self-adapting robotics;adaptable robotics\",\"correct;avoid\",\"DEF: Robotics that adapts, quickly.\",\"CONT: Long\r\nquote.\",,\"Robotique spatiale\",\"robotique adaptative\",\"correct,feminine noun,standardized\",,,,,,,\r\n"
            + "\"Space Robotics\",\"orbital debris\",correct,,,,,,,,,,,,,,,,,\r\n";
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("Robotique_spatiale_Space_Robotics.csv").Open(), new UTF8Encoding(true));
            w.Write(csv);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task TermiumIsOfferedForEnglishFrenchAndImported()
    {
        var ct = TestContext.Current.CancellationToken;
        _files["opusapi?source=en&target=fr&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes("""{"corpora":[]}""");
        _files["opusapi?source=fr&target=en&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes("""{"corpora":[]}""");
        _files["termium/domaine-RobotiqueSpatiale-subject-SpaceRobotics.zip"] = TermiumZip();

        var sets = await LanguageData.SearchAsync("en-GB", "fr-CA", ct);
        var termium = sets.Where(s => s.Id.StartsWith("termium-")).ToList();
        Assert.Equal(23, termium.Count);
        Assert.All(termium, s => Assert.True(s.CommercialUse));
        Assert.Contains(termium, s => s.Recommended && s.Category == "Recht und Verwaltung");
        _files["opusapi?source=de&target=fr&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes("""{"corpora":[]}""");
        _files["opusapi?source=fr&target=de&preprocessing=tmx&version=latest"] = Encoding.UTF8.GetBytes("""{"corpora":[]}""");
        Assert.DoesNotContain(await LanguageData.SearchAsync("de-DE", "fr-FR", ct), s => s.Id.StartsWith("termium-"));   // nur Englisch–Französisch

        var space = termium.Single(s => s.Name.EndsWith("Space Robotics"));
        var result = await LanguageData.InstallAsync(space, "en-GB", "fr-CA", _dir.Path, null, ct);
        Assert.Equal(1, result.Count);                                                    // „orbital debris“ ohne Französisch fällt weg
        using var tb = new Termbase(result.Path);
        var concept = Assert.Single(tb.AllConcepts());
        Assert.Equal("Robotics that adapts, quickly.", concept.Definition);              // nur die Definition, kein Beleg
        Assert.Equal("Robotique spatiale", concept.Domain);
        Assert.Contains(concept.Terms, t => t is { Language: "fr-CA", Text: "robotique adaptative", Status: TermStatus.Preferred });
        Assert.Contains(concept.Terms, t => t is { Language: "en-GB", Text: "self-adapting robotics", Status: TermStatus.Admitted });
        Assert.Contains(concept.Terms, t => t is { Text: "adaptable robotics", Status: TermStatus.Forbidden });    // „avoid“
        Assert.Contains(concept.Terms, t => t is { Text: "AR" });
        var hit = Assert.Single(tb.Recognize("The adaptive robotics lab.", "en-GB", "fr-CA"));
        Assert.Contains(hit.Translations, t => t.Text == "robotique adaptative");
    }
}
