# Erweiterungen entwickeln

Eine Erweiterung fügt Sententiam converte Dateiformate, QA-Prüfungen und Befehle hinzu. Sie ist eine
.NET-Bibliothek, die im Reiter **Erweiterungen** installiert wird. Eine vollständige Vorlage liegt im Quellcode
unter `Erweiterungen/Beispiel.Erweiterung`.

## Aufbau

Eine Erweiterung besteht aus

- einer **.NET-10-Bibliothek**, kompiliert gegen `SententiamConverte.Core.dll`,
- einer **öffentlichen Klasse mit `IExtension`**,
- einer **`extension.json`** mit den Angaben zur Erweiterung.

Beides zusammen als ZIP mit der Endung **`.scext`** verpackt ergibt das Installationspaket. Eine einzelne DLL
lässt sich ebenfalls installieren; dann werden die Angaben aus dem Dateinamen gebildet.

```json
{
  "id": "firma.qa-pruefungen",
  "name": "QA-Prüfungen der Firma",
  "version": "1.2.0",
  "author": "Firma GmbH",
  "description": "Prüft Hausregeln für Maßeinheiten und Produktnamen.",
  "assembly": "Firma.QaPruefungen.dll"
}
```

Die `id` darf nur Buchstaben, Ziffern, Punkt, Unterstrich und Bindestrich enthalten; sie ist auch der Ordnername.

## Projektdatei

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Das SDK: Schnittstellen und Datentypen. Das Programm bringt die Bibliothek selbst mit – nicht mitkopieren -->
    <PackageReference Include="SententiamConverte.Core" Version="1.0.0" ExcludeAssets="runtime;native" />
  </ItemGroup>
</Project>
```

Das NuGet-Paket `SententiamConverte.Core` entsteht mit `dotnet run --project build/Paketbau -- sdk` im Ordner
`artifacts/<Version>/`. Diesen Ordner als Paketquelle eintragen (`nuget.config`):

```xml
<configuration>
  <packageSources>
    <add key="sententiam" value="pfad/zu/artifacts/1.0.0" />
  </packageSources>
</configuration>
```

Ohne Paket geht es auch mit einem direkten Verweis auf `SententiamConverte.Core.dll` aus dem Programmordner
(`<Reference>` mit `<Private>false</Private>`); im Quellcode-Baum genügt eine `ProjectReference` mit
`Private="false"`. Die Vorlage enthält außerdem ein MSBuild-Ziel, das nach dem Bauen automatisch das
`.scext`-Paket erzeugt.

Eigene Abhängigkeiten (NuGet-Pakete) gehören mit ins Paket. Bibliotheken, die das Programm selbst schon
mitbringt (z. B. `SententiamConverte.Core`, `Microsoft.Data.Sqlite`), werden immer aus dem Programm geladen,
damit die Typen zusammenpassen.

## Einstiegspunkt

```csharp
using SententiamConverte.Core.Extensions;

public sealed class MeineErweiterung : IExtension
{
    public string Id => "firma.qa-pruefungen";
    public string Name => "QA-Prüfungen der Firma";
    public string Version => "1.2.0";
    public string? Author => "Firma GmbH";
    public string? Description => "Prüft Hausregeln für Maßeinheiten und Produktnamen.";

