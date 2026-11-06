using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Tests;

/// <summary>Tests gegen echte Dienste im Netz. Laufen nur auf Anforderung: dotnet test -- --explicit only</summary>
public class LiveTests
{
    /// <summary>Eigene OpenCode-Installation: Server starten, Modelle (kostenlose zuerst) auflisten, wieder beenden.</summary>
    [Fact(Explicit = true)]
    public async Task OpenCodeInstallationListsFreeModels()
    {
        Assert.True(OpenCodeServer.IsInstalled, "OpenCode ist nicht installiert");
        var config = new AiProviderConfig { Name = "OpenCode", Kind = AiProviderKind.OpenCode };
        try
        {
            var models = await OpenCodeServer.ModelsAsync(config, TestContext.Current.CancellationToken);
            foreach (var m in models.Take(12)) TestContext.Current.TestOutputHelper?.WriteLine($"{m.Id}  {(m.Free ? "kostenlos" : "")}  {m.DataNote}");
            Assert.True(models.First().Free);
            Assert.Contains(models, m => m.ProviderId == "opencode" && m.Free);
        }
        finally { OpenCodeServer.Stop(); }
    }

    /// <summary>Eine echte Übersetzung über ein kostenloses OpenCode-Modell (verbraucht einen Teil des Tageskontingents).</summary>
    [Fact(Explicit = true)]
    public async Task OpenCodeFreeModelTranslates()
    {
        var config = new AiProviderConfig { Name = "OpenCode", Kind = AiProviderKind.OpenCode, Model = "opencode/glm-5.3-flash-free", TimeoutSeconds = 180 };
        try
        {
            var answer = await ChatModelFactory.Create(config).AskAsync("You are a professional translator. Answer only with the translation.",
                "Translate from German into English: Das Gerät vor der Reinigung ausschalten.", TestContext.Current.CancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(answer);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-opencode-antwort.txt"), answer);
            Assert.Contains("device", answer, StringComparison.OrdinalIgnoreCase);
        }
        finally { OpenCodeServer.Stop(); }
    }

    /// <summary>
    /// Eingebaute KI mit echtem Modell (Gemma 4 E2B): Laufzeit starten, mit dem Übersetzungsagenten übersetzen, Zeiten messen.
    /// Ordner mit Laufzeit und Modell über SC_BUILTIN_FOLDER; Ergebnis zusätzlich in %TEMP%\sc-eingebaute-ki.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task BuiltInAiTranslates()
    {
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder) BuiltInAi.Folder = folder;
        var model = BuiltInAi.Model("gemma-4-e2b")!;
        Assert.True(BuiltInAi.IsInstalled(model), "Laufzeit und Modell fehlen in " + BuiltInAi.Folder);
        var report = new System.Text.StringBuilder();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var chat = ChatModelFactory.Create(AiSettings.BuiltInPreset(model));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var first = await chat.AskAsync("You are a professional translator. Answer only with the translation.",
                "Translate from German into English: Das Gerät vor der Reinigung ausschalten.", ct);
            report.AppendLine($"Start + erste Antwort: {watch.Elapsed.TotalSeconds:0.0} s → {first}");
            Assert.Contains("device", first, StringComparison.OrdinalIgnoreCase);

            var agent = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = chat });
            (string Source, string Target)[] cases =
            [
                ("Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "en-GB"),
                ("Entwicklung eines Systems zur Materialverfolgung von der Konstruktion bis in die Produktion.", "en-GB"),
                ("⟨1⟩Material & Einkauf:⟨/1⟩ Stücklisten, Bestelllisten, automatisierte Bestellprozesse, Materialnachverfolgung.", "en-GB"),
                ("Koordination komplexer Bauprojekte: Anforderungen klären, Aufgaben verteilen, Termine planen und die Umsetzung bis zur Produktion begleiten.", "en-GB"),
                ("Das Gerät vor der Reinigung ausschalten und den Netzstecker ziehen.", "pl-PL"),
                ("Freiwillige Feuerwehr Stimpfach; Erste-Hilfe-Schulung.", "pl-PL"),
            ];
            foreach (var (source, target) in cases)
            {
                watch.Restart();
                var result = await agent.TranslateAsync(source, "de-DE", target, "Lebenslauf / Technik", ct: ct);
                report.AppendLine($"[{target}] {watch.Elapsed.TotalSeconds:0.0} s");
                report.AppendLine($"  {source}");
                report.AppendLine($"  → {result}");
                Assert.False(string.IsNullOrWhiteSpace(result));
                foreach (var tag in new[] { "⟨1⟩", "⟨/1⟩" })
                    if (source.Contains(tag)) Assert.Contains(tag, result);
            }
        }
        finally
        {
            report.AppendLine("--- Laufzeit ---");
            foreach (var line in BuiltInAi.RecentLog.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l,
                         "Vulkan|GPU|offload|device|CPU|eval time|prompt eval|tokens per second|error|warn", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(60))
                report.AppendLine(line);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-eingebaute-ki.txt"), report.ToString());
            TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
    }

    /// <summary>
    /// Alle kostenlosen OpenCode-Modelle mit denselben Übersetzungsaufgaben (verbraucht Tageskontingent).
    /// Ergebnis zusätzlich in %TEMP%\sc-opencode-modelle.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task AllFreeOpenCodeModelsTranslate()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        (string Source, string Target)[] cases =
        [
            ("Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "en-GB"),
            ("Koordination komplexer Bauprojekte: Anforderungen klären, Aufgaben verteilen, Termine planen und die Umsetzung bis zur Produktion begleiten.", "en-GB"),
            ("Freiwillige Feuerwehr Stimpfach; Erste-Hilfe-Schulung.", "pl-PL"),
        ];
        var ok = 0;
        try
        {
            var models = (await OpenCodeServer.ModelsAsync(new AiProviderConfig { Kind = AiProviderKind.OpenCode }, ct))
                .Where(m => m.Free && m.ProviderId == "opencode").ToList();
            report.AppendLine($"{models.Count} kostenlose Modelle: {string.Join(", ", models.Select(m => m.ModelId))}");
            foreach (var m in models)
            {
                report.AppendLine($"== {m.Id} ({m.DataNote ?? "ohne Hinweis"})");
                var chat = ChatModelFactory.Create(new AiProviderConfig { Name = "OpenCode", Kind = AiProviderKind.OpenCode, Model = m.Id, TimeoutSeconds = 120 });
                var agent = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = chat });
                foreach (var (source, target) in cases)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var result = await agent.TranslateAsync(source, "de-DE", target, "Lebenslauf / Technik", ct: ct);
                        report.AppendLine($"  [{target}] {watch.Elapsed.TotalSeconds:0.0} s → {result}");
                        ok++;
                    }
                    catch (AiException ex)
                    {
                        report.AppendLine($"  [{target}] Fehler nach {watch.Elapsed.TotalSeconds:0.0} s: {ex.Message}");
                        if (ex.Message.Contains("Kontingent")) break;
                    }
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-opencode-modelle.txt"), report.ToString());
            OpenCodeServer.Stop();
        }
        Assert.True(ok > 0, report.ToString());
    }

    /// <summary>Kostenlose Online-Dienste ohne Anmeldung (OVHcloud, Kilo) über den Übersetzungsagenten. Ergebnis in %TEMP%\sc-online-ohne-konto.txt.</summary>
    [Fact(Explicit = true)]
    public async Task KeylessOnlineServicesTranslate()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        var ok = 0;
        foreach (var svc in AiDiscovery.FreeServices.Where(s => !s.NeedsKey))
        {
            try
            {
                var models = (await AiDiscovery.ModelsAsync(svc.BaseUrl, null, ct: ct)).Where(svc.IsFreeModel).ToList();
                var model = AiDiscovery.SuggestModel(models, svc.Preferred)!;
                report.AppendLine($"== {svc.Name}: {models.Count} Modelle, gewählt: {model}");
                var chat = ChatModelFactory.Create(new AiProviderConfig { Name = svc.Name, Kind = AiProviderKind.OpenAiCompatible, BaseUrl = svc.BaseUrl, Model = model, Temperature = 0.2 });
                var agent = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = chat });
                foreach (var (source, target) in new[] { ("Die Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.", "en-GB"), ("Freiwillige Feuerwehr Stimpfach; Erste-Hilfe-Schulung.", "pl-PL") })
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var result = await agent.TranslateAsync(source, "de-DE", target, "Lebenslauf / Technik", ct: ct);
                    report.AppendLine($"  [{target}] {watch.Elapsed.TotalSeconds:0.0} s → {result}");
                    ok++;
                }
            }
            catch (AiException ex)
            {
                report.AppendLine($"  Fehler: {ex.Message}");
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-online-ohne-konto.txt"), report.ToString());
        Assert.True(ok >= 2, report.ToString());
    }

    /// <summary>
    /// Freie Sprachdaten: Suche für de→en, echter Download und Import des EZB-Speichers und von IATE (nur de/en).
    /// Ordner über SC_LANGDATA_FOLDER; Ergebnis in %TEMP%\sc-sprachdaten.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task LanguageDataDownloadsAndImports()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Environment.GetEnvironmentVariable("SC_LANGDATA_FOLDER") ?? Path.Combine(Path.GetTempPath(), "sc-sprachdaten");
        var report = new System.Text.StringBuilder();
        try
        {
            var sets = await SententiamConverte.Core.Resources.LanguageData.SearchAsync("de-DE", "en-GB", ct);
            report.AppendLine($"{sets.Count} Datensätze, davon {sets.Count(s => s.Recommended)} empfohlen:");
            foreach (var s in sets.Where(s => s.Recommended).Take(20))
                report.AppendLine($"  {s.Name,-22} {s.KindText,-18} {s.UnitsText,12} Einheiten {s.SizeText,9}  {s.License}");

            foreach (var id in new[] { "opus-ecb", "iate" })
            {
                var set = sets.First(s => s.Id == id);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = await SententiamConverte.Core.Resources.LanguageData.InstallAsync(set, "de-DE", "en-GB", folder, null, ct);
                report.AppendLine($"{set.Name}: {result.Count:N0} Einträge in {watch.Elapsed.TotalSeconds:0} s → {result.Path} ({new FileInfo(result.Path).Length / 1e6:0} MB)");
                Assert.True(result.Count > 1000);
                if (set.Kind == SententiamConverte.Core.Remote.ResourceKind.Tm)
                {
                    using var tm = new SententiamConverte.Core.Tm.TranslationMemory(result.Path);
                    watch.Restart();
                    var hits = tm.Search("de-DE", "en-GB", "Die Europäische Zentralbank hat beschlossen, die Leitzinsen unverändert zu lassen.");
                    report.AppendLine($"  TM-Suche ({watch.ElapsedMilliseconds} ms): " + string.Join(" | ", hits.Take(2).Select(h => $"{h.Score}% {h.Unit.Target}")));
                }
                else
                {
                    using var tb = new SententiamConverte.Core.Terminology.Termbase(result.Path);
                    var hits = tb.Lookup("de-DE", "Zentralbank").Take(3).ToList();
                    report.AppendLine("  Terminologie „Zentralbank“: " + string.Join(" | ", hits.Select(c => string.Join(", ", c.Terms.Select(x => $"{x.Language}:{x.Text}").Take(4)))));
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-sprachdaten.txt"), report.ToString());
        }
    }

    /// <summary>Lokale KI-Server auf den üblichen Ports.</summary>
    [Fact(Explicit = true)]
    public async Task DetectsLocalServers()
    {
        foreach (var s in await AiDiscovery.DetectLocalAsync(TestContext.Current.CancellationToken))
            TestContext.Current.TestOutputHelper?.WriteLine($"{s.Name} {s.BaseUrl}: {string.Join(", ", s.Models)}");
    }

    [Fact(Explicit = true)]
    public async Task FetcherReadsPage()
    {
        var text = await new PageFetcher().FetchTextAsync("https://de.wikipedia.org/wiki/Drehmoment", 4000, TestContext.Current.CancellationToken);
        Assert.NotNull(text);
        Assert.Contains("Drehmoment", text);
    }

    /// <summary>
    /// Prüft die mitgelieferten Fachquellen: erlaubt robots.txt das Lesen, und kommt lesbarer Text an?
    /// Gibt eine Tabelle aus; schlägt nicht fehl, damit alle Seiten geprüft werden.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task SpecialistSitesAreReadable()
    {
        var samples = Environment.GetEnvironmentVariable("SC_SITES") is { Length: > 0 } list
            ? list.Split(';').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1])
            : new Dictionary<string, string>
        {
            ["gesetze-im-internet.de"] = "https://www.gesetze-im-internet.de/bgb/__433.html",
            ["dejure.org"] = "https://dejure.org/gesetze/BGB/433.html",
            ["bundesgerichtshof.de"] = "https://www.bundesgerichtshof.de/DE/DasGericht/dasGericht_node.html",
            ["bundesjustizamt.de"] = "https://www.bundesjustizamt.de/DE/Home/Home_node.html",
            ["ris.bka.gv.at"] = "https://www.ris.bka.gv.at/GeltendeFassung.wxe?Abfrage=Bundesnormen&Gesetzesnummer=10001622",
            ["oesterreich.gv.at"] = "https://www.oesterreich.gv.at/themen/dokumente_und_recht.html",
            ["fedlex.admin.ch"] = "https://www.fedlex.admin.ch/eli/cc/24/233_245_233/de",
            ["bger.ch"] = "https://www.bger.ch/de/index.htm",
            ["legislation.gov.uk"] = "https://www.legislation.gov.uk/ukpga/1979/54/section/14",
            ["judiciary.uk"] = "https://www.judiciary.uk/about-the-judiciary/",
            ["gov.uk"] = "https://www.gov.uk/make-court-claim-for-money",
            ["lawcom.gov.uk"] = "https://lawcom.gov.uk/about-us/",
            ["law.cornell.edu"] = "https://www.law.cornell.edu/wex/consideration",
            ["uscode.house.gov"] = "https://uscode.house.gov/browse.xhtml",
            ["supremecourt.gov"] = "https://www.supremecourt.gov/about/about.aspx",
            ["uscourts.gov"] = "https://www.uscourts.gov/about-federal-courts",
            ["isap.sejm.gov.pl"] = "https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU19640160093",
            ["sn.pl"] = "https://www.sn.pl/osadzie/Strony/default.aspx",
            ["gov.pl"] = "https://www.gov.pl/web/sprawiedliwosc",
            ["trybunal.gov.pl"] = "https://trybunal.gov.pl/o-trybunale",
            ["legifrance.gouv.fr"] = "https://www.legifrance.gouv.fr/codes/article_lc/LEGIARTI000006441370",
            ["courdecassation.fr"] = "https://www.courdecassation.fr/la-cour-de-cassation",
            ["service-public.fr"] = "https://www.service-public.fr/particuliers/vosdroits/F1426",
            ["normattiva.it"] = "https://www.normattiva.it/",
            ["giustizia.it"] = "https://www.giustizia.it/giustizia/",
            ["boe.es"] = "https://www.boe.es/buscar/act.php?id=BOE-A-1889-4763",
            ["poderjudicial.es"] = "https://www.poderjudicial.es/cgpj/es/Poder-Judicial/",
            ["eur-lex.europa.eu"] = "https://eur-lex.europa.eu/legal-content/DE/TXT/?uri=CELEX:32016R0679",
            ["iate.europa.eu"] = "https://iate.europa.eu/home",
            ["curia.europa.eu"] = "https://curia.europa.eu/jcms/jcms/Jo2_6999/de/",
            ["awmf.org"] = "https://register.awmf.org/de/leitlinien/aktuelle-leitlinien",
            ["rki.de"] = "https://www.rki.de/DE/Themen/Infektionskrankheiten/infektionskrankheiten-node.html",
            ["gesund.bund.de"] = "https://gesund.bund.de/bluthochdruck",
            ["gesundheitsinformation.de"] = "https://www.gesundheitsinformation.de/bluthochdruck.html",
            ["bfarm.de"] = "https://www.bfarm.de/DE/Arzneimittel/_node.html",
            ["nhs.uk"] = "https://www.nhs.uk/conditions/high-blood-pressure-hypertension/",
            ["nice.org.uk"] = "https://www.nice.org.uk/guidance/ng136",
            ["ncbi.nlm.nih.gov"] = "https://www.ncbi.nlm.nih.gov/books/NBK539859/",
            ["medlineplus.gov"] = "https://medlineplus.gov/highbloodpressure.html",
            ["cdc.gov"] = "https://www.cdc.gov/high-blood-pressure/about/index.html",
            ["fda.gov"] = "https://www.fda.gov/drugs",
            ["pzh.gov.pl"] = "https://www.pzh.gov.pl/",
            ["mp.pl"] = "https://www.mp.pl/pacjent/nadcisnienie/",
            ["ema.europa.eu"] = "https://www.ema.europa.eu/en/about-us",
            ["who.int"] = "https://www.who.int/news-room/fact-sheets/detail/hypertension",
            ["electropedia.org"] = "https://www.electropedia.org/iev/iev.nsf/display?openform&ievref=151-11-01",
            ["iso.org"] = "https://www.iso.org/about",
            ["iec.ch"] = "https://www.iec.ch/who-we-are",
            ["din.de"] = "https://www.din.de/de/ueber-normen-und-standards",
            ["dke.de"] = "https://www.dke.de/de/ueber-uns",
            ["vdi.de"] = "https://www.vdi.de/ueber-uns",
            ["baua.de"] = "https://www.baua.de/DE/Themen/Arbeitsgestaltung/Arbeitsgestaltung_node.html",
            ["bsigroup.com"] = "https://www.bsigroup.com/en-GB/about-bsi/",
            ["hse.gov.uk"] = "https://www.hse.gov.uk/work-equipment-machinery/",
            ["ansi.org"] = "https://www.ansi.org/about/introduction",
            ["osha.gov"] = "https://www.osha.gov/machine-guarding",
            ["nist.gov"] = "https://www.nist.gov/about-nist",
            ["pkn.pl"] = "https://www.pkn.pl/",
            ["ciop.pl"] = "https://www.ciop.pl/",
            ["europa.eu"] = "https://european-union.europa.eu/institutions-law-budget/law_de",
            ["publications.europa.eu"] = "https://op.europa.eu/de/home",
        };
        var fetcher = new PageFetcher();
        var lines = new List<string>();
        await Parallel.ForEachAsync(samples, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken }, async (kv, ct) =>
        {
            var f = new PageFetcher();
            var text = await f.FetchTextAsync(kv.Value, 20000, ct);
            lock (lines) lines.Add($"{kv.Key}\t{(text == null ? "NEIN: " + f.LastSkipReason : $"ok {text.Length} Zeichen")}");
        });
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fachseiten-pruefung.txt"), lines.OrderBy(l => l));
        foreach (var l in lines.OrderBy(l => l)) TestContext.Current.TestOutputHelper?.WriteLine(l);
    }
}
