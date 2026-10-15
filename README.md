# Sententiam converte

Übersetzungsumgebung (CAT-Werkzeug) für professionelle Übersetzer: Translation Memory, Terminologie,
Qualitätsprüfung und Projektverwaltung – erweitert um KI-Agenten, die im Netz recherchieren, prüfen,
ob Ausdrücke wirklich üblich sind, und Übersetzungsvarianten bewerten.

Läuft auf **Windows, Linux, macOS** (Desktop) und **Android** aus einer gemeinsamen Codebasis (.NET 10, Avalonia 12).

## Aufbau

| Projekt | Inhalt |
|---|---|
| `SententiamConverte.Core` | Alles ohne Oberfläche: Datenmodell, Segmentierung, TM, Terminologie, QA, Analyse, Dateifilter, Alignment, Pakete, KI-Anbieter, Agenten |
| `SententiamConverte` | Gemeinsame Oberfläche (Avalonia, MVVM) |
| `SententiamConverte.Desktop` | Start für Windows/Linux/macOS |
| `SententiamConverte.Android` | Start für Android |
| `SententiamConverte.Server` | Teamserver: gemeinsame TMs, Terminologien, Wissensdatenbanken und gemeinsame Projekte |
| `SententiamConverte.Api` | REST-Schnittstelle (v2) mit allen Funktionen – im Teamserver und lokal in der Desktop-App |
| `SententiamConverte.Sdk` | .NET-Client der REST-API (`SententiamClient`) – dieselbe Schnittstelle `ISententiam` wie intern |
| `Erweiterungen/Beispiel.Erweiterung` | Vorlage für eigene Erweiterungen (Dateiformat, QA-Prüfung, Befehle) |
| `SententiamConverte.Core.Tests` | Tests des Kerns (xUnit v3) |
| `SententiamConverte.UiTests` | Oberflächentests ohne Bildschirm (Avalonia.Headless), erzeugen Bildschirmfotos |
| `build/Paketbau` | erzeugt die Installationspakete aller Plattformen |
| `docs/` | Handbuch, API-Referenz, Anleitung für Erweiterungen (Deutsch, `docs/en/` Englisch) – auch im Programm unter „Hilfe“ |

## Funktionen

**Projekte und Dateien**
- Projekte mit einer Ausgangs- und mehreren Zielsprachen, Fachgebiet, Projekt-TM und Projekt-Terminologie
- Dateiformate: DOCX, XLSX, PPTX (Formatierungen als Tags, Bilder/Felder bleiben erhalten), ODT/ODS/ODP
  (OpenDocument), IDML (InDesign-Austauschformat), XLIFF 1.2/2.x (auch .sdlxliff mit Segmentstatus),
  PDF (Ausgabe als DOCX), HTML, Markdown, RESX, JSON, gettext PO, Untertitel SRT/VTT, Text – weitere über
  Erweiterungen. Platzhalter wie `{0}`, `%s`, `${name}` werden in Software-Texten geschützt.
- Projektpakete im Format .sdlppx öffnen und Rückgabepakete (.sdlrpx) erzeugen
- Gescannte PDFs per KI-Texterkennung (Claude-Zugang) in bearbeitbare DOCX umwandeln
- Vorübersetzung, Analyse nach Trefferstufen (Kontext, Wiederholungen, 100 %, 95–99 % … kein Treffer)
- Zieldatei im Originalformat erzeugen, Vorschau, zweisprachiges XLIFF exportieren

**Editor**
- Quelle/Ziel nebeneinander, Tags farbig hervorgehoben, Status je Segment, Filter, Suchen/Ersetzen
- TM-Treffer mit Prozentwert; Zahlen in unscharfen Treffern werden automatisch angepasst
- **Kontext im TM**: Jede Übersetzung wird mit vorherigem/nächstem Satz und Stelle im Dokument gespeichert.
  Gleicher Satz im gleichen Zusammenhang = **Kontexttreffer (101 %)**; bei Software-Texten zählt die gleiche ID.
  Derselbe Satz darf je Kontext verschieden übersetzt sein; gibt es mehrere Übersetzungen und keine passt zum
  Kontext, wird um 1 % abgewertet. Kontexttreffer werden beim Vorübersetzen auf Wunsch gleich bestätigt.
