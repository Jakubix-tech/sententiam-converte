using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Agents;

/// <summary>Datenbanken und Gedächtnis der Agenten: Projekt-TM, Terminologie, Wissen – durchsuchen und ergänzen.</summary>
public interface IAgentMemory
{
    /// <summary>Was die Datenbanken zu einer Frage wissen, als lesbarer Text (leer = nichts gefunden).</summary>
    string Search(string query, string language);

    /// <summary>Eine Erkenntnis dauerhaft speichern (für spätere Recherchen und das Team).</summary>
    void Remember(string title, string content, string language, string? sourceUrl);
}

/// <summary>Gedächtnis über eine Wissensdatenbank, optional mit TMs und Terminologien zum Nachschlagen.</summary>
public sealed class KnowledgeMemory(IKnowledgeBase store, IEnumerable<IKnowledgeBase>? knowledge = null,
    IEnumerable<ITermbase>? termbases = null, IEnumerable<ITranslationMemory>? tms = null,
    string? sourceLanguage = null, IReadOnlyList<string>? targetLanguages = null) : IAgentMemory
{
    public string Search(string query, string language)
    {
        var sb = new StringBuilder();
        foreach (var kb in (knowledge ?? [store]).Distinct())
            foreach (var e in kb.Search(null, query, 5))
                sb.AppendLine($"[Wissen, {e.Language}] {e.Title}: {e.Content}" + (e.SourceUrl != null ? $" (Quelle: {e.SourceUrl})" : ""));
        foreach (var tb in termbases ?? [])
            foreach (var c in tb.Lookup(language, query, 5))
                sb.AppendLine("[Terminologie] " + string.Join(" | ", c.Terms.Select(t => $"{t.Language}: {t.Text}" + (t.Status == Terminology.TermStatus.Forbidden ? " (verboten)" : "")))
                              + (c.Definition != null ? $" – {c.Definition}" : ""));
        if (sourceLanguage != null)
            foreach (var tm in tms ?? [])
                foreach (var target in targetLanguages ?? [])
                    foreach (var u in tm.Concordance(sourceLanguage, target, query, max: 3))
                        sb.AppendLine($"[TM {target}] {u.Source} → {u.Target}");
        return sb.ToString().Trim();
    }

    public void Remember(string title, string content, string language, string? sourceUrl) =>
        store.Add(new KnowledgeEntry
        {
            Title = title, Content = content, Language = language, Kind = KnowledgeKind.Note, SourceUrl = sourceUrl,
            Author = "Recherche-Agent", Confidence = 0.8,
        });
}

/// <summary>Ein Schritt der Recherche, wie die Oberfläche ihn zeigt.</summary>
public sealed record ResearchStep(int Number, string Thought, string Tool, string Arguments, string Observation);

/// <param name="Translation">Übersetzungsvorschlag, wenn die Aufgabe nach einer Übersetzung fragt.</param>
/// <param name="Note">Anmerkung des Übersetzers für die Leser der Übersetzung (in der Zielsprache), z. B. bei Rechtsbegriffen.</param>
public sealed record ResearchOutcome(string Answer, List<SourceRef> Sources, List<ResearchStep> Steps, Skill? LearnedSkill, bool Finished,
    string? Translation = null, string? Note = null, List<EvidenceCheck>? Evidence = null);

/// <summary>Beleg-Prüfung: Steht der vorgeschlagene Begriff wirklich in einer Quelle, die das Programm gelesen hat?</summary>
/// <param name="Source">wo er steht (Werkzeug und Quelle), sonst null</param>
/// <param name="Excerpt">die Stelle, kurz</param>
public sealed record EvidenceCheck(string Term, string? Source, string? Excerpt)
{
    public bool Found => Source != null;

    public override string ToString() => Found
        ? $"✔ „{Term}“ steht in: {Source} – {Excerpt}"
        : $"⚠ „{Term}“ kommt in keiner gelesenen Quelle vor – Vorschlag der KI ohne Beleg, bitte selbst prüfen.";
}

/// <summary>Die Stellen eines langen Textes (Gesetz, Urteil, Norm), an denen ein Begriff vorkommt.</summary>
public static class TextPassages
{
    /// <summary>
    /// Fundstellen mit Umgebung; findet die Wortfolge nicht genau, genügen die Wortanfänge (gebeugte Formen:
    /// „Verwaltungsaktes“, „decyzji“). Überlappende Stellen werden zusammengefasst.
    /// </summary>
    public static List<string> Find(string text, string find, int max = 5, int radius = 300)
    {
        var words = find.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var exact = string.Join(@"\s+", words.Select(System.Text.RegularExpressions.Regex.Escape));
        var stems = string.Join(@"\s+", words.Select(w => w.Length >= 5
            ? System.Text.RegularExpressions.Regex.Escape(w[..Math.Max(4, w.Length - 3)]) + @"\w*"
            : System.Text.RegularExpressions.Regex.Escape(w)));
        var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var hits = System.Text.RegularExpressions.Regex.Matches(text, exact, options).Select(m => m.Index).ToList();
        if (hits.Count == 0) hits = System.Text.RegularExpressions.Regex.Matches(text, stems, options).Select(m => m.Index).ToList();
        var passages = new List<string>();
        var lastEnd = -1;
        foreach (var hit in hits)
        {
            var start = Math.Max(0, hit - radius);
            if (start < lastEnd) continue;                       // liegt in der vorigen Stelle
            var end = Math.Min(text.Length, hit + radius);
            while (start > 0 && start > hit - radius - 40 && !char.IsWhiteSpace(text[start - 1])) start--;
            while (end < text.Length && end < hit + radius + 40 && !char.IsWhiteSpace(text[end])) end++;
            var passage = System.Text.RegularExpressions.Regex.Replace(text[start..end], @"\s+", " ").Trim();
            passages.Add((start > 0 ? "… " : "") + passage + (end < text.Length ? " …" : ""));
            lastEnd = end;
            if (passages.Count >= max) break;
        }
        return passages;
    }
}

