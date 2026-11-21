using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Segmentation;

namespace SententiamConverte.Core.Tests;

/// <summary>
/// Recherche-Gedächtnis über alle Projekte: was ein Projekt recherchiert, nutzen die anderen – ohne Satzzusammenhang und
/// eigene Zitate, aus vertraulichen Projekten nur mit Schalter, Belegtes wird nie durch Unbelegtes ersetzt.
/// </summary>
public sealed class ResearchMemoryTests
{
    private static readonly SourceRef Web = new("en-GB", "Pumps", "https://pumps.example/") { Number = 1, Quote = "A mechanical seal keeps the pump tight." };

    private static TermDossier Glrd(string recommended = "mechanical seal", bool evidenced = true) => new()
    {
        Expression = "GLRD", SourceLanguage = "de-DE", TargetLanguage = "en-GB", Recommended = recommended, Meaning = "Gleitringdichtung",
        Context = "Herr Beispiel meldet: Die GLRD ist verschlissen.", SegmentIds = [3], FileId = "f1",
        Sources = evidenced
            ? [Web, new SourceRef("en-GB", "Eigene Fachtexte", @"C:\Kunde\Handbuch.pdf") { Number = 2, Quote = "Kunde X: mechanical seal Typ 4711" }]
            : [],
    };

    private static (BilingualDocument Doc, Segment Segment) Doc(string text)
    {
        var doc = new BilingualDocument { FileName = "a.txt", SourceLanguage = "de-DE", TargetLanguage = "en-GB" };
        var paragraph = new Paragraph { Locator = "p0" };
        new Segmenter("de-DE").Segment(paragraph, text);
        doc.Paragraphs.Add(paragraph);
        return (doc, doc.Segments.Single());
    }

