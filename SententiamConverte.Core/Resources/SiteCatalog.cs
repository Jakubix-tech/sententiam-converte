using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Resources;

/// <summary>Woher ein Eintrag stammt: als Beleg tatsächlich genutzt oder von der KI als Fundort vorgeschlagen.</summary>
public enum SiteOrigin { Suggested, Evidence }

/// <summary>Eine Webseite in der Quellen-Datenbank: Sprachen, Themen, wie oft sie Belege lieferte, was man dort findet.</summary>
public sealed class SiteEntry
{
    public string Domain { get; set; } = "";
    public List<string> Languages { get; set; } = new();
    public List<string> Topics { get; set; } = new();
    public SiteOrigin Origin { get; set; }
    /// <summary>Wie oft die Seite als Beleg in einem Dossier stand.</summary>
    public int Uses { get; set; }
    /// <summary>Wie oft die KI sie als Fundort vorgeschlagen hat.</summary>
    public int Suggestions { get; set; }
    /// <summary>Was man dort findet (Vorschlag der KI) oder der Titel einer belegenden Seite.</summary>
    public string? Note { get; set; }
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    /// <summary>Art der Quelle (Behörde, Norm, Forum …); ältere Einträge: leer (dann an der Adresse eingeordnet).</summary>
    public string? Kind { get; set; }

    /// <summary>Wie oft eine von dieser Seite gestützte Benennung beim Bestätigen übernommen wurde.</summary>
    public int Accepted { get; set; }

    /// <summary>Wie oft sie ersetzt oder im Lektorat als falscher Fachbegriff bemängelt wurde.</summary>
    public int Rejected { get; set; }
}

