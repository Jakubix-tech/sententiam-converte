using System.Text;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>Zahlen und Datum in der Form der Zielsprache, Pseudoübersetzung, Höchstlänge.</summary>
public class AutoLocalizerTests
{
    [Fact]
    public void NumbersAndDatesTakeTheFormOfTheTargetLanguage()
    {
        Assert.Equal("The pump weighs 1,234.5 kg and costs 0.75 euros.",
            AutoLocalizer.Localize("Die Pumpe wiegt 1.234,5 kg und kostet 0,75 Euro.", "The pump weighs 1.234,5 kg and costs 0,75 euros.", "de-DE", "en-GB"));
        Assert.Equal("Die Pumpe wiegt 1.234,5 kg.",
            AutoLocalizer.Localize("The pump weighs 1,234.5 kg.", "Die Pumpe wiegt 1,234.5 kg.", "en-GB", "de-DE"));
        Assert.Equal("Pompa waży 12 345,5 kg i 1234 g.",                                   // polnisch: Leerzeichen, vierstellig ohne
            AutoLocalizer.Localize("Die Pumpe wiegt 12.345,5 kg und 1.234 g.", "Pompa waży 12.345,5 kg i 1.234 g.", "de-DE", "pl-PL"));
        Assert.Equal("Filed on 12/03/2024 with ⟨1⟩2.5⟨/1⟩ Nm.",
            AutoLocalizer.Localize("Eingereicht am 12.03.2024 mit ⟨1⟩2,5⟨/1⟩ Nm.", "Filed on 12.03.2024 with ⟨1⟩2,5⟨/1⟩ Nm.", "de-DE", "en-GB"));
        // schon richtig, Versionen, Uhrzeiten und IP-Adressen bleiben; gleiche Konvention ändert nichts
        Assert.Equal("Version 1.2.3 at 14:30 on 192.168.0.1 costs 2.5 euros.",
            AutoLocalizer.Localize("Version 1.2.3 um 14:30 auf 192.168.0.1 kostet 2,5 Euro.", "Version 1.2.3 at 14:30 on 192.168.0.1 costs 2.5 euros.", "de-DE", "en-GB"));
        Assert.Equal("Il pèse 2,5 kg.", AutoLocalizer.Localize("Er wiegt 2,5 kg.", "Il pèse 2,5 kg.", "de-DE", "fr-FR"));
    }

    [Fact]
    public void QaReportsNumberFormatsAndLengthLimits()
    {
        var s = new Segment { Id = 1, Source = "Gewicht: 1.234,5 kg", Target = "Weight: 1.234,5 kg" };
        var issues = QaChecker.CheckSegment(s, "de-DE", "en-GB", new QaSettings(), null).ToList();
        Assert.Contains(issues, i => i.Check == "Zahlenformat" && i.Message.Contains("1,234.5"));

        s.Target = "Weight: 1,234.5 kg";
        s.MaxLength = 10;
        issues = QaChecker.CheckSegment(s, "de-DE", "en-GB", new QaSettings(), null).ToList();
        Assert.DoesNotContain(issues, i => i.Check == "Zahlenformat");
        Assert.Contains(issues, i => i.Check == "Höchstlänge" && i.Message.Contains("18"));
        s.MaxLength = null;
        Assert.Contains(QaChecker.CheckSegment(s, "de-DE", "en-GB", new QaSettings { MaxChars = 12 }, null), i => i.Check == "Höchstlänge");
        Assert.DoesNotContain(QaChecker.CheckSegment(s, "de-DE", "en-GB", new QaSettings(), null), i => i.Check == "Höchstlänge");
    }

    [Fact]
    public void PseudoTranslationKeepsTagsAndNumbersAndGetsLonger()
    {
        var pseudo = PseudoTranslator.Translate("Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.");
        Assert.StartsWith("[Šçĥŕàüƀé ", pseudo);
        Assert.Contains("⟨1⟩5 Ñm⟨/1⟩", pseudo);
        Assert.EndsWith("~]", pseudo);
        Assert.True(pseudo.Length > "Schraube mit ⟨1⟩5 Nm⟨/1⟩ anziehen.".Length * 1.25);
        Assert.Equal(TagText.TagSignature("⟨1⟩5 Nm⟨/1⟩"), TagText.TagSignature(pseudo));
    }

    [Fact]
    public void XliffMaxWidthBecomesTheSegmentLimit()
    {
        var xliff = """
            <?xml version="1.0" encoding="utf-8"?>
            <xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
              <file source-language="de-DE" target-language="en-GB" datatype="plaintext" original="app">
                <body>
                  <trans-unit id="ok" maxwidth="12" size-unit="char"><source>Speichern</source></trans-unit>
                  <trans-unit id="free"><source>Abbrechen</source></trans-unit>
                </body>
              </file>
            </xliff>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xliff));
        var doc = new XliffFilter().Read(ms, "app.xlf", "de-DE", "en-GB", new Segmenter("de-DE"));
        var segs = doc.Segments.ToList();
        Assert.Equal(12, segs[0].MaxLength);
        Assert.Null(segs[1].MaxLength);
    }
}
