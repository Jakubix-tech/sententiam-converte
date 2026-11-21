using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Resources;

/// <summary>Ein frei herunterladbarer Sprachdatensatz (Translation Memory oder Terminologie).</summary>
public sealed record LanguageDataSet(string Id, string Name, string Description, string License, string InfoUrl, string Url,
    long Size, long Units, ResourceKind Kind, bool Recommended, string Category = "Sonstiges")
{
    /// <summary>Die Lizenz erlaubt auch berufliche Nutzung (keine Beschränkung auf Forschung oder nicht kommerziell).</summary>
    public bool CommercialUse =>
        !License.Contains("Forschung", StringComparison.OrdinalIgnoreCase) && !License.Contains("nicht kommerziell", StringComparison.OrdinalIgnoreCase)
        && !License.Contains("urheberrechtlich", StringComparison.OrdinalIgnoreCase) && !License.Contains("siehe Korpusseite", StringComparison.OrdinalIgnoreCase);

    public string SizeText => BuiltInAi.FormatSize(Size);
    public string UnitsText => Units > 0 ? Units.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "?";
    public string KindText => Kind == ResourceKind.Tm ? "Translation Memory" : "Terminologie";

    /// <summary>Geschätzter Platz für Download und fertige Datenbank (ein TM braucht mit Suchindex etwa 2,5 KB je Satzpaar).</summary>
    public long DiskNeed => Size + (Kind == ResourceKind.Tm ? Math.Max(Size * 6, Units * 2500) : Size * 2) + 200_000_000;
    public string DiskNeedText => BuiltInAi.FormatSize(DiskNeed);
}

/// <summary>Ergebnis eines Downloads: Datei der neuen Datenbank und Zahl der übernommenen Einträge.</summary>
public sealed record LanguageDataResult(string Path, int Count);

/// <summary>
/// Freie Sprachdaten zum Herunterladen und Anbinden: Translation Memories aus dem OPUS-Archiv (offizielle Schnittstelle,
/// u. a. EU-Übersetzungsspeicher DGT, Europarl, EMEA) und die frei verfügbare IATE-Gesamtausgabe der EU-Terminologie.
/// Geladen wird nur, was der Benutzer auswählt; übernommen werden nur die Sprachen des Projekts.
/// </summary>
public static partial class LanguageData
{
    /// <summary>Für Tests: andere Adressen.</summary>
    internal static string OpusApi { get; set; } = "https://opus.nlpl.eu/opusapi/";
    internal static string IateUrl { get; set; } = "https://iate.europa.eu/em-api/artifacts/full-tbx";
    internal static string TermiumBase { get; set; } = "https://donnees-data.tpsgc-pwgsc.gc.ca/bt1/tp-tp/";