/// <summary>
/// Recherche, die die KI selbst steuert: Sie entscheidet Schritt für Schritt, was sie sucht (Web, Wikipedia,
/// Wiktionary), welche Seite oder welchen Artikel sie liest, was sie in den Projekt-Datenbanken nachschlägt,
/// welchen Skill sie anwendet und was sie sich merkt – das Programm führt aus und liefert die Inhalte.
/// Seiten werden nur gelesen, wenn robots.txt und Nutzungsvorbehalte es erlauben. Nach einer erfolgreichen
/// Recherche kann die KI eine neue Vorgehensweise als Skill festhalten (Prinzip wie beim Hermes Agent).
/// Modelle mit Function Calling bekommen die Werkzeuge als echte Funktionen; alle anderen ein einfaches
/// JSON-Protokoll (ein Objekt je Schritt).
/// </summary>
public sealed class ResearchAgent(AgentContext ctx, SkillLibrary skills, IAgentMemory? memory)
{
    private sealed record Action(string Thought, string Tool, JsonObject Args);

    private sealed class Run
    {
        public required string SourceLanguage;
        public required IReadOnlyList<string> Targets;
        public readonly List<ResearchStep> Steps = new();
        public readonly List<SourceRef> Sources = new();
        public readonly HashSet<string> UsedSkills = new();
        public readonly Dictionary<string, int> Seen = new();
        public string? Answer;
        public string? Translation;
        public string? Note;
        public Skill? Suggested;
    }

    public async Task<ResearchOutcome> RunAsync(string task, string sourceLanguage, IReadOnlyList<string> targetLanguages,
        int maxSteps = 10, bool learn = true, bool saveResult = true, CancellationToken ct = default)
    {
        var run = new Run { SourceLanguage = sourceLanguage, Targets = targetLanguages, Suggested = SuggestSkill(task, skills.All()) };
        if (run.Suggested != null)
        {
            ctx.Say($"🧩 Skill (passt zur Aufgabe): {run.Suggested.Name}");
            run.UsedSkills.Add(run.Suggested.Name);
        }
        var native = false;
        if (ctx.Model is IToolChatModel toolModel)
        {
            try
            {
                await RunWithToolsAsync(toolModel, task, run, maxSteps, ct);
                native = true;
            }
            catch (NotSupportedException)
            {
                ctx.Say("(Das Modell nimmt keine Werkzeuge an – einfaches Protokoll.)");
            }
        }
        if (!native) await RunWithTextAsync(task, run, maxSteps, ct);

        var finished = run.Answer != null;
        var answer = run.Answer ?? "Die Recherche wurde ohne Ergebnis beendet.";
        ctx.Say(finished ? "✔ Fertig." : "Abgebrochen: Schrittgrenze erreicht.");
        var evidence = finished ? CheckEvidence(answer, run.Translation, run.Steps) : [];
        foreach (var e in evidence) ctx.Say("🔍 " + e);
        foreach (var s in run.UsedSkills) skills.RecordUse(s);

        if (finished && saveResult && memory != null)
        {
            var content = answer + (run.Translation != null ? $"\nÜbersetzung: {run.Translation}" : "")
                                 + (run.Note != null ? $"\nAnmerkung des Übersetzers: {run.Note}" : "")
                                 + string.Concat(evidence.Select(e => "\n" + e));
            memory.Remember(Shorten(task, 120), content, targetLanguages.FirstOrDefault() ?? sourceLanguage, run.Sources.FirstOrDefault()?.Url);
            ctx.Say("🧠 Ergebnis im Gedächtnis gespeichert.");
        }
        Skill? learned = null;
        if (finished && learn && run.Steps.Count(s => s.Tool != "finish") >= 3)
            learned = await LearnAsync(task, run.Steps, ct);
        return new ResearchOutcome(answer, run.Sources, run.Steps, learned, finished, run.Translation, run.Note, evidence);
    }

    // ---------- Function Calling ----------

    private static JsonObject Schema(params (string Name, string Description, bool Required, string[]? Enum)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            var o = new JsonObject { ["type"] = "string", ["description"] = p.Description };
            if (p.Enum != null) o["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)e).ToArray());
            properties[p.Name] = o;
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
        };
    }

