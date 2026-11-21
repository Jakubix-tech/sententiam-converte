using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Resources;

/// <summary>Eigene Fachtexte: welche Ordner, wo der Suchindex liegt und wer Auszüge daraus bekommt.</summary>
public sealed class ReferenceLibrarySettings
{
    public List<string> Folders { get; set; } = new();

    /// <summary>Ordner für den Suchindex; leer = Datenordner des Programms. Der Index braucht etwa so viel Platz wie der reine Text.</summary>
    public string IndexFolder { get; set; } = "";

    /// <summary>Auszüge auch an Online-KI geben (Vorgabe: nur an KI auf diesem Rechner – die Texte können vertraulich sein).</summary>
    public bool AllowOnlineAi { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "fachtexte.json");

    public string IndexPath => Path.Combine(IndexFolder is { Length: > 0 } f ? f : AppPaths.DataRoot, "fachtexte.scidx");

    public static ReferenceLibrarySettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ReferenceLibrarySettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Eine eingelesene Datei: Titel, Sprache, Stand (woher er stammt) und ob sich Text fand.</summary>
public sealed record LibraryFile(string Path, string Title, string? Language, DateTime? Date, string? Version, int Pages, int Passages, string? Problem);

/// <summary>Fundstelle: Satz mit der Wortfolge, Datei (oder eingearbeitete Quelle), Seite bzw. Abschnitt, Stand.</summary>
public sealed record LibraryHit(string Path, string Title, string? Language, int? Page, string Quote, DateTime? Date, string? Version)
{
    /// <summary>Abschnitt einer eingearbeiteten Quelle, z. B. „§ 242 Diebstahl“.</summary>
    public string? Section { get; init; }

    /// <summary>Adresse zum Öffnen (PDF-Betrachter springen mit #page= auf die Seite).</summary>
    public string Uri => new Uri(Path).AbsoluteUri + (Page is { } p ? "#page=" + p : "");

    public string Label => Section is { Length: > 0 } s ? $"{Title}, {s}" : Page is { } p ? $"{Title}, S. {p}" : Title;
}

/// <summary>Ergebnis eines Durchgangs: neu, geändert, entfernt, unverändert, und was sich nicht lesen ließ.</summary>
public sealed record LibraryScan(int Added, int Updated, int Removed, int Unchanged, List<(string Path, string Problem)> Problems);

/// <summary>
/// Eigene Fachtexte (PDF, Word, Writer, Text, HTML …) als lokaler Volltextindex. „Gelernt“ heißt hier nachgeschlagen:
/// Die Recherche findet darin Belegstellen mit Datei, Seite und Stand und zählt, wie oft eine Benennung vorkommt – das
/// bleibt aktuell, nachvollziehbar und löschbar, und nichts verlässt den Rechner (außer an eine Online-KI, wenn erlaubt).
/// Beim erneuten Einlesen werden nur neue und geänderte Dateien gelesen.
/// </summary>
public sealed partial class ReferenceLibrary : IDisposable
{
    private static readonly HashSet<string> Extensions =
        [".pdf", ".docx", ".odt", ".txt", ".md", ".html", ".htm", ".pptx", ".odp", ".xlsx", ".ods"];

    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public string Path { get; }