    /// <summary>TERMIUM Plus je Fachgebiet: Datei, Name (englisch), Kategorie, empfohlen, Größe in Bytes.</summary>
    private static readonly (string File, string Subject, string Category, bool Recommended, long Size)[] Termium =
    [
        ("domaine-subject-administration.zip", "Administration", "Recht und Verwaltung", true, 14_775_335),
        ("domaine-droitJustice-Subject-LawJustice.zip", "Law and Justice", "Recht und Verwaltung", true, 12_422_306),
        ("domaine-Appellations-subject-NamesAndTitles.zip", "Names and Titles", "Recht und Verwaltung", false, 30_851_380),
        ("domaine-MilitaireEtSecurite-Subject-MilitaryAndSecurity.zip", "Military and Security", "Recht und Verwaltung", false, 12_843_736),
        ("domaine-MedecineEtSante-subject-MedicineAndHealth.zip", "Medicine and Health", "Medizin und Gesundheit", true, 13_343_814),
        ("domaine-economie-subject-economy.zip", "Economy", "Wirtschaft und Finanzen", true, 20_817_294),
        ("domaine-ElectroniqueInformatique-subject-ElectronicsInformatics.zip", "Electronics and Informatics", "Technik und Software", true, 14_358_463),
        ("domaine-electricite-subject_electricity.zip", "Electricity", "Technik und Software", false, 4_649_668),
        ("domaine-MecaniqueEtChaleur-subject-MechanicsAndHeat.zip", "Mechanics and Heat", "Technik und Software", false, 4_057_107),
        ("domaine-subject-construction.zip", "Construction", "Technik und Software", false, 6_161_928),
        ("domaine-subject-industries.zip", "Industries", "Technik und Software", false, 20_754_511),
        ("domaine-metallurgie-sbject-metallurgy.zip", "Metallurgy", "Technik und Software", false, 2_019_801),
        ("domaine-petrole-subject-petroleum.zip", "Petroleum", "Technik und Software", false, 1_475_547),
        ("domaine-MinesEtCarrieres-subject-MinesAndQuarries.zip", "Mines and Quarries", "Technik und Software", false, 2_589_536),
        ("domaine-RobotiqueSpatiale-subject-SpaceRobotics.zip", "Space Robotics", "Technik und Software", false, 67_838),
        ("domaine-TelecommunicationsPostes-subject-TelecommunicationsPostalService.zip", "Telecommunications and Postal Service", "Technik und Software", false, 4_430_853),
        ("domaine-TransportManutention-subject-TransportationMaterialsHandling.zip", "Transportation and Materials Handling", "Technik und Software", false, 11_642_949),
        ("domaine-Math_Physique_ScNaturelles-Subject-Math_Physics_NaturalSc.zip", "Mathematics, Physics and Natural Sciences", "Technik und Software", false, 28_983_936),
        ("domaine-SciencesMilieu-suject-EnvironmentalSciences.zip", "Environmental Sciences", "Technik und Software", false, 13_753_900),
        ("domaine-subject-agriculture.zip", "Agriculture", "Allgemein und Alltag", false, 7_320_258),
        ("domaine-ScienceHumaines-subject-HumanitiesSocialSciences.zip", "Humanities and Social Sciences", "Allgemein und Alltag", false, 19_841_186),
        ("domaine-EconomieDomHotellerie-subject-HomeEconomicsAccommodationSc.zip", "Home Economics and Accommodation Science", "Allgemein und Alltag", false, 2_072_406),
        ("domaine-ArtsLoisirsSports-subject-ArtsRecreationSports.zip", "Arts, Recreation and Sports", "Untertitel und Literatur", false, 9_071_510),
    ];

    /// <summary>TERMIUM Plus gibt es für Englisch und Französisch (Spanisch und Portugiesisch nur vereinzelt).</summary>
    private static bool TermiumPair(string a, string b) =>
        new[] { Languages.Primary(a), Languages.Primary(b) }.Order().SequenceEqual(["en", "fr"]);

    /// <summary>
    /// Bekannte Korpora mit Beschreibung und Lizenz; „empfohlen“ sind gepflegte, frei nutzbare Texte aus Verwaltung, Recht,
    /// Technik und Software. Aus dem Netz gesammelte Riesenkorpora und solche mit engen Lizenzen stehen unter „Alle anzeigen“.
    /// </summary>
    private const string Law = "Recht und Verwaltung", Medicine = "Medizin und Gesundheit", Tech = "Technik und Software",
        Economy = "Wirtschaft und Finanzen", General = "Allgemein und Alltag", Media = "Untertitel und Literatur", Web = "Aus dem Netz gesammelt";

    /// <summary>Die Fachgebiete der Datensätze (zum Filtern).</summary>
    public static readonly IReadOnlyList<string> Categories = [Law, Medicine, Tech, Economy, General, Media, Web, "Sonstiges"];

