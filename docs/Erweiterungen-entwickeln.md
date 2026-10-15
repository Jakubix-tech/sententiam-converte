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
    <!-- Die Schnittstellen liefert das Programm: nicht mitkopieren -->
    <Reference Include="SententiamConverte.Core">
      <HintPath>pfad/zum/Programm/SententiamConverte.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

Im Quellcode-Baum genügt eine `ProjectReference` mit `Private="false"`. Die Vorlage enthält außerdem ein
MSBuild-Ziel, das nach dem Bauen automatisch das `.scext`-Paket erzeugt.

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
| `Log` | Fortschrittsmeldungen, erscheinen im Reiter |

Mit `DocumentChanged: true` speichert das Programm das Dokument und lädt den Editor neu.
Befehle sind auch über die API aufrufbar: `POST /api/v2/extensions/commands/{id}`.

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
