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
    ];

    /// <summary>Hauptsprache ohne Region, klein geschrieben: "de-DE" → "de".</summary>
    public static string Primary(string code) =>
        (code.Split('-', '_')[0]).ToLowerInvariant();

    /// <summary>Gleiche Hauptsprache gilt als passend (de-AT findet de-DE-Einträge).</summary>
    public static bool Matches(string a, string b) => Primary(a) == Primary(b);

    public static string DisplayName(string code) =>
        Common.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Name
        ?? Common.FirstOrDefault(l => Matches(l.Code, code))?.Name.Split(' ')[0]
        ?? code;
}
