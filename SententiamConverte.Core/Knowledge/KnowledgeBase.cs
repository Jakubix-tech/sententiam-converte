using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Knowledge;

public enum KnowledgeKind
{
    /// <summary>Sachwissen zum Fachgebiet.</summary>
    Fact,
    /// <summary>Begriffserklärung.</summary>
    Definition,
    /// <summary>Wie etwas in der Sprache üblicherweise gesagt wird (Wendungen, Kollokationen).</summary>
    Usage,
    /// <summary>Stil- und Schreibregeln (Anrede, Zahlenformat, Normen, Kundenvorgaben).</summary>
    Style,
    /// <summary>Freie Notiz.</summary>
    Note,
}

/// <summary>Ein Wissenseintrag mit Herkunft – damit jeder im Team nachprüfen kann, woher er stammt.</summary>
public sealed record KnowledgeEntry
{
    public long Id { get; init; }
    public string Language { get; init; } = "";
    public string Topic { get; init; } = "";
    public KnowledgeKind Kind { get; init; } = KnowledgeKind.Fact;
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    /// <summary>Kurzes wörtliches Belegzitat (nur wenige Sätze).</summary>
    public string? Quote { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceTitle { get; init; }
    public string? Author { get; init; }
    public double Confidence { get; init; } = 1;
    public DateTime Created { get; init; } = DateTime.UtcNow;
    public DateTime Modified { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Wissensdatenbank in einer SQLite-Datei mit Volltextindex. Kann pro Projekt und pro Sprache
/// angebunden werden; die KI bekommt passende Einträge als Kontext.
/// </summary>
public sealed class KnowledgeBase : IKnowledgeBase
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    public string Path { get; }
    public string Name { get; }
    public string Location => Path;
    public bool IsRemote => false;

    public KnowledgeBase(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS entry(
              id INTEGER PRIMARY KEY, lang TEXT NOT NULL, lang_p TEXT NOT NULL, topic TEXT NOT NULL,
              kind INTEGER NOT NULL, title TEXT NOT NULL, content TEXT NOT NULL, quote TEXT,
              source_url TEXT, source_title TEXT, author TEXT, confidence REAL NOT NULL DEFAULT 1,
              created TEXT NOT NULL, modified TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entry_lang ON entry(lang_p);
            CREATE VIRTUAL TABLE IF NOT EXISTS entry_fts USING fts5(
              title, content, topic, content='entry', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS entry_ai AFTER INSERT ON entry BEGIN
              INSERT INTO entry_fts(rowid, title, content, topic) VALUES (new.id, new.title, new.content, new.topic); END;
            CREATE TRIGGER IF NOT EXISTS entry_ad AFTER DELETE ON entry BEGIN
              INSERT INTO entry_fts(entry_fts, rowid, title, content, topic) VALUES ('delete', old.id, old.title, old.content, old.topic); END;
            CREATE TRIGGER IF NOT EXISTS entry_au AFTER UPDATE ON entry BEGIN
              INSERT INTO entry_fts(entry_fts, rowid, title, content, topic) VALUES ('delete', old.id, old.title, old.content, old.topic);
              INSERT INTO entry_fts(rowid, title, content, topic) VALUES (new.id, new.title, new.content, new.topic); END;
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long Count()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entry";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public long Add(KnowledgeEntry e)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entry(lang,lang_p,topic,kind,title,content,quote,source_url,source_title,author,confidence,created,modified)
                VALUES($l,$lp,$t,$k,$ti,$c,$q,$su,$st,$a,$conf,$cr,$m); SELECT last_insert_rowid();
                """;
            Bind(cmd, e);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void Update(KnowledgeEntry e)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE entry SET lang=$l, lang_p=$lp, topic=$t, kind=$k, title=$ti, content=$c, quote=$q,
                  source_url=$su, source_title=$st, author=$a, confidence=$conf, modified=$m WHERE id=$id
                """;
            Bind(cmd, e with { Modified = DateTime.UtcNow });
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, KnowledgeEntry e)
    {
        cmd.Parameters.AddWithValue("$l", e.Language);
        cmd.Parameters.AddWithValue("$lp", e.Language.Length == 0 ? "" : Languages.Primary(e.Language));
        cmd.Parameters.AddWithValue("$t", e.Topic);
        cmd.Parameters.AddWithValue("$k", (int)e.Kind);
        cmd.Parameters.AddWithValue("$ti", e.Title);
        cmd.Parameters.AddWithValue("$c", e.Content);
        cmd.Parameters.AddWithValue("$q", (object?)e.Quote ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$su", (object?)e.SourceUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", (object?)e.SourceTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$a", (object?)e.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", e.Confidence);
        cmd.Parameters.AddWithValue("$cr", e.Created.ToString("o"));
        cmd.Parameters.AddWithValue("$m", e.Modified.ToString("o"));
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM entry WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<KnowledgeEntry> Search(string? language, string query, int max = 10)
    {
        var words = FuzzyMatcher.Words(query).Where(w => w.Length > 2).Distinct().Take(30).ToList();
        if (words.Count == 0) return [];
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT entry.* FROM entry_fts JOIN entry ON entry.id = entry_fts.rowid
                WHERE entry_fts MATCH $q {(language == null ? "" : "AND (entry.lang_p = $lp OR entry.lang_p = '')")}
                ORDER BY bm25(entry_fts) LIMIT $max
                """;
            cmd.Parameters.AddWithValue("$q", string.Join(" OR ", words.Select(w => "\"" + w.Replace("\"", "\"\"") + "\"")));
            if (language != null) cmd.Parameters.AddWithValue("$lp", Languages.Primary(language));
            cmd.Parameters.AddWithValue("$max", max);
            return Read(cmd);
        }
    }

    public List<KnowledgeEntry> Browse(string? language, string? filter, int offset, int limit)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrEmpty(language)) { where.Add("lang_p = $lp"); cmd.Parameters.AddWithValue("$lp", Languages.Primary(language)); }
            if (!string.IsNullOrWhiteSpace(filter))
            {
                where.Add("(title LIKE $f ESCAPE '!' OR content LIKE $f ESCAPE '!' OR topic LIKE $f ESCAPE '!')");
                cmd.Parameters.AddWithValue("$f", "%" + filter.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%");
            }
            cmd.CommandText = "SELECT * FROM entry" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                              " ORDER BY modified DESC LIMIT $l OFFSET $o";
            cmd.Parameters.AddWithValue("$l", limit);
            cmd.Parameters.AddWithValue("$o", offset);
            return Read(cmd);
        }
    }

    public IEnumerable<KnowledgeEntry> All() => Browse(null, null, 0, int.MaxValue);

    private static List<KnowledgeEntry> Read(SqliteCommand cmd)
    {
        var list = new List<KnowledgeEntry>();
        using var r = cmd.ExecuteReader();
        string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        while (r.Read())
            list.Add(new KnowledgeEntry
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                Language = r.GetString(r.GetOrdinal("lang")),
                Topic = r.GetString(r.GetOrdinal("topic")),
                Kind = (KnowledgeKind)r.GetInt32(r.GetOrdinal("kind")),
                Title = r.GetString(r.GetOrdinal("title")),
                Content = r.GetString(r.GetOrdinal("content")),
                Quote = S("quote"),
                SourceUrl = S("source_url"),
                SourceTitle = S("source_title"),
                Author = S("author"),
                Confidence = r.GetDouble(r.GetOrdinal("confidence")),
                Created = DateTime.Parse(r.GetString(r.GetOrdinal("created")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Modified = DateTime.Parse(r.GetString(r.GetOrdinal("modified")), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        return list;
    }

    public static string KindName(KnowledgeKind k) => k switch
    {
        KnowledgeKind.Fact => "Sachwissen",
        KnowledgeKind.Definition => "Definition",
        KnowledgeKind.Usage => "Sprachgebrauch",
        KnowledgeKind.Style => "Stilregel",
        _ => "Notiz",
    };

    public void Dispose() => _db.Dispose();
}
