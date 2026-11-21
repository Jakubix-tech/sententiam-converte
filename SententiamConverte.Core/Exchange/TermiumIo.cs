using System.IO.Compression;
using System.Text;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;

namespace SententiamConverte.Core.Exchange;

/// <summary>
/// TERMIUM Plus (offene Daten der kanadischen Regierung, Open Government Licence – Canada): ZIP je Fachgebiet mit
/// CSV-Dateien, je Zeile ein Begriff auf Englisch und Französisch, teils Spanisch und Portugiesisch. Übernommen werden
/// Benennung, Synonyme und Abkürzungen mit Status („correct“ = bevorzugt bzw. zugelassen, „avoid“/„incorrect“ = nicht
/// verwenden), die Definition und das Fachgebiet. Belegstellen (CONT) bleiben weg.
/// </summary>
public static class TermiumIo
{
    /// <summary>Spalten je Sprache: Benennung, ihre Angaben, Abkürzung, ihre Angaben, Synonyme, ihre Angaben, Belege, Fachgebiet.</summary>
    private sealed record Columns(string Term, string TermParam, string Abbr, string AbbrParam, string Syn, string SynParam, string[] Support, string Subject);

    private static readonly Dictionary<string, Columns> ByLanguage = new()
    {
        ["en"] = new("TERM_EN", "TERM_EN_PARAMETER", "ABBREVIATION_EN", "ABBREVIATION_EN_PARAMETER", "SYNONYMS_EN", "SYNONYMS_EN_PARAMETERS",
            ["TEXTUAL_SUPPORT_1_EN", "TEXTUAL_SUPPORT_2_EN", "TEXTUAL_SUPPORT_3_EN"], "SUBJECT_EN"),
        ["fr"] = new("TERME_FR", "TERME_FR_PARAMETRE", "ABBREVIATION_FR", "ABBREVIATION_FR_PARAMETRE", "SYNONYMES_FR", "SYNONYMES_FR_PARAMETRE",
            ["JUSTIFICATION_1_FR", "JUSTIFICATION_2_FR", "JUSTIFICATION_3_FR"], "DOMAINE_FR"),
        ["es"] = new("TERME_TERM_ES", "TERME_TERM_PARAM_ES", "ABBR_ES", "ABBR_PARAM_ES", "SYNO_ES", "SYNO_PARAM_ES",
            ["JUST_TEXTSUPP_1_ES", "JUST_TEXTSUPP_2_ES", "JUST_TEXTSUPP_3_ES"], "DOM_SUBJ_ES"),
        ["pt"] = new("TERME_TERM_PT", "TERME_TERM_PARAM_PT", "ABBR_PT", "ABBR_PARAM_PT", "SYNO_PT", "SYNO_PARAM_PT",
            ["JUST_TEXTSUPP_1_PT", "JUST_TEXTSUPP_2_PT", "JUST_TEXTSUPP_3_PT"], "DOM_SUBJ_PT"),
    };

    public static bool Supports(string language) => ByLanguage.ContainsKey(Languages.Primary(language));

    /// <summary>Einträge mit Benennungen in beiden Sprachen in die Termbank übernehmen; Ergebnis: Zahl der Einträge.</summary>
    public static int Import(Stream zip, ITermbase termbase, string sourceLanguage, string targetLanguage,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var src = ByLanguage[Languages.Primary(sourceLanguage)];
        var tgt = ByLanguage[Languages.Primary(targetLanguage)];
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        long seen = 0;

        IEnumerable<(IReadOnlyList<(string Lang, string Text, TermStatus Status)> Terms, string? Definition, string? Domain, string? Source)> Concepts()
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string[]? header = null;
                foreach (var record in Csv(reader))
                {
                    ct.ThrowIfCancellationRequested();
                    if (header == null)
                    {
                        header = record;
                        continue;
                    }
                    if (++seen % 2000 == 0) progress?.Report(seen);
                    string Get(string column)
                    {
                        var i = Array.IndexOf(header, column);
                        return i >= 0 && i < record.Length ? record[i].Trim() : "";
                    }
                    var srcTerms = Terms(Get, src, sourceLanguage);
                    var tgtTerms = Terms(Get, tgt, targetLanguage);
                    if (srcTerms.Count == 0 || tgtTerms.Count == 0) continue;
                    var definition = Definition(Get, tgt) ?? Definition(Get, src);
                    var domain = Get(tgt.Subject) is { Length: > 0 } d ? d : Get(src.Subject);
                    yield return ([.. srcTerms, .. tgtTerms], definition, domain.Length > 0 ? domain : null, "TERMIUM Plus");
                }
            }
            progress?.Report(seen);
        }

        if (termbase is Termbase local) return local.AddConcepts(Concepts());
        var count = 0;
        foreach (var c in Concepts())
        {
            termbase.AddConcept(c.Terms, c.Definition, c.Domain, c.Source);
            count++;
        }
        return count;
    }

    private static List<(string Lang, string Text, TermStatus Status)> Terms(Func<string, string> get, Columns c, string language)
    {
        var list = new List<(string Lang, string Text, TermStatus Status)>();
        void Add(string text, string param, TermStatus normal)
        {
            text = text.Replace("\"\"", "\"").Trim();
            if (text.Length == 0 || list.Any(t => t.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) return;
            list.Add((language, text, Avoid(param) ? TermStatus.Forbidden : normal));
        }
        Add(get(c.Term), get(c.TermParam), TermStatus.Preferred);
        var synonyms = get(c.Syn).Split(';');
        var synParams = get(c.SynParam).Split(';');
        for (var i = 0; i < synonyms.Length; i++)
            Add(synonyms[i], i < synParams.Length ? synParams[i] : synParams[^1], TermStatus.Admitted);
        Add(get(c.Abbr), get(c.AbbrParam), TermStatus.Admitted);
        return list;
    }

    private static bool Avoid(string param)
    {
        var p = param.ToLowerInvariant();
        return p.Contains("avoid") || p.Contains("à éviter") || p.Contains("incorrect") || p.Contains("erroneous") || p.Contains("à proscrire");
    }

    /// <summary>„DEF: …“ aus den Belegen (Kontext und Beobachtungen bleiben weg).</summary>
    private static string? Definition(Func<string, string> get, Columns c) =>
        c.Support.Select(get).FirstOrDefault(s => s.StartsWith("DEF:", StringComparison.OrdinalIgnoreCase)) is { } d
            ? d[4..].Trim() is { Length: > 0 } text ? (text.Length > 2000 ? text[..2000] + " …" : text) : null
            : null;

    /// <summary>CSV nach RFC 4180: Felder in Anführungszeichen dürfen Kommas, Zeilenumbrüche und doppelte Anführungszeichen enthalten.</summary>
    internal static IEnumerable<string[]> Csv(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            var c = (char)ch;
            any = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return fields.ToArray();
                    fields.Clear();
                    any = false;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }
        if (any || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}
