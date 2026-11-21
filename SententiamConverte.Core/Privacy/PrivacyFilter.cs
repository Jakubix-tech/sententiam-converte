using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Privacy;

/// <summary>Art einer geschützten Angabe; der Name steht im Platzhalter, z. B. [NAME_1].</summary>
public enum SensitiveKind { Name, Date, Case, Email, Phone, Iban, Term, Address }

/// <summary>Datenschutz eines Projekts (bzw. ohne Projekt die Voreinstellung): was geschützt wird und wovor.</summary>
public sealed class PrivacySettings
{
    /// <summary>Vertrauliche Inhalte: geschützte Angaben gehen nur als Platzhalter an Online-KI und ins TM.</summary>
    public bool Enabled { get; set; }

    /// <summary>Auch an KI auf diesem Rechner (eingebaute KI, Ollama …) nur Platzhalter schicken.</summary>
    public bool AlsoLocalAi { get; set; }

    /// <summary>Geschützte Angaben nicht ins TM schreiben (dort stehen Platzhalter).</summary>
    public bool KeepOutOfTm { get; set; } = true;

    public bool Names { get; set; } = true;
    public bool Dates { get; set; } = true;
    public bool CaseNumbers { get; set; } = true;
    public bool Emails { get; set; } = true;
    public bool Phones { get; set; } = true;
    public bool Iban { get; set; } = true;

    /// <summary>Anschriften: Straße mit Hausnummer (auch mit Postleitzahl und Ort), „ul. …“, „12 Baker Street“.</summary>
    public bool Addresses { get; set; } = true;

    /// <summary>Geschützte Begriffe von Hand: Namen, Firmen, Orte, Kennzeichen … (je Zeile einer).</summary>
    public List<string> Terms { get; set; } = new();

    /// <summary>
    /// Was die Erkennung findet, aber nicht schützenswert ist (z. B. ein Datum in einer Norm, „Herr der Ringe“): wird nicht
    /// ersetzt. Gilt nicht für die Schutzliste – was dort steht, wird immer geschützt.
    /// </summary>
    public List<string> NotSensitive { get; set; } = new();

    /// <summary>Der Nutzer wurde schon gefragt, ob das Projekt vertraulich ist (Vorschlag nicht wiederholen).</summary>
    public bool Asked { get; set; }

    /// <summary>
    /// Vertrauliches Projekt: Recherchen trotzdem ins Recherche-Gedächtnis (alle Projekte) übernehmen – ohne Satzzusammenhang
    /// und ohne Zitate aus eigenen Fachtexten. Nicht vertrauliche Projekte tun das immer.
    /// </summary>
    public bool ShareResearch { get; set; }

    public PrivacySettings Clone() => new()
    {
        Enabled = Enabled, AlsoLocalAi = AlsoLocalAi, KeepOutOfTm = KeepOutOfTm, Names = Names, Dates = Dates, CaseNumbers = CaseNumbers,
        Emails = Emails, Phones = Phones, Iban = Iban, Addresses = Addresses, Terms = Terms.ToList(), NotSensitive = NotSensitive.ToList(), Asked = Asked,
        ShareResearch = ShareResearch,
    };

    public bool MasksFor(AiProviderConfig config) => Enabled && (AlsoLocalAi || !config.IsLocal);
}

/// <summary>Eine gefundene geschützte Angabe; bei Namen nach einer Anrede auch das Geschlecht („male“/„female“).</summary>
public readonly record struct SensitiveSpan(int Index, int Length, SensitiveKind Kind, string Value, string? Gender = null);

/// <summary>
/// Findet geschützte Angaben – ohne Netz, auf diesem Rechner: Begriffe der Schutzliste, Namen nach Anrede oder Titel
/// („Herr Müller“, „Frau Dr. Schmidt“, „Pan Kowalski“), Datum, Aktenzeichen, E-Mail, Telefon, IBAN, Anschrift.
/// </summary>
public static partial class SensitiveData
{
    internal const string Months =
        "Januar|Jänner|Februar|März|April|Mai|Juni|Juli|August|September|Oktober|November|Dezember|" +
        "January|February|March|May|June|July|October|December|" +
        "stycznia|lutego|marca|kwietnia|maja|czerwca|lipca|sierpnia|września|października|listopada|grudnia|" +
        "janvier|février|mars|avril|mai|juin|juillet|août|septembre|octobre|novembre|décembre";

