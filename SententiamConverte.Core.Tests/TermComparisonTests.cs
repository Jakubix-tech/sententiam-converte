using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Tests;

public class TermComparisonTests
{
    private sealed class Model : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string DisplayName => "Skript";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            var p = messages[^1].Text;
            Prompts.Add(p);
            if (p.Contains("Explain what the term means IN THIS CONTEXT"))
                return Task.FromResult("""{"meaning": "Zugelassener Rechtsanwalt, der hier vor Gericht vertritt", "keyFeatures": ["Zulassung", "Vertretung vor Gericht"], "sourceSystem": "deutsches Recht", "targetSystem": "Recht von England und Wales", "candidates": [{"term": "solicitor", "type": "functional", "why": "Beratung"}, {"term": "barrister", "type": "functional", "why": "Vertretung vor Gericht"}], "queries": {"de-DE": ["Rechtsanwalt Zulassung BRAO"], "en-GB": ["barrister rights of audience", "solicitor role"]}, "warnings": ["„lawyer“ ist zu allgemein"]}""");
            if (p.Contains("Find up to 4 passages") && p.Contains("Rechtsanwalt"))
                return Task.FromResult("""{"items": [{"term": "Rechtsanwalt", "quote": "Der Rechtsanwalt ist ein unabhängiges Organ der Rechtspflege.", "note": "Stellung"}, {"term": "Rechtsanwalt", "quote": "Erfundenes Zitat, das nirgends steht.", "note": "x"}]}""");
            if (p.Contains("Find up to 4 passages"))
                return Task.FromResult("""{"items": [{"term": "barrister", "quote": "Barristers have rights of audience in all courts.", "note": "Vertretung"}]}""");
            return Task.FromResult("""{"source": {"term": "Rechtsanwalt", "meaningInContext": "Prozessvertreter vor dem Landgericht", "keyFeatures": ["Vertretung"], "system": "deutsches Recht"}, "candidates": [{"term": "barrister", "equivalence": "partial", "sharedFeatures": ["Vertretung vor Gericht"], "differences": ["in England getrennter Berufsstand"], "suitableWhen": "Prozessvertretung", "unsuitableWhen": "Beratung", "evidence": [1, 2, 9]}, {"term": "solicitor", "equivalence": "Partial", "sharedFeatures": ["Rechtsberatung"], "differences": ["eingeschränkte Vertretung"], "evidence": []}], "recommendation": {"term": "barrister", "strategy": "funktionale Entsprechung", "rendering": "counsel (barrister) for the claimant", "reasoning": "Im Kontext geht es um Prozessvertretung [2]."}, "warnings": []}""");
        }
    }

    private sealed class Search : ISearchProvider
    {
        public List<IReadOnlyList<string>> SiteCalls { get; } = new();
        public string Name => "x";
        public Task<List<SearchResult>> SearchAsync(string q, string l, int m, CancellationToken ct = default) =>
            Task.FromResult(new List<SearchResult> { new("Irgendwo", "https://blog.example/x", "") });
        public Task<List<SearchResult>> SearchInSitesAsync(string q, string l, IReadOnlyList<string> sites, int m, CancellationToken ct = default)
        {
            SiteCalls.Add(sites);
            return Task.FromResult(l.StartsWith("de")
                ? new List<SearchResult> { new("BRAO", "https://www.gesetze-im-internet.de/brao/__1.html", ""), new("Fremd", "https://andere.example/a", "") }
                : new List<SearchResult> { new("Barristers", "https://www.legislation.gov.uk/ukpga/2007/29", "") });
        }
    }

    private sealed class Fetcher : IPageFetcher
    {
        public string? LastSkipReason => null;
        public Task<string?> FetchTextAsync(string url, int maxChars, CancellationToken ct = default) =>
            Task.FromResult<string?>(new string('.', 220) + (url.Contains("gesetze")
                ? " § 1 Der Rechtsanwalt ist ein unabhängiges Organ der Rechtspflege. "
                : " Barristers have rights of audience in all courts, solicitors in some. "));
    }

    [Fact]
    public async Task ComparesConceptsAcrossLegalSystemsWithVerifiedEvidence()
    {
        var model = new Model();
        var search = new Search();
        var ctx = new AgentContext { Model = model, Search = search, Fetcher = new Fetcher() };
        var recht = SourceProfile.Defaults().First(p => p.Name == "Recht");

        var r = await new TermComparisonAgent(ctx).RunAsync(new TermQuery("Rechtsanwalt", "de-DE", "en-GB",
            "Der Kläger wird vor dem Landgericht durch seinen Rechtsanwalt vertreten.", "Zivilprozess", recht), 2,
            TestContext.Current.CancellationToken);

        // nur Fachseiten der jeweiligen Sprache (+ EU-Quellen)
        Assert.Contains(search.SiteCalls, s => s.Contains("gesetze-im-internet.de") && s.Contains("curia.europa.eu"));
        Assert.Contains(search.SiteCalls, s => s.Contains("legislation.gov.uk") && !s.Contains("gesetze-im-internet.de"));
        Assert.True(r.UsedSpecialistSites);
        Assert.DoesNotContain(r.Evidence, e => e.Url.Contains("andere.example"));      // fremde Seite herausgefiltert

        // erfundenes Zitat verworfen, echte Belege nummeriert
        Assert.Equal(2, r.Evidence.Count);
        Assert.DoesNotContain(r.Evidence, e => e.Quote.Contains("Erfundenes"));
        var barrister = r.Candidates.First(c => c.Term == "barrister");
        Assert.Equal(Equivalence.Partial, barrister.Equivalence);
        Assert.Equal([1, 2], barrister.Evidence);                                       // Beleg [9] gibt es nicht

        // Kontext, Rechtsordnungen und Belege gingen in die Anfragen ein
        Assert.Contains("vor dem Landgericht", model.Prompts[0]);
        Assert.Contains("Recht von England und Wales", model.Prompts[0]);
        Assert.Contains("[1] (de-DE) Rechtsanwalt", model.Prompts[^1]);

        var text = TermComparisonAgent.Format(r);
        Assert.Contains("EMPFEHLUNG: „barrister“ (funktionale Entsprechung)", text);
        Assert.Contains("teilweise Entsprechung", text);

        using var dir = new TempDir();
        using var tb = new Termbase(dir.File("t.sctb"));
        var id = TermComparisonAgent.SaveToTermbase(r, tb, "de-DE", "en-GB", "Recht");
        var concept = tb.GetConcept(id);
        Assert.Contains(concept.Terms, t => t.Text == "barrister" && t.Status == TermStatus.Preferred);
        Assert.Contains(concept.Terms, t => t.Text == "solicitor" && t.Status == TermStatus.Admitted);
        Assert.Contains("getrennter Berufsstand", concept.Definition);

        using var kb = new KnowledgeBase(dir.File("w.sckb"));
        TermComparisonAgent.SaveToKnowledge(r, kb, "en-GB", "Recht", "Anna");
        var entry = Assert.Single(kb.All());
        Assert.Equal(KnowledgeKind.Usage, entry.Kind);
        Assert.Contains("legislation.gov.uk", entry.SourceUrl);
    }

    [Fact]
    public void ProfilesRoundTripAsText()
    {
        var recht = SourceProfile.Defaults().First(p => p.Name == "Recht");
        var parsed = SourceProfile.ParseText(recht.ToText());
        Assert.Equal(recht.Languages.Count, parsed.Count);
        Assert.Equal(["curia.europa.eu"], recht.SitesFor("xx").ToList());
        Assert.DoesNotContain("isap.sejm.gov.pl", recht.SitesFor("pl-PL"));          // untersagt das Lesen per robots.txt
        Assert.Equal("polnisches Recht (Kodeks cywilny, k.p.c., k.k.)", recht.SystemFor("pl-PL"));
        Assert.Equal("beispiel.de", SourceProfile.CleanDomain("https://www.Beispiel.de/pfad"));
        Assert.True(SiteSearch.IsOnSite("https://www.legislation.gov.uk/x", ["legislation.gov.uk"]));
        Assert.False(SiteSearch.IsOnSite("https://fake-legislation.gov.uk.evil.com", ["legislation.gov.uk"]));
    }
}