- Fragment-Treffer (kürzere TM-Einträge, die im Segment vorkommen) und AutoSuggest beim Tippen
- Terminologieerkennung (auch mit Endungen), verbotene Benennungen, Begriffe direkt aufnehmen
- Automatische Übernahme in gleiche Segmente, Segmente teilen und zusammenführen
- Kommentare je Segment, Änderungsverlauf mit Wortvergleich
- QA: Tags, Zahlen, Leerzeichen, Satzzeichen, Klammern, Wortdopplungen, Länge, Konsistenz, Terminologie,
  unerwünschte Wörter, Rechtschreibung (Hunspell) und eigene Regeln mit regulären Ausdrücken

**TM und Terminologie**
- TM in SQLite mit Volltextindex; Konkordanzsuche; Einträge blättern, bearbeiten, löschen
- Import/Export: TMX, TBX, XML-Terminologieexporte; vorhandene .sdltm-Dateien direkt übernehmen
- Alignment: alte Übersetzungen satzweise ausrichten und ins TM übernehmen

**Wissen und Teamarbeit**
- Wissensdatenbanken (Sachwissen, Definitionen, Sprachgebrauch, Stilregeln – jeweils mit Quelle und Autor),
  pro Projekt und pro Sprache anbindbar; passende Einträge erscheinen im Editor und gehen in KI-Übersetzung
  und Bewertung ein, Stilregeln der Zielsprache immer
- **Wissens-Agent**: füllt eine Wissensdatenbank aus Online-Quellen in den gewählten Sprachen; jeder Eintrag
  braucht ein wörtliches Belegzitat, das tatsächlich auf der gelesenen Seite steht, sonst wird er verworfen
- **Teamserver**: mehrere Übersetzer arbeiten gleichzeitig mit gemeinsamen TMs, Terminologien und
  Wissensdatenbanken; bestätigte Übersetzungen fließen auf Wunsch sofort ins gemeinsame TM („schreibt zurück“),
  Team-Aktivität zeigt, wer was beigetragen hat. Rollen: Admin, Bearbeiter, Leser.
- **Gemeinsame Projekte**: mehrere Personen bearbeiten dieselben Dateien gleichzeitig. Änderungen werden alle paar
  Sekunden abgeglichen, ein Segment in Arbeit ist für die anderen gesperrt (mit Namen), bei gleichzeitigen
  Änderungen gilt die zuerst gespeicherte Fassung und die andere landet im Verlauf
- Jede Ressource (lokal oder Server) lässt sich auf bestimmte Sprachen beschränken; ist der Server nicht
  erreichbar, arbeitet das Projekt mit den lokalen Daten weiter

**Terminologie-Assistent (Begriffsvergleich)**
- Für kontextabhängige Fachbegriffe, z. B. im Recht: klärt die Bedeutung im konkreten Satz, sucht auf den
  **Fachseiten** beider Sprachen nach Definitionen und Verwendungen und vergleicht die Begriffe der beiden
  Systeme (z. B. Rechtsordnungen): volle, teilweise oder keine Entsprechung, gemeinsame Merkmale, Unterschiede,
  wann eine Lösung passt und wann nicht
- Empfiehlt eine Lösung mit Strategie (funktionale Entsprechung, wörtlich mit Erläuterung, Original mit
  Erklärung, Umschreibung) und begründet sie mit nummerierten, **überprüften Belegzitaten**
- Ergebnis per Klick in die Terminologie (empfohlen = bevorzugt, Teilentsprechungen = zugelassen) oder als Wissen
- Im Editor: Begriff im Quelltext markieren, Strg+Umschalt+B
- **Fachquellen-Profile** je Thema (Recht, Medizin, Technik, EU – frei erweiterbar): je Sprache das System
  (z. B. Rechtsordnung) und die bevorzugten Fachseiten; gilt auch für Themenrecherche und Wissens-Agent

**Schnittstelle (API)**
- REST-Schnittstelle mit **allen Funktionen**: Projekte, Dateien, Segmente (setzen, bestätigen, teilen,
  zusammenführen, Treffer, Termini, Wissen), Vorübersetzung, Analyse, QA, TM, Terminologie, Wissen, Pakete,
  Alignment, Texterkennung, alle Agenten und die Befehle der Erweiterungen
