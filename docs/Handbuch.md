# Handbuch

Sententiam converte ist eine Übersetzungsumgebung für professionelle Übersetzerinnen und Übersetzer:
Translation Memory (TM), Terminologie, Qualitätssicherung und Projektverwaltung – ergänzt um KI-Agenten,
die im Netz recherchieren, prüfen, ob Ausdrücke wirklich üblich sind, und Übersetzungen begründet bewerten.

## Überblick

Die Arbeit folgt immer demselben Weg:

1. **Projekt anlegen** – Ausgangssprache, eine oder mehrere Zielsprachen, Fachgebiet.
2. **Dateien hinzufügen** – sie werden in Segmente (meist Sätze) zerlegt; Formatierungen werden zu Tags.
3. **Vorübersetzen** – was das TM schon kennt, wird eingesetzt.
4. **Im Editor übersetzen** – mit TM-Treffern, Terminologie, Wissen, Konkordanz und auf Wunsch KI.
5. **Prüfen** – QA findet fehlende Tags, falsche Zahlen, Terminologieverstöße und mehr.
6. **Zieldatei erzeugen** – im Originalformat, mit allen Formatierungen.

Die Reiter oben: **Projekt**, **Editor**, **TM & Terminologie**, **Wissen**, **Agenten**, **Einstellungen**,
**Erweiterungen** und **Hilfe** (auch mit F1).

## Installation und erster Start

Das Programm läuft unter Windows, Linux, macOS und Android. Es braucht keine Installation von .NET; die
Pakete bringen alles mit.

- **Windows**: ZIP entpacken, `SententiamConverte.Desktop.exe` starten.
- **Linux**: Archiv entpacken, `SententiamConverte.Desktop` starten (ggf. ausführbar machen).
- **macOS**: `Sententiam converte.app` in den Programme-Ordner ziehen. Beim ersten Start mit Rechtsklick →
  „Öffnen“ bestätigen, solange das Programm nicht von Apple beglaubigt ist.
- **Android**: APK installieren (Installation aus unbekannten Quellen erlauben).

Einstellungen, Wörterbücher und Erweiterungen liegen unter `%LOCALAPPDATA%\SententiamConverte` (Windows) bzw.
`~/.local/share/SententiamConverte` (Linux/macOS). Mit der Umgebungsvariablen `SENTENTIAM_HOME` lässt sich ein
anderer Ort wählen, etwa für eine portable Installation auf einem USB-Stick.

Vor der ersten KI-Nutzung unter **Einstellungen** einen KI-Zugang einrichten (siehe „KI einrichten“).
Alles andere – TM, Terminologie, QA, Dateiformate – funktioniert ohne KI.

## Projekt anlegen

Reiter **Projekt** → **Neues Projekt**:

- **Name** – wird auch der Ordnername.
- **Ausgangssprache** und **Zielsprachen** als Sprachcodes, z. B. `de-DE` und `en-GB, pl-PL`.
- **Fachgebiet / Thema** – hilft dem TM bei der Kontextbewertung und den KI-Agenten bei der Recherche.
- **als gemeinsames Projekt auf dem Teamserver anlegen** – siehe „Teamarbeit“.

Danach **Dateien hinzufügen …**. Jede Datei erscheint je Zielsprache mit Fortschritt in der Liste.
Weitere Möglichkeiten:

- **Projektordner öffnen …** – ein vorhandenes Projekt öffnen; unter „Zuletzt geöffnet“ stehen die letzten.
- **Projektpaket öffnen (.sdlppx) …** – Paket eines Auftraggebers übernehmen; zurück geht es mit
  **Rückgabepaket (.sdlrpx) …**.
- **Fachquellen für die Agenten** – welches Fachquellen-Profil (Recht, Medizin, Technik …) für dieses Projekt gilt.
- **Vorübersetzen ab (%)** – Mindesttreffer, ab dem vorübersetzt wird.
- **Kontexttreffer (101 %) beim Vorübersetzen bestätigen** – solche Segmente gelten dann als fertig.

## Dateiformate

| Format | Hinweise |
|---|---|
| DOCX, XLSX, PPTX | Formatierungen werden zu Tags, Bilder, Felder und Kommentare bleiben erhalten |
| ODT, ODS, ODP (OpenDocument) | wie oben; Leerzeichen-/Tabulator-Elemente bleiben erhalten |
| IDML (InDesign) | Textabschnitte (Stories) mit Zeichenformaten und Hyperlinks |
| XLIFF 1.2 / 2.x, .sdlxliff | zweisprachige Austauschdateien, Segmentstatus wird übernommen |
| PDF | Text wird gelesen, Ausgabe als DOCX; gescannte PDFs mit **Gescanntes PDF erkennen (KI) …** |
| HTML, Markdown | Auszeichnungen werden zu Tags |
| RESX, JSON, PO | Software-Texte; Platzhalter wie `{0}`, `%s`, `${name}` sind geschützt |
| SRT, VTT | Untertitel, Zeitangaben bleiben unverändert |
| TXT | jede Zeile ein Absatz |

