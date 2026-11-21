using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Agents;

/// <summary>Eine Notiz, aus der die Antwort schöpft: Wissenseintrag oder Stelle aus den eigenen Fachtexten.</summary>
public sealed record KnowledgeNote(int Number, string Origin, string Title, string Text, string? Quote, string? SourceTitle, string? SourceUrl, string? Language);

/// <summary>Antwort mit den Notizen, auf die sie sich stützt; <see cref="Missing"/> sagt, was im Wissen fehlt.</summary>
public sealed record KnowledgeAnswer(string Question, string Answer, bool Found, string? Missing, List<KnowledgeNote> Notes, List<int> Cited)
{
    /// <summary>Begriffe aus der Frage, zu denen keine Notiz etwas sagt – dazu gibt es noch keine Informationen.</summary>
    public List<string> UnknownTerms { get; init; } = [];

    /// <summary>Aussagen der Antwort ohne Verweis [n] – nicht aus dem Wissen belegt.</summary>
    public int UncitedStatements { get; init; }

    /// <summary>Zitierte Notizen ohne Link – nicht nachprüfbar.</summary>
    public List<int> CitedWithoutSource => Cited.Where(n => Notes.FirstOrDefault(x => x.Number == n) is { SourceUrl: null or "" }).ToList();

    /// <summary>Klare Aussage, wozu noch nichts im Wissen steht (null, wenn alles abgedeckt ist).</summary>
    public string? NotYetKnown => UnknownTerms.Count > 0
        ? $"Zu {string.Join(", ", UnknownTerms.Select(t => $"„{t}“"))} habe ich noch keine Informationen."
        : Found ? null
        : Missing is { Length: > 0 } m && m != Question ? $"Dazu habe ich noch keine Informationen: {m.TrimEnd('.')}." : "Dazu habe ich noch keine Informationen.";
}

/// <summary>
/// Fragen an das Wissen: sucht zur Frage die passenden Einträge der Wissensdatenbanken (über Sprachgrenzen hinweg) und
/// Stellen in den eigenen Fachtexten heraus und lässt die KI nur daraus antworten, jede Aussage mit [n] belegt. Steht
/// die Antwort nicht darin, sagt sie das und nennt, was fehlt – dann lässt sich gezielt nachrecherchieren. Weil das
/// Programm die Notizen auswählt, reicht auch ein kleines lokales Modell.
/// </summary>
public sealed partial class KnowledgeQa(AgentContext ctx)
{
    private sealed record Keywords(Dictionary<string, List<string>>? Terms);
    private sealed record Reply(string? Answer, bool? Found, string? Missing);

