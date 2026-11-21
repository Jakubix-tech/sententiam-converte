using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Core.Projects;

/// <summary>Wie die Übersetzung den Leser anspricht – gilt für das ganze Dokument.</summary>
public enum AddressForm
{
    /// <summary>Noch offen (die KI war unsicher) – das Programm fragt.</summary>
    Unknown,
    /// <summary>Der Text spricht niemanden an.</summary>
    None,
    /// <summary>Höflich an mehrere oder ein allgemeines Publikum (Państwo, Sie, vous).</summary>
    FormalPlural,
    /// <summary>Höflich an einen Mann (Pan).</summary>
    FormalMale,
    /// <summary>Höflich an eine Frau (Pani).</summary>
    FormalFemale,
    /// <summary>Höflich an eine Person unbekannten Geschlechts (Pan/Pani).</summary>
    FormalUnknownGender,
    /// <summary>Vertraut, in Anleitungen der Imperativ (ty / du / tu).</summary>
    Informal,
    /// <summary>Unpersönlich, ohne Anrede (należy …, Infinitiv, Passiv).</summary>
    Impersonal,
}

/// <summary>Wer im Text in der ersten Person spricht – wichtig für Sprachen mit Geschlecht im Verb (zrobiłem / zrobiłam).</summary>
public enum SpeakerForm { Unknown, None, Male, Female, Organisation }

/// <summary>
/// Steckbrief eines Dokuments für eine Zielsprache: Die KI liest das Dokument einmal und hält fest, worum es geht, für
/// wen es ist, wie angesprochen wird und wer spricht. Jede KI-Übersetzung bekommt ihn mit; Anrede und Sprecher bestimmt
/// im Zweifel der Nutzer.
/// </summary>
public sealed class DocumentBrief
{
    public string? Topic { get; set; }
    public string? TextType { get; set; }
    public string? Audience { get; set; }
    public string? Tone { get; set; }
    public AddressForm Address { get; set; }
    /// <summary>Angesprochene Person (Name aus der Grußformel), falls bekannt.</summary>
    public string? Recipient { get; set; }
    public SpeakerForm Speaker { get; set; }
    public string? Author { get; set; }
    public List<string> Names { get; set; } = new();
    public string? Notes { get; set; }
    /// <summary>Vom Nutzer bestätigt – dann fragt das Programm nicht mehr.</summary>
    public bool Confirmed { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Was das Dokument genau ist (z. B. „Lebenslauf eines Softwareentwicklers für eine Bewerbung“); ältere Steckbriefe: leer.</summary>
    public string? DocumentKind { get; set; }
    /// <summary>Für welches Land die landesüblichen Ausdrücke gelten (z. B. „PL“).</summary>
    public string? Country { get; set; }
    /// <summary>Gepflogenheiten dieser Dokumentart im Zielland (Aufbau, Pflichtangaben, Formate).</summary>
    public List<string> CountryNotes { get; set; } = new();
    /// <summary>Im Zielland übliche Formulierungen für Überschriften, Titel und feste Wendungen dieses Dokuments.</summary>
    public List<CountryConvention> Conventions { get; set; } = new();

    /// <summary>Anrede oder Sprecher sind offen – vor der Übersetzung fragen.</summary>
    public bool NeedsAnswer => !Confirmed && (Address == AddressForm.Unknown || Speaker == SpeakerForm.Unknown);

    /// <summary>Wann die KI das Dokument zuletzt gelesen hat; null = noch nie (auch bei älteren Steckbriefen).</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Thema, Textsorte, Zielgruppe oder Ton fehlen noch.</summary>
    [JsonIgnore]
    public bool Incomplete => string.IsNullOrWhiteSpace(Topic) || string.IsNullOrWhiteSpace(TextType)
                              || string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(Tone);

    /// <summary>Die KI soll das Dokument lesen: Es fehlt etwas, und gelesen hat sie es noch nie (sonst blieben Lücken ewig offen).</summary>
    [JsonIgnore]
    public bool ShouldRead => ReadAt == null && Incomplete;

    /// <summary>
    /// Leere Angaben aus einem (neu) gelesenen Steckbrief ergänzen. Was schon da ist – vom Nutzer eingetragen oder bestätigt –
    /// bleibt; offene Anrede und offener Sprecher übernehmen die Erkennung; Namen werden zusammengeführt.
    /// </summary>
    public void FillFrom(DocumentBrief read)
    {
        static string? Pick(string? mine, string? theirs) => string.IsNullOrWhiteSpace(mine) ? theirs : mine;
        Topic = Pick(Topic, read.Topic);
        TextType = Pick(TextType, read.TextType);
        Audience = Pick(Audience, read.Audience);
        Tone = Pick(Tone, read.Tone);
        Recipient = Pick(Recipient, read.Recipient);
        Author = Pick(Author, read.Author);
        Notes = Pick(Notes, read.Notes);
        if (!Confirmed && Address is AddressForm.Unknown or AddressForm.None) Address = read.Address;
        if (!Confirmed && Speaker is SpeakerForm.Unknown or SpeakerForm.None) Speaker = read.Speaker;
        Names = Names.Concat(read.Names).Where(n => n.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        ReadAt = read.ReadAt ?? DateTime.UtcNow;
    }

    /// <summary>Anweisungen für die KI (englisch, wie die übrigen Anweisungen).</summary>
    public string Instructions(string targetLanguage)
    {
        var sb = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"- {label}: {value.Trim()}");
        }
        Line("Topic", Topic);
        Line("Text type", TextType);
        Line("Audience", Audience);
        Line("Tone", Tone);
        Line("Form of address in the translation", AddressInstruction(Address, Recipient));
        Line("Speaker", SpeakerInstruction(Speaker, Author));
        if (Names.Count > 0) Line("Recurring names (keep spelling consistent)", string.Join(", ", Names.Take(20)));
        Line("Further notes", Notes);
        Line("Document kind", DocumentKind);
        var country = Country is { Length: > 0 } c ? $" in {c}" : "";
        if (CountryNotes.Count > 0) Line($"Conventions of this kind of document{country}", string.Join("; ", CountryNotes));
        if (Conventions.Where(x => x.Use && x.Target.Length > 0).ToList() is { Count: > 0 } used)
            Line($"Established wordings{country} for this kind of document (use them exactly)",
                string.Join("; ", used.Select(x => $"\"{x.Source}\" → \"{x.Target}\"")));
        return sb.ToString();
    }

