using System.Text;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Tests;

/// <summary>Anonymisierte Fassung: geschützte Angaben im ganzen Dokument einheitlich ersetzt, Layout über den Dateifilter.</summary>
public sealed class AnonymizerTests
{
    [Fact]
    public void SourceAndTargetAreAnonymizedConsistently()
    {
        using var dir = new TempDir();
        var project = TranslationProject.Create(dir.File("p"), "Vertrag", "de-DE", ["en-GB"]);
        var file = project.AddFile(new MemoryStream("Herr Müller unterschreibt am 12.03.2024.\nHerr Müller zahlt an anna@beispiel.de.\nDanke.\n"u8.ToArray()), "vertrag.txt");
        var doc = project.LoadDocument(file, "en-GB");
        var segs = doc.Segments.ToList();
        segs[0].Target = "Mr Müller signs on 12/03/2024.";
        segs[1].Target = "Mr Müller pays to anna@beispiel.de.";
        project.SaveDocument(file, doc);

        var source = new MemoryStream();
        var replaced = project.ExportAnonymized(file, "en-GB", source: true, source);
        var text = Encoding.UTF8.GetString(source.ToArray());
        Assert.DoesNotContain("Müller", text);
        Assert.DoesNotContain("anna@beispiel.de", text);
        Assert.DoesNotContain("12.03.2024", text);
        Assert.Contains("[NAME_1] unterschreibt", text);
        Assert.Contains("[NAME_1] zahlt", text);                                           // gleicher Name, gleicher Platzhalter
        Assert.Contains("Danke.", text);
        Assert.True(replaced >= 3);

        var target = new MemoryStream();
        project.ExportAnonymized(file, "en-GB", source: false, target);
        var translated = Encoding.UTF8.GetString(target.ToArray());
        Assert.StartsWith("Mr [NAME_1] signs on", translated);
        Assert.DoesNotContain("anna@beispiel.de", translated);
        Assert.Contains("Danke.", translated);                                             // unübersetzt: Ausgangstext, anonymisiert
        Assert.Equal("vertrag_en-GB_anonymisiert.txt", project.AnonymizedFileName(file, "en-GB", source: false));
        Assert.Equal("vertrag_anonymisiert.txt", project.AnonymizedFileName(file, "en-GB", source: true));

        // das Projekt selbst bleibt unverändert
        Assert.Equal("Mr Müller signs on 12/03/2024.", project.LoadDocument(file, "en-GB").Segments.First().Target);
        project.Dispose();
    }
}