TMX, TBX und .sdltm werden unter **TM & Terminologie** importiert. Weitere Formate können Erweiterungen
hinzufügen (die Beispiel-Erweiterung bringt z. B. CSV mit).

## Der Editor

Doppelklick auf eine Datei (oder **Öffnen**) lädt sie in den Editor. Links steht die Quelle, rechts das Ziel.

- **Tags** (⟨1⟩ … ⟨/1⟩) stehen für Formatierungen. Sie müssen im Ziel vorkommen; fehlende Tags zeigt das
  Programm unter dem Segment an, **Strg+Umschalt+G** setzt den nächsten fehlenden an der Cursorposition ein.
- **Status**: nicht übersetzt, Entwurf, übersetzt, abgelehnt, lektoriert, Lektorat abgelehnt, abgezeichnet. Gesperrte Segmente (z. B. reine Zahlen) sind nicht bearbeitbar.
- **✔ Bestätigen** (Strg+Eingabe) speichert das Segment ins TM und springt zum nächsten offenen Segment.
  Gleiche Segmente weiter unten werden automatisch übernommen.
- **Teilen / Zusammenführen** ändert die Segmentgrenzen (nicht bei XLIFF-Dateien und gemeinsamen Projekten).
- **Filter** oben: alle Segmente, nicht übersetzt, nicht bestätigt, mit QA-Meldungen.
- **Suchen/Ersetzen** wirkt auf den Zieltext.

Die Seitenleiste rechts:

- **TM** – Treffer mit Prozentwert und Herkunft; Zahlen werden beim Einsetzen angepasst. Darunter
  **Fragment-Treffer**: kürzere TM-Einträge, die im Segment vorkommen.
- **Termini** – erkannte Begriffe der Terminologie (auch mit anderen Endungen), verbotene Benennungen rot.
- **Konkordanz** – freie Suche im TM, in Quelle oder Ziel.
- **QA** – Meldungen des Dokuments und Rechtschreibung des Segments.
- **Wissen** – passende Einträge aus den Wissensdatenbanken.
- **Notiz** – Kommentar zum Segment und **Verlauf** aller Änderungen mit Wortvergleich.
- **KI** – Übersetzen, Varianten bewerten, Usus prüfen, Begriff im Kontext klären.

Beim Tippen schlägt **AutoSuggest** Wörter und Wendungen aus TM und Terminologie vor.

## Tastenkürzel

| Kürzel | Wirkung |
|---|---|
| Strg+Eingabe | Segment bestätigen, ins TM schreiben, zum nächsten offenen |
| Strg+Alt+Eingabe | als lektoriert bestätigen |
| Strg+↓ / Strg+↑ | nächstes / vorheriges Segment |
| Strg+1 … 9 | TM-Treffer Nr. n einsetzen |
| Strg+Einfg / Strg+Entf | Quelle ins Ziel kopieren / Ziel leeren |
| Alt+T / Alt+Z | Segment an der Cursorposition im Quelltext teilen / mit dem nächsten zusammenführen |
| Tab oder Eingabe | AutoSuggest-Vorschlag übernehmen (↑/↓ wählen, Esc schließen) |
| Strg+Umschalt+G | nächsten fehlenden Tag einsetzen |
| F3 | markierten Text in der Konkordanz suchen |
| Strg+F2 | markierten Quell- und Zieltext als Begriff aufnehmen |
| Strg+Umschalt+T / E / U | KI-Übersetzung / Varianten bewerten / Usus prüfen |
| Strg+Umschalt+B | markierten Begriff im Kontext klären (Begriffsvergleich) |
| Strg+S | speichern |
| F1 | Hilfe |

## Translation Memory und Kontext

Jedes bestätigte Segment landet im **Projekt-TM** – zusammen mit dem vorherigen und dem nächsten Satz und der
Stelle im Dokument. Deshalb unterscheidet das Programm:

- **101 % Kontexttreffer** – gleicher Satz im gleichen Zusammenhang (bei Software-Texten: gleiche ID).
- **100 %** – gleicher Satz, anderer Zusammenhang.
- **unscharfe Treffer** (z. B. 85 %) – ähnlicher Satz; Unterschiede werden hervorgehoben.

