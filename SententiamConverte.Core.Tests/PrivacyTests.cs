using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Tests;

/// <summary>Datenschutz: geschützte Angaben erkennen, nur als Platzhalter an die KI und ins TM.</summary>
[Collection("BuiltInAi")]
public sealed class PrivacyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PrivacySettings _old = PrivacyContext.Current;
    private static readonly PrivacySettings On = new() { Enabled = true };

    public void Dispose()
    {
        PrivacyContext.Current = _old;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static List<(SensitiveKind, string)> Found(string text, PrivacySettings? s = null) =>
        SensitiveData.Find(text, s ?? On).Select(x => (x.Kind, x.Value)).ToList();

    [Fact]
    public void NamesDatesCaseNumbersAndContactDataAreFound()
    {
        var found = Found("Herr Müller hat am 12.03.2024 unter Az. 2 O 123/24 Klage erhoben; E-Mail max.mueller@example.de, " +
                          "Tel. +49 30 1234567, IBAN DE89 3704 0044 0532 0130 00.");
        Assert.Contains((SensitiveKind.Name, "Müller"), found);
        Assert.Contains((SensitiveKind.Date, "12.03.2024"), found);
        Assert.Contains((SensitiveKind.Case, "2 O 123/24"), found);
        Assert.Contains((SensitiveKind.Email, "max.mueller@example.de"), found);
        Assert.Contains((SensitiveKind.Phone, "+49 30 1234567"), found);
        Assert.Contains((SensitiveKind.Iban, "DE89 3704 0044 0532 0130 00"), found);

        var pl = Found("Pan Kowalski, sygn. akt I C 123/24, dnia 12 marca 2024 r. – Frau Dr. Anna Schmidt, VI ZR 45/20, 1 BvR 2345/19.");
        Assert.Contains((SensitiveKind.Name, "Kowalski"), pl);
        Assert.Contains((SensitiveKind.Case, "I C 123/24"), pl);
        Assert.Contains((SensitiveKind.Date, "12 marca 2024"), pl);
        Assert.Contains((SensitiveKind.Name, "Anna Schmidt"), pl);
        Assert.Contains((SensitiveKind.Case, "VI ZR 45/20"), pl);
        Assert.Contains((SensitiveKind.Case, "1 BvR 2345/19"), pl);

        // nichts Schützenswertes
        Assert.Empty(Found("Ziehen Sie die Schraube mit 5 Nm an."));
        Assert.Empty(Found("Nach Art. 6 der Verordnung (EU) 2016/679 und der Richtlinie 95/46/EG."));
        // Schutzliste, auch im Genitiv
        Assert.Contains((SensitiveKind.Term, "Bauhaus GmbHs"), Found("Die Rechnung der Bauhaus GmbHs liegt vor.", new() { Enabled = true, Terms = ["Bauhaus GmbH"] }));
        // abgeschaltete Arten
        Assert.Empty(Found("am 12.03.2024", new() { Enabled = true, Dates = false }));
    }

    [Fact]
    public void DatesThatTheAiOnlySawAsPlaceholdersAreWrittenTheTargetLanguageWay()
    {
        Assert.Equal("Served on 3 April 2024.", DateLocalizer.Localize("Served on 3. April 2024.", "Zugestellt am 3. April 2024.", "en-GB"));
        Assert.Equal("Served on April 3, 2024.", DateLocalizer.Localize("Served on 3. April 2024.", "Zugestellt am 3. April 2024.", "en-US"));
        Assert.Equal("Doręczono 3 kwietnia 2024.", DateLocalizer.Localize("Doręczono 3. April 2024.", "Zugestellt am 3. April 2024.", "pl-PL"));
        Assert.Equal("Filed on 12/03/2024.", DateLocalizer.Localize("Filed on 12.03.2024.", "Eingereicht am 12.03.2024.", "en-GB"));
        Assert.Equal("Złożono 12.03.2024.", DateLocalizer.Localize("Złożono 12.03.2024.", "Eingereicht am 12.03.2024.", "pl-PL"));    // schon richtig
        Assert.False(DateLocalizer.TryParse("31.02.2024", out _, out _));                                                            // kein Datum
    }

    [Fact]
    public async Task TheAiIsToldWhatThePlaceholdersMean()
    {
        var inner = new Recording("ok");
        await new PrivacyChatModel(inner, On).AskAsync("Translate.", "Herr Müller klagt.", TestContext.Current.CancellationToken);
        Assert.Contains("[NAME_n] = a person", inner.Sent[0]);                                     // Erklärung im Systemtext
        Assert.Contains("[NAME_1] male", inner.Sent[0]);                                           // Geschlecht aus der Anrede, nicht der Name
        var plain = new Recording("ok");
        await new PrivacyChatModel(plain, On).AskAsync("Translate.", "Die Schraube anziehen.", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("placeholders", plain.Sent[0]);                                     // nur wenn es Platzhalter gibt
    }

    [Fact]
    public void PlaceholdersGoOutAndRealValuesComeBack()
    {
        var map = new PrivacyMap(On);
        var masked = map.Mask("Herr Müller zahlt am 12.03.2024; Herr Müller unterschreibt.");
        Assert.Equal("Herr [NAME_1] zahlt am [DATE_1]; Herr [NAME_1] unterschreibt.", masked);
        Assert.Equal("Mr Müller pays on 12.03.2024.", map.Unmask("Mr [Name 1] pays on [DATE-1]."));   // leicht veränderte Platzhalter
        Assert.Equal(2, map.Count);
    }

    private sealed class Recording(string reply) : IToolChatModel
    {
        public readonly List<string> Sent = new();
        public ToolReply? ToolReply;
        public string DisplayName => "online";
        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Sent.Add(system);
            Sent.AddRange(messages.Select(m => m.Text));
            return Task.FromResult(reply);
        }
        public Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
        {
            Sent.Add(system);
            Sent.AddRange(messages.Select(m => m.Text ?? ""));
            return Task.FromResult(ToolReply!);
        }
    }

    [Fact]
    public async Task OnlineAiSeesOnlyPlaceholdersAndSearchesStayMasked()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new Recording("Mr [NAME_1] filed the claim on [DATE_1] under case no. [CASE_1].");
        var model = new PrivacyChatModel(inner, On);
        var masked = 0;
        model.Masked += n => masked = n;
        var answer = await model.AskAsync("Translate.", "Herr Müller hat am 12.03.2024 unter Az. 2 O 123/24 Klage erhoben.", ct);

        Assert.DoesNotContain(inner.Sent, s => s.Contains("Müller") || s.Contains("12.03.2024") || s.Contains("123/24"));
        Assert.Equal("Mr Müller filed the claim on 12.03.2024 under case no. 2 O 123/24.", answer);
        Assert.Equal(3, masked);

        inner.ToolReply = new ToolReply("", [
            new ToolCall("1", "search", (JsonObject)JsonNode.Parse("""{"query": "[NAME_1] Klage"}""")!),
            new ToolCall("2", "finish", (JsonObject)JsonNode.Parse("""{"answer": "Kläger ist [NAME_1]."}""")!)]);
        var reply = await model.CompleteWithToolsAsync("s", [AgentMessage.User("Wer ist Herr Müller?")], [], ct);
        Assert.Equal("[NAME_1] Klage", (string?)reply.Calls[0].Arguments["query"]);                   // Suchanfrage ohne Namen ins Netz
        Assert.Equal("Kläger ist Müller.", (string?)reply.Calls[1].Arguments["answer"]);              // Antwort an den Nutzer mit Namen
    }

    [Fact]
    public void OnlyOnlineProvidersAreWrappedUnlessLocalIsRequested()
    {
        var online = new AiProviderConfig { Name = "online", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "https://example.org/v1", Model = "m" };
        var local = new AiProviderConfig { Name = "lokal", Kind = AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:8080/v1", Model = "m" };
        PrivacyContext.Current = new PrivacySettings();
        Assert.IsNotType<PrivacyChatModel>(ChatModelFactory.Create(online));                        // aus
        PrivacyContext.Current = new PrivacySettings { Enabled = true };
        Assert.IsType<PrivacyChatModel>(ChatModelFactory.Create(online));
        Assert.IsNotType<PrivacyChatModel>(ChatModelFactory.Create(local));                         // Texte bleiben auf dem Rechner
        PrivacyContext.Current = new PrivacySettings { Enabled = true, AlsoLocalAi = true };
        Assert.IsType<PrivacyChatModel>(ChatModelFactory.Create(local));
        var pool = new AiProviderConfig { Name = "Verbund", Kind = AiProviderKind.Pool, Members = [online] };
        var wrapped = Assert.IsType<PrivacyChatModel>(ChatModelFactory.Create(pool));
        Assert.IsType<PooledChatModel>(wrapped.Inner);                                               // Verbund als Ganzes, Mitglieder nicht doppelt
        Assert.IsType<PooledChatModel>(PrivacyContext.Unwrap(wrapped));
    }

    [Fact]
    public void TheTmStoresPlaceholdersAndMatchesGetTheValuesOfTheCurrentSegment()
    {
        Assert.Equal(("Herr [NAME_1] unterschreibt am [DATE_1].", "[NAME_1] signs on [DATE_1]."),
            PrivacyScanner.MaskPair("Herr Müller unterschreibt am 12.03.2024.", "Müller signs on 12 March 2024.", On));   // Name im Ziel ohne Anrede

        using var p = TranslationProject.Create(_dir.File("p"), "Vertraulich", "de-DE", ["en-GB"]);
        p.Privacy = new PrivacySettings { Enabled = true };
        var doc = new BilingualDocument { SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        var para = new Paragraph();
        para.Segments.Add(new Segment { Id = 1, Source = "Einleitung zum Fall von Herr Müller.", Target = "" });
        para.Segments.Add(new Segment { Id = 2, Source = "Herr Müller unterschreibt am 12.03.2024.", Target = "Mr Müller signs on 12.03.2024." });
        para.Segments.Add(new Segment { Id = 3, Source = "Herr Schmidt unterschreibt am 01.04.2025.", Target = "" });
        doc.Paragraphs.Add(para);
        var all = doc.Segments.ToList();

        Workflow.Confirm(p, doc, all[1]);
        var unit = Assert.Single(p.ProjectTm.All());
        Assert.Equal("Herr [NAME_1] unterschreibt am [DATE_1].", unit.Source);
        Assert.Equal("Mr [NAME_1] signs on [DATE_1].", unit.Target);
        Assert.DoesNotContain("Müller", unit.ContextPrevious ?? "");                                 // auch der Satzzusammenhang

        var match = Workflow.Lookup(p, doc, all[2]).First();
        Assert.Equal(100, match.Score);
        Assert.Equal("Mr Schmidt signs on 01/04/2025.", match.Unit.Target);                          // Werte dieses Segments, Datum englisch

        // vorhandenes TM bereinigen
        p.ProjectTm.Add("de-DE", "en-GB", "Frau Weber zahlt am 02.02.2022.", "Ms Weber pays on 02.02.2022.");
        Assert.Equal(1, PrivacyScanner.Anonymize(p.ProjectTm, p.Privacy));
        Assert.DoesNotContain(p.ProjectTm.All(), u => u.Source.Contains("Weber") || u.Target.Contains("Weber"));
    }

    [Fact]
    public void ValuesMarkedNotSensitiveStayButTheProtectionListWins()
    {
        var settings = new PrivacySettings { Enabled = true, Terms = ["Kowalski"], NotSensitive = ["01.01.2024", "Kowalski", "Herr Meier"] };
        var spans = SensitiveData.Find("Am 01.01.2024 und 02.02.2024 unterschreiben Herr Meier und Herr Kowalski.", settings);
        Assert.DoesNotContain(spans, s => s.Value == "01.01.2024");                        // nicht schützen
        Assert.Contains(spans, s => s.Value == "02.02.2024");
        Assert.Contains(spans, s => s.Value == "Meier");                                    // nur exakt der Wert zählt
        Assert.Contains(spans, s => s.Value == "Kowalski" && s.Kind == SensitiveKind.Term);  // Schutzliste geht vor

        var map = new PrivacyMap(settings);
        map.Mask("Herr Meier und Herr Meier am 02.02.2024.");
        Assert.Equal(("[NAME_1]", "[DATE_1]", null), (map.PlaceholderFor("Meier"), map.PlaceholderFor("02.02.2024"), map.PlaceholderFor("Nobody")));
        Assert.Equal(["01.01.2024", "Kowalski", "Herr Meier"], settings.Clone().NotSensitive);
    }
}
