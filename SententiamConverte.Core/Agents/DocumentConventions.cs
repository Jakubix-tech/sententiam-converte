using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Agents;

public sealed record ConventionAnalysis(string DocumentKind, string Country, List<string> Notes, List<CountryConvention> Conventions, string Model, string Search);

/// <summary>
/// Dokumentart und landesübliche Ausdrücke: Die KI bestimmt, was das Dokument genau ist (z. B. ein Lebenslauf für eine
/// Bewerbung), welche Gepflogenheiten solche Dokumente im Zielland haben und wie man dort die Überschriften, Titel und
/// festen Wendungen dieses Dokuments tatsächlich formuliert. Jeder Kandidat wird in der Zielsprache gezählt (mit einem
/// Suchdienst, der Länder kennt, im Zielland); ist einer klar häufiger, gilt er, sonst der erste Vorschlag der KI.
/// </summary>
public sealed class DocumentConventions(AgentContext ctx)
{
    private sealed record Dto(string? Kind, List<string>? Notes, List<ItemDto>? Items);
    private sealed record ItemDto(string? Source, List<string>? Candidates, string? Why);

    public async Task<ConventionAnalysis> AnalyseAsync(BilingualDocument doc, string targetLanguage, string? topic, DocumentBrief? brief = null,
        CancellationToken ct = default)
    {
        var country = PhraseFrequency.CountryOf(targetLanguage) ?? "";
        var countryName = country.Length > 0 ? PhraseFrequency.CountryName(country) : Languages.DisplayName(targetLanguage);
        var sources = doc.Segments.Where(s => !s.Locked).Select(s => TagText.StripTags(s.Source)).Where(s => s.Trim().Length > 0).ToList();
        var text = DocumentReader.Excerpt(sources);

        ctx.Say($"Bestimme die Dokumentart und was in {countryName} üblich ist …");
        var dto = JsonExtract.Parse<Dto>(await ctx.Model.AskAsync(
            "You are an experienced translator who knows how documents are written in each country. Output only JSON.",
            $$"""
            This document ({{Languages.DisplayName(doc.SourceLanguage)}}) will be translated into {{Languages.DisplayName(targetLanguage)}} for readers in {{countryName}}.
            {{(string.IsNullOrWhiteSpace(topic) ? "" : "Project subject: " + topic)}}
            {{(brief?.TextType is { Length: > 0 } tt ? "Text type found so far: " + tt : "")}}
            1. kind: what exactly this document is (e.g. "CV of a software developer for a job application"), in {{ctx.ExplanationLanguage}}.
            2. notes: up to 5 conventions that documents of this kind follow in {{countryName}} and that the translation must respect
               (structure, usual or mandatory parts, formats, how titles and qualifications are given), in {{ctx.ExplanationLanguage}}.
            3. items: up to 12 elements of THIS document that have an established wording in documents of this kind in {{countryName}} –
               section headings, job titles, degrees and qualifications, fixed formulas, labels. Copy "source" exactly from the document.
               "candidates": 1–3 wordings that are actually used in {{countryName}} (in {{Languages.DisplayName(targetLanguage)}}), most common first;
               keep internationally used terms (e.g. English job titles in IT) when that is what people there really write.
               "why": short, in {{ctx.ExplanationLanguage}}.
            Return: {"kind": "...", "notes": ["..."], "items": [{"source": "...", "candidates": ["..."], "why": "..."}]}

            Document:
            {{text}}
            """, ct));

        var conventions = new List<CountryConvention>();
        foreach (var item in (dto.Items ?? []).Where(i => i.Source is { Length: > 1 } s && sources.Any(x => UsageCheckAgent.ContainsPhrase(x, s)))
                     .DistinctBy(i => i.Source!.Trim(), StringComparer.OrdinalIgnoreCase).Take(12))
        {
            var candidates = (item.Candidates ?? []).Select(c => c.Trim()).Where(c => c.Length > 1 && TranslationWebCheck.Searchable(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (candidates.Count == 0) continue;
            var counts = new List<PhraseCount>();
            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                counts.Add(await PhraseFrequency.CountAsync(ctx, c, targetLanguage, ct));
            }
            var best = PhraseFrequency.ClearlyMostCommon(counts);
            var chosen = best ?? counts[0];
            conventions.Add(new CountryConvention
            {
                Source = item.Source!.Trim(),
                Target = chosen.Variant,
                ByFrequency = best != null,
                Why = item.Why?.Trim(),
                Evidence = counts.Select(c => $"„{c.Variant}“: {TranslationWebCheck.Describe(c)}").ToList(),
                Sources = chosen.Examples.Take(3).Select(e => new SourceRef(targetLanguage, e.Title, e.Url)).ToList(),
            });
        }
        var search = ctx.Search is null or WikipediaSearch ? "Wikipedia" : "Wikipedia + " + ctx.Search.Name;
        return new ConventionAnalysis(dto.Kind?.Trim() ?? "", country, (dto.Notes ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Take(5).ToList(),
            conventions, ctx.Model.DisplayName, search);
    }

    /// <summary>Ergebnis in den Steckbrief übernehmen; was der Nutzer früher abgewählt hat, bleibt abgewählt.</summary>
    public static void Apply(DocumentBrief brief, ConventionAnalysis analysis)
    {
        var unused = brief.Conventions.Where(c => !c.Use).Select(c => c.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        brief.DocumentKind = analysis.DocumentKind.Length > 0 ? analysis.DocumentKind : brief.DocumentKind;
        brief.Country = analysis.Country;
        brief.CountryNotes = analysis.Notes;
        brief.Conventions = analysis.Conventions;
        foreach (var c in brief.Conventions) c.Use = !unused.Contains(c.Source);
    }
}
