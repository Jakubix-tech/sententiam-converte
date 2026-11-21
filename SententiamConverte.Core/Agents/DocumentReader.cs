using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Liest ein Dokument vor der Übersetzung einmal ganz (bei langen Dokumenten Anfang, Mitte und Ende) und erstellt den
/// <see cref="DocumentBrief"/>: Thema, Textsorte, Zielgruppe, Ton, Anrede, Sprecher und wiederkehrende Namen. Was die KI
/// nicht sicher erkennt (etwa ob eine Frau oder ein Mann angesprochen wird), bleibt offen – dann fragt das Programm.
/// </summary>
public sealed class DocumentReader(AgentContext ctx)
{
    /// <summary>So viel Text bekommt die KI höchstens (Zeichen).</summary>
    internal const int MaxChars = 12_000;

    public async Task<DocumentBrief> ReadAsync(BilingualDocument doc, string? projectTopic, CancellationToken ct = default)
    {
        ctx.Say("Lese das Dokument …");
        var text = Excerpt(doc.Segments.Where(s => !s.Locked).Select(s => TagText.StripTags(s.Source)).Where(s => s.Trim().Length > 0).ToList());
        var prompt = new StringBuilder();
        prompt.AppendLine($"Read this document ({Languages.DisplayName(doc.SourceLanguage)}). It will be translated into {Languages.DisplayName(doc.TargetLanguage)} ({doc.TargetLanguage}).");
        if (!string.IsNullOrWhiteSpace(projectTopic)) prompt.AppendLine($"Project subject: {projectTopic}");
        prompt.AppendLine($$"""
            Describe it for the translator as JSON:
            {"topic": "...", "text_type": "letter|contract|manual|website|report|cv|…", "audience": "...", "tone": "...",
             "address": "formal_plural|formal_man|formal_woman|formal_unknown_gender|informal|impersonal|no_address|unsure",
             "recipient": "name of the person addressed, if any",
             "speaker": "man|woman|organisation|no_first_person|unsure", "author": "name of the author, if any",
             "names": ["recurring names of people, companies, products"], "notes": "anything else the translator must keep in mind"}
            Rules for "address": how the TRANSLATION should address the reader. A letter to "Sehr geehrte Frau …" is formal_woman,
            to "Sehr geehrter Herr …" formal_man, "Sehr geehrte Damen und Herren" or a general public formal_plural; manuals and
            software texts usually informal (imperative) or impersonal – choose what fits the text type and target language;
            no_address if the text never speaks to the reader. Use "unsure" whenever the gender or the number of addressees is
            not clear from the text. Rules for "speaker": who writes in the first person (I/we) – "unsure" if there is first-person
            text but the author's gender is not clear; no_first_person if nobody writes as I/we.
            Write topic, audience, tone and notes in {{ctx.ExplanationLanguage}}, briefly (they are shown to the translator). Reply with the JSON only.
            """);
        prompt.AppendLine();
        prompt.AppendLine("Document:");
        prompt.AppendLine(text);
        var reply = await ctx.Model.AskAsync("You analyse documents for professional translators.", prompt.ToString(), ct);
        var brief = Parse(reply);
        brief.ReadAt = DateTime.UtcNow;
        return brief;
    }

    /// <summary>Kurze Dokumente ganz, lange als Anfang, Mitte und Ende (dort stehen Anrede, Gruß und Unterschrift).</summary>
    internal static string Excerpt(IReadOnlyList<string> sentences)
    {
        var all = string.Join("\n", sentences);
        if (all.Length <= MaxChars) return all;
        var part = MaxChars / 3;
        var middle = all.Length / 2 - part / 2;
        return all[..part] + "\n[…]\n" + all[middle..(middle + part)] + "\n[…]\n" + all[^part..];
    }

    internal static DocumentBrief Parse(string reply)
    {
        // Ohne auswertbare Antwort: keine Angaben, aber auch keine Rückfrage (gefragt wird nur, wenn die KI „unsicher“ sagt)
        var brief = new DocumentBrief { Address = AddressForm.None, Speaker = SpeakerForm.None };
        if (JsonExtract.Find(reply) is not { } json || JsonNode.Parse(json) is not JsonObject o) return brief;
        string? S(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        brief.Topic = S("topic");
        brief.TextType = S("text_type");
        brief.Audience = S("audience");
        brief.Tone = S("tone");
        brief.Recipient = S("recipient");
        brief.Author = S("author");
        brief.Notes = S("notes");
        brief.Address = S("address")?.ToLowerInvariant() switch
        {
            "formal_plural" => AddressForm.FormalPlural,
            "formal_man" => AddressForm.FormalMale,
            "formal_woman" => AddressForm.FormalFemale,
            "formal_unknown_gender" => AddressForm.FormalUnknownGender,
            "informal" => AddressForm.Informal,
            "impersonal" => AddressForm.Impersonal,
            "unsure" => AddressForm.Unknown,
            _ => AddressForm.None,
        };
        brief.Speaker = S("speaker")?.ToLowerInvariant() switch
        {
            "man" => SpeakerForm.Male,
            "woman" => SpeakerForm.Female,
            "organisation" or "organization" => SpeakerForm.Organisation,
            "unsure" => SpeakerForm.Unknown,
            _ => SpeakerForm.None,
        };
        if (o["names"] is JsonArray names)
            brief.Names = names.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).Distinct().Take(30).ToList();
        return brief;
    }
}
