using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Privacy;

/// <summary>Art eines gefundenen Namens – sie entscheidet, ob er geschützt werden sollte.</summary>
public enum NameKind { Person, Address, Place, Company, OwnBrand, Product, Institution, Country, Other }

/// <summary>Ein gefundener Name mit Einordnung und Empfehlung (schützen oder nicht) samt Begründung.</summary>
public sealed record NameCandidate(string Name, NameKind Kind, bool Protect, string Reason);

/// <summary>Werkzeuge rund um geschützte Angaben: übersehen Namen finden, Überblick, vorhandene TMs bereinigen.</summary>
public static class PrivacyScanner
{
    /// <summary>Länder (deutsch, englisch, polnisch, französisch) – nie schützen, sie werden übersetzt („Polen“ → „Polska“).</summary>
    private static readonly Lazy<HashSet<string>> CountryNames = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Deutschland", "Polen", "Österreich", "Schweiz", "Frankreich", "Italien", "Spanien", "Portugal", "Niederlande", "Belgien",
            "Luxemburg", "Tschechien", "Slowakei", "Ungarn", "Rumänien", "Ukraine", "Russland", "Schweden", "Norwegen", "Dänemark",
            "Finnland", "Großbritannien", "England", "Irland", "USA", "Vereinigte Staaten", "Kanada", "Mexiko", "China", "Japan",
            "Türkei", "Griechenland", "Kroatien", "Slowenien", "Litauen", "Lettland", "Estland", "Bulgarien", "Serbien", "Europa", "EU",
        };
        foreach (var c in System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures))
        {
            try
            {
                var r = new System.Globalization.RegionInfo(c.Name);
                set.Add(r.EnglishName);
                set.Add(r.NativeName);
            }
            catch (ArgumentException) { }
        }
        return set;
    });

    public static bool IsCountry(string name) => CountryNames.Value.Contains(name.Trim());

    /// <summary>
    /// Gefundene Namen einordnen (mit einer KI auf diesem Rechner – sie sieht die Namen samt Satz). Geschützt werden
    /// sollten Personen, Anschriften, kleine Wohnorte, eigene Firmen, Marken und Projekte, kleine Arbeitgeber; nicht aber
    /// Software und Produkte, große Marken, Länder und Arten von Schulen oder Einrichtungen – die trägt die Übersetzung,
    /// und was übersetzt werden muss, darf nie hinter einem Platzhalter verschwinden. Feste Regeln ergänzen die KI:
    /// Länder, Produkte und Einrichtungsarten werden nie geschützt. Ohne Antwort der KI: schützen (vorsichtig).
    /// </summary>
    public static async Task<List<NameCandidate>> ClassifyNamesAsync(IChatModel localModel, IReadOnlyList<string> names, IReadOnlyList<string> texts,
        string explanationLanguage = "Deutsch", IProgress<string>? log = null, CancellationToken ct = default)
    {
        var plain = texts.Select(TagText.StripTags).Where(t => t.Trim().Length > 0).ToList();
        string Context(string name)
        {
            var t = plain.FirstOrDefault(x => x.Contains(name, StringComparison.Ordinal)) ?? "";
            var i = t.IndexOf(name, StringComparison.Ordinal);
            if (i < 0) return "";
            var from = Math.Max(0, i - 60);
            return t.Substring(from, Math.Min(t.Length - from, name.Length + 120)).Replace('\n', ' ');
        }
        var judged = new Dictionary<string, NameCandidate>(StringComparer.OrdinalIgnoreCase);
        var batches = names.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(25).ToList();
        for (var b = 0; b < batches.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Ordne Namen ein ({b + 1}/{batches.Count}) …");
            var list = string.Join("\n", batches[b].Select(n => $"- \"{n}\" – {Context(n)}"));
            try
            {
                var reply = await localModel.AskAsync("You protect personal data in documents before they are translated. Output only JSON.",
                    $$"""
                    These names were found in a document that will be translated, possibly with an online AI. A protected name is replaced
                    by a placeholder and comes back unchanged – so only protect what identifies a person and stays the same in a translation.
                    Protect: names of people, street addresses, small places of residence, the author's own company, brands and projects,
                    small employers or clients that could identify a person.
                    Do NOT protect: software, products and brands of large companies (e.g. Autodesk Inventor, MariaDB, Tesla), countries,
                    regions, large cities that are not part of an address, kinds of schools or institutions (e.g. "Berufskolleg für
                    Fachinformatiker"), public authorities – they carry meaning the translation needs, and whatever must be translated
                    (country names, kinds of institutions) must never be hidden.
                    For each name: kind (person|address|place|company|own_brand|product|institution|country|other), protect (true/false),
                    reason (a few words in {{explanationLanguage}}).
                    Return: {"items": [{"name": "...", "kind": "...", "protect": true, "reason": "..."}]}

                    Names, each with the passage where it occurs:
                    {{list}}
                    """, ct);
                if (JsonExtract.Find(reply) is { } json && JsonNode.Parse(json)?["items"] is JsonArray items)
                    foreach (var o in items.OfType<JsonObject>())
                    {
                        var name = ((string?)(o["name"] as JsonValue))?.Trim();
                        if (name == null || !batches[b].Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        var kind = ((string?)(o["kind"] as JsonValue))?.Trim().ToLowerInvariant() switch
                        {
                            "person" => NameKind.Person,
                            "address" => NameKind.Address,
                            "place" => NameKind.Place,
                            "company" => NameKind.Company,
                            "own_brand" => NameKind.OwnBrand,
                            "product" => NameKind.Product,
                            "institution" => NameKind.Institution,
                            "country" => NameKind.Country,
                            _ => NameKind.Other,
                        };
                        var protect = o["protect"] is JsonValue p && p.TryGetValue<bool>(out var v) ? v : true;
                        judged[name] = new NameCandidate(batches[b].First(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)), kind, protect,
                            ((string?)(o["reason"] as JsonValue))?.Trim() ?? "");
                    }
            }
            catch (Exception ex) when (ex is AiException or System.Text.Json.JsonException or InvalidOperationException) { log?.Report("Einordnung fehlgeschlagen: " + ex.Message); }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n =>
        {
            var c = judged.GetValueOrDefault(n) ?? new NameCandidate(n, NameKind.Other, true, "nicht eingeordnet – vorsichtshalber schützen");
            if (IsCountry(n)) c = c with { Kind = NameKind.Country };
            // was übersetzt werden muss oder nichts über eine Person verrät, nie schützen
            return c.Kind is NameKind.Country or NameKind.Product or NameKind.Institution ? c with { Protect = false } : c;
        }).ToList();
    }

    /// <summary>
    /// Namen von Personen, Firmen, Orten usw. mit einer KI auf diesem Rechner finden – Vorschläge für die Schutzliste.
    /// Darf nur mit einer lokalen KI laufen, denn hier geht der ungeschützte Text an das Modell.
    /// </summary>
    public static async Task<List<string>> FindNamesAsync(IChatModel localModel, IEnumerable<string> texts, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var all = string.Join("\n", texts.Select(TagText.StripTags).Where(t => t.Trim().Length > 0));
        var chunks = new List<string>();
        for (var i = 0; i < all.Length; i += 3000) chunks.Add(all.Substring(i, Math.Min(3000, all.Length - i)));
        var found = new List<string>();
        for (var k = 0; k < chunks.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Suche Namen ({k + 1}/{chunks.Count}) …");
            var reply = await localModel.AskAsync("You find personal data in documents. Output only JSON.",
                $$"""
                List every name of a person, company, organisation, authority, street address and place of residence in this
                text, copied exactly as written (one entry per name, no duplicates, no common nouns, no dates or numbers).
                Return: {"items": ["…"]}
                === TEXT
                {{chunks[k]}}
                === END
                """, ct);
            if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json)?["items"] is not JsonArray items) continue;
            foreach (var item in items.Select(i => ((string?)(i as JsonValue))?.Trim()).OfType<string>())
                if (item.Length >= 2 && chunks[k].Contains(item, StringComparison.Ordinal) && !found.Contains(item, StringComparer.OrdinalIgnoreCase))
                    found.Add(item);
        }
        return found;
    }

    /// <summary>Was in den Texten geschützt würde – je Angabe Art und Häufigkeit.</summary>
    public static List<(SensitiveKind Kind, string Value, int Count)> Detect(IEnumerable<string> texts, PrivacySettings settings) =>
        texts.SelectMany(t => SensitiveData.Find(t, settings))
            .GroupBy(s => (s.Kind, s.Value))
            .Select(g => (g.Key.Kind, g.Key.Value, g.Count()))
            .OrderBy(x => x.Kind).ThenByDescending(x => x.Item3).ToList();

    /// <summary>
    /// Ausgangs- und Zieltext für das TM: Platzhalter statt geschützter Angaben. Was im Ausgangstext geschützt ist,
    /// wird im Zieltext auch dann ersetzt, wenn dort keine Anrede davorsteht („Müller“ nach „Herr Müller“).
    /// </summary>
    public static (string Source, string Target) MaskPair(string source, string target, PrivacySettings settings)
    {
        var maskedSource = TmPrivacy.Mask(source, settings);
        var sourceValues = SensitiveData.Find(source, settings).Select(s => s.Value).Distinct().ToList();
        var extended = settings.Clone();
        extended.Terms.AddRange(sourceValues);
        var maskedTarget = TmPrivacy.Mask(target, extended);
        // Platzhalter im Ziel auf die Art des Ausgangs abbilden (Terms → ursprüngliche Art)
        var kinds = SensitiveData.Find(source, settings).GroupBy(s => s.Value).ToDictionary(g => g.Key, g => g.First().Kind);
        if (kinds.Count > 0)
        {
            var targetSpans = SensitiveData.Find(target, extended);
            var counters = new Dictionary<SensitiveKind, int>();
            var seen = new Dictionary<string, string>();
            var sb = new System.Text.StringBuilder();
            var pos = 0;
            foreach (var s in targetSpans)
            {
                sb.Append(target, pos, s.Index - pos);
                var value = sourceValues.FirstOrDefault(v => s.Value.StartsWith(v, StringComparison.OrdinalIgnoreCase)) ?? s.Value;
                var kind = kinds.TryGetValue(value, out var k) ? k : s.Kind;
                if (!seen.TryGetValue(value, out var ph))
                {
                    var n = counters[kind] = counters.GetValueOrDefault(kind) + 1;
                    seen[value] = ph = SensitiveData.Placeholder(kind, n);
                }
                sb.Append(ph);
                pos = s.Index + s.Length;
            }
            sb.Append(target, pos, target.Length - pos);
            maskedTarget = sb.ToString();
        }
        return (maskedSource, maskedTarget);
    }

    /// <summary>Vorhandene TM-Einträge bereinigen: geschützte Angaben durch Platzhalter ersetzen (auch im Satzzusammenhang).</summary>
    public static int Anonymize(ITranslationMemory tm, PrivacySettings settings)
    {
        var changed = 0;
        foreach (var u in tm.All().ToList())
        {
            var (s, t) = MaskPair(u.Source, u.Target, settings);
            var prev = u.ContextPrevious == null ? null : TmPrivacy.Mask(u.ContextPrevious, settings);
            var next = u.ContextNext == null ? null : TmPrivacy.Mask(u.ContextNext, settings);
            if (s == u.Source && t == u.Target && prev == u.ContextPrevious && next == u.ContextNext) continue;
            tm.Delete(u.Id);
            tm.Add(u.SourceLanguage, u.TargetLanguage, s, t, u.Origin, u.Context, u.Created, prev, next);
            changed++;
        }
        return changed;
    }
}
