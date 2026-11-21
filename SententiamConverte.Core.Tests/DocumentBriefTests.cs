using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>Steckbrief: Dokument lesen, Anrede und Sprecher, Weitergabe an die Übersetzung, Prüfung auf gemischte Anrede.</summary>
public sealed class DocumentBriefTests
{
    private sealed class Capture(string answer) : IChatModel
    {
        public readonly List<string> Prompts = new();
        public string DisplayName => "Test";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            return Task.FromResult(answer);
        }
    }

    private static BilingualDocument Doc(string targetLanguage, params string[] lines)
    {
        var doc = new BilingualDocument { FileName = "a.txt", SourceLanguage = "de-DE", TargetLanguage = targetLanguage };
        var seg = new Segmenter("de-DE");
        foreach (var line in lines)
        {
            var p = new Paragraph { Locator = "p" + doc.Paragraphs.Count };
            seg.Segment(p, line);
            doc.Paragraphs.Add(p);
        }
        doc.Renumber();
        return doc;
    }

    [Fact]
    public async Task ReadingTheDocumentFillsTheBrief()
    {
        var model = new Capture("""
            {"topic": "rent increase", "text_type": "letter", "audience": "tenant", "tone": "polite",
             "address": "formal_woman", "recipient": "Frau Nowak", "speaker": "unsure", "names": ["Nowak", "Haus AG"], "notes": "keep dates"}
            """);
        var doc = Doc("pl-PL", "Sehr geehrte Frau Nowak,", "ich habe Ihre Nachricht erhalten.", "Mit freundlichen Grüßen");
        var brief = await new DocumentReader(new AgentContext { Model = model }).ReadAsync(doc, "Mietrecht", TestContext.Current.CancellationToken);

        Assert.Contains("Sehr geehrte Frau Nowak", model.Prompts.Single());                 // das ganze Dokument geht hin
        Assert.Equal((AddressForm.FormalFemale, "Frau Nowak", SpeakerForm.Unknown), (brief.Address, brief.Recipient, brief.Speaker));
        Assert.True(brief.NeedsAnswer);                                                    // Sprecher unklar → fragen
        Assert.Equal(["Nowak", "Haus AG"], brief.Names);
        var instructions = brief.Instructions("pl-PL");
        Assert.Contains("one woman (Frau Nowak)", instructions);
        Assert.Contains("'Pani'", instructions);

        brief.Speaker = SpeakerForm.Male;
        brief.Confirmed = true;
        Assert.False(brief.NeedsAnswer);
        Assert.Contains("masculine first-person", brief.Instructions("pl-PL"));

        // keine auswertbare Antwort: keine Angaben, aber auch keine Rückfrage
        var nothing = DocumentReader.Parse("Ich weiß es nicht.");
        Assert.Equal((AddressForm.None, SpeakerForm.None, false), (nothing.Address, nothing.Speaker, nothing.NeedsAnswer));

        // lange Dokumente: Anfang, Mitte und Ende
        var excerpt = DocumentReader.Excerpt(Enumerable.Range(0, 3000).Select(i => $"Satz {i} mit etwas Text.").ToList());
        Assert.True(excerpt.Length <= DocumentReader.MaxChars + 20);
        Assert.Contains("Satz 0 ", excerpt);
        Assert.Contains("Satz 2999 ", excerpt);
    }

    [Fact]
    public async Task EveryTranslationGetsTheBrief()
    {
        var model = new Capture("Dziękuję Pani.");
        var brief = new DocumentBrief { TextType = "letter", Address = AddressForm.FormalFemale, Speaker = SpeakerForm.Female, Confirmed = true };
        await new AiTranslator(new AgentContext { Model = model }).TranslateAsync("Ich danke Ihnen.", "de-DE", "pl-PL", null,
            brief: brief, ct: TestContext.Current.CancellationToken);
        var prompt = model.Prompts.Single();
        Assert.Contains("About the whole document", prompt);
        Assert.Contains("to one woman", prompt);
        Assert.Contains("feminine first-person forms", prompt);
    }

    [Fact]
    public void QaFindsMixedAddressForms()
    {
        var doc = Doc("pl-PL", "Satz eins.", "Satz zwei.", "Satz drei.", "Satz vier.");
        var s = doc.Segments.ToList();
        s[0].Target = "Dziękuję Pani za list.";
        s[1].Target = "Czy Pan może przyjść?";
        s[2].Target = "Proszę Panią o odpowiedź.";
        s[3].Target = "Pan/Pani może zadzwonić.";
        var settings = new QaSettings { Spelling = false };

        // Steckbrief sagt: an eine Frau → „Pan“ und „Pan/Pani“ passen nicht
        var brief = new DocumentBrief { Address = AddressForm.FormalFemale, Confirmed = true };
        var issues = QaChecker.Check(doc, settings, brief: brief).Where(i => i.Check == "Anrede").ToList();
        Assert.Equal([2, 4], issues.Select(i => i.SegmentId).Order());
        Assert.Contains("„Pan“ statt „Pani“", issues.First(i => i.SegmentId == 2).Message);

        // ohne Steckbrief: die seltener vorkommende Form fällt auf
        var mixed = QaChecker.Check(doc, settings).Where(i => i.Check == "Anrede").Select(i => i.SegmentId).ToList();
        Assert.Contains(2, mixed);
        Assert.DoesNotContain(1, mixed);

        // Deutsch: Sie und du gemischt
        var de = Doc("de-DE", "a.", "b.");
        de.Segments.First().Target = "Bitte prüfen Sie das Gerät.";
        de.Segments.Last().Target = "Dann schaltest du es ein.";
        var german = QaChecker.Check(de, settings, brief: new DocumentBrief { Address = AddressForm.FormalPlural }).Where(i => i.Check == "Anrede").ToList();
        Assert.Equal(2, Assert.Single(german).SegmentId);
        Assert.Empty(QaChecker.Check(de, new QaSettings { Spelling = false, Address = false }).Where(i => i.Check == "Anrede"));
    }

    [Fact]
    public void TheBriefIsStoredWithTheProject()
    {
        using var dir = new TempDir();
        var project = TranslationProject.Create(dir.File("p"), "Brief", "de-DE", ["pl-PL"]);
        var file = project.AddFile(new MemoryStream("Sehr geehrte Frau Nowak,\nvielen Dank.\n"u8.ToArray()), "brief.txt");
        Assert.Null(project.Brief(file, "pl-PL"));
        project.SetBrief(file, "pl-PL", new DocumentBrief { Address = AddressForm.FormalFemale, Speaker = SpeakerForm.Male, Confirmed = true, Names = ["Nowak"] });
        project.Dispose();

        using var again = TranslationProject.Open(dir.File("p"));
        var brief = again.Brief(again.Files.Single(), "pl-PL");
        Assert.NotNull(brief);
        Assert.Equal((AddressForm.FormalFemale, SpeakerForm.Male, true), (brief.Address, brief.Speaker, brief.Confirmed));
        Assert.Equal(["Nowak"], brief.Names);
    }

    [Fact]
    public void ReadingFillsOnlyTheGapsAndKeepsWhatTheUserEntered()
    {
        // vom Nutzer: Thema und Sprecher; nie von der KI gelesen → soll gelesen werden
        var mine = new DocumentBrief { Topic = "CV", Speaker = SpeakerForm.Male, Address = AddressForm.Unknown, Names = ["Nowakowski"] };
        Assert.True(mine.ShouldRead);
        var read = new DocumentBrief
        {
            Topic = "Lebenslauf eines Softwareentwicklers", TextType = "cv", Audience = "Personalabteilungen", Tone = "sachlich",
            Address = AddressForm.None, Speaker = SpeakerForm.Female, Names = ["Nowakowski", "Jakubix-tech"], ReadAt = new DateTime(2026, 11, 9),
        };
        mine.FillFrom(read);
        Assert.Equal(("CV", "cv", "Personalabteilungen", "sachlich"), (mine.Topic, mine.TextType, mine.Audience, mine.Tone));
        Assert.Equal((AddressForm.None, SpeakerForm.Male), (mine.Address, mine.Speaker));        // offene Anrede übernommen, Sprecher bleibt
        Assert.Equal(["Nowakowski", "Jakubix-tech"], mine.Names);
        Assert.False(mine.ShouldRead);
        // einmal gelesen und trotzdem lückenhaft (z. B. kein Ton erkannt): nicht bei jedem Öffnen wieder lesen
        Assert.False(new DocumentBrief { Topic = "x", ReadAt = DateTime.UtcNow }.ShouldRead);
        // bestätigt: Anrede und Sprecher bleiben, auch wenn sie offen sind
        var confirmed = new DocumentBrief { Confirmed = true, Address = AddressForm.Unknown };
        confirmed.FillFrom(new DocumentBrief { Address = AddressForm.Informal });
        Assert.Equal(AddressForm.Unknown, confirmed.Address);
    }
}