    private static readonly Dictionary<string, (string Description, string License, bool Recommended, string Category)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DGT"] = ("EU-Übersetzungsspeicher der Generaldirektion Übersetzung: Rechtsakte der EU", "EU-Wiederverwendung, Quelle angeben", true, Law),
        ["Europarl"] = ("Reden und Protokolle des Europäischen Parlaments", "frei, Quelle angeben", true, Law),
        ["JRC-Acquis"] = ("EU-Recht (Acquis communautaire)", "EU-Wiederverwendung, Quelle angeben", true, Law),
        ["EMEA"] = ("Arzneimittelinformationen der Europäischen Arzneimittel-Agentur", "EMA, Wiederverwendung erlaubt, Quelle angeben", true, Medicine),
        ["ECB"] = ("Veröffentlichungen der Europäischen Zentralbank", "EZB, Wiederverwendung erlaubt, Quelle angeben", true, Economy),
        ["ECDC"] = ("Gesundheitsinformationen des ECDC", "EU-Wiederverwendung, Quelle angeben", true, Medicine),
        ["EUconst"] = ("Entwurf der EU-Verfassung", "frei", true, Law),
        ["EUbookshop"] = ("Veröffentlichungen der EU-Institutionen", "EU-Wiederverwendung, Quelle angeben", true, Law),
        ["TildeMODEL"] = ("Mehrsprachige Texte von EU-Websites und -Diensten", "CC BY 4.0", true, Law),
        ["ELITR-ECA"] = ("Berichte des Europäischen Rechnungshofs", "EU-Wiederverwendung, Quelle angeben", true, Law),
        ["KDE4"] = ("Oberflächentexte der KDE-Software", "freie Softwarelizenzen (GPL u. a.)", true, Tech),
        ["GNOME"] = ("Oberflächentexte der GNOME-Software", "freie Softwarelizenzen (GPL u. a.)", true, Tech),
        ["Ubuntu"] = ("Oberflächentexte der Ubuntu-Software", "freie Softwarelizenzen (GPL u. a.)", true, Tech),
        ["PHP"] = ("PHP-Handbuch", "CC BY 3.0", false, Tech),
        ["Tatoeba"] = ("Alltagssätze aus dem Tatoeba-Projekt", "CC BY 2.0 FR", true, General),
        ["MultiUN"] = ("Dokumente der Vereinten Nationen", "frei, Quelle angeben", true, Law),
        ["News-Commentary"] = ("Wirtschafts- und Politikkommentare", "nur für Forschung", false, Economy),
        ["TED2020"] = ("Untertitel von TED-Vorträgen", "CC BY-NC-ND 4.0 (nicht kommerziell)", false, Media),
        ["EuroPat"] = ("Patentschriften (sehr groß)", "siehe Korpusseite", false, Tech),
        ["OpenSubtitles"] = ("Film-Untertitel (sehr groß)", "nur für Forschung", false, Media),
        ["Books"] = ("Literarische Werke", "urheberrechtlich geschützt, nur für Forschung", false, Media),
        ["QED"] = ("Untertitel von Lehrvideos", "siehe Korpusseite", false, Media),
        ["GlobalVoices"] = ("Bürgerjournalismus (Global Voices)", "CC BY 3.0", false, General),
        ["WikiMatrix"] = ("Aus Wikipedia automatisch gepaarte Sätze", "CC BY-SA 3.0", false, Web),
        ["wikimedia"] = ("Übersetzte Wikipedia-Inhalte", "CC BY-SA 3.0", false, Web),
        ["CCMatrix"] = ("Automatisch gepaarte Sätze aus Webseiten (sehr groß, ungeprüft)", "siehe Korpusseite", false, Web),
        ["CCAligned"] = ("Automatisch gepaarte Webseiten (sehr groß, ungeprüft)", "siehe Korpusseite", false, Web),
        ["ParaCrawl"] = ("Automatisch gesammelte Webseiten (sehr groß, ungeprüft)", "siehe Korpusseite (Rechte an den Inhalten bleiben bei den Websites)", false, Web),
        ["XLEnt"] = ("Automatisch gepaarte Namen und Bezeichnungen", "CC BY-NC 4.0 (nicht kommerziell)", false, Web),
        ["NLLB"] = ("Automatisch gesammelte Sätze (sehr groß, ungeprüft)", "siehe Korpusseite", false, Web),
        ["HPLT"] = ("Automatisch gesammelte Webseiten (sehr groß, ungeprüft)", "siehe Korpusseite", false, Web),
        ["LinguaTools-WikiTitles"] = ("Gepaarte Wikipedia-Titel", "CC BY-SA 3.0", false, Web),
        ["bible-uedin"] = ("Bibelübersetzungen", "siehe Korpusseite", false, Media),
    };

    /// <summary>Fachgebiet eines ELRC-Datensatzes aus seinem Namen.</summary>
    private static string ElrcCategory(string topic)
    {
        var t = topic.ToLowerInvariant();
        if (Regex.IsMatch(t, "health|medic|covid|corona|pharma|hospital|vaccin")) return Medicine;
        if (Regex.IsMatch(t, "bank|financ|tax|econom|budget|trade|business")) return Economy;
        if (Regex.IsMatch(t, "it |software|techn|digital|cyber|energy|transport|patent")) return Tech;
        return Law;
    }

    /// <summary>Sprachcode für OPUS (de-DE → de, pt-BR → pt_BR, zh-CN → zh_CN, sonst Hauptsprache).</summary>
    public static string OpusCode(string language)
    {
        var parts = language.Replace('_', '-').Split('-');
        var primary = parts[0].ToLowerInvariant();
        return parts.Length > 1 && primary is "pt" or "zh" && parts[1].ToUpperInvariant() is "BR" or "CN" or "TW"
            ? $"{primary}_{parts[1].ToUpperInvariant()}"
            : primary;
    }

    [GeneratedRegex("^ELRC", RegexOptions.IgnoreCase)]
    private static partial Regex Elrc();

    /// <summary>„ELRC-1077-EUIPO_law“ → „EUIPO law“.</summary>
    private static string ElrcTopic(string name) =>
        Regex.Replace(Regex.Replace(name, @"^ELRC[-_]?(\d+[-_]?)?", "", RegexOptions.IgnoreCase), "[_-]+", " ").Trim() is { Length: > 0 } t ? t : name;

    /// <summary>Verfügbare Datensätze für ein Sprachpaar, empfohlene zuerst.</summary>
    public static async Task<List<LanguageDataSet>> SearchAsync(string sourceLanguage, string targetLanguage, CancellationToken ct = default)
    {
        using var http = BuiltInAi.Http();
        http.Timeout = TimeSpan.FromSeconds(60);
        var s = OpusCode(sourceLanguage);
        var t = OpusCode(targetLanguage);
        var corpora = await QueryAsync(http, s, t, ct);
        if (corpora.Count == 0) corpora = await QueryAsync(http, t, s, ct);   // OPUS führt jedes Paar nur in einer Richtung

        var list = new List<LanguageDataSet>
        {
            new("iate", "IATE", "Terminologiedatenbank der EU (Gesamtausgabe, Stand 2019)", "EU-Wiederverwendung, Quelle angeben",
                "https://iate.europa.eu", IateUrl, 112_828_998, 0, ResourceKind.Termbase, true, Law),
        };
        if (TermiumPair(sourceLanguage, targetLanguage))
            list.AddRange(Termium.Select(t => new LanguageDataSet("termium-" + Path.GetFileNameWithoutExtension(t.File).ToLowerInvariant(),
                "TERMIUM Plus – " + t.Subject, "Termbank der kanadischen Regierung, Fachgebiet " + t.Subject + " (Englisch–Französisch)",
                "Open Government Licence – Canada", "https://open.canada.ca/data/en/dataset/94fc74d6-9b9a-4c2e-9c6c-45a5092453aa",
                TermiumBase + t.File, t.Size, 0, ResourceKind.Termbase, t.Recommended, t.Category)));
        foreach (var c in corpora)
        {
            var name = (string?)c["corpus"] ?? "";
            var url = (string?)c["url"] ?? "";
            var pairs = Number(c["alignment_pairs"]);
            if (name.Length == 0 || url.Length == 0 || pairs == 0) continue;
            var size = Number(c["size"]) * 1024;
            (string Description, string License, bool Recommended, string Category) known = Known.TryGetValue(name, out var k) ? k
                : Elrc().IsMatch(name) ? ("Öffentliche Verwaltung (Sprachressourcen-Initiative der EU, ELRC): " + ElrcTopic(name),
                    "offene Lizenz (meist CC BY 4.0), siehe Korpusseite", pairs >= 20_000, ElrcCategory(ElrcTopic(name)))
                : ("Weitere Sammlung aus dem OPUS-Archiv", "siehe Korpusseite", false, "Sonstiges");
            // Riesenkorpora und unplausible Angaben (viele Paare in wenig Daten) nicht empfehlen
            var plausible = pairs <= 5_000_000 && size >= pairs * 20;
            list.Add(new LanguageDataSet("opus-" + name.ToLowerInvariant(), name, known.Description, known.License,
                $"https://opus.nlpl.eu/{name}/corpus/version/{name}", url, size, pairs, ResourceKind.Tm, known.Recommended && plausible, known.Category));
        }
        // OPUS führt manche Datensätze doppelt (z. B. „ELRC-1077-EUIPO_law“ und „ELRC-EUIPO_law“): den kürzeren Namen behalten
        return list.DistinctBy(x => x.Url)
            .GroupBy(x => (x.Units, x.Size, x.Kind)).Select(g => g.OrderBy(x => x.Name.Length).First())
            .OrderByDescending(x => x.Recommended).ThenBy(x => x.Kind == ResourceKind.Termbase ? 0 : 1).ThenByDescending(x => x.Units)
            .ToList();
    }

    /// <summary>OPUS liefert Zahlen mal als Zahl, mal als Text (auch leer).</summary>
    private static long Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var n) ? n
        : long.TryParse((string?)(node as JsonValue)?.ToString(), out var parsed) ? parsed : 0;

    private static async Task<List<JsonNode>> QueryAsync(HttpClient http, string source, string target, CancellationToken ct)
    {
        var json = await http.GetStringAsync($"{OpusApi}?source={Uri.EscapeDataString(source)}&target={Uri.EscapeDataString(target)}&preprocessing=tmx&version=latest", ct);
        return (JsonNode.Parse(json)?["corpora"]?.AsArray() ?? []).OfType<JsonNode>().ToList();
    }

    /// <summary>
    /// Datensatz laden und als neue Datenbank im Ordner anlegen (nur das Sprachpaar des Projekts, in Projektrichtung).
    /// Der Download wird danach gelöscht; abgebrochene Downloads werden beim nächsten Mal fortgesetzt.
    /// </summary>
    public static async Task<LanguageDataResult> InstallAsync(LanguageDataSet set, string sourceLanguage, string targetLanguage, string folder,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        if (BuiltInAi.FreeSpace(folder) is { } free && free < set.DiskNeed)
            throw new AiException($"Zu wenig Speicherplatz: gebraucht etwa {set.DiskNeedText}, frei {BuiltInAi.FormatSize(free)} auf {Path.GetPathRoot(Path.GetFullPath(folder))}. " +
                                  "Einen anderen Ordner wählen.");
        var pair = $"{OpusCode(sourceLanguage)}-{OpusCode(targetLanguage)}";
        var archive = Path.Combine(folder, $"{set.Id}-{pair}" + (set.Kind == ResourceKind.Termbase ? ".zip" : ".tmx.gz"));
        if (!File.Exists(archive))          // vollständig geladen, aber Import abgebrochen: nicht noch einmal laden
            using (var http = BuiltInAi.Http())
                await BuiltInAi.DownloadAsync(http, set.Url, archive, 0, null, set.Name, progress, ct);

        var target = Path.Combine(folder, $"{SafeName(set.Name)} {pair}{ResourceFactory.Extension(set.Kind)}");
        if (File.Exists(target)) File.Delete(target);
        var what = $"Import {set.Name}";
        var importProgress = progress == null ? null : new Progress<long>(n => progress.Report(new DownloadProgress(what, n, Math.Max(n, set.Units))));
        int count;
        try
        {
            if (set.Kind == ResourceKind.Tm)
            {
                using var tm = new TranslationMemory(target, $"{set.Name} {pair}");
                await using var file = File.OpenRead(archive);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await Task.Run(() => TmxIo.Import(gzip, tm, sourceLanguage, targetLanguage, anyDirection: true, importProgress, ct), ct);
                count = (int)tm.Count();                     // doppelte Paare im Datensatz werden zusammengeführt
            }
            else if (set.Id.StartsWith("termium-"))
            {
                using var termbase = new Termbase(target, $"{set.Name} {pair}");
                await using var file = File.OpenRead(archive);
                count = await Task.Run(() => Exchange.TermiumIo.Import(file, termbase, sourceLanguage, targetLanguage, importProgress, ct), ct);
            }
            else
            {
                using var termbase = new Termbase(target, $"{set.Name} {pair}");
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".tbx", StringComparison.OrdinalIgnoreCase))
                            ?? throw new AiException($"{set.Name}: Das Archiv enthält keine TBX-Datei.");
                await using var tbx = entry.Open();
                count = await Task.Run(() => TbxIo.Import(tbx, termbase, [sourceLanguage, targetLanguage], importProgress, ct), ct);
            }
            if (count == 0)
                throw new AiException($"{set.Name}: Für {sourceLanguage} → {targetLanguage} enthält der Datensatz keine Einträge.");
        }
        catch (Exception ex)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { target, target + "-wal", target + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            if (ex is not OperationCanceledException && File.Exists(archive)) File.Delete(archive);   // kaputt oder unbrauchbar
            throw;
        }
        File.Delete(archive);
        return new LanguageDataResult(target, count);
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Standardordner für heruntergeladene Sprachdaten.</summary>
    public static string DefaultFolder => Path.Combine(AppPaths.DataRoot, "ressourcen", "sprachdaten");
}