    public async Task<KnowledgeAnswer> AskAsync(string question, IReadOnlyList<IKnowledgeBase> bases, string? topic = null, int maxNotes = 10,
        CancellationToken ct = default)
    {
        question = question.Trim();
        // In welchen Sprachen das Wissen vorliegt – danach richten sich die Suchwörter
        var languages = bases.SelectMany(b => Safe(() => b.Browse(null, null, 0, 300))).Select(e => Languages.Primary(e.Language))
            .Where(l => l.Length > 0).GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(4).ToList();
        if (languages.Count == 0) languages = ["de", "en"];

        ctx.Say("Suche im Wissen …");
        var terms = new List<(string Language, string Term)>();
        var example = "{\"terms\": {" + string.Join(", ", languages.Select(l => $"\"{l}\": [\"…\"]")) + "}}";
        try
        {
            var kw = JsonExtract.Parse<Keywords>(await ctx.Model.AskAsync(
                "You prepare a search in a knowledge base for a translator. Output only JSON.",
                $$"""
                Question: {{question}}
                {{(string.IsNullOrWhiteSpace(topic) ? "" : "Subject: " + topic)}}
                Write the 3–6 most important search terms for this question in each of these languages: {{string.Join(", ", languages)}}.
                Use the specialist terms as they are used in each language; no stop words, no question words.
                Return: {{example}}
                """, ct));
            foreach (var (lang, words) in kw.Terms ?? [])
                foreach (var t in words ?? [])
                    if (t.Trim().Length > 1) terms.Add((Languages.Primary(lang), t.Trim()));
        }
        catch (AiException ex) { ctx.Say("Suchwörter nicht lesbar, suche mit der Frage selbst: " + ex.Message); }

        // Wissenseinträge: Frage und Suchwörter aller Sprachen zusammen, die besten je Datenbank
        var query = question + " " + string.Join(" ", terms.Select(t => t.Term)) + " " + topic;
        var entries = bases.SelectMany(b => Safe(() => b.Search(null, query, maxNotes)))
            .DistinctBy(e => (e.Title, e.Content)).ToList();
        // Einträge, die ein Suchwort wörtlich enthalten, zuerst
        entries = entries.OrderByDescending(e => terms.Count(t => ReferenceLibrary.Mentions(e.Title + " " + e.Content, t.Term)))
            .Take(maxNotes).ToList();

        var notes = new List<KnowledgeNote>();
        foreach (var e in entries)
            notes.Add(new KnowledgeNote(notes.Count + 1, "Wissen", e.Title, e.Content, e.Quote, e.SourceTitle, e.SourceUrl, e.Language));
        // Eigene Fachtexte: Stellen zu den Suchwörtern (je Wort eine, höchstens vier)
        if (ctx.Library is { } library)
            try
            {
                foreach (var (lang, term) in terms)
                {
                    if (notes.Count(n => n.Origin == "Eigene Fachtexte") >= 4) break;
                    foreach (var hit in library.FindPhrase(term, lang, 1))
                        if (notes.All(n => n.Text != hit.Quote))
                            notes.Add(new KnowledgeNote(notes.Count + 1, "Eigene Fachtexte", hit.Label, hit.Quote, null, hit.Label, hit.Uri, hit.Language));
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) { ctx.Say("Eigene Fachtexte nicht lesbar: " + ex.Message); }

        // Begriffe der Frage, zu denen keine Notiz etwas sagt
        var unknown = terms.Select(t => t.Term).Where(t => ReferenceLibrary.Mentions(question, t))
            .Where(t => !notes.Any(n => ReferenceLibrary.Mentions(n.Title + " " + n.Text, t)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if (notes.Count == 0)
        {
            ctx.Say("Im Wissen steht dazu nichts.");
            var none = new KnowledgeAnswer(question, "", false, question, notes, []) { UnknownTerms = unknown };
            return none with { Answer = none.NotYetKnown! };
        }

        ctx.Say($"{notes.Count} passende Notizen – die KI antwortet daraus …");
        var list = new StringBuilder();
        foreach (var n in notes)
            list.AppendLine($"[{n.Number}] ({n.Origin}{(n.Language is { Length: > 0 } l ? ", " + l : "")}{(n.SourceUrl is { Length: > 0 } ? "" : ", no source")}) {n.Title}: {n.Text}{(n.Quote is { Length: > 0 } q ? $" – „{q}“" : "")}");
        var raw = await ctx.Model.AskAsync(
            "You answer a translator's questions strictly from the numbered notes provided – never from your own knowledge. Output only JSON.",
            $$"""
            Question: {{question}}

            Notes:
            {{list}}
            Rules: Answer in the language of the question. Use ONLY facts from the notes and cite each statement with its note number,
            e.g. [2]; every sentence needs a number. Prefer notes that have a source. If the question asks for several things
            (what it is, methods, machines, materials …), answer each briefly.
            If the notes do not answer the question or only partly, say so plainly and describe in "missing" what is not covered.
            Keep it concise (at most about 250 words).
            Return: {"answer": "...", "found": true, "missing": ""}
            """, ct);
        Reply reply;
        try { reply = JsonExtract.Parse<Reply>(raw); }
        catch (AiException) { reply = new Reply(raw.Trim(), null, null); }       // kein JSON: die Antwort ist der Text selbst
        var answer = (reply.Answer ?? "").Trim();
        var cited = CitationRegex().Matches(answer).Select(m => int.Parse(m.Groups[1].Value)).Where(n => n >= 1 && n <= notes.Count).Distinct().Order().ToList();
        var found = (reply.Found ?? cited.Count > 0) && answer.Length > 0;
        var missing = string.IsNullOrWhiteSpace(reply.Missing) ? null : reply.Missing.Trim();
        var uncited = SentenceRegex().Split(answer).Count(s => s.Trim().Length > 25 && !CitationRegex().IsMatch(s));
        return new KnowledgeAnswer(question, answer.Length > 0 ? answer : "Keine Antwort erhalten.", found, missing ?? (found ? null : question), notes, cited)
        {
            UnknownTerms = unknown,
            UncitedStatements = uncited,
        };
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex CitationRegex();

    private static List<T> Safe<T>(Func<List<T>> query)
    {
        try { return query(); }
        catch (Exception ex) when (ex is Remote.RemoteException or Microsoft.Data.Sqlite.SqliteException or IOException) { return []; }
    }
}
