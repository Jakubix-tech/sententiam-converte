using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Analysis;

namespace SententiamConverte.Core.Tests;

/// <summary>Kostenvoranschlag aus der Analyse.</summary>
public class QuoteTests
{
    private static AnalysisResult Analysis()
    {
        var a = new AnalysisResult();
        a.Bands[MatchBand.NoMatch].Segments = 10;
        a.Bands[MatchBand.NoMatch].Words = 1000;
        a.Bands[MatchBand.NoMatch].Characters = 5500;
        a.Bands[MatchBand.Repetition].Segments = 2;
        a.Bands[MatchBand.Repetition].Words = 200;
        a.Bands[MatchBand.Repetition].Characters = 1100;
        a.Bands[MatchBand.Locked].Segments = 1;
        a.Bands[MatchBand.Locked].Words = 3;
        return a;
    }

    [Fact]
    public void MatchesCostLessAndTheMinimumFeeApplies()
    {
        var rates = new RateSettings { Price = 0.10m };
        var q = QuoteCalculator.Calculate(Analysis(), rates, "en-GB");
        Assert.Equal(1203, q.Units);
        Assert.Equal(1050, q.WeightedUnits);                       // 1000 + 25 % von 200, Gesperrtes kostet nichts
        Assert.Equal(105.00m, q.Net);
        Assert.Equal(12.7m, q.SavingsPercent);

        rates.Unit = RateUnit.StandardLine;
        rates.Price = 1.50m;
        q = QuoteCalculator.Calculate(Analysis(), rates, "en-GB");
        Assert.Equal(120, q.Units);                                // (5500 + 1100) / 55 Normzeilen
        Assert.Equal(105, q.WeightedUnits);
        Assert.Equal(157.50m, q.Net);

        rates.MinimumFee = 200;
        q = QuoteCalculator.Calculate(Analysis(), rates, "en-GB");
        Assert.True(q.MinimumApplied);
        Assert.Equal(200, q.Net);
    }

    [Fact]
    public void TheQuoteCanBeSavedAsWord()
    {
        var rates = new RateSettings { Price = 0.10m, VatPercent = 19 };
        var quotes = new[] { QuoteCalculator.Calculate(Analysis(), rates, "en-GB"), QuoteCalculator.Calculate(Analysis(), rates, "pl-PL") };
        using var ms = new MemoryStream();
        QuoteCalculator.ExportDocx("Mietvertrag", "de-DE", quotes, rates, ms);
        ms.Position = 0;
        using var word = WordprocessingDocument.Open(ms, false);
        var text = word.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Kostenvoranschlag", text);
        Assert.Contains("de-DE → pl-PL", text);
        Assert.Contains("Netto: 210,00 EUR", text);
        Assert.Contains("MwSt. 19 %: 39,90 EUR", text);
        Assert.Contains("Gesamt: 249,90 EUR", text);
        Assert.Contains("Preis je Wort", text);
    }
}