    private static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new("database", "Search the project's terminology, translation memories and knowledge, including earlier research. Use this first.",
            Schema(("query", "term or question", true, null), ("language", "language code, e.g. de-DE", false, null))),
        new("search", "Search Wikipedia, Wiktionary or the web. Wikipedia results already show the article titles in the other languages. " +
                      "With site, the web search only covers that website (a domain such as sjp.pwn.pl) or the official websites of a field (e.g. Recht).",
            Schema(("query", "search words", true, null), ("language", "language of the search, e.g. pl-PL", true, null),
                   ("source", "where to search", true, ["wikipedia", "wiktionary", "web"]),
                   ("site", "optional, web only: a domain or a field", false, null))),
        new("article", "Read a Wikipedia or Wiktionary article; also lists its versions in the other languages (often the exact technical term).",
            Schema(("title", "article title", true, null), ("language", "language of the article", true, null),
                   ("source", "wikipedia or wiktionary", false, ["wikipedia", "wiktionary"]))),
        new("open", "Read a web page or PDF (only if its operator permits it). With find, only the passages containing these words are returned – use it for long texts such as laws.",
            Schema(("url", "https address", true, null), ("find", "optional: word or phrase to look for", false, null))),
        new("sources", "List the official and specialist websites for a field and language, the legal or professional system behind them, and the tools that query them directly.",
            Schema(("field", "e.g. Recht (law), Medizin, Technik, Sprache (language), EU", true, null), ("language", "language code, e.g. pl-PL", true, null))),
        new("law", "Search official legislation databases: de = German federal law, pl = Polish law, uk = UK legislation, eu = EU law in every official language. " +
                   "Accepts title words, short titles or citations such as \"§ 35 VwVfG\", \"Kodeks postępowania administracyjnego\", \"Regulation 2016/679\". Then read a result with open and find.",
            Schema(("jurisdiction", "legal system", true, ["de", "pl", "uk", "eu"]), ("query", "in the language of that legal system", true, null),
                   ("language", "eu only: language of the query", false, null))),
        new("dictionary", "Look a word up: German in the DWDS (entry, valid spelling, part of speech, frequency class, pronunciation), every language in Wiktionary; also names the national dictionaries.",
            Schema(("word", "word or expression", true, null), ("language", "language code", true, null))),
        new("add_source", "Remember an official or specialist website for a field and language, so that later research searches it too.",
            Schema(("field", "e.g. Recht", true, null), ("language", "language code, e.g. pl-PL", true, null), ("site", "domain, e.g. uokik.gov.pl", true, null),
                   ("system", "optional: the legal or professional system", false, null))),
        new("skill", "Load the instructions of a skill (a proven procedure).", Schema(("name", "skill name", true, null))),
        new("remember", "Store a verified finding for later research and the team.",
            Schema(("title", "short title", true, null), ("content", "the finding", true, null), ("language", "language code", false, null), ("url", "source URL", false, null))),
        new("finish", "Give the final answer with the URLs of the sources; for translation questions also the proposed translation and, where the concepts differ, a translator's note.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["answer"] = new JsonObject { ["type"] = "string", ["description"] = "final answer with the reasoning" },
                    ["sources"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "source URLs" },
                    ["translation"] = new JsonObject { ["type"] = "string", ["description"] = "optional: proposed translation in the target language" },
                    ["note"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "optional: translator's note for the readers of the translation, in the target language – what the term means in the source system and how it differs",
                    },
                },
                ["required"] = new JsonArray("answer"),
            }),
    ];

    private static readonly HashSet<string> StopStems =
    [
        "zwisc", "welch", "werde", "einer", "einen", "nicht", "diese", "durch", "unter", "sowie", "beide", "wirkl", "mache", "sollt",
        "könne", "etwas", "about", "which", "there", "their", "would", "could", "should", "these", "those", "where", "other",
    ];

    /// <summary>
    /// Passt ein Skill eindeutig zur Aufgabe (gemeinsame Wortstämme mit Name und Zweck), bekommt die KI sein Vorgehen
    /// gleich mit – kleine Modelle laden Skills sonst zu spät oder gar nicht.
    /// </summary>
    internal static Skill? SuggestSkill(string task, IEnumerable<Skill> all)
    {
        static HashSet<string> Stems(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"\p{L}{5,}").Select(m => m.Value[..5]).Where(x => !StopStems.Contains(x)).ToHashSet();
        var words = Stems(task);
        var ranked = all.Select(s => (Skill: s, Score: Stems(s.Name.Replace('-', ' ') + " " + s.Description).Count(words.Contains)))
            .OrderByDescending(x => x.Score).ToList();
        return ranked.Count > 0 && ranked[0].Score >= 2 && (ranked.Count == 1 || ranked[0].Score > ranked[1].Score) ? ranked[0].Skill : null;
    }

    private string Rules(Run run, int maxSteps) => $"""
        Source language {run.SourceLanguage}, target language(s) {string.Join(", ", run.Targets)}.
        Only state terms or facts you found in a tool result; cite their URLs. Prefer native texts in the target language.
        Quote at most short passages. You have at most {maxSteps} tool calls. Write your reasoning and the answer in {ctx.ExplanationLanguage}.
        Legal and administrative terms depend on the legal system: anchor the term in the source law (law), read the counterpart
        in the target law, compare both, then give a translation and a translator's note (skill rechtsbegriff-vergleichen).
        The sources tool names the official websites of a field; law and dictionary query official databases directly.
        A translator's note (note in finish) is written in the target language {run.Targets.FirstOrDefault() ?? run.SourceLanguage}.

        Skills (proven procedures; load one with the skill tool when it fits):
        {skills.Catalog()}
        {(run.Suggested is { } s ? $"\nRecommended procedure for this task (skill {s.Name}, already loaded) – follow it step by step:\n{s.Instructions}" : "")}
        """;

    private async Task RunWithToolsAsync(IToolChatModel model, string task, Run run, int maxSteps, CancellationToken ct)
    {
        var system = "You are a research agent for professional translators. You find and verify terminology and facts " +
                     "with the tools; the program executes each tool call and returns its result.\n" + Rules(run, maxSteps);
        var messages = new List<AgentMessage> { AgentMessage.User("Task: " + task) };
        var calls = 0;
        var nudges = 0;
        while (run.Answer == null)
        {
            ct.ThrowIfCancellationRequested();
            if (calls >= maxSteps)
            {
                if (nudges++ > 1) return;
                messages.Add(AgentMessage.User("The limit is reached. Call finish now with your best answer from what you found."));
            }
            var reply = await ctx.WaitAsync(model.CompleteWithToolsAsync(system, messages, Tools, ct), ct);
            if (reply.Text.Length > 0 && reply.Calls.Count > 1) ctx.Say($"💭 {reply.Text}");      // bei einem Aufruf meldet Step den Gedanken
            if (reply.Calls.Count == 0)
            {
                // Manche Modelle antworten am Ende einfach mit Text, manche schreiben das JSON-Protokoll
                if (ParseAction(reply.Text) is { } action && calls < maxSteps)
                {
                    messages.Add(new AgentMessage("assistant", reply.Text));
                    var obs = await Step(action, run, ++calls, ct);
                    if (run.Answer == null) messages.Add(AgentMessage.User($"Result ({action.Tool}):\n{obs}"));
                    continue;
                }
                if (reply.Text.Length > 20)
                {
                    run.Answer = reply.Text;
                    run.Steps.Add(new ResearchStep(calls + 1, "", "finish", "", ""));
                    return;
                }
                if (nudges++ > 2) return;
                messages.Add(new AgentMessage("assistant", reply.Text));
                messages.Add(AgentMessage.User("Use a tool, or call finish with your answer."));
                continue;
            }
            messages.Add(new AgentMessage("assistant", reply.Text, reply.Calls));
            foreach (var call in reply.Calls)
            {
                var obs = await Step(new Action(reply.Calls.Count == 1 ? reply.Text : "", call.Name.Trim().ToLowerInvariant(), call.Arguments), run, ++calls, ct);
                messages.Add(AgentMessage.Tool(call.Id, run.Answer != null ? "Done." : obs));
                if (run.Answer != null) return;
            }
        }
    }

    // ---------- Einfaches JSON-Protokoll ----------

    private async Task RunWithTextAsync(string task, Run run, int maxSteps, CancellationToken ct)
    {
        var system = $$"""
            You are a research agent for professional translators. You work step by step with tools; the program executes
            each tool and returns the result. Reply with exactly ONE JSON object per turn and nothing else:
            {"thought": "short reason for this step", "tool": "<tool name>", "args": { ... } }

            Tools:
            {{string.Join("\n", Tools.Select(t => $"- {t.Name} {ArgsHint(t)}: {t.Description}"))}}

            {{Rules(run, maxSteps)}}
            """;
        var messages = new List<ChatTurn> { new(true, "Task: " + task) };
        var failures = 0;
        for (var step = 1; step <= maxSteps + 1 && run.Answer == null; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (step == maxSteps + 1)
                messages.Add(new ChatTurn(true, "The step limit is reached. Reply now with the finish tool and your best answer from what you found."));
            var reply = await ctx.WaitAsync(ctx.Model.CompleteAsync(system, messages, ct), ct);
            messages.Add(new ChatTurn(false, reply));
            var action = ParseAction(reply);
            if (action == null)
            {
                if (++failures >= 3) throw new AiException("Die KI hält das Recherche-Protokoll nicht ein: " + OpenAiCompatibleChatModel.Shorten(reply));
                messages.Add(new ChatTurn(true, """Reply with exactly ONE JSON object: {"thought": "...", "tool": "<tool name>", "args": {...}}"""));
                step--;
                continue;
            }
            failures = 0;
            var obs = await Step(action, run, step, ct);
            if (run.Answer == null) messages.Add(new ChatTurn(true, $"Result ({action.Tool}):\n{obs}"));
        }
    }

    private static string ArgsHint(ToolSpec t) =>
        "{" + string.Join(", ", (t.Parameters["properties"] as JsonObject ?? new JsonObject()).Select(p => $"\"{p.Key}\": \"...\"")) + "}";

    private static Action? ParseAction(string reply)
    {
        try
        {
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json) is not JsonObject o) return null;
            var tool = ((string?)o["tool"] ?? (string?)o["action"] ?? (string?)o["name"] ?? "").Trim().ToLowerInvariant();
            if (tool.Length == 0 || Tools.All(t => t.Name != tool)) return null;
            var args = o["args"] as JsonObject ?? o["arguments"] as JsonObject ?? o["parameters"] as JsonObject ?? new JsonObject();
            // manche Modelle schreiben die Argumente direkt ins Objekt
            foreach (var key in new[] { "query", "language", "source", "title", "url", "answer", "sources", "content", "word", "jurisdiction",
                                        "field", "site", "find", "translation", "note", "system" })
                if (args[key] == null && o[key] != null) args[key] = o[key]!.DeepClone();
            if (tool == "skill" && args["name"] == null && o["skill"] != null) args["name"] = o["skill"]!.DeepClone();
            return new Action(((string?)o["thought"] ?? "").Trim(), tool, (JsonObject)args.DeepClone());
        }
        catch (System.Text.Json.JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // ---------- Werkzeuge ausführen ----------

    /// <summary>Einen Schritt ausführen und protokollieren; „finish“ setzt die Antwort.</summary>
    private async Task<string> Step(Action a, Run run, int number, CancellationToken ct)
    {
        if (a.Thought.Length > 0) ctx.Say($"💭 {a.Thought}");
        if (a.Tool == "finish")
        {
            run.Answer = (string?)a.Args["answer"] ?? "";
            string? Text(params string[] keys) =>
                keys.Select(k => a.Args[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).FirstOrDefault(s => s is { Length: > 0 });
            run.Translation = Text("translation", "proposal", "proposed_translation");
            run.Note = Text("note", "comment", "translator_note", "translators_note");
            var urls = a.Args["sources"] switch
            {
                JsonArray arr => arr.Select(u => (string?)u).OfType<string>().ToList(),
                JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList(),
                _ => [],
            };
            foreach (var url in urls.Where(u => u.StartsWith("http")))
                if (run.Sources.All(s => s.Url != url)) run.Sources.Add(new SourceRef("", url, url));
            run.Steps.Add(new ResearchStep(number, a.Thought, "finish", "", ""));
            return "";
        }

        // Dieselbe Aktion noch einmal? Dann auf das vorhandene Ergebnis verweisen (kleine Modelle drehen sich sonst im Kreis)
        var key = a.Tool + " " + a.Args.ToJsonString().ToLowerInvariant();
        string observation;
        if (run.Seen.TryGetValue(key, out var earlier))
        {
            observation = $"You already did exactly this in step {earlier}; its result is above. Use it, or do something different " +
                          "(for example read an article from the results, or search in the target language).";
            ctx.Say($"↺ Wiederholung von Schritt {earlier} übersprungen");
        }
        else
        {
            run.Seen[key] = number;
            observation = await Execute(a, run, ct);
            if (observation.Length > ctx.MaxCharsPerPage) observation = observation[..ctx.MaxCharsPerPage] + " …";
        }
        run.Steps.Add(new ResearchStep(number, a.Thought, a.Tool, a.Args.ToJsonString(), observation));
        return observation;
    }

    private async Task<string> Execute(Action a, Run run, CancellationToken ct)
    {
        string Arg(string key, string fallback = "") => (string?)a.Args[key] is { Length: > 0 } v ? v.Trim() : fallback;
        var language = Arg("language", run.SourceLanguage);
        var wanted = run.Targets.Append(run.SourceLanguage).Select(WikiApi.Code).Distinct().ToList();
        try
        {
            switch (a.Tool)
            {
                case "database":
                {
                    ctx.Say($"🗂 Datenbanken: {Arg("query")}");
                    var found = memory?.Search(Arg("query"), language) ?? "";
                    return found.Length > 0 ? found
                        : $"Nothing found in the project databases. Next: search Wikipedia in the source language ({run.SourceLanguage}) " +
                          "for the term – the results show the article titles in the other languages.";
                }
                case "search":
                {
                    var source = Arg("source", "web").ToLowerInvariant();
                    if (Arg("site") is { Length: > 0 } site && source is not ("wikipedia" or "wiktionary"))
                        return await SearchSitesAsync(site, Arg("query"), language, ct);
                    ctx.Say($"🔎 [{language}, {source}] {Arg("query")}");
                    if (source is "wikipedia" or "wiktionary")
                    {
                        var hits = await WikiApi.SearchAsync(source, language, Arg("query"), 8, ct);
                        if (hits.Count == 0)
                            return WikiApi.Code(language) == WikiApi.Code(run.SourceLanguage)
                                ? "No results. Try a more general or a related term."
                                : $"No results. Search terms must be in the language of the wiki: search the {run.SourceLanguage} Wikipedia with the original term instead.";
                        // Sprachversionen der besten Treffer gleich mitliefern – oft steht dort der gesuchte Fachbegriff
                        var versions = await WikiApi.VersionsAsync(source, language, hits.Take(3).Select(h => h.Title).ToList(), ct);
                        return string.Join("\n", hits.Select((r, i) =>
                        {
                            var line = $"{i + 1}. {r.Title} – {r.Url}\n   {r.Snippet}";
                            if (versions.TryGetValue(r.Title, out var v) && v.Where(x => wanted.Contains(x.Key)).ToList() is { Count: > 0 } w)
                                line += "\n   In other languages: " + string.Join("; ", w.Select(x => $"{x.Key}: {x.Value}"));
                            return line;
                        })) + (WikiApi.Code(language) == WikiApi.Code(run.SourceLanguage)
                            ? "\nTip: the titles in the target language are the usual technical terms; read one with the article tool to confirm it."
                            : "");
                    }
                    var web = ctx.Search ?? new WikipediaSearch();
                    if ((web is Privacy.IWrappedSearch w ? w.Inner : web) is WikipediaSearch) ctx.Say("   (keine Websuche eingerichtet – gesucht wird in Wikipedia)");
                    var results = await web.SearchAsync(Arg("query"), language, 8, ct);
                    return results.Count == 0 ? "No results."
                        : string.Join("\n", results.Select((r, i) => $"{i + 1}. {r.Title} – {r.Url}\n   {r.Snippet}"));
                }
                case "article":
                {
                    var source = Arg("source", "wikipedia").ToLowerInvariant();
                    ctx.Say($"📖 [{language}, {source}] {Arg("title")}");
                    var article = await WikiApi.ArticleAsync(source, language, Arg("title"), ctx.MaxCharsPerPage, ct);
                    if (article == null) return "No such article. Search first and use a title from the results.";
                    AddSource(run, language, article.Title, article.Url);
                    var versions = article.Versions.Where(v => wanted.Contains(v.Key)).Select(v => $"{v.Key}: {v.Value}").ToList();
                    return $"{article.Title} ({article.Url})\nVersions in other languages: {(versions.Count > 0 ? string.Join("; ", versions) : "none for the requested languages")}\n\n{article.Text}";
                }
                case "open":
                {
                    var url = Arg("url");
                    var find = Arg("find");
                    ctx.Say(find.Length > 0 ? $"🌐 {url} („{find}“)" : $"🌐 {url}");
                    var text = await ctx.Fetcher.FetchTextAsync(url, find.Length > 0 ? 3_000_000 : ctx.MaxCharsPerPage, ct);
                    if (text == null) return $"Not readable: {ctx.Fetcher.LastSkipReason ?? "unknown reason"}. Choose another source.";
                    AddSource(run, language, url, url);
                    if (find.Length == 0) return text;
                    var passages = TextPassages.Find(text, find, 5, Math.Clamp(ctx.MaxCharsPerPage / 12, 150, 600));
                    return passages.Count == 0
                        ? $"\"{find}\" does not occur in this text ({text.Length:N0} characters). Try another word form, or read the start:\n{text[..Math.Min(text.Length, 1200)]}"
                        : $"Passages with \"{find}\":\n" + string.Join("\n", passages.Select((x, i) => $"[{i + 1}] {x}"));
                }
                case "sources":
                {
                    var field = Arg("field", "Recht");
                    ctx.Say($"🏛 Fachquellen: {field} ({language})");
                    return DescribeSources(field, language);
                }
                case "law":
                {
                    var jurisdiction = Arg("jurisdiction", language);
                    var code = LawSources.Normalize(jurisdiction);
                    ctx.Say($"⚖ [{code ?? jurisdiction}] {Arg("query")}");
                    if (code == null)
                    {
                        var sites = SourceProfile.Find(ctx.Profiles, "Recht")?.SitesFor(jurisdiction) ?? [];
                        return $"No legislation database is connected for \"{jurisdiction}\" (available: de, pl, uk, eu). " +
                               (sites.Count > 0 ? $"Official sites: {string.Join(", ", sites)} – search them with search (source web, site) or open a known address."
                                                : "Find the official legislation portal with search.");
                    }
                    var languages = code == "eu"
                        ? new[] { Arg("language", run.SourceLanguage) }.Concat(run.Targets).Append(run.SourceLanguage).Distinct().ToList()
                        : [language];
                    var hits = await LawSources.SearchAsync(code, Arg("query"), languages, 6, ct);
                    if (hits.Count == 0)
                        return "Nothing found. Search with the official title or short title in the language of that legal system, or a citation such as " +
                               $"\"§ 35 VwVfG\". Main acts: {LawSources.MainActs(code)}.";
                    return string.Join("\n", hits.Select((h, i) => $"{i + 1}. {h.Title} [{h.Citation}] – {h.Url}" + (h.Note != null ? $"\n   {h.Note}" : ""))) +
                           $"\nNext: read it with open (url \"{hits[0].Url}\", find = the term you are researching) to see the definition.";
                }
                case "dictionary":
                {
                    var word = Arg("word", Arg("query"));
                    ctx.Say($"📚 [{language}] {word}");
                    var sb = new StringBuilder();
                    if (WikiApi.Code(language) == "de")
                    {
                        var dwds = await Dictionaries.DwdsAsync(word, ct);
                        sb.AppendLine(dwds.Describe(word));
                        if (dwds.Entries.Count > 0) AddSource(run, language, "DWDS: " + dwds.Entries[0].Lemma, dwds.Entries[0].Url);
                    }
                    var entry = await WikiApi.ArticleAsync("wiktionary", language, word, Math.Min(1500, ctx.MaxCharsPerPage), ct);
                    if (entry != null)
                    {
                        sb.AppendLine($"Wiktionary ({entry.Url}):\n{entry.Text}");
                        AddSource(run, language, entry.Title, entry.Url);
                    }
                    else sb.AppendLine("Wiktionary: no entry.");
                    var dictionaries = SourceProfile.Find(ctx.Profiles, "Sprache")?.SitesFor(language).Where(s => s != "wiktionary.org").ToList() ?? [];
                    if (dictionaries.Count > 0)
                        sb.AppendLine($"National dictionaries for {language}: {string.Join(", ", dictionaries)}" +
                                      (HasWebSearch ? " – search them with search (source web, site)." : "."));
                    return sb.ToString().Trim();
                }
                case "add_source":
                {
                    var site = SourceProfile.CleanDomain(Arg("site"));
                    if (!site.Contains('.') || site.Any(char.IsWhiteSpace)) return "Give a domain such as uokik.gov.pl.";
                    var field = Arg("field", "Recht");
                    var lang = Arg("language", run.Targets.FirstOrDefault() ?? run.SourceLanguage);
                    var profile = SourceProfile.Find(ctx.Profiles, field);
                    if (profile == null)
                    {
                        profile = new SourceProfile { Name = field.Trim(), Description = "von der Recherche-KI angelegt" };
                        ctx.Profiles.Add(profile);
                    }
                    var entry = profile.Languages.FirstOrDefault(l => string.Equals(l.Language, lang, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) profile.Languages.Add(entry = new LanguageSources { Language = lang });
                    if (Arg("system") is { Length: > 0 } system && string.IsNullOrEmpty(entry.System)) entry.System = system;
                    if (entry.Sites.Contains(site, StringComparer.OrdinalIgnoreCase)) return $"{site} is already listed for {profile.Name} ({lang}).";
                    entry.Sites.Add(site);
                    ctx.SaveProfiles?.Invoke();
                    ctx.Say($"📌 Neue Fachquelle: {site} ({profile.Name}, {lang})");
                    return $"Added {site} to {profile.Name} ({lang}).";
                }
                case "skill":
                {
                    var skill = skills.Get(Arg("name"));
                    if (skill == null) return "Unknown skill. Available:\n" + skills.Catalog();
                    ctx.Say($"🧩 Skill: {skill.Name}");
                    run.UsedSkills.Add(skill.Name);
                    return skill.Instructions;
                }
                case "remember":
                {
                    if (memory == null) return "No memory available.";
                    memory.Remember(Arg("title"), Arg("content"), language, Arg("url") is { Length: > 0 } u ? u : null);
                    ctx.Say($"🧠 Gemerkt: {Arg("title")}");
                    return "Stored.";
                }
                default:
                    return $"Unknown tool \"{a.Tool}\". Use one of: {string.Join(", ", Tools.Select(t => t.Name))}.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                       or System.Text.Json.JsonException or AiException)
        {
            ctx.Say($"   Fehler: {ex.Message}");
            return $"Error: {ex.Message}. Try something else.";
        }
    }

    private bool HasWebSearch => ctx.Search is { } s && s is not WikipediaSearch;

    /// <summary>Hinweise des Programms in Werkzeugergebnissen – sie sind kein Beleg.</summary>
    private static readonly string[] NoEvidence =
        ["Nothing found", "No results", "No such", "You already", "Unknown", "Error", "Not readable", "No legislation", "No source profile", "No websites", "Searching particular"];

    /// <summary>
    /// Prüft, ob der vorgeschlagene Begriff (Übersetzungsvorschlag, sonst die hervorgehobenen Begriffe der Antwort) in
    /// einem gelesenen Text vorkommt – Wikipedia-Artikel samt Sprachversionen, Webseiten, Gesetze, Wörterbücher,
    /// Projekt-Datenbanken. Gebeugte Formen zählen mit.
    /// </summary>
    internal static List<EvidenceCheck> CheckEvidence(string answer, string? translation, IReadOnlyList<ResearchStep> steps)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(translation))
            terms.AddRange(System.Text.RegularExpressions.Regex.Replace(translation, @"\([^)]*\)", " ")
                .Split([';', '/', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().Trim('.', ',', '"', '„', '“', '*')).Where(t => t.Length >= 3).Take(2));
        else
            terms.AddRange(System.Text.RegularExpressions.Regex.Matches(answer, @"„([^“”""]{3,60})[“”""]|""([^""]{3,60})""|\*\*([^*]{3,60})\*\*")
                .Select(m => m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).First(g => g.Success).Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        var readable = steps.Where(s => s.Tool is not ("finish" or "skill" or "remember" or "add_source" or "sources") && s.Observation.Length > 0
                                        && !NoEvidence.Any(n => s.Observation.StartsWith(n, StringComparison.Ordinal))).ToList();
        return terms.Select(term =>
        {
            foreach (var step in readable)
                if (TextPassages.Find(step.Observation, term, 1, 70) is [var excerpt]) return new EvidenceCheck(term, Where(step), excerpt);
            return new EvidenceCheck(term, null, null);
        }).ToList();
    }

    private static string Where(ResearchStep step)
    {
        string? Arg(string key)
        {
            try { return System.Text.Json.Nodes.JsonNode.Parse(step.Arguments)?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return step.Tool switch
        {
            "open" => Arg("url") ?? "Webseite",
            "article" => $"{(Arg("source") ?? "wikipedia") switch { "wiktionary" => "Wiktionary", _ => "Wikipedia" }} ({Arg("language")}) „{Arg("title")}“",
            "search" => $"Suchergebnis „{Arg("query")}“ ({(Arg("source") ?? "web") switch { "wikipedia" => "Wikipedia", "wiktionary" => "Wiktionary", _ => "Web" }}, {Arg("language")})",
            "law" => $"Rechtsdatenbank {Arg("jurisdiction")}: „{Arg("query")}“",
            "dictionary" => $"Wörterbuch ({Arg("language")}) „{Arg("word")}“",
            "database" => "Projekt-Datenbanken",
            _ => step.Tool,
        };
    }

    private static void AddSource(Run run, string language, string title, string url)
    {
        if (run.Sources.All(s => s.Url != url)) run.Sources.Add(new SourceRef(language, title, url));
    }

    /// <summary>Websuche auf bestimmten Seiten: einer Domain oder allen Fachseiten eines Themas.</summary>
    private async Task<string> SearchSitesAsync(string site, string query, string language, CancellationToken ct)
    {
        var sites = site.Contains('.')
            ? [SourceProfile.CleanDomain(site)]
            : SourceProfile.Find(ctx.Profiles, site)?.SitesFor(language).ToList() ?? [];
        if (sites.Count == 0) return $"No websites known for \"{site}\" in {language}. Use sources, or give a domain.";
        ctx.Say($"🔎 [{language}, {string.Join(", ", sites.Take(3))}{(sites.Count > 3 ? " …" : "")}] {query}");
        if (!HasWebSearch)
        {
            ctx.Say("   (Seitensuche braucht eine Websuche – Seite „KI einrichten“ → Websuche)");
            return "Searching particular websites needs a web search service (Brave, Tavily, SearXNG or Claude in the settings); none is set up. " +
                   "Instead use law for legislation, dictionary for words, Wikipedia, or open a known address on that site.";
        }
        var (results, restricted) = await SiteSearch.SearchAsync(ctx.Search!, query, language, sites, 8, ctx.Say, ct);
        if (results.Count == 0) return "No results.";
        return (restricted ? "" : "Nothing on those sites; general results:\n") +
               string.Join("\n", results.Select((r, i) => $"{i + 1}. {r.Title} – {r.Url}\n   {r.Snippet}"));
    }

    /// <summary>Für die KI: Fachseiten, System und direkte Werkzeuge zu einem Thema und einer Sprache.</summary>
    private string DescribeSources(string field, string language)
    {
        var profile = SourceProfile.Find(ctx.Profiles, field);
        if (profile == null)
            return $"No source profile for \"{field}\". Known fields: {string.Join(", ", ctx.Profiles.Select(p => p.Name))}. " +
                   "Find the competent authority with search (wikipedia) and add its website with add_source.";
        var sb = new StringBuilder($"{profile.Name}: {profile.Description}\n");
        var entries = profile.Languages.Where(l => l.Language == "*" || Languages.Matches(l.Language, language)).ToList();
        foreach (var l in entries)
            sb.AppendLine($"{(l.Language == "*" ? "all languages" : l.Language)}: {l.System ?? "–"} – sites: {string.Join(", ", l.Sites)}");
        if (entries.All(l => l.Language == "*"))
            sb.AppendLine($"No sites for {language} yet: find them (skill amtliche-quellen-finden) and add them with add_source.");
        if (profile.Name is "Recht" or "EU und Verwaltung" && LawSources.Normalize(language) is { } code)
            sb.AppendLine($"Direct database: law with jurisdiction \"{code}\".");
        if (profile.Name is "Recht" or "EU und Verwaltung")
            sb.AppendLine("EU law in every official language: law with jurisdiction \"eu\".");
        if (profile.Name == "Sprache")
            sb.AppendLine("Direct lookup: dictionary" + (WikiApi.Code(language) == "de" ? " (DWDS)." : " (Wiktionary)."));
        sb.Append(HasWebSearch
            ? $"Search these sites with search (source web, site = a domain or \"{profile.Name}\")."
            : "No web search service is set up, so these sites cannot be searched; use the direct tools or open a known address.");
        return sb.ToString();
    }

    // ---------- Lernen ----------

    /// <summary>Nach einer erfolgreichen Recherche: eine wiederverwendbare Vorgehensweise als neuen Skill festhalten.</summary>
    private async Task<Skill?> LearnAsync(string task, List<ResearchStep> steps, CancellationToken ct)
    {
        try
        {
            var trace = string.Join("\n", steps.Select(s => $"{s.Number}. {s.Tool} {s.Arguments} – {s.Thought}"));
            var reply = await ctx.Model.AskAsync(
                "You maintain a library of reusable research procedures for translators. Output only JSON.",
                $$"""
                Task: {{task}}
                Steps that led to the answer:
                {{trace}}

                Existing skills:
                {{skills.Catalog()}}

                If these steps show a reusable procedure for a whole class of similar tasks that the existing skills do not
                cover yet, describe it as a new skill (general, not specific to this one term), in {{ctx.ExplanationLanguage}}:
                {"name": "kebab-case-name", "description": "one sentence: when to use it", "instructions": "numbered steps using the tools"}
                Otherwise return {"name": null}.
                """, ct);
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json) is not JsonObject o) return null;
            var name = (string?)(o["name"] as JsonValue);
            var instructions = o["instructions"] switch
            {
                JsonArray arr => string.Join("\n", arr.Select((x, i) => $"{i + 1}. {x}")),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(instructions)) return null;
            name = SkillLibrary.NormalizeName(name);
            if (name.Length < 3 || skills.Get(name) is { Learned: false }) return null;      // mitgelieferte nicht überschreiben
            var skill = new Skill(name, ((string?)(o["description"] as JsonValue) ?? "").Trim(), instructions.Trim(), true, 0);
            skills.Save(skill);
            ctx.Say($"🧩 Neuer Skill gelernt: {skill.Name}");
            return skill;
        }
        catch (Exception ex) when (ex is AiException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ctx.Say("Kein neuer Skill: " + ex.Message);
            return null;
        }
    }

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] + " …" : s;
}
