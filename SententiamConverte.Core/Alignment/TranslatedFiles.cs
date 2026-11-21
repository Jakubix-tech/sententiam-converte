using System.Text.RegularExpressions;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Alignment;

/// <summary>Eine gewählte Datei (Name und Inhalt).</summary>
public sealed record ImportFile(string Name, byte[] Data);

/// <summary>Original und Übersetzung – oder eine zweisprachige Datei (XLIFF), die beides schon enthält.</summary>
public sealed class TranslatedFilePair
{
    public required ImportFile Source { get; set; }
    public ImportFile? Target { get; set; }
    public string SourceLanguage { get; set; } = "";
    public string TargetLanguage { get; set; } = "";

    /// <summary>Wie die Zuordnung zustande kam (zur Anzeige).</summary>
    public string How { get; set; } = "";

    public bool IsBilingual => Target == null;

    public void Swap()
    {
        if (Target == null) return;
        (Source, Target) = (Target, Source);
        (SourceLanguage, TargetLanguage) = (TargetLanguage, SourceLanguage);
    }
}

/// <summary>Ergebnis der Zuordnung: Paare und Dateien ohne Gegenstück.</summary>
public sealed record FilePairing(List<TranslatedFilePair> Pairs, List<ImportFile> Unpaired);

/// <summary>
/// Alte Übersetzungen übernehmen: ordnet gewählte Dateien einander zu (über den Dateinamen – „Vertrag_DE.docx“ /
/// „Vertrag_EN.docx“ –, die erkannte Sprache und gemeinsame Zahlen), liest sie satzweise, richtet die Sätze aus und
/// schreibt die sicheren Paare ins TM. Zweisprachige Dateien (XLIFF) gehen ohne Ausrichtung direkt.
/// </summary>
public static partial class TranslatedFiles
{
    private static readonly HashSet<string> BilingualExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xlf", ".xliff", ".sdlxliff", ".mqxliff" };

    public static bool IsBilingual(string fileName) => BilingualExtensions.Contains(Path.GetExtension(fileName));

