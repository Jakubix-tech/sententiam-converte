using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Ai.LocalMt;

/// <summary>Eine Datei eines Modells: Hash ist SHA-256 (64 Zeichen) oder bei kleinen Dateien die Git-Kennung (SHA-1, 40 Zeichen).</summary>
public sealed record LocalMtFile(string Name, string Revision, long Size, string Hash);

/// <summary>Ein OPUS-MT-Modell der Universität Helsinki (Apache-2.0 oder CC-BY-4.0), fest auf eine Fassung gepinnt.</summary>
public sealed record LocalMtModel(string Repo, string License, IReadOnlyList<LocalMtFile> Files)
{
    /// <summary>Ordnername, z. B. „opus-mt-de-en“.</summary>
    public string Id => Repo[(Repo.IndexOf('/') + 1)..];
    public long Size => Files.Sum(f => f.Size);
    public string SizeText => BuiltInAi.FormatSize(Size);
    public string Url(LocalMtFile f) => $"https://huggingface.co/{Repo}/resolve/{f.Revision}/{f.Name}";
}

/// <summary>Eine Übersetzungsrichtung: Modell und – bei Mehrsprachenmodellen – die Kennung der Zielsprache.</summary>
public sealed record LocalMtPair(string Source, string Target, LocalMtModel Model, string? TargetToken);

/// <summary>Weg von der Ausgangs- zur Zielsprache: direkt oder über Englisch (zwei Schritte, schwächer).</summary>
public sealed record LocalMtRoute(IReadOnlyList<LocalMtPair> Steps)
{
    public bool ViaEnglish => Steps.Count > 1;
    public IEnumerable<LocalMtModel> Models => Steps.Select(s => s.Model).DistinctBy(m => m.Id);
    public bool IsInstalled => Models.All(LocalTranslation.IsInstalled);
    public long MissingBytes => Models.Where(m => !LocalTranslation.IsInstalled(m)).Sum(m => m.Size);
}

/// <summary>
/// Übersetzung auf diesem Rechner mit OPUS-MT (Universität Helsinki): je Sprachrichtung ein kleines Übersetzungsmodell
/// (~300 MB), das auf Wunsch von Hugging Face geladen und über seine Prüfsummen geprüft wird. Danach verlässt kein Text
/// den Rechner; es läuft ohne Grafikkarte und ohne native Bibliothek, also auch auf Android. Wie jede klassische
/// maschinelle Übersetzung ohne Anweisungen, Terminologie und Kontext – als schneller Entwurf gedacht.
/// </summary>
public static partial class LocalTranslation
{
    /// <summary>Alle verfügbaren Richtungen (aus dem mitgelieferten Katalog).</summary>
    public static IReadOnlyList<LocalMtPair> Catalog { get; } = LoadCatalog();