- Beschreibung im OpenAPI-Format unter `/openapi/v2.json`; Anmeldung mit Zugangsschlüssel (Bearer)
- Im Teamserver mit dessen Benutzern und Rollen; Änderungen erreichen alle, die das Projekt offen haben
- In der Desktop-App auf Wunsch lokal (nur 127.0.0.1) – Skripte arbeiten am geöffneten Projekt, der Editor
  zeigt Änderungen sofort
- **SDK für C#/.NET**: Die Programmierschnittstelle `ISententiam` (im Kern, `SententiamConverte.Core.Sdk`) enthält
  jede Funktion als typisierte Methode – intern umgesetzt von `SententiamService` (darauf baut auch die REST-API
  auf) und extern von `SententiamClient` (Paket `SententiamConverte.Sdk`). Code gegen `ISententiam` läuft
  unverändert als Erweiterung, als eigenes Programm oder als Skript; beide Wege sind mit demselben Testablauf geprüft.
- Referenz und Beispiele (curl, PowerShell, Python, C#): [docs/API-Referenz.md](docs/API-Referenz.md)

**Erweiterungen**
- Reiter „Erweiterungen“: Erweiterungen anderer Entwickler installieren (`.scext`), bewusst einschalten,
  ausschalten, entfernen; ihre Befehle wirken auf das im Editor Geöffnete
- Erweiterungen liefern Dateiformate, QA-Prüfungen und Befehle (auch mit KI und Websuche); geschrieben z. B. in C#,
  mit Zugriff auf **alle Funktionen** des Programms über die interne Schnittstelle `context.Api`
- Für Entwickler: NuGet-Paket `SententiamConverte.Core` (Schnittstellen und Datentypen) aus dem Paketbau
- Erweiterungen laufen mit den Rechten des Benutzers und sind deshalb nach der Installation ausgeschaltet
- Vorlage: `Erweiterungen/Beispiel.Erweiterung` (CSV-Format, Prüfung „Ausrufezeichen“, typografische
  Anführungszeichen, Segment-Statistik, KI-Alternative, Projektbericht über `context.Api`);
  Anleitung: [docs/Erweiterungen-entwickeln.md](docs/Erweiterungen-entwickeln.md)

**Oberfläche und Hilfe**
- Deutsch und Englisch (Einstellungen → Sprache der Oberfläche; Standard: Systemsprache)
- Handbuch, API-Referenz und Entwickleranleitung im Programm unter „Hilfe“ (F1), mit Suche

**KI**
- KI-Zugänge frei definierbar: Claude (Anthropic-API) oder jeder Server mit OpenAI-kompatibler Schnittstelle,
  lokal (llama.cpp/Strata, Ollama, LM Studio, vLLM) oder in der Cloud. Je Aufgabe ein anderer Zugang möglich.
- **Themenrecherche**: Suchanfragen in jeder Sprache, Fachtexte lesen, mehrsprachige Begriffsliste mit Belegen
- **Usus-Prüfung**: exakte Wortfolgen-Suche für Ausdruck und Alternativen, Zählung wörtlicher Belege, Urteil
- **Bewertung**: Varianten nach Richtigkeit, Sprachfluss, Terminologie und Stil benoten; fragliche Wendungen prüfen
- KI-Übersetzung einzelner oder aller leeren Segmente mit TM-Treffern, Terminologie und Nachbarsätzen als Kontext
- Websuche über offizielle Schnittstellen: Claude-Websuche, Brave Search, Tavily oder eigene SearXNG-Instanz

## Starten

```bash
dotnet run --project SententiamConverte.Desktop
```

Tests:

```bash
dotnet test --project SententiamConverte.Core.Tests
dotnet test --project SententiamConverte.UiTests
```

Installationspakete für alle Plattformen (Ergebnis in `artifacts/<Version>/`):

```bash
dotnet run --project build/Paketbau
```

| Paket | Inhalt |
|---|---|
| `SententiamConverte-<v>-win-x64.zip` | Windows 10/11, entpacken und starten |
| `SententiamConverte-<v>-linux-x64.tar.gz` | Linux, mit `installieren.sh` für Menüeintrag |
| `SententiamConverte-<v>-osx-arm64.tar.gz` / `-osx-x64.tar.gz` | macOS (Apple-Chip / Intel), `Sententiam converte.app` |
| `SententiamConverte-<v>-android.apk` | Android 6 und neuer |
| `SententiamConverte-Server-<v>.zip` | Teamserver (braucht die ASP.NET Core Runtime 10) |
| `Beispiel.Erweiterung-<v>.scext` | Beispiel-Erweiterung |
| `SententiamConverte.Core.<v>.nupkg`, `SententiamConverte.Sdk.<v>.nupkg` | SDK für Entwickler (Erweiterungen, .NET-Client) |

Einzelne Ziele: `dotnet run --project build/Paketbau -- win-x64 android`. Die Pakete sind nicht signiert
(Windows SmartScreen bzw. macOS Gatekeeper fragen beim ersten Start nach; Hinweise stehen in `LIESMICH.txt`).
Für eine Veröffentlichung im Play Store wird ein eigener Signaturschlüssel gebraucht.

## Teamserver betreiben

```bash
dotnet run --project SententiamConverte.Server -- --daten ./teamdaten
```

Beim ersten Start wird der Benutzer `admin` angelegt und sein Zugangsschlüssel einmalig ausgegeben.
Weitere Benutzer und Ressourcen:

```bash
dotnet run --project SententiamConverte.Server -- --daten ./teamdaten benutzer neu Anna bearbeiter
dotnet run --project SententiamConverte.Server -- --daten ./teamdaten ressource neu tm Maschinenbau
```

Erweiterungen auf dem Server (der Admin entscheidet, was läuft):

```bash
dotnet run --project SententiamConverte.Server -- --daten ./teamdaten erweiterung installieren Beispiel.Erweiterung.scext
dotnet run --project SententiamConverte.Server -- --daten ./teamdaten erweiterung ein beispiel.erweiterung
```

Die REST-API (v2) läuft immer mit; die Agenten nutzen dort `<daten>/ki-einstellungen.json` (gleiches Format wie
die KI-Einstellungen der App). Als Container: siehe `SententiamConverte.Server/Dockerfile`. Der Server lauscht auf Port 5280. Außerhalb des
eigenen Netzes nur über HTTPS betreiben (z. B. hinter einem Reverse Proxy), da die Zugangsschlüssel sonst
unverschlüsselt übertragen werden. In der App unter **Einstellungen → Teamserver** Adresse und Schlüssel
eintragen, dann unter **TM & Terminologie → Ressource anbinden** die gemeinsamen Ressourcen wählen.

## KI einrichten

Unter **Einstellungen** einen Zugang aus einer Vorlage hinzufügen, Adresse/Modell anpassen, „testen“, speichern.
API-Schlüssel können als `env:NAME` eingetragen werden (z. B. `env:ANTHROPIC_API_KEY`); dann steht der Schlüssel
nicht in der Einstellungsdatei. Einstellungen liegen in `%LOCALAPPDATA%\SententiamConverte\`
(Linux/macOS: `~/.local/share/SententiamConverte/`); mit der Umgebungsvariablen `SENTENTIAM_HOME` lässt sich
ein anderer Ort wählen (z. B. für eine portable Installation).

Für kleine lokale Modelle „Zeichen pro Webseite“ auf etwa 2000–3000 senken.

## Tastenkürzel im Editor

| Kürzel | Wirkung |
|---|---|
| Strg+Eingabe | Segment bestätigen, ins TM schreiben, zum nächsten offenen |
| Strg+Alt+Eingabe | als lektoriert bestätigen |
| Strg+↓ / Strg+↑ | nächstes / vorheriges Segment |
| Strg+1 … 9 | TM-Treffer Nr. n einsetzen |
| Strg+Einfg / Strg+Entf | Quelle ins Ziel kopieren / Ziel leeren |
| Alt+T / Alt+Z | Segment an der Cursorposition im Quelltext teilen / mit dem nächsten zusammenführen |
| Tab oder Eingabe | AutoSuggest-Vorschlag übernehmen (↑/↓ wählen, Esc schließen) |
| F3 | markierten Text in der Konkordanz suchen |
| Strg+F2 | markierten Quell- und Zieltext als Begriff aufnehmen |
| Strg+Umschalt+T / E / U | KI-Übersetzung / Varianten bewerten / Usus prüfen |
| Strg+Umschalt+B | markierten Begriff im Kontext klären (Begriffsvergleich) |
| Strg+Umschalt+G | nächsten fehlenden Tag einsetzen |
| Strg+S | speichern |
| F1 | Hilfe |

## Projektordner

```
<Projekt>/
  project.json            Einstellungen, Dateiliste, QA-Regeln
  quelle/                 Originaldateien
  arbeit/<id>.<lang>.json zweisprachige Arbeitsfassungen (mit Kommentaren und Verlauf)
  tm/projekt.sctm         Projekt-TM (SQLite)
  terminologie/projekt.sctb
  wissen/projekt.sckb     Projekt-Wissen
  paket/                  Originalpaket, falls das Projekt aus einem Paket stammt
```

## Rechtliches

- **Marken**: Das Programm verwendet keine fremden Produktnamen. Fremde Austauschformate werden nur über ihre
  Dateiendung genannt und ausschließlich zur Interoperabilität gelesen und geschrieben; es enthält keinen Code
  anderer Hersteller. Namen von KI-Anbietern dienen nur zur Angabe, womit eine Verbindung möglich ist.
- **Websuche und Webseiten**: Es werden nur offizielle Such-Schnittstellen genutzt, keine Suchmaschinenseiten
  ausgelesen. Beim Lesen von Webseiten meldet sich das Programm mit eigener Kennung, beachtet robots.txt sowie
  Nutzungsvorbehalte für Text und Data Mining (§ 44b UrhG, Meta/Header `tdm-reservation`, `noai`) und
  übernimmt nur kurze Belegzitate.
- **Teamserver**: Die Daten liegen auf eurem eigenen Server; gespeichert werden nur Prüfsummen der Zugangsschlüssel.
  Mitarbeitende sollten wissen, dass Beiträge mit ihrem Namen protokolliert werden (Team-Aktivität).
- **Datenschutz/Vertraulichkeit**: Bei Cloud-Zugängen verlassen die Texte den Rechner – die Einstellungen weisen
  darauf hin. Für vertrauliche Aufträge einen lokalen Zugang verwenden; Vereinbarungen mit Kunden und die DSGVO beachten.
- **Lizenzen**: siehe [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Wörterbücher werden nicht mitgeliefert,
  sondern auf Wunsch aus dem LibreOffice-Projekt geladen und stehen unter deren eigenen Lizenzen.

Diese Hinweise sind keine Rechtsberatung; vor einer Veröffentlichung oder einem Verkauf lohnt eine kurze anwaltliche Prüfung.

## Leistung

Gemessen auf einem Arbeitsplatzrechner mit einem TM aus 200 000 Einheiten (Test `PerformanceTests`, nur auf
Anforderung: `dotnet test --project SententiamConverte.Core.Tests -- --explicit only`):

| Vorgang | Dauer |
|---|---|
| 200 000 TM-Einheiten einfügen (mit Kontext) | 104 s |
| unscharfe TM-Suche | 29 ms je Segment |
| exakte Suche mit Kontext | 28 ms je Segment |
| Fragment-Treffer | 1 ms je Segment |
| Konkordanzsuche | 0,23 s je Suche |
| Dokument mit 3 000 Segmenten laden / speichern | 0,1 s / 0,05 s |
| Vorübersetzung (3 000 Segmente) | 22 s |
| Analyse (3 000 Segmente) | 17 s |
| Segment bestätigen (ins TM mit Kontext) | 4 ms |

Richtwerte vom 15.10.2026 (Release-Build; auf dem Rechner liefen nebenbei andere Builds).

## Noch offen

- Test auf echten Geräten: macOS-Paket (nur unter Windows gebaut, nicht auf einem Mac ausgeführt) und Android-Gerät
- Test mit echten KI-Zugängen (bisher: nachgebildete Modelle und ein lokaler Testserver)
- Signierte Pakete (Windows-Codesignatur, Apple-Beglaubigung, Play-Store-Schlüssel)
- Weitere Formate, z. B. DITA, und weitere Oberflächensprachen
