using System.Diagnostics;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Messungen mit großen Datenmengen. Nur auf Anforderung: dotnet test -- --explicit only</summary>
public class PerformanceTests
{
    private static readonly string[] Words =
    [
        "Pumpe", "Ventil", "Schraube", "Gehäuse", "Dichtung", "Welle", "Motor", "Lager", "Filter", "Leitung", "Druck",
        "Temperatur", "Betrieb", "Wartung", "Prüfung", "Anschluss", "Kabel", "Schalter", "Sensor", "Steuerung", "Gerät",
        "prüfen", "reinigen", "ersetzen", "montieren", "lösen", "anziehen", "einschalten", "ausschalten", "messen",
        "regelmäßig", "sofort", "vorsichtig", "vollständig", "neue", "alte", "obere", "untere", "linke", "rechte",
    ];

    // Realistischer Wortschatz: wenige sehr häufige Wörter, viele seltene (Zipf-Verteilung)
    private static readonly string[] Vocabulary = Words.Concat(Enumerable.Range(0, 20000).Select(i =>
        new string(Enumerable.Range(0, 4 + i % 7).Select(k => (char)('a' + (i * 7 + k * 13) % 26)).ToArray()) + i)).ToArray();

    private static string Word(Random rnd) =>
        // wie echte Fachtexte: gut ein Drittel häufige Wörter, der Rest aus einem großen Fachwortschatz
        rnd.NextDouble() < 0.35 ? Vocabulary[rnd.Next(Words.Length)] : Vocabulary[Words.Length + rnd.Next(Vocabulary.Length - Words.Length)];

    private static string Sentence(Random rnd, int n) =>
        "Die " + string.Join(' ', Enumerable.Range(0, n).Select(_ => Word(rnd))) + " " + rnd.Next(1, 500) + ".";

    private static void Log(string s)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(s);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "sc-leistung.txt"), s + Environment.NewLine);
    }

    /// <summary>Zerlegt die Zeit einer Suche in ihre Schritte (Wortstatistik, Volltextsuche, Bewertung).</summary>
    [Fact(Explicit = true)]
    public void SearchBreakdown()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-gross2.sctm");
        var rnd = new Random(42);
        var tm = new TranslationMemory(path);
        if (tm.Count() < 200_000)
            tm.AddMany(Enumerable.Range(0, 200_000).Select(i => { var s = Sentence(rnd, rnd.Next(5, 18)); return new TmEntry("de-DE", "en-GB", s, "EN " + s); }).ToList());
        var queries = Enumerable.Range(0, 50).Select(_ => Sentence(rnd, rnd.Next(5, 18))).ToList();
        foreach (var (name, ms) in tm.ProfileSearch(queries)) Log($"{name}: {ms:0.0} ms je Suche");
        tm.Dispose();
    }

    /// <summary>Analyse eines Dokuments mit 3 000 Segmenten gegen das große TM aus <see cref="SearchBreakdown"/>.</summary>
    [Fact(Explicit = true)]
    public void AnalysisSpeed()
    {
        var path = Path.Combine(Path.GetTempPath(), "sc-gross2.sctm");
        var rnd = new Random(7);
        using var tm = new TranslationMemory(path);
        var doc = new BilingualDocument { SourceLanguage = "de-DE", TargetLanguage = "en-GB", FilterId = "txt" };
        var para = new Model.Paragraph();
        para.Segments = Enumerable.Range(0, 3000).Select(i => new Segment { Id = i + 1, Source = Sentence(rnd, rnd.Next(5, 18)), TrailingWhitespace = " " }).ToList();
        doc.Paragraphs.Add(para);
        var sw = Stopwatch.StartNew();
        Analyzer.Analyze([doc], [tm]);
        Log($"Analyse 3000 Segmente: {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact(Explicit = true)]
    public void LargeTmAndDocument()
    {
        File.Delete(Path.Combine(Path.GetTempPath(), "sc-leistung.txt"));
        using var dir = new TempDir();
        var rnd = new Random(42);
        var sw = Stopwatch.StartNew();

        using var project = TranslationProject.Create(dir.File("p"), "Leistung", "de-DE", ["en-GB"]);
        var units = Enumerable.Range(0, 200_000).Select(i =>
        {
            var s = Sentence(rnd, rnd.Next(5, 18));
            return new TmEntry("de-DE", "en-GB", s, "EN " + s, null, "test", null, i % 3 == 0 ? "Vorheriger Satz " + i : null);
        }).ToList();
        sw.Restart();
        project.ProjectTm.AddMany(units);
        Log($"TM: 200 000 Einheiten eingefügt in {sw.Elapsed.TotalSeconds:0.0} s");

        sw.Restart();
        var searches = Enumerable.Range(0, 200).Select(_ => Sentence(rnd, rnd.Next(5, 18))).ToList();
        foreach (var s in searches) project.ProjectTm.Search("de-DE", "en-GB", s, 70, 10, new TmContext("x", null, null));
        Log($"Fuzzy-Suche: {sw.Elapsed.TotalMilliseconds / searches.Count:0.0} ms je Segment");

        sw.Restart();
        foreach (var u in units.Take(200)) project.ProjectTm.Search("de-DE", "en-GB", u.Source, 70, 10, new TmContext(u.Previous, null, null));
        Log($"Exakte Suche mit Kontext: {sw.Elapsed.TotalMilliseconds / 200:0.0} ms je Segment");

        sw.Restart();
        foreach (var s in searches.Take(100)) project.ProjectTm.Fragments("de-DE", "en-GB", s);
        Log($"Fragment-Treffer: {sw.Elapsed.TotalMilliseconds / 100:0.0} ms je Segment");

        sw.Restart();
        for (var i = 0; i < 50; i++) project.ProjectTm.Concordance("de-DE", "en-GB", Words[i % Words.Length] + " " + Words[(i + 3) % Words.Length]);
        Log($"Konkordanz: {sw.Elapsed.TotalMilliseconds / 50:0.0} ms je Suche");

        // Dokument mit 3 000 Segmenten, ein Drittel davon im TM
        var lines = Enumerable.Range(0, 3000).Select(i => i % 3 == 0 ? units[i * 7].Source : Sentence(rnd, rnd.Next(5, 18)));
        var file = project.AddFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))), "gross.txt");
        sw.Restart();
        var doc = project.LoadDocument(file, "en-GB");
        Log($"Dokument laden ({doc.Segments.Count()} Segmente): {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var (applied, _) = Workflow.Pretranslate(project, doc);
        Log($"Vorübersetzung: {sw.Elapsed.TotalSeconds:0.0} s, {applied} eingesetzt");

        sw.Restart();
        project.SaveDocument(file, doc);
        Log($"Dokument speichern: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        Analyzer.Analyze([doc], project.TmsFor("en-GB"));
        Log($"Analyse: {sw.Elapsed.TotalSeconds:0.0} s");

        sw.Restart();
        var segs = doc.Segments.ToList();
        foreach (var s in segs.Take(100)) { if (s.Target.Length == 0) s.Target = "x " + s.Source; Workflow.Confirm(project, doc, s); }
        Log($"Bestätigen: {sw.Elapsed.TotalMilliseconds / 100:0.0} ms je Segment");
    }
}