    public void Register(IExtensionRegistry registry)
    {
        registry.AddQaCheck(new MasseinheitenPruefung());
        registry.AddCommand(new ProduktnamenBefehl());
    }
}
```

`Register` wird einmal beim Laden aufgerufen – beim Programmstart oder sobald die Erweiterung eingeschaltet wird.

## Dateiformat

`IFileFilter` liest eine Datei in ein zweisprachiges Dokument und schreibt die Übersetzung zurück:

```csharp
public sealed class MeinFormat : IFileFilter
{
    public string Id => "firma.meinformat";
    public string Name => "Mein Format";
    public IReadOnlyList<string> Extensions { get; } = [".mf"];

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        // je übersetzbarem Textstück einen Absatz anlegen; Locator merkt sich die Stelle in der Datei
        var p = new Paragraph { Locator = "0" };
        segmenter.Segment(p, "Text aus der Datei");
        doc.Paragraphs.Add(p);
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        // Original lesen, je Absatz p.JoinTarget() an der Stelle p.Locator einsetzen, Ergebnis schreiben
    }
}
```

Formatierungen werden im Text als Tags dargestellt: `⟨1⟩fett⟨/1⟩` (Paar) und `⟨2/⟩` (Platzhalter). Was die Tags
bedeuten, speichert der Filter selbst (z. B. in `Paragraph.TagData`). Hilfen: `TagText.StripTags`,
`TagText.Tags`, `Segmenter.HasTranslatableText`. Ein Filter mit derselben Endung wie ein eingebauter hat Vorrang.

## QA-Prüfung

```csharp
public sealed class MasseinheitenPruefung : IQaCheck
{
    public string Name => "Maßeinheiten";

    public IEnumerable<QaIssue> Check(Segment segment, BilingualDocument document)
    {
        if (segment.Target.Contains("Kilo ") )
            yield return new QaIssue(segment.Id, QaSeverity.Warning, Name, "„kg“ statt „Kilo“ schreiben.");
    }
}
```

Die Prüfung läuft bei jeder Qualitätsprüfung für jedes nicht gesperrte Segment. Löst sie eine Ausnahme aus,
erscheint diese als Hinweis – die übrige Prüfung läuft weiter.

## Befehle

```csharp
public sealed class ProduktnamenBefehl : IExtensionCommand
{
    public string Id => "firma.produktnamen";
    public string Name => "Produktnamen vereinheitlichen";
    public string? Description => "Ersetzt alte Produktnamen im Zieltext. Argument: alt=neu";
    public CommandScope Scope => CommandScope.Document;