    private static IReadOnlyList<LocalMtPair> LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SententiamConverte.Core.Ai.LocalMt.katalog.json")!;
        var models = new Dictionary<string, LocalMtModel>();
        var pairs = new List<LocalMtPair>();
        foreach (var e in JsonNode.Parse(stream)!.AsArray().Select(n => n!.AsObject()))
        {
            var repo = (string)e["repo"]!;
            if (!models.TryGetValue(repo, out var model))
            {
                string rev = (string)e["rev"]!, weights = (string)e["wrev"]!;
                var files = e["files"]!.AsArray().Select(f => f!.AsArray()).Select(f =>
                    new LocalMtFile((string)f[0]!, (string)f[0]! == "model.safetensors" ? weights : rev, (long)f[1]!, (string)f[2]!)).ToList();
                models[repo] = model = new LocalMtModel(repo, (string)e["license"]!, files);
            }
            var pair = ((string)e["pair"]!).Split('-');
            pairs.Add(new LocalMtPair(pair[0], pair[1], model, (string?)e["token"]));
        }
        return pairs;
    }

    /// <summary>Sprachcode des Programms → Code der Modelle (Hauptsprache; Norwegisch bokmål/nynorsk → „no“).</summary>
    public static string Code(string language) => Languages.Primary(language) switch { "nb" or "nn" => "no", var p => p };

    /// <summary>Direkte Richtung, sonst über Englisch; null, wenn es keinen Weg gibt.</summary>
    public static LocalMtRoute? Route(string sourceLanguage, string targetLanguage)
    {
        string s = Code(sourceLanguage), t = Code(targetLanguage);
        if (s == t) return null;
        LocalMtPair? Direct(string a, string b) => Catalog.FirstOrDefault(p => p.Source == a && p.Target == b);
        if (Direct(s, t) is { } direct) return new LocalMtRoute([direct]);
        if (s != "en" && t != "en" && Direct(s, "en") is { } first && Direct("en", t) is { } second) return new LocalMtRoute([first, second]);
        return null;
    }

    // ---------- Ordner, Laden, Entfernen ----------

    private static string? _folder;

    /// <summary>Wo die Modelle liegen; Standard: neben der eingebauten KI (deren Ordner ist wählbar).</summary>
    public static string Folder
    {
        get => _folder is { Length: > 0 } f ? f : Path.Combine(BuiltInAi.Folder, "uebersetzung");
        set => _folder = value;
    }

    private static string ModelDir(LocalMtModel m) => Path.Combine(Folder, m.Id);

    public static bool IsInstalled(LocalMtModel m) =>
        m.Files.All(f => new FileInfo(Path.Combine(ModelDir(m), f.Name)) is { Exists: true } info && info.Length == f.Size);

    /// <summary>Geladene Modelle (aus dem Katalog).</summary>
    public static IReadOnlyList<LocalMtModel> Installed =>
        Catalog.Select(p => p.Model).DistinctBy(m => m.Id).Where(IsInstalled).OrderBy(m => m.Id).ToList();

    /// <summary>Für Tests: andere Downloadquelle.</summary>
    internal static Func<string, string>? UrlOverride { get; set; }

    /// <summary>Alle Modelle eines Weges laden (abgebrochene Downloads werden fortgesetzt) und prüfen.</summary>
    public static async Task InstallAsync(LocalMtRoute route, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var missing = route.MissingBytes;
        Directory.CreateDirectory(Folder);
        if (BuiltInAi.FreeSpace(Folder) is { } free && free < missing + 200_000_000)
            throw new AiException($"Zu wenig Speicherplatz: gebraucht {BuiltInAi.FormatSize(missing)}, frei {BuiltInAi.FormatSize(free)} auf " +
                                  $"{Path.GetPathRoot(Path.GetFullPath(Folder))}. Unter „Kostenlos auf diesem Rechner“ einen anderen Ordner wählen.");
        using var http = BuiltInAi.Http();
        foreach (var model in route.Models.Where(m => !IsInstalled(m)))
        {
            var dir = ModelDir(model);
            Directory.CreateDirectory(dir);
            foreach (var file in model.Files)
            {
                var target = Path.Combine(dir, file.Name);
                if (new FileInfo(target) is { Exists: true } info && info.Length == file.Size) continue;
                var url = UrlOverride?.Invoke(model.Url(file)) ?? model.Url(file);
                var what = file.Name == "model.safetensors" ? $"Übersetzungsmodell {model.Id}" : $"{model.Id}/{file.Name}";
                await BuiltInAi.DownloadAsync(http, url, target, file.Size, file.Hash.Length == 64 ? file.Hash : null, what, progress, ct);
                if (file.Hash.Length == 40 && GitBlobHash(await File.ReadAllBytesAsync(target, ct)) != file.Hash)
                {
                    File.Delete(target);
                    throw new AiException($"{what}: die heruntergeladene Datei ist beschädigt (Prüfsumme stimmt nicht). Bitte erneut versuchen.");
                }
            }
        }
    }

    /// <summary>Git-Kennung einer Datei: SHA-1 über „blob &lt;Länge&gt;\0“ und den Inhalt.</summary>
    internal static string GitBlobHash(byte[] content)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        sha.AppendData(content);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static void Remove(LocalMtModel model)
    {
        lock (Loaded) Loaded.RemoveAll(l => l.Id == model.Id);
        if (Directory.Exists(ModelDir(model))) Directory.Delete(ModelDir(model), true);
    }

    // ---------- Übersetzen ----------

    private sealed record LoadedModel(string Id, MarianModel Model, MarianVocabulary Vocabulary);
    private static readonly List<LoadedModel> Loaded = [];                     // zuletzt benutzte zuerst
    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private const int KeepLoaded = 2;

    private static async Task<LoadedModel> LoadAsync(LocalMtModel model, CancellationToken ct)
    {
        lock (Loaded)
            if (Loaded.FirstOrDefault(l => l.Id == model.Id) is { } hit)
            {
                Loaded.Remove(hit);
                Loaded.Insert(0, hit);
                return hit;
            }
        await LoadGate.WaitAsync(ct);
        try
        {
            lock (Loaded)
                if (Loaded.FirstOrDefault(l => l.Id == model.Id) is { } hit) return hit;
            var dir = ModelDir(model);
            var loaded = await Task.Run(() =>
            {
                var m = MarianModel.Load(dir);
                return new LoadedModel(model.Id, m, new MarianVocabulary(dir, m.Config));
            }, ct);
            lock (Loaded)
            {
                Loaded.Insert(0, loaded);
                if (Loaded.Count > KeepLoaded) Loaded.RemoveRange(KeepLoaded, Loaded.Count - KeepLoaded);
            }
            return loaded;
        }
        finally { LoadGate.Release(); }
    }

    /// <summary>Segmenttext (mit Tags ⟨1⟩…) übersetzen; Zeilen einzeln, Tags bleiben erhalten.</summary>
    public static async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        if (text.Trim().Length == 0) return text;
        var route = Route(sourceLanguage, targetLanguage)
                    ?? throw new AiException($"Für {Languages.DisplayName(sourceLanguage)} → {Languages.DisplayName(targetLanguage)} gibt es keine lokale Übersetzung.");
        if (!route.IsInstalled)
            throw new AiException($"Die lokale Übersetzung {Languages.DisplayName(sourceLanguage)} → {Languages.DisplayName(targetLanguage)} ist noch nicht " +
                                  "heruntergeladen: Seite „KI einrichten“ → „Maschinelle Übersetzung“ → „Offline auf diesem Rechner“.");
        var result = text;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var step in route.Steps)
        {
            var loaded = await LoadAsync(step.Model, ct);
            var token = TargetToken(step, loaded.Vocabulary, targetLanguage);
            var input = result;
            result = await Task.Run(() => string.Join("\n", input.Split('\n').Select(line => TranslateLine(line, loaded, token, ct))), ct);
        }
        result = Typography.Quotes(text, result, targetLanguage);
        Diagnostics.ActivityLog.Write("Offline-Übersetzung", $"{sourceLanguage} → {targetLanguage}", string.Join(" + ", route.Models.Select(m => m.Id)),
            $"{text}\n→ {result}", watch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>Kennung der Zielsprache bei Mehrsprachenmodellen; mit Region, wenn das Modell sie kennt (z. B. „&gt;&gt;pt_BR&lt;&lt;“).</summary>
    private static string? TargetToken(LocalMtPair step, MarianVocabulary vocabulary, string targetLanguage)
    {
        if (step.TargetToken is not { } token) return null;
        if (targetLanguage.Split('-') is [_, var region] && $"{token[..^2]}_{region.ToUpperInvariant()}<<" is var regional && vocabulary.Has(regional))
            return regional;
        return token;
    }

    private static string TranslateLine(string line, LoadedModel loaded, string? token, CancellationToken ct)
    {
        var core = line.Trim();
        if (core.Length == 0 || !core.Any(char.IsLetter)) return line;              // Zahlen, Zeichen, Tags allein
        var lead = line[..line.IndexOf(core[0])];
        var trail = line[(lead.Length + core.Length)..];

        // Tags, die den ganzen Satz umschließen, gar nicht erst übersetzen lassen
        var (open, inner, close) = Wrapping(core);
        var parts = Sentences(ToMarkup(inner), loaded.Vocabulary)
            .Select(s => loaded.Vocabulary.Decode(loaded.Model.Generate(loaded.Vocabulary.Encode(s, token), ct: ct)));
        var translated = FromMarkup(string.Join(" ", parts));
        return lead + open + TidyTags(inner, translated) + close + trail;
    }

    /// <summary>„⟨1⟩Text⟨/1⟩“ → („⟨1⟩“, „Text“, „⟨/1⟩“), auch geschachtelt; sonst ("", text, "").</summary>
    internal static (string Open, string Inner, string Close) Wrapping(string text)
    {
        string open = "", close = "";
        while (WrapRegex().Match(text) is { Success: true } m && TagText.Tags(m.Groups[2].Value).All(t => t.Id != int.Parse(m.Groups[1].Value)))
        {
            open += TagText.Start(int.Parse(m.Groups[1].Value));
            close = TagText.End(int.Parse(m.Groups[1].Value)) + close;
            text = m.Groups[2].Value;
        }
        return (open, text, close);
    }

    [GeneratedRegex(@"^⟨(\d+)⟩(.*)⟨/\1⟩$", RegexOptions.Singleline)]
    private static partial Regex WrapRegex();

    /// <summary>Tags als einfache XML-Elemente; der Text bleibt unverändert (die Modelle kennen „&amp;“ nicht als „&amp;amp;“).</summary>
    internal static string ToMarkup(string text)
    {
        var sb = new StringBuilder();
        foreach (var part in TagText.Split(text))
            sb.Append(part.Tag is not { } t ? part.Text : t.Kind switch
            {
                TagText.TagKind.Start => $"<g id=\"{t.Id}\">",
                TagText.TagKind.End => "</g>",
                _ => $"<x id=\"{t.Id}\"/>",
            });
        return sb.ToString();
    }

    [GeneratedRegex("""<g\s*id\s*=\s*["']?(\d+)["']?\s*>|</\s*g\s*>|<x\s*id\s*=\s*["']?(\d+)["']?\s*/?\s*>(?:\s*</x>)?""", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"&\s?#\s?(\d{2,5})\s?;|&\s?(amp|lt|gt|quot|nbsp|apos)\s?;")]
    private static partial Regex EntityRegex();

    /// <summary>Antwort zurück in Segmenttext; Entitäten, die das Modell erfindet („&amp; #160;“), werden zu Zeichen.</summary>
    internal static string FromMarkup(string markup)
    {
        var open = new Stack<int>();
        var text = MarkupRegex().Replace(markup, m =>
        {
            if (m.Groups[1].Success)
            {
                open.Push(int.Parse(m.Groups[1].Value));
                return TagText.Start(open.Peek());
            }
            if (m.Groups[2].Success) return TagText.Placeholder(int.Parse(m.Groups[2].Value));
            return open.Count > 0 ? TagText.End(open.Pop()) : "";
        });
        text = EntityRegex().Replace(text, m => m.Groups[1].Success
            ? int.Parse(m.Groups[1].Value) is <= char.MaxValue and var code ? ((char)code).ToString() : m.Value
            : m.Groups[2].Value switch { "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", _ => " " });
        return NbspRegex().Replace(text, " ");                             // „«   Suivant“ → „« Suivant“
    }

    [GeneratedRegex(" *  *")]
    private static partial Regex NbspRegex();

    /// <summary>Leerzeichen innen an Tags wie in der Quelle: „⟨1⟩ Speichern ⟨/1⟩“ → „⟨1⟩Speichern⟨/1⟩“.</summary>
    internal static string TidyTags(string source, string target)
    {
        if (!TagText.Tags(source).Any()) return target;
        target = Regex.Replace(target, @"(⟨\d+⟩) +", m => source.Contains(m.Groups[1].Value + " ") ? m.Value : m.Groups[1].Value);
        target = Regex.Replace(target, @" +(⟨/\d+⟩)", m => source.Contains(" " + m.Groups[1].Value) ? m.Value : m.Groups[1].Value);
        return Regex.Replace(target, @"  +", " ");
    }

    /// <summary>Lange Zeilen an Satzgrenzen teilen, damit jede Hälfte in das Modell passt (höchstens 500 Wortteile).</summary>
    private static IEnumerable<string> Sentences(string text, MarianVocabulary vocabulary)
    {
        if (vocabulary.Pieces(text).Count <= 400) return [text];
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?;:])\s+"))
        {
            if (current.Length > 0 && vocabulary.Pieces(current + " " + sentence).Count > 400)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        // ein einzelner Satz über der Grenze: hart nach Wörtern teilen
        return parts.SelectMany(p => vocabulary.Pieces(p).Count <= 480 ? [p] : Chunks(p, vocabulary));
    }

    private static IEnumerable<string> Chunks(string text, MarianVocabulary vocabulary)
    {
        var words = text.Split(' ');
        var current = new List<string>();
        foreach (var w in words)
        {
            current.Add(w);
            if (vocabulary.Pieces(string.Join(' ', current)).Count > 400)
            {
                yield return string.Join(' ', current);
                current.Clear();
            }
        }
        if (current.Count > 0) yield return string.Join(' ', current);
    }
}