    [GeneratedRegex(@"\b\d{1,2}\.\s?\d{1,2}\.\s?(?:\d{4}|\d{2})\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}/\d{1,2}/\d{2,4}\b|\b\d{1,2}\.?\s(?:" + Months + @")\s\d{4}\b|\b(?:" + Months + @")\s\d{1,2},?\s\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    // Aktenzeichen: „2 O 123/24“, „12 Js 345/23“, „1 BvR 2345/19“, „VI ZR 123/20“, „I C 123/24“; dazu alles nach „Az.“, „Sygn.“ …
    [GeneratedRegex(@"\b(?:[IVX]{1,5}|\d{1,3})\s?[A-Z][A-Za-z]{0,4}\s?\d{1,6}/\d{2,4}\b|(?<=\b(?:Az\.?|Aktenzeichen|Geschäftszeichen|GZ|sygn\.\s?akt|Sygn\.|Case\sNo\.?|Ref\.)\s?:?\s)[A-Z0-9][\w./-]{2,30}(?:\s[\w./-]{1,12}){0,3}")]
    private static partial Regex CaseRegex();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\w/])(?:\+|00)\d{1,3}[\s/-]?(?:\(0\)\s?)?\d{2,5}[\s/-]?\d{3,}(?:[\s-]?\d{2,})*\b|(?<![\w/.])0\d{2,5}[\s/-]\d{3,}(?:[\s-]?\d{2,})*\b")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){3,7}(?:\s?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanRegex();

