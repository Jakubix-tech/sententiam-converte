# API-Referenz

Die REST-Schnittstelle (Version 2) macht alle Funktionen von Sententiam converte für Skripte und andere
Programme nutzbar. Sie läuft im **Teamserver** (immer) und auf Wunsch **lokal in der Desktop-App**
(Einstellungen → Schnittstelle (API), nur 127.0.0.1).

## Grundlagen

- **Adresse**: `http://<server>:5280/api/v2/` (Teamserver) bzw. `http://127.0.0.1:5290/api/v2/` (Desktop, Port
  einstellbar).
- **Anmeldung**: Kopfzeile `Authorization: Bearer <Zugangsschlüssel>`. Ohne gültigen Schlüssel: `401`.
- **Rechte**: Leser dürfen nur lesen (`GET`); Bearbeiter dürfen ändern; Dateien löschen nur Admins (`403` sonst).
  In der Desktop-App hat der Schlüssel alle Rechte.
- **Format**: JSON (UTF-8), Feldnamen in camelCase, Aufzählungen als Text (z. B. `"status": "Translated"`).
  Dateien werden als `{ "name": "...", "base64": "..." }` übertragen.
- **Fehler**: `400` mit Klartext bei ungültigen Angaben, `404` wenn Projekt, Datei oder Segment fehlt,
  `502` wenn ein KI-Anbieter oder der Teamserver nicht antwortet.
- **Beschreibung**: `GET /openapi/v2.json` (OpenAPI 3) – damit lassen sich Clients für jede Sprache erzeugen.
- **Bezeichner**: `{project}` ist der Projektname (Ordnername), `{file}` die Datei-ID oder der Dateiname,
  `{lang}` die Zielsprache, `{id}` die Segmentnummer.

Im Teamserver arbeitet die API mit den gemeinsamen Projekten des Servers. Änderungen über die API erreichen
alle, die das Projekt gerade im Programm offen haben, beim nächsten Abgleich – wie die Änderung einer Kollegin.

## Allgemein

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `/` | Name, Version, Bereiche |
| GET | `/formats` | unterstützte Dateiformate (auch aus Erweiterungen) |
| GET | `/languages` | bekannte Sprachcodes |
| GET | `/source-profiles` | Fachquellen-Profile |

## Projekte und Dateien

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `/projects` | alle Projekte |
| POST | `/projects` | anlegen: `{ name, sourceLanguage, targetLanguages[], topic }` |
| GET | `/projects/{project}` | Projekt mit Dateien und Ressourcen |
| PATCH | `/projects/{project}` | `{ topic, pretranslateMinScore, confirmContextMatches, sourceProfile }` |
| POST | `/projects/{project}/resources` | TM, Terminologie oder Wissen anbinden: `{ kind, location, server, languages[], writeBack }` |
| DELETE | `/projects/{project}/resources?kind=&location=&server=` | Ressource lösen |
| GET | `/projects/{project}/files` | Dateiliste |
| POST | `/projects/{project}/files` | Datei hinzufügen: `{ name, base64 }` |
| DELETE | `/projects/{project}/files/{file}` | Datei entfernen (Admin) |
| GET | `/projects/{project}/files/{file}/{lang}` | alle Segmente |
| GET | `/projects/{project}/files/{file}/{lang}/target` | Zieldatei im Originalformat (Base64) |
| GET | `/projects/{project}/files/{file}/{lang}/xliff` | zweisprachiges XLIFF (Base64) |
| POST | `/projects/{project}/files/{file}/{lang}/replace?find=&replace=&matchCase=` | Suchen und Ersetzen im Ziel |

## Segmente

Pfad-Anfang: `/projects/{project}/files/{file}/{lang}/segments/{id}`

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `` | Segment (Quelle, Ziel, Status, Herkunft, Treffer-%, Kommentar, Kontext, Verlauf) |
| PUT | `` | `{ target, status, comment }` – jedes Feld optional |
| POST | `/confirm` | bestätigen: ins TM (mit Kontext), auf gleiche Segmente übertragen; Antwort `{ segment, propagated }` |
| GET | `/matches?minScore=` | TM-Treffer (101 = Kontexttreffer) |
| GET | `/terms` | erkannte Terminologie |
| GET | `/fragments` | Fragment-Treffer |
| GET | `/knowledge` | passende Wissenseinträge |
| GET | `/suggestions` | AutoSuggest-Vorschläge |
| POST | `/apply-match?index=` | Treffer Nr. `index` einsetzen (Zahlen werden angepasst) |
| POST | `/copy-source` | Quelle ins Ziel kopieren |
| POST | `/split` | `{ sourceIndex }` – an dieser Stelle im Quelltext teilen |
| POST | `/merge` | mit dem folgenden Segment zusammenführen |