    private static string? AddressInstruction(AddressForm form, string? recipient) => form switch
    {
        AddressForm.FormalPlural => "formal, to several people or a general audience (e.g. Polish 'Państwo' with plural agreement, German 'Sie', French 'vous')",
        AddressForm.FormalMale => "formal, to one man" + (recipient is { Length: > 0 } r ? $" ({r})" : "") + " (e.g. Polish 'Pan' with masculine agreement, German 'Sie')",
        AddressForm.FormalFemale => "formal, to one woman" + (recipient is { Length: > 0 } r ? $" ({r})" : "") + " (e.g. Polish 'Pani' with feminine agreement, German 'Sie')",
        AddressForm.FormalUnknownGender => "formal, to one person of unknown gender (e.g. Polish 'Pan/Pani' double forms such as 'Pan/Pani może', German 'Sie')",
        AddressForm.Informal => "informal (Polish 'ty', German 'du', French 'tu'); instructions in the imperative (e.g. Polish 'Kliknij')",
        AddressForm.Impersonal => "impersonal, no direct address (e.g. Polish 'należy kliknąć', German infinitive or passive, French infinitive)",
        AddressForm.None => "the text does not address the reader directly",
        _ => null,
    };

    private static string? SpeakerInstruction(SpeakerForm form, string? author) => form switch
    {
        SpeakerForm.Male => "the author is a man" + (author is { Length: > 0 } a ? $" ({a})" : "") + " – use masculine first-person forms (e.g. Polish 'zrobiłem')",
        SpeakerForm.Female => "the author is a woman" + (author is { Length: > 0 } a ? $" ({a})" : "") + " – use feminine first-person forms (e.g. Polish 'zrobiłam')",
        SpeakerForm.Organisation => "an organisation speaking as 'we' – use plural first-person forms",
        _ => null,
    };
}

/// <summary>
/// Ein Element des Dokuments (Überschrift, Berufsbezeichnung, feste Wendung) und wie man es in Dokumenten dieser Art im
/// Zielland tatsächlich formuliert – mit den gezählten Kandidaten und Belegen.
/// </summary>
public sealed class CountryConvention
{
    /// <summary>Wörtlich aus dem Dokument.</summary>
    public string Source { get; set; } = "";
    /// <summary>Die gewählte Formulierung.</summary>
    public string Target { get; set; } = "";
    /// <summary>Alle Kandidaten mit ihren Zahlen, z. B. „„Doświadczenie zawodowe“: Wikipedia: 312 Artikel“.</summary>
    public List<string> Evidence { get; set; } = new();
    public string? Why { get; set; }
    /// <summary>Nach Häufigkeit gewählt (sonst Vorschlag der KI, weil die Zahlen nicht aussagekräftig waren).</summary>
    public bool ByFrequency { get; set; }
    public List<Agents.SourceRef> Sources { get; set; } = new();
    /// <summary>Bei der Übersetzung verwenden (der Nutzer kann einzelne abwählen).</summary>
    public bool Use { get; set; } = true;
}

