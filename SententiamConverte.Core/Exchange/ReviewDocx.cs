using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SententiamConverte.Core.Exchange;

/// <summary>Ergebnis des Rückimports einer Prüfdatei.</summary>
public sealed record ReviewImportResult(int Changed, int Comments, int Unchanged, List<int> TagWarnings, List<string> Problems);

/// <summary>
/// Zweisprachige Prüfdatei für das Lektorat ohne Übersetzungsprogramm: eine Word-Tabelle mit Nummer, Ausgangstext,
/// Übersetzung, Status und Anmerkung. Der Prüfer korrigiert nur die Spalte „Übersetzung“ (gern mit der
/// Änderungsverfolgung von Word) und schreibt Anmerkungen in die letzte Spalte oder als Word-Kommentar. Beim
/// Rückimport werden Änderungen übernommen (Änderungsverfolgung gilt als angenommen), der vorige Stand bleibt im
/// Verlauf des Segments. Formatierungs-Tags (⟨1⟩ … ⟨/1⟩) stehen grau im Text und müssen erhalten bleiben.
/// </summary>
public static class ReviewDocx
{
    private const string Marker = "sententiam-review";

    private static readonly Dictionary<string, string[]> Headers = new()
    {
        ["de"] = ["Nr.", "Ausgangstext", "Übersetzung", "Status", "Anmerkung"],
        ["en"] = ["No.", "Source text", "Translation", "Status", "Comment"],
    };

    private static string Note(string lang) => lang == "en"
        ? "Please edit only the “Translation” column (Word's track changes are welcome) and write remarks in the “Comment” column or as Word comments. Keep the grey tags such as ⟨1⟩ … ⟨/1⟩ – they carry the formatting. Do not add or remove rows."
        : "Bitte nur die Spalte „Übersetzung“ bearbeiten (gern mit der Änderungsverfolgung von Word) und Anmerkungen in die Spalte „Anmerkung“ oder als Word-Kommentar schreiben. Die grauen Tags wie ⟨1⟩ … ⟨/1⟩ tragen die Formatierung und müssen erhalten bleiben. Keine Zeilen einfügen oder löschen.";

    public static string StatusName(SegmentStatus s, string lang) => (lang == "en"
        ? s switch
        {
            SegmentStatus.NotTranslated => "not translated", SegmentStatus.Draft => "draft", SegmentStatus.Translated => "translated",
            SegmentStatus.TranslationRejected => "rejected", SegmentStatus.Reviewed => "reviewed", SegmentStatus.ReviewRejected => "review rejected",
            _ => "signed off",
        }
        : s switch
        {
            SegmentStatus.NotTranslated => "nicht übersetzt", SegmentStatus.Draft => "Entwurf", SegmentStatus.Translated => "übersetzt",
            SegmentStatus.TranslationRejected => "abgelehnt", SegmentStatus.Reviewed => "lektoriert", SegmentStatus.ReviewRejected => "Lektorat abgelehnt",
            _ => "freigegeben",
        });