Status-Werte: `NotTranslated`, `Draft`, `Translated`, `TranslationRejected`, `Reviewed`, `ReviewRejected`, `Signed`. Gesperrte Segmente haben `locked: true`.

## Stapelaufgaben und Qualität

| Methode | Pfad | Wirkung |
|---|---|---|
| POST | `/projects/{project}/pretranslate` | `{ confirmExact, overwrite }` – Vorübersetzung aller Dateien |
| GET | `/projects/{project}/analysis` | Analyse je Sprache und Trefferstufe |
| GET | `/projects/{project}/qa` | QA-Meldungen aller Dateien |
| PUT | `/projects/{project}/qa-settings` | QA-Einstellungen und eigene Regeln |

## Translation Memory

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `/projects/{project}/tm/search?source=&sourceLanguage=&targetLanguage=&minScore=&previous=&next=` | Suche, optional mit Kontext |
| GET | `/projects/{project}/tm/concordance?text=&sourceLanguage=&targetLanguage=&inTarget=` | Konkordanz |
| POST | `/projects/{project}/tm` | `{ sourceLanguage, targetLanguage, source, target, previous, next, structure }` |
| GET | `/projects/{project}/tm/units?filter=&offset=&limit=` | Projekt-TM blättern |
| PUT | `/projects/{project}/tm/units/{id}` | `{ source, target }` |
| DELETE | `/projects/{project}/tm/units/{id}` | Eintrag löschen |
| POST | `/projects/{project}/tm/import` | TMX oder .sdltm (`{ name, base64 }`), Antwort: Anzahl |
| GET | `/projects/{project}/tm/export` | Projekt-TM als TMX |

## Terminologie und Wissen

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `/projects/{project}/terms?query=&language=` | suchen (ohne `query`: alle des Projekts) |
| POST | `/projects/{project}/terms` | `{ terms: [{ lang, text, status }], definition, domain, source }` |
| DELETE | `/projects/{project}/terms/{id}` | Begriff löschen |
| POST | `/projects/{project}/terms/import` | TBX/XML (`{ name, base64 }`) |
| GET | `/projects/{project}/terms/export` | TBX |
| GET | `/projects/{project}/knowledge?query=&language=` | Wissen aller angebundenen Datenbanken |
| POST | `/projects/{project}/knowledge` | `{ language, topic, kind, title, content, quote, sourceUrl, sourceTitle }` |
| PUT | `/projects/{project}/knowledge/{id}` | Eintrag ändern |
| DELETE | `/projects/{project}/knowledge/{id}` | Eintrag löschen |

Status für Benennungen: `Preferred`, `Admitted`, `Forbidden`. Arten von Wissen: `Fact`, `Definition`, `Usage`, `Style`, `Note`.

## Pakete, Alignment, Texterkennung

| Methode | Pfad | Wirkung |
|---|---|---|
| POST | `/packages/import?topic=&name=` | .sdlppx als neues Projekt (`{ name, base64 }`) |
| GET | `/projects/{project}/return-package` | Rückgabepaket .sdlrpx |
| POST | `/align` | `{ source, target, sourceLanguage, targetLanguage, importIntoProject }` – Dateien als `{ name, base64 }` |
| POST | `/projects/{project}/ocr` | gescanntes PDF per KI erkennen, als DOCX hinzufügen |

## Agenten

| Methode | Pfad | Wirkung |
|---|---|---|
| POST | `/agents/translate` | Segment (`project, file, language, segmentId`) oder freier Text (`text, sourceLanguage, targetLanguage, topic`) |
| POST | `/agents/evaluate` | `{ source, sourceLanguage, targetLanguage, candidates: [{ label, text }], topic, checkUsage, project }` |
| POST | `/agents/usage` | `{ expression, language, topic, alternatives[] }` |
| POST | `/agents/research` | `{ topic, sourceLanguage, targetLanguages[], seedTerms[], pagesPerLanguage, sourceProfile, saveToProject }` |
| POST | `/agents/knowledge` | `{ project, topic, languages[], focus, pagesPerLanguage, knowledgeBase, sourceProfile }` |
| POST | `/agents/compare` | Begriffsvergleich: `{ term, sourceLanguage, targetLanguage, context, topic, sourceProfile, candidates[], project, saveToTermbase, saveToKnowledge }` |

Die Agenten nutzen die KI-Einstellungen des Gastgebers (Desktop: Einstellungen → KI; Teamserver:
`<daten>/ki-einstellungen.json` im selben Format wie `ai-settings.json`). Sie können je nach Umfang einige
Minuten laufen; Clients sollten eine entsprechende Wartezeit einstellen.

## Erweiterungen

