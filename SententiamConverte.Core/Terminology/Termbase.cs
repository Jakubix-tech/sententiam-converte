using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Terminology;

public enum TermStatus { Preferred, Admitted, Forbidden }

public sealed record Term(long Id, long ConceptId, string Language, string Text, TermStatus Status, string? Note);

public sealed record Concept(long Id, string? Definition, string? Domain, string? Source, List<Term> Terms);

/// <summary>Ein im Segment erkannter Begriff mit seinen Übersetzungen.</summary>
public sealed record TermHit(Term SourceTerm, int Index, int Length, List<Term> Translations, Concept Concept);

/// <summary>
/// Terminologie-Datenbank: Konzepte mit Benennungen in beliebig vielen Sprachen.
/// </summary>
public sealed class Termbase : ITermbase
{
    private readonly SqliteConnection _db;
    private readonly Dictionary<string, List<Term>> _cache = new();
    public string Path { get; }
    public string Name { get; }
    public string Location => Path;
    public bool IsRemote => false;

    public Termbase(string path, string? name = null)
    {
        Path = path;
        Name = name ?? System.IO.Path.GetFileNameWithoutExtension(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS concept(
              id INTEGER PRIMARY KEY, definition TEXT, domain TEXT, source TEXT, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS term(
              id INTEGER PRIMARY KEY,
              concept_id INTEGER NOT NULL REFERENCES concept(id) ON DELETE CASCADE,
              lang TEXT NOT NULL, lang_p TEXT NOT NULL, text TEXT NOT NULL,
              status INTEGER NOT NULL DEFAULT 0, note TEXT);
            CREATE INDEX IF NOT EXISTS term_lang ON term(lang_p);
            CREATE INDEX IF NOT EXISTS term_concept ON term(concept_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public long AddConcept(IEnumerable<(string Lang, string Text, TermStatus Status)> terms, string? definition = null, string? domain = null, string? source = null)
    {
        using var tx = _db.BeginTransaction();
        long id;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO concept(definition,domain,source,created) VALUES($d,$dom,$src,$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$d", (object?)definition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dom", (object?)domain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
            id = (long)cmd.ExecuteScalar()!;
        }
        foreach (var t in terms.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
            InsertTerm(id, t.Lang, t.Text.Trim(), t.Status, null);
        tx.Commit();
        _cache.Clear();
        return id;
    }

    public void AddTerm(long conceptId, string lang, string text, TermStatus status = TermStatus.Preferred, string? note = null)
    {
        InsertTerm(conceptId, lang, text, status, note);
        _cache.Clear();
    }

    private void InsertTerm(long conceptId, string lang, string text, TermStatus status, string? note)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO term(concept_id,lang,lang_p,text,status,note) VALUES($c,$l,$lp,$t,$s,$n)";
        cmd.Parameters.AddWithValue("$c", conceptId);
        cmd.Parameters.AddWithValue("$l", lang);
        cmd.Parameters.AddWithValue("$lp", Languages.Primary(lang));
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$n", (object?)note ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteConcept(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM concept WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        _cache.Clear();
    }

    public long ConceptCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM concept";
        return (long)cmd.ExecuteScalar()!;
    }

    public Concept GetConcept(long id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT definition, domain, source FROM concept WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read();
        var c = new Concept(id, r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), TermsOf(id));
        return c;
    }

    public IEnumerable<Concept> AllConcepts()
    {
        var ids = new List<long>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM concept ORDER BY id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        foreach (var id in ids) yield return GetConcept(id);
    }

    private List<Term> TermsOf(long conceptId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, concept_id, lang, text, status, note FROM term WHERE concept_id=$c";
        cmd.Parameters.AddWithValue("$c", conceptId);
        return ReadTerms(cmd);
    }

    private List<Term> TermsInLanguage(string lang)
    {
        var p = Languages.Primary(lang);
        if (_cache.TryGetValue(p, out var list)) return list;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, concept_id, lang, text, status, note FROM term WHERE lang_p=$l";
        cmd.Parameters.AddWithValue("$l", p);
        list = ReadTerms(cmd);
        _cache[p] = list;
        return list;
    }

    private static List<Term> ReadTerms(SqliteCommand cmd)
    {
        var list = new List<Term>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Term(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), (TermStatus)r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Suche nach Benennungen, die mit dem Text beginnen oder ihn enthalten.</summary>
    public List<Concept> Lookup(string lang, string text, int max = 30)
    {
        var needle = text.Trim();
        if (needle.Length == 0) return [];
        return TermsInLanguage(lang)
            .Where(t => t.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Text.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Text.Length)
            .Select(t => t.ConceptId).Distinct().Take(max)
            .Select(GetConcept).ToList();
    }

    /// <summary>
    /// Termerkennung: findet Begriffe der Ausgangssprache im Segment, auch mit Endungen
    /// ("Schraube" findet "Schrauben"), längere Begriffe zuerst.
    /// </summary>
    public List<TermHit> Recognize(string segmentText, string srcLang, string tgtLang)
    {
        var plain = TagText.StripTags(segmentText);
        var hits = new List<TermHit>();
        var taken = new bool[plain.Length];
        foreach (var term in TermsInLanguage(srcLang).OrderByDescending(t => t.Text.Length))
        {
            foreach (var (index, length) in FindTerm(plain, term.Text))
            {
                if (Enumerable.Range(index, length).Any(i => taken[i])) continue;
                for (var i = index; i < index + length; i++) taken[i] = true;
                var concept = GetConcept(term.ConceptId);
                var translations = concept.Terms.Where(t => Languages.Matches(t.Language, tgtLang))
                    .OrderBy(t => t.Status).ToList();
                hits.Add(new TermHit(term, index, length, translations, concept));
            }
        }
        return hits.OrderBy(h => h.Index).ToList();
    }

    internal static IEnumerable<(int Index, int Length)> FindTerm(string text, string term)
    {
        // Wortanfang muss passen; am Ende sind bis zu 3 weitere Buchstaben als Endung erlaubt,
        // bei sehr kurzen Begriffen nur exakte Wortgrenzen.
        var maxSuffix = term.Length >= 5 ? 3 : 0;
        var pattern = $@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(term)}\p{{L}}{{0,{maxSuffix}}}(?![\p{{L}}\p{{Nd}}])";
        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            yield return (m.Index, m.Length);
    }

    public void Dispose() => _db.Dispose();
}
