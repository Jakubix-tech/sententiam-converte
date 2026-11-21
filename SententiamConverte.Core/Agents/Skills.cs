using System.Text;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Agents;

/// <summary>
/// Eine Fähigkeit der Agenten: eine bewährte Vorgehensweise in Markdown (offenes agentskills.io-Format, SKILL.md mit
/// name/description im Kopf). Mitgelieferte Skills sind Startpunkte; gelernte legt der Agent nach erfolgreichen
/// Recherchen selbst an und verbessert sie (Prinzip wie beim Hermes Agent).
/// </summary>
/// <param name="Version">Fassung eines mitgelieferten Skills; neuere ersetzen ältere, solange man sie nicht selbst geändert hat.</param>
/// <param name="Edited">Selbst geändert – wird nie durch eine neuere mitgelieferte Fassung ersetzt.</param>
public sealed record Skill(string Name, string Description, string Instructions, bool Learned, int Uses, string? Path = null,
    int Version = 0, bool Edited = false)
{
    public string OriginText => Learned ? "gelernt" : "mitgeliefert";
}

/// <summary>Skills als Ordner mit SKILL.md-Dateien (je Skill ein Unterordner), lesbar und bearbeitbar.</summary>
public sealed partial class SkillLibrary
{
    public string Folder { get; }

    public SkillLibrary(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        Directory.CreateDirectory(Folder);
        foreach (var s in BuiltIn)
        {
            var existing = File.Exists(FileOf(s.Name)) ? Read(FileOf(s.Name)) : null;
            if (existing == null) Save(s);
            else if (!existing.Learned && !existing.Edited && existing.Version < s.Version) Save(s with { Uses = existing.Uses });
        }
    }

    public static string DefaultFolder => Path.Combine(AppPaths.DataRoot, "skills");

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NotKebab();

    /// <summary>Skill-Namen wie im Format vorgesehen: klein, mit Bindestrichen.</summary>
    public static string NormalizeName(string name)
    {
        var n = name.Trim().ToLowerInvariant().Replace('ä', 'a').Replace('ö', 'o').Replace('ü', 'u').Replace("ß", "ss").Replace(' ', '-');
        n = NotKebab().Replace(n, "-").Trim('-');
        return n.Length > 64 ? n[..64].Trim('-') : n;
    }

    private string FileOf(string name) => Path.Combine(Folder, NormalizeName(name), "SKILL.md");

    public List<Skill> All() =>
        Directory.EnumerateFiles(Folder, "SKILL.md", SearchOption.AllDirectories)
            .Select(Read).OfType<Skill>().OrderBy(s => s.Learned).ThenBy(s => s.Name).ToList();

    public Skill? Get(string name) => File.Exists(FileOf(name)) ? Read(FileOf(name)) : null;

