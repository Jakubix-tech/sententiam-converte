using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.Core.Analysis;

/// <summary>Abrechnungseinheit eines Angebots.</summary>
public enum RateUnit
{
    Word,
    Character,
    /// <summary>Normzeile: 55 Zeichen einschließlich Leerzeichen (im deutschen Sprachraum üblich).</summary>
    StandardLine,
}

/// <summary>Preise eines Projekts: Grundpreis je Einheit und Anteil je Trefferstufe (in Prozent des Grundpreises).</summary>
public sealed class RateSettings
{
    public string Currency { get; set; } = "EUR";
    public RateUnit Unit { get; set; } = RateUnit.Word;
    public decimal Price { get; set; } = 0.12m;
    public decimal MinimumFee { get; set; }
    public decimal VatPercent { get; set; } = 19;

    /// <summary>Anteil des Grundpreises je Trefferstufe; fehlende Stufen zählen voll.</summary>
    public Dictionary<MatchBand, int> Weights { get; set; } = DefaultWeights();

    public static Dictionary<MatchBand, int> DefaultWeights() => new()
    {
        [MatchBand.PerfectMatch] = 10,
        [MatchBand.Repetition] = 25,
        [MatchBand.Exact] = 25,
        [MatchBand.Fuzzy95] = 50,
        [MatchBand.Fuzzy85] = 60,
        [MatchBand.Fuzzy75] = 75,
        [MatchBand.Fuzzy50] = 100,
        [MatchBand.NoMatch] = 100,
        [MatchBand.Locked] = 0,
    };

    public int WeightOf(MatchBand band) => Weights.TryGetValue(band, out var w) ? w : 100;

    public RateSettings Clone() => new()
    {
        Currency = Currency, Unit = Unit, Price = Price, MinimumFee = MinimumFee, VatPercent = VatPercent, Weights = new(Weights),
    };
}

public sealed record QuoteLine(MatchBand Band, decimal Units, int Weight, decimal WeightedUnits, decimal Amount);

/// <summary>Angebot für eine Zielsprache.</summary>
public sealed record Quote(string Language, List<QuoteLine> Lines, decimal Units, decimal WeightedUnits, decimal Net, bool MinimumApplied)
{
    public decimal SavingsPercent => Units == 0 ? 0 : Math.Round(100 - WeightedUnits * 100 / Units, 1);
}

/// <summary>
/// Kostenvoranschlag aus der Analyse: Einheiten je Trefferstufe × Anteil × Grundpreis. Wiederholungen und
/// TM-Treffer kosten weniger, gesperrte Segmente nichts; ein Mindestbetrag gilt je Zielsprache.
/// </summary>
public static class QuoteCalculator
{
    public const int CharactersPerLine = 55;

    public static decimal UnitsOf(BandCount c, RateUnit unit) => unit switch
    {
        RateUnit.Word => c.Words,
        RateUnit.Character => c.Characters,
        _ => Math.Round((decimal)c.Characters / CharactersPerLine, 2),
    };

    public static Quote Calculate(AnalysisResult analysis, RateSettings rates, string language)
    {
        var lines = new List<QuoteLine>();
        foreach (var (band, count) in analysis.Bands.Where(b => b.Value.Segments > 0))
        {
            var units = UnitsOf(count, rates.Unit);
            var weight = rates.WeightOf(band);
            var weighted = Math.Round(units * weight / 100m, 2);
            lines.Add(new QuoteLine(band, units, weight, weighted, Math.Round(weighted * rates.Price, 2)));
        }
        var net = lines.Sum(l => l.Amount);
        var minimum = net < rates.MinimumFee;
        return new Quote(language, lines, lines.Sum(l => l.Units), lines.Sum(l => l.WeightedUnits), minimum ? rates.MinimumFee : net, minimum);
    }

    public static string UnitName(RateUnit unit, string lang) => (unit, lang == "en") switch
    {
        (RateUnit.Word, false) => "Wörter",
        (RateUnit.Character, false) => "Zeichen",
        (RateUnit.StandardLine, false) => "Normzeilen",
        (RateUnit.Word, true) => "words",
        (RateUnit.Character, true) => "characters",
        _ => "standard lines",
    };

