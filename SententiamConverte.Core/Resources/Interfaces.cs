using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Resources;

/// <summary>Gemeinsames für alle Sprachressourcen – lokal (SQLite-Datei) oder auf einem Teamserver.</summary>
public interface ILanguageResource : IDisposable
{
    string Name { get; }
    /// <summary>Dateipfad oder Serveradresse – zur Anzeige.</summary>
    string Location { get; }
    bool IsRemote { get; }
}

public interface ITranslationMemory : ILanguageResource
{
    long Count();
    void Add(string srcLang, string tgtLang, string source, string target, string? origin = null, string? context = null, DateTime? created = null);
    int AddMany(IEnumerable<(string SrcLang, string TgtLang, string Source, string Target, DateTime? Created)> units);
    void Update(long id, string source, string target);
    void Delete(long id);
    List<TmMatch> Search(string srcLang, string tgtLang, string source, int minScore = 70, int max = 10);
    List<FragmentMatch> Fragments(string srcLang, string tgtLang, string source, int max = 15);
    List<TmUnit> Concordance(string srcLang, string tgtLang, string phrase, bool inTarget = false, int max = 50);
    List<TmUnit> Browse(string? filter, int offset, int limit);
    IEnumerable<TmUnit> All();
}

public interface ITermbase : ILanguageResource
{
    long AddConcept(IEnumerable<(string Lang, string Text, TermStatus Status)> terms, string? definition = null, string? domain = null, string? source = null);
    void DeleteConcept(long id);
    long ConceptCount();
    Concept GetConcept(long id);
    IEnumerable<Concept> AllConcepts();
    List<Concept> Lookup(string lang, string text, int max = 30);
    List<TermHit> Recognize(string segmentText, string srcLang, string tgtLang);
}

public interface IKnowledgeBase : ILanguageResource
{
    long Count();
    long Add(KnowledgeEntry entry);
    void Update(KnowledgeEntry entry);
    void Delete(long id);
    /// <summary>Volltextsuche; <paramref name="language"/> null = alle Sprachen.</summary>
    List<KnowledgeEntry> Search(string? language, string query, int max = 10);
    List<KnowledgeEntry> Browse(string? language, string? filter, int offset, int limit);
    IEnumerable<KnowledgeEntry> All();
}