    // Anschrift: „Bahnhofstraße 3a, 12345 Musterstadt“, „Hauptstr. 12“, „Lange Straße 5“, „ul. Długa 5/3, 00-950 Warszawa“,
    // „221B Baker Street“, dazu eine Zeile nur aus Postleitzahl und Ort („12345 Musterstadt“)
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\p{Lu}[\p{L}'’.-]*?(?:straße|strasse|str\.|weg|gasse|platz|allee|damm|ufer|chaussee)|\p{Lu}[\p{L}'’-]+(?:\s\p{Lu}[\p{L}'’-]+)?\s(?:Straße|Strasse|Str\.|Weg|Gasse|Platz|Allee|Damm|Ufer|Chaussee))\s\d{1,4}\s?[a-zA-Z]?(?:\s?[-/]\s?\d{1,4}\s?[a-zA-Z]?)?(?![\p{L}\p{N}])(?:,?\s(?:D-)?\d{5}\s\p{Lu}[\p{L}'’-]+(?:\s(?:am|an\sder|im|in\sder|ob\sder|bei)\s\p{Lu}[\p{L}'’-]+)?)?" +
                    @"|\b(?:ul|al|pl|os)\.\s?\p{Lu}[\p{L}.'’-]*(?:\s\p{Lu}?[\p{L}.'’-]+){0,3}\s\d{1,4}[a-zA-Z]?(?:\s?/\s?\d{1,4}[a-zA-Z]?)?(?:,?\s\d{2}-\d{3}\s\p{Lu}[\p{L}-]+)?" +
                    @"|\b\d{1,5}[A-Za-z]?\s(?:\p{Lu}[\p{L}'’-]+\s){1,3}(?:Street|St\.|Road|Rd\.|Avenue|Ave\.|Lane|Ln\.|Drive|Way|Boulevard|Blvd\.|Place|Court|Crescent|Close|Terrace|Square)(?![\p{L}])" +
                    @"|^[ \t]*(?:D-)?\d{5}[ \t]\p{Lu}[\p{L}'’-]+(?:[ \t]\p{Lu}[\p{L}'’-]+)?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex AddressRegex();

    // Name nach Anrede/Titel; geschützt wird der Name, die Anrede bleibt (sie wird übersetzt)
    [GeneratedRegex(@"\b(?<title>Herrn?|Frau|Dr\.|Prof\.|Rechtsanwalt|Rechtsanwältin|RA|RAin|Notar|Notarin|Richter|Richterin|Mr\.?|Mrs\.?|Ms\.?|Pan|Pani|Pana|Panią|Panu|M\.|Mme|Monsieur|Madame)\s+(?:(?:Dr\.|Prof\.)\s+)*(?<name>\p{Lu}[\p{Ll}'’-]+(?:[\s-]\p{Lu}[\p{Ll}'’-]+){0,2})")]
    private static partial Regex TitledNameRegex();

    public static List<SensitiveSpan> Find(string text, PrivacySettings settings)
    {
        var found = new List<SensitiveSpan>();
        void Add(int index, int length, SensitiveKind kind, string? gender = null)
        {
            if (length <= 1) return;
            found.Add(new SensitiveSpan(index, length, kind, text.Substring(index, length), gender));
        }
        foreach (var term in settings.Terms.Select(t => t.Trim()).Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length))
            foreach (Match m in Regex.Matches(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?:s|es|’s|'s)?(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase))
                Add(m.Index, m.Length, SensitiveKind.Term);
        if (settings.Names)
            foreach (Match m in TitledNameRegex().Matches(text))
                Add(m.Groups["name"].Index, m.Groups["name"].Length, SensitiveKind.Name, GenderOf(m.Groups["title"].Value));
        if (settings.Emails) foreach (Match m in EmailRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Email);
        if (settings.Iban) foreach (Match m in IbanRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Iban);
        if (settings.Addresses) foreach (Match m in AddressRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Address);
        if (settings.CaseNumbers) foreach (Match m in CaseRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Case);
        if (settings.Dates) foreach (Match m in DateRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Date);
        if (settings.Phones) foreach (Match m in PhoneRegex().Matches(text)) Add(m.Index, m.Length, SensitiveKind.Phone);

        // vom Nutzer als nicht schützenswert markiert (die Schutzliste geht vor)
        if (settings.NotSensitive.Count > 0)
            found.RemoveAll(s => s.Kind != SensitiveKind.Term
                                 && settings.NotSensitive.Any(n => n.Trim().Equals(s.Value.Trim(), StringComparison.OrdinalIgnoreCase)));
        // Überlappungen: das zuerst gefundene (Schutzliste vor Mustern), bei gleicher Stelle das längere
        var result = new List<SensitiveSpan>();
        foreach (var s in found.OrderBy(s => s.Index).ThenByDescending(s => s.Length))
            if (result.All(r => s.Index >= r.Index + r.Length || s.Index + s.Length <= r.Index))
                result.Add(s);
        return result.OrderBy(s => s.Index).ToList();
    }

    /// <summary>Geschlecht aus der Anrede – damit die KI Namen richtig beugt, ohne sie zu kennen.</summary>
    private static string? GenderOf(string title) => title.TrimEnd('.') switch
    {
        "Herr" or "Herrn" or "Mr" or "Pan" or "Pana" or "Panu" or "Monsieur" or "M" or "Rechtsanwalt" or "Notar" or "Richter" => "male",
        "Frau" or "Mrs" or "Ms" or "Pani" or "Panią" or "Madame" or "Mme" or "Rechtsanwältin" or "RAin" or "Notarin" or "Richterin" => "female",
        _ => null,
    };

    public static string Placeholder(SensitiveKind kind, int number) => $"[{kind.ToString().ToUpperInvariant()}_{number}]";

    /// <summary>Platzhalter, auch wenn die KI sie leicht verändert („[Name 1]“, „[NAME-1]“, „【NAME_1】“).</summary>
    [GeneratedRegex(@"[\[【]\s*(NAME|DATE|CASE|EMAIL|PHONE|IBAN|TERM|ADDRESS)[\s_-]*(\d+)\s*[\]】]", RegexOptions.IgnoreCase)]
    public static partial Regex PlaceholderRegex();
}

/// <summary>
/// Zuordnung echter Werte zu Platzhaltern für eine Unterhaltung mit der KI: gleicher Wert, gleicher Platzhalter.
/// Hin: maskieren, zurück: die echten Werte wieder einsetzen.
/// </summary>
public sealed class PrivacyMap(PrivacySettings settings)
{
    private readonly Dictionary<string, string> _byValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _byPlaceholder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SensitiveKind, int> _counters = new();
    private readonly Dictionary<string, string> _genders = new();
    private readonly object _lock = new();

    /// <summary>Für die KI: Geschlecht der Personen hinter den Platzhaltern (aus der Anrede), z. B. „[NAME_1] male“.</summary>
    public string GenderHint()
    {
        lock (_lock)
            return _genders.Count == 0 ? "" : "Grammatical gender of the persons: " + string.Join(", ", _genders.Select(g => $"{g.Key} {g.Value}")) + ".";
    }

    public int Count
    {
        get { lock (_lock) return _byValue.Count; }
    }

    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var spans = SensitiveData.Find(text, settings);
        if (spans.Count == 0) return text;
        var sb = new System.Text.StringBuilder();
        var pos = 0;
        lock (_lock)
        {
            foreach (var s in spans)
            {
                sb.Append(text, pos, s.Index - pos);
                if (!_byValue.TryGetValue(s.Value, out var ph))
                {
                    var n = _counters[s.Kind] = _counters.GetValueOrDefault(s.Kind) + 1;
                    ph = SensitiveData.Placeholder(s.Kind, n);
                    _byValue[s.Value] = ph;
                    _byPlaceholder[ph] = s.Value;
                }
                if (s.Gender != null) _genders[ph] = s.Gender;
                sb.Append(ph);
                pos = s.Index + s.Length;
            }
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>Der Platzhalter („Tag“), den ein Wert in dieser Unterhaltung bzw. diesem Dokument bekommen hat.</summary>
    public string? PlaceholderFor(string value)
    {
        lock (_lock) return _byValue.GetValueOrDefault(value);
    }

    public string Unmask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return SensitiveData.PlaceholderRegex().Replace(text, m =>
        {
            var key = SensitiveData.Placeholder(Enum.Parse<SensitiveKind>(m.Groups[1].Value, true), int.Parse(m.Groups[2].Value));
            lock (_lock) return _byPlaceholder.TryGetValue(key, out var value) ? value : m.Value;
        });
    }

    /// <summary>Alle Zeichenketten in einem JSON-Objekt maskieren bzw. zurücksetzen (Werkzeugaufrufe).</summary>
    public JsonObject Mask(JsonObject o) => (JsonObject)Walk(o.DeepClone(), Mask)!;
    public JsonObject Unmask(JsonObject o) => (JsonObject)Walk(o.DeepClone(), Unmask)!;

    private static JsonNode? Walk(JsonNode? node, Func<string, string> f)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList()) obj[key] = Walk(obj[key]?.DeepClone(), f);
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++) arr[i] = Walk(arr[i]?.DeepClone(), f);
                return arr;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(f(s));
            default:
                return node;
        }
    }
}