    [Fact]
    public void WhatOneProjectResearchedHelpsTheNext()
    {
        using var dir = new TempDir();
        ResearchMemory.UseFolderHere(dir.File("gedaechtnis"));
        var first = TranslationProject.Create(dir.File("a"), "Pumpe A", "de-DE", ["en-GB"]);
        first.Topic = "Pumpentechnik";
        DossierStore.Add(first, Glrd());

        // aufgenommen – ohne Satzzusammenhang, Datei, Segmente und das Zitat aus eigenen Fachtexten
        var entry = Assert.Single(ResearchMemory.Entries("de-DE", "en-GB"));
        Assert.Equal(("Pumpe A", "Pumpentechnik"), (entry.Project, entry.Topic));
        Assert.Null(entry.Dossier.Context);
        Assert.Null(entry.Dossier.FileId);
        Assert.Empty(entry.Dossier.SegmentIds);
        Assert.Equal(["https://pumps.example/"], entry.Dossier.Sources.Select(s => s.Url));
        Assert.True(DossierStore.IsEvidenced(entry.Dossier));

        // ein unbelegtes Ergebnis ersetzt das belegte nicht
        DossierStore.Add(first, Glrd("seal", evidenced: false));
        Assert.Equal("mechanical seal", ResearchMemory.Find("glrd", "de-DE", "en-GB")!.Dossier.Recommended);

        // ein anderes Projekt zum selben Thema: übernehmen statt recherchieren – mit Herkunft
        var (reuse, previous) = ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", "Pumpentechnik, Wartung", null);
        Assert.NotNull(reuse);
        Assert.Null(previous);
        Assert.StartsWith("Pumpe A (", reuse!.Origin);
        // anderes Thema: nicht übernehmen, nur dort ansetzen
        (reuse, previous) = ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", "Gartenbau", null);
        Assert.Null(reuse);
        Assert.Equal("mechanical seal", previous!.Recommended);
        // das Projekt hat schon ein belegtes Dossier: das gilt, nichts übernehmen
        var own = Glrd("face seal");
        own.Sources = [Web with { Quote = "A face seal keeps the pump tight." }];
        (reuse, previous) = ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", "Pumpentechnik", own);
        Assert.Null(reuse);
        Assert.Equal("face seal", previous!.Recommended);
        // Übernommenes geht nicht noch einmal ins Gedächtnis
        var second = TranslationProject.Create(dir.File("b"), "Pumpe B", "de-DE", ["en-GB"]);
        second.Topic = "Pumpentechnik";
        DossierStore.Add(second, ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", "Pumpentechnik", null).Reuse!);
        Assert.Equal("Pumpe A", ResearchMemory.Find("GLRD", "de-DE", "en-GB")!.Project);

        // die Übersetzung eines neuen Projekts nutzt den belegten Eintrag, auch ohne eigenes Dossier
        var third = TranslationProject.Create(dir.File("c"), "Pumpe C", "de-DE", ["en-GB"]);
        third.Topic = "Pumpentechnik";
        var (doc, segment) = Doc("Die GLRD ist verschlissen.");
        Assert.Equal(["GLRD"], Workflow.Research(third, doc, segment).Select(d => d.Expression));
        // anderes Zielland: nichts
        Assert.Empty(ResearchMemory.Entries("de-DE", "en-US"));

        ResearchMemory.Forget("de-DE", "en-GB", "GLRD");
        Assert.Empty(ResearchMemory.Entries("de-DE", "en-GB"));
        Assert.Empty(Workflow.Research(third, doc, segment));
        first.Dispose();
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public void ConfidentialProjectsShareOnlyWhenSwitchedOn()
    {
        using var dir = new TempDir();
        ResearchMemory.UseFolderHere(dir.File("gedaechtnis"));
        var secret = TranslationProject.Create(dir.File("a"), "Vertraulich", "de-DE", ["en-GB"]);
        secret.Privacy.Enabled = true;
        DossierStore.Add(secret, Glrd());
        Assert.Empty(ResearchMemory.Entries("de-DE", "en-GB"));

        secret.Privacy.ShareResearch = true;
        DossierStore.Add(secret, Glrd());
        Assert.Single(ResearchMemory.Entries("de-DE", "en-GB"));
        secret.Dispose();
    }

    [Fact]
    public void ExplanationsInAnotherLanguageAreNeitherShownNorTakenOver()
    {
        using var dir = new TempDir();
        ResearchMemory.UseFolderHere(dir.File("gedaechtnis"));
        var project = TranslationProject.Create(dir.File("a"), "Pumpe", "de-DE", ["en-GB"]);
        var german = Glrd();
        german.ExplanationLanguage = "Deutsch";
        DossierStore.Add(project, german);
        var polish = Glrd();
        polish.ExplanationLanguage = "Polski";
        polish.Meaning = "uszczelnienie mechaniczne";
        DossierStore.Add(project, polish);

        // je Erklärungssprache ein Eintrag – der polnische ersetzt den deutschen nicht
        Assert.Equal(["Deutsch", "Polski"], ResearchMemory.Entries("de-DE", "en-GB").Select(e => ResearchMemory.ExplainedIn(e.Dossier)).Order());
        // übernommen wird nur, was in der eingestellten Erklärungssprache steht
        Assert.Equal("uszczelnienie mechaniczne", ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", null, null, "Polski").Reuse!.Meaning);
        Assert.Equal("Gleitringdichtung", ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", null, null, "Deutsch").Reuse!.Meaning);
        // gibt es nur andere Erklärungssprachen: nichts übernehmen, aber dort ansetzen (Kandidaten, Anfragen, Belege)
        var (reuse, previous) = ResearchMemory.Lookup("GLRD", "de-DE", "en-GB", null, null, "Français");
        Assert.Null(reuse);
        Assert.Equal("mechanical seal", previous!.Recommended);
        // die Übersetzung bekommt dann nur die belegte Benennung – ohne fremden Erklärungstext
        var neutral = Assert.Single(ResearchMemory.ForTranslation("de-DE", "en-GB", null, [], "Français"));
        Assert.Equal(("mechanical seal", ""), (neutral.Recommended, neutral.Meaning));
        Assert.Equal("uszczelnienie mechaniczne", Assert.Single(ResearchMemory.ForTranslation("de-DE", "en-GB", null, [], "Polski")).Meaning);
        // ältere Dossiers ohne Angabe gelten als deutsch erklärt
        Assert.Equal("Deutsch", ResearchMemory.ExplainedIn(new TermDossier()));
        project.Dispose();
    }

    [Fact]
    public void TopicsMatchByASharedWord()
    {
        Assert.True(ResearchMemory.SameTopic("Pumpentechnik", "pumpentechnik"));
        Assert.True(ResearchMemory.SameTopic("Kreiselpumpen, Wartung", "Wartung von Anlagen"));
        Assert.True(ResearchMemory.SameTopic(null, "Recht"));                      // unbekannt: kein Hindernis
        Assert.False(ResearchMemory.SameTopic("Pumpentechnik", "Familienrecht"));
    }
}