/// <summary>
/// Quellen-Datenbank: Webseiten, auf denen sich bei Recherchen Belege fanden oder die die KI als Fundort nennt – mit
/// Sprache und Thema. Sie wächst mit jeder Recherche (über alle Projekte, im Datenordner) und fließt in künftige
/// Recherchen ein: Seiten zum Thema und zur Sprache werden gezielt durchsucht, belegte vor vorgeschlagenen.
/// </summary>
public static class SiteCatalog
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static readonly AsyncLocal<string?> LocalFile = new();

    /// <summary>Für Tests: in diesem Ablauf eine eigene Quellen-Datenbank in dieser Datei.</summary>
    public static void UseFileHere(string path) => LocalFile.Value = path;

    public static string FilePath => LocalFile.Value ?? Path.Combine(AppPaths.DataRoot, "quellen-datenbank.json");

    public static List<SiteEntry> Load()
    {
        lock (Lock)
        {
            if (!File.Exists(FilePath)) return new();
            try { return JsonSerializer.Deserialize<List<SiteEntry>>(File.ReadAllText(FilePath), Json) ?? new(); }
            catch (JsonException) { return new(); }
        }
    }

    private static void Save(List<SiteEntry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all.OrderBy(e => e.Domain).ToList(), Json));
    }

    /// <summary>„https://www.Example.org/a“ oder „www.example.org“ → „example.org“; null, wenn es keine Adresse ist.</summary>
    public static string? DomainOf(string urlOrDomain)
    {
        var s = urlOrDomain.Trim();
        if (s.Length == 0) return null;
        var host = Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var u) ? u.Host : null;
        if (host is null || !host.Contains('.') || host.Contains(' ')) return null;
        host = host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// <summary>
    /// Allgemeine Nachschlagewerke und Suchmaschinen (Wikipedia, Wiktionary …): liefern Belege, sind aber keine Fachseite,
    /// auf die sich eine Suche beschränken sollte. Ältere Einträge dazu bleiben in der Datei, werden aber nicht genutzt.
    /// </summary>
    public static bool IsGeneral(string domain) =>
        domain.EndsWith("wikipedia.org") || domain.EndsWith("wiktionary.org") || domain.EndsWith("wikidata.org") || domain.Contains("google.");

    private static string TopicKey(string topic) => topic.Trim().ToLowerInvariant();

    /// <summary>Themen eines Projekts: das Fachquellen-Profil (z. B. „Recht“) und das Fachgebiet des Projekts.</summary>
    public static List<string> Topics(string? topic, SourceProfile? profile) =>
        new[] { profile?.Name, topic }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => TopicKey(t!)).Distinct().ToList();

    private static SiteEntry Entry(List<SiteEntry> all, string domain)
    {
        var e = all.FirstOrDefault(x => x.Domain == domain);
        if (e == null) all.Add(e = new SiteEntry { Domain = domain });
        return e;
    }

    private static void Merge(List<string> list, IEnumerable<string> values)
    {
        foreach (var v in values.Where(v => v.Length > 0))
            if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
    }

    /// <summary>Belege eines Dossiers festhalten: jede Seite zählt einmal je Dossier.</summary>
    public static void RecordEvidence(IEnumerable<SourceRef> sources, IReadOnlyList<string> topics)
    {
        lock (Lock)
        {
            var all = Load();
            foreach (var group in sources.GroupBy(s => DomainOf(s.Url)).Where(g => g.Key != null && !IsGeneral(g.Key)))
            {
                var e = Entry(all, group.Key!);
                e.Origin = SiteOrigin.Evidence;
                e.Uses++;
                e.LastUsed = DateTime.UtcNow;
                e.Note ??= group.First().Title;
                e.Kind ??= group.Select(s => s.Kind).FirstOrDefault(k => k != null) ?? Agents.SourceKinds.Classify(group.First().Url);
                Merge(e.Languages, group.Select(s => s.Language));
                Merge(e.Topics, topics);
            }
            Save(all);
        }
    }

    /// <summary>Fundorte, die die KI vorschlägt (ungeprüft, bis sie einmal einen Beleg liefern).</summary>
    public static void RecordSuggestions(IEnumerable<SiteHint> hints, IReadOnlyList<string> topics)
    {
        lock (Lock)
        {
            var all = Load();
            foreach (var h in hints)
            {
                if (DomainOf(h.Domain) is not { } domain) continue;
                var e = Entry(all, domain);
                e.Suggestions++;
                e.LastUsed = DateTime.UtcNow;
                if (e.Origin == SiteOrigin.Suggested || e.Note == null) e.Note = h.What;
                Merge(e.Languages, [h.Language]);
                Merge(e.Topics, topics);
            }
            Save(all);
        }
    }

    /// <summary>
    /// Rückmeldung aus dem Bestätigen: Wurde eine Benennung übernommen, die diese Seiten stützten, steigt ihr Ruf; wurde sie
    /// ersetzt oder im Lektorat bemängelt, sinkt er. <paramref name="delta"/> −1 nimmt eine frühere Rückmeldung zurück.
    /// </summary>
    public static void RecordFeedback(IEnumerable<string> domains, bool accepted, int delta = 1)
    {
        lock (Lock)
        {
            var all = Load();
            foreach (var domain in domains.Select(DomainOf).OfType<string>().Distinct())
            {
                var e = Entry(all, domain);
                if (accepted) e.Accepted = Math.Max(0, e.Accepted + delta);
                else e.Rejected = Math.Max(0, e.Rejected + delta);
            }
            Save(all);
        }
    }

    /// <summary>
    /// Ruf einer Seite: Gewicht ihrer Art × (Belege + 3 × übernommen − 4 × ersetzt); was über ein halbes Jahr nichts mehr
    /// geliefert hat, zählt nur noch halb. Vorgeschlagene, noch unerprobte Seiten bekommen einen kleinen Vorschuss.
    /// </summary>
    public static double Reputation(SiteEntry e)
    {
        var decay = (DateTime.UtcNow - e.LastUsed).TotalDays > 180 ? 0.5 : 1.0;
        var kind = e.Kind ?? Agents.SourceKinds.Classify("https://" + e.Domain);
        return Agents.SourceKinds.Weight(kind) * (e.Uses + 3 * e.Accepted - 4 * e.Rejected + (e.Origin == SiteOrigin.Suggested ? 0.5 : 0)) * decay;
    }

    public static void Remove(string domain)
    {
        lock (Lock)
        {
            var all = Load();
            all.RemoveAll(e => e.Domain == domain);
            Save(all);
        }
    }

    /// <summary>
    /// Seiten für eine Sprache und ein Thema, beste zuerst: bewährt (belegt) vor vorgeschlagen, dann nach Ruf – der Art der
    /// Quelle, den Belegen und vor allem dem, was beim Bestätigen übernommen oder ersetzt wurde. Seiten mit schlechtem Ruf
    /// (mehr ersetzt als genutzt) fallen heraus. Ohne Themen-Treffer zählen nur Seiten, die mehrfach Belege lieferten.
    /// </summary>
    public static List<string> SitesFor(string language, IReadOnlyList<string> topics, int max = 8) =>
        Load().Where(e => !IsGeneral(e.Domain) && e.Languages.Any(l => Model.Languages.Matches(l, language)))
            .Where(e => e.Topics.Intersect(topics, StringComparer.OrdinalIgnoreCase).Any() || e.Uses >= 3)
            .Where(e => Reputation(e) > 0)
            .OrderByDescending(e => e.Origin).ThenByDescending(Reputation).ThenByDescending(e => e.Suggestions)
            .Take(max).Select(e => e.Domain).ToList();

    /// <summary>Fachquellen-Profil um die gelernten Seiten beider Sprachen ergänzen (das gespeicherte Profil bleibt unverändert).</summary>
    public static SourceProfile? Augment(SourceProfile? profile, IReadOnlyList<string> topics, string sourceLanguage, string targetLanguage)
    {
        var extra = new[] { sourceLanguage, targetLanguage }
            .Select(l => new LanguageSources { Language = l, System = profile?.SystemFor(l), Sites = SitesFor(l, topics).Where(s => profile?.SitesFor(l).Contains(s) != true).ToList() })
            .Where(l => l.Sites.Count > 0).ToList();
        if (extra.Count == 0) return profile;
        return new SourceProfile
        {
            Name = profile?.Name ?? "Gelernte Quellen",
            Description = profile?.Description,
            Languages = [.. profile?.Languages ?? [], .. extra],
        };
    }
}
