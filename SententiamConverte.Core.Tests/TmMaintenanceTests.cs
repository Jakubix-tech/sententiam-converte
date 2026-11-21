using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Tests;

/// <summary>TM-Pflege: Suchen und Ersetzen, Bereinigen, uneinheitliche Übersetzungen.</summary>
public sealed class TmMaintenanceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TermsAreReplacedEverywhereButNeverInsideTags()
    {
        using var tm = new TranslationMemory(_dir.File("tm.sctm"));
        tm.Add("de-DE", "en-GB", "Den Netzstecker ziehen.", "Pull the mains plug.");
        tm.Add("de-DE", "en-GB", "Der ⟨1⟩Netzstecker⟨/1⟩ ist defekt.", "The ⟨1⟩mains plug⟨/1⟩ is faulty.");
        tm.Add("de-DE", "en-GB", "Stecker prüfen.", "Check the plug.");

        var preview = TmMaintenance.Preview(tm, "mains plug", "power plug", inSource: false, inTarget: true);
        Assert.Equal(2, preview.Count);
        Assert.Equal(3, tm.All().Count(u => u.Target.Contains("plug")));          // Vorschau ändert nichts

        Assert.Equal(2, TmMaintenance.ReplaceAll(tm, "mains plug", "power plug", false, true));
        Assert.Contains(tm.All(), u => u.Target == "The ⟨1⟩power plug⟨/1⟩ is faulty.");
        Assert.Equal(0, TmMaintenance.ReplaceAll(tm, "1", "9", false, true));     // Tag-Nummern bleiben
        Assert.Equal(1, TmMaintenance.ReplaceAll(tm, "plug", "connector", false, true, wholeWords: true, matchCase: true) - 2);
        Assert.Equal(1, TmMaintenance.ReplaceAll(tm, @"Check the (\w+)", "Inspect the $1", false, true, regex: true));
        Assert.Contains(tm.All(), u => u.Target == "Inspect the connector.");
    }

    [Fact]
    public void DuplicatesAndUnusableEntriesAreCleanedAndInconsistenciesFound()
    {
        using var tm = new TranslationMemory(_dir.File("tm2.sctm"));
        // derselbe Satz in drei Zusammenhängen (wie beim Übersetzen gespeichert)
        tm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Switch off the device.", previous: "Wartung beginnen.");
        tm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Switch off the unit.", previous: "Reinigung beginnen.");
        tm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Turn the device off.", previous: "Arbeit beenden.");   // andere Übersetzung
        tm.Add("de-DE", "en-GB", "Sicherheitshinweise beachten.", "Sicherheitshinweise beachten.");
        tm.Add("de-DE", "en-GB", "ABC-123", "ABC-123");                                 // bleibt: Kennung

        TmMaintenance.ReplaceAll(tm, "unit", "device", false, true);                   // wird mit dem gleichen Eintrag zusammengeführt
        Assert.Equal(4, tm.Count());
        tm.Add("de-DE", "en-GB", "Gerät ausschalten.", "Switch off  the device.", previous: "Prüfung beginnen.");   // Dublette, nur Leerraum anders
        var inconsistent = TmMaintenance.Inconsistent(tm);
        Assert.Equal("Gerät ausschalten.", Assert.Single(inconsistent).Source.Trim());

        var r = TmMaintenance.Clean(tm, duplicates: true, emptyTargets: true, identical: true);
        Assert.Equal((1, 0, 1), (r.Duplicates, r.EmptyTargets, r.Identical));
        Assert.Equal(3, tm.Count());
        Assert.Contains(tm.All(), u => u.Source == "ABC-123");
    }
}
