using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tm;

public sealed record TmUnit(
    long Id, string SourceLanguage, string TargetLanguage, string Source, string Target,
    DateTime Created, DateTime Modified, int UsageCount, string? Origin, string? Context,
    string? ContextPrevious = null, string? ContextNext = null);

/// <summary>Wodurch ein Treffer zum Kontexttreffer wird.</summary>
public enum TmContextMatch
{
    None,
    /// <summary>Der vorherige Satz ist derselbe.</summary>
    Previous,
    /// <summary>Vorheriger und nächster Satz sind dieselben.</summary>
    PreviousAndNext,
    /// <summary>Gleiche ID/gleicher Schlüssel (Software-Texte: RESX, JSON, PO).</summary>
    Id,
}

/// <summary>
/// Ein TM-Treffer. Score 101 = Kontexttreffer (gleicher Satz im gleichen Zusammenhang).
/// <see cref="Ambiguous"/>: für denselben Satz gibt es mehrere Übersetzungen, keine passt zum Kontext (1 % Abzug).
/// </summary>
public sealed record TmMatch(TmUnit Unit, int Score, string TmName, TmContextMatch ContextMatch = TmContextMatch.None, bool Ambiguous = false);

/// <summary>Zusammenhang eines Segments: Nachbarsätze und Stelle im Dokument (Überschrift, Zelle, ID …).</summary>
public sealed record TmContext(string? Previous, string? Next, string? Structure, bool StructureIsId = false)
{
    /// <summary>Vergleichsform: ohne Tags, Leerraum vereinheitlicht.</summary>
    public static string Key(string? text) =>
        text == null ? "" : string.Join(' ', TagText.StripTags(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Eine Einheit zum Einfügen, z. B. aus einem TMX-Import.</summary>
public sealed record TmEntry(string SrcLang, string TgtLang, string Source, string Target, DateTime? Created = null,
    string? Origin = null, string? Structure = null, string? Previous = null, string? Next = null);

/// <summary>Ein kürzerer TM-Eintrag, der vollständig im aktuellen Segment vorkommt (Fragment-Treffer).</summary>
public sealed record FragmentMatch(string Phrase, int WordIndex, int WordCount, TmUnit Unit, string TmName);

/// <summary>
/// Translation Memory in einer SQLite-Datei. Eine Datei kann mehrere Sprachpaare enthalten.
/// Volltextindex (FTS5) liefert die Kandidaten, <see cref="FuzzyMatcher"/> bewertet sie.
/// </summary>
public sealed class TranslationMemory : ITranslationMemory
{
    private readonly SqliteConnection _db;
    public string Path { get; }
    public string Name { get; }
    public string Location => Path;
    public bool IsRemote => false;

    public TranslationMemory(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS tu(
              id INTEGER PRIMARY KEY,
              src_lang TEXT NOT NULL, tgt_lang TEXT NOT NULL,
              src_p TEXT NOT NULL, tgt_p TEXT NOT NULL,
              source TEXT NOT NULL, target TEXT NOT NULL,
              source_plain TEXT NOT NULL, target_plain TEXT NOT NULL,
              created TEXT NOT NULL, modified TEXT NOT NULL,
              usage_count INTEGER NOT NULL DEFAULT 0,
              origin TEXT, context TEXT);
            CREATE TABLE IF NOT EXISTS tu_context(
              tu_id INTEGER NOT NULL, prev TEXT NOT NULL, next TEXT NOT NULL DEFAULT '',
              structure TEXT NOT NULL DEFAULT '', added TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS tu_context_key ON tu_context(tu_id, prev, structure);
            CREATE TRIGGER IF NOT EXISTS tu_ctx_ad AFTER DELETE ON tu BEGIN
              DELETE FROM tu_context WHERE tu_id = old.id; END;
            CREATE VIRTUAL TABLE IF NOT EXISTS tu_fts USING fts5(
              source_plain, target_plain, content='tu', content_rowid='id',
              tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS tu_ai AFTER INSERT ON tu BEGIN
              INSERT INTO tu_fts(rowid, source_plain, target_plain) VALUES (new.id, new.source_plain, new.target_plain); END;
            CREATE TRIGGER IF NOT EXISTS tu_ad AFTER DELETE ON tu BEGIN
              INSERT INTO tu_fts(tu_fts, rowid, source_plain, target_plain) VALUES ('delete', old.id, old.source_plain, old.target_plain); END;
            CREATE TRIGGER IF NOT EXISTS tu_au AFTER UPDATE ON tu BEGIN
              INSERT INTO tu_fts(tu_fts, rowid, source_plain, target_plain) VALUES ('delete', old.id, old.source_plain, old.target_plain);
              INSERT INTO tu_fts(rowid, source_plain, target_plain) VALUES (new.id, new.source_plain, new.target_plain); END;
            """);
        Migrate();
    }

    /// <summary>Ältere TM-Dateien um neue Spalten ergänzen.</summary>
    private void Migrate()
    {
        var columns = new HashSet<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(tu)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        if (!columns.Contains("source_norm"))
        {
            Exec("ALTER TABLE tu ADD COLUMN source_norm TEXT NOT NULL DEFAULT ''");
            var rows = new List<(long, string)>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, source FROM tu";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1)));
            }
            using var tx = _db.BeginTransaction();
            foreach (var (id, src) in rows)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "UPDATE tu SET source_norm=$n WHERE id=$id";
                cmd.Parameters.AddWithValue("$n", Norm(src));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Exec("CREATE INDEX IF NOT EXISTS tu_norm ON tu(src_p, tgt_p, source_norm)");
        // Früher eine Übersetzung je Quelle; jetzt mehrere (je Kontext), eindeutig ist Quelle + Übersetzung.
        Exec("DROP INDEX IF EXISTS tu_key; CREATE UNIQUE INDEX IF NOT EXISTS tu_key2 ON tu(src_p, tgt_p, source, target)");
    }

    internal static string Norm(string text) => string.Join(' ', FuzzyMatcher.Words(text));

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long Count()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tu";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Speichert eine Übersetzung mit ihrem Kontext (vorheriger/nächster Satz, Stelle im Dokument).
    /// Derselbe Satz darf je Kontext unterschiedlich übersetzt sein. Eine neue Übersetzung im selben Kontext
    /// ersetzt die alte; ohne Kontextangabe wird – wie früher – die kontextlose Übersetzung ersetzt.
    /// </summary>
    public void Add(string srcLang, string tgtLang, string source, string target, string? origin = null, string? context = null,
        DateTime? created = null, string? previous = null, string? next = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return;
        using var tx = _db.BeginTransaction();
        AddCore(new TmEntry(srcLang, tgtLang, source, target, created, origin, context, previous, next));
        tx.Commit();
    }

    private void AddCore(TmEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Source) || string.IsNullOrWhiteSpace(e.Target)) return;
        var sp = Languages.Primary(e.SrcLang);
        var tp = Languages.Primary(e.TgtLang);
        var hasContext = e.Previous != null;
        var prev = TmContext.Key(e.Previous);
        var structure = e.Structure ?? "";
        var now = DateTime.UtcNow.ToString("o");

        long? sameTarget = Scalar("SELECT id FROM tu WHERE src_p=$sp AND tgt_p=$tp AND source=$s AND target=$t",
            ("$sp", sp), ("$tp", tp), ("$s", e.Source), ("$t", e.Target));

        if (hasContext)
        {
            // Gab es in genau diesem Kontext eine andere Übersetzung? Dann ist sie hier überholt.
            var superseded = Ids("""
                SELECT tu.id FROM tu JOIN tu_context c ON c.tu_id = tu.id
                WHERE tu.src_p=$sp AND tu.tgt_p=$tp AND tu.source=$s AND tu.target<>$t AND c.prev=$prev AND c.structure=$st
                """, ("$sp", sp), ("$tp", tp), ("$s", e.Source), ("$t", e.Target), ("$prev", prev), ("$st", structure));
            foreach (var old in superseded)
            {
                Run("DELETE FROM tu_context WHERE tu_id=$id AND prev=$prev AND structure=$st", ("$id", old), ("$prev", prev), ("$st", structure));
                // Hatte die alte Übersetzung keinen anderen Kontext mehr, ist es eine Korrektur: Ziel ersetzen.
                if (sameTarget == null && Scalar("SELECT COUNT(*) FROM tu_context WHERE tu_id=$id", ("$id", old)) == 0)
                {
                    UpdateTarget(old, e.Target, e.Origin);
                    sameTarget = old;
                }
            }
        }
        else if (sameTarget == null)
        {
            // Ohne Kontext: kontextlose Übersetzung desselben Satzes ersetzen (früheres Verhalten)
            var plain = Scalar("""
                SELECT id FROM tu WHERE src_p=$sp AND tgt_p=$tp AND source=$s
                  AND NOT EXISTS (SELECT 1 FROM tu_context c WHERE c.tu_id = tu.id) ORDER BY modified DESC LIMIT 1
                """, ("$sp", sp), ("$tp", tp), ("$s", e.Source));
            if (plain != null)
            {
                UpdateTarget(plain.Value, e.Target, e.Origin);
                sameTarget = plain;
            }
        }

        long id;
        if (sameTarget != null)
        {
            id = sameTarget.Value;
            Run("UPDATE tu SET modified=$m, origin=COALESCE($o, origin), context=COALESCE($ctx, context), tgt_lang=$tl WHERE id=$id",
                ("$m", now), ("$o", e.Origin), ("$ctx", e.Structure), ("$tl", e.TgtLang), ("$id", id));
        }
        else
        {
            id = Scalar("""
                INSERT INTO tu(src_lang,tgt_lang,src_p,tgt_p,source,target,source_plain,target_plain,created,modified,origin,context,source_norm)
                VALUES($sl,$tl,$sp,$tp,$s,$t,$spl,$tpl,$c,$m,$o,$ctx,$norm); SELECT last_insert_rowid();
                """,
                ("$sl", e.SrcLang), ("$tl", e.TgtLang), ("$sp", sp), ("$tp", tp), ("$s", e.Source), ("$t", e.Target),
                ("$spl", TagText.StripTags(e.Source)), ("$tpl", TagText.StripTags(e.Target)),
                ("$c", (e.Created ?? DateTime.UtcNow).ToString("o")), ("$m", now), ("$o", e.Origin), ("$ctx", e.Structure),
                ("$norm", Norm(e.Source)))!.Value;
        }

        if (hasContext)
            Run("""
                INSERT INTO tu_context(tu_id, prev, next, structure, added) VALUES($id, $prev, $next, $st, $m)
                ON CONFLICT(tu_id, prev, structure) DO UPDATE SET next=excluded.next, added=excluded.added
                """, ("$id", id), ("$prev", prev), ("$next", TmContext.Key(e.Next)), ("$st", structure), ("$m", now));
    }

    private void UpdateTarget(long id, string target, string? origin) =>
        Run("UPDATE tu SET target=$t, target_plain=$tpl, modified=$m, origin=COALESCE($o, origin) WHERE id=$id",
            ("$t", target), ("$tpl", TagText.StripTags(target)), ("$m", DateTime.UtcNow.ToString("o")), ("$o", origin), ("$id", id));

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    private void Run(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private long? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    private List<long> Ids(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Viele Einträge in einer Transaktion (TMX-Import).</summary>
    public int AddMany(IEnumerable<TmEntry> units)
    {
        var n = 0;
        using var tx = _db.BeginTransaction();
        foreach (var u in units)
        {
            AddCore(u with { Origin = u.Origin ?? "import" });
            n++;
        }
        tx.Commit();
        return n;
    }

    /// <summary>Alle gespeicherten Kontexte einer Einheit (für den TMX-Export).</summary>
    public List<(string Previous, string Next, string Structure)> ContextsOf(long id)
    {
        using var cmd = Command("SELECT prev, next, structure FROM tu_context WHERE tu_id=$id ORDER BY added DESC", [("$id", id)]);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>Eintrag bearbeiten (TM-Pflege).</summary>
    public void Update(long id, string source, string target)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE tu SET source=$s, target=$t, source_plain=$spl, target_plain=$tpl, source_norm=$n, modified=$m WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$t", target);
        cmd.Parameters.AddWithValue("$spl", TagText.StripTags(source));
        cmd.Parameters.AddWithValue("$tpl", TagText.StripTags(target));
        cmd.Parameters.AddWithValue("$n", Norm(source));
        cmd.Parameters.AddWithValue("$m", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Einträge blättern, optional nach Text in Quelle oder Ziel gefiltert (TM-Pflege).</summary>
    public List<TmUnit> Browse(string? filter, int offset, int limit)
    {
        using var cmd = _db.CreateCommand();
        if (string.IsNullOrWhiteSpace(filter))
            cmd.CommandText = "SELECT * FROM tu ORDER BY modified DESC LIMIT $l OFFSET $o";
        else
        {
            cmd.CommandText = "SELECT * FROM tu WHERE source_plain LIKE $f ESCAPE '!' OR target_plain LIKE $f ESCAPE '!' ORDER BY modified DESC LIMIT $l OFFSET $o";
            cmd.Parameters.AddWithValue("$f", "%" + filter.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%");
        }
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        return Read(cmd).ToList();
    }

    public void Delete(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM tu WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkUsed(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE tu SET usage_count = usage_count + 1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Unscharfe Suche: alle Treffer ab <paramref name="minScore"/>, beste zuerst.</summary>
    public List<TmMatch> Search(string srcLang, string tgtLang, string source, int minScore = 70, int max = 10, TmContext? context = null)
    {
        var results = new Dictionary<long, TmMatch>();

        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM tu WHERE src_p=$sp AND tgt_p=$tp AND source_plain=$pl LIMIT 20";
            cmd.Parameters.AddWithValue("$sp", Languages.Primary(srcLang));
            cmd.Parameters.AddWithValue("$tp", Languages.Primary(tgtLang));
            cmd.Parameters.AddWithValue("$pl", TagText.StripTags(source));
            foreach (var u in Read(cmd))
                results[u.Id] = new TmMatch(u, FuzzyMatcher.Score(source, u.Source), Name);
        }

        var words = FuzzyMatcher.Words(source).Where(w => w.Length > 1).Distinct().Take(40).ToList();
        if (words.Count > 0)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT tu.* FROM tu_fts JOIN tu ON tu.id = tu_fts.rowid
                WHERE tu_fts MATCH $q AND tu.src_p=$sp AND tu.tgt_p=$tp
                ORDER BY bm25(tu_fts) LIMIT 200
                """;
            cmd.Parameters.AddWithValue("$q", "source_plain : (" + string.Join(" OR ", words.Select(Quote)) + ")");
            cmd.Parameters.AddWithValue("$sp", Languages.Primary(srcLang));
            cmd.Parameters.AddWithValue("$tp", Languages.Primary(tgtLang));
            foreach (var u in Read(cmd))
            {
                if (results.ContainsKey(u.Id)) continue;
                var score = FuzzyMatcher.Score(source, u.Source);
                if (score >= minScore) results[u.Id] = new TmMatch(u, score, Name);
            }
        }

        var scored = ApplyContext(results.Values.ToList(), context);
        return scored.Where(r => r.Score >= minScore)
            .OrderByDescending(r => r.Score).ThenByDescending(r => r.Unit.Modified)
            .Take(max).ToList();
    }

    /// <summary>
    /// Kontext bewerten: gleicher Satz im gleichen Zusammenhang → 101 %. Gibt es für denselben Satz mehrere
    /// Übersetzungen und passt eine nicht zum Kontext, wird sie um 1 % abgewertet (nicht blind einsetzen).
    /// </summary>
    private List<TmMatch> ApplyContext(List<TmMatch> matches, TmContext? context)
    {
        var exact = matches.Where(m => m.Score >= 99).ToList();
        if (exact.Count == 0) return matches;
        var prevKey = context?.Previous == null ? null : TmContext.Key(context.Previous);
        var nextKey = context?.Next == null ? null : TmContext.Key(context.Next);
        var multiple = exact.Select(m => m.Unit.Target).Distinct().Count() > 1;

        var result = new List<TmMatch>();
        foreach (var m in matches)
        {
            if (m.Score < 99) { result.Add(m); continue; }
            var contexts = ContextsOf(m.Unit.Id);
            var kind = TmContextMatch.None;
            if (prevKey != null)
            {
                var hit = contexts.FirstOrDefault(c => c.Previous == prevKey &&
                                                       (c.Structure == (context!.Structure ?? "") || c.Structure.Length == 0 || string.IsNullOrEmpty(context.Structure)));
                if (hit.Previous != null)
                    kind = nextKey != null && hit.Next == nextKey ? TmContextMatch.PreviousAndNext : TmContextMatch.Previous;
            }
            if (kind == TmContextMatch.None && context is { StructureIsId: true, Structure: { Length: > 0 } id } &&
                (contexts.Any(c => c.Structure == id) || m.Unit.Context == id))
                kind = TmContextMatch.Id;

            var last = contexts.FirstOrDefault();
            var unit = last.Previous == null ? m.Unit : m.Unit with { ContextPrevious = last.Previous, ContextNext = last.Next };
            if (kind != TmContextMatch.None && m.Score == 100)
                result.Add(m with { Unit = unit, Score = 101, ContextMatch = kind });
            else if (multiple)
                result.Add(m with { Unit = unit, Score = Math.Max(0, m.Score - 1), Ambiguous = true });
            else
                result.Add(m with { Unit = unit });
        }
        return result;
    }

    /// <summary>
    /// Fragment-Treffer: TM-Einträge mit 2 bis 12 Wörtern, deren ganze Quelle im Segment vorkommt
    /// (z. B. Überschriften, Benennungen, feste Wendungen). Längere zuerst, ohne Überlappung.
    /// </summary>
    public List<FragmentMatch> Fragments(string srcLang, string tgtLang, string source, int max = 15)
    {
        var words = FuzzyMatcher.Words(source).ToList();
        if (words.Count < 3) return [];
        var grams = new Dictionary<string, (int Index, int Count)>();
        for (var n = Math.Min(12, words.Count - 1); n >= 2; n--)
            for (var i = 0; i + n <= words.Count; i++)
                grams.TryAdd(string.Join(' ', words.Skip(i).Take(n)), (i, n));

        var found = new List<FragmentMatch>();
        foreach (var chunk in grams.Keys.Chunk(400))
        {
            using var cmd = _db.CreateCommand();
            var names = chunk.Select((_, k) => "$g" + k).ToList();
            cmd.CommandText = $"SELECT * FROM tu WHERE src_p=$sp AND tgt_p=$tp AND source_norm IN ({string.Join(',', names)})";
            cmd.Parameters.AddWithValue("$sp", Languages.Primary(srcLang));
            cmd.Parameters.AddWithValue("$tp", Languages.Primary(tgtLang));
            for (var k = 0; k < chunk.Length; k++) cmd.Parameters.AddWithValue(names[k], chunk[k]);
            foreach (var u in Read(cmd))
            {
                var (idx, count) = grams[Norm(u.Source)];
                found.Add(new FragmentMatch(TagText.StripTags(u.Source), idx, count, u, Name));
            }
        }

        var taken = new bool[words.Count];
        var result = new List<FragmentMatch>();
        foreach (var f in found.OrderByDescending(f => f.WordCount).ThenByDescending(f => f.Unit.Modified))
        {
            if (Enumerable.Range(f.WordIndex, f.WordCount).Any(i => taken[i])) continue;
            for (var i = f.WordIndex; i < f.WordIndex + f.WordCount; i++) taken[i] = true;
            result.Add(f);
            if (result.Count >= max) break;
        }
        return result.OrderBy(f => f.WordIndex).ToList();
    }

    /// <summary>Konkordanzsuche: wo kommt ein Ausdruck in Quelle oder Ziel vor.</summary>
    public List<TmUnit> Concordance(string srcLang, string tgtLang, string phrase, bool inTarget = false, int max = 50)
    {
        var words = FuzzyMatcher.Words(phrase).ToList();
        if (words.Count == 0) return [];
        using var cmd = _db.CreateCommand();
        var column = inTarget ? "target_plain" : "source_plain";
        cmd.CommandText = $"""
            SELECT tu.* FROM tu_fts JOIN tu ON tu.id = tu_fts.rowid
            WHERE tu_fts MATCH $q AND tu.src_p=$sp AND tu.tgt_p=$tp
            ORDER BY bm25(tu_fts) LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$q", $"{column} : " + string.Join(" ", words.Select(Quote)));
        cmd.Parameters.AddWithValue("$sp", Languages.Primary(srcLang));
        cmd.Parameters.AddWithValue("$tp", Languages.Primary(tgtLang));
        cmd.Parameters.AddWithValue("$max", max);
        return Read(cmd).ToList();
    }

    public IEnumerable<TmUnit> All()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM tu ORDER BY id";
        foreach (var u in Read(cmd)) yield return u;
    }

    private static string Quote(string word) => "\"" + word.Replace("\"", "\"\"") + "\"";

    private static IEnumerable<TmUnit> Read(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new TmUnit(
                r.GetInt64(r.GetOrdinal("id")),
                r.GetString(r.GetOrdinal("src_lang")), r.GetString(r.GetOrdinal("tgt_lang")),
                r.GetString(r.GetOrdinal("source")), r.GetString(r.GetOrdinal("target")),
                DateTime.Parse(r.GetString(r.GetOrdinal("created")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(r.GetString(r.GetOrdinal("modified")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.GetInt32(r.GetOrdinal("usage_count")),
                r.IsDBNull(r.GetOrdinal("origin")) ? null : r.GetString(r.GetOrdinal("origin")),
                r.IsDBNull(r.GetOrdinal("context")) ? null : r.GetString(r.GetOrdinal("context")));
        }
    }

    public void Dispose() => _db.Dispose();
}