    public Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var doc = context.Document ?? throw new InvalidOperationException("Bitte ein Dokument öffnen.");
        var n = 0;
        foreach (var (alt, neu) in context.Arguments)
            foreach (var s in doc.Segments.Where(s => s.Target.Contains(alt)))
            {
                s.Target = s.Target.Replace(alt, neu);
                n++;
            }
        return Task.FromResult(new ExtensionResult($"{n} Ersetzungen.", DocumentChanged: n > 0));
    }
}
```

`Scope` sagt, worauf der Befehl wirkt: `Segment`, `Document`, `Project` oder `Global`. Das Programm prüft
vor dem Ausführen, ob das Nötige offen bzw. gewählt ist.

`ExtensionContext` enthält:

| Eigenschaft | Inhalt |
|---|---|
| `Project` | das geöffnete Projekt (TMs, Terminologien, Wissen, Dateien) |
| `File`, `Document` | die Datei im Editor und ihr zweisprachiges Dokument |
| `Segment` | das gewählte Segment |
| `Arguments` | Argumente aus dem Reiter (`name=wert` je Zeile) oder aus der API |
| `ChatModel` | das eingestellte Sprachmodell (`AskAsync(system, user)`), sonst `null` |
| `Search` | die eingestellte Websuche, sonst `null` |
| `Api` | die interne Programmierschnittstelle mit **allen** Funktionen (siehe unten) |
| `Log` | Fortschrittsmeldungen, erscheinen im Reiter |

Mit `DocumentChanged: true` speichert das Programm das Dokument und lädt den Editor neu.
Befehle sind auch über die API aufrufbar: `POST /api/v2/extensions/commands/{id}`.

## Die interne Programmierschnittstelle (context.Api)

`context.Api` ist vom Typ `ISententiam` (Namensraum `SententiamConverte.Core.Sdk`) und bietet alles, was das
Programm kann – typisiert und direkt im Programm, ohne Umweg über das Netz:

| Bereich | Beispiele |
|---|---|
| Projekte und Dateien | `ProjectsAsync`, `CreateProjectAsync`, `AddFileAsync`, `SegmentsAsync`, `TargetFileAsync`, `XliffAsync` |
| Segmente | `UpdateSegmentAsync`, `ConfirmSegmentAsync`, `MatchesAsync`, `SegmentTermsAsync`, `ApplyMatchAsync`, `SplitSegmentAsync` |
| Stapel und Qualität | `PretranslateAsync`, `AnalysisAsync`, `QaAsync`, `SetQaSettingsAsync` |
| TM | `SearchTmAsync`, `ConcordanceAsync`, `AddToTmAsync`, `ImportTmAsync`, `ExportTmAsync` |
| Terminologie und Wissen | `SearchTermsAsync`, `AddConceptAsync`, `SearchKnowledgeAsync`, `AddKnowledgeAsync` |
| Pakete | `ImportPackageAsync`, `ReturnPackageAsync`, `AlignAsync`, `RecognizePdfAsync` |
| Agenten | `TranslateAsync`, `EvaluateAsync`, `CheckUsageAsync`, `ResearchAsync`, `FillKnowledgeAsync`, `CompareTermAsync` |
| Erweiterungen | `ExtensionsAsync`, `RunCommandAsync` (Befehle anderer Erweiterungen) |

```csharp
public async Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
{
    var api = context.Api!;
    var name = context.Project!.Name;
    await api.PretranslateAsync(name, new PretranslateDto(ConfirmExact: true), ct);
    var offen = 0;
    foreach (var file in (await api.GetProjectAsync(name, ct)).Files)
        foreach (var lang in file.Languages)
            offen += (await api.SegmentsAsync(name, file.Id, lang, ct)).Count(s => s.Status == SegmentStatus.NotTranslated);
    return new ExtensionResult($"Vorübersetzt; noch {offen} Segmente offen.");
}
```

Dieselbe Schnittstelle gibt es außerhalb des Programms als .NET-Client der REST-API (`SententiamClient` im
Paket `SententiamConverte.Sdk`, siehe API-Referenz). Code gegen `ISententiam` läuft deshalb unverändert als
Erweiterung, als eigenes Programm oder als Skript.

Ein Dokument entweder über `context.Document` (direkt im Speicher) oder über `context.Api` bearbeiten, nicht
beides zugleich: die Schnittstelle speichert das offene Dokument vorher und lädt den Editor danach neu.
Der Befehl „Projektbericht“ der Beispiel-Erweiterung zeigt die Nutzung.

## Sicherheit und gute Sitten

- Erweiterungen laufen mit den Rechten des Benutzers. Nach der Installation sind sie ausgeschaltet; der Benutzer
  muss sie bewusst einschalten. Schreiben Sie in `description` ehrlich, was die Erweiterung tut.
- Daten nur dorthin senden, wo der Benutzer es erwartet; Webseiten nur über `context.Search` bzw. unter
  Beachtung von robots.txt und Nutzungsvorbehalten lesen.
- Lange Befehle sollten `ct` beachten (Schaltfläche **Abbrechen**).
- Bibliotheken werden aus dem Speicher geladen; `Assembly.Location` ist daher leer. Eigene Dateien im
  Erweiterungsordner findet man unter `<Datenordner>/erweiterungen/<id>/`.

## Testen

```bash
cd Erweiterungen/Beispiel.Erweiterung
dotnet build -c Release
```

Das Paket `bin/Release/net10.0/Beispiel.Erweiterung.scext` im Reiter **Erweiterungen** installieren,
einschalten und die Befehle ausprobieren. In automatischen Tests lässt sich `ExtensionManager` direkt verwenden
(`Install`, `SetEnabled`, `FindCommand`) – siehe `SententiamConverte.Core.Tests/ExtensionTests.cs`.
