using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using WeCantSpell.Hunspell;

namespace SententiamConverte.Core.Qa;

public sealed record SpellingError(string Word, int Index, IReadOnlyList<string> Suggestions);

/// <summary>
/// Rechtschreibprüfung mit Hunspell-Wörterbüchern (dieselben wie in LibreOffice).
/// Wörterbücher werden auf Wunsch aus dem LibreOffice-Projekt geladen und lokal abgelegt;
/// eigene Wörter landen in einer Benutzerliste je Sprache.
/// </summary>
public sealed partial class SpellChecker
{
    private static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de-DE"] = "de/de_DE_frami", ["de-AT"] = "de/de_AT_frami", ["de-CH"] = "de/de_CH_frami",
        ["en-GB"] = "en/en_GB", ["en-US"] = "en/en_US", ["pl-PL"] = "pl_PL/pl_PL", ["fr-FR"] = "fr_FR/dictionaries/fr",
        ["it-IT"] = "it_IT/it_IT", ["es-ES"] = "es/es_ES", ["pt-PT"] = "pt_PT/pt_PT", ["pt-BR"] = "pt_BR/pt_BR",
        ["nl-NL"] = "nl_NL/nl_NL", ["cs-CZ"] = "cs_CZ/cs_CZ", ["sk-SK"] = "sk_SK/sk_SK", ["hu-HU"] = "hu_HU/hu_HU",
        ["ro-RO"] = "ro/ro_RO", ["uk-UA"] = "uk_UA/uk_UA", ["ru-RU"] = "ru_RU/ru_RU", ["sv-SE"] = "sv_SE/dictionaries/sv_SE",
        ["da-DK"] = "da_DK/da_DK", ["nb-NO"] = "no/nb_NO", ["tr-TR"] = "tr_TR/tr_TR", ["el-GR"] = "el_GR/el_GR",
    };

    private const string BaseUrl = "https://raw.githubusercontent.com/LibreOffice/dictionaries/master/";
    private readonly Dictionary<string, WordList> _loaded = new();
    private readonly Dictionary<string, HashSet<string>> _user = new();

    public string Folder { get; }

    public SpellChecker(string? folder = null)
    {
        Folder = folder ?? Path.Combine(AppPaths.DataRoot, "woerterbuecher");
        Directory.CreateDirectory(Folder);
    }

    public static IEnumerable<string> DownloadableLanguages => Sources.Keys;

    private string? Key(string language) =>
        Sources.Keys.FirstOrDefault(k => string.Equals(k, language, StringComparison.OrdinalIgnoreCase))
        ?? Sources.Keys.FirstOrDefault(k => Languages.Matches(k, language));

    private string DicPath(string key) => Path.Combine(Folder, key + ".dic");
    private string AffPath(string key) => Path.Combine(Folder, key + ".aff");
    private string UserPath(string key) => Path.Combine(Folder, key + ".benutzer.txt");

    public bool IsAvailable(string language) => Key(language) is { } k && File.Exists(DicPath(k)) && File.Exists(AffPath(k));

    public async Task DownloadAsync(string language, CancellationToken ct = default)
    {
        var key = Key(language) ?? throw new NotSupportedException($"Für {language} ist kein Wörterbuch hinterlegt.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var ext in new[] { ".aff", ".dic" })
        {
            var bytes = await http.GetByteArrayAsync(BaseUrl + Sources[key] + ext, ct);
            await File.WriteAllBytesAsync(Path.Combine(Folder, key + ext), bytes, ct);
        }
        _loaded.Remove(key);
    }

    private WordList? Load(string language)
    {
        var key = Key(language);
        if (key == null || !IsAvailable(language)) return null;
        if (!_loaded.TryGetValue(key, out var list))
        {
            list = WordList.CreateFromFiles(DicPath(key), AffPath(key));
            _loaded[key] = list;
        }
        return list;
    }

    private HashSet<string> UserWords(string language)
    {
        var key = Key(language) ?? language;
        if (_user.TryGetValue(key, out var set)) return set;
        set = File.Exists(UserPath(key))
            ? new HashSet<string>(File.ReadAllLines(UserPath(key)).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _user[key] = set;
        return set;
    }

    public void AddToUserDictionary(string language, string word)
    {
        var key = Key(language) ?? language;
        if (UserWords(language).Add(word)) File.AppendAllLines(UserPath(key), [word]);
    }

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}'’\-]*")]
    private static partial Regex WordRegex();

    /// <summary>Unbekannte Wörter im Text. Wörter in <paramref name="ignore"/> (z. B. Terminologie) gelten als richtig.</summary>
    public List<SpellingError> Check(string text, string language, ISet<string>? ignore = null, bool withSuggestions = true)
    {
        var list = Load(language);
        if (list == null) return [];
        var user = UserWords(language);
        var plain = TagText.StripTags(text);
        var errors = new List<SpellingError>();
        foreach (Match m in WordRegex().Matches(plain))
        {
            var w = m.Value.Trim('\'', '’', '-');
            if (w.Length < 2 || w.All(char.IsUpper) || user.Contains(w) || ignore?.Contains(w) == true) continue;
            if (list.Check(w)) continue;
            // zusammengesetzte Bindestrich-Wörter teilweise prüfen
            if (w.Contains('-') && w.Split('-').All(p => p.Length == 0 || list.Check(p) || user.Contains(p))) continue;
            errors.Add(new SpellingError(w, m.Index, withSuggestions ? list.Suggest(w).Take(5).ToList() : []));
        }
        return errors;
    }
}
