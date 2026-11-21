using SententiamConverte.Core.Resources;

namespace SententiamConverte.Core.Tests;

/// <summary>Katalog der Hilfsmittel: gültige Einträge, passende Vorschläge, Nachschlage-Adressen.</summary>
public sealed class TranslatorToolsTests
{
    [Fact]
    public void EveryEntryIsComplete()
    {
        Assert.Equal(TranslatorTools.All.Count, TranslatorTools.All.Select(t => t.Id).Distinct().Count());
        Assert.All(TranslatorTools.All, t =>
        {
            Assert.StartsWith("https://", t.Url);
            Assert.True(t.SearchUrl == null || t.SearchUrl.StartsWith("https://") && t.SearchUrl.Contains("{q}"), t.Id);
            Assert.NotEmpty(t.Description);
            Assert.NotEmpty(t.Languages);
            Assert.All(t.Languages, l => Assert.Matches("^([a-z]{2,3}|\\*)$", l));
            Assert.DoesNotMatch("(?i)trados|\\bsdl\\b|multiterm", t.Name + t.Description);
        });
    }

    [Fact]
    public void SuggestionsFitLanguagesAndTopic()
    {
        var law = TranslatorTools.Suggest("de-DE", ["fr-FR"], "Vertragsrecht", 12);
        var ids = law.Select(s => s.Tool.Id).ToList();
        Assert.Contains("termdat", ids);                                                  // Deutsch und Französisch, Recht
        Assert.Contains("iate", ids);
        Assert.Contains("eurlex", ids);
        Assert.Contains("legifrance", ids);
        Assert.DoesNotContain("rae", ids);                                                // Spanisch passt nicht
        Assert.True(ids.IndexOf("termdat") < ids.IndexOf("wiktionary") || !ids.Contains("wiktionary"));
        var termdat = law.Single(s => s.Tool.Id == "termdat");
        Assert.Equal(["de", "fr"], termdat.Languages);
        Assert.Equal(TranslatorTools.Law, termdat.Topic);

        // Ohne Rechtsbezug keine Rechtsquellen ganz oben
        var tech = TranslatorTools.Suggest("de-DE", ["fr-FR"], "Maschinenbau", 6).Select(s => s.Tool.Id).ToList();
        Assert.DoesNotContain("legifrance", tech);
        Assert.Contains("termium", TranslatorTools.Suggest("en-GB", ["fr-CA"], "Medizin", 6).Select(s => s.Tool.Id));

        // Rätoromanisch
        var rm = TranslatorTools.Suggest("de-CH", ["rm-CH"], "Verwaltung", 12).Select(s => s.Tool.Id).ToList();
        Assert.Contains("pledari", rm);
        Assert.Contains("termdat", rm);
        Assert.Contains("fedlex", rm);
        Assert.Contains("dwds", rm);                                                      // je Sprache das eigene Wörterbuch
    }

    [Fact]
    public void LookupAddressesCarryTermAndLanguages()
    {
        Assert.Equal("https://www.dwds.de/wb/Gleitringdichtung", TranslatorTools.ById("dwds")!.LookupUrl("Gleitringdichtung", "de-DE"));
        Assert.Equal("https://pl.wiktionary.org/wiki/umowa%20najmu", TranslatorTools.ById("wiktionary")!.LookupUrl(" umowa najmu ", "pl-PL"));
        Assert.Equal("https://tatoeba.org/de/sentences/search?query=Vertrag&from=deu&to=fra",
            TranslatorTools.ById("tatoeba")!.LookupUrl("Vertrag", "de-DE", "fr-FR"));
        Assert.Equal("https://www.pledarigrond.ch/", TranslatorTools.ById("pledari")!.LookupUrl("Haus", "rm-CH"));   // ohne Suchadresse: Startseite
        Assert.Equal(TranslatorTools.Law, TranslatorTools.TopicCategory("Arbeitsrecht"));
        Assert.Equal(TranslatorTools.Medicine, TranslatorTools.TopicCategory("Pharmazie"));
        Assert.Null(TranslatorTools.TopicCategory(""));
    }
}
