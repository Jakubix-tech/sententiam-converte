using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Agents;
using SententiamConverte.Services;
using SententiamConverte.ViewModels;
using SententiamConverte.Views;

namespace SententiamConverte.UiTests;

/// <summary>Recherche zum Dokument: Klick auf ein Segment zeigt das Dossier, Übernahme in die Terminologie.</summary>
public class ResearchUiTests
{
    private static async Task Run(IRelayCommand command, object? parameter = null)
    {
        if (command is IAsyncRelayCommand async) await async.ExecuteAsync(parameter);
        else command.Execute(parameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ClickingASegmentShowsItsDossier()
    {
        using var home = TestSetup.OwnDataFolder();
        var platform = new FakePlatform();
        platform.FilesToPick.Add(("kauf.txt", "La finca está inscrita en el Registro de la Propiedad.\nSe sirve zumo de naranja.\n"u8.ToArray()));
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        AppServices.Platform = platform;
        await Run(vm.BeginNewProjectCommand);
        vm.NewName = "Kauf " + Guid.NewGuid().ToString("N")[..4];
        vm.NewSource = "es-ES";
        vm.NewTargets = "es-MX";
        await Run(vm.CreateProjectCommand);
        await Run(vm.AddFilesCommand);
        DossierStore.Add(vm.Project!, new TermDossier
        {
            Expression = "Registro de la Propiedad", SourceLanguage = "es-ES", TargetLanguage = "es-MX",
            Meaning = "Öffentliches Register für Grundstücke in Spanien.",
            Candidates = [new() { Term = "Registro Público de la Propiedad", Equivalence = Core.Agents.Equivalence.Partial, SuitableWhen = "Mexiko, Bundesstaaten" }],
            Recommended = "Registro Público de la Propiedad", Strategy = "funktionale Entsprechung", Rendering = "Registro Público de la Propiedad",
            Reasoning = "In Mexiko führen die Bundesstaaten das Register.", Difficulty = ResearchDifficulty.Medium, DifficultyNote = "Nur teilweise Entsprechung – Unterschiede beachten.",
            Sources = [new SourceRef("es-MX", "Registro Público", "https://www.gob.mx/") { Number = 1, Term = "Registro Público de la Propiedad", Quote = "El Registro Público de la Propiedad es una institución" }],
            Model = "Eingebaute KI (gemma-4-e4b)", Search = "Wikipedia",
            WhereToLook = [new SiteHint("gob.mx", "es-MX", "Behörden und Register in Mexiko")],
            Previous = new TermDossier { Expression = "Registro de la Propiedad", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Recommended = "Catastro", Created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc) },
        });
        vm.Project!.SetBrief(vm.Files[0].File, "es-MX", new Core.Projects.DocumentBrief
        {
            Address = Core.Projects.AddressForm.None, Speaker = Core.Projects.SpeakerForm.None, Confirmed = true,
            DocumentKind = "Kaufvertrag über ein Grundstück", Country = "MX", CountryNotes = ["Notarielle Urkunde"],
            Conventions = [new() { Source = "Registro de la Propiedad", Target = "Registro Público de la Propiedad", Evidence = ["„Registro Público de la Propiedad“: Wikipedia: 40 Artikel"], ByFrequency = true }],
        });
        await Run(vm.OpenInEditorCommand, vm.Files[0]);
        var editor = vm.Editor;
        // Dokumentart und landesübliche Ausdrücke aus dem Steckbrief; abwählen wird gespeichert
        Assert.StartsWith("Kaufvertrag über ein Grundstück · ", editor.DocumentKindText);
        Assert.Equal("• Notarielle Urkunde", editor.ConventionNotes);
        var convention = Assert.Single(editor.Conventions);
        Assert.Equal("„Registro de la Propiedad“ → „Registro Público de la Propiedad“", convention.Title);
        Assert.Contains("nach Häufigkeit gewählt", convention.Evidence);
        convention.IsUsed = false;
        Assert.False(vm.Project.Brief(vm.Files[0].File, "es-MX")!.Conventions[0].Use);
        convention.IsUsed = true;
        Assert.Equal(("es-ES", "es-MX"), (editor.ResearchFrom, editor.ResearchTo));                // Sprache mit Land vorbelegt

        // Klick auf das Segment mit dem Ausdruck: Dossier erscheint; im anderen Segment nicht
        editor.Selected = editor.Segments[0];
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(editor.Dossiers);
        Assert.Contains("Registro Público de la Propiedad", row.Title);
        Assert.Contains("Öffentliches Register", row.Text);
        // Herkunft und Belege: Modell, Suche, nummerierte Quelle mit Zitat; Klick springt zur Stelle
        Assert.StartsWith("Erstellt mit: Eingebaute KI (gemma-4-e4b) · Suche: Wikipedia · ", row.Provenance);
        Assert.Contains("Registro Público de la Propiedad (teilweise) – ohne Beleg", row.Text);
        var source = Assert.Single(row.Sources);
        Assert.Equal("[1] es-MX · Registro Público (www.gob.mx)", source.Label);
        Assert.Equal("„El Registro Público de la Propiedad es una institución“ – Registro Público de la Propiedad", source.Quote);
        Assert.Equal("https://www.gob.mx/#:~:text=El%20Registro%20P%C3%BAblico%20de,Propiedad%20es%20una%20instituci%C3%B3n",
            EditorViewModel.WithTextFragment(source.Url, source.Source.Quote));
        Assert.Equal("https://a.example/x#teil:~:text=Grund%2Dbuch", EditorViewModel.WithTextFragment("https://a.example/x#teil", "„Grund-buch“"));
        Assert.Equal("https://a.example/", EditorViewModel.WithTextFragment("https://a.example/", null));
        // Fundorte, frühere Fassung, aufgeklappt beim Segment
        Assert.Equal("gob.mx – Behörden und Register in Mexiko", Assert.Single(row.WhereToLook).Label);
        Assert.True(row.HasPrevious);
        Assert.StartsWith("Frühere Fassung vom 01.11.2026", row.PreviousInfo);
        Assert.True(row.IsExpanded);
        editor.Selected = editor.Segments[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(editor.Dossiers);

        // Markierung „Registro“: vorhandenes Dossier zeigen, ohne neue Recherche
        editor.Selected = editor.Segments[0];
        await editor.ShowOrResearchAsync("Registro de la Propiedad");
        Assert.Equal(8, editor.SidePanelTab);
        Assert.Single(editor.Dossiers);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.Contains("Registro Público de la Propiedad") == true);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "Frühere Fassung");
        window.CaptureRenderedFrame()?.Save(Path.Combine(TestSetup.ScreenshotDir, "34-recherche-dossier.png"));

        // Frühere Fassung zurückholen (und wieder zurück); in der Liste aller Dossiers zugeklappt
        await Run(editor.RestorePreviousDossierCommand, editor.Dossiers[0]);
        Assert.Equal("Catastro", Assert.Single(DossierStore.Load(vm.Project!)).Recommended);
        await Run(editor.RestorePreviousDossierCommand, new DossierRow(DossierStore.Load(vm.Project!)[0], editor));
        Assert.Equal("Registro Público de la Propiedad", DossierStore.Load(vm.Project!)[0].Recommended);
        DossierStore.Add(vm.Project!, new TermDossier { Expression = "zumo", SourceLanguage = "es-ES", TargetLanguage = "es-MX", Recommended = "jugo" });
        editor.ShowAllDossiers = false;
        editor.ShowAllDossiers = true;
        Assert.All(editor.Dossiers, d => Assert.False(d.IsExpanded));
        editor.ShowAllDossiers = false;
        DossierStore.Remove(vm.Project!, new TermDossier { Expression = "zumo", SourceLanguage = "es-ES", TargetLanguage = "es-MX" });
        row = new DossierRow(DossierStore.Load(vm.Project!)[0], editor);

        // Ergebnis der Netzprüfung lesbar: Urteil, Zahlen je Variante, Empfehlung, Belege
        var report = EditorViewModel.FormatWebCheck(new Core.Agents.TranslationWebCheckResult("…", [
            new("uszczelkę pierścienia", "Gleitringdichtung", [
                new("uszczelkę pierścienia", 0, null, null, []),
                new("uszczelnienie mechaniczne", 4, 7, 10, [new("https://pl.wikipedia.org/wiki/Uszczelnienie_mechaniczne", "Uszczelnienie mechaniczne", "")])],
                Core.Agents.PhraseVerdict.Wrong, "uszczelnienie mechaniczne", "0 gegen 4 Artikel.")], "besser", "Ein Begriff ist wörtlich.", "Prüfmodell", "Wikipedia"), "pl-PL");
        Assert.Contains("✖ „uszczelkę pierścienia“ ← „Gleitringdichtung“ – falsch", report);
        Assert.Contains("„uszczelnienie mechaniczne“: Wikipedia: 4 Artikel · Web: 7 von 10 Treffern", report);
        Assert.Contains("→ Empfehlung: „uszczelnienie mechaniczne“", report);
        Assert.Contains("https://pl.wikipedia.org/wiki/Uszczelnienie_mechaniczne", report);
        Assert.Contains("Verbesserte Fassung unten", report);

        await Run(editor.DossierToTermbaseCommand, row);
        Assert.Contains(vm.Project!.ProjectTermbase.AllConcepts(), c => c.Terms.Any(t => t.Text == "Registro Público de la Propiedad"));

        // Eigene Begriffe: mit ; getrennt, angehakt oben in der Liste, mit Fundstellen; doppelte nur einmal
        editor.OwnResearchTerms = "zumo de naranja; escritura pública;zumo de naranja";
        await Run(editor.AddOwnResearchTermsCommand);
        Assert.Equal(["zumo de naranja", "escritura pública"], editor.ResearchItems.Select(r => r.Expression));
        Assert.All(editor.ResearchItems, r => Assert.True(r.IsOwn && r.IsChecked));
        Assert.Equal("Eigener Begriff – selbst hinzugefügt, 1× im Dokument", editor.ResearchItems[0].Info);
        Assert.Equal("Se sirve zumo de naranja.", editor.ResearchItems[0].Item.Context);
        Assert.Equal("Eigener Begriff – selbst hinzugefügt, kommt im Dokument nicht vor", editor.ResearchItems[1].Info);
        Assert.Equal("", editor.OwnResearchTerms);
        Assert.True(editor.HasResearchItems);
        editor.ResearchItems[1].IsChecked = false;
        editor.OwnResearchTerms = "Escritura pública";
        await Run(editor.AddOwnResearchTermsCommand);
        Assert.Equal(2, editor.ResearchItems.Count);
        Assert.True(editor.ResearchItems[1].IsChecked);                               // schon da: nur wieder angehakt
    }
}
