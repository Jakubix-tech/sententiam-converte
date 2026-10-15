using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tm;

public sealed record TmUnit(
    long Id, string SourceLanguage, string TargetLanguage, string Source, string Target,
    DateTime Created, DateTime Modified, int UsageCount, string? Origin, string? Context);

public sealed record TmMatch(TmUnit Unit, int Score, string TmName);

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
            CREATE UNIQUE INDEX IF NOT EXISTS tu_key ON tu(src_p, tgt_p, source);
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

    /// <summary>Speichert eine Übersetzung. Gibt es die Quelle schon, wird die Übersetzung ersetzt (übliches Standardverhalten).</summary>
    public void Add(string srcLang, string tgtLang, string source, string target, string? origin = null, string? context = null, DateTime? created = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return;
        var now = DateTime.UtcNow.ToString("o");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tu(src_lang,tgt_lang,src_p,tgt_p,source,target,source_plain,target_plain,created,modified,origin,context,source_norm)
            VALUES($sl,$tl,$sp,$tp,$s,$t,$spl,$tpl,$c,$m,$o,$ctx,$norm)
            ON CONFLICT(src_p,tgt_p,source) DO UPDATE SET
              target=excluded.target, target_plain=excluded.target_plain, tgt_lang=excluded.tgt_lang,
              modified=excluded.modified, origin=excluded.origin, context=COALESCE(excluded.context, tu.context)
            """;
        cmd.Parameters.AddWithValue("$sl", srcLang);
        cmd.Parameters.AddWithValue("$tl", tgtLang);
        cmd.Parameters.AddWithValue("$sp", Languages.Primary(srcLang));
        cmd.Parameters.AddWithValue("$tp", Languages.Primary(tgtLang));
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$t", target);
        cmd.Parameters.AddWithValue("$spl", TagText.StripTags(source));
        cmd.Parameters.AddWithValue("$tpl", TagText.StripTags(target));
        cmd.Parameters.AddWithValue("$c", (created ?? DateTime.UtcNow).ToString("o"));
        cmd.Parameters.AddWithValue("$m", now);
        cmd.Parameters.AddWithValue("$o", (object?)origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ctx", (object?)context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$norm", Norm(source));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Viele Einträge in einer Transaktion (TMX-Import).</summary>
    public int AddMany(IEnumerable<(string SrcLang, string TgtLang, string Source, string Target, DateTime? Created)> units)
    {
        var n = 0;
        using var tx = _db.BeginTransaction();
        foreach (var u in units)
        {
            Add(u.SrcLang, u.TgtLang, u.Source, u.Target, "import", null, u.Created);
            n++;
        }
        tx.Commit();
        return n;
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
    public List<TmMatch> Search(string srcLang, string tgtLang, string source, int minScore = 70, int max = 10)
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

        return results.Values.Where(r => r.Score >= minScore)
            .OrderByDescending(r => r.Score).ThenByDescending(r => r.Unit.Modified)
            .Take(max).ToList();
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