/// <summary>
/// TM-Fassung ohne geschützte Angaben: Platzhalter je Art in der Reihenfolge des Auftretens ([NAME_1], [DATE_1] …),
/// in Ausgangs- und Zieltext getrennt gezählt. Ein späterer Treffer bekommt die Werte des aktuellen Segments.
/// </summary>
public static class TmPrivacy
{
    public static string Mask(string text, PrivacySettings settings)
    {
        var spans = SensitiveData.Find(text, settings);
        if (spans.Count == 0) return text;
        var sb = new System.Text.StringBuilder();
        var counters = new Dictionary<SensitiveKind, int>();
        var seen = new Dictionary<string, string>();
        var pos = 0;
        foreach (var s in spans)
        {
            sb.Append(text, pos, s.Index - pos);
            if (!seen.TryGetValue(s.Value, out var ph))
            {
                var n = counters[s.Kind] = counters.GetValueOrDefault(s.Kind) + 1;
                seen[s.Value] = ph = SensitiveData.Placeholder(s.Kind, n);
            }
            sb.Append(ph);
            pos = s.Index + s.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>Platzhalter eines TM-Eintrags mit den Werten des aktuellen Ausgangssatzes füllen (je Art in Reihenfolge).</summary>
    public static string Fill(string text, string currentSource, PrivacySettings settings)
    {
        if (!SensitiveData.PlaceholderRegex().IsMatch(text)) return text;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<SensitiveKind, int>();
        foreach (var s in SensitiveData.Find(currentSource, settings))
        {
            if (values.ContainsValue(s.Value)) continue;
            var n = counters[s.Kind] = counters.GetValueOrDefault(s.Kind) + 1;
            values[SensitiveData.Placeholder(s.Kind, n)] = s.Value;
        }
        return SensitiveData.PlaceholderRegex().Replace(text, m =>
        {
            var key = SensitiveData.Placeholder(Enum.Parse<SensitiveKind>(m.Groups[1].Value, true), int.Parse(m.Groups[2].Value));
            return values.TryGetValue(key, out var v) ? v : m.Value;
        });
    }
}
