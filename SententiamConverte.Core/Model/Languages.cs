namespace SententiamConverte.Core.Model;

public sealed record Language(string Code, string Name);

public static class Languages
{
    public static readonly IReadOnlyList<Language> Common =
    [
        new("de-DE", "Deutsch (Deutschland)"),
        new("de-AT", "Deutsch (Österreich)"),
        new("de-CH", "Deutsch (Schweiz)"),
        new("en-GB", "Englisch (UK)"),
        new("en-US", "Englisch (USA)"),
        new("pl-PL", "Polnisch"),
        new("fr-FR", "Französisch"),
        new("it-IT", "Italienisch"),
        new("es-ES", "Spanisch"),
        new("pt-PT", "Portugiesisch"),
        new("pt-BR", "Portugiesisch (Brasilien)"),
        new("nl-NL", "Niederländisch"),
        new("cs-CZ", "Tschechisch"),
        new("sk-SK", "Slowakisch"),
        new("hu-HU", "Ungarisch"),
        new("ro-RO", "Rumänisch"),
        new("uk-UA", "Ukrainisch"),
        new("ru-RU", "Russisch"),
        new("sv-SE", "Schwedisch"),
        new("da-DK", "Dänisch"),
        new("nb-NO", "Norwegisch"),
        new("fi-FI", "Finnisch"),
        new("tr-TR", "Türkisch"),
        new("el-GR", "Griechisch"),
        new("la", "Latein"),
        new("zh-CN", "Chinesisch (vereinfacht)"),
        new("ja-JP", "Japanisch"),
        new("ko-KR", "Koreanisch"),
        new("ar-SA", "Arabisch"),
        // weitere Sprachen und Varianten (Auswahllisten, Spracherkennung, freie Sprachdaten)
        new("en-IE", "Englisch (Irland)"),
        new("en-AU", "Englisch (Australien)"),
        new("en-CA", "Englisch (Kanada)"),
        new("fr-BE", "Französisch (Belgien)"),
        new("fr-CH", "Französisch (Schweiz)"),
        new("fr-CA", "Französisch (Kanada)"),
        new("it-CH", "Italienisch (Schweiz)"),
        new("es-MX", "Spanisch (Mexiko)"),
        new("nl-BE", "Niederländisch (Belgien)"),
        new("bg-BG", "Bulgarisch"),
        new("hr-HR", "Kroatisch"),
        new("sr-RS", "Serbisch"),
        new("sl-SI", "Slowenisch"),
        new("bs-BA", "Bosnisch"),
        new("mk-MK", "Mazedonisch"),
        new("sq-AL", "Albanisch"),
        new("be-BY", "Belarussisch"),
        new("lt-LT", "Litauisch"),
        new("lv-LV", "Lettisch"),
        new("et-EE", "Estnisch"),
        new("nn-NO", "Norwegisch (Nynorsk)"),
        new("is-IS", "Isländisch"),
        new("ga-IE", "Irisch"),
        new("mt-MT", "Maltesisch"),
        new("cy-GB", "Walisisch"),
        new("eu-ES", "Baskisch"),
        new("ca-ES", "Katalanisch"),
        new("gl-ES", "Galicisch"),
        new("lb-LU", "Luxemburgisch"),
        new("he-IL", "Hebräisch"),
        new("fa-IR", "Persisch"),
        new("ur-PK", "Urdu"),
        new("hi-IN", "Hindi"),
        new("bn-BD", "Bengalisch"),
        new("ta-IN", "Tamil"),
        new("th-TH", "Thailändisch"),
        new("vi-VN", "Vietnamesisch"),
        new("id-ID", "Indonesisch"),
        new("ms-MY", "Malaiisch"),
        new("fil-PH", "Filipino"),
        new("zh-TW", "Chinesisch (traditionell)"),
        new("ka-GE", "Georgisch"),
        new("hy-AM", "Armenisch"),
        new("az-AZ", "Aserbaidschanisch"),
        new("kk-KZ", "Kasachisch"),
        new("uz-UZ", "Usbekisch"),
        new("sw-KE", "Suaheli"),
        new("af-ZA", "Afrikaans"),
        new("am-ET", "Amharisch"),
        new("eo", "Esperanto"),
        new("rm-CH", "Rätoromanisch"),
    ];

