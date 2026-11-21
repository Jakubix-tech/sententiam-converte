using System.Text.Json;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Projects;

/// <summary>Was eine Korrektur ausdrückt.</summary>
public enum CorrectionKind
{
    /// <summary>Wortwahl („Benutzer“ → „Nutzer“) – kann eine Regel werden.</summary>
    Wording,
    /// <summary>Andere Anrede (Pan → Pani, Sie → du) – gehört in den Steckbrief, nicht in eine Regel.</summary>
    Address,
    /// <summary>Anderes Geschlecht des Sprechers (zrobiłem → zrobiłam) – ebenfalls Steckbrief.</summary>
    Speaker,
}

public enum RuleState { Collecting, Suggested, Active, Rejected }

/// <summary>Gelernte Wortwahl: in der Zielsprache „From“ durch „To“ ersetzen.</summary>
public sealed class LearnedRule
{
    public string Language { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Count { get; set; }
    public RuleState State { get; set; }
    public DateTime Last { get; set; } = DateTime.UtcNow;
    /// <summary>Ein Beispielsatz (Übersetzung nach der Korrektur).</summary>
    public string? Example { get; set; }
}

/// <summary>Gewohnheit je Kunde und Textsorte: so wurde zuletzt angesprochen bzw. gesprochen.</summary>
public sealed class LearnedDefault
{
    public string Client { get; set; } = "";
    public string TextType { get; set; } = "";
    public string Language { get; set; } = "";
    public AddressForm Address { get; set; }
    public SpeakerForm Speaker { get; set; }
}

/// <summary>Ein Hinweis aus einer Korrektur: Regel vorschlagen oder den Steckbrief anpassen.</summary>
public sealed record LearningSuggestion(CorrectionKind Kind, LearnedRule? Rule, AddressForm? Address, SpeakerForm? Speaker);

/// <summary>
/// Aus Korrekturen lernen (abschaltbar): Das Programm vergleicht beim Bestätigen die Übersetzung mit dem ursprünglichen
/// Vorschlag (KI, MÜ, TM). Wiederholt sich dieselbe Änderung der Wortwahl, schlägt es eine Regel vor; bestätigte Regeln
/// bekommt die KI als Vorgabe, und die QA meldet Verstöße. Änderungen nur der Anrede oder des Geschlechts werden keine
/// Regel – sie gelten für ein Dokument und gehören in dessen Steckbrief. Gewohnheiten je Kunde und Textsorte (z. B.
/// „Anleitungen: Imperativ“) dienen als Vorgabe für neue Steckbriefe.
/// </summary>
public static partial class CorrectionLearning
{
    /// <summary>Ab so vielen gleichen Korrekturen wird eine Regel vorgeschlagen.</summary>
    public const int Threshold = 2;

    private static readonly object Lock = new();
    private static Store? _store;
    private static string? _path;

    /// <summary>Wird von der Oberfläche gesetzt (Einstellung); ohne Oberfläche (Server, API) aus.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Speicherort (Vorgabe: im Datenordner).</summary>
    public static string StorePath
    {
        get => _path ?? Path.Combine(Ai.AppPaths.DataRoot, "gelernt.json");
        set { lock (Lock) { _path = value; _store = null; } }
    }

    /// <summary>Neuer Hinweis für die Oberfläche (aus dem Thread, der bestätigt hat).</summary>
    public static event Action<LearningSuggestion>? Suggested;

    private sealed class Store
    {
        public List<LearnedRule> Rules { get; set; } = new();
        public List<LearnedDefault> Defaults { get; set; } = new();
    }

