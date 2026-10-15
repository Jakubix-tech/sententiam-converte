using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;
using static SententiamConverte.Core.Remote.RemoteApi;

namespace SententiamConverte.Core.Remote;

/// <summary>Ein TM auf dem Teamserver – alle im Team lesen und schreiben gleichzeitig.</summary>
public sealed class RemoteTranslationMemory(RemoteApi api, string name) : ITranslationMemory
{
    private string P => $"tm/{Seg(name)}/";
    public string Name => name;
    public string Location => $"{api.Account.Name}: {name}";
    public bool IsRemote => true;

    public long Count() => api.Send<long>(HttpMethod.Get, P + "count");

    public void Add(string srcLang, string tgtLang, string source, string target, string? origin = null, string? context = null, DateTime? created = null) =>
        api.Send(HttpMethod.Post, P + "units", new TmAddRequest(srcLang, tgtLang, source, target, origin, context, created));

    public int AddMany(IEnumerable<(string SrcLang, string TgtLang, string Source, string Target, DateTime? Created)> units)
    {
        var total = 0;
        foreach (var chunk in units.Chunk(500))
            total += api.Send<int>(HttpMethod.Post, P + "units/batch",
                chunk.Select(u => new TmAddRequest(u.SrcLang, u.TgtLang, u.Source, u.Target, "import", null, u.Created)).ToList());
        return total;
    }

    public void Update(long id, string source, string target) => api.Send(HttpMethod.Put, P + $"units/{id}", new TmUpdateRequest(source, target));
    public void Delete(long id) => api.Send(HttpMethod.Delete, P + $"units/{id}");

    public List<TmMatch> Search(string srcLang, string tgtLang, string source, int minScore = 70, int max = 10) =>
        api.Send<List<TmMatch>>(HttpMethod.Post, P + "search", new TmSearchRequest(srcLang, tgtLang, source, minScore, max));

    public List<FragmentMatch> Fragments(string srcLang, string tgtLang, string source, int max = 15) =>
        api.Send<List<FragmentMatch>>(HttpMethod.Post, P + "fragments", new TmSearchRequest(srcLang, tgtLang, source, 0, max));

    public List<TmUnit> Concordance(string srcLang, string tgtLang, string phrase, bool inTarget = false, int max = 50) =>
        api.Send<List<TmUnit>>(HttpMethod.Post, P + "concordance", new TmSearchRequest(srcLang, tgtLang, phrase, 0, max, inTarget));

    public List<TmUnit> Browse(string? filter, int offset, int limit) =>
        api.Send<List<TmUnit>>(HttpMethod.Get, P + $"units?filter={Uri.EscapeDataString(filter ?? "")}&offset={offset}&limit={limit}");

    public IEnumerable<TmUnit> All()
    {
        for (var offset = 0; ; offset += 1000)
        {
            var page = Browse(null, offset, 1000);
            foreach (var u in page) yield return u;
            if (page.Count < 1000) yield break;
        }
    }

    public void Dispose() { }
}

/// <summary>Eine Terminologie auf dem Teamserver.</summary>
public sealed class RemoteTermbase(RemoteApi api, string name) : ITermbase
{
    private string P => $"tb/{Seg(name)}/";
    public string Name => name;
    public string Location => $"{api.Account.Name}: {name}";
    public bool IsRemote => true;

    public long AddConcept(IEnumerable<(string Lang, string Text, TermStatus Status)> terms, string? definition = null, string? domain = null, string? source = null) =>
        api.Send<long>(HttpMethod.Post, P + "concepts",
            new ConceptAddRequest(terms.Select(t => new TermDto(t.Lang, t.Text, t.Status)).ToList(), definition, domain, source));

    public void DeleteConcept(long id) => api.Send(HttpMethod.Delete, P + $"concepts/{id}");
    public long ConceptCount() => api.Send<long>(HttpMethod.Get, P + "count");
    public Concept GetConcept(long id) => api.Send<Concept>(HttpMethod.Get, P + $"concepts/{id}");
    public IEnumerable<Concept> AllConcepts() => api.Send<List<Concept>>(HttpMethod.Get, P + "concepts");

    public List<Concept> Lookup(string lang, string text, int max = 30) =>
        api.Send<List<Concept>>(HttpMethod.Get, P + $"lookup?lang={Uri.EscapeDataString(lang)}&text={Uri.EscapeDataString(text)}&max={max}");

    public List<TermHit> Recognize(string segmentText, string srcLang, string tgtLang) =>
        api.Send<List<TermHit>>(HttpMethod.Post, P + "recognize", new TermRecognizeRequest(segmentText, srcLang, tgtLang));

    public void Dispose() { }
}

/// <summary>Eine Wissensdatenbank auf dem Teamserver.</summary>
public sealed class RemoteKnowledgeBase(RemoteApi api, string name) : IKnowledgeBase
{
    private string P => $"kb/{Seg(name)}/";
    public string Name => name;
    public string Location => $"{api.Account.Name}: {name}";
    public bool IsRemote => true;

    public long Count() => api.Send<long>(HttpMethod.Get, P + "count");
    public long Add(KnowledgeEntry entry) => api.Send<long>(HttpMethod.Post, P + "entries", entry);
    public void Update(KnowledgeEntry entry) => api.Send(HttpMethod.Put, P + $"entries/{entry.Id}", entry);
    public void Delete(long id) => api.Send(HttpMethod.Delete, P + $"entries/{id}");

    public List<KnowledgeEntry> Search(string? language, string query, int max = 10) =>
        api.Send<List<KnowledgeEntry>>(HttpMethod.Post, P + "search", new KnowledgeSearchRequest(language, query, max));

    public List<KnowledgeEntry> Browse(string? language, string? filter, int offset, int limit) =>
        api.Send<List<KnowledgeEntry>>(HttpMethod.Get,
            P + $"entries?language={Uri.EscapeDataString(language ?? "")}&filter={Uri.EscapeDataString(filter ?? "")}&offset={offset}&limit={limit}");

    public IEnumerable<KnowledgeEntry> All()
    {
        for (var offset = 0; ; offset += 500)
        {
            var page = Browse(null, null, offset, 500);
            foreach (var e in page) yield return e;
            if (page.Count < 500) yield break;
        }
    }

    public void Dispose() { }
}
