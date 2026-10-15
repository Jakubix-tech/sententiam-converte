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
| `SententiamConverte.Server` | Teamserver für gemeinsame TMs, Terminologien und Wissensdatenbanken |
| `SententiamConverte.Core.Tests` | Tests des Kerns (xUnit v3) |
| `SententiamConverte.UiTests` | Oberflächentests ohne Bildschirm (Avalonia.Headless), erzeugen Bildschirmfotos |

## Funktionen

**Projekte und Dateien**
- Projekte mit einer Ausgangs- und mehreren Zielsprachen, Fachgebiet, Projekt-TM und Projekt-Terminologie
- Dateiformate: DOCX, XLSX, PPTX (Formatierungen als Tags, Bilder/Felder bleiben erhalten), XLIFF 1.2/2.x
  (auch .sdlxliff mit Segmentstatus), PDF (Ausgabe als DOCX), HTML, Markdown, RESX, JSON, gettext PO,
  Untertitel SRT/VTT, Text. Platzhalter wie `{0}`, `%s`, `${name}` werden in Software-Texten geschützt.
- Projektpakete im Format .sdlppx öffnen und Rückgabepakete (.sdlrpx) erzeugen
- Gescannte PDFs per KI-Texterkennung (Claude-Zugang) in bearbeitbare DOCX umwandeln
- Vorübersetzung, Analyse nach Trefferstufen (Kontext, Wiederholungen, 100 %, 95–99 % … kein Treffer)
- Zieldatei im Originalformat erzeugen, Vorschau, zweisprachiges XLIFF exportieren

**Editor**
- Quelle/Ziel nebeneinander, Tags farbig hervorgehoben, Status je Segment, Filter, Suchen/Ersetzen
- TM-Treffer mit Prozentwert; Zahlen in unscharfen Treffern werden automatisch angepasst
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
- Jede Ressource (lokal oder Server) lässt sich auf bestimmte Sprachen beschränken; ist der Server nicht
  erreichbar, arbeitet das Projekt mit den lokalen Daten weiter

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

Veröffentlichen (Beispiele):

```bash
dotnet publish SententiamConverte.Desktop -c Release -r win-x64 --self-contained
dotnet publish SententiamConverte.Desktop -c Release -r linux-x64 --self-contained
dotnet publish SententiamConverte.Desktop -c Release -r osx-arm64 --self-contained
dotnet publish SententiamConverte.Android -c Release
```

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

Als Container: siehe `SententiamConverte.Server/Dockerfile`. Der Server lauscht auf Port 5280. Außerhalb des
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
| Strg+S | speichern |

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

## Noch offen

- Gemeinsames Bearbeiten derselben Datei durch mehrere Personen (bisher: gemeinsame Ressourcen, Dateien werden je Person bearbeitet)
- Weitere Formate: IDML (InDesign), ODT/ODS (OpenDocument), DITA
- Test mit echten KI-Zugängen (bisher: nachgebildete Modelle und ein lokaler Testserver) und auf einem Android-Gerät