Derselbe Satz darf je Zusammenhang verschieden übersetzt sein. Gibt es mehrere Übersetzungen und passt keine
zum Kontext, wird der Treffer um 1 % abgewertet – ein Hinweis, genauer hinzusehen.

Unter **TM & Terminologie**:

- **TMX importieren / exportieren**, **.sdltm übernehmen** (vorhandene TMs anderer Programme).
- **Weitere TMs/Terminologien einbinden** – zusätzliche Datenbanken, auch nur für bestimmte Sprachen.
- **TM-Einträge bearbeiten** – blättern, filtern, ändern, löschen.
- **Alignment** – Ausgangstext und vorhandene Übersetzung wählen; die Sätze werden nach Länge und Zahlen
  einander zugeordnet. Sichere Paare sind vorausgewählt, **Markierte ins TM** übernimmt sie.

## Terminologie

Begriffe bestehen aus Benennungen in mehreren Sprachen mit Status (bevorzugt, zugelassen, verboten),
Definition und Fachgebiet.

- Anlegen unter **TM & Terminologie → Neuer Eintrag** oder im Editor mit Strg+F2.
- **TBX / Terminologie-XML importieren**, **TBX exportieren**.
- Im Editor erscheinen erkannte Begriffe unter **Termini**; die QA meldet, wenn eine vorgeschriebene
  Benennung fehlt oder eine verbotene verwendet wurde.

## Qualitätssicherung

**QA alle Dateien** (Reiter Projekt) oder **Dokument prüfen** (Editor) prüft:
leere Segmente, Ziel = Quelle, Tags, Zahlen, Leerzeichen, Satzzeichen am Ende, Klammern, Terminologie,
Wortdopplungen, Konsistenz (gleiche Quelle – verschiedene Übersetzungen), Länge, Rechtschreibung,
unerwünschte Wörter und **eigene Regeln** mit regulären Ausdrücken (Quell-Muster optional, Ziel-Muster,
Schweregrad). Einstellungen unter **QA-Einstellungen und eigene Regeln**; Erweiterungen können weitere
Prüfungen beisteuern.

Rechtschreibung: Unter **Einstellungen → Rechtschreibprüfung** das Wörterbuch der Zielsprache laden
(freie Wörterbücher des LibreOffice-Projekts).

## Vorübersetzung und Analyse

- **Vorübersetzen (TM)** setzt Treffer ab der eingestellten Schwelle ein; Kontexttreffer auf Wunsch bestätigt.
- **Analyse** zählt Segmente, Wörter und Zeichen je Trefferstufe: Kontext, Wiederholungen, 100 %, 95–99 %,
  85–94 %, 75–84 %, 50–74 %, kein Treffer – die Grundlage für Angebote.
- **Leere per KI** (Editor) übersetzt alle leeren Segmente mit der KI; das Ergebnis ist ein Entwurf, der
  geprüft und bestätigt werden muss.

## Wissensdatenbanken

Neben TM und Terminologie gibt es **Wissen**: Sachwissen, Definitionen, Sprachgebrauch und Stilregeln, jeweils
mit Sprache, Thema, Quelle und Autor. Jedes Projekt hat ein Projekt-Wissen; weitere Datenbanken lassen sich
pro Projekt und pro Sprache anbinden – lokal oder auf dem Teamserver.

- Reiter **Wissen → Durchsuchen**: Einträge suchen, anlegen, ändern.
- Im Editor zeigt die Seitenleiste **Wissen** passende Einträge; sie gehen auch in jede KI-Übersetzung und
  Bewertung ein. Stilregeln der Zielsprache gelten immer.
- **Wissens-Agent**: Thema und Sprachen angeben – der Agent sucht verlässliche Quellen, liest sie und legt
  Einträge mit **wörtlichem Beleg** an. Steht der Beleg nicht wirklich auf der gelesenen Seite, wird der
  Eintrag verworfen.
- **Team-Aktivität**: wer im Team zuletzt was beigetragen hat.

## KI einrichten

**Einstellungen → KI-Zugänge**: Vorlage wählen (Claude, lokal mit llama.cpp/Ollama/LM Studio, OpenAI,
Mistral, OpenRouter, eigener Server …), Adresse und Modell anpassen, **Gewählten Zugang testen**, speichern.

- API-Schlüssel können als `env:NAME` eingetragen werden (z. B. `env:ANTHROPIC_API_KEY`); dann steht der
  Schlüssel nicht in der Einstellungsdatei.