/// <summary>Anredeformen im Text erkennen (Polnisch, Deutsch, Französisch) – für die Prüfung auf gemischte Formen.</summary>
public static partial class AddressForms
{
    [GeneratedRegex(@"\b(Pan|Pana|Panu|Panem|Panie)\b(?!/)")] private static partial Regex PlMale();
    [GeneratedRegex(@"(?<!/)\b(Pani|Panią)\b")] private static partial Regex PlFemale();
    [GeneratedRegex(@"\b(Pan/Pani|Pana/Pani|Panu/Pani|Panem/Panią)\b")] private static partial Regex PlDouble();
    [GeneratedRegex(@"\b(Państwo|Państwa|Państwu|Państwem)\b")] private static partial Regex PlPlural();
    [GeneratedRegex(@"\b(du|dich|dir|dein|deine|deinen|deinem|deiner|deines)\b")] private static partial Regex DeInformal();
    [GeneratedRegex(@"(?<=\p{L}[^.!?:]*\s)(Sie|Ihnen|Ihr|Ihre|Ihren|Ihrem|Ihrer|Ihres)\b")] private static partial Regex DeFormal();
    [GeneratedRegex(@"\b(tu|te|toi|ton|ta|tes)\b", RegexOptions.IgnoreCase)] private static partial Regex FrInformal();
    [GeneratedRegex(@"\b(vous|votre|vos)\b", RegexOptions.IgnoreCase)] private static partial Regex FrFormal();

    /// <summary>Welche Anredeformen der Text enthält.</summary>
    public static HashSet<AddressForm> Detect(string text, string language)
    {
        var found = new HashSet<AddressForm>();
        text = TagText.StripTags(text);
        switch (Languages.Primary(language))
        {
            case "pl":
                if (PlDouble().IsMatch(text)) found.Add(AddressForm.FormalUnknownGender);
                var withoutDouble = PlDouble().Replace(text, "");
                if (PlMale().IsMatch(withoutDouble)) found.Add(AddressForm.FormalMale);
                if (PlFemale().IsMatch(withoutDouble)) found.Add(AddressForm.FormalFemale);
                if (PlPlural().IsMatch(text)) found.Add(AddressForm.FormalPlural);
                break;
            case "de":
                if (DeInformal().IsMatch(text)) found.Add(AddressForm.Informal);
                if (DeFormal().IsMatch(text)) found.Add(AddressForm.FormalPlural);
                break;
            case "fr":
                if (FrInformal().IsMatch(text)) found.Add(AddressForm.Informal);
                if (FrFormal().IsMatch(text)) found.Add(AddressForm.FormalPlural);
                break;
        }
        return found;
    }

    /// <summary>Kurzname einer Anredeform in der Zielsprache (für Auswahl und Meldungen).</summary>
    public static string Label(AddressForm form, string language) => (Languages.Primary(language), form) switch
    {
        ("pl", AddressForm.FormalPlural) => "Państwo",
        ("pl", AddressForm.FormalMale) => "Pan",
        ("pl", AddressForm.FormalFemale) => "Pani",
        ("pl", AddressForm.FormalUnknownGender) => "Pan/Pani",
        ("pl", AddressForm.Informal) => "ty (Kliknij)",
        ("pl", AddressForm.Impersonal) => "bezosobowo (Należy kliknąć)",
        ("de", AddressForm.Informal) => "du",
        ("de", AddressForm.Impersonal) => "unpersönlich",
        ("de", _) when form is AddressForm.FormalPlural or AddressForm.FormalMale or AddressForm.FormalFemale or AddressForm.FormalUnknownGender => "Sie",
        ("fr", AddressForm.Informal) => "tu",
        ("fr", AddressForm.Impersonal) => "infinitif",
        ("fr", _) when form is AddressForm.FormalPlural or AddressForm.FormalMale or AddressForm.FormalFemale or AddressForm.FormalUnknownGender => "vous",
        (_, AddressForm.FormalPlural) => "formal (plural)",
        (_, AddressForm.FormalMale) => "formal (man)",
        (_, AddressForm.FormalFemale) => "formal (woman)",
        (_, AddressForm.FormalUnknownGender) => "formal (unknown gender)",
        (_, AddressForm.Informal) => "informal",
        (_, AddressForm.Impersonal) => "impersonal",
        (_, AddressForm.None) => "–",
        _ => "?",
    };

    /// <summary>Passt eine gefundene Form zur gewählten? Im Deutschen und Französischen ist jede höfliche Anrede „Sie“ bzw. „vous“.</summary>
    public static bool Fits(AddressForm found, AddressForm chosen, string language)
    {
        if (found == chosen) return true;
        var lang = Languages.Primary(language);
        var formal = chosen is AddressForm.FormalPlural or AddressForm.FormalMale or AddressForm.FormalFemale or AddressForm.FormalUnknownGender;
        if (lang is "de" or "fr") return formal && found == AddressForm.FormalPlural;
        return false;
    }
}
