using System.Net;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Privacy;

/// <summary>
/// Letzte Sperre vor dem Netz: Keine Suchanfrage und kein Seitenabruf trägt eine geschützte Angabe des offenen Dokuments
/// hinaus – Name, Anschrift, E-Mail, Telefon, IBAN, Aktenzeichen, Schutzliste. Das gilt auch, wenn das Projekt nicht als
/// vertraulich markiert ist (für eine Übersetzungsrecherche braucht niemand die Anschrift im Netz), und auch für eine
/// Suchmaschine auf diesem Rechner: SearXNG fragt ihrerseits Suchmaschinen im Netz. Ein Datum ist keine solche Angabe –
/// Normen und Urteile werden mit Datum gesucht. Was der Nutzer unter „Nicht schützen“ einträgt, bleibt frei.
/// </summary>
public static class PrivacyGuard
{
    private static IReadOnlyList<string> _values = [];

    /// <summary>Geschützte Angaben des offenen Dokuments (setzt die Oberfläche beim Öffnen und nach Änderungen am Datenschutz).</summary>
    public static IReadOnlyList<string> Values
    {
        get => _values;
        set => _values = value ?? [];
    }

    /// <summary>Was nie ins Netz geht: alle Arten außer Datum, unabhängig von den Häkchen des Projekts.</summary>
    public static PrivacySettings ForWeb(PrivacySettings? project)
    {
        var s = (project ?? new PrivacySettings()).Clone();
        s.Names = s.Addresses = s.Emails = s.Phones = s.Iban = s.CaseNumbers = true;
        s.Dates = false;
        return s;
    }

    /// <summary>Die geschützten Angaben in diesen Texten (ohne Datum).</summary>
    public static List<string> Collect(IEnumerable<string> texts, PrivacySettings? project)
    {
        var settings = ForWeb(project);
        return texts.Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => SensitiveData.Find(t, settings))
            .Select(f => Normalize(f.Value))
            .Where(v => v.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Die erste geschützte Angabe in einer Anfrage oder null. Adressen werden dekodiert („Bahnhofstra%C3%9Fe+3a“);
    /// neben den Angaben des Dokuments zählen Anschrift, E-Mail, IBAN und die Schutzliste auch in fremdem Text.
    /// </summary>
    public static string? Find(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        // roh und dekodiert prüfen: „+49 30 …“ ist im Klartext ein Pluszeichen, in einer Adresse ein Leerzeichen
        var texts = new List<string> { Normalize(request) };
        try { texts.Add(Normalize(WebUtility.UrlDecode(request))); }
        catch (ArgumentException) { }
        foreach (var value in _values)
            if (texts.Any(text => Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase)))
                return value;
        var settings = ForWeb(PrivacyContext.Current);
        settings.Names = settings.Phones = settings.CaseNumbers = false;   // in Adressen und Treffern zu oft Fehlalarm
        return texts.SelectMany(text => SensitiveData.Find(text, settings)).Select(f => f.Value)
            .FirstOrDefault(v => !IsFreed(v, settings));
    }

    /// <summary>Steht der Wert (oder ein längerer, der ihn enthält) unter „Nicht schützen“?</summary>
    public static bool IsFreed(string value, PrivacySettings settings) =>
        settings.NotSensitive.Any(n => Regex.IsMatch(Normalize(n), $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(Normalize(value))}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase));

    /// <summary>Wirft, wenn eine Suchanfrage eine geschützte Angabe enthält.</summary>
    public static void CheckQuery(string service, string query)
    {
        if (Find(query) is { } value)
            throw new OutboundDeclinedException($"Nicht gesendet: Die Suchanfrage an {service} enthielt die geschützte Angabe „{value}“.");
    }

    /// <summary>Die Websuche mit dieser Sperre davor (null bleibt null).</summary>
    public static ISearchProvider? Wrap(ISearchProvider? search) => search switch
    {
        null or GuardedSearch => search,
        ICountrySearchProvider => new GuardedCountrySearch(search),
        _ => new GuardedSearch(search),
    };

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}

/// <summary>Websuche, die vor jeder Anfrage <see cref="PrivacyGuard.CheckQuery"/> prüft.</summary>
public class GuardedSearch(ISearchProvider inner) : ISearchProvider, IWrappedSearch
{
    public ISearchProvider Inner => inner;
    public string Name => inner.Name;

    public Task<List<SearchResult>> SearchAsync(string query, string language, int max, CancellationToken ct = default)
    {
        PrivacyGuard.CheckQuery(inner.Name, query);
        return inner.SearchAsync(query, language, max, ct);
    }

    public Task<List<SearchResult>> SearchInSitesAsync(string query, string language, IReadOnlyList<string> sites, int max, CancellationToken ct = default)
    {
        PrivacyGuard.CheckQuery(inner.Name, query);
        return inner.SearchInSitesAsync(query, language, sites, max, ct);
    }
}

/// <summary>Dasselbe für eine Suche, die nach Land und genauer Wortfolge suchen kann.</summary>
public sealed class GuardedCountrySearch(ISearchProvider inner) : GuardedSearch(inner), ICountrySearchProvider
{
    private ICountrySearchProvider Country => (ICountrySearchProvider)Inner;
    public int MaxPages => Country.MaxPages;

    public Task<SearchPage> SearchPhraseAsync(string phrase, string language, string country, int page, CancellationToken ct = default)
    {
        PrivacyGuard.CheckQuery(Inner.Name, phrase);
        return Country.SearchPhraseAsync(phrase, language, country, page, ct);
    }
}

/// <summary>Eine Suche, die eine andere umhüllt (für Prüfungen wie „ist es Wikipedia?“).</summary>
public interface IWrappedSearch
{
    ISearchProvider Inner { get; }
}