    public void Save(Skill skill)
    {
        var path = FileOf(skill.Name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {NormalizeName(skill.Name)}");
        sb.AppendLine($"description: {skill.Description.Replace('\n', ' ').Trim()}");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  origin: {(skill.Learned ? "learned" : "built-in")}");
        sb.AppendLine($"  uses: {skill.Uses}");
        if (!skill.Learned && skill.Version > 0) sb.AppendLine($"  version: {skill.Version}");
        if (skill.Edited) sb.AppendLine("  edited: true");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(skill.Instructions.Trim());
        sb.AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public void Delete(string name)
    {
        var dir = System.IO.Path.GetDirectoryName(FileOf(name))!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public void RecordUse(string name)
    {
        if (Get(name) is { } s) Save(s with { Uses = s.Uses + 1 });
    }

    /// <summary>Für die KI: Name und Zweck aller Skills, eine Zeile je Skill.</summary>
    public string Catalog() => string.Join("\n", All().Select(s => $"- {s.Name}: {s.Description}"));

    private static Skill? Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            var m = Regex.Match(text, "^---\n(.*?)\n---\n?(.*)$", RegexOptions.Singleline);
            if (!m.Success) return null;
            string? Field(string key) => Regex.Match(m.Groups[1].Value, $"^\\s*{key}:\\s*(.*)$", RegexOptions.Multiline) is { Success: true } f ? f.Groups[1].Value.Trim() : null;
            var name = Field("name");
            if (string.IsNullOrEmpty(name)) return null;
            return new Skill(name, Field("description") ?? "", m.Groups[2].Value.Trim(), Field("origin") == "learned",
                int.TryParse(Field("uses"), out var u) ? u : 0, path,
                int.TryParse(Field("version"), out var v) ? v : 0, Field("edited") == "true");
        }
        catch (IOException) { return null; }
    }

    /// <summary>Mitgelieferte Startpunkte; werden beim ersten Start in den Skill-Ordner gelegt.</summary>
    public static readonly IReadOnlyList<Skill> BuiltIn =
    [
        new("fachbegriff-recherchieren", "Den richtigen Fachbegriff in der Zielsprache finden und belegen.", """
            1. Zuerst `database` mit dem Begriff: steht er schon in Terminologie, TM oder Wissen des Projekts?
            2. `search` mit source "wikipedia" in der Ausgangssprache; den passendsten Artikel mit `article` öffnen.
            3. In der Artikelantwort stehen die Sprachversionen: den Titel in der Zielsprache mit `article` (Zielsprache) öffnen
               und prüfen, ob Definition und Fachgebiet wirklich übereinstimmen.
            4. Gibt es keinen Artikel: `sources` mit dem Fachgebiet und der Zielsprache nennt die maßgeblichen Fachseiten; dort
               mit `search` (source "web", site = Domain) suchen und eine Seite mit `open` lesen – `find` liefert gleich die
               Stellen mit dem Begriff.
            5. Gesetzlich geregelte Begriffe (Arbeitsschutz, Produktsicherheit, Verwaltung …): `law` in der Zielrechtsordnung;
               bei EU-Bezug dieselbe EU-Norm in beiden Sprachen (`law` mit jurisdiction "eu") – das ist die amtliche Entsprechung.
            6. Den Begriff erst nennen, wenn ein Beleg aus einem Text in der Zielsprache vorliegt; Belege als Quellen angeben.
            7. Das Ergebnis mit `remember` speichern (Titel: Begriff in beiden Sprachen).
            """, false, 0, Version: 2),
        new("usus-pruefen", "Prüfen, ob eine Formulierung in der Zielsprache wirklich so verwendet wird.", """
            1. `database`: gibt es schon eine Regel oder einen Beleg im Projekt?
            2. `dictionary` mit dem Wort in der Zielsprache: Eintrag, gültige Schreibung, für Deutsch auch die Häufigkeitsklasse
               im DWDS (0 = selten oder nicht belegt … 6 = sehr häufig).
            3. `search` mit source "wiktionary" in der Zielsprache nach dem Hauptwort; Eintrag mit `article` lesen
               (Bedeutungen, Wendungen, Beispiele).
            4. Die Sprachseiten der Zielsprache gezielt durchsuchen (`sources` mit field "Sprache"): `search` mit source "web",
               site und der genauen Wortfolge in Anführungszeichen; zwei bis drei Treffer mit `open` (find = Wortfolge) lesen.
            5. Zum Vergleich eine gleichbedeutende Alternative genauso prüfen.
            6. Urteil: üblich, gelegentlich, selten oder nicht belegt – mit Belegen und Empfehlung.
            """, false, 0, Version: 2),
        new("rechtsbegriff-vergleichen", "Rechts- und Verwaltungsbegriffe zwischen zwei Rechtsordnungen vergleichen und mit Anmerkung übersetzen.", """
            1. `database`: steht der Begriff schon in Terminologie (z. B. IATE), TM oder Wissen des Projekts?
            2. Rechtsordnungen klären: `sources` mit field "Recht" für Ausgangs- und Zielsprache – welches System, welche
               amtlichen Seiten, welche Datenbank (`law`).
            3. Den Begriff im Ausgangsrecht verankern: `law` in der Ausgangsrechtsordnung (Gesetzesname oder Fundstelle,
               z. B. "§ 35 VwVfG"), die Norm mit `open` und find = Begriff lesen. Festhalten: Definition, Voraussetzungen,
               Rechtsfolgen, Fundstelle.
            4. Kandidaten in der Zielsprache sammeln: Sprachversion des Wikipedia-Artikels (`search`, `article`), IATE
               (`database`); bei EU-Bezug dieselbe EU-Norm in beiden Sprachen (`law` mit jurisdiction "eu") – amtliche Entsprechung.
            5. Das Gegenstück im Zielrecht prüfen: `law` in der Zielrechtsordnung (z. B. "Kodeks postępowania
               administracyjnego") und die Norm mit `open` (find) lesen.
            6. Vergleichen: Begriffsumfang, Voraussetzungen, Rechtsfolgen, zuständige Stellen, Verfahren. Ergebnis: volle,
               teilweise oder keine Entsprechung.
            7. Strategie wählen: funktionale Entsprechung (bei voller oder weitgehender Entsprechung); wörtliche bzw.
               beschreibende Übersetzung (bei teilweiser); Ausgangsbegriff beibehalten und erklären (wenn das Zielrecht
               nichts Vergleichbares kennt).
            8. `finish`: answer = Vergleich mit Begründung und Normen; translation = Übersetzungsvorschlag; note = Anmerkung
               des Übersetzers in der Zielsprache (kurz: was der Begriff im Ausgangsrecht bedeutet, worin er vom Zielbegriff
               abweicht, Fundstelle); sources = Normen und Belege.
            9. Mit `remember` als Wissen speichern.
            """, false, 0, Version: 2),
        new("eigennamen-pruefen", "Firmen-, Produkt- und Behördennamen richtig schreiben oder übersetzen.", """
            1. Eigennamen von Firmen und Produkten werden nicht übersetzt; die offizielle Schreibweise mit `search`
               (source "web" oder "wikipedia") belegen.
            2. Behörden und Institutionen haben oft eine offizielle Bezeichnung in der Zielsprache: die Sprachversion des
               Wikipedia-Artikels oder die eigene Website der Einrichtung prüfen.
            3. Ergebnis: Schreibweise in der Zielsprache, ob übersetzt oder nicht, mit Beleg.
            """, false, 0, Version: 1),
        new("amtliche-quellen-finden", "Die maßgeblichen amtlichen oder fachlichen Websites für ein Thema und Land finden und merken.", """
            1. `sources` mit Thema und Sprache: bekannte amtliche Seiten und die Werkzeuge, die sie direkt abfragen.
            2. Fehlt etwas: `search` mit source "wikipedia" in der Landessprache nach der zuständigen Behörde, dem Gericht,
               dem Normungs- oder Fachverband; der Artikel nennt meist die offizielle Website.
            3. Die Website mit `open` prüfen: ist sie wirklich amtlich bzw. fachlich maßgeblich (Behörde, Gericht, Akademie,
               Normungsstelle)? Keine Foren, keine Werbeseiten.
            4. Mit `add_source` dem Thema hinzufügen; danach mit `search` (source "web", site) gezielt dort suchen.
            """, false, 0, Version: 1),
    ];
}