| Methode | Pfad | Wirkung |
|---|---|---|
| GET | `/extensions` | installierte Erweiterungen mit Befehlen |
| POST | `/extensions/commands/{command}` | `{ project, file, language, segmentId, arguments: { name: wert } }` |

Ändert ein Befehl das Dokument (`documentChanged: true`), wird es gespeichert und allen Beteiligten gemeldet.
Auf dem Teamserver installiert der Administrator Erweiterungen auf der Kommandozeile:
`SententiamConverte.Server erweiterung installieren <datei.scext>` und `erweiterung ein <kennung>`.

## .NET-SDK

Für C# und andere .NET-Sprachen gibt es dieselbe Schnittstelle typisiert: `ISententiam` (Paket
`SententiamConverte.Core`) mit dem Client `SententiamClient` (Paket `SententiamConverte.Sdk`). Jede Funktion der
REST-API ist eine Methode; Fehler kommen als `KeyNotFoundException`, `ArgumentException`,
`UnauthorizedAccessException` bzw. `InvalidOperationException`.

```csharp
using SententiamConverte.Core.Sdk;
using SententiamConverte.Sdk;

using var sc = new SententiamClient("http://127.0.0.1:5290", Environment.GetEnvironmentVariable("SC_KEY")!);
await sc.CreateProjectAsync(new ProjectCreateDto("Handbuch", "de-DE", ["en-GB"], "Technik"));
var datei = await sc.AddFileAsync("Handbuch", FileContent.FromFile("anleitung.docx"));
await sc.PretranslateAsync("Handbuch", new PretranslateDto(ConfirmExact: true));
foreach (var s in await sc.SegmentsAsync("Handbuch", datei.Id, "en-GB"))
    if (s.Status == SegmentStatus.NotTranslated)
    {
        var ki = await sc.TranslateAsync(new TranslateRequest("Handbuch", datei.Id, "en-GB", s.Id));
        await sc.UpdateSegmentAsync("Handbuch", datei.Id, "en-GB", s.Id, new SegmentUpdateDto(ki.Translation));
    }
var ziel = await sc.TargetFileAsync("Handbuch", datei.Id, "en-GB");
File.WriteAllBytes(ziel.Name, ziel.Content);
```

Die Pakete entstehen mit `dotnet run --project build/Paketbau -- sdk` im Ordner `artifacts/<Version>/`.
Innerhalb des Programms – in Erweiterungen – steht dieselbe Schnittstelle als `context.Api` bereit, ohne Umweg
über das Netz (siehe „Erweiterungen entwickeln“).

## Beispiele

**curl** – Projekt anlegen, Datei hochladen, vorübersetzen, Zieldatei holen:

```bash
API=http://127.0.0.1:5290/api/v2
KEY="Authorization: Bearer $SC_KEY"
curl -s -H "$KEY" -H "Content-Type: application/json" -d '{"name":"Handbuch","sourceLanguage":"de-DE","targetLanguages":["en-GB"],"topic":"Technik"}' $API/projects
curl -s -H "$KEY" -H "Content-Type: application/json" -d "{\"name\":\"anleitung.docx\",\"base64\":\"$(base64 -w0 anleitung.docx)\"}" $API/projects/Handbuch/files
curl -s -H "$KEY" -H "Content-Type: application/json" -d '{"confirmExact":true}' $API/projects/Handbuch/pretranslate
curl -s -H "$KEY" $API/projects/Handbuch/files/anleitung.docx/en-GB/target | jq -r .base64 | base64 -d > anleitung_en-GB.docx
```

**PowerShell** – alle leeren Segmente auflisten:

```powershell
$h = @{ Authorization = "Bearer $env:SC_KEY" }
$api = "http://127.0.0.1:5290/api/v2"
Invoke-RestMethod "$api/projects/Handbuch/files/anleitung.docx/en-GB" -Headers $h |
  Where-Object { $_.status -eq "NotTranslated" } | Select-Object id, source
```

**Python** – jedes Segment mit 101-%-Treffer übernehmen und bestätigen:

```python
import os, requests
api = "http://127.0.0.1:5290/api/v2"
s = requests.Session(); s.headers["Authorization"] = "Bearer " + os.environ["SC_KEY"]
seg = f"{api}/projects/Handbuch/files/anleitung.docx/en-GB"
for x in s.get(seg).json():
    hits = s.get(f"{seg}/segments/{x['id']}/matches").json()
    if hits and hits[0]["score"] == 101 and not x["target"]:
        s.post(f"{seg}/segments/{x['id']}/apply-match", params={"index": 1})
        s.post(f"{seg}/segments/{x['id']}/confirm", json={})
```

Hinweis: `apply-match` zählt ab 1 wie Strg+1 im Editor.