- **Wer macht was**: für Übersetzen, Recherche und Bewertung jeweils einen eigenen Zugang wählen –
  z. B. lokal übersetzen, in der Cloud recherchieren.
- **Lokal oder Cloud**: Die Einstellungen zeigen bei jedem Zugang, ob die Texte den Rechner verlassen.
  Für vertrauliche Aufträge einen lokalen Zugang nutzen.
- **Websuche für die Agenten**: Claude-Websuche, Brave Search, Tavily oder eine eigene SearXNG-Instanz.
  Lokale Modelle können nicht selbst suchen; das übernimmt dieser Dienst.
- Für kleine lokale Modelle **Zeichen pro Webseite** auf etwa 2000–3000 senken.

## Agenten

Reiter **Agenten** (die meisten Funktionen gibt es auch direkt im Editor unter **KI**):

- **Begriffsvergleich** – für kontextabhängige Fachbegriffe, etwa im Recht. Der Assistent klärt die Bedeutung
  im angegebenen Satz, sucht auf den Fachseiten beider Sprachen nach Definitionen und vergleicht die Begriffe
  der beiden Systeme (z. B. Rechtsordnungen): volle, teilweise oder keine Entsprechung, Gemeinsamkeiten,
  Unterschiede, wann eine Lösung passt und wann nicht. Er empfiehlt eine Lösung mit Strategie (funktionale
  Entsprechung, wörtlich mit Erläuterung, Original mit Erklärung, Umschreibung) und begründet sie mit
  nummerierten, **überprüften Belegzitaten**. **Empfehlung → Terminologie** und **Begründung → Wissen**
  übernehmen das Ergebnis. Im Editor: Begriff im Quelltext markieren, Strg+Umschalt+B.
- **Themenrecherche** – sucht Fachtexte in jeder gewählten Sprache und stellt eine mehrsprachige Begriffsliste
  mit Belegstellen zusammen; gute Begriffe gehen mit einem Klick in die Projekt-Terminologie.
- **Usus-Prüfung** – ist ein Ausdruck üblich? Der Agent sucht die genaue Wortfolge und gleichbedeutende
  Alternativen, zählt wörtliche Belege und urteilt anhand der Belege.
- **Übersetzungen bewerten** – mehrere Varianten nach Richtigkeit, Sprachfluss, Terminologie und Stil benoten,
  mit Begründung und Verbesserungsvorschlag; fragliche Wendungen auf Wunsch im Netz prüfen.

Das **Protokoll der Agenten** zeigt jeden Schritt: Suchanfragen, gelesene Seiten, übersprungene Seiten.
KI-Ergebnisse sind Vorschläge – die fachliche Verantwortung bleibt bei Ihnen.

## Fachquellen-Profile

Unter **Einstellungen → Fachquellen je Thema** steht je Thema (Recht, Medizin, Technik, EU und Verwaltung –
frei erweiterbar) eine Zeile pro Sprache:

`Sprache | System | Domains, mit Komma getrennt`

Beispiel: `de-DE | deutsches Recht (BGB, HGB, ZPO, StGB …) | gesetze-im-internet.de, dejure.org`.
`*` gilt für alle Sprachen. Die Agenten suchen zuerst auf diesen Seiten und erst danach allgemein.
**Fachseiten prüfen** testet, ob die Seiten das Auslesen erlauben und Text liefern. Seiten, die automatisches
Auslesen untersagen oder technisch verhindern, gehören nicht in die Liste – das Programm umgeht solche Sperren nicht.

## Teamarbeit

Mehrere Personen arbeiten über einen eigenen **Teamserver** zusammen.

**Server einrichten** (Administrator):

```
SententiamConverte.Server --daten ./teamdaten
SententiamConverte.Server --daten ./teamdaten benutzer neu Anna bearbeiter
SententiamConverte.Server --daten ./teamdaten ressource neu tm Maschinenbau
```

Beim ersten Start entsteht der Benutzer `admin`; sein Zugangsschlüssel wird einmal angezeigt. Rollen: Admin,
Bearbeiter, Leser. Im Internet nur über HTTPS betreiben (z. B. hinter einem Reverse Proxy).

**Im Programm**: **Einstellungen → Teamserver** Name, Adresse und Zugangsschlüssel eintragen,
**Verbindung testen**. Danach:

- **Gemeinsame Ressourcen**: unter **TM & Terminologie → Ressource anbinden** ein TM, eine Terminologie oder
  eine Wissensdatenbank vom Server wählen, auf Wunsch nur für bestimmte Sprachen. **schreibt zurück**:
  bestätigte Übersetzungen und neue Begriffe landen sofort auch dort.