    /// <summary>Angebot als Word-Datei (alle Zielsprachen, netto, MwSt., gesamt).</summary>
    public static void ExportDocx(string projectName, string sourceLanguage, IReadOnlyList<Quote> quotes, RateSettings rates, Stream output, string uiLanguage = "de")
    {
        var en = uiLanguage == "en";
        var culture = System.Globalization.CultureInfo.GetCultureInfo(en ? "en-GB" : "de-DE");
        string Money(decimal v) => v.ToString("N2", culture) + " " + rates.Currency;
        string Num(decimal v) => v.ToString(v == Math.Floor(v) ? "N0" : "N2", culture);
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(Para(en ? "Quotation" : "Kostenvoranschlag", true, 32));
            body.Append(Para($"{projectName} · {DateTime.Now.ToString("d", culture)}", false, 20, "666666"));
            body.Append(Para((en ? "Price per " : "Preis je ") + SingleUnitName(rates.Unit, en) + ": " +
                             rates.Price.ToString("0.00##", culture) + " " + rates.Currency, false, 20));
            var net = 0m;
            foreach (var q in quotes)
            {
                body.Append(Para($"{sourceLanguage} → {q.Language}", true, 24));
                var table = new W.Table(new W.TableProperties(new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                    new W.TableBorders(new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" },
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D9" })));
                string[] head = en
                    ? ["Match band", UnitName(rates.Unit, uiLanguage), "Share", "Weighted", "Amount"]
                    : ["Trefferstufe", UnitName(rates.Unit, uiLanguage), "Anteil", "Gewichtet", "Betrag"];
                table.Append(Row(head, bold: true));
                foreach (var l in q.Lines)
                    table.Append(Row([BandName(l.Band, en), Num(l.Units), l.Weight + " %", Num(l.WeightedUnits), Money(l.Amount)]));
                table.Append(Row([en ? "Total" : "Summe", Num(q.Units), "", Num(q.WeightedUnits), Money(q.Lines.Sum(l => l.Amount))], bold: true));
                body.Append(table);
                if (q.MinimumApplied)
                    body.Append(Para((en ? "Minimum fee applies: " : "Es gilt der Mindestbetrag: ") + Money(q.Net), false, 20));
                net += q.Net;
            }
            var vat = Math.Round(net * rates.VatPercent / 100m, 2);
            body.Append(Para("", false, 12));
            body.Append(Para((en ? "Net: " : "Netto: ") + Money(net), true, 22));
            if (rates.VatPercent > 0)
                body.Append(Para((en ? "VAT " : "MwSt. ") + rates.VatPercent.ToString("0.#", culture) + " %: " + Money(vat), false, 22));
            body.Append(Para((en ? "Total: " : "Gesamt: ") + Money(net + vat), true, 26));
            body.Append(Para(en
                ? "Based on an analysis of the source files against the available translation memories. Repetitions and matches are charged at the share shown."
                : "Grundlage ist die Analyse der Ausgangsdateien gegen die vorhandenen Translation Memories. Wiederholungen und Treffer werden mit dem angegebenen Anteil berechnet.",
                false, 18, "666666"));
            main.Document = new W.Document(body);
            main.Document.Save();
        }
        ms.Position = 0;
        ms.CopyTo(output);
    }

    private static string SingleUnitName(RateUnit unit, bool en) => (unit, en) switch
    {
        (RateUnit.Word, false) => "Wort",
        (RateUnit.Character, false) => "Zeichen",
        (RateUnit.StandardLine, false) => "Normzeile",
        (RateUnit.Word, true) => "word",
        (RateUnit.Character, true) => "character",
        _ => "standard line",
    };

    private static string BandName(MatchBand b, bool en) => !en ? AnalysisResult.BandName(b) : b switch
    {
        MatchBand.PerfectMatch => "Context match (101%)",
        MatchBand.Repetition => "Repetitions",
        MatchBand.NoMatch => "No match",
        MatchBand.Locked => "Locked",
        _ => AnalysisResult.BandName(b).Replace(" %", "%"),
    };

    private static W.Paragraph Para(string text, bool bold, int size, string? color = null)
    {
        var rp = new W.RunProperties();
        if (bold) rp.Append(new W.Bold());
        if (color != null) rp.Append(new W.Color { Val = color });
        rp.Append(new W.FontSize { Val = size.ToString() });
        return new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "80" }),
            new W.Run(rp, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static W.TableRow Row(string[] cells, bool bold = false)
    {
        var row = new W.TableRow();
        for (var i = 0; i < cells.Length; i++)
        {
            var p = Para(cells[i], bold, 20);
            if (i > 0) p.ParagraphProperties!.Append(new W.Justification { Val = W.JustificationValues.Right });
            row.Append(new W.TableCell(p));
        }
        return row;
    }
}
