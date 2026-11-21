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

    /// <summary>
    /// Die KI steuert die Recherche selbst (Wikipedia/Wiktionary/Web, Datenbanken, Skills, Gedächtnis) – mit dem
    /// kostenlosen Kilo-Modell und, wenn SC_BUILTIN_FOLDER gesetzt ist, mit der eingebauten KI. Bericht in %TEMP%\sc-recherche.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ResearchAgentIsSteeredByTheAi()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        var kilo = AiDiscovery.FreeServices.Single(s => s.Id == "kilo");
        var configs = new List<AiProviderConfig>
        {
            new() { Name = "Kilo", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = kilo.BaseUrl, Model = "kilo-auto/free", Temperature = 0.2, MaxCharsPerPage = 3000 },
        };
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder)
        {
            BuiltInAi.Folder = folder;
            configs.Add(AiSettings.BuiltInPreset());
        }
        var ok = 0;
        try
        {
            foreach (var config in configs)
            {
                using var dir = new TempDir();
                using var kb = new SententiamConverte.Core.Knowledge.KnowledgeBase(dir.File("gedaechtnis.sckb"));
                var skills = new SententiamConverte.Core.Agents.SkillLibrary(dir.File("skills"));
                var log = new System.Text.StringBuilder();
                var ctx = new SententiamConverte.Core.Agents.AgentContext
                {
                    Model = ChatModelFactory.Create(config), Search = null, MaxCharsPerPage = config.MaxCharsPerPage,
                    Log = new SyncLog(log), ExplanationLanguage = "Deutsch",
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                report.AppendLine($"===== {config.Name}");
                try
                {
                    var result = await new SententiamConverte.Core.Agents.ResearchAgent(ctx, skills, new SententiamConverte.Core.Agents.KnowledgeMemory(kb))
                        .RunAsync("Wie heißt „Abkantpresse“ (Blechbearbeitung) auf Polnisch? Mit Beleg.", "de-DE", ["pl-PL"], 8, true, true, ct);
                    report.AppendLine($"{watch.Elapsed.TotalSeconds:0} s, {result.Steps.Count} Schritte, fertig: {result.Finished}");
                    report.Append(log);
                    report.AppendLine("ANTWORT: " + result.Answer);
                    report.AppendLine("QUELLEN: " + string.Join(", ", result.Sources.Select(s => s.Url)));
                    report.AppendLine("GEDÄCHTNIS: " + kb.Count() + " Einträge; GELERNT: " + (result.LearnedSkill?.Name ?? "–"));
                    if (result.LearnedSkill != null) report.AppendLine(result.LearnedSkill.Instructions);
                    if (result.Finished && result.Answer.Contains("krawędziow", StringComparison.OrdinalIgnoreCase)) ok++;
                }
                catch (AiException ex)
                {
                    report.Append(log);
                    report.AppendLine("FEHLER: " + ex.Message);
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-recherche.txt"), report.ToString());
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
        Assert.True(ok > 0, report.ToString());
    }

    /// <summary>
    /// Kostenloser Verbund: die empfohlenen Modelle ohne Konto (OVHcloud, Kilo) übersetzen 12 Segmente gleichzeitig,
    /// wie die Stapelübersetzung im Editor. Bericht in %TEMP%\sc-verbund.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task FreePoolTranslatesInParallel()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        try
        {
            var candidates = await AiDiscovery.FreeModelsAsync([], null, ct);
            foreach (var c in candidates) report.AppendLine($"{(c.Recommended ? "[x]" : "[ ]")} {c.Config.Name}");
            var pool = new AiProviderConfig
            {
                Name = "Verbund", Kind = AiProviderKind.Pool, Members = candidates.Where(c => c.Recommended).Select(c => c.Config).ToList(),
            };
            Assert.True(pool.Members.Count >= 2, report.ToString());
            var model = (PooledChatModel)ChatModelFactory.Create(pool);
            model.Report = m => { lock (report) report.AppendLine("  " + m); };
            var translator = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = model });
            string[] sources =
            [
                "Schalten Sie das Gerät vor der Reinigung aus.", "Ziehen Sie den Netzstecker.", "Die Abdeckung mit einem feuchten Tuch abwischen.",
                "Verwenden Sie keine Scheuermittel.", "Prüfen Sie die Dichtung auf Risse.", "Setzen Sie die Abdeckung wieder ein.",
                "Das Gerät erst nach dem Trocknen einschalten.", "Wartung nur durch Fachpersonal.", "Ersatzteile erhalten Sie beim Hersteller.",
                "Bewahren Sie diese Anleitung auf.", "Bei Störungen den Kundendienst anrufen.", "Die Garantie beträgt zwei Jahre.",
            ];
            var results = new string?[sources.Length];
            var next = -1;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, model.Parallelism).Select(async _ =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < sources.Length)
                {
                    try { results[i] = await translator.TranslateAsync(sources[i], "de-DE", "en-GB", null, ct: ct); }
                    catch (AiException ex) { results[i] = "FEHLER: " + ex.Message; }
                }
            }));
            report.AppendLine($"{watch.Elapsed.TotalSeconds:0} s für {sources.Length} Segmente mit {model.Parallelism} Modellen");
            for (var i = 0; i < sources.Length; i++) report.AppendLine($"  {sources[i]} → {results[i]}");
            foreach (var u in model.Usage) report.AppendLine($"  {u.Name}: {u.Done} Antworten, {u.Failed} Fehler");
            Assert.All(results, r => Assert.False(r!.StartsWith("FEHLER"), r));
            Assert.True(model.Usage.Count(u => u.Done > 0) >= 2, report.ToString());       // wirklich mehrere Modelle im Einsatz
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-verbund.txt"), report.ToString());
        }
    }

    /// <summary>Datenschutz mit echten Online-Modellen (kostenloser Verbund): behalten sie die Platzhalter? Bericht in %TEMP%\sc-datenschutz.txt.</summary>
    [Fact(Explicit = true)]
    public async Task PrivacyWithTheFreePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        var before = SententiamConverte.Core.Privacy.PrivacyContext.Current;
        try
        {
            var candidates = await AiDiscovery.FreeModelsAsync([], null, ct);
            var pool = new AiProviderConfig { Name = "Verbund", Kind = AiProviderKind.Pool, Members = candidates.Where(c => c.Recommended).Select(c => c.Config).ToList() };
            SententiamConverte.Core.Privacy.PrivacyContext.Current = new SententiamConverte.Core.Privacy.PrivacySettings { Enabled = true, Terms = ["Bauhaus GmbH", "Leipzig"] };
            var model = ChatModelFactory.Create(pool);
            var privacy = Assert.IsType<SententiamConverte.Core.Privacy.PrivacyChatModel>(model);
            var translator = new SententiamConverte.Core.Agents.AiTranslator(new SententiamConverte.Core.Agents.AgentContext { Model = model });
            string[] sources =
            [
                "Herr Müller hat am 12.03.2024 unter dem Aktenzeichen 2 O 123/24 Klage gegen die Bauhaus GmbH erhoben.",
                "Die Zustellung an Frau Dr. Anna Schmidt erfolgte am 3. April 2024 in Leipzig.",
                "Rückfragen bitte an max.mueller@example.de oder +49 341 1234567.",
                "Das Landgericht Leipzig wies die Berufung von Herrn Müller (VI ZR 45/20) zurück.",
            ];
            foreach (var target in new[] { "en-GB", "pl-PL" })
                foreach (var s in sources)
                {
                    var result = await translator.TranslateAsync(s, "de-DE", target, "Recht", ct: ct);
                    var leftover = SententiamConverte.Core.Privacy.SensitiveData.PlaceholderRegex().IsMatch(result);
                    report.AppendLine($"[{target}] {s}\n   → {result}{(leftover ? "   ⚠ PLATZHALTER ÜBRIG" : "")}");
                }
            report.AppendLine($"Ersetzt: {privacy.MaskedValues} Angaben");
            Assert.DoesNotContain("[NAME", report.ToString());
        }
        finally
        {
            SententiamConverte.Core.Privacy.PrivacyContext.Current = before;
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-datenschutz.txt"), report.ToString());
        }
    }

    /// <summary>Alte Übersetzungen mit dem kostenlosen Verbund: zwei Dateien zuordnen, mit KI prüfen, Fachbegriffe vorschlagen. Bericht in %TEMP%\sc-altbestand.txt.</summary>
    [Fact(Explicit = true)]
    public async Task ImportingOldTranslationsWithTheFreePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        try
        {
            var candidates = await AiDiscovery.FreeModelsAsync([], null, ct);
            var pool = new AiProviderConfig { Name = "Verbund", Kind = AiProviderKind.Pool, Members = candidates.Where(c => c.Recommended).Select(c => c.Config).ToList() };
            var model = ChatModelFactory.Create(pool);
            static SententiamConverte.Core.Alignment.ImportFile Txt(string name, params string[] p) => new(name, System.Text.Encoding.UTF8.GetBytes(string.Join("\n\n", p)));
            var files = new[]
            {
                Txt("Mietvertrag_PL.txt",
                    "Najemca płaci czynsz miesięczny w wysokości 950 euro z góry do trzeciego dnia roboczego miesiąca.",
                    "Kaucja wynosi trzy miesięczne czynsze bez kosztów eksploatacyjnych.",
                    "Wynajmujący zwraca kaucję wraz z odsetkami po zakończeniu stosunku najmu.",
                    "Koszty eksploatacyjne rozlicza się raz w roku.",
                    "Umowa najmu może zostać wypowiedziana przez najemcę z zachowaniem trzymiesięcznego okresu wypowiedzenia."),
                Txt("Mietvertrag_DE.txt",
                    "Der Mieter zahlt die monatliche Miete von 950 Euro im Voraus bis zum dritten Werktag des Monats.",
                    "Die Kaution beträgt drei Monatsmieten ohne Betriebskosten.",
                    "Der Vermieter zahlt die Kaution nach Beendigung des Mietverhältnisses mit Zinsen zurück.",
                    "Über die Betriebskosten wird jährlich abgerechnet.",
                    "Der Mietvertrag kann vom Mieter mit einer Frist von drei Monaten gekündigt werden."),
            };
            var pair = Assert.Single(SententiamConverte.Core.Alignment.TranslatedFiles.Pair(files, "de-DE", ["pl-PL"]).Pairs);
            report.AppendLine($"{pair.Source.Name} → {pair.Target!.Name} ({pair.TargetLanguage}, {pair.How})");
            var session = SententiamConverte.Core.Alignment.TranslatedFiles.Align(pair);
            var (fixedRuns, doubtful) = await new SententiamConverte.Core.Alignment.AiAligner(model).AutoAsync(session, ct);
            report.AppendLine($"Paare: {session.Pairs.Count}, KI neu ausgerichtet: {fixedRuns}, zweifelhaft: {doubtful}");
            foreach (var p in session.Pairs) report.AppendLine($"  {(p.Accepted ? "✔" : "✖")} {p.AiScore:0.00} {p.SourceText} ↔ {p.TargetText}");
            var terms = await new SententiamConverte.Core.Alignment.TermSuggester(model).SuggestAsync(
                session.Accepted.Select(p => (p.SourceText, p.TargetText)).ToList(), "de-DE", "pl-PL", "Mietrecht", ct: ct);
            report.AppendLine($"Fachbegriffe ({terms.Count}):");
            foreach (var t in terms) report.AppendLine($"  {t.Source} → {t.Target} ({t.Count}×)");
            Assert.Equal(5, session.Accepted.Count());
            Assert.NotEmpty(terms);
        }
        finally { File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-altbestand.txt"), report.ToString()); }
    }

    /// <summary>KI-Prüfung mit dem kostenlosen Verbund: eine richtige und eine falsche Übersetzung (50 statt 5 Nm). Bericht in %TEMP%\sc-pruefung.txt.</summary>
    [Fact(Explicit = true)]
    public async Task TranslationCheckWithTheFreePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        try
        {
            var candidates = await AiDiscovery.FreeModelsAsync([], null, ct);
            var pool = new AiProviderConfig { Name = "Verbund", Kind = AiProviderKind.Pool, Members = candidates.Where(c => c.Recommended).Select(c => c.Config).ToList() };
            var model = (PooledChatModel)ChatModelFactory.Create(pool);
            model.Report = m => { lock (report) report.AppendLine("  " + m); };
            var checker = new SententiamConverte.Core.Agents.TranslationChecker(new SententiamConverte.Core.Agents.AgentContext { Model = model, ExplanationLanguage = "Deutsch" });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var good = await checker.CheckAsync("Ziehen Sie die Schraube mit 5 Nm an.", "Tighten the screw to 5 Nm.", "de-DE", "en-GB", "Montage", ct: ct);
            report.AppendLine($"RICHTIG ({watch.Elapsed.TotalSeconds:0} s):\n{good.Format()}\nAlternativen: {string.Join(" | ", good.Alternatives)}\n");
            watch.Restart();
            var bad = await checker.CheckAsync("Ziehen Sie die Schraube mit 5 Nm an.", "Tighten the screw to 50 Nm.", "de-DE", "en-GB", "Montage", ct: ct);
            report.AppendLine($"FALSCH ({watch.Elapsed.TotalSeconds:0} s):\n{bad.Format()}\nAlternativen: {string.Join(" | ", bad.Alternatives)}");
            Assert.False(good.Doubtful, report.ToString());
            Assert.True(bad.Doubtful, report.ToString());
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-pruefung.txt"), report.ToString());
        }
    }

    /// <summary>Amtliche Schnittstellen: DWDS, Bundesrecht, Sejm, legislation.gov.uk, EUR-Lex. Bericht in %TEMP%\sc-amtlich.txt.</summary>
    [Fact(Explicit = true)]
    public async Task OfficialSourcesAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        void Show(string what, IEnumerable<LawHit> hits)
        {
            report.AppendLine("== " + what);
            foreach (var h in hits) report.AppendLine($"  {h.Title} [{h.Citation}] {h.Url} {h.Note}");
        }
        try
        {
            var dwds = await Dictionaries.DwdsAsync("Bescheid", ct);
            report.AppendLine(dwds.Describe("Bescheid"));
            Assert.True(dwds.Found);
            Assert.Equal(3, dwds.FrequencyClass);

            var de = await LawSources.SearchAsync("de", "§ 35 VwVfG", ["de-DE"], 5, ct);
            Show("de § 35 VwVfG", de);
            Assert.Equal("https://www.gesetze-im-internet.de/vwvfg/__35.html", de[0].Url);
            Show("de Verwaltungsverfahrensgesetz", await LawSources.SearchAsync("de", "Verwaltungsverfahrensgesetz", ["de-DE"], 5, ct));
            Show("de SGB X", await LawSources.SearchAsync("de", "§ 31 SGB X", ["de-DE"], 3, ct));

            var pl = await LawSources.SearchAsync("pl", "Kodeks postępowania administracyjnego", ["pl-PL"], 5, ct);
            Show("pl KPA", pl);
            Assert.Contains("jednolity", pl[0].Note);

            var uk = await LawSources.SearchAsync("uk", "Tribunals, Courts and Enforcement", ["en-GB"], 3, ct);
            Show("uk", uk);
            Assert.NotEmpty(uk);

            var eu = await LawSources.SearchAsync("eu", "Verordnung (EU) 2016/679", ["de-DE", "pl-PL"], 3, ct);
            Show("eu 2016/679", eu);
            Assert.Equal("CELEX 32016R0679", eu[0].Citation);
            Show("eu Datenschutz-Grundverordnung", await LawSources.SearchAsync("eu", "Datenschutz-Grundverordnung", ["de-DE", "pl-PL"], 3, ct));

            var fetcher = new PageFetcher();
            var norm = await fetcher.FetchTextAsync(de[0].Url, 3_000_000, ct);
            report.AppendLine("== § 35 VwVfG: " + (norm == null ? fetcher.LastSkipReason : string.Join(" | ", SententiamConverte.Core.Agents.TextPassages.Find(norm, "Verwaltungsakt", 2))));
            Assert.NotNull(norm);
            var kpa = await fetcher.FetchTextAsync(pl[0].Url, 3_000_000, ct);
            report.AppendLine($"== KPA ({kpa?.Length ?? 0} Zeichen): " + (kpa == null ? fetcher.LastSkipReason : string.Join(" | ", SententiamConverte.Core.Agents.TextPassages.Find(kpa, "decyzja administracyjna", 2))));
            Assert.NotNull(kpa);
            var gdpr = await fetcher.FetchTextAsync(eu[0].Note!.Split("PL: ")[1].Trim(), 3_000_000, ct);
            report.AppendLine($"== DSGVO PL ({gdpr?.Length ?? 0} Zeichen): " + (gdpr == null ? fetcher.LastSkipReason : string.Join(" | ", SententiamConverte.Core.Agents.TextPassages.Find(gdpr, "administrator", 1))));
            Assert.NotNull(gdpr);
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-amtlich.txt"), report.ToString());
        }
    }

    /// <summary>
    /// Rechtsvergleich mit Übersetzerkommentar: „Verwaltungsakt“ ins Polnische, mit Kilo und – wenn SC_BUILTIN_FOLDER
    /// gesetzt ist – der eingebauten KI. Bericht in %TEMP%\sc-rechtsvergleich.txt.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ResearchAgentComparesLegalTerms()
    {
        var ct = TestContext.Current.CancellationToken;
        var report = new System.Text.StringBuilder();
        var kilo = AiDiscovery.FreeServices.Single(s => s.Id == "kilo");
        var configs = new List<AiProviderConfig>
        {
            new() { Name = "Kilo", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = kilo.BaseUrl, Model = "kilo-auto/free", Temperature = 0.2, MaxCharsPerPage = 3000 },
        };
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder)
        {
            BuiltInAi.Folder = folder;
            configs.Add(AiSettings.BuiltInPreset());
        }
        var ok = 0;
        try
        {
            foreach (var config in configs)
            {
                using var dir = new TempDir();
                var skills = new SententiamConverte.Core.Agents.SkillLibrary(dir.File("skills"));
                var log = new System.Text.StringBuilder();
                var ctx = new SententiamConverte.Core.Agents.AgentContext
                {
                    Model = ChatModelFactory.Create(config), Search = null, MaxCharsPerPage = config.MaxCharsPerPage,
                    Log = new SyncLog(log), ExplanationLanguage = "Deutsch",
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                report.AppendLine($"===== {config.Name}");
                try
                {
                    var result = await new SententiamConverte.Core.Agents.ResearchAgent(ctx, skills, null)
                        .RunAsync("Wie übersetze ich den deutschen Rechtsbegriff „Verwaltungsakt“ (§ 35 VwVfG) ins Polnische? " +
                                  "Vergleiche beide Rechtsordnungen und gib eine Anmerkung des Übersetzers.", "de-DE", ["pl-PL"], 12, false, false, ct);
                    report.AppendLine($"{watch.Elapsed.TotalSeconds:0} s, {result.Steps.Count} Schritte, fertig: {result.Finished}");
                    report.Append(log);
                    report.AppendLine("ANTWORT: " + result.Answer);
                    report.AppendLine("ÜBERSETZUNG: " + result.Translation);
                    report.AppendLine("ANMERKUNG: " + result.Note);
                    report.AppendLine("QUELLEN: " + string.Join(", ", result.Sources.Select(s => s.Url)));
                    if (result.Finished && (result.Answer + result.Translation).Contains("decyzj", StringComparison.OrdinalIgnoreCase)
                        && result.Steps.Any(s => s.Tool == "law")) ok++;
                }
                catch (AiException ex)
                {
                    report.Append(log);
                    report.AppendLine("FEHLER: " + ex.Message);
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-rechtsvergleich.txt"), report.ToString());
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
        Assert.True(ok > 0, report.ToString());
    }

    private sealed class SyncLog(System.Text.StringBuilder sb) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sb) sb.AppendLine("  " + value);
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

    /// <summary>
    /// Freie Sprachdaten über die offizielle OPUS-Schnittstelle: Liste für Deutsch → Polnisch holen, einen kleinen,
    /// beruflich nutzbaren Datensatz wirklich laden und als TM anlegen (lädt einige MB).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task LanguageDataDownloadsARealDataset()
    {
        var ct = TestContext.Current.CancellationToken;
        var sets = await Resources.LanguageData.SearchAsync("de-DE", "pl-PL", ct);
        var log = TestContext.Current.TestOutputHelper;
        log?.WriteLine($"{sets.Count} Datensätze, davon {sets.Count(x => x.Recommended)} empfohlen");
        Assert.True(sets.Count > 10);
        Assert.Contains(sets, x => x.Kind == Remote.ResourceKind.Termbase);                   // IATE
        Assert.All(sets, x => Assert.StartsWith("https://", x.Url));

        var small = sets.Where(x => x.Kind == Remote.ResourceKind.Tm && x.CommercialUse && x.Units is >= 500 and <= 200_000 && x.Size is > 0 and <= 20_000_000)
            .OrderBy(x => x.Size).First();
        log?.WriteLine($"Lade {small.Name}: {small.Units} Paare, {small.Size / 1024} KB, {small.License}");

        using var dir = new TempDir();
        var result = await Resources.LanguageData.InstallAsync(small, "de-DE", "pl-PL", dir.Path, ct: ct);
        log?.WriteLine($"{result.Count} Einträge in {Path.GetFileName(result.Path)}");
        Assert.True(result.Count > 100);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.gz"));                                   // Download wieder gelöscht

        using var tm = new Tm.TranslationMemory(result.Path);
        Assert.Equal(result.Count, tm.Count());
        var unit = tm.Browse(null, 0, 1).Single();
        log?.WriteLine($"{unit.Source}  →  {unit.Target}");
        Assert.False(string.IsNullOrWhiteSpace(unit.Target));
    }

    /// <summary>TERMIUM Plus echt laden: kleinstes Fachgebiet (Weltraumrobotik, etwa 70 KB) für Englisch → Französisch.</summary>
    [Fact(Explicit = true)]
    public async Task TermiumDownloadsARealSubject()
    {
        var ct = TestContext.Current.CancellationToken;
        var set = (await Resources.LanguageData.SearchAsync("en-GB", "fr-CA", ct)).Single(s => s.Name.EndsWith("Space Robotics"));
        using var dir = new TempDir();
        var result = await Resources.LanguageData.InstallAsync(set, "en-GB", "fr-CA", dir.Path, ct: ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Count} Einträge");
        Assert.True(result.Count > 50);
        using var tb = new Terminology.Termbase(result.Path);
        var hit = tb.Recognize("Research in adaptive robotics continues.", "en-GB", "fr-CA").Single();
        Assert.Contains(hit.Translations, t => t.Text == "robotique adaptative");
    }

    /// <summary>Öffentliche LanguageTool-Schnittstelle: eine Anfrage mit zwei französischen Sätzen.</summary>
    [Fact(Explicit = true)]
    public async Task LanguageToolPublicFindsFrenchAgreementErrors()
    {
        var issues = await Qa.GrammarChecker.CheckAsync([(1, "Tout va bien."), (2, "Les enfant sont arrivé hier.")], "fr-FR",
            new Qa.GrammarSettings { Engine = Qa.GrammarEngine.LanguageToolPublic }, ct: TestContext.Current.CancellationToken);
        foreach (var i in issues) TestContext.Current.TestOutputHelper?.WriteLine(i.ToQa().Message);
        Assert.All(issues, i => Assert.Equal(2, i.SegmentId));
        Assert.Contains(issues, i => i.Wrong.Contains("enfant"));
    }
}
