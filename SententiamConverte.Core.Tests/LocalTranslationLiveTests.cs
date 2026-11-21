using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Lokale Übersetzung mit echten Modellen über einen festen Testsatz (Testdaten/uebersetzungstest.json: 16 Sätze aus
/// Vertrag, Technik, Brief, Medizin, Redewendung, Software mit Referenzen in de/en/pl/fr). Laufen nur auf Anforderung.
/// Die Ausgaben gehen nach %TEMP%\sc-lokal-&lt;name&gt;.json, die Bewertung (chrF) geschieht außerhalb.
/// </summary>
[Collection("BuiltInAi")]
public sealed class LocalTranslationLiveTests : IDisposable
{
    private static readonly string[] Directions = ["de-en", "en-de", "de-fr", "de-pl"];
    private static readonly Dictionary<string, string> Locale = new() { ["de"] = "de-DE", ["en"] = "en-GB", ["fr"] = "fr-FR", ["pl"] = "pl-PL" };

    /// <summary>Sätze mit Formatierungs-Tags: die müssen erhalten bleiben.</summary>
    private static readonly string[] Tagged =
    [
        "Klicken Sie auf ⟨1⟩Speichern⟨/1⟩, um die Änderungen zu übernehmen.",
        "Die ⟨1⟩Gleitringdichtung⟨/1⟩ ist alle 500 Betriebsstunden⟨2/⟩ zu prüfen.",
        "Weitere Informationen finden Sie unter ⟨1⟩Abschnitt ⟨3⟩4.2⟨/3⟩⟨/1⟩.",
    ];

    private readonly TempDir _home = new();
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("SENTENTIAM_HOME");

    // Eigener Datenordner: gelernte Regeln des Nutzers sollen die Messung nicht beeinflussen
    public LocalTranslationLiveTests() => Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SENTENTIAM_HOME", _oldHome);
        _home.Dispose();
    }

    private static List<JsonObject> TestSet() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdaten", "uebersetzungstest.json")))!
            .AsArray().Select(n => n!.AsObject()).ToList();

    /// <summary>Testsatz in alle vier Richtungen übersetzen; Ausgaben, Zeiten und Auffälligkeiten speichern.</summary>
    private static async Task<JsonObject> RunAsync(Func<string, string, string, Task<string>> translate, string name, string[]? directions = null)
    {
        var data = TestSet();
        var result = new JsonObject();
        var log = TestContext.Current.TestOutputHelper;
        foreach (var direction in directions ?? Directions)
        {
            var (src, tgt) = (direction[..2], direction[3..]);
            var outputs = new JsonArray();
            var watch = Stopwatch.StartNew();
            foreach (var d in data)
                outputs.Add(await translate(d[src]!.GetValue<string>(), Locale[src], Locale[tgt]));
            var seconds = watch.Elapsed.TotalSeconds / data.Count;
            var texts = outputs.Select(o => o!.GetValue<string>()).ToList();
            var inflated = data.Zip(texts).Count(p => AiTranslator.Inflated(p.First[src]!.GetValue<string>(), p.Second));
            result[direction] = new JsonObject { ["seconds"] = Math.Round(seconds, 3), ["inflated"] = inflated, ["outputs"] = outputs };
            log?.WriteLine($"{name} {direction}: {seconds:0.00} s/Satz, auffällig lang {inflated}");
        }
        var tagged = new JsonArray();
        foreach (var source in Tagged)
        {
            var target = await translate(source, "de-DE", "en-GB");
            tagged.Add(target);
            log?.WriteLine($"{name} Tags: {target}");
            Assert.Equal(TagText.Tags(source).Select(t => (t.Kind, t.Id)).Order(), TagText.Tags(target).Select(t => (t.Kind, t.Id)).Order());
        }
        result["tagged"] = tagged;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"sc-lokal-{name}.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    /// <summary>
    /// OPUS-MT-Zerlegung in Wortteile gegenüber dem Original (SC_OPUSMT_FOLDER: Ordner mit je einem Unterordner pro
    /// Sprachpaar; SC_OPUSMT_PIECES: JSON {paar: [[wortteile …] …]} aus der Referenz-Umsetzung).
    /// </summary>
    [Fact(Explicit = true)]
    public void OpusMtSplitsTextLikeTheOriginal()
    {
        var folder = Environment.GetEnvironmentVariable("SC_OPUSMT_FOLDER")!;
        var reference = JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SC_OPUSMT_PIECES")!))!.AsObject();
        var extra = new[] { "Größe: 5 µm – „Test“ … ½ Ⅻ ﬁ", "  Doppelte  Leerzeichen\tund Tab", "Klicken Sie auf <g id=\"1\">Speichern</g> &amp; [NAME_1]." };
        foreach (var (pair, lists) in reference)
        {
            var dir = Path.Combine(folder, pair);
            var vocab = new Ai.LocalMt.MarianVocabulary(dir, Ai.LocalMt.MarianConfig.Parse(File.ReadAllText(Path.Combine(dir, "config.json"))));
            var texts = TestSet().Select(d => d[pair[..2]]!.GetValue<string>()).Concat(extra).ToList();
            for (var i = 0; i < texts.Count; i++)
                Assert.Equal(lists![i]!.AsArray().Select(p => p!.GetValue<string>()), vocab.Pieces(texts[i]));
        }
    }

    /// <summary>
    /// OPUS-MT-Netz gegenüber dem Original: Ausgaben des Testsatzes neben die Referenz legen (SC_OPUSMT_REFERENCE:
    /// JSON {paar: {outputs: […]}}); mindestens 14 von 16 Sätzen müssen wörtlich gleich sein.
    /// </summary>
    [Fact(Explicit = true)]
    public void OpusMtTranslatesLikeTheOriginal()
    {
        var folder = Environment.GetEnvironmentVariable("SC_OPUSMT_FOLDER")!;
        var reference = JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SC_OPUSMT_REFERENCE")!))!.AsObject();
        var log = TestContext.Current.TestOutputHelper;
        var result = new JsonObject();
        foreach (var pair in new[] { "de-en", "en-de", "de-fr", "de-pl" })
        {
            var dir = Path.Combine(folder, pair);
            var watch = Stopwatch.StartNew();
            var model = Ai.LocalMt.MarianModel.Load(dir);
            var vocab = new Ai.LocalMt.MarianVocabulary(dir, model.Config);
            var load = watch.Elapsed.TotalSeconds;
            var expected = reference[pair]!["outputs"]!.AsArray().Select(o => o!.GetValue<string>()).ToList();
            var outputs = new JsonArray();
            var same = 0;
            watch.Restart();
            foreach (var (d, i) in TestSet().Select((d, i) => (d, i)))
            {
                var text = vocab.Decode(model.Generate(vocab.Encode(d[pair[..2]]!.GetValue<string>())));
                outputs.Add(text);
                if (text == expected[i]) same++;
                else log?.WriteLine($"{pair} {i}: {text}\n     Original: {expected[i]}");
            }
            var seconds = watch.Elapsed.TotalSeconds / expected.Count;
            log?.WriteLine($"{pair}: {same}/{expected.Count} gleich, {seconds:0.00} s/Satz, Laden {load:0.0} s");
            result[pair] = new JsonObject { ["seconds"] = Math.Round(seconds, 3), ["load"] = Math.Round(load, 2), ["same"] = same, ["outputs"] = outputs };
            Assert.True(same >= 14, $"{pair}: nur {same} von {expected.Count} Sätzen wie im Original");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-lokal-opusmt-kern.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Lokale Übersetzung wie im Programm: Modelle echt von Hugging Face laden und prüfen (SC_OPUSMT_APPFOLDER), dann über
    /// den Übersetzungsagenten mit dem Dienst „Offline auf diesem Rechner“ übersetzen – auch Englisch → Polnisch
    /// (Gruppenmodell) und Deutsch → Portugiesisch (über Englisch).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task OpusMtTranslatesTheTestSetInTheApp()
    {
        Ai.LocalMt.LocalTranslation.Folder = Environment.GetEnvironmentVariable("SC_OPUSMT_APPFOLDER")!;
        var ct = TestContext.Current.CancellationToken;
        var log = TestContext.Current.TestOutputHelper;
        try
        {
            string[] directions = ["de-en", "en-de", "de-fr", "de-pl", "en-pl"];
            foreach (var route in directions.Append("de-pt").Select(d => Ai.LocalMt.LocalTranslation.Route(Locale.GetValueOrDefault(d[..2], d[..2]), Locale.GetValueOrDefault(d[3..], "pt-PT"))!))
            {
                var watch = Stopwatch.StartNew();
                var missing = route.MissingBytes;
                await Ai.LocalMt.LocalTranslation.InstallAsync(route, null, ct);
                Assert.True(route.IsInstalled);
                log?.WriteLine($"{string.Join(" + ", route.Models.Select(m => m.Id))}: {missing / 1e6:0} MB in {watch.Elapsed.TotalSeconds:0} s geladen");
            }
            var mt = new MachineTranslationModel(new AiProviderConfig { Name = "Offline", Kind = AiProviderKind.MachineTranslation, Model = "lokal" });
            var agent = new AiTranslator(new AgentContext { Model = mt });
            await RunAsync((text, from, to) => agent.TranslateAsync(text, from, to, null, ct: ct), "opusmt", directions);
            var pt = await agent.TranslateAsync("Klicken Sie auf „Weiter“, um die Installation fortzusetzen.", "de-DE", "pt-PT", null, ct: ct);
            log?.WriteLine("de-pt über Englisch: " + pt);
            Assert.Contains("instala", pt, StringComparison.OrdinalIgnoreCase);
        }
        finally { Ai.LocalMt.LocalTranslation.Folder = ""; }
    }

    /// <summary>PDF eines Projekts neu setzen (SC_PDF_PROJECT: Projektordner, nur gelesen; SC_PDF_OUT: Ausgabedatei).</summary>
    [Fact(Explicit = true)]
    public void RewritesTheProjectPdf()
    {
        using var project = Projects.TranslationProject.Open(Environment.GetEnvironmentVariable("SC_PDF_PROJECT")!);
        var (file, language) = project.Documents().First(d => d.File.OriginalName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var doc = project.LoadDocument(file, language);
        using var output = File.Create(Environment.GetEnvironmentVariable("SC_PDF_OUT")!);
        project.ExportTarget(file, doc, output);                                        // wie das Programm beim Speichern der Zieldatei
    }

    /// <summary>
    /// Namen mit der eingebauten KI einordnen (SC_BUILTIN_FOLDER, SC_BUILTIN_MODEL): Namen aus SC_NAMES_FILE (je Zeile einer),
    /// Sätze aus dem Projekt in SC_NAMES_PROJECT (nur gelesen). Ergebnis nach %TEMP%\sc-namen.txt – nichts verlässt den Rechner.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task BuiltInAiClassifiesNames()
    {
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder) BuiltInAi.Folder = folder;
        var model = BuiltInAi.Model(Environment.GetEnvironmentVariable("SC_BUILTIN_MODEL") ?? "gemma-4-e4b")!;
        var names = File.ReadAllLines(Environment.GetEnvironmentVariable("SC_NAMES_FILE")!).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        var texts = new List<string>();
        if (Environment.GetEnvironmentVariable("SC_NAMES_PROJECT") is { Length: > 0 } path)
        {
            using var project = Projects.TranslationProject.Open(path);
            texts.AddRange(project.Documents().SelectMany(d => project.LoadDocument(d.File, d.Language).Segments).Select(s => s.Source).Distinct());
        }
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await Privacy.PrivacyScanner.ClassifyNamesAsync(ChatModelFactory.CreateRaw(AiSettings.BuiltInPreset(model)), names, texts,
                ct: TestContext.Current.CancellationToken);
            var report = string.Join("\n", result.Select(r => $"{(r.Protect ? "schützen" : "frei    ")} | {r.Kind,-11} | {r.Name} – {r.Reason}"));
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-namen.txt"), report + $"\n({watch.Elapsed.TotalSeconds:0} s, {model.Name})");
            Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), result.Count);
        }
        finally
        {
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
    }

    /// <summary>
    /// Testsatz recherchieren wie im Editor (SC_RESEARCH_MODEL, Vorgabe gemma-4-e4b; Suche Wikipedia wie bei der
    /// Vorgabe): je Richtung die Ausdrücke finden, die Recherche brauchen, und zu jedem ein Dossier erstellen. Ergebnis
    /// nach %TEMP%\sc-lokal-recherche.json; der Übersetzungstest nimmt es mit SC_RESEARCH_FILE mit, so bekommen alle
    /// Modelle dieselben Dossiers.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task BuiltInAiResearchesTheTestSet()
    {
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder) BuiltInAi.Folder = folder;
        var model = BuiltInAi.Model(Environment.GetEnvironmentVariable("SC_RESEARCH_MODEL") ?? "gemma-4-e4b")!;
        Assert.True(BuiltInAi.IsInstalled(model), "Laufzeit und Modell fehlen in " + BuiltInAi.Folder);
        var ct = TestContext.Current.CancellationToken;
        var log = TestContext.Current.TestOutputHelper;
        var settings = new AiSettings { Providers = [AiSettings.BuiltInPreset(model)], Search = new SearchConfig { Kind = SearchProviderKind.Wikipedia } };
        var research = new DocumentResearch(AgentContext.From(settings, settings.Providers[0].Id));
        TermComparisonAgent.WikipediaLinks = Environment.GetEnvironmentVariable("SC_WIKI_LINKS") != "0";
        var data = TestSet();
        var result = new JsonObject { ["model"] = model.Id };
        try
        {
            foreach (var direction in Directions)
            {
                var (src, tgt) = (Locale[direction[..2]], Locale[direction[3..]]);
                var doc = new BilingualDocument { FileName = "test.txt", SourceLanguage = src, TargetLanguage = tgt };
                foreach (var d in data)
                {
                    var paragraph = new Paragraph { Locator = "p" + doc.Paragraphs.Count };
                    new Segmentation.Segmenter(src).Segment(paragraph, d[direction[..2]]!.GetValue<string>());
                    doc.Paragraphs.Add(paragraph);
                }
                var watch = Stopwatch.StartNew();
                var items = await research.FindAsync(doc, src, tgt, null, null, max: 10, ct: ct);
                var dossiers = new List<TermDossier>();
                var failed = new List<string>();
                foreach (var item in items)
                    try { dossiers.Add(await research.ResearchAsync(item, src, tgt, null, ct: ct)); }
                    catch (AiException ex) { failed.Add($"{item.Expression}: {ex.Message}"); }
                Assert.True(items.Count == 0 || dossiers.Count > 0, $"{direction}: keine Recherche gelungen – " + string.Join("; ", failed));
                result[direction + "-fehler"] = new JsonArray(failed.Select(f => (JsonNode)f).ToArray());
                log?.WriteLine($"{direction}: {items.Count} Ausdrücke, {dossiers.Count(d => !string.IsNullOrWhiteSpace(d.Recommended))} mit Empfehlung, {watch.Elapsed.TotalMinutes:0.0} min");
                result[direction] = JsonSerializer.SerializeToNode(dossiers);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sc-lokal-recherche.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
    }

    /// <summary>Dossiers aus <see cref="BuiltInAiResearchesTheTestSet"/> je Richtung („de-en“ …), falls SC_RESEARCH_FILE gesetzt ist.</summary>
    private static Dictionary<string, List<TermDossier>>? ResearchFile()
    {
        if (Environment.GetEnvironmentVariable("SC_RESEARCH_FILE") is not { Length: > 0 } path) return null;
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return Directions.Where(d => json[d] != null).ToDictionary(d => d, d => json[d].Deserialize<List<TermDossier>>()!);
    }

    /// <summary>
    /// Eingebaute KI (SC_BUILTIN_FOLDER: Ordner mit Laufzeit und Modellen, SC_BUILTIN_MODEL: z. B. gemma-4-e4b) über den
    /// Übersetzungsagenten. Sätze ohne Tags dürfen keine erfundenen Tags oder Formelreste bekommen. Mit SC_RESEARCH_FILE
    /// bekommt jeder Satz die Dossiers zu seinen Ausdrücken mit (wie im Editor); die Ausgabe heißt dann „…-recherche“.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task BuiltInAiTranslatesTheTestSet()
    {
        if (Environment.GetEnvironmentVariable("SC_BUILTIN_FOLDER") is { Length: > 0 } folder) BuiltInAi.Folder = folder;
        var model = BuiltInAi.Model(Environment.GetEnvironmentVariable("SC_BUILTIN_MODEL") ?? "gemma-4-e4b")!;
        Assert.True(BuiltInAi.IsInstalled(model), "Laufzeit und Modell fehlen in " + BuiltInAi.Folder);
        var ct = TestContext.Current.CancellationToken;
        var dossiers = ResearchFile();
        try
        {
            var agent = new AiTranslator(new AgentContext { Model = ChatModelFactory.Create(AiSettings.BuiltInPreset(model)) });
            var result = await RunAsync((text, from, to) => agent.TranslateAsync(text, from, to, null, ct: ct,
                    research: dossiers?.GetValueOrDefault(from[..2] + "-" + to[..2]) is { } all ? DossierStore.ForText(all, text, to) : null),
                model.Id + (dossiers != null ? "-recherche" : ""));
            foreach (var direction in Directions)
                Assert.DoesNotContain(result[direction]!["outputs"]!.AsArray(), o => o!.GetValue<string>().Contains("langle") || o.GetValue<string>().Contains('⟨'));
        }
        finally
        {
            BuiltInAi.Stop();
            BuiltInAi.Folder = "";
        }
    }
}