- **Gemeinsame Projekte**: Mehrere Personen bearbeiten **dieselben Dateien** gleichzeitig. Änderungen werden
  alle paar Sekunden abgeglichen. Ein Segment, an dem jemand arbeitet, ist für die anderen gesperrt
  (mit Namen angezeigt). Haben zwei Personen dasselbe Segment geändert, gilt die Fassung, die zuerst auf dem
  Server war; die eigene kommt in den **Verlauf** des Segments und geht nicht verloren.
  Anlegen mit **als gemeinsames Projekt auf dem Teamserver anlegen**, beitreten über
  **Gemeinsames Projekt (Team) …**.

Ist der Server nicht erreichbar, arbeitet ein Projekt mit seinen lokalen Daten weiter.

## Pakete und Austausch

- **.sdlppx** öffnen: legt ein Projekt mit allen Dateien, TMs und Terminologien des Pakets an.
- **.sdlrpx** erzeugen: Rückgabepaket mit den übersetzten zweisprachigen Dateien.
- **XLIFF** je Datei (Schaltfläche in der Dateiliste) für den Austausch mit anderen Programmen.
- **TMX, TBX, .sdltm** unter **TM & Terminologie**.

Fremde Formate werden nur zur Zusammenarbeit gelesen und geschrieben.

## Schnittstelle (API)

Alle Funktionen sind über eine REST-Schnittstelle erreichbar – für eigene Skripte, Automatisierung und andere
Programme. Es gibt sie an zwei Stellen:

- **In der Desktop-App**: **Einstellungen → Schnittstelle (API)** einschalten, Port wählen, Zugangsschlüssel
  erzeugen, **Übernehmen**. Sie ist nur auf diesem Rechner erreichbar (127.0.0.1) und arbeitet mit dem Projekt,
  das im Fenster offen ist; Änderungen erscheinen sofort im Editor.
- **Im Teamserver**: immer an, mit den Benutzern und Rollen des Servers.

Anmeldung mit der Kopfzeile `Authorization: Bearer <Schlüssel>`. Die maschinenlesbare Beschreibung (OpenAPI)
steht unter `/openapi/v2.json`. Alle Endpunkte und Beispiele: Kapitel „API-Referenz“.

## Erweiterungen

Im Reiter **Erweiterungen** installieren Sie Erweiterungen anderer Entwickler: Dateiformate, QA-Prüfungen und
Befehle.

1. **Erweiterung installieren …** und die Datei wählen (`.scext`, `.zip` oder `.dll`).
2. Die Erweiterung ist zunächst **ausgeschaltet**. **Einschalten …** fragt nach: Eine Erweiterung ist
   Programmcode und läuft mit Ihren Rechten – nur aus vertrauenswürdiger Quelle einschalten.
3. Unter **Befehle** erscheinen ihre Befehle. Sie wirken auf das, was im Editor offen bzw. gewählt ist;
   Argumente je Zeile als `name=wert`. **Ausführen** zeigt das Ergebnis darunter.

**Ausschalten** und **Entfernen** wirken vollständig nach einem Neustart. Wie man eigene Erweiterungen
schreibt, steht im Kapitel „Erweiterungen entwickeln“.

## Datenschutz und Rechtliches

- **KI in der Cloud**: Die Texte verlassen den Rechner. Vereinbarungen mit Kunden und die DSGVO beachten;
  für vertrauliche Aufträge einen lokalen Zugang verwenden.
- **Webseiten**: Das Programm nutzt nur offizielle Such-Schnittstellen, meldet sich beim Lesen von Seiten mit
  eigener Kennung, beachtet robots.txt und Nutzungsvorbehalte für Text und Data Mining (§ 44b UrhG) und
  übernimmt nur kurze Belegzitate.
- **Teamserver**: Die Daten liegen auf Ihrem eigenen Server; gespeichert werden nur Prüfsummen der
  Zugangsschlüssel. Beiträge werden mit Namen protokolliert – das sollten alle Beteiligten wissen.
- **Fremde Formate** werden nur über ihre Dateiendung genannt und ausschließlich zur Interoperabilität verarbeitet.

Diese Hinweise sind keine Rechtsberatung.

## Dateien und Ordner

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

Im Datenordner des Programms: `ai-settings.json` (KI), `ui-settings.json` (Oberfläche, lokale API),
`servers.json` (Teamserver), `woerterbuecher/`, `erweiterungen/`.