    public ReferenceLibrary(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS file(
              id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, size INTEGER NOT NULL, stamp TEXT NOT NULL, title TEXT NOT NULL,
              lang TEXT, doc_date TEXT, version TEXT, pages INTEGER NOT NULL DEFAULT 0, problem TEXT, indexed TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS passage(id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, page INTEGER, text TEXT NOT NULL, label TEXT);
            CREATE INDEX IF NOT EXISTS passage_file ON passage(file_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS passage_fts USING fts5(
              text, content='passage', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS passage_ai AFTER INSERT ON passage BEGIN
              INSERT INTO passage_fts(rowid, text) VALUES (new.id, new.text); END;
            CREATE TRIGGER IF NOT EXISTS passage_ad AFTER DELETE ON passage BEGIN
              INSERT INTO passage_fts(passage_fts, rowid, text) VALUES ('delete', old.id, old.text); END;
            """);
        // Indexdateien aus der ersten Fassung kennen keine Abschnittsnamen
        using var columns = _db.CreateCommand();
        columns.CommandText = "SELECT count(*) FROM pragma_table_info('passage') WHERE name = 'label'";
        if (Convert.ToInt32(columns.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) Exec("ALTER TABLE passage ADD COLUMN label TEXT");
    }

    /// <summary>Eingearbeitete Quellen (Gesetze …) stehen unter ihrer Webadresse, nicht unter einem Dateipfad.</summary>
    internal static bool IsSourceKey(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Eine eingearbeitete Quelle aufnehmen (z. B. ein Gesetz): Abschnitte mit Namen („§ 242 Diebstahl“) und Text. Eine
    /// ältere Fassung derselben Adresse wird ersetzt; das Einlesen der Ordner lässt solche Quellen stehen.
    /// </summary>
    public void AddDocument(string url, string title, string? language, DateTime? date, string? version, IReadOnlyList<(string Label, string Text)> sections)
    {
        var content = new Extracted(title, language is { } l ? Languages.Primary(l) : null, date, version, 0,
            sections.Select(s => ((int?)null, s.Text)).ToList(), null);
        Store(url, sections.Sum(s => (long)s.Text.Length), version ?? date?.ToString("O", CultureInfo.InvariantCulture) ?? "", content, sections.Select(s => (string?)s.Label).ToList());
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearPool(_db);
    }

    // ---------- gemeinsamer Index ----------

    private static ReferenceLibrary? _shared;
    private static readonly object SharedLock = new();

    /// <summary>Der Index am angegebenen Ort, einmal geöffnet für das ganze Programm.</summary>
    public static ReferenceLibrary Shared(string path)
    {
        lock (SharedLock)
        {
            if (_shared?.Path != path)
            {
                _shared?.Dispose();
                _shared = new ReferenceLibrary(path);
            }
            return _shared;
        }
    }

    /// <summary>Gemeinsamen Index schließen (vor dem Verschieben oder Löschen der Datei).</summary>
    public static void CloseShared()
    {
        lock (SharedLock)
        {
            _shared?.Dispose();
            _shared = null;
        }
    }

    /// <summary>Index an einen anderen Ort verschieben (samt Begleitdateien); ohne vorhandenen Index passiert nichts.</summary>
    public static void MoveIndex(string from, string to)
    {
        CloseShared();
        if (!File.Exists(from) || string.Equals(System.IO.Path.GetFullPath(from), System.IO.Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(to))!);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(from + suffix)) File.Move(from + suffix, to + suffix, true);
    }

    /// <summary>
    /// Die eigenen Fachtexte für eine KI: immer für KI auf diesem Rechner, für Online-KI nur, wenn erlaubt; null, wenn
    /// keine Ordner eingestellt oder noch nichts eingelesen ist.
    /// </summary>
    public static ReferenceLibrary? For(AiProviderConfig provider)
    {
        var settings = ReferenceLibrarySettings.Load();
        if (settings.Folders.Count == 0 || !File.Exists(settings.IndexPath)) return null;
        if (!provider.IsLocal && !settings.AllowOnlineAi) return null;
        try { return Shared(settings.IndexPath); }
        catch (SqliteException) { return null; }
    }

    // ---------- Einlesen ----------

    public static bool IsSupported(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()) && FileFilters.ForFile(System.IO.Path.GetFileName(path)) != null
        && !System.IO.Path.GetFileName(path).StartsWith("~$");                          // Sperrdatei eines geöffneten Word-Dokuments

    /// <summary>
    /// Ordner durchgehen: neue und geänderte Dateien lesen, verschwundene entfernen. Dateien eines Ordners, der gerade
    /// nicht erreichbar ist (abgezogenes Laufwerk), bleiben im Index.
    /// </summary>
    public async Task<LibraryScan> ScanAsync(IReadOnlyList<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var roots = folders.Where(f => f.Trim().Length > 0).Select(f => System.IO.Path.GetFullPath(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = roots.Where(Directory.Exists).SelectMany(f => Directory.EnumerateFiles(f, "*", options)).Where(IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var known = Known();
        int added = 0, updated = 0, unchanged = 0, i = 0;
        var problems = new List<(string, string)>();
        foreach (var file in found)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var info = new FileInfo(file);
            var stamp = info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture);
            if (known.TryGetValue(file, out var k) && k.Size == info.Length && k.Stamp == stamp)
            {
                unchanged++;
                continue;
            }
            progress?.Report($"Lese {info.Name} ({i} von {found.Count}) …");
            var content = await Task.Run(() => Extract(file), ct);
            Store(file, info.Length, stamp, content);
            if (content.Problem != null) problems.Add((file, content.Problem));
            if (known.ContainsKey(file)) updated++;
            else added++;
        }
        var present = found.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in known.Keys.Where(p => !present.Contains(p) && !IsSourceKey(p)))
        {
            var root = roots.FirstOrDefault(r => path.StartsWith(r.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root != null && !Directory.Exists(root)) continue;                     // Laufwerk gerade nicht da
            Delete(path);
            removed++;
        }
        return new LibraryScan(added, updated, removed, unchanged, problems);
    }

    private Dictionary<string, (long Size, string Stamp)> Known()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, size, stamp FROM file";
            using var r = cmd.ExecuteReader();
            var result = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) result[r.GetString(0)] = (r.GetInt64(1), r.GetString(2));
            return result;
        }
    }

    private void Delete(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM passage WHERE file_id IN (SELECT id FROM file WHERE path = $p); DELETE FROM file WHERE path = $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    private void Store(string path, long size, string stamp, Extracted content, IReadOnlyList<string?>? labels = null)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            using (var del = _db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM passage WHERE file_id IN (SELECT id FROM file WHERE path = $p); DELETE FROM file WHERE path = $p;";
                del.Parameters.AddWithValue("$p", path);
                del.ExecuteNonQuery();
            }
            long id;
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO file(path, size, stamp, title, lang, doc_date, version, pages, problem, indexed)
                    VALUES ($p, $s, $t, $title, $lang, $date, $version, $pages, $problem, $now); SELECT last_insert_rowid();
                    """;
                ins.Parameters.AddWithValue("$p", path);
                ins.Parameters.AddWithValue("$s", size);
                ins.Parameters.AddWithValue("$t", stamp);
                ins.Parameters.AddWithValue("$title", content.Title);
                ins.Parameters.AddWithValue("$lang", (object?)content.Language ?? DBNull.Value);
                ins.Parameters.AddWithValue("$date", (object?)content.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
                ins.Parameters.AddWithValue("$version", (object?)content.Version ?? DBNull.Value);
                ins.Parameters.AddWithValue("$pages", content.Pages);
                ins.Parameters.AddWithValue("$problem", (object?)content.Problem ?? DBNull.Value);
                ins.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                id = (long)ins.ExecuteScalar()!;
            }
            using (var p = _db.CreateCommand())
            {
                p.Transaction = tx;
                p.CommandText = "INSERT INTO passage(file_id, page, text, label) VALUES ($f, $page, $text, $label)";
                var f = p.Parameters.Add("$f", SqliteType.Integer);
                var page = p.Parameters.Add("$page", SqliteType.Integer);
                var text = p.Parameters.Add("$text", SqliteType.Text);
                var label = p.Parameters.Add("$label", SqliteType.Text);
                f.Value = id;
                for (var i = 0; i < content.Passages.Count; i++)
                {
                    var (pg, t) = content.Passages[i];
                    page.Value = (object?)pg ?? DBNull.Value;
                    text.Value = t;
                    label.Value = (object?)(labels != null && i < labels.Count ? labels[i] : null) ?? DBNull.Value;
                    p.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    internal sealed record Extracted(string Title, string? Language, DateTime? Date, string? Version, int Pages, List<(int? Page, string Text)> Passages, string? Problem);

    /// <summary>Text einer Datei in Abschnitten (bei PDF mit Seite), dazu Sprache, Titel und Stand.</summary>
    internal static Extracted Extract(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var title = System.IO.Path.GetFileNameWithoutExtension(path);
        try
        {
            var data = File.ReadAllBytes(path);
            var hint = TranslatedFiles.SplitName(name).Language;                       // „Handbuch_EN.pdf“
            var language = hint != null ? TranslatedFiles.FullCode(hint, []) : "de-DE";
            BilingualDocument doc;
            using (var ms = new MemoryStream(data))
                doc = FileFilters.ForFile(name)!.Read(ms, name, language, language, new Segmenter(language));
            var passages = Passages(doc);
            if (passages.Count == 0) return new(title, null, null, null, 0, [], "kein Text gefunden");
            var sample = string.Join(" ", passages.Take(200).Select(p => p.Text));
            var detected = TranslatedFiles.DetectLanguage(sample) ?? hint;
            string? pdfTitle = null;
            DateTime? pdfDate = null;
            if (doc.FilterId == "pdf") (pdfTitle, pdfDate) = PdfInfo(data);
            var (date, version) = StatedDate(sample.Length > 4000 ? sample[..4000] : sample) is { } stated ? stated
                : pdfDate is { } p ? (p, $"PDF vom {p:dd.MM.yyyy}")
                : (File.GetLastWriteTime(path), $"Datei vom {File.GetLastWriteTime(path):dd.MM.yyyy}");
            var pages = passages.Max(x => x.Page) ?? 0;
            return new(pdfTitle ?? title, detected, date, version, pages, passages, null);
        }
        catch (InvalidDataException ex) when (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return new(title, null, null, null, 0, [], ex.Message);                     // gescannt: erst Texterkennung (OCR)
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(title, null, null, null, 0, [], ex.Message);
        }
    }

    private static List<(int? Page, string Text)> Passages(BilingualDocument doc)
    {
        var result = new List<(int?, string)>();
        int? page = null;
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Count(char.IsLetter) >= 3) result.Add((page, text));
            current.Clear();
        }
        foreach (var p in doc.Paragraphs)
        {
            var text = string.Join(" ", p.Segments.Select(s => TagText.StripTags(s.Source).Trim()).Where(s => s.Length > 0));
            if (text.Length == 0) continue;
            int? pg = PdfLocator().Match(p.Locator) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
            if (pg != page || current.Length + text.Length > 700) Flush();
            page = pg;
            if (current.Length > 0) current.Append('\n');                    // Absatzgrenze bleibt Satzgrenze
            current.Append(text);
        }
        Flush();
        return result;
    }

    [GeneratedRegex(@"^(\d+):\d+:\d+$")]
    private static partial Regex PdfLocator();

    private static (string? Title, DateTime? Date) PdfInfo(byte[] data)
    {
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(data);
            var info = pdf.Information;
            var date = info.GetModifiedDateTimeOffset() ?? info.GetCreatedDateTimeOffset();
            return (string.IsNullOrWhiteSpace(info.Title) ? null : info.Title.Trim(), date?.LocalDateTime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, null); }
    }

    // „Stand: 01.03.2021“, „Ausgabe 2019-03“, „Edition 2020“, „Wydanie 3, 2018“, „DIN EN ISO 12100:2011-03“
    [GeneratedRegex(@"\b(?<label>Stand|Ausgabe|Fassung|Version|Edition|Issue|Revision|Wydanie|Stan na dzień|Stan prawny na|Mise à jour|Édition)\s*(?:vom|from|du|z)?\s*:?\s*(?<date>\d{1,2}\.\s?\d{1,2}\.\s?\d{4}|\d{4}-\d{2}(?:-\d{2})?|\d{1,2}/\d{4}|\d{1,2}\.\d{4}|(?:\p{L}+\s)?\d{4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex StatedRegex();

    [GeneratedRegex(@"\b(?:DIN|EN|ISO|IEC|PN|NF|BS|ÖNORM|VDI)(?:\s(?:EN|ISO|IEC|VDE))*\s?\d{2,5}(?:-\d+)*:(?<date>\d{4}(?:-\d{2})?)")]
    private static partial Regex StandardRegex();

    /// <summary>Stand, den das Dokument selbst nennt (Text und Datum); null, wenn keiner dasteht.</summary>
    internal static (DateTime Date, string Version)? StatedDate(string text)
    {
        foreach (var m in new[] { StatedRegex().Match(text), StandardRegex().Match(text) }.Where(m => m.Success))
            if (ParseDate(m.Groups["date"].Value) is { } d && d.Year is >= 1950 and <= 2100)
                return (d, Regex.Replace(m.Value, @"\s+", " ").Trim());
        return null;
    }

    private static DateTime? ParseDate(string s)
    {
        s = Regex.Replace(s, @"\s", "");
        string[] formats = ["d.M.yyyy", "yyyy-MM-dd", "yyyy-MM", "M/yyyy", "M.yyyy", "yyyy"];
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return Regex.Match(s, @"\d{4}$") is { Success: true } y ? new DateTime(int.Parse(y.Value, CultureInfo.InvariantCulture), 1, 1) : null;
    }

    // ---------- Suchen ----------

    public List<LibraryFile> Files()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT f.path, f.title, f.lang, f.doc_date, f.version, f.pages, (SELECT count(*) FROM passage p WHERE p.file_id = f.id), f.problem
                FROM file f ORDER BY f.path
                """;
            using var r = cmd.ExecuteReader();
            var result = new List<LibraryFile>();
            while (r.Read())
                result.Add(new LibraryFile(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), ReadDate(r, 3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
            return result;
        }
    }

    /// <summary>Alle Abschnitte einer eingelesenen Datei in ihrer Reihenfolge (Seite, Text) – zum Einarbeiten.</summary>
    public List<(int? Page, string Text)> Passages(string path)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT p.page, p.text FROM passage p JOIN file f ON f.id = p.file_id WHERE f.path = $path ORDER BY p.id";
            cmd.Parameters.AddWithValue("$path", path);
            using var r = cmd.ExecuteReader();
            var result = new List<(int?, string)>();
            while (r.Read()) result.Add((r.IsDBNull(0) ? null : r.GetInt32(0), r.GetString(1)));
            return result;
        }
    }

    private static DateTime? ReadDate(SqliteDataReader r, int i) =>
        !r.IsDBNull(i) && DateTime.TryParseExact(r.GetString(i), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Suchausdruck für die Wortfolge; das letzte Wort darf eine Endung haben („Gleitringdichtung“ findet „…dichtungen“).</summary>
    private static string? PhraseQuery(string phrase)
    {
        var words = WordRegex().Matches(phrase).Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        var prefix = words[^1].Length >= 4 ? "*" : "";
        return "\"" + string.Join(" ", words) + "\"" + prefix;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    /// <summary>Muster für die Wortfolge im Text: Wortgrenzen, beliebige Trennzeichen, Endung am letzten Wort (wie <see cref="PhraseQuery"/>).</summary>
    private static Regex? PhraseRegex(string phrase)
    {
        var words = WordRegex().Matches(phrase).Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        var pattern = string.Join(@"[^\p{L}\p{N}]+", words.Select(Regex.Escape)) + (words[^1].Length >= 4 ? @"\p{L}*" : "");
        return new Regex(@"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Steht die Wortfolge im Text (ganze Wörter, das letzte darf eine Endung haben, Groß/klein und Trennzeichen egal)?</summary>
    internal static bool Mentions(string text, string phrase) => PhraseRegex(phrase)?.IsMatch(text) == true;

    /// <summary>Wie oft die Wortfolge vorkommt (in der Sprache, sonst in allen) – jedes Vorkommen zählt, auch mehrere in einem Abschnitt.</summary>
    public int CountPhrase(string phrase, string? language = null)
    {
        if (PhraseQuery(phrase) is not { } q || PhraseRegex(phrase) is not { } rx) return 0;
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT p.text FROM passage_fts JOIN passage p ON p.id = passage_fts.rowid JOIN file f ON f.id = p.file_id
                WHERE passage_fts MATCH $q {(language == null ? "" : "AND f.lang = $lp")} LIMIT 20000
                """;
            cmd.Parameters.AddWithValue("$q", q);
            if (language != null) cmd.Parameters.AddWithValue("$lp", Languages.Primary(language));
            using var r = cmd.ExecuteReader();
            var count = 0;
            while (r.Read()) count += rx.Matches(r.GetString(0)).Count;
            return count;
        }
    }

    /// <summary>Stellen mit der Wortfolge: der Satz, in dem sie steht (wörtlich, damit er als Beleg taugt), mit Datei und Seite.</summary>
    public List<LibraryHit> FindPhrase(string phrase, string? language = null, int max = 5)
    {
        if (PhraseQuery(phrase) is not { } q) return [];
        var rows = new List<(string Text, int? Page, string Path, string Title, string? Lang, DateTime? Date, string? Version, string? Label)>();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT p.text, p.page, f.path, f.title, f.lang, f.doc_date, f.version, p.label
                FROM passage_fts JOIN passage p ON p.id = passage_fts.rowid JOIN file f ON f.id = p.file_id
                WHERE passage_fts MATCH $q {(language == null ? "" : "AND f.lang = $lp")}
                ORDER BY bm25(passage_fts) LIMIT $max
                """;
            cmd.Parameters.AddWithValue("$q", q);
            if (language != null) cmd.Parameters.AddWithValue("$lp", Languages.Primary(language));
            cmd.Parameters.AddWithValue("$max", max * 3);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    ReadDate(r, 5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return rows.Select(x => Sentence(x.Text, phrase) is { } quote ? new LibraryHit(x.Path, x.Title, x.Lang, x.Page, quote, x.Date, x.Version) { Section = x.Label } : null)
            .OfType<LibraryHit>().DistinctBy(h => h.Quote).Take(max).ToList();
    }

    /// <summary>Der Satz um die Wortfolge (höchstens 300 Zeichen); null, wenn sie so nicht dasteht.</summary>
    internal static string? Sentence(string text, string phrase)
    {
        if (PhraseRegex(phrase)?.Match(text) is not { Success: true } m) return null;
        var start = text.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, m.Index - 1));
        start = start < 0 || m.Index - start > 200 ? Math.Max(0, m.Index - 120) : start + 1;
        var endMatch = Regex.Match(text[(m.Index + m.Length)..], @"[.!?](?=\s|$)");
        var end = endMatch.Success && endMatch.Index < 200 ? m.Index + m.Length + endMatch.Index + 1 : Math.Min(text.Length, m.Index + m.Length + 120);
        return text[start..end].Trim();
    }
}
