using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Einarbeiten in ein Gesetz: amtliche Fassung laden, Paragraphen durchsuchbar machen, Begriffe aus Überschriften und
/// Legaldefinitionen mit Fundstelle ablegen, englische Benennungen aus der Übersetzung (nur Überschriften, keine Sätze).
/// </summary>
[Collection("BuiltInAi")]
public sealed class LawStudyTests : IDisposable
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <dokumente>
          <norm builddate="20260928220003"><metadaten><jurabk>StGB</jurabk><langue>Strafgesetzbuch</langue>
            <standangabe><standtyp>Neuf</standtyp><standkommentar>Neugefasst durch Bek. v. 13.11.1998 I 3322;</standkommentar></standangabe></metadaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 11</enbez><titel format="parat">Personen- und Sachbegriffe</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(1) Im Sinne dieses Gesetzes ist</P><DL Type="arabic"><DT>1.</DT><DD><LA>Angehöriger:</LA><LA>wer zu den folgenden Personen gehört: a) Verwandte gerader Linie, der Ehegatte, b) Pflegeeltern und Pflegekinder;</LA></DD>
            <DT>7.</DT><DD><LA>Behörde:</LA><LA>auch ein Gericht;</LA></DD><DT>9.</DT><DD><LA>Entgelt:</LA><LA>jede in einem Vermögensvorteil bestehende Gegenleistung.</LA></DD></DL>
            <P>(2) Vorsätzlich im Sinne dieses Gesetzes ist eine Tat auch dann, wenn sie einen gesetzlichen Tatbestand verwirklicht.</P></Content></text></textdaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 32</enbez><titel format="parat">Notwehr</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(1) Wer eine Tat begeht, die durch Notwehr geboten ist, handelt nicht rechtswidrig.</P>
            <P>(2) Notwehr ist die Verteidigung, die erforderlich ist, um einen gegenwärtigen rechtswidrigen Angriff von sich oder einem anderen abzuwenden.</P></Content></text></textdaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 242</enbez><titel format="parat">Diebstahl</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(1) Wer eine fremde bewegliche Sache einem anderen in der Absicht wegnimmt, die Sache sich oder einem Dritten rechtswidrig zuzueignen, wird mit Freiheitsstrafe bis zu fünf Jahren oder mit Geldstrafe bestraft.</P><P>(2) Der Versuch ist strafbar.</P><P>(3) Ausgenommen ist eine Sache, die herrenlos ist und niemandem gehört.</P></Content></text></textdaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 244</enbez><titel format="parat">Diebstahl mit Waffen; Bandendiebstahl</titel></metadaten>
            <textdaten><text format="XML"><Content><P>(1) Mit Freiheitsstrafe von sechs Monaten bis zu zehn Jahren wird bestraft, wer einen Diebstahl begeht, bei dem er eine Waffe bei sich führt.</P></Content></text></textdaten></norm>
          <norm><metadaten><jurabk>StGB</jurabk><enbez>§ 248</enbez><titel format="parat">(weggefallen)</titel></metadaten><textdaten><text format="XML"><Content><P>-</P></Content></text></textdaten></norm>
        </dokumente>
        """;

    private const string English = """
        <html><body>
        <a href="englisch_stgb.html#p0300">Section 32 Self-defence</a>
        <a href="englisch_stgb.html#p2200">Section 242 Theft</a>
        <a href="englisch_stgb.html#p2210">Section 244 Theft with weapons; gang theft</a>
        <p style="text-align: center; font-weight: bold"><a name="p0098"><!----></a>Section 11<br />Definitions</p>
        <p><a name="p0099"><!----></a>(1) For the purposes of this Act</p>
        <p class="Liste1"><a name="p0100"><!----></a>1.&#160;&#160;&#8216;relative&#8217; means anyone who</p>
        <p class="Liste1"><a name="p0118"><!----></a>7.&#160;&#160;&#8216;authority&#8217; also means a court;</p>
        <p class="Liste1"><a name="p0120"><!----></a>9.&#160;&#160;&#8216;a consideration&#8217; means any pecuniary benefit.</p>
        <p style="text-align: center; font-weight: bold"><a name="p0123"><!----></a>Section<i> </i>12<br />Serious and less serious criminal offences</p>
        <p class="Liste1"><a name="p0124"><!----></a>1.&#160;&#160;&#8216;not a term&#8217; means something else</p>
        </body></html>
        """;

    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly string _oldBase = LawSources.GiiBase;

    public LawStudyTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
        LawSources.GiiBase = $"http://localhost:{port}";
    }

    public void Dispose()
    {
        LawSources.GiiBase = _oldBase;
        _listener.Close();
        _dir.Dispose();
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            byte[] body;
            if (path == "/stgb/xml.zip")
            {
                using var ms = new MemoryStream();
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (var w = new StreamWriter(zip.CreateEntry("BJNR001270871.xml").Open(), new UTF8Encoding(false)))
                    w.Write(Xml);
                body = ms.ToArray();
            }
            else if (path == "/englisch_stgb/englisch_stgb.html") body = Encoding.UTF8.GetBytes(English);
            else if (path == "/robots.txt") body = "User-agent: *\nDisallow:\n"u8.ToArray();
            else { ctx.Response.StatusCode = 404; body = []; }
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    [Fact]
    public async Task StudiesTheCriminalCodeIntoTermsKnowledgeAndOwnTexts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var library = new ReferenceLibrary(_dir.File("index.scidx"));
        using var termbase = new Termbase(_dir.File("t.sctb"));
        using var knowledge = new KnowledgeBase(_dir.File("w.sckb"), "Recht");
        var result = await LawStudy.StudyAsync("StGB", "en-GB", library, termbase, knowledge, ct: ct);

        Assert.Equal(("StGB", "Strafgesetzbuch", "Neugefasst durch Bek. v. 13.11.1998 I 3322", 5), (result.Abbreviation, result.Title, result.Version, result.Sections));
        // Begriffsliste wie § 11: jeder Eintrag ist eine Legaldefinition, die englische Benennung kommt über die Nummer
        var relative = Assert.Single(result.Terms, t => t.Term == "Angehöriger");
        Assert.Equal(("§ 11", "relative", true), (relative.Section, relative.Target, relative.LegalDefinition));
        Assert.StartsWith("Angehöriger: wer zu den folgenden Personen gehört: a) Verwandte gerader Linie", relative.Definition);
        Assert.Equal(("Behörde: auch ein Gericht", "authority"), result.Terms.Single(t => t.Term == "Behörde") is var b ? (b.Definition, b.Target) : default);
        Assert.Equal("consideration", result.Terms.Single(t => t.Term == "Entgelt").Target);     // ohne Artikel
        Assert.DoesNotContain(result.Terms, t => t.Term.StartsWith("Vorsätzlich") || t.Term.StartsWith("Ausgenommen"));
        var notwehr = Assert.Single(result.Terms, t => t.Term == "Notwehr");
        Assert.True(notwehr.LegalDefinition);                                            // die Legaldefinition schlägt die Überschrift
        Assert.StartsWith("Notwehr ist die Verteidigung, die erforderlich ist", notwehr.Definition);
        Assert.Equal(("§ 32", "Self-defence"), (notwehr.Section, notwehr.Target));
        var theft = Assert.Single(result.Terms, t => t.Term == "Diebstahl");
        Assert.Equal("Wer eine fremde bewegliche Sache einem anderen in der Absicht wegnimmt, die Sache sich oder einem Dritten rechtswidrig zuzueignen, wird mit Freiheitsstrafe bis zu fünf Jahren oder mit Geldstrafe bestraft.", theft.Definition);
        Assert.Equal("Theft", theft.Target);
        Assert.Equal("gang theft", result.Terms.Single(t => t.Term == "Bandendiebstahl").Target);   // geteilte Überschrift paarweise
        Assert.DoesNotContain(result.Terms, t => t.Term.Contains("weggefallen"));
        Assert.EndsWith("/stgb/__242.html", theft.Url);

        // Terminologie: deutsch bevorzugt, englisch zugelassen, Definition mit Fundstelle
        var concept = termbase.Lookup("de-DE", "Diebstahl").Single(c => c.Terms.Any(t => t.Text == "Diebstahl"));
        Assert.Contains(concept.Terms, t => t is { Text: "Theft", Status: TermStatus.Admitted });
        Assert.Contains("(§ 242 StGB)", concept.Definition);
        Assert.Contains("nicht amtlichen Übersetzung", concept.Definition);
        // Wissen: Definition mit Fundstelle und Stand
        var entry = knowledge.Search("de-DE", "Notwehr").First(e => e.Title == "Notwehr");
        Assert.Equal(KnowledgeKind.Definition, entry.Kind);
        Assert.StartsWith("§ 32 StGB (Stand: Neugefasst", entry.SourceTitle);
        // Eigene Fachtexte: jeder Paragraph durchsuchbar, mit Abschnitt als Fundstelle
        var hit = Assert.Single(library.FindPhrase("bewegliche Sache", "de-DE"));
        Assert.Equal("StGB – Strafgesetzbuch, § 242 Diebstahl", hit.Label);

        // zweites Einarbeiten: nichts doppelt, die Quelle bleibt beim Einlesen der Ordner stehen
        var again = await LawStudy.StudyAsync("StGB", "en-GB", library, termbase, knowledge, ct: ct);
        Assert.Equal((0, 0), (again.TermsSaved, again.KnowledgeSaved));
        await library.ScanAsync([], ct: ct);
        Assert.Single(library.Files());
    }

    [Fact]
    public async Task UnknownLawsAreReportedClearly()
    {
        var ex = await Assert.ThrowsAsync<AiException>(() => LawStudy.LoadAsync("GibtsNicht", TestContext.Current.CancellationToken));
        Assert.Contains("gibt es auf gesetze-im-internet.de nicht", ex.Message);
    }

    /// <summary>Echtes StGB von gesetze-im-internet.de (nur auf Anforderung).</summary>
    [Fact(Explicit = true)]
    public async Task RealCriminalCode()
    {
        LawSources.GiiBase = _oldBase;
        var (_, title, version, _, sections) = await LawStudy.LoadAsync("StGB", TestContext.Current.CancellationToken);
        var english = await LawStudy.EnglishHeadingsAsync("StGB", TestContext.Current.CancellationToken);
        var terms = LawStudy.ExtractTerms(sections, english);
        var report = $"{title} – {version}\n{sections.Count} Paragraphen, {english.Count} englische Überschriften, {terms.Count} Begriffe, " +
                     $"{terms.Count(t => t.LegalDefinition)} Legaldefinitionen, {terms.Count(t => t.Target != null)} mit Englisch\n" +
                     string.Join("\n", terms.Where(t => t.LegalDefinition).Take(25).Select(t => $"{t.Section} {t.Term} → {t.Target}: {t.Definition[..Math.Min(90, t.Definition.Length)]}")) + "\n…\n" +
                     string.Join("\n", terms.Where(t => !t.LegalDefinition).Take(15).Select(t => $"{t.Section} {t.Term} → {t.Target}"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-lokal-stgb.txt"), report);
        Assert.True(sections.Count > 300);
        Assert.Contains(terms, t => t.Term == "Diebstahl" && t.Target == "Theft");
    }
}