    private static Store Data
    {
        get
        {
            if (_store != null) return _store;
            try { _store = File.Exists(StorePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) ?? new() : new(); }
            catch (JsonException) { _store = new(); }
            return _store;
        }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyList<LearnedRule> Rules
    {
        get { lock (Lock) return Data.Rules.ToList(); }
    }

    public static IEnumerable<LearnedRule> Active(string language)
    {
        lock (Lock) return Data.Rules.Where(r => r.State == RuleState.Active && Languages.Matches(r.Language, language)).ToList();
    }

    // ---------- Korrekturen auswerten ----------

    [GeneratedRegex(@"\p{L}")] private static partial Regex Letter();

    /// <summary>Geänderte Stellen: aufeinanderfolgende gelöschte und eingefügte Wörter als Paar (höchstens vier Wörter).</summary>
    public static List<(string From, string To)> Changes(string before, string after)
    {
        var parts = WordDiff.Compare(TagText.StripTags(before), TagText.StripTags(after));
        var result = new List<(string, string)>();
        var deleted = new List<string>();
        var inserted = new List<string>();
        void Flush()
        {
            var from = string.Concat(deleted).Trim();
            var to = string.Concat(inserted).Trim();
            if (from.Length > 0 && to.Length > 0 && Letter().IsMatch(from + to)
                && from.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 && to.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
                result.Add((from, to));
            deleted.Clear();
            inserted.Clear();
        }
        foreach (var part in parts)
        {
            if (part.Kind == WordDiff.Kind.Deleted) deleted.Add(part.Text);
            else if (part.Kind == WordDiff.Kind.Inserted) inserted.Add(part.Text);
            else if (part.Text.Trim().Length > 0) Flush();
            else if (deleted.Count > 0 || inserted.Count > 0) { deleted.Add(part.Text); inserted.Add(part.Text); }   // Leerzeichen innerhalb einer Änderung
        }
        Flush();
        return result;
    }

    [GeneratedRegex(@"(łem|łam|łeś|łaś|ł|ła|łby|łaby)$")] private static partial Regex PlPast();

    /// <summary>Ist die Änderung nur eine andere Anrede oder ein anderes Geschlecht des Sprechers?</summary>
    public static CorrectionKind Classify(string from, string to, string language)
    {
        var a = AddressForms.Detect(from, language);
        var b = AddressForms.Detect(to, language);
        if (b.Count > 0 && !a.SetEquals(b)) return CorrectionKind.Address;
        if (Languages.Primary(language) == "pl")
        {
            // zrobiłem ↔ zrobiłam, był ↔ była: gleicher Stamm, andere Endung der Vergangenheit
            var f = from.Trim();
            var t = to.Trim();
            var stemF = PlPast().Replace(f, "");
            var stemT = PlPast().Replace(t, "");
            if (stemF.Length > 1 && stemF == stemT && PlPast().IsMatch(f) && PlPast().IsMatch(t) && f != t) return CorrectionKind.Speaker;
        }
        return CorrectionKind.Wording;
    }

    /// <summary>Beim Bestätigen: Vorschlag und endgültige Übersetzung vergleichen und lernen.</summary>
    public static void Record(string language, string suggestion, string final)
    {
        if (!Enabled || suggestion == final) return;
        var hints = new List<LearningSuggestion>();
        lock (Lock)
        {
            foreach (var (from, to) in Changes(suggestion, final))
            {
                var kind = Classify(from, to, language);
                if (kind == CorrectionKind.Address)
                {
                    var form = AddressForms.Detect(to, language).FirstOrDefault();
                    hints.Add(new LearningSuggestion(kind, null, form, null));
                    continue;
                }
                if (kind == CorrectionKind.Speaker)
                {
                    var female = to.Trim().EndsWith("łam") || to.Trim().EndsWith("ła") || to.Trim().EndsWith("łaś") || to.Trim().EndsWith("łaby");
                    hints.Add(new LearningSuggestion(kind, null, null, female ? SpeakerForm.Female : SpeakerForm.Male));
                    continue;
                }
                var rule = Data.Rules.FirstOrDefault(r => Languages.Matches(r.Language, language) && r.From == from && r.To == to);
                if (rule == null) Data.Rules.Add(rule = new LearnedRule { Language = Languages.Primary(language), From = from, To = to });
                rule.Count++;
                rule.Last = DateTime.UtcNow;
                rule.Example = TagText.StripTags(final);
                if (rule.State == RuleState.Collecting && rule.Count >= Threshold)
                {
                    rule.State = RuleState.Suggested;
                    hints.Add(new LearningSuggestion(CorrectionKind.Wording, rule, null, null));
                }
            }
            Save();
        }
        foreach (var h in hints) Suggested?.Invoke(h);
    }

    public static void SetState(LearnedRule rule, RuleState state)
    {
        lock (Lock)
        {
            var stored = Data.Rules.FirstOrDefault(r => r.Language == rule.Language && r.From == rule.From && r.To == rule.To);
            if (stored == null) Data.Rules.Add(stored = rule);
            stored.State = state;
            Save();
        }
    }

    public static void Remove(LearnedRule rule)
    {
        lock (Lock)
        {
            Data.Rules.RemoveAll(r => r.Language == rule.Language && r.From == rule.From && r.To == rule.To);
            Save();
        }
    }

    /// <summary>Vorgaben für die KI aus den aktiven Regeln (null, wenn keine).</summary>
    public static string? Instructions(string language)
    {
        if (!Enabled) return null;
        var rules = Active(language).Take(30).ToList();
        return rules.Count == 0 ? null : string.Join("\n", rules.Select(r => $"- write \"{r.To}\" instead of \"{r.From}\""));
    }

    /// <summary>QA: Übersetzungen, die eine aktive Regel verletzen.</summary>
    public static IEnumerable<QaIssue> Check(BilingualDocument doc)
    {
        if (!Enabled) yield break;
        var rules = Active(doc.TargetLanguage).ToList();
        if (rules.Count == 0) yield break;
        foreach (var s in doc.Segments.Where(s => !s.Locked && s.Target.Length > 0))
        {
            var text = TagText.StripTags(s.Target);
            foreach (var r in rules)
                if (Regex.IsMatch(text, $@"(?<!\p{{L}}){Regex.Escape(r.From)}(?!\p{{L}})"))
                    yield return new QaIssue(s.Id, QaSeverity.Warning, "Gelernte Regel", $"Statt „{r.From}“ lieber „{r.To}“ (aus Ihren Korrekturen gelernt).");
        }
    }

    // ---------- Gewohnheiten je Kunde und Textsorte ----------

    /// <summary>Bestätigter Steckbrief: Anrede und Sprecher als Vorgabe für diesen Kunden und diese Textsorte merken.</summary>
    public static void RememberDefault(string? client, string? textType, string language, AddressForm address, SpeakerForm speaker)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(textType) || address == AddressForm.Unknown) return;
        lock (Lock)
        {
            var key = (client ?? "").Trim();
            var type = textType.Trim().ToLowerInvariant();
            var lang = Languages.Primary(language);
            var d = Data.Defaults.FirstOrDefault(x => x.Client == key && x.TextType == type && x.Language == lang);
            if (d == null) Data.Defaults.Add(d = new LearnedDefault { Client = key, TextType = type, Language = lang });
            d.Address = address;
            d.Speaker = speaker;
            Save();
        }
    }

    /// <summary>Gelernte Vorgabe für einen neuen Steckbrief (zuerst für diesen Kunden, sonst allgemein).</summary>
    public static LearnedDefault? DefaultFor(string? client, string? textType, string language)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(textType)) return null;
        var type = textType.Trim().ToLowerInvariant();
        var lang = Languages.Primary(language);
        lock (Lock)
            return Data.Defaults.FirstOrDefault(x => x.Client == (client ?? "").Trim() && x.TextType == type && x.Language == lang)
                   ?? Data.Defaults.FirstOrDefault(x => x.Client.Length == 0 && x.TextType == type && x.Language == lang);
    }
}