    /// <summary>Prüfdatei schreiben. <paramref name="uiLanguage"/>: „de“ oder „en“ für Überschriften und Hinweis.</summary>
    public static void Export(BilingualDocument doc, Stream output, string uiLanguage = "de", bool onlyTranslated = false)
    {
        var lang = uiLanguage == "en" ? "en" : "de";
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            word.PackageProperties.Identifier = $"{Marker}:{doc.Id}";
            word.PackageProperties.Keywords = $"{doc.SourceLanguage} {doc.TargetLanguage}";
            word.PackageProperties.Title = doc.FileName;
            var main = word.AddMainDocumentPart();
            var body = new W.Body();
            body.Append(Para(doc.FileName, bold: true, size: 28));
            body.Append(Para($"{doc.SourceLanguage} → {doc.TargetLanguage} · {DateTime.Now:yyyy-MM-dd}", size: 18, color: "666666"));
            body.Append(Para(Note(lang), size: 18, color: "444444"));

            var table = new W.Table(new W.TableProperties(
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "BFBFBF" })),
                new W.TableGrid(new W.GridColumn { Width = "700" }, new W.GridColumn { Width = "5600" }, new W.GridColumn { Width = "5600" },
                    new W.GridColumn { Width = "1300" }, new W.GridColumn { Width = "2200" }));
            var header = new W.TableRow(new W.TableRowProperties(new W.TableHeader()));
            foreach (var h in Headers[lang]) header.Append(Cell([Para(h, bold: true, size: 18)], "D9E2F3"));
            table.Append(header);
            foreach (var s in doc.Segments.Where(s => !s.Locked && (!onlyTranslated || s.Target.Length > 0)))
            {
                table.Append(new W.TableRow(
                    Cell([Para(s.Id.ToString(), size: 18, color: "666666")], "F2F2F2"),
                    Cell([Tagged(s.Source)], "F7F7F7"),
                    Cell([Tagged(s.Target)]),
                    Cell([Para(StatusName(s.Status, lang), size: 16, color: "666666")], "F7F7F7"),
                    Cell([Para(s.Comment ?? "", size: 18)])));
            }
            body.Append(table);
            body.Append(new W.SectionProperties(
                new W.PageSize { Width = 16838, Height = 11906, Orient = W.PageOrientationValues.Landscape },
                new W.PageMargin { Top = 720, Bottom = 720, Left = 720, Right = 720, Header = 360, Footer = 360, Gutter = 0 }));
            main.Document = new W.Document(body);
            main.Document.Save();
        }
        ms.Position = 0;
        ms.CopyTo(output);
    }

    private static W.TableCell Cell(IEnumerable<OpenXmlElement> content, string? fill = null)
    {
        var props = new W.TableCellProperties();
        if (fill != null) props.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        var cell = new W.TableCell(props);
        foreach (var c in content) cell.Append(c);
        return cell;
    }

    private static W.Paragraph Para(string text, bool bold = false, int size = 20, string? color = null)
    {
        var rp = new W.RunProperties();
        if (bold) rp.Append(new W.Bold());
        if (color != null) rp.Append(new W.Color { Val = color });
        rp.Append(new W.FontSize { Val = size.ToString() });
        return new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "60" }),
            new W.Run(rp, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    /// <summary>Segmenttext mit grau gesetzten Tags.</summary>
    private static W.Paragraph Tagged(string text)
    {
        var p = new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "0" }));
        foreach (var part in TagText.Split(text))
        {
            var rp = new W.RunProperties(new W.FontSize { Val = "20" });
            string value;
            if (part.Tag is { } tag)
            {
                rp.Append(new W.Color { Val = "8C8C8C" });
                value = text.Substring(tag.Index, tag.Length);
            }
            else value = part.Text!;
            p.Append(new W.Run(rp, new W.Text(value) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return p;
    }

    /// <summary>Gehört die Datei zu diesem Dokument? (Kennung beim Export gespeichert.)</summary>
    public static string? DocumentIdOf(Stream input)
    {
        using var word = WordprocessingDocument.Open(input, false);
        var id = word.PackageProperties.Identifier;
        return id != null && id.StartsWith(Marker + ":", StringComparison.Ordinal) ? id[(Marker.Length + 1)..] : null;
    }

    /// <summary>Text einer Zelle: eingefügter Text zählt, gelöschter nicht (Änderungsverfolgung gilt als angenommen).</summary>
    private static string CellText(W.TableCell cell) =>
        string.Join(" ", cell.Elements<W.Paragraph>().Select(p =>
            string.Concat(p.Descendants().Select(e => e switch
            {
                W.Text t when !t.Ancestors<W.DeletedRun>().Any() => t.Text,
                W.TabChar => "\t",
                W.Break => " ",
                _ => "",
            })))).Trim();

    /// <summary>
    /// Prüfdatei zurücklesen. Geänderte Übersetzungen werden übernommen (vorige Fassung bleibt im Verlauf) und – mit
    /// <paramref name="project"/> – als lektoriert bestätigt und ins TM geschrieben. Anmerkungen aus der Spalte und
    /// Word-Kommentare kommen in den Segmentkommentar.
    /// </summary>
    public static ReviewImportResult Import(Stream input, BilingualDocument doc, TranslationProject? project = null, bool markReviewed = true,
        bool markUnchangedReviewed = false)
    {
        var ms = new MemoryStream();
        input.CopyTo(ms);
        ms.Position = 0;
        var problems = new List<string>();
        var tagWarnings = new List<int>();
        int changed = 0, comments = 0, unchanged = 0;
        using var word = WordprocessingDocument.Open(ms, false);
        var id = word.PackageProperties.Identifier;
        if (id != $"{Marker}:{doc.Id}")
            problems.Add(id?.StartsWith(Marker) == true
                ? "Die Prüfdatei gehört zu einer anderen Datei oder Sprache – zugeordnet wird nur über die Segmentnummern."
                : "Keine Prüfdatei dieses Programms – zugeordnet wird nur über die Segmentnummern.");
        var body = word.MainDocumentPart?.Document.Body ?? throw new InvalidDataException("Leeres Word-Dokument.");
        var wordComments = word.MainDocumentPart!.WordprocessingCommentsPart?.Comments?.Elements<W.Comment>()
            .ToDictionary(c => c.Id?.Value ?? "", c => string.Join(" ", c.Elements<W.Paragraph>().Select(p => p.InnerText)).Trim()) ?? new();
        var byId = doc.Segments.ToDictionary(s => s.Id);
        var table = body.Elements<W.Table>().FirstOrDefault() ?? throw new InvalidDataException("In der Datei steht keine Prüftabelle.");
        foreach (var row in table.Elements<W.TableRow>().Skip(1))
        {
            var cells = row.Elements<W.TableCell>().ToList();
            if (cells.Count < 3) continue;
            if (!int.TryParse(CellText(cells[0]), out var segId) || !byId.TryGetValue(segId, out var seg))
            {
                problems.Add($"Zeile ohne gültige Segmentnummer übersprungen: „{CellText(cells[0])}“.");
                continue;
            }
            var target = CellText(cells[2]);
            var note = cells.Count > 4 ? CellText(cells[4]) : "";
            var inWord = row.Descendants<W.CommentReference>().Select(r => wordComments.GetValueOrDefault(r.Id?.Value ?? ""))
                .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var comment = string.Join(" · ", new[] { note }.Concat(inWord!).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
            if (comment.Length > 0 && comment != (seg.Comment ?? ""))
            {
                seg.Comment = comment;
                comments++;
            }
            else if (comment.Length == 0 && note.Length == 0 && !string.IsNullOrEmpty(seg.Comment)) { seg.Comment = null; comments++; }

            if (target == seg.Target.Trim() || target.Length == 0 && seg.Target.Length == 0)
            {
                unchanged++;
                // durchgesehen und nicht beanstandet: auf Wunsch als lektoriert
                if (markUnchangedReviewed && seg.Status == SegmentStatus.Translated) seg.Status = SegmentStatus.Reviewed;
                continue;
            }
            if (target.Length == 0) { problems.Add($"Segment {segId}: Übersetzung gelöscht – nicht übernommen."); continue; }
            if (TagText.TagSignature(target) != TagText.TagSignature(seg.Source)) tagWarnings.Add(segId);
            // vorige Fassung im Verlauf und für die Änderungsverfolgung festhalten
            if (seg.Target.Length > 0) seg.ReviewBaseline ??= seg.Target;
            if (seg.Target.Length > 0 && seg.History.LastOrDefault()?.Target != seg.Target)
                seg.History.Add(new SegmentRevision(seg.Modified ?? DateTime.UtcNow, seg.Target, seg.Status, seg.Origin));
            seg.Target = target;
            seg.Origin = TranslationOrigin.Manual;
            seg.Modified = DateTime.UtcNow;
            var status = markReviewed ? SegmentStatus.Reviewed : SegmentStatus.Translated;
            if (project != null) Workflow.Confirm(project, doc, seg, status);
            else
            {
                seg.Status = status;
                seg.History.Add(new SegmentRevision(DateTime.UtcNow, seg.Target, status, seg.Origin));
            }
            changed++;
        }
        return new ReviewImportResult(changed, comments, unchanged, tagWarnings, problems);
    }
}