    // Sprachangaben in Dateinamen → Hauptsprache
    private static readonly Dictionary<string, string> NameMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "de", ["deu"] = "de", ["ger"] = "de", ["deutsch"] = "de", ["german"] = "de", ["dt"] = "de",
        ["en"] = "en", ["eng"] = "en", ["englisch"] = "en", ["english"] = "en", ["gb"] = "en", ["us"] = "en", ["uk"] = "en",
        ["pl"] = "pl", ["pol"] = "pl", ["polnisch"] = "pl", ["polish"] = "pl", ["polski"] = "pl",
        ["fr"] = "fr", ["fra"] = "fr", ["fre"] = "fr", ["französisch"] = "fr", ["french"] = "fr", ["francais"] = "fr", ["français"] = "fr",
        ["it"] = "it", ["ita"] = "it", ["italienisch"] = "it", ["italian"] = "it", ["italiano"] = "it",
        ["es"] = "es", ["spa"] = "es", ["esp"] = "es", ["spanisch"] = "es", ["spanish"] = "es", ["espanol"] = "es", ["español"] = "es",
        ["pt"] = "pt", ["por"] = "pt", ["portugiesisch"] = "pt", ["portuguese"] = "pt",
        ["nl"] = "nl", ["nld"] = "nl", ["dut"] = "nl", ["niederländisch"] = "nl", ["dutch"] = "nl",
        ["cs"] = "cs", ["cz"] = "cs", ["ces"] = "cs", ["tschechisch"] = "cs", ["czech"] = "cs",
        ["sk"] = "sk", ["slk"] = "sk", ["slowakisch"] = "sk", ["slovak"] = "sk",
        ["hu"] = "hu", ["hun"] = "hu", ["ungarisch"] = "hu", ["hungarian"] = "hu",
        ["ro"] = "ro", ["ron"] = "ro", ["rum"] = "ro", ["rumänisch"] = "ro", ["romanian"] = "ro",
        ["ua"] = "uk", ["ukr"] = "uk", ["ukrainisch"] = "uk", ["ukrainian"] = "uk",
        ["ru"] = "ru", ["rus"] = "ru", ["russisch"] = "ru", ["russian"] = "ru",
        ["sv"] = "sv", ["swe"] = "sv", ["schwedisch"] = "sv", ["swedish"] = "sv",
        ["da"] = "da", ["dk"] = "da", ["dan"] = "da", ["dänisch"] = "da", ["danish"] = "da",
        ["nb"] = "nb", ["nor"] = "nb", ["norwegisch"] = "nb", ["norwegian"] = "nb",
        ["fi"] = "fi", ["fin"] = "fi", ["finnisch"] = "fi", ["finnish"] = "fi",
        ["tr"] = "tr", ["tur"] = "tr", ["türkisch"] = "tr", ["turkish"] = "tr",
        ["el"] = "el", ["gr"] = "el", ["ell"] = "el", ["griechisch"] = "el", ["greek"] = "el",
        ["zh"] = "zh", ["cn"] = "zh", ["chinesisch"] = "zh", ["chinese"] = "zh",
        ["ja"] = "ja", ["jp"] = "ja", ["japanisch"] = "ja", ["japanese"] = "ja",
        ["ko"] = "ko", ["kr"] = "ko", ["koreanisch"] = "ko", ["korean"] = "ko",
        ["ar"] = "ar", ["arabisch"] = "ar", ["arabic"] = "ar",
        ["la"] = "la", ["lat"] = "la", ["latein"] = "la", ["latin"] = "la",
    };

    [GeneratedRegex(@"[\s_.\-()\[\]]+")]
    private static partial Regex NameSeparators();

    /// <summary>„Vertrag_DE-de.docx“ → („vertrag“, „de“): Name ohne Sprachangabe und die Hauptsprache, falls erkennbar.</summary>
    public static (string Stem, string? Language) SplitName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var codes = Regex.Matches(stem, @"(?<![A-Za-z])([a-z]{2})[-_]([A-Z]{2})(?![A-Za-z])", RegexOptions.IgnoreCase);
        string? language = null;
        if (codes.Count > 0 && codes[^1].Groups[1].Value.ToLowerInvariant() is var iso && Languages.Common.Any(l => Languages.Primary(l.Code) == iso))
        {
            language = iso;                                       // „uk-UA“ ist Ukrainisch, „en-GB“ Englisch
            stem = stem.Remove(codes[^1].Index, codes[^1].Length);
        }
        var tokens = NameSeparators().Split(stem).Where(t => t.Length > 0).ToList();
        if (language == null)
            // die Sprachangabe steht meist am Ende oder am Anfang („EN_Vertrag“), nie als einziges Wort
            foreach (var i in new[] { tokens.Count - 1, 0 }.Distinct())
                if (tokens.Count > 1 && i >= 0 && NameMarkers.TryGetValue(tokens[i], out var l))
                {
                    language = l;
                    tokens.RemoveAt(i);
                    break;
                }
        return (string.Join("_", tokens).ToLowerInvariant(), language);
    }

    // häufige, kennzeichnende Wörter je Sprache (für die Spracherkennung am Inhalt)
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["de"] = Words("der die das und ist nicht mit den von zu für auf im ein eine dem des sich wird werden oder auch bei"),
        ["en"] = Words("the and of to is in that for with are be this on by from as an it or not which shall"),
        ["pl"] = Words("i w na się nie z do że jest to od po przez dla jak oraz są lub jego może"),
        ["fr"] = Words("le la les et des est une du que pour dans sur au par pas qui avec ce sont"),
        ["it"] = Words("il di che è la le per un una del della con non sono gli nel alla dei"),
        ["es"] = Words("el la de que y en los las del se por con una para es al lo como"),
        ["pt"] = Words("o a de que e do da em um uma para com não os as dos no na"),
        ["nl"] = Words("de het een en van is dat op te in voor met niet zijn aan door"),
        ["cs"] = Words("a je se na v že to s z do jako pro by jsou ve které nebo"),
        ["sk"] = Words("a je sa na v že to s z do ako pre by sú vo ktoré alebo"),
        ["hu"] = Words("a az és hogy nem is egy van meg csak de ez el mint"),
        ["ro"] = Words("și de la în cu pe nu este că o un din care pentru se"),
        ["sv"] = Words("och att det är som en på för med av till inte har den"),
        ["da"] = Words("og at det er som en på for med af til ikke har den"),
        ["nb"] = Words("og at det er som en på for med av til ikke har den"),
        ["fi"] = Words("ja on ei se että oli hän mutta kun tai joka myös ovat"),
        ["tr"] = Words("ve bir bu da de için ile çok olarak daha gibi ne olan"),
        ["la"] = Words("et in est non ad cum quod ut sed qui quae per"),
    };

    private static HashSet<string> Words(string list) => new(list.Split(' '), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordRegex();

    /// <summary>Hauptsprache eines Textes („de“, „en“ …) – nach häufigen Wörtern bzw. der Schrift; null = nicht erkennbar.</summary>
    public static string? DetectLanguage(string text)
    {
        if (text.Length > 20000) text = text[..20000];
        var letters = text.Count(char.IsLetter);
        if (letters < 20) return null;
        int Count(Func<char, bool> f) => text.Count(f);
        var scripts = new (string Lang, int Count)[]
        {
            ("ja", Count(c => c is >= '぀' and <= 'ヿ')),
            ("ko", Count(c => c is >= '가' and <= '힯')),
            ("zh", Count(c => c is >= '一' and <= '鿿')),
            ("ar", Count(c => c is >= '؀' and <= 'ۿ')),
            ("el", Count(c => c is >= 'Ͱ' and <= 'Ͽ')),
            ("ru", Count(c => c is >= 'Ѐ' and <= 'ӿ')),
        };
        var script = scripts.MaxBy(s => s.Count);
        if (script.Count > letters * 0.3)
        {
            if (script.Lang == "zh" && scripts[0].Count > letters * 0.05) return "ja";       // Kanji mit Kana
            if (script.Lang == "ru" && text.Any(c => c is 'і' or 'ї' or 'є' or 'ґ')) return "uk";
            return script.Lang;
        }
        var words = WordRegex().Matches(text).Select(m => m.Value).ToList();
        if (words.Count < 5) return null;
        var best = StopWords.Select(kv => (kv.Key, Hits: words.Count(w => kv.Value.Contains(w)))).MaxBy(x => x.Hits);
        return best.Hits >= Math.Max(2, words.Count / 50) ? best.Key : null;
    }

    /// <summary>Vollständiger Sprachcode: bevorzugt der passende des Projekts („en“ → „en-GB“), sonst der übliche.</summary>
    public static string FullCode(string primary, IEnumerable<string> preferred) =>
        preferred.FirstOrDefault(p => Languages.Primary(p) == primary)
        ?? Languages.Common.FirstOrDefault(l => Languages.Primary(l.Code) == primary)?.Code
        ?? primary;

    /// <summary>Sätze einer einsprachigen Datei; die Formatierung steht als Tags darin (⟨1⟩…⟨/1⟩).</summary>
    public static List<string> Sentences(ImportFile file, string language)
    {
        var filter = FileFilters.ForFile(file.Name) ?? throw new NotSupportedException($"{file.Name}: Dateityp wird nicht unterstützt.");
        using var ms = new MemoryStream(file.Data);
        var doc = filter.Read(ms, file.Name, language, language, new Segmenter(language));
        return doc.Segments.Where(x => !x.Locked).Select(x => x.Source).ToList();
    }

    private static string? Text(ImportFile file)
    {
        try { return string.Join(" ", Sentences(file, "de-DE").Take(400).Select(TagText.StripTags)); }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException or System.Xml.XmlException) { return null; }
    }

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Ordnet die Dateien einander zu. <paramref name="sourceLanguage"/>/<paramref name="targetLanguages"/>: die
    /// Sprachen des Projekts (bzw. die gewählten) – sie bestimmen, was Original und was Übersetzung ist.
    /// </summary>
    public static FilePairing Pair(IReadOnlyList<ImportFile> files, string sourceLanguage, IReadOnlyList<string> targetLanguages)
    {
        var pairs = new List<TranslatedFilePair>();
        var preferred = new[] { sourceLanguage }.Concat(targetLanguages).ToList();
        var srcPrimary = Languages.Primary(sourceLanguage);
        var defaultTarget = targetLanguages.FirstOrDefault() ?? "en-GB";

        // zweisprachige Dateien enthalten Original und Übersetzung schon
        foreach (var f in files.Where(f => IsBilingual(f.Name)))
        {
            var (_, lang) = SplitName(f.Name);
            pairs.Add(new TranslatedFilePair
            {
                Source = f, SourceLanguage = sourceLanguage,
                TargetLanguage = lang != null && lang != srcPrimary ? FullCode(lang, preferred) : defaultTarget,
                How = "zweisprachige Datei",
            });
        }

        // einsprachige: Sprache aus dem Namen, sonst aus dem Inhalt
        var mono = files.Where(f => !IsBilingual(f.Name)).Select(f =>
        {
            var (stem, lang) = SplitName(f.Name);
            var text = Text(f);
            var detected = lang ?? (text != null ? DetectLanguage(text) : null);
            var numbers = text == null ? new HashSet<string>() : NumberRegex().Matches(text).Select(m => m.Value).ToHashSet();
            return new Candidate(f, stem, detected, lang != null, text?.Length ?? 0, numbers);
        }).ToList();

        void Add(Candidate s, Candidate t, string how)
        {
            pairs.Add(new TranslatedFilePair
            {
                Source = s.File, Target = t.File, SourceLanguage = sourceLanguage,
                TargetLanguage = t.Language != null && t.Language != srcPrimary ? FullCode(t.Language, preferred) : defaultTarget,
                How = how,
            });
            mono.Remove(s);
            mono.Remove(t);
        }
        bool IsSource(Candidate c) => c.Language == srcPrimary;
        bool IsTarget(Candidate c) => c.Language != null && c.Language != srcPrimary;

        // 1. gleicher Name ohne Sprachangabe: „Vertrag_DE“ ↔ „Vertrag_EN“ (auch mehrere Übersetzungen eines Originals)
        foreach (var group in mono.GroupBy(c => c.Stem).Where(g => g.Count() > 1).ToList())
        {
            var source = group.FirstOrDefault(IsSource);
            if (source == null) continue;
            foreach (var t in group.Where(c => c != source && IsTarget(c)).ToList())
                pairs.Add(new TranslatedFilePair
                {
                    Source = source.File, Target = t.File, SourceLanguage = sourceLanguage,
                    TargetLanguage = FullCode(t.Language!, preferred), How = "gleicher Dateiname",
                });
            if (pairs.Any(p => p.Source == source.File)) mono.RemoveAll(c => c.Stem == group.Key && (c == source || IsTarget(c)));
        }

        // 2. übrige Originale und Übersetzungen nach Ähnlichkeit (gemeinsame Zahlen, Länge)
        var sources = mono.Where(IsSource).ToList();
        var targets = mono.Where(c => !IsSource(c)).ToList();
        while (sources.Count > 0 && targets.Count > 0)
        {
            var best = sources.SelectMany(s => targets.Select(t => (s, t, Score: Similarity(s, t)))).MaxBy(x => x.Score);
            Add(best.s, best.t, sources.Count == 1 && targets.Count == 1 ? "nach Sprache zugeordnet" : "nach Inhalt zugeordnet");
            sources.Remove(best.s);
            targets.Remove(best.t);
        }

        // 3. genau zwei Dateien ohne erkennbare Sprache: in der gewählten Reihenfolge
        if (mono.Count == 2 && mono.All(c => c.Language == null))
            Add(mono[0], mono[1], "in der gewählten Reihenfolge – bitte prüfen");

        return new FilePairing(pairs, mono.Select(c => c.File).ToList());
    }

    private sealed record Candidate(ImportFile File, string Stem, string? Language, bool FromName, int Length, HashSet<string> Numbers);

    private static double Similarity(Candidate a, Candidate b)
    {
        var shared = a.Numbers.Count + b.Numbers.Count == 0 ? 0.5
            : 2.0 * a.Numbers.Intersect(b.Numbers).Count() / (a.Numbers.Count + b.Numbers.Count);
        var length = a.Length == 0 || b.Length == 0 ? 0 : Math.Min(a.Length, b.Length) / (double)Math.Max(a.Length, b.Length);
        return shared + length;
    }

    /// <summary>Ein Paar satzweise ausrichten; zweisprachige Dateien ergeben fertige, bestätigte Paare.</summary>
    public static AlignmentSession Align(TranslatedFilePair pair)
    {
        if (pair.Target is { } target)
            return new AlignmentSession(pair.SourceLanguage, pair.TargetLanguage, Sentences(pair.Source, pair.SourceLanguage), Sentences(target, pair.TargetLanguage));
        var filter = FileFilters.ForFile(pair.Source.Name) ?? throw new NotSupportedException($"{pair.Source.Name}: Dateityp wird nicht unterstützt.");
        using var ms = new MemoryStream(pair.Source.Data);
        var doc = filter.Read(ms, pair.Source.Name, pair.SourceLanguage, pair.TargetLanguage, new Segmenter(pair.SourceLanguage));
        return AlignmentSession.FromPairs(pair.SourceLanguage, pair.TargetLanguage,
            doc.Segments.Where(s => !s.Locked && s.Target.Trim().Length > 0).Select(s => (s.Source, s.Target)));
    }

    /// <summary>
    /// Die ausgewählten Paare ins TM schreiben – mit Nachbarsätzen als Kontext, angeglichenen Tags (oder ohne) und
    /// nach den Datenschutz-Einstellungen nur mit Platzhaltern.
    /// </summary>
    public static int ImportToTm(AlignmentSession session, ITranslationMemory tm, string origin, bool keepTags = true, PrivacySettings? privacy = null)
    {
        var accepted = session.Accepted.ToList();
        var entries = new List<TmEntry>();
        for (var i = 0; i < accepted.Count; i++)
        {
            var p = accepted[i];
            var source = keepTags ? p.SourceText : TagText.StripTags(p.SourceText);
            var target = keepTags ? AlignmentSession.MatchTags(p.SourceText, p.TargetText) : TagText.StripTags(p.TargetText);
            string? previous = i > 0 ? TagText.StripTags(accepted[i - 1].SourceText) : null;
            string? next = i + 1 < accepted.Count ? TagText.StripTags(accepted[i + 1].SourceText) : null;
            if (privacy is { Enabled: true, KeepOutOfTm: true })
            {
                (source, target) = PrivacyScanner.MaskPair(source, target, privacy);
                previous = previous == null ? null : TmPrivacy.Mask(previous, privacy);
                next = next == null ? null : TmPrivacy.Mask(next, privacy);
            }
            entries.Add(new TmEntry(session.SourceLanguage, session.TargetLanguage, source, target, Origin: origin, Previous: previous, Next: next));
        }
        return tm.AddMany(entries);
    }

    /// <summary>
    /// Wohin alte Übersetzungen gehören: die gemeinsame TM bzw. Terminologie des Sprachpaars (gilt in jedem Projekt mit
    /// diesen Sprachen). Gibt es keine, wird sie im Standardordner angelegt und an die Sprachen gebunden.
    /// </summary>
    public static (string Name, string Path, bool Created) SharedDatabase(ResourceKind kind, string sourceLanguage, string targetLanguage, TranslationProject? project)
    {
        var existing = LanguageResources.Load().Concat(project?.Resources ?? [])
            .Where(r => r.Kind == kind && !r.IsRemote && r.WriteBack && r.Languages.Count > 0 && r.AppliesTo(sourceLanguage) && r.AppliesTo(targetLanguage))
            .FirstOrDefault(r => File.Exists(r.Location));
        if (existing != null) return (Path.GetFileNameWithoutExtension(existing.Location), existing.Location, false);
        var name = (kind == ResourceKind.Tm ? "TM " : "Terminologie ") + $"{sourceLanguage}–{targetLanguage}";
        var r = new DatabaseRecommendation(kind, name, DatabaseCatalog.SuggestedPath(kind, name), [sourceLanguage, targetLanguage], "");
        var created = !File.Exists(r.Path);
        var fits = project != null && Languages.Matches(project.SourceLanguage, sourceLanguage)
                   && project.TargetLanguages.Any(t => Languages.Matches(t, targetLanguage));
        DatabaseCatalog.Create(r, fits ? project : null);
        return (name, r.Path, created);
    }
}