    /// <summary>Hauptsprache ohne Region, klein geschrieben: "de-DE" → "de"; Dreibuchstaben-Codes wie "fra" → "fr".</summary>
    public static string Primary(string code)
    {
        var primary = code.Split('-', '_')[0].ToLowerInvariant();
        return ThreeLetter.GetValueOrDefault(primary, primary);
    }

    /// <summary>Code mit zweibuchstabiger Hauptsprache, Region bleibt: "fra" → "fr", "deu-AT" → "de-AT".</summary>
    public static string Normalize(string code)
    {
        var parts = code.Trim().Split('-', '_');
        return ThreeLetter.TryGetValue(parts[0].ToLowerInvariant(), out var two) ? string.Join('-', [two, .. parts[1..]]) : code.Trim();
    }

    /// <summary>
    /// Dreibuchstaben-Codes (ISO 639-2/3, auch die bibliografischen wie „ger“, „fre“) der Sprachen mit Zweibuchstaben-Code.
    /// Fest hinterlegt statt aus den Systemdaten, die z. B. auf Android fehlen können.
    /// </summary>
    private static readonly Dictionary<string, string> ThreeLetter = new()
    {
        ["deu"] = "de", ["ger"] = "de", ["eng"] = "en", ["fra"] = "fr", ["fre"] = "fr", ["pol"] = "pl", ["ita"] = "it", ["spa"] = "es",
        ["por"] = "pt", ["nld"] = "nl", ["dut"] = "nl", ["ces"] = "cs", ["cze"] = "cs", ["slk"] = "sk", ["slo"] = "sk", ["hun"] = "hu",
        ["ron"] = "ro", ["rum"] = "ro", ["ukr"] = "uk", ["rus"] = "ru", ["swe"] = "sv", ["dan"] = "da", ["nor"] = "no", ["nob"] = "nb",
        ["nno"] = "nn", ["fin"] = "fi", ["tur"] = "tr", ["ell"] = "el", ["gre"] = "el", ["bul"] = "bg", ["hrv"] = "hr", ["srp"] = "sr",
        ["slv"] = "sl", ["bos"] = "bs", ["mkd"] = "mk", ["mac"] = "mk", ["sqi"] = "sq", ["alb"] = "sq", ["bel"] = "be", ["lit"] = "lt",
        ["lav"] = "lv", ["est"] = "et", ["isl"] = "is", ["ice"] = "is", ["gle"] = "ga", ["mlt"] = "mt", ["cym"] = "cy", ["wel"] = "cy",
        ["eus"] = "eu", ["baq"] = "eu", ["cat"] = "ca", ["glg"] = "gl", ["ltz"] = "lb", ["roh"] = "rm", ["lat"] = "la", ["epo"] = "eo",
        ["heb"] = "he", ["ara"] = "ar", ["fas"] = "fa", ["per"] = "fa", ["urd"] = "ur", ["hin"] = "hi", ["ben"] = "bn", ["tam"] = "ta",
        ["tha"] = "th", ["vie"] = "vi", ["ind"] = "id", ["msa"] = "ms", ["may"] = "ms", ["zho"] = "zh", ["chi"] = "zh", ["jpn"] = "ja",
        ["kor"] = "ko", ["kat"] = "ka", ["geo"] = "ka", ["hye"] = "hy", ["arm"] = "hy", ["aze"] = "az", ["kaz"] = "kk", ["uzb"] = "uz",
        ["swa"] = "sw", ["afr"] = "af", ["amh"] = "am",
    };

    /// <summary>Gleiche Hauptsprache gilt als passend (de-AT findet de-DE-Einträge).</summary>
    public static bool Matches(string a, string b) => Primary(a) == Primary(b);

    public static string DisplayName(string code) =>
        Common.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Name
        ?? Common.FirstOrDefault(l => Matches(l.Code, code))?.Name.Split(' ')[0]
        ?? code;
}
