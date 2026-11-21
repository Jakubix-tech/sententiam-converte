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

- **Windows**: `SententiamConverte-<Version>-win-x64.msi` doppelklicken, Lizenz bestätigen, **Installieren**. Das
  Programm wird nur für den angemeldeten Benutzer eingerichtet (keine Administratorrechte nötig) – unter
  `%LOCALAPPDATA%\Programs\Sententiam converte`, mit Einträgen im Startmenü und auf dem Desktop; am Ende startet es auf
  Wunsch gleich. Eine neuere Version wird einfach darüber installiert, entfernt wird es unter **Einstellungen → Apps**.
  Wer nichts installieren möchte, nimmt die ZIP-Fassung: entpacken und `SententiamConverte.Desktop.exe` starten.
  Windows SmartScreen kann warnen, weil das Paket nicht signiert ist: **Weitere Informationen → Trotzdem ausführen**.
- **Linux**: Archiv entpacken, `SententiamConverte.Desktop` starten (ggf. ausführbar machen).
- **macOS**: `Sententiam converte.app` in den Programme-Ordner ziehen. Beim ersten Start mit Rechtsklick →
  „Öffnen“ bestätigen, solange das Programm nicht von Apple beglaubigt ist.
- **Android**: APK installieren (Installation aus unbekannten Quellen erlauben).

Einstellungen, Wörterbücher und Erweiterungen liegen unter `%LOCALAPPDATA%\SententiamConverte` (Windows) bzw.
`~/.local/share/SententiamConverte` (Linux/macOS). Mit der Umgebungsvariablen `SENTENTIAM_HOME` lässt sich ein
anderer Ort wählen, etwa für eine portable Installation auf einem USB-Stick.

Die Oberfläche gibt es auf Deutsch, Englisch und Polnisch (**Einstellungen → Sprache der Oberfläche**, Standard:
Systemsprache; wirksam nach einem Neustart). Bei polnischer Oberfläche erscheinen Handbuch und erzeugte Dokumente
(Angebot, Prüfdatei, Bewertungsbericht) auf Englisch.

Vor der ersten KI-Nutzung auf der Seite **KI einrichten** einen KI-Zugang einrichten (siehe „KI einrichten“) – oder im
Editor bei **Jetzt herunterladen** die kostenlose eingebaute KI laden.
Alles andere – TM, Terminologie, QA, Dateiformate – funktioniert ohne KI.

### Erste Schritte

Beim ersten Start zeigt der Reiter **Projekt** oben die **Ersten Schritte** – vier Schritte, jeder mit Häkchen, sobald
er erledigt ist:

1. **KI wählen** – **Kostenlose Online-KI einrichten (ein Klick)** sucht die kostenlosen Online-Modelle, prüft sie und
   richtet sie als Verbund für alle Aufgaben ein. **Andere KI wählen** führt zur Seite **KI einrichten** (eingebaute KI
   offline, eigener Schlüssel …).
2. **Projekt anlegen** – **Dateien wählen …** (der Name wird aus der ersten Datei vorgeschlagen), Ausgangs- und
   Zielsprachen, Fachgebiet, **Vertraulich** (Datenschutz gleich einschalten) und **Gemeinsame TM, Terminologie und
   Wissensdatenbank anlegen** (empfohlen). **Projekt anlegen** erledigt alles in einem Schritt.
3. **Alte Übersetzungen nutzen** (freiwillig) – siehe „Alte Übersetzungen übernehmen“.
4. **Übersetzen** – öffnet die erste unfertige Datei im Editor, mit den wichtigsten Handgriffen in Kürze.

**Ausblenden** blendet die Anleitung aus; der Knopf **Erste Schritte** oben im Reiter Projekt holt sie zurück. Ist im
Editor noch keine Datei offen, sagt er, wie es weitergeht.

## Projekt anlegen

Reiter **Projekt** → **Neues Projekt**:

- **Name** – wird auch der Ordnername.
- **Ausgangssprache** und **Zielsprachen** aus der Sprachauswahl: Namen oder Code eintippen („Polnisch“, „polski“ oder
  „pl“) und aus der Liste wählen – angezeigt als „Polnisch · pl-PL“. Zielsprachen erscheinen als Marken, **✕** entfernt
  eine. Rund 80 Sprachen und Varianten stehen in der Liste; andere gehen als Code (z. B. `eu`). Dieselbe Auswahl gibt es
  überall, wo Sprachen gewählt werden (Erste Schritte, alte Übersetzungen, Alignment, Sprachdaten, Ressourcen).
- **Fachgebiet / Thema** – hilft dem TM bei der Kontextbewertung und den KI-Agenten bei der Recherche.
- **als gemeinsames Projekt auf dem Teamserver anlegen** – siehe „Teamarbeit“.

Danach **Dateien hinzufügen …**. Jede Datei erscheint je Zielsprache mit Fortschritt in der Liste.
Weitere Möglichkeiten:

- **Projektordner öffnen …** – ein vorhandenes Projekt öffnen; unter „Zuletzt geöffnet“ stehen die letzten.
- **Projekt wechseln** – links in der Statusleiste steht das offene Projekt. Ein Klick darauf zeigt die zuletzt
  geöffneten Projekte mit Namen und Sprachen (das offene mit ✓), ein Klick auf eines schaltet um – von jedem Reiter aus.
  Im Reiter Projekt stehen die anderen Projekte zusätzlich unter **Andere Projekte** (aufklappen); ✕ nimmt eines aus
  der Liste, der Projektordner bleibt, wie er ist. Wer im Editor umschaltet, landet in der Datei und am Segment, an dem
  er in diesem Projekt zuletzt stand; aus einem anderen Reiter geöffnet, geht es dort weiter, sobald man in den Editor
  wechselt. Arbeitet die KI im Editor noch (Übersetzung, Prüfung, Recherche), hält sie vor dem Wechsel an – was fertig
  ist, bleibt gespeichert; nichts davon landet im neuen Projekt.
- **Alte Übersetzungen übernehmen …** – Original und Übersetzung früherer Aufträge ins TM und in die Terminologie
  übernehmen (siehe „Translation Memory und Kontext“).
- **Projektpaket öffnen (.sdlppx) …** – Paket eines Auftraggebers übernehmen; zurück geht es mit
  **Rückgabepaket (.sdlrpx) …**.
- **Fachquellen für die Agenten** – welches Fachquellen-Profil (Recht, Medizin, Technik …) für dieses Projekt gilt.
- **Vorübersetzen ab (%)** – Mindesttreffer, ab dem vorübersetzt wird.
- **Kontexttreffer (101 %) beim Vorübersetzen bestätigen** – solche Segmente gelten dann als fertig.

### Projektvorlagen

Einstellungen eines Projekts lassen sich als **Vorlage für neue Projekte** speichern (Reiter **Projekt**, Name eingeben,
**Speichern**): Sprachen, Fachgebiet, Fachquellen-Profil, QA-Einstellungen, Datenschutz, Preise, Fehlerbewertung und die
angebundenen gemeinsamen Datenbanken. Beim nächsten **Neuen Projekt** unter **Vorlage** wählen – Sprachen und Fachgebiet
werden vorgeschlagen, alles andere beim Anlegen übernommen. Praktisch je Kunde.

## Dateiformate

| Format | Hinweise |
|---|---|
| DOCX, XLSX, PPTX | Formatierungen werden zu Tags, Bilder, Felder und Kommentare bleiben erhalten; Textfelder und Inhaltssteuerelemente werden mit übersetzt, Seitenzahl- und Datumsfelder bleiben unverändert |
| ODT, ODS, ODP (OpenDocument) | wie oben, auch Textrahmen, Kopf- und Fußzeilen; Leerzeichen-/Tabulator-Elemente bleiben erhalten |
| IDML (InDesign) | Textabschnitte (Stories) mit Zeichenformaten und Hyperlinks |
| XLIFF 1.2 / 2.x, .sdlxliff | zweisprachige Austauschdateien, Segmentstatus wird übernommen |
| PDF | Ausgabe wieder als PDF im Layout des Originals (siehe unten); gescannte PDFs mit **Gescanntes PDF erkennen (KI) …** |
| HTML, Markdown | Auszeichnungen werden zu Tags |
| DITA (.dita, .ditamap) | Topics und Maps: Titel, Absätze, Schritte, Tabellen, Navigationstitel; Auszeichnungen im Satz werden zu Tags, Code und `translate="no"` bleiben stehen. Die Zieldatei behält ihren Namen, damit die Verweise stimmen – je Sprache in einen eigenen Ordner speichern |
| RESX, JSON, PO | Software-Texte; Platzhalter wie `{0}`, `%s`, `${name}` sind geschützt |
| SRT, VTT | Untertitel, Zeitangaben bleiben unverändert |
| TXT | jede Zeile ein Absatz |

Word- und OpenDocument-Dateien werden beim Export in die Originaldatei zurückgeschrieben: Seitenlayout, Formatvorlagen,
Bilder, Rahmen, Tabellen, Kopf- und Fußzeilen bleiben, wie sie sind – nur der Text wird ersetzt und fließt bei
längerer Übersetzung wie in Word bzw. Writer weiter.

TMX, TBX und .sdltm werden unter **TM & Terminologie** importiert. Weitere Formate können Erweiterungen
hinzufügen (die Beispiel-Erweiterung bringt z. B. CSV mit).

**PDF-Dateien** werden wieder als PDF ausgegeben, im Layout des Originals: Bilder, Linien, Farbflächen und das
Seitenformat bleiben unverändert, jeder Absatz steht an seiner Stelle – in Größe, Farbe und Ausrichtung des
Originals, mit Aufzählungszeichen und hängendem Einzug. Fett und kursiv gesetzte Wörter erscheinen im Editor als
Tags und bleiben so in der Übersetzung erhalten; weite Abstände (z. B. „Berlin  |  Telefon“) stehen als mehrere
Leerzeichen im Text. Gesetzt wird mit Noto Sans bzw. Noto Serif, je nachdem, ob das Original eine Schrift mit oder
ohne Serifen verwendet. Weil diese Schriften oft etwas breiter laufen als die Originalschrift (z. B. Arial oder
Calibri), misst das Programm, wie breit das Original seine Zeilen setzt, und gleicht die Schriftgröße daran an – die
Übersetzung wirkt dann so groß wie das Original und bricht ähnlich um; die Zeilenabstände bleiben. Braucht die Übersetzung mehr Zeilen, rückt der Text darunter nach – bis zur nächsten Linie,
Grafik oder Fußzeile; reicht der Platz nicht, wird die Schrift des Absatzes etwas kleiner. Das Ergebnis vor dem
Weitergeben kurz durchsehen.

## Der Editor

Doppelklick auf eine Datei (oder **Öffnen**) lädt sie in den Editor. Links steht die Quelle, rechts das Ziel –
als Tabelle mit Statusfarbe am Rand. **Untereinander** in der Werkzeugleiste zeigt Quelle und Ziel interlinear:
die Quelle oben, das Ziel darunter, beide über die volle Breite – die Quelle bleibt auswählbar (zum Kopieren), das
Ziel bearbeitbar. Ein zweiter Klick schaltet zurück; die Wahl bleibt gespeichert. Bei schmalem Fenster bricht die
Werkzeugleiste in eine zweite Zeile um.

Der Editor arbeitet wie eine Textverarbeitung:

- **Formatierung sichtbar**: Text, der im Original fett, kursiv, unterstrichen, hoch- oder tiefgestellt ist,
  erscheint auch so – in Quelle und Ziel. Die Tags (⟨1⟩ … ⟨/1⟩) bleiben als kleine farbige Marken sichtbar.
- **Strg+B / Strg+I / Strg+U** (oder die Knöpfe **B**, **I**, **U**) setzen die passende Formatierung aus der
  Quelle um die Markierung bzw. um das, was man danach tippt.
- **Rechtschreibung beim Tippen**: Unbekannte Wörter werden rot gewellt unterstrichen; Rechtsklick zeigt
  Vorschläge und „Ins Wörterbuch aufnehmen“. Fehlt das Wörterbuch der Zielsprache, bietet der Editor es oben mit
  einem Klick an (freie Wörterbücher des LibreOffice-Projekts). Das Symbol mit dem Häkchen schaltet die Prüfung ab.
- **Rückgängig/Wiederholen** (Strg+Z / Strg+Y), **Größer/Kleiner** (Lupe) für die Schrift.
- Fehlende Tags zeigt das Programm unter dem Segment an; **Strg+Umschalt+G** setzt den nächsten fehlenden an
  der Cursorposition ein.
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
- **Konkordanz** – freie Suche im TM, in Quelle oder Ziel. **Wie oft?** zählt, in wie vielen Sätzen der eigenen Daten
  (Projekt-TM, gemeinsame TMs, geladene freie Sprachdaten) die Wendung genau so vorkommt, je Datenbank; mehrere
  Varianten mit „|“ trennen und vergleichen („Benutzer | Nutzer“). Für deutsche Einzelwörter steht dazu die
  Häufigkeitsklasse im DWDS. **Im Netz je Land …** prüft die Wendung unter „Hilfsmittel → Verbreitung im Netz“ je Land.
- **Meldungen** (früher „QA“) – alles, was zu einem Segment vorliegt, an einer Stelle: QA (mit Grammatik und
  KI-Prüfung), Fehler aus Lektorat und KI-Lektorat, die intelligente Bewertung, die Recherche zu den Ausdrücken des
  Satzes (Benennung und Gütestufe), geschützte Angaben und die Meldungen der KI aus Übersetzung, Recherche,
  Netzprüfung und Rückmeldung. Fehler stehen oben, dann Warnungen, dann Hinweise; im Reiterkopf steht die Anzahl. Lange
  Meldungen stehen gekürzt da, ganz beim Darüberfahren mit der Maus.
  **Nur gewähltes Segment** ist eingeschaltet: Ein Klick auf ein Segment zeigt dessen Meldungen. Ausgeschaltet stehen
  alle Meldungen des Dokuments nach Segment geordnet da, und ein Klick auf eine Meldung springt zum Segment. Darüber
  die Knöpfe der QA und die Rechtschreibung des Segments. Die Meldungen der KI und die KI-Prüfungen stehen gespeichert
  beim Dokument (`meldungen/`), die Recherche in ihren Dossiers – nach dem Schließen und erneuten Öffnen ist alles
  wieder da.
- **Zeichen am Segment** zeigen beim Darüberfahren mit der Maus ihre Meldungen: ⚠ die QA-Meldungen („Zahlen: 12 fehlt
  im Ziel“), ✗ die erfassten Fehler (Kategorie, Schwere, Anmerkung), ⓘ die Meldungen der KI (die neuesten zuerst),
  ● die Bewertung, 🔒 die geschützten Angaben.
- **Wissen** – passende Einträge aus den Wissensdatenbanken.
- **Notiz** – Kommentar zum Segment und **Verlauf** aller Änderungen mit Wortvergleich.
- **KI** – Übersetzen, Varianten bewerten, im Netz prüfen, Begriff im Kontext klären.
  **Im Netz prüfen** (Strg+Umschalt+U): Die KI wählt die Wendungen der Übersetzung, an denen sich Richtigkeit und
  Natürlichkeit zeigen (Fachbegriffe, Kollokationen, feste Wendungen), und nennt Alternativen. Jede Variante wird in
  Texten der Zielsprache gezählt – Artikel der Wikipedia mit der genauen Wortfolge, mit einem Suchdienst zusätzlich
  Treffer im Netz –, dann urteilt die KI im Zusammenhang (✔ passt, ⚠ fraglich, ✖ falsch), nennt die Zahlen, die
  Belege mit Adresse und schlägt, wenn es sich lohnt, eine verbesserte Fassung vor (**übernehmen** setzt sie als
  Entwurf ein). Die Häufigkeit ist ein Hinweis, kein Beweis. Namen, Zahlen und Platzhalter werden nie gesucht. Mit
  einer Markierung im Zieltext wird nur dieser Ausdruck geprüft.
  Ergebnisse und Vorschläge der KI gehören immer zu dem Segment, für das sie erstellt wurden: Beim Wechsel zu einem
  anderen Segment verschwinden sie, beim Zurückwechseln sind sie wieder da – „übernehmen“ wirkt nie im falschen Segment.

Beim Tippen schlägt **AutoSuggest** Wörter und Wendungen aus TM und Terminologie vor.

### Lektorat: Änderungen verfolgen und Fehler bewerten

Im Seitenreiter **Lektorat**:

- **Änderungen an bestätigten Segmenten verfolgen** – ändert der Prüfer eine bestätigte Übersetzung, bleibt die vorige
  Fassung erhalten. Unter dem Zielfeld erscheint die Änderung wortweise (rot gelöscht, grün eingefügt). **Annehmen** /
  **Ablehnen** gilt für das gewählte Segment, **Alle annehmen** / **Alle ablehnen** für die Datei; der Filter **Vom Prüfer
  geändert** zeigt nur diese Segmente. Änderungen aus einer Prüfdatei (Word) werden immer so verfolgt.
- **Fehlerbewertung** – Fehler im gewählten Segment mit Kategorie (Genauigkeit, Sprache, Terminologie, Stil,
  Lokalisierung, Formatierung – je mit Unterart) und Schwere (leicht 1, schwer 5, kritisch 25 Strafpunkte, neutral 0)
  erfassen. Die Punktzahl ist 100 − Strafpunkte × 100 / Wörter; **Bestanden ab** legt die Grenze fest (Vorgabe 98), ein
  kritischer Fehler lässt immer durchfallen. **Bewertungsbericht (Word)** gibt Ergebnis, Kategorien und jeden Fehler mit
  Satz und Anmerkung aus. Der Filter **Mit Fehlerbewertung** zeigt die betroffenen Segmente.
- **KI bewertet die Datei** – die KI geht alle übersetzten Segmente durch und schlägt Fehler im selben Schema vor
  (Kategorie, Schwere, Stelle, Verbesserung, Begründung; angelehnt an MQM), gemessen auch am Steckbrief. Die Vorschläge
  stehen als **KI-Vorschlag** in der Liste des Segments und zählen zur Punktzahl; einzelne mit ✕ entfernen oder alle mit
  **KI-Vorschläge entfernen**. Ein erneuter Lauf ersetzt die früheren KI-Vorschläge, Fehler menschlicher Prüfer bleiben.
  So lässt sich auch eine fremde, fertige Übersetzung schnell einschätzen. Die Begründung schreibt die KI in der Sprache,
  die unter **KI einrichten** für Erklärungen eingestellt ist.
- **Bewertungsbericht im Programm** – im Reiter **Lektorat** klappt **Bewertungsbericht – alle Fehler** die Strafpunkte je
  Kategorie und jeden Fehler aller Segmente auf: Segment, Kategorie, Schwere, Prüfer, Ausgangstext → Übersetzung und die
  Anmerkung (bei der KI: „falsche Stelle“ → „bessere Fassung“: Begründung). **Zum Segment** springt hin;
  **Vorschlag übernehmen** ersetzt die bemängelte Stelle durch die bessere Fassung (als Entwurf), der Fehler fällt dann
  aus der Bewertung. Steht die Stelle so nicht (mehr) in der Übersetzung – etwa bei einer Auslassung –, fehlt der Knopf:
  dann von Hand verbessern. **Bericht groß anzeigen** (auch **Bericht ansehen** im Reiter **KI**) öffnet denselben Bericht
  groß über dem Editor, dazu den Ablauf des letzten automatischen Laufs, die Korrekturen, die Ausdrücke für eine
  gründlichere Recherche und das Protokoll – jeder Teil aufklappbar.

In gemeinsamen Projekten gleicht der Teamserver beides mit ab: Der Übersetzer sieht die Änderungen des Prüfers und die
erfassten Fehler beim nächsten Abgleich und nimmt sie dort an oder lehnt sie ab. Ältere Programmversionen ändern daran
nichts – sie lassen den Lektoratsstand auf dem Server unverändert.

### Diktat

**🎤 Diktat** (Strg+Umschalt+D) nimmt über das Mikrofon auf; ein zweiter Klick beendet die Aufnahme, der erkannte Text
erscheint an der Schreibmarke des Zielfelds. Die Spracherkennung wird unter **KI einrichten → Diktat** gewählt. Unter
Windows geht auch die eingebaute Spracheingabe (Windows+H) in jedem Textfeld; unter Linux braucht das Diktat `arecord`,
unter macOS `sox`.

### Höchstlänge

Hat ein Segment eine Höchstlänge (aus der Datei, z. B. XLIFF `maxwidth`, oder für alle Segmente in den QA-Einstellungen),
steht neben der Nummer ein Zähler „23/40“, rot, sobald die Übersetzung zu lang ist; die QA meldet es als Fehler.

### Recherche zum Dokument

Im Seitenreiter **Recherche** bereitet die KI die Übersetzung vor. **Von** und **Nach** sind Sprache *mit Land* (vorbelegt
aus dem Projekt, z. B. es-ES → es-MX); Spanisch in Spanien ist nicht Spanisch in Mexiko, und ein Gesetz oder eine Behörde
aus Land A hat in Land B ein anderes Gegenstück.

- **Dokument durchsehen** – die KI liest das **ganze Dokument** in Abschnitten von etwa 4000 Zeichen (im Verbund
  mehrere gleichzeitig) und nennt je Abschnitt alle Ausdrücke, die Recherche brauchen: Fachbegriffe, Rechtsbegriffe
  und Einrichtungen, Realien (Tiere, Pflanzen, Speisen, Bräuche …), Redewendungen, Abkürzungen – mit Grund und erster
  Einschätzung, wie schwierig sie sind. Zusätzlich findet das Programm selbst **Abkürzungen** („GLRD“, „DIN“),
  **Paragraphenverweise** („§ 242 StGB“, „Art. 6 DSGVO“) und **EU-Rechtsakte** („Verordnung (EU) 2016/679“). Es zählt
  nur, was wörtlich im Dokument steht; nennen mehrere Abschnitte denselben Ausdruck, gilt die höhere Schwierigkeit.
  Mittelschwere und schwere Ausdrücke sind angehakt, leichte nicht – so bleibt die Liste vollständig, ohne dass alles
  recherchiert werden muss.
- **Recherche läuft weiter**: Recherchen stehen in einer **Warteschlange** und laufen nacheinander im Hintergrund – auch
  wenn du den Reiter (TM, Termini, QA …) oder das Segment wechselst. Jedes Dossier erscheint, sobald es fertig ist;
  der Reiter zeigt „Recherche ⏳“ (ebenso „KI ⏳“), solange dort gearbeitet wird. Neue Aufträge kommen dazu, ohne den
  laufenden abzubrechen; nur **Abbrechen** hält an. Lange Arbeiten wie KI-Bewertung, Grammatik- oder KI-Prüfung holen
  dich am Ende nicht mehr in ihren Reiter zurück, wenn du inzwischen woanders bist – die Statusleiste meldet das
  Ergebnis.
- **Dieses Segment**: Ist der Recherche-Reiter offen, zeigt er oben die Ausdrücke des gewählten Segments mit
  **Bedeutung** und **Entsprechung in der Zielsprache** („Gleitringdichtung → mechanical seal“). Was noch kein Dossier
  hat, steht klar als „noch nicht recherchiert“ da („Zu „GLRD“ habe ich noch nichts – ich recherchiere jetzt.“) und
  kommt mit **Ausdrücke des gewählten Segments gleich recherchieren** (vorgegeben) vorn in die Warteschlange; ist das
  Dokument noch nicht durchgesehen, liest die KI dafür nur dieses Segment.
- **Markierung erklären**: Markieren Sie bei offenem Recherche-Reiter Text im Ausgangstext oder in der Übersetzung,
  erklärt die KI ihn darüber: **Bedeutung im Satz**, **Grammatik** (Wortart, Form, Satzglied, Konstruktion, Valenz),
  **Sprachwissenschaftlich** (Register, Kollokation oder feste Wendung, Wortbildung, Mehrdeutigkeit, falsche Freunde),
  die entsprechende Stelle **in der Übersetzung** bzw. **im Ausgangstext** (nur, wenn sie dort wörtlich steht), eine
  **Einschätzung**, ob sie trifft, **Alternativen** und was **aus der Recherche** zu diesen Wörtern schon vorliegt
  (belegt oder nicht, mit Quelle). Das ist eine Einschätzung der KI ohne eigene Quellen; **Mit Belegen recherchieren**
  recherchiert die Markierung (bei einer Markierung in der Übersetzung: die Stelle im Ausgangstext). Die Erklärung
  bleibt stehen, wenn Sie woanders hinklicken; heben Sie die Markierung im Feld auf oder wählen ein anderes Segment,
  gilt wieder die gewohnte Ansicht. Dieselbe Markierung wird nicht zweimal gefragt.
- **Angehakte recherchieren** – zu jedem Ausdruck ein **Dossier** über den Begriffsvergleich (Fachseiten beider Länder,
  wörtliche Belege): was er in Ausgangssprache und -land bedeutet, welche Entsprechungen es in Zielsprache und -land gibt
  (voll, teilweise, keine), wann und warum welche Variante passt, Hintergrund, Empfehlung mit Strategie (Entsprechung,
  Umschreibung, Original mit Erklärung), Warnungen, Quellen – und **wie schwierig** er ist (nach der Recherche neu
  eingeschätzt: ohne Entsprechung im Zielland schwierig).
- **Eigene Begriffe** – im Feld darunter eintippen (mehrere mit `;` trennen) und **Hinzufügen** oder Eingabe: Sie kommen
  angehakt oben in die Liste, mit dem Hinweis, wie oft sie im Dokument stehen – auch Begriffe, die die KI nicht
  vorgeschlagen hat oder die gar nicht im Dokument vorkommen. Beim erneuten Durchsehen bleiben sie stehen.
- **Begriffsfeld mit Beratung**: Zu Fachbegriffen, Rechtsbegriffen und Realien klärt die Recherche nicht nur den
  einen Ausdruck, sondern seine **Begriffsfamilie in beiden Sprachen** – beim „Spitzdach“ etwa den Oberbegriff
  Dachform/roof shape und die Geschwister Satteldach, Walmdach, Zeltdach, Pultdach … mit ihren unterscheidenden
  Merkmalen und Entsprechungen. Die Entsprechungen werden belegt: über die Sprachverknüpfung der Wikipedia, Seiten in
  beiden Sprachen und die eigenen Fachtexte; steht eine andere Benennung in den Quellen häufiger, wird sie genommen.
  Im Dossier stehen dann der Oberbegriff, alle Begriffe (der im Satz gemeinte mit ▶), **Woran man unterscheidet**
  (Prüffragen wie „Wie viele Dachflächen? Gibt es Giebel?“), **Für diesen Satz** (was gemeint ist und welche
  Benennung deshalb passt – ein umgangssprachliches „Spitzdach“ kann ein Zeltdach sein) und, wenn der Kontext nicht
  reicht, eine **Rückfrage an den Auftraggeber**. Die Entsprechung des gemeinten Begriffs geht als Kandidat in den
  Begriffsvergleich. Redewendungen und Abkürzungen haben kein Begriffsfeld.
- **Woher die Belege kommen**: zuerst aus den **eigenen Fachtexten** (siehe dort), dann über die **Sprachverknüpfung
  der Wikipedia** – der Artikel „Gleitringdichtung“ verweist z. B. auf „Mechanical seal“; das Gegenstück wird mit seinem
  ersten Satz als Beleg und als Kandidat genommen –, dann über die Websuche. Mit der Vorgabe **Wikipedia** als Suche
  sind die Dossiers dünn: Im Test mit 16 Sätzen empfahl die Recherche mit Gemma 4 E4B bei Fachbegriffen öfter eine
  wörtliche Fehlübersetzung („sliding ring seal“), und die Übersetzungen wurden damit insgesamt schlechter (E4B 76,9 →
  75,0 chrF, EuroLLM 74,7 → 73,1). Deshalb bekommt die KI-Übersetzung nur Dossiers, deren Empfehlung wörtlich in einer
  Quelle der Zielsprache steht; damit blieb der Schaden aus (E4B 76,6, EuroLLM 74,9). Jedes Dossier zeigt, ob das
  zutrifft; eine unbelegte Empfehlung, die stimmt, übernimmst du mit **→ Terminologie**. Einen echten Gewinn bringt
  Recherche erst mit besseren Quellen: Läuft auf dem Rechner die eigene Websuche (SearXNG), stellt das Programm
  beim Start selbst darauf um, wenn bisher nur Wikipedia gesucht wurde (Wikipedia bleibt als zweite Quelle; wer
  SearXNG ausgeschaltet hat, behält das). Sonst eine Websuche einrichten (**KI einrichten → Websuche**: kostenlos **Tavily**
  mit 1000 Suchen im Monat ohne Kreditkarte; **Brave** mit 5 $ Guthaben im Monat, etwa 1000 Suchen, Kreditkarte bei
  der Anmeldung nötig; eine eigene SearXNG-Instanz oder die Websuche über Claude) und eigene Fachtexte angeben. Die
  Websuche von Google steht Programmen nicht offen (für neue Kunden geschlossen, Ende 1.1.2027), und Google
  automatisiert abzufragen ist nicht erlaubt. Auch die Google-Suche der Gemini-Schnittstelle („Grounding“) ist nicht
  eingebunden: Nach Googles Bedingungen dürfen ihre Links nicht dazu dienen, Seiten zum Lesen zu finden, und die
  Ergebnisse dürfen nicht gespeichert oder ausgewertet werden – genau das tut die Recherche. Personennamen („Frau
  Kowalska“) und Einträge der Schutzliste werden nicht recherchiert.
- **Weitersuchen wie ein Mensch**: Steht nach der ersten Suche kein Kandidat in **zwei unabhängigen Quellen** der
  Zielsprache (je Website bzw. eigene Datei eine), sucht die Recherche gründlich anders weiter – bis zu sechs Runden.
  Fallen der KI keine neuen Wege mehr ein, sucht das Programm mit einfachen Anfragen weiter (jeder Kandidat allein, mit
  Fachgebiet und mit „Glossar“, der Ausgangsbegriff mit dem Namen der Zielsprache), bis alles Naheliegende versucht ist.
  Die KI bekommt, was schon gesucht und gefunden ist, und wählt neue Wege: den Begriff beschreiben, zweisprachige Glossare
  und Wörterbücher, Herstellerunterlagen, Normen, Behörden und Fachverbände des Ziellands, Synonyme, Ober- und
  Unterbegriffe; nichts wird doppelt gesucht. Das Programm sucht jeden Kandidaten selbst auf den gelesenen Seiten
  (kleine Modelle übersehen ihn oft). Nennt eine Seite eine **Sprachversion** – etwa die englische Fassung einer
  deutschen Herstellerseite –, liest es auch diese und schaut, welchen Begriff sie an derselben Stelle verwendet. Im
  Dossier steht, wie viele Runden und Seiten es brauchte und ob der Beleg eindeutig ist; was lange gesucht werden
  musste, gilt als schwieriger. Reichen schon die eigenen Fachtexte, bleibt die Websuche aus.
- **Stand der Quellen**: Jede Quelle im Dossier zeigt, wann sie gelesen wurde („abgerufen am …“) bzw. welchen Stand das
  Dokument nennt – Seiten im Netz und Gesetze ändern sich; ein älteres Dossier lässt sich **neu recherchieren**.
- **Klicken** auf ein Segment zeigt die Dossiers zu den Ausdrücken darin; **Markierung / Segment** bzw.
  **Strg+Umschalt+R** zeigt das Dossier zum markierten Wort oder Satz oder recherchiert ihn, wenn es noch keines gibt.
- **Belege**: Jede Quelle steht nummeriert mit Sprache, Seite, Adresse und dem **wörtlichen Zitat**; die Entsprechungen
  verweisen mit [1], [2] … darauf. Ein Klick öffnet die Seite und springt direkt zur zitierten Stelle (der Browser hebt
  sie hervor). Ohne Belege steht ein Warnhinweis: Dann stammen die Angaben nur aus dem Wissen des Modells.
- **Herkunft**: unter jedem Dossier, mit welcher KI es erstellt wurde (im kostenlosen Verbund die Modelle, die geantwortet
  haben), mit welcher Suche und wann. Oben im Reiter steht, womit die nächste Recherche läuft. Mit der Vorgabe sucht die
  KI nur in Wikipedia; für Fachseiten unter **KI einrichten → Websuche für die Agenten** einen Suchdienst einrichten.
- Je Dossier: **Einsetzen** (vorgeschlagene Formulierung an der Schreibmarke), **→ Terminologie**, **→ Wissen**,
  **Verwendungsbeispiele** (die Usus-Prüfung für die empfohlene Benennung gegen die anderen Kandidaten: echte Sätze
  aus der Zielsprache mit Link zur Stelle und die klare Aussage, was üblich ist und was nicht), **Neu recherchieren** (vielleicht findet die KI jetzt bessere Belege – die bisherige Fassung bleibt erhalten und
  kommt mit **Frühere Fassung** zurück), ✕. Ein Klick auf die Kopfzeile klappt ein Dossier zu oder auf; in der Liste
  aller Dossiers sind sie zugeklappt. Die Dossiers liegen im Projekt (`recherche.json`) und gelten für alle Dateien.
- **Gespeichert, was gefunden wurde**: Welche Ausdrücke die KI in einem Satz fand und wie sie ihn bewertete, steht
  ebenfalls im Projekt (`recherche-ausdruecke.json`, je Ausgangssatz). Wer ein Dokument schließt und wieder öffnet oder
  denselben Satz an anderer Stelle oder in einem anderen Dokument des Projekts hat, bekommt Ausdrücke, Dossiers und
  Bewertung sofort – die KI liest den Satz nicht noch einmal, und nichts wird neu recherchiert. Ein geänderter Satz ist
  ein neuer Satz. Erfasst die KI einen Ausdruck in einem anderen Satz etwas anders, gilt er als derselbe, wenn nur
  Artikel, Groß-/Kleinschreibung, Satzzeichen oder die Beugung abweichen („der Spaltring“, „Spaltringe“ = „Spaltring“;
  „uszczelnienia mechanicznego“ = „uszczelnienie mechaniczne“). Kurze Wörter müssen gleich sein („Druck“ ist nicht
  „Drucker“). Das gilt auch für das Recherche-Gedächtnis.
- **Quellengüte und Gütestufen**: Jede gelesene Seite wird nach ihrer Art eingeordnet – an ihr selbst, nicht an einer
  Liste bekannter Seiten, damit das in jedem Fachgebiet funktioniert, auch bei nie gesehenen Seiten: sicher Erkennbares an
  der Adresse (Foren, Wikipedia, Gesetzesportale, Normungsstellen, amtliche Adressen wie .gv.at, .admin.ch, bund.de,
  europa.eu, Hochschulen, Wörterbücher), sonst ordnet die KI die Seite beim Lesen ein. Die Rangfolge der Arten ist fast
  überall dieselbe, daraus ergibt sich das Gewicht eines Belegs: Termbank oder Glossar, Gesetz, Norm, Behörde 1,1 –
  eigene Fachtexte 1,0 – Fachverband, Hersteller, Firmen-Webseite, Hochschule, Wörterbuch 0,85 – Lexikon, Presse,
  sonstige Seite 0,7 –
  Blog 0,45 – Forum 0,35. **Eindeutig belegt** ist ein Kandidat ab zwei unabhängigen Websites mit zusammen mindestens
  1,4: zwei gewöhnliche Seiten reichen wie bisher; ein **Forum** zählt, allein reicht es aber nicht – mit einer Behörde,
  Norm oder Termbank zusammen schon, und vier unabhängige Foren, die übereinstimmen, auch (das ist echter Sprachgebrauch).
  Für die eigenen Namen, Produkte und Begriffe einer Firma oder Einrichtung ist **ihre eigene Seite** die stärkste
  Quelle und zählt wie eine Behörde: Ist der gesuchte Name ein ganzer Teil der Adresse („Raindancer“ → raindancer.com,
  ab-raindancer.de), wiegt die Seite dafür 1,1 – ein Wort, das nur in einem längeren Teil steckt („Pumpe“ in
  pumpen-shop.de), genügt nicht. Nennt nur ein Forum eine Benennung, sucht die nächste Runde gezielt nach Bestätigung in Termbanken, Normen, bei
  Behörden, Fachverbänden oder Herstellern. Jedes Dossier trägt eine **Gütestufe**: **A** eindeutig belegt mit einer
  starken Quelle, **B** eindeutig belegt, **C** belegt, aber nicht eindeutig (eine Quelle oder nur schwache), **D** nicht
  belegt – am Dossier, in „Dieses Segment“ und in der Erklärung einer Markierung. Ältere Dossiers werden an der Adresse
  ihrer Quellen eingeordnet. **Sättigung**: Bringen zwei Suchrunden hintereinander weder neue Belege noch neue Kandidaten,
  hört die Recherche auf – weiter zu suchen lohnt sich dann kaum, und das Dossier sagt es.
- **Rückmeldung aus dem Bestätigen**: Beim Bestätigen prüft das Programm für jeden recherchierten Ausdruck des Segments,
  ob die empfohlene Benennung stehen blieb (**übernommen**) oder etwas anderes an ihre Stelle trat (**ersetzt**); ein
  Terminologie-Fehler, den ein Prüfer im Lektorat erfasst, zählt als ersetzt. Erkannt wird in drei Stufen: wörtlich;
  gebeugt, mit Umlaut oder umgestellt („uszczelnienia mechanicznego“ für „uszczelnienie mechaniczne“, „Laufräder“ für
  „Laufrad“) – beides sofort; und wenn der Satz **umformuliert** ist und keine der Benennungen so dasteht, fragt das
  Programm im Hintergrund die KI für das Lektorat, welche Wörter der Übersetzung den Ausdruck wiedergeben. Ihre Antwort
  zählt nur, wenn sie sich am Text bestätigen lässt (die genannten Wörter stehen in der Übersetzung, „die Empfehlung“
  ähnelt ihr auch). Eine **eigene Benennung** des Übersetzers, die die Recherche nicht vorschlug, zählt als ersetzt; ist
  der Ausdruck nur **umschrieben** (Pronomen, in andere Wörter aufgelöst, ausgelassen), zählt es nicht – das sagt nichts
  über die Benennung. Jede Zuordnung steht im KI-Protokoll („Rückmeldung Segment 4: „GLRD“ – ersetzt durch eigene
  Benennung „seal unit“ (KI-Zuordnung)“). Ohne KI-Zugang bleibt es bei den ersten beiden Stufen. Je Segment und Ausdruck
  zählt eine Rückmeldung; ändert sie sich bei erneutem Bestätigen, wird die frühere zurückgenommen – auch, wenn der
  Ausdruck jetzt nur noch umschrieben ist. Daraus lernt das Programm zweierlei: Im
  Reiter **Recherche** steht, wie oft Empfehlungen jeder Gütestufe übernommen wurden („A 23/24 · B 8/10 · C 3/7“) – so
  zeigt sich, ob die Stufen stimmen. Und die Seiten, die eine übernommene Benennung stützten, gewinnen **Ruf** in der
  Quellen-Datenbank, ersetzte verlieren ihn: Spätere Recherchen durchsuchen zuerst die bewährten Seiten mit dem besten
  Ruf (Art der Quelle × Belege, übernommen, ersetzt; was über ein halbes Jahr nichts geliefert hat, zählt nur noch halb),
  Seiten mit mehr ersetzten als genutzten Benennungen fallen heraus. Die Quellen-Datenbank wächst so von selbst mit jeder
  Recherche – für wiederkehrende Themen; ein neues Thema braucht sie nicht, die Einordnung nach Art gilt von der ersten
  Seite an. Alles bleibt im Datenordner dieses Rechners (`recherche-rueckmeldung.json`, `quellen-datenbank.json`).
- **Recherche-Gedächtnis (alle Projekte)**: Jedes Dossier, das ein Projekt speichert, kommt auch in ein Gedächtnis je
  Sprachpaar (mit Land) im Datenordner dieses Rechners – nie auf einen Server. Mitgenommen werden Ausdruck, Bedeutung,
  Kandidaten, Empfehlung, Begründung und die Belege aus dem Netz, dazu Projekt, Thema und Datum; nicht mitgenommen werden
  der Satzzusammenhang, Datei und Segmentnummern und Zitate aus eigenen Fachtexten. Aus vertraulichen Projekten kommt nur
  etwas hinein, wenn unter **Datenschutz** „Recherchen trotzdem ins Recherche-Gedächtnis übernehmen“ angehakt ist. Ein
  belegter Eintrag wird nie durch einen unbelegten ersetzt. So nutzt jedes Projekt, was andere schon recherchiert haben:
  Vor jeder Recherche (Stapel, Warteschlange, Nachrecherche) schaut das Programm dort nach – ist der Ausdruck dort
  **belegt** und passt das **Thema** (gleich oder ein gemeinsames Wort; ohne Thema passt es immer), wird das Ergebnis
  übernommen, ohne neue Recherche, und am Dossier steht „Aus dem Recherche-Gedächtnis: Projekt (Datum)“; sonst setzt die
  Recherche dort an. Die Übersetzung nutzt belegte Einträge zum selben Thema auch dann, wenn das Projekt selbst noch
  kein Dossier dazu hat. Im Reiter **Recherche** zeigt **Recherche-Gedächtnis (alle Projekte)** die Einträge des
  Sprachpaars – durchsuchbar, mit ✕ zum Entfernen (die Dossiers der Projekte bleiben).
  Getrennt wird nach Sprachpaar und nach **Erklärungssprache** (Bedeutung, Begründung und Hinweise schreibt die KI in der
  Sprache, die unter **KI einrichten** für Erklärungen eingestellt ist): Übernommen und angezeigt wird nur, was in der
  eingestellten Erklärungssprache geschrieben ist – wer auf Polnisch erklären lässt, bekommt keine deutschen oder
  französischen Erklärungen. Gibt es einen Ausdruck nur mit Erklärungen in einer anderen Sprache, nutzt die Übersetzung
  davon nur die belegte Benennung (ohne Erklärungstext), und eine neue Recherche setzt bei seinen Kandidaten, Anfragen
  und Belegen an. Ältere Dossiers ohne Angabe gelten als deutsch erklärt.
- **Eine erneute Recherche baut auf der vorigen auf** (**Neu recherchieren**, **Gründlich recherchieren** im Bericht,
  die Nachrecherche von **Automatisch übersetzen**): Die Kandidaten und die Empfehlung des vorigen Dossiers sind der
  Startpunkt, seine Suchanfragen werden nicht wiederholt (die KI erfährt sie und sucht andere Wege), seine Seiten nicht
  noch einmal gelesen, und seine geprüften Belege zählen weiter – mit einer neuen unabhängigen Quelle kann ein Ausdruck
  so schneller belegt sein. Jedes Dossier merkt sich alle bisher gestellten Anfragen, auch die früherer Recherchen.
  Vorhandene Termbank-Einträge der Segmente, in denen der Ausdruck steht, kennt jede Recherche – auch im Stapel und im
  automatischen Ablauf. Das Protokoll im Reiter **KI** nennt je Ausdruck die Dauer, die Runden, die gelesenen Seiten
  und ob er belegt ist (⏱ „Ausdruck“: 2 min 13 s, 3 Runde(n), 9 Seiten – belegt).
- **Die Übersetzung nutzt sie von selbst**: Übersetzt die KI einen Satz (einzeln, alle leeren Segmente, überwachte
  Ordner, Schnittstelle), sucht das Programm die Dossiers zu den Ausdrücken darin heraus und gibt ihr Empfehlung,
  Bedeutung, Strategie und die Varianten ohne Entsprechung („nicht verwenden“) mit – höchstens fünf je Satz, nur
  Dossiers, deren Empfehlung in einer Quelle der Zielsprache belegt ist (siehe unten). Ebenso bekommt sie zu jedem erkannten Begriff der Terminologie dessen Definition. Weil das
  Programm das Passende auswählt und nicht die KI selbst in der Datenbank sucht, hilft das auch kleinen lokalen
  Modellen wie EuroLLM oder Gemma, ohne sie zu verlangsamen.
- **Wo man das findet**: Zu jedem Dossier nennt die KI Fundorte für diese Art Information in beiden Ländern
  (Behörden, amtliche Termbanken, Verbände, Wörterbücher).

### Dokumentart und landesübliche Ausdrücke

Oben im Reiter **Recherche** bestimmt **Dokumentart prüfen**, was das Dokument genau ist (z. B. „Lebenslauf eines
Softwareentwicklers für eine Bewerbung“), welche Gepflogenheiten solche Dokumente im Zielland haben (Aufbau,
Pflichtangaben, Formate) und wie man dort die Überschriften, Berufsbezeichnungen und festen Wendungen dieses Dokuments
tatsächlich schreibt – auch, wo ein Fachwort üblicherweise unübersetzt bleibt. Jeder Vorschlag wird in der Zielsprache
gezählt (Wikipedia; mit Brave oder Tavily nur Treffer aus dem Zielland). Ist ein Kandidat klar häufiger, gilt er
(„nach Häufigkeit gewählt“), sonst der erste Vorschlag der KI. Die angehakten Ausdrücke kommen in den Steckbrief und
gehen in jede KI-Übersetzung dieses Dokuments ein; abgewählte bleiben auch bei einer neuen Prüfung abgewählt. Fehlt das
Land in der Zielsprache (z. B. nur „pl“), gilt das Hauptland der Sprache.

### Quellen-Datenbank

Unter **Hilfsmittel → Quellen-Datenbank** sammelt das Programm die Webseiten, auf denen Recherchen Belege fanden, und
die Fundorte, die die KI nennt – mit Sprache, Thema, Zahl der Belege und was man dort findet. Die Datenbank gilt für
alle Projekte und wächst mit jeder Recherche. Künftige Recherchen zum selben Thema durchsuchen diese Seiten gezielt
mit (belegte zuerst; das setzt einen Suchdienst unter **KI einrichten → Websuche für die Agenten** voraus). Filtern,
**Öffnen** und mit ✕ entfernen.

Bei vertraulichen Projekten gehen auch hier nur Platzhalter an die KI und in die Suchanfragen.

### Fuzzy-Treffer gezielt anpassen

Bei einem TM-Treffer von 70–99 % steht im Seitenreiter **TM** neben **Einsetzen** der Knopf **Anpassen (KI)**: Die KI
bekommt den alten und den neuen Ausgangstext, die freigegebene Übersetzung und die Unterschiede und ändert nur die
Wörter, die sich geändert haben – die übrige Formulierung des Kunden bleibt Wort für Wort. Darunter erscheint die
Änderung gegenüber dem Treffer (rot gelöscht, grün eingefügt); hat die KI deutlich mehr geändert als nötig, steht dort
ein Hinweis. **Leere per KI** macht das von selbst, wenn **Bei „Leere per KI“ Fuzzy-Treffer (70–99 %) gezielt anpassen**
im Seitenreiter **KI** eingeschaltet ist (Vorgabe); sonst wird frei übersetzt, mit dem Treffer als Beispiel. Eine
klassische maschinelle Übersetzung kann keine Anweisungen befolgen – dann übernimmt das Sprachmodell für Recherche.

### Steckbrief: das Dokument erst lesen

Beim Öffnen eines Dokuments (sonst spätestens vor der ersten KI-Übersetzung) liest die KI es einmal ganz (lange
Dokumente: Anfang, Mitte und Ende) im Hintergrund und
hält im **Steckbrief** fest: Thema, Textsorte, Zielgruppe, Ton, wiederkehrende Namen – und vor allem **wer angesprochen
wird** und **wer spricht**. Beides gilt für das ganze Dokument, nicht für einen Satz: Im Polnischen etwa wird „Sie“ je
nach Empfänger zu *Państwo*, *Pan*, *Pani* oder *Pan/Pani*, in Anleitungen oft zum Imperativ (*Kliknij*) oder unpersönlich
(*Należy kliknąć*); und „ich habe gemacht“ heißt je nach Verfasser *zrobiłem* oder *zrobiłam*.

- Erkennt die KI das sicher (Grußformel, Textsorte), übernimmt sie es. Ist sie unsicher, **fragt das Programm einmal je
  Dokument**: Anrede und Sprecher auswählen, **Übernehmen und übersetzen** – oder **Später entscheiden**.
- Jede KI-Übersetzung des Dokuments bekommt den Steckbrief mit; die KI setzt alle abhängigen Formen passend
  (*Pani była*, *Pan był*, *Państwo byli*).
- Im Seitenreiter **KI** unter **Steckbrief des Dokuments** lässt er sich ansehen und ändern (**Übernehmen**) oder
  ergänzen (**Dokument lesen**). Lesen füllt nur **leere Felder**: Was du eingetragen oder bestätigt hast (etwa Thema
  oder Sprecher), bleibt stehen; Namen werden zusammengeführt. Ein lückenhafter Steckbrief, den die KI noch nie gelesen
  hat, wird beim Öffnen von selbst ergänzt. Die Angaben schreibt die KI in der eingestellten Erklärsprache. Beim Öffnen
  wird nur gelesen, wenn eine KI bereitsteht (die eingebaute muss heruntergeladen sein); gefragt wird erst vor der
  KI-Übersetzung. **Vor der KI-Übersetzung das Dokument lesen** schaltet das Lesen ab (eine Anfrage je Dokument und
  Zielsprache).
- Die QA meldet unter **Anrede** Formen, die nicht zum Steckbrief passen – ohne Steckbrief gemischte Formen im Dokument
  (Polnisch Pan/Pani/Państwo, Deutsch Sie/du, Französisch vous/tu).

### Aus Korrekturen lernen

Beim Bestätigen vergleicht das Programm die Übersetzung mit dem eingesetzten Vorschlag (KI, maschinelle Übersetzung,
TM-Treffer). Ändern Sie dieselbe Stelle zum zweiten Mal gleich – etwa „Benutzer“ in „Nutzer“ –, erscheint über dem Editor
**Gelernt: Sie ändern … Künftig immer so?** Mit **Als Regel übernehmen** bekommt die KI die Regel bei jeder Übersetzung
dieser Zielsprache als Vorgabe, und die QA meldet Abweichungen unter **Gelernte Regel**; **Nein** schlägt sie nicht
wieder vor, **Später** blendet den Hinweis aus.

Ändern Sie nur die Anrede (Pan → Pani, Sie → du) oder das Geschlecht der Ich-Form (zrobiłem → zrobiłam), entsteht keine
Regel – das gilt für ein Dokument: Der Hinweis fragt, ob es für das ganze Dokument gilt, und passt dann dessen Steckbrief
an. Bestätigte Steckbriefe merken sich außerdem Anrede und Sprecher je Kunde und Textsorte; bei der nächsten Anleitung
desselben Kunden steht die gewohnte Anrede schon fest.

**Einstellungen → Aus Korrekturen lernen** schaltet das Ganze ab (dann gelten auch die Regeln nicht) und zeigt die Regeln
mit **Aktivieren** und **Entfernen**. Gespeichert wird nur im Datenordner (`gelernt.json`).

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
| Strg+B / Strg+I / Strg+U | Formatierung der Quelle setzen (fett, kursiv, unterstrichen) |
| Strg+Z / Strg+Y | rückgängig / wiederholen |
| Rechtsklick auf ein rot unterstrichenes Wort | Rechtschreibvorschläge |
| Strg+Umschalt+G | nächsten fehlenden Tag einsetzen |
| F3 | markierten Text in der Konkordanz suchen |
| Strg+Umschalt+L | markierten Text unter „Hilfsmittel“ nachschlagen (Wörterbücher, Termbanken) |
| Strg+Umschalt+R | Recherche zur Markierung bzw. zum Segment (Dossier zeigen oder erstellen) |
| Strg+F2 | markierten Quell- und Zieltext als Begriff aufnehmen |
| Strg+Umschalt+T / E / U | KI-Übersetzung / Varianten bewerten / im Netz prüfen |
| Strg+Umschalt+B | markierten Begriff im Kontext klären (Begriffsvergleich) |
| Strg+Umschalt+D | Diktat starten / beenden |
| Strg+S | speichern |
| F1 | Hilfe |

## Datenbanken: Empfehlungen und Übersicht

Oben im Reiter **TM & Terminologie** steht die Übersicht aller Datenbanken:

- **Welche Datenbank wofür:** **Translation Memory** (TM) – ganze Sätze mit Übersetzung zum Wiederverwenden;
  **Terminologie** – Fachbegriffe, die immer gleich übersetzt werden; **Wissensdatenbank** – Stilregeln, Fachwissen
  und Rechercheergebnisse für die KI.
- **Empfehlungen:** Fehlt einem Projekt je Sprachpaar eine gemeinsame TM und Terminologie oder je Zielsprache eine
  Wissensdatenbank, nennt das Programm Typ, Namen (z. B. „TM de-DE–pl-PL“), Grund und Pfad. **Anlegen** bzw.
  **Alle empfohlenen anlegen** legt sie an und bindet sie an die Sprachen – sie gelten dann in jedem Projekt mit
  diesen Sprachen.
- **Meldung beim Öffnen:** Fehlen solche Datenbanken, zeigt die Projektseite eine Meldung mit **Empfohlene
  anlegen**, **Künftig immer automatisch** und **Empfehlungen und Pfade ansehen**. Mit „automatisch“ legt das
  Programm sie beim Öffnen eines Projekts selbst an.
- **Alle Datenbanken** mit Typ, Herkunft, Sprachen, Einträgen und Pfad: der gemeinsame Ordner (**Ordner öffnen**),
  alle sprachbezogenen Datenbanken und die Projekt-Datenbanken der zuletzt geöffneten Projekte.
- **In diesem Projekt verwenden** bindet eine Datenbank – auch die TM, Terminologie oder das Wissen eines anderen
  Projekts – ans aktuelle Projekt (Datenbanken anderer Projekte nur lesend). **Für alle Projekte mit diesen Sprachen
  freigeben** bindet sie an die Sprachen. **Zusammenführen** übernimmt alle Einträge in eine andere Datenbank gleichen
  Typs; die Quelle bleibt unverändert, Doppeltes wird übersprungen.

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
- **Alte Übersetzungen übernehmen** – siehe unten.
- **Alignment von Hand** – **Alignment-Editor (von Hand)**: Ausgangstext und vorhandene Übersetzung wählen (mit Sprachen),
  **Ausrichten** ordnet die Sätze nach Länge und Zahlen einander zu; sichere Paare sind vorausgewählt, unsichere gelb.
  Von Hand korrigieren: **Verbinden ↓** (z. B. zwei Ausgangssätze, eine Übersetzung), **Teilen** (Satz für Satz),
  **↑/↓** verschiebt den ersten bzw. letzten Satz einer Seite ins vorige bzw. nächste Paar, Text direkt bearbeiten –
  jede Zeile ist ein Satz, ein Zeilenumbruch teilt einen Satz (neu segmentieren). **✓** bestätigt ein Paar: Beim
  **Neu ausrichten** bleibt es als fester Anker stehen.
  Mit KI: **KI prüft Paare** bewertet jedes Paar (ist das wirklich die Übersetzung?) und wählt zweifelhafte ab,
  **KI: Unsicheres neu ausrichten** ordnet unsichere Abschnitte nach dem Sinn neu, **Automatisch mit KI** macht beides
  und prüft noch einmal. Mit dem kostenlosen Verbund arbeiten mehrere Modelle parallel.
  **Formatierung als Tags übernehmen**: Fett, kursiv usw. stehen als Tags (⟨1⟩ … ⟨/1⟩) in den Sätzen; die Tags der
  Übersetzung werden an die des Ausgangssatzes angeglichen, damit TM-Treffer später die Formatierung mitbringen.
  **Ausgewählte ins TM** übernimmt die Paare in die gewählte TM (Projekt-TM, sprachbezogene TM) oder in eine **neue TM
  für dieses Sprachpaar**.

### Alte Übersetzungen übernehmen

Wer Texte schon übersetzt hat, macht sie mit **Alte Übersetzungen übernehmen …** (Reiter **Projekt** oder **TM &
Terminologie**) sofort nutzbar – drei Schritte:

1. **Dateien wählen …** – alle auf einmal, auch mehrere Paare und mehrere Zielsprachen. Das Programm ordnet zu, was
   zusammengehört: über den Dateinamen („Vertrag_DE.docx“ ↔ „Vertrag_EN.docx“, „EN-Vertrag“, „Handbuch.en-GB“,
   „Bericht (polski)“), sonst über die am Inhalt erkannte Sprache und gemeinsame Zahlen. Ein Original kann mehrere
   Übersetzungen haben. Zweisprachige Dateien (XLIFF, auch .sdlxliff) enthalten beides und gehen ohne Ausrichtung.
   Jede Zeile zeigt Original, Übersetzung, Sprachen und wie zugeordnet wurde; **⇄** tauscht, **✕** nimmt heraus.
   Dateien ohne Gegenstück werden genannt.
2. **Wohin und wie** – empfohlen ist die **gemeinsame TM und Terminologie des Sprachpaars**: Sie gilt in jedem Projekt
   mit diesen Sprachen und wird bei Bedarf im Standardordner angelegt. Alternativ **nur in dieses Projekt**. **KI prüft
   die Zuordnung** bewertet jedes Satzpaar und richtet Unsicheres nach dem Sinn neu aus; **Fachbegriffe vorschlagen**
   lässt die KI Fachbegriffe samt der dort verwendeten Übersetzung nennen. Ohne KI entscheiden Satzlänge und Zahlen.
3. **Übernehmen** – die sicheren Satzpaare kommen ins TM, mit den Nachbarsätzen als Kontext (später 101-%-Treffer)
   und – bei vertraulichen Projekten – nur mit Platzhaltern. Unsichere Paare werden ausgelassen; **Prüfen** öffnet sie
   im Alignment-Editor, **← Zurück** führt wieder hierher.

Die **vorgeschlagenen Fachbegriffe** sind gegen die Sätze geprüft: Übernommen wird nur, was auf beiden Seiten wirklich
steht (die Übersetzung auch gebeugt, z. B. „rachunek oszczędnościowy“ in „na rachunku oszczędnościowym“). Erfundene
Paare, Platzhalter und Begriffe, die schon in der Terminologie stehen, fallen heraus. Neben jedem Begriff stehen
Häufigkeit und ein Beispielsatz; den Zielbegriff kann man vor der Übernahme berichtigen. **Ausgewählte in die
Terminologie** legt sie an.

### TM-Pflege

Unter **TM-Einträge bearbeiten und pflegen** die TM wählen (Projekt-TM oder eine beschreibbare angebundene) und:

- **Suchen und Ersetzen in allen Einträgen** – in der Quelle und/oder der Übersetzung, auf Wunsch mit Groß/klein, ganzen
  Wörtern oder regulärem Ausdruck (`$1` setzt Gruppen ein). **Vorschau** zeigt die geänderten Einträge, **Alle ersetzen**
  übernimmt sie. Tags (⟨1⟩ …) werden nie verändert. Wird ein Eintrag dadurch einem anderen gleich, werden beide
  zusammengeführt; ihre Satzzusammenhänge bleiben erhalten.
- **Bereinigen** – Dubletten (nur Leerraum verschieden) zusammenführen, Einträge ohne Übersetzung und auf Wunsch solche mit
  Ziel = Quelle löschen (Kennungen und Zahlen bleiben).
- **Uneinheitliche Übersetzungen zeigen** – gleiche Ausgangssätze mit verschiedenen Übersetzungen, zum Vereinheitlichen
  oder Löschen überholter Fassungen.

### Eigene Fachtexte (PDF-Ordner)

Unter **TM & Terminologie → Eigene Fachtexte** gibst du Ordner mit eigenen Texten an: PDFs, Word- und Writer-Dateien,
Präsentationen, Tabellen, Text, Markdown oder HTML – etwa Normen, die du besitzt, Handbücher, Datenblätter,
Gesetzestexte oder frühere Übersetzungen. Das Programm liest sie selbst (auch Unterordner) und legt einen Suchindex an.
Nichts verlässt den Rechner; Auszüge bekommt nur KI auf diesem Rechner, außer du hakst **Auszüge auch an Online-KI
geben** an.

- **Ordner hinzufügen …** liest den Ordner gleich ein; **Jetzt einlesen** liest später nur neue und geänderte Dateien
  und nimmt verschwundene heraus. Ist ein Ordner gerade nicht erreichbar (abgezogenes Laufwerk), bleiben seine Dateien
  im Index. ✕ nimmt einen Ordner heraus; die Dateien selbst bleiben unberührt.
- Je Datei erkennt das Programm die **Sprache**, bei PDF die **Seite** und den **Stand**: was das Dokument selbst nennt
  („Stand: 01.03.2021“, „Ausgabe 2019-03“, „Edition 2020“, „Wydanie 2018“, „DIN EN ISO 12100:2011-03“), sonst das
  Datum aus den PDF-Eigenschaften oder der Datei. **Eingelesene Dateien** zeigt das alles; gescannte PDFs ohne Text
  stehen dort als nicht lesbar – sie brauchen vorher eine Texterkennung (OCR).
- **Suchindex:** wo der Index liegt; **Ordner …** verschiebt ihn, am besten auf ein Laufwerk mit viel Platz. Er braucht
  etwa so viel wie der reine Text der Dateien (meist ein Bruchteil der PDF-Größe).
- Die **Probesuche** zeigt, wo eine Wortfolge vorkommt (Datei, Seite, Satz; ein Klick öffnet die Datei) und wie oft.

Die **Recherche** (Dossiers und Begriffsvergleich) sucht zuerst hier: Zu Ausgangsbegriff und jedem Kandidaten kommen
wörtliche Belegstellen mit Datei, Seite und Stand ins Dossier, und die KI erfährt, wie oft jeder Kandidat in deinen
Texten vorkommt (z. B. „mechanical seal: 14, sliding ring seal: 0“) – ein starkes Signal, das auch kleine lokale
Modelle nutzen können. „Gelernt“ heißt dabei nachgeschlagen, nicht das Modell neu trainiert: Das bleibt aktuell,
nachvollziehbar und löschbar. Liegen dieselben Unterlagen in zwei Sprachen vor, lassen sie sich über das Alignment
(übersetzte Dateien ins TM) zu Satzpaaren für den Übersetzungsspeicher machen; die Übersetzung bekommt sie dann als
TM-Treffer.

### Freie Sprachdaten herunterladen

Unter **TM & Terminologie → Freie Sprachdaten herunterladen** findet das Programm frei verfügbare Daten: Translation
Memories aus dem OPUS-Archiv (u. a. der EU-Übersetzungsspeicher **DGT**, **Europarl**, EU-Recht, Arzneimittelinformationen
der EMA, Veröffentlichungen der EZB, Verwaltungstexte aus der ELRC-Initiative, Oberflächentexte freier Software,
Alltagssätze aus Tatoeba) und die EU-Terminologie **IATE** (frei verfügbare Gesamtausgabe, Stand 2019). OPUS kennt über
1000 Sprachen; je Sprachpaar gibt es oft Dutzende Datensätze (Deutsch–Englisch über 120, Deutsch–Polnisch etwa 50,
Deutsch–Französisch fast 90). Für Englisch↔Französisch kommt die Termbank **TERMIUM Plus** der kanadischen Regierung
dazu – 23 Fachgebiete einzeln ladbar (Recht, Verwaltung, Medizin, Wirtschaft, Informatik …, je bis 30 MB), mit Status
(bevorzugt, zugelassen, nicht verwenden), Synonymen, Abkürzungen und Definitionen; Lizenz: Open Government Licence – Canada.
Für Rätoromanisch hat OPUS nur wenig (Deutsch–Rätoromanisch etwa 10 000 Satzpaare aus der Wikipedia); Wörterbücher und
TERMDAT stehen unter **Hilfsmittel**.

So geht es:

1. **Von** / **nach** wählen (vorgeschlagen: die Sprachen des offenen Projekts; es geht auch ohne Projekt) und **Suchen**.
2. Eingrenzen: Suchtext (Name oder Beschreibung), Art (Translation Memory oder Terminologie), Fachgebiet (Recht und
   Verwaltung, Medizin, Technik und Software, Wirtschaft, Allgemein, Untertitel und Literatur, aus dem Netz gesammelt),
   **nur beruflich nutzbare Lizenzen** (blendet „nur für Forschung“, „nicht kommerziell“ und Unklares aus) und die
   Reihenfolge (empfohlene, größte oder kleinste zuerst, Name). Angezeigt werden zunächst empfohlene, frei nutzbare
   Datensätze; **Alle anzeigen** zeigt auch sehr große, aus dem Netz gesammelte oder eingeschränkt nutzbare. Die Zeile
   über der Liste sagt, wie viele angezeigt sind.
3. Datensatz anklicken: Darunter stehen Beschreibung, Lizenz und Platzbedarf; **Info** öffnet die Seite des Datensatzes.
4. **Herunterladen und anbinden** – das Programm lädt aus der offiziellen Quelle, übernimmt nur das gewählte Sprachpaar
   (auch wenn die Daten in der Gegenrichtung vorliegen) und bindet die neue Datenbank an: **für alle Projekte mit diesen
   Sprachen** (Vorgabe) oder nur für das offene Projekt. Sie schreibt nicht zurück. Danach erscheinen die Einträge im Editor
   als TM-Treffer bzw. Fachbegriffe.

Ein TM braucht mit Suchindex etwa 2,5 KB je Satzpaar (DGT Deutsch–Englisch also rund 9 GB); mit **Ordner …** lässt sich
ein Laufwerk mit mehr Platz wählen. Bei den meisten Lizenzen ist die Quelle anzugeben.

## Terminologie

Begriffe bestehen aus Benennungen in mehreren Sprachen mit Status (bevorzugt, zugelassen, verboten),
Definition und Fachgebiet.

- Anlegen unter **TM & Terminologie → Neuer Eintrag** oder im Editor mit Strg+F2.
- **TBX / Terminologie-XML importieren**, **TBX exportieren**.
- Im Editor erscheinen erkannte Begriffe unter **Termini**; die QA meldet, wenn eine vorgeschriebene
  Benennung fehlt oder eine verbotene verwendet wurde.

### Fachbegriffe aus den Projektdateien gewinnen

Vor dem Übersetzen unter **TM & Terminologie → Fachbegriffe aus den Projektdateien gewinnen**: Zielsprache wählen,
**Begriffe suchen**. Das Programm findet wiederkehrende Begriffe und Wortgruppen der Ausgangstexte (im Deutschen besonders
Substantive und lange Komposita; Wortgruppen, die nur Teil einer längeren sind, fallen weg). Mit **KI wählt aus und schlägt
Übersetzungen vor** bleiben die echten Fachbegriffe, jeder mit der üblichen Übersetzung – nur Begriffe, die in den Texten
stehen, und keine, die schon in der Terminologie sind. Prüfen, Übersetzung berichtigen, **Ausgewählte in die
Terminologie** (gemeinsame Terminologie des Sprachpaars oder nur das Projekt). Ohne KI erscheinen die Kandidaten nach
Häufigkeit zum Anhaken und Übersetzen von Hand.

## Hilfsmittel: Wörterbücher, Termbanken, Rechtsquellen

Der Reiter **Hilfsmittel** sammelt Termbanken, Wörterbücher, Korpora, Rechtsquellen und Sprachberatung öffentlicher und
gemeinnütziger Herausgeber – etwa IATE, TERMIUM Plus, TERMDAT, FranceTerme, UNTERM, DWDS, Wiktionary, CNRTL, das
Wörterbuch der Polnischen Akademie der Wissenschaften, Pledari Grond (Rätoromanisch), EUR-Lex, Fedlex, ISAP und den
Stilleitfaden der EU. Jeder Eintrag nennt Art, Sprachen, Herausgeber und Nutzungsbedingungen und sagt, wie man ihn
nutzt: *im Programm eingebaut* (die Agenten greifen darauf zu), *als Datenbank ladbar* (**Laden …** führt zu „Freie
Sprachdaten herunterladen“) oder *im Browser* (**Öffnen**).

- **Für dieses Projekt empfohlen** (auch im Reiter Projekt): passend zu Ausgangs- und Zielsprachen und zum Fachgebiet,
  jede Projektsprache mindestens mit ihrem eigenen Wörterbuch.
- **Nachschlagen**: Begriff eingeben oder im Editor markieren und **Strg+Umschalt+L** drücken, Sprache wählen,
  Wörterbuch anklicken – der Browser öffnet die Seite mit dem Begriff.
- **Alle Hilfsmittel**: nach Art, Sprache und Suchwort filtern.
- **Verbreitung im Netz**: Kommt ein Ausdruck in bestimmten Ländern überhaupt so vor – und wie oft? Ausdruck und
  Varianten mit „|“ eingeben („Jänner | Januar“), Sprache und Länder wählen (vorgeschlagen: für Deutsch DE, AT, CH; für
  Französisch FR, BE, CH, CA …) und **Prüfen**. Je Variante und Land wird die genaue Wortfolge über eine offizielle
  Such-Schnittstelle mit Länderfilter gesucht – Brave (bis 10 Seiten zu 20 Treffern) oder Tavily (20 Treffer), eingerichtet
  unter **KI einrichten → Websuche für die Agenten** mit eigenem Schlüssel. Gezählt werden die Treffer, die die Wortfolge
  wörtlich enthalten, und wie viele davon auf Seiten des Landes liegen (.at, .ch …); „≥ 40“ heißt: alle geprüften passten,
  es gäbe weitere. Darunter Beispielseiten zum Öffnen. Vor dem Prüfen steht, wie viele Anfragen es kostet (Varianten ×
  Länder × Seiten). Geschätzte Gesamtzahlen („ungefähr 1 230 000 Ergebnisse“) liefert keine offizielle Schnittstelle;
  für die Frage „kommt das überhaupt vor?“ sind die gezählten Belege ohnehin verlässlicher. Im Editor übernimmt
  **Konkordanz → Im Netz je Land …** die Wendung.

Das Programm liest diese Seiten nicht selbst aus; der Aufruf im Browser erscheint im Datenausgang.

## Qualitätssicherung

**QA alle Dateien** (Reiter Projekt) oder **Dokument prüfen** (Editor) prüft:
leere Segmente, Ziel = Quelle, Tags, Zahlen, Leerzeichen, Satzzeichen am Ende, Klammern, Terminologie,
Wortdopplungen, Konsistenz (gleiche Quelle – verschiedene Übersetzungen), Anrede (siehe Steckbrief), Länge, Rechtschreibung,
unerwünschte Wörter und **eigene Regeln** mit regulären Ausdrücken (Quell-Muster optional, Ziel-Muster,
Schweregrad). Einstellungen unter **QA-Einstellungen und eigene Regeln**; Erweiterungen können weitere
Prüfungen beisteuern.

Rechtschreibung: Unter **Einstellungen → Rechtschreibprüfung** das Wörterbuch der Zielsprache laden
(freie Wörterbücher des LibreOffice-Projekts).

### Grammatikprüfung

Im Editor unter **Meldungen → Grammatik prüfen** werden alle Übersetzungen auf Grammatik, Fall und Kongruenz, Verbformen,
Rechtschreibung und Zeichensetzung geprüft; die Hinweise stehen unter „Grammatik“ in der Meldungsliste, mit Vorschlag.
Womit, wird auf der Seite **KI einrichten → Grammatikprüfung** gewählt:

- **KI (Sprachmodell)** – Vorgabe; auch für Polnisch gut. Vertrauliche Angaben gehen wie sonst nur als Platzhalter hinaus;
  es zählt nur, was wirklich im Text steht.
- **LanguageTool auf eigenem Server** – freie Software (LGPL); die Texte bleiben im Haus. Start z. B. mit Java:
  `java -cp languagetool-server.jar org.languagetool.server.HTTPServer --port 8081` (Paket von languagetool.org)
  oder mit Docker; Adresse eintragen (Vorgabe `http://localhost:8081`).
- **LanguageTool öffentlich** – kostenlos, aber begrenzt (20 Anfragen und 75 KB je Minute) und nicht für automatische
  Massenprüfungen gedacht: Das Programm nutzt sie nur auf Knopfdruck, bündelt die Segmente und hält die Grenzen ein.
  Bei vertraulichen Projekten wird sie nicht genutzt.

### Zahlenformat, Höchstlänge, QA-Profile

- **Zahlenformat der Zielsprache** meldet Zahlen und Datum, die noch in der Form der Ausgangssprache stehen
  („1.234,5 kg“ in einer englischen Übersetzung). **Zahlen und Datum anpassen** im Reiter Projekt bringt sie in allen
  Dateien in die Form der Zielsprache (1,234.5; 12.03.2024 → 12/03/2024); mit **Zahlen und Datum automatisch anpassen**
  geschieht das bei TM-Treffern und KI-Übersetzungen von selbst. Versionsnummern, Uhrzeiten und IP-Adressen bleiben,
  Maßeinheiten werden nicht umgerechnet.
- **Höchstlänge jeder Übersetzung** (Zeichen) gilt für alle Segmente; Grenzen aus der Datei (XLIFF `maxwidth`) zusätzlich.
- **QA-Profil** – die QA-Einstellungen eines Projekts unter einem Namen speichern (z. B. je Kunde) und in anderen Projekten
  mit **Übernehmen** einsetzen.

### Trifft die Übersetzung den Sinn? (KI-Prüfung)

Die formale QA findet Zahlen, Tags und Terminologie, aber keine Sinnfehler. Dafür gibt es zwei Methoden:

- **Rückübersetzung:** Ein Modell übersetzt die Übersetzung zurück in die Ausgangssprache, ohne das Original zu
  kennen; danach vergleicht die KI den Sinn mit dem Original und nennt, was fehlt, hinzugekommen oder verändert ist
  (Zahlen, Verneinung, wer was tut, falsche Begriffe).
- **Abgleich mehrerer Modelle:** Mehrere Modelle übersetzen unabhängig; weicht die Übersetzung im Sinn von ihnen ab,
  ist sie verdächtig. Mit dem kostenlosen Verbund kommen die Vorschläge von verschiedenen Modellen gleichzeitig; mit
  einem einzelnen Modell ist der Abgleich weniger aussagekräftig.

Im Editor unter **KI → Übersetzung prüfen** (für das gewählte Segment): Urteil (plausibel, bitte prüfen,
wahrscheinlich fehlerhaft) mit den Befunden, der Rückübersetzung und den unabhängigen Übersetzungen – jede lässt sich
mit **übernehmen** als Entwurf einsetzen. Unter **Meldungen → Mit KI prüfen (Sinn)** wird das ganze Dokument geprüft
(Rückübersetzung; **auch mehrere Modelle abgleichen** ist gründlicher, braucht aber etwa doppelt so viele
Anfragen); zweifelhafte Segmente erscheinen als Meldung „KI-Prüfung“ und lassen sich mit dem Filter „mit Meldungen“
ansteuern. Die KI-Prüfung ist ein Hinweis, kein Ersatz für das Lektorat durch einen Menschen.

## Vorübersetzung und Analyse

- **Vorübersetzen (TM)** setzt Treffer ab der eingestellten Schwelle ein; Kontexttreffer auf Wunsch bestätigt.
- **Analyse** zählt Segmente, Wörter und Zeichen je Trefferstufe: Kontext, Wiederholungen, 100 %, 95–99 %,
  85–94 %, 75–84 %, 50–74 %, kein Treffer – die Grundlage für Angebote.
- **Leere per KI** (Editor) übersetzt alle leeren Segmente mit der KI; das Ergebnis ist ein Entwurf, der
  geprüft und bestätigt werden muss. Vorher fragt das Programm **Auch recherchieren?**:
  - **Ja, mit Recherche**: Vor jedem Segment sucht die KI dessen Ausdrücke, die Recherche brauchen (sind sie aus
    „Dokument durchsehen“ noch nicht bekannt, liest sie das Segment), recherchiert die noch ohne Dossier – Fachseiten
    zuerst, dann allgemein – und übersetzt das Segment mit den belegten Ergebnissen. Steht ein Ausdruck in mehreren
    Segmenten, wird er nur einmal recherchiert. Eine fehlgeschlagene Recherche hält die Übersetzung nicht auf.
    **Zeit je Ausdruck** wählen Sie im selben Dialog (das Feld mit dem Pfeil). Vorgegeben ist **Intelligent – die KI
    bewertet jedes Segment und vergibt die Zeit**: Vor der Recherche schätzt die KI jedes Segment ein – wie schwierig
    (einfach, mittel, schwer, sehr schwer, mit Grund), was mit jedem Ausdruck geschehen soll (recherchieren mit 0,5 bis
    15 Minuten – schwierige Begriffe mehr, bekannte weniger; **unverändert lassen** bei Eigennamen, Marken, Produkten,
    die in beiden Sprachen gleich bleiben; **nicht nötig**, wenn Zusammenhang oder Termbank es schon klären) und wie viele
    Nachbarsegmente davor und danach (0 bis 3) sie als Zusammenhang braucht – für die Recherche und für die Übersetzung.
    Einfaches wie „Hallo“ braucht gar nichts. Nach jeder Recherche prüft das Programm, ob sie reicht: belegt und eindeutig
    reicht ohne weitere Frage; sonst sagt die KI, was fehlt (etwa die Definition im Zielland, die Verwendung in dieser
    Textart), und eine **zweite Runde** mit doppelter Zeit (3 bis 15 Minuten) sucht gezielt danach – auf dem Gefundenen
    aufbauend, höchstens einmal je Ausdruck. Was danach noch unsicher ist, steht im Bericht unter **Gründlicher
    recherchieren?**. Jede Recherche hört auf, sobald es reicht: Steht ein Kandidat in zwei unabhängigen Quellen der
    Zielsprache, wird keine weitere Seite gelesen – das Zeitbudget ist nur die Obergrenze. Das Protokoll nennt je Segment
    die Bewertung („Segment 3 – Bewertung: schwer … „Raindancer“ bleibt unverändert · „GLRD“ 4 min“); in der
    Segmentliste steht dazu ein farbiger Punkt unter der Nummer (grün einfach, gelb mittel, orange schwer, rot sehr
    schwer), sein Tooltip zeigt dieselbe Zeile. Was als „bleibt unverändert“ eingestuft ist, geht als Hinweis „unverändert
    lassen“ in die Übersetzung – ohne Recherche, denn ein Name braucht keinen Beleg. **Die Bewertung lernt die
    Geschwindigkeit dieses Rechners**: Jede Recherche wird festgehalten (KI, Schwierigkeitsstufe, Zeitbudget, Dauer,
    belegt oder nicht – nur Zahlen, keine Ausdrücke; die letzten 500, in `recherche-statistik.json` im Datenordner). Die
    Bewertung erfährt, wie lange Ausdrücke jeder Stufe mit der eingestellten KI bis zum Beleg typischerweise brauchen,
    und vergibt die Minuten danach; ab vier belegten Recherchen einer Stufe gilt das Gemessene (mit etwas Spielraum)
    auch dann, wenn die KI selbst keine Zeit nennt. So bekommt ein langsamer Rechner mehr Zeit und eine schnelle Online-KI
    weniger. Alternativ eine
    feste Zeit: 2, 5, 10 oder 20 Minuten oder ohne Zeitgrenze (dann je Segment höchstens 4 Ausdrücke, die schwierigsten
    zuerst; bei einem einzelnen Segment alle).
    **Mehrere Ausdrücke gleichzeitig**: Während der eine auf Webseiten wartet, nutzt der andere die KI – mit der
    eingebauten KI 2 zugleich, mit einer Online-KI 3, mit einem Verbund so viele, wie Modelle bereitstehen (höchstens 4).
    Jede Protokollzeile beginnt dann mit dem Ausdruck in eckigen Klammern; die ⏱-Zeilen zeigen, was es bringt.
    Damit die Übersetzung nicht an einem schwierigen Ausdruck stehen bleibt, wird jeder Schritt (Ausdrücke suchen,
    bewerten, recherchieren, im Netz prüfen) spätestens nach 4 Minuten abgebrochen (bei längerem Zeitbudget nach dem
    doppelten Budget); das Protokoll im Reiter **KI** vermerkt es, und es geht ohne ihn weiter. Gründlicher
    sucht **Recherchieren** im Reiter **Recherche**; dessen Warteschlange pausiert, solange **Leere per KI** läuft, weil
    die eingebaute KI eine Anfrage nach der anderen beantwortet. Namen, Anschriften und andere geschützte Angaben sowie
    bloße Datumsangaben und Zahlen werden nie recherchiert (siehe Datenschutz); das Protokoll nennt sie.
  - **Nein, nur übersetzen**: wie bisher, mit den vorhandenen Dossiers.
  - **Danach jede Übersetzung im Netz prüfen** (vorgegeben): Nach dem Übersetzen prüft die KI jede Übersetzung wie
    **Im Netz prüfen** – sind Satzbau und Wortverbindungen in der Zielsprache üblich, werden die Wörter so verwendet? –
    und kennt dabei die Recherche zu jedem Teil des Satzes (belegt oder nicht). Fragliches ersetzt sie durch die
    bessere Fassung (als Entwurf).
- **Ausgewählte per KI** (Editor, neben **Leere per KI**): dasselbe für die ausgewählten Segmente – auch schon übersetzte
  werden neu übersetzt (als Entwurf), bestätigte und gesperrte bleiben unverändert. Mehrere Segmente wählen Sie mit
  **Strg+Klick** oder **Umschalt+Klick** auf Nummer oder Status des Segments (ein Klick in Quelle oder Ziel wählt nur
  dieses); ohne solche Auswahl gilt das gewählte Segment. Es kommt dieselbe Frage **Auch recherchieren?** mit derselben
  Wahl der **Zeit je Ausdruck** (intelligent, 2, 5, 10 oder 20 Minuten oder **ohne Zeitgrenze**); bei einem einzelnen
  Segment werden mit fester Zeit alle seine Ausdrücke recherchiert, nicht nur die vier schwierigsten.
- **Automatisch übersetzen** (Editor, neben **Ausgewählte per KI**): alles nacheinander, wie ein sorgfältiger Übersetzer
  vorgeht. Vorher kommt dieselbe Frage **Auch recherchieren?**, dazu lassen sich das KI-Lektorat und die Nachrecherche
  abwählen. Die Schritte:
  1. **Steckbrief lesen** (falls noch leer) – Textart, Zielgruppe, Ton, Anrede.
  2. **Übersetzen** – die leeren Segmente, auf Wunsch mit Recherche je Segment und Netzprüfung (wie **Leere per KI**).
  3. **QA-Prüfung** – Zahlen, Tags, Terminologie, Länge (Ergebnis im Reiter **Meldungen**).
  4. **KI-Lektorat** – die KI bewertet alle Übersetzungen (wie **KI bewertet die Datei**).
  5. **Nachrecherche unsicherer Ausdrücke** (nur mit Recherche) – bis zu fünf Ausdrücke, die mehr Recherche brauchen
     (siehe unten), werden gründlicher recherchiert (5 Minuten je Ausdruck); ändert sich dadurch die belegte Übersetzung,
     werden die betroffenen Segmente neu übersetzt.
  6. **Verbesserungen des Lektorats übernehmen** – wo die KI eine bessere Fassung vorschlägt und die Stelle in der
     Übersetzung steht, wird sie eingesetzt; der Rest bleibt zum Prüfen von Hand.
  7. **Geänderte Segmente erneut bewerten** – die Punktzahl vorher → nachher.

  Bestätigte und gesperrte Segmente bleiben unverändert, alles Neue ist Entwurf. Ein Schritt, der fehlschlägt, hält die
  übrigen nicht auf; **Abbrechen** beendet den Lauf. Am Ende öffnet sich der **Bericht** mit aufgeklapptem Protokoll.

  **Woran das Programm erkennt, dass ein Ausdruck mehr Recherche braucht** (Abschnitt **Gründlicher recherchieren?** im
  Bericht, je Ausdruck mit den Gründen und dem Knopf **Gründlich recherchieren**, der ihn ohne Zeitgrenze in die
  Warteschlange des Reiters **Recherche** stellt): nicht belegt (keine Quelle der Zielsprache bestätigt die Übersetzung),
  vom Lektorat bemängelt (Terminologie oder Fehlübersetzung in einem Segment mit dem Ausdruck – schwerer, wenn die
  Anmerkung ihn nennt), von der Netzprüfung als fraglich eingestuft, nicht eindeutig (kein Kandidat in zwei
  unabhängigen Quellen), Zeitbudget erreicht. Kommt der Ausdruck in drei oder mehr Segmenten vor, wiegt das schwerer –
  eine falsche Wahl wirkt sich mehrfach aus; allein ist Häufigkeit aber kein Grund.
- **Formulierungen verbessern** (Seitenreiter **KI**): dasselbe für vorhandene Übersetzungen – für das gewählte
  Segment oder mit **Alle Segmente verbessern** für alle übersetzten, noch nicht bestätigten (bestätigte bleiben
  unangetastet).
- **Was die KI gesammelt hat**: Je Segment steht im Reiter **KI** (beim Anklicken), was recherchiert und geprüft wurde:
  jeder Ausdruck mit Entsprechung, Bedeutung, Begründung und Quelle (Link springt zur Stelle), ob er belegt war und in
  die Übersetzung einging, die Netzprüfung mit Zahlen, Urteil und Erklärung und gegebenenfalls „Verbessert: alt →
  neu“. Die Übersicht **Was die KI gesammelt hat (alle Segmente)** zeigt alle Segmente mit einer Kurzfassung; ein Klick
  springt zum Segment.
- **Bericht erstellen** (Reiter **KI**): eine HTML-Seite im Projektordner unter `berichte`, die sich im Browser
  öffnet: welche Sätze und Ausdrücke wie übersetzt wurden und warum – alle recherchierten Ausdrücke mit Übersetzung,
  belegt oder nicht, Bedeutung, Strategie, Begründung und Belegen zum Anklicken; dann jeder Satz mit Ausgangstext,
  Übersetzung, Herkunft (KI, TM-Treffer, von Hand), Recherche, Netzprüfung und Verbesserung. Die Netzprüfungen stammen
  aus der laufenden Sitzung, die Dossiers aus dem Projekt.

### Angebot aus der Analyse

Nach **Analyse** steht unter **Angebot** der Preis: Grundpreis je Wort, Zeichen oder Normzeile (55 Zeichen), Währung, MwSt.
und Mindestbetrag; unter **Anteil des Preises je Trefferstufe** gilt z. B. 25 % für Wiederholungen und 100-%-Treffer, 0 % für
Gesperrtes. Angezeigt werden je Zielsprache Einheiten, gewichtete Einheiten, die Ersparnis durch Treffer und der Betrag,
darunter netto, MwSt. und gesamt. **Angebot als Word speichern** erzeugt den Kostenvoranschlag. Die Preise werden im Projekt
gespeichert und von neuen Projekten übernommen.

### Pseudoübersetzung

**Pseudoübersetzung …** erzeugt die Zieldatei der gewählten Datei mit Akzentbuchstaben („[Ɓéţŕîéƀšàñĺéîţüñĝ ~~~~~~]“) und
30 % längerem Text, ohne das Projekt zu ändern. Im Originalprogramm geöffnet zeigt sie vor der Übersetzung, ob Sonderzeichen
ankommen, längere Texte ins Layout passen und keine Textstelle fehlt (unübersetzter Text fällt sofort auf).

## Wissensdatenbanken

Neben TM und Terminologie gibt es **Wissen**: Sachwissen, Definitionen, Sprachgebrauch und Stilregeln, jeweils
mit Sprache, Thema, Quelle und Autor. Jedes Projekt hat ein Projekt-Wissen; weitere Datenbanken lassen sich
pro Projekt und pro Sprache anbinden – lokal oder auf dem Teamserver. Ohne geöffnetes Projekt stehen das
allgemeine **KI-Gedächtnis** (dort speichern die Agenten ihre Recherchen) und alle Wissensdatenbanken im
Benutzerordner zur Wahl.

**Sprachbezogene Datenbanken:** Beim Anbinden unter **TM & Terminologie → Ressource anbinden** bindet **für alle
Projekte mit diesen Sprachen** eine TM, Terminologie oder Wissensdatenbank an die Sprachen statt an das Projekt.
Sie gilt dann automatisch in jedem Projekt mit diesen Sprachen (ohne Angabe: die Sprachen des aktuellen
Projekts) und für die Agenten auch ohne Projekt; in der Liste steht sie mit dem Zusatz „für alle Projekte mit
diesen Sprachen“. **Lösen** löst sie für alle Projekte.

- Reiter **Wissen → Durchsuchen**: Einträge suchen, anlegen, ändern.
- Im Editor zeigt die Seitenleiste **Wissen** passende Einträge; sie gehen auch in jede KI-Übersetzung und
  Bewertung ein. Stilregeln der Zielsprache gelten immer.
- **Wissens-Agent**: Ziel-Datenbank wählen oder unter **Neue Datenbank anlegen** eine neue anlegen (bei offenem
  Projekt wird sie gleich angebunden), Thema und Sprachen angeben (weitere Sprachen ins Feld daneben). Der Agent
  arbeitet sich ein wie ein Mensch: Er **gliedert das Thema** in Unterthemen (beim Schweißen etwa Verfahren,
  Maschinen und ihr Aufbau, Werkstoffe, Normen und Sicherheit), sucht und liest zu jedem **in jeder gewählten
  Sprache** verlässliche Quellen und legt Einträge mit **wörtlichem Beleg** an (Thema „Schweißen – Werkstoffe“).
  Steht der Beleg nicht wirklich auf der gelesenen Seite, wird der Eintrag verworfen. Danach prüft er, zu welchen
  Unterthemen in welcher Sprache noch nichts gefunden ist, und **sucht dort mit neuen Anfragen weiter** (bis zu drei
  Runden); das Protokoll zeigt am Ende die Abdeckung je Unterthema und Sprache. Fachseiten, die Einträge geliefert
  haben, merkt sich das Programm und sucht beim nächsten Mal dort zuerst.
- **Fragen an das Wissen** (Reiter **Wissen → Fragen**): Nach einer Recherche – etwa zum Thema Schweißen: was es ist,
  welche Verfahren, welche Maschinen, wie sie aufgebaut sind, welche Werkstoffe – stellst du Fragen wie „Wie ist ein
  MIG/MAG-Schweißgerät aufgebaut?“. Das Programm lässt die KI Suchwörter in den Sprachen des Wissens bilden (eine
  deutsche Frage findet auch englische Einträge), sucht die passenden Einträge aller angezeigten Wissensdatenbanken
  (oder mit **nur in dieser Datenbank suchen** nur der gewählten) und Stellen aus den eigenen Fachtexten heraus und
  lässt die KI **nur daraus** antworten, jede Aussage mit [n]. Darunter stehen die **Quellen der Antwort** – die
  zitierten oben, mit Beleg und Link. Jede Aussage soll eine Quelle haben: Sätze der Antwort ohne [n] meldet das
  Programm („Achtung: 1 Aussagen ohne Quellenangabe“), zitierte Notizen ohne Link ebenso („nicht nachprüfbar“), und bei
  jeder Notiz steht, ob sie eine Quelle hat. Steht zu einem Begriff aus der Frage noch nichts im Wissen, sagt das
  Programm das ausdrücklich („Zu „Gleitringdichtung“ habe ich noch keine Informationen.“). Mit **Fehlendes gleich im
  Netz nachrecherchieren** (vorgegeben) sucht der Wissens-Agent sofort genau danach, legt es in der gewählten
  Datenbank ab und beantwortet die Frage neu; findet auch er nichts Belegtes, steht das da. Ohne den Haken übernimmt
  das **Nachrecherchieren** auf Knopfdruck. Weil das Programm die Notizen auswählt, reicht auch ein kleines lokales
  Modell.
- **Gesetz einarbeiten** (Reiter **Wissen → Einarbeiten**): Abkürzung eines Bundesgesetzes eingeben, wie sie auf
  gesetze-im-internet.de steht (StGB, StPO, BGB, ZPO, HGB, GG …), und die Zielsprache. Das Programm lädt die
  **amtliche Fassung** mit ihrem **Stand** („zuletzt geändert durch …“) – amtliche Werke sind frei verwendbar – und
  arbeitet sich in drei Schritten ein:
  1. Jeder Paragraph kommt in die **eigenen Fachtexte** (unter **TM & Terminologie** als Quelle mit Webadresse
     aufgeführt; das Einlesen der Ordner lässt sie stehen). Die Recherche und **Fragen an das Wissen** finden darin
     Belegstellen mit Fundstelle wie „StGB – Strafgesetzbuch, § 242 Diebstahl“.
  2. Die **Begriffe** werden herausgezogen: **Legaldefinitionen** („Notwehr ist die Verteidigung, die …“,
     „Verbrechen sind rechtswidrige Taten, die …“) und **Begriffslisten** wie in § 11 StGB („Amtsträger: wer nach
     deutschem Recht …“), dazu die **Überschriften** mit dem ersten Satz des Paragraphen als Definition. Satzanfänge
     wie „Jedoch ist …“ oder „Rechtswidrig ist …“ gelten nicht als Begriff. Beim StGB sind das gut 460 Begriffe.
  3. Sie landen mit Paragraph als Fundstelle in der **gemeinsamen Terminologie des Sprachpaars** (z. B.
     „Terminologie de-DE–en-GB“, wird angelegt, wenn es sie nicht gibt) und als Definition in der gewählten
     **Wissensdatenbank** (ohne gewählte Datenbank in „Wissen de-DE“, an Deutsch gebunden, also in jedem Projekt mit
     Deutsch dabei). Was schon da ist, wird nicht doppelt angelegt; ein zweites Einarbeiten nach einer
     Gesetzesänderung ergänzt nur das Neue, die eigenen Fachtexte bekommen die neue Fassung.

  Bei **Englisch** als Zielsprache kommen die englischen Benennungen aus der Übersetzung des
  Bundesjustizministeriums dazu: aus den Überschriften („§ 242 Diebstahl“ → „Theft“) und den Begriffslisten
  („Amtsträger“ → „public official“). Diese Übersetzung ist **nicht amtlich und urheberrechtlich geschützt**; deshalb
  übernimmt das Programm nur einzelne Benennungen – als **zugelassen**, nicht bevorzugt, mit dem Hinweis auf die
  nicht amtliche Übersetzung –, keine Sätze und keine Satzpaare. Für andere Zielsprachen kommen nur die deutschen
  Begriffe mit Definition; die Entsprechung klärt dann die Recherche.
- **Eigene Dokumente einarbeiten** (ebenfalls unter **Einarbeiten**): Die Liste zeigt die Dateien, die unter
  **TM & Terminologie → Eigene Fachtexte** eingelesen sind (PDF, Word, Writer, Text …); angehakt wird, was eingearbeitet
  werden soll, dazu das Sprachpaar der Terminologie. **Begriffe herausziehen** sucht die Begriffe, die dort erklärt
  werden:
  - **Definitionssätze**: „Unter einer Gleitringdichtung versteht man …“, „Als Kavitation bezeichnet man …“,
    „Der Begriff Förderhöhe bezeichnet …“, „Der Spaltring ist ein …“, „Kreiselpumpen sind Strömungsmaschinen, die …“,
    englisch „X is a …“, „X means …“, „The term X refers to …“, polnisch „X to …“, „X oznacza …“. Satzanfänge wie
    „Ziel ist eine …“ oder „Das Ergebnis ist ein …“ zählen nicht.
  - **Abkürzungen**: „Gleitringdichtung (GLRD)“, „Bundesamt für Sicherheit in der Informationstechnik (BSI)“ – die
    Langform wird zur Benennung, die Abkürzung zur zugelassenen zweiten Benennung.
  - **Glossarlisten**: Zeilen „Laufrad: rotierendes Bauteil …“ (ab drei solchen Zeilen oder nach einer Überschrift
    wie „Begriffe“, „Glossar“, „Terms and definitions“) und der Normstil „3.1 Saugstutzen“ mit der Erklärung in der
    nächsten Zeile.
  - Mit **KI liest mit** sucht zusätzlich die KI nach erklärten Begriffen. Ihr Vorschlag zählt nur, wenn der
    Belegsatz **Wort für Wort** im Abschnitt steht und den Begriff enthält – erfundene Sätze fallen heraus. Online-KI
    liest die Dokumente nur, wenn das bei den eigenen Fachtexten erlaubt ist; sonst gelten nur die Regeln.

  Als Definition gilt immer der Satz aus dem Dokument. Jeder Begriff zeigt Datei und Seite, wie oft er in allen
  eigenen Fachtexten vorkommt und woher er stammt; **Öffnen** zeigt das Dokument an der Fundstelle (bei PDF auf der
  Seite). Gespeichert wird erst mit **Übernehmen**, und nur, was angehakt ist: in die gemeinsame Terminologie des
  Sprachpaars (Begriffe in einer der beiden Sprachen, Definition mit Fundstelle) und in die oben gewählte
  Wissensdatenbank (ohne Auswahl „Wissen“ der Ausgangssprache) – nichts doppelt.
- **Thema → TM (EU-Recht)** (ebenfalls unter **Einarbeiten**): Thema und Sprachpaar angeben, z. B. „Maschinen“,
  de-DE → en-GB, und **Rechtsakte suchen**. Das Programm sucht über die SPARQL-Schnittstelle des Amts für
  Veröffentlichungen Rechtsakte der EU, deren Titel in der Ausgangssprache zum Thema passt und die es auch in der
  Zielsprache gibt. Wörter werden auf ihren Stamm gekürzt („Maschinen“ findet auch „Maschinenrichtlinie“), mehrere
  Wörter müssen alle im Titel stehen, ein Komma trennt Alternativen („Maschinen, Aufzüge“); eine CELEX-Nummer wie
  32023R1230 sucht genau diesen Rechtsakt. Oben stehen Verordnungen und Richtlinien, neuere vor älteren, Änderungs-
  und Durchführungsakte weiter unten; die drei ersten sind angehakt, **Öffnen** zeigt den Rechtsakt auf EUR-Lex.
  **TM aufbauen** lädt die amtlichen Fassungen beider Sprachen und paart sie: Jede Sprachfassung ist verbindlich
  und gleich gegliedert, deshalb wird Absatz für Absatz gepaart – aber nur, wenn beide Fassungen gleich viele
  Absätze gleicher Art haben, sonst wird der Rechtsakt übersprungen. Innerhalb eines Absatzes werden Sätze gepaart,
  wenn beide gleich viele haben, sonst der ganze Absatz (bis 600 Zeichen). Fußnoten, bloße Überschriften wie
  „Artikel 46“ und Paare mit abweichenden Zahlen fallen weg. Aus der Maschinenverordnung entstehen so gut 1800
  Satzpaare. Sie kommen mit der **CELEX-Nummer als Herkunft** und den Nachbarsätzen als Kontext in eine eigene TM
  („TM Maschinen (EU-Recht) de-DE–en-GB“, Name änderbar), die an das Sprachpaar gebunden ist und damit in jedem
  Projekt mit diesen Sprachen Treffer liefert; bei offenem Projekt mit passendem Sprachpaar wird sie gleich
  angebunden. Nichts wird doppelt angelegt. Weiterverwendung ist erlaubt (Beschluss 2011/833/EU), Quelle: EUR-Lex,
  © Europäische Union; verbindlich ist nur die im Amtsblatt veröffentlichte Fassung. Es geht nur mit Amtssprachen
  der EU. Für sehr allgemeine Bestände (alle EU-Übersetzungen eines Sprachpaars) gibt es die großen OPUS-Speicher
  (**Freie Sprachdaten herunterladen**); „Thema → TM“ liefert dagegen gezielt die aktuellen Rechtsakte eines
  Fachgebiets.
- **Team-Aktivität**: wer im Team zuletzt was beigetragen hat (nur mit Teamserver).

Alle Agenten laufen im Hintergrund weiter, wenn man den Reiter oder das Projekt wechselt: Sie arbeiten mit eigenen
Verbindungen zu ihren Datenbanken und speichern ihr Ergebnis in die Datenbank, mit der sie gestartet wurden. Die
Statusleiste meldet, wann sie fertig sind; während sie auf das Modell warten, zeigt das Protokoll alle 20 Sekunden
„⏳ Warte auf …“. Beim Schließen des Programms brechen sie ab.

## KI einrichten

Alles rund um die KI steht auf der eigenen Seite **KI einrichten** (links in der Leiste): einen Weg wählen,
**Verwenden** klicken – das Programm legt den Zugang an, testet ihn und nutzt ihn (auf Wunsch) für Übersetzen,
Recherche und Bewertung. Dort stehen auch die Zugänge zum Bearbeiten, **Wer macht was** und die **Websuche**.

- **Kostenlos – mehrere Modelle gleichzeitig** (ganz oben): **Angehakte Modelle gleichzeitig verwenden** sucht alle
  kostenlosen Modelle ohne Konto (OVHcloud, Kilo), dazu die eingebaute KI (wenn geladen) und schon eingerichtete
  kostenlose Zugänge (OpenCode, lokale Server), hakt die empfohlenen an, prüft jedes gleichzeitig mit einer kurzen
  Übersetzung und richtet aus allen, die antworten, einen **Verbund** ein. Mit **Kostenlose Modelle suchen** lässt
  sich die Auswahl vorher anpassen. Der Verbund verteilt eine ganze Datei auf alle Modelle gleichzeitig (Editor →
  KI → alle übersetzen); stößt ein Modell an die Mengenbegrenzung seines Anbieters, pausiert es so lange wie
  verlangt und ein anderes übernimmt, fällt eines aus, versucht dasselbe Segment das nächste. Im Test übersetzte ein
  Verbund aus fünf Modellen 12 Segmente in etwa 50 Sekunden – ein einzelnes OVHcloud-Modell bräuchte wegen der
  Grenze von 2 Anfragen pro Minute rund sechs Minuten. Weil kostenlose Modelle gern etwas hinzudichten, prüft das
  Programm jede Übersetzung: Hat sie mehr Sätze als das Original oder ist sie auffällig länger, übersetzt ein anderes
  Modell des Verbunds das Segment noch einmal. Dienste mit kostenlosem Schlüssel (NVIDIA, Groq, Gemini, Mistral,
  Cerebras, OpenRouter) kommen dazu, sobald ihr Schlüssel unter **Kostenlos online** eingetragen und verbunden ist.

- **Kostenlos auf diesem Rechner** (eingebaute KI, Windows, macOS, Linux): Ein offenes Sprachmodell (Gemma 4 von
  Google oder EuroLLM, beide Apache-2.0) läuft mit der Laufzeit llama.cpp direkt auf dem Rechner – ohne Konto, ohne
  Schlüssel, ohne Tageslimit, und die Texte verlassen den Rechner nicht. Zur Wahl stehen **Gemma 4 E2B** (2,8 GB,
  schnell, auch ohne Grafikkarte), **Gemma 4 E4B** (4,6 GB, bessere Übersetzungen), **Gemma 4 12B** (7,2 GB, die
  genauesten Übersetzungen der eingebauten KI; braucht eine Grafikkarte mit mindestens 10 GB, auf einer 12-GB-Karte
  liegt es ganz im Grafikspeicher und braucht pro Satz etwa dreimal so lange wie E4B) und **EuroLLM 9B** (5,6 GB): ein
  Modell europäischer Universitäten und Forschungseinrichtungen, trainiert für alle 24 Amtssprachen der EU und vor
  allem aufs Übersetzen ausgelegt. Es belegt etwa 7 GB Grafikspeicher (die Karte sollte also mindestens 8 GB haben)
  und braucht pro Satz etwa doppelt so lange wie E4B; auf dem Prozessor allein ist es sehr langsam. Im Test mit 16
  Sätzen (chrF gegen je eine Referenz) war EuroLLM ins Polnische (73 statt 70 Punkte) und ins Deutsche (79 statt 78)
  besser als Gemma 4 E4B, ins Englische (75 statt 79) und ins Französische (72 statt 81) schwächer. Gemma 4 12B war
  im selben Test in allen vier Richtungen am besten: im Mittel 79,6 Punkte gegenüber 76,9 (E4B) und 74,7 (EuroLLM),
  am deutlichsten ins Deutsche (82 statt 78) und ins Polnische (73 statt 70); mit belegten Recherche-Dossiers 79,5.
  Wer eine Grafikkarte mit mindestens 10 GB hat, nimmt also das 12B; sonst E4B, für Polnisch EuroLLM.
  **Herunterladen und verwenden** lädt
  Laufzeit und Modell einmalig aus den offiziellen Quellen (GitHub, Hugging Face) und prüft ihre Prüfsummen; ein
  abgebrochener Download geht beim nächsten Mal an derselben Stelle weiter. Mit **Ordner …** lässt sich ein
  Laufwerk mit mehr Platz wählen; **Modell löschen** gibt den Platz wieder frei. Eine Grafikkarte wird genutzt,
  wenn vorhanden. Bei neuen Installationen ist die eingebaute KI voreingestellt: Der Editor zeigt dann einen
  Hinweis mit **Jetzt herunterladen**, bis sie bereitsteht. Kleine lokale Modelle übersetzen solide, erreichen
  aber nicht die Qualität großer Cloud-Modelle – für anspruchsvolle Texte das größere Modell oder einen anderen
  Zugang wählen.
- **Kostenlos über OpenCode**: Das Programm startet OpenCode im Hintergrund und bietet dessen kostenlose Modelle
  an (kostenlose zuerst, mit Hinweis zur Datennutzung). Ist OpenCode nicht installiert, lädt **OpenCode
  herunterladen** es aus dem offiziellen Release (MIT-Lizenz, etwa 80 MB). Die Anfragen laufen über OpenCode
  selbst, wie es OpenCode für seine kostenlosen Modelle vorschreibt; ein Konto ist nicht nötig. Die kostenlosen
  Modelle haben ein Tageskontingent je Internetanschluss; ist es aufgebraucht, sagt das Programm, ab wann es
  wieder geht.
- **Lokal auf diesem Rechner**: **Suchen** findet Ollama, LM Studio, llama.cpp und ähnliche Server automatisch,
  samt ihrer Modelle. Kostenlos und vertraulich – die Texte verlassen den Rechner nicht.
- **Kostenlos online**: Ohne Anmeldung gehen **OVHcloud AI Endpoints** (Rechenzentren in der EU, ohne Schlüssel
  2 Anfragen pro Minute und Modell; bewährt: gpt-oss-120b) und das **Kilo Gateway** (kostenlose Modelle, etwa 200
  Anfragen pro Stunde; die Anbieter dahinter dürfen protokollieren): Dienst wählen, **Verwenden** (verbindet bei
  Bedarf selbst).
  Mit kostenlosem Schlüssel außerdem OpenRouter (Modelle mit „:free“), Google Gemini, Groq, Mistral, NVIDIA NIM und
  Cerebras – **Schlüssel holen** öffnet die Seite des Anbieters. Stößt ein Dienst an seine Mengenbegrenzung, wartet
  das Programm kurz und versucht es erneut. Kostenlose Cloud-Stufen speichern Eingaben teilweise – nicht für
  vertrauliche Texte.

Von Hand geht es unter **Erweitert: KI-Zugänge von Hand bearbeiten**: Vorlage wählen (Claude, lokal,
OpenAI, Mistral, OpenRouter, OpenCode, eigener Server …), Adresse und Modell anpassen, testen, speichern.

- API-Schlüssel können als `env:NAME` eingetragen werden (z. B. `env:ANTHROPIC_API_KEY`); dann steht der
  Schlüssel nicht in der Einstellungsdatei.
- **Wer macht was**: für Übersetzen, Recherche und Bewertung jeweils einen eigenen Zugang wählen –
  z. B. lokal übersetzen, in der Cloud recherchieren.
- **Lokal oder Cloud**: Die Einstellungen zeigen bei jedem Zugang, ob die Texte den Rechner verlassen.
  Für vertrauliche Aufträge einen lokalen Zugang nutzen.
- **Websuche für die Agenten**: **mehrere Dienste gleichzeitig ankreuzbar** – **Wikipedia** (kostenlos, ohne
  Schlüssel, Standard bei neuen Installationen), eine **SearXNG**-Metasuche (eigener Server oder mit einem Klick auf
  diesem Rechner, siehe unten), **Tavily**, **Brave Search** und die **Claude-Websuche**, jeder mit eigener Adresse
  bzw. eigenem Schlüssel. Wie ein Mensch, der in verschiedenen Quellen nachschlägt, fragt die Recherche alle
  angekreuzten gleichzeitig, mischt die Treffer abwechselnd und nimmt doppelte Seiten nur einmal; fällt einer aus
  (kein Netz, Docker angehalten, Kontingent aufgebraucht), suchen die anderen weiter. **Websuche testen** prüft jeden
  Dienst einzeln. Dienste ohne Schlüssel bzw. Adresse fallen weg; bleibt keiner übrig, sucht das Programm in
  Wikipedia. Für Brave und Tavily führt **Schlüssel holen**
  zur Anmeldung beim Anbieter; beide haben ein kostenloses monatliches Kontingent (Tavily: 1000 Abfragen ohne
  Kreditkarte; Brave: 5 $ Guthaben, etwa 1000 Suchen, Kreditkarte nötig). Erst mit einer solchen Suche kann die KI
  gezielt auf einzelnen Fachseiten suchen. Lokale Modelle können nicht selbst suchen; das übernimmt dieser Dienst.
- **Kostenlose Websuche auf diesem Rechner (SearXNG)**: Ein Klick richtet alles ein. Das Programm prüft, ob Docker
  installiert ist und läuft (sonst startet es Docker Desktop), richtet die Metasuche SearXNG in einem Container ein –
  nur über `127.0.0.1:8888` erreichbar, also nicht aus dem Netz –, stellt die Websuche darauf um und testet sie. Fehlt
  Docker, lädt das Programm unter Windows Docker Desktop aus der offiziellen Quelle (etwa 600 MB, braucht etwa 6 GB)
  und installiert es nur für diesen Benutzer; Programm und Daten kommen unter den angezeigten **Docker-Ordner**
  (Vorgabe: neben der eingebauten KI, damit das Systemlaufwerk frei bleibt). Mit dem Klick auf **Docker installieren
  und Websuche einrichten** stimmst du der Docker-Lizenz zu: kostenlos für Privatleute, Ausbildung und Firmen unter
  250 Mitarbeitern und 10 Mio. $ Umsatz, größere Firmen brauchen ein Abo. Voraussetzung ist WSL 2; fehlt es, einmalig
  mit Administratorrechten `wsl --install` ausführen und neu starten. Unter macOS und Linux öffnet sich die
  Docker-Anleitung; danach noch einmal klicken. Docker selbst darf nicht mit dem Programm weitergegeben werden und
  wird deshalb erst auf Knopfdruck geladen. Ist die Instanz angehalten oder Docker beendet, startet die nächste Suche
  sie von selbst. **Anhalten**, **Entfernen** (Container und Abbild; Docker bleibt) und **Zustand prüfen** stehen
  daneben. Startet Docker nicht (etwa wenn die Installation nicht vollständig war), erscheint **Docker neu
  installieren**: Es lädt Docker noch einmal aus der offiziellen Quelle und richtet danach weiter ein. SearXNG fragt
  Suchmaschinen wie Google, Bing oder DuckDuckGo ab – deren Bedingungen gelten, und sie lehnen Anfragen zeitweise ab;
  am besten zusammen mit Tavily oder Brave ankreuzen.
- Für kleine lokale Modelle **Zeichen pro Webseite** auf etwa 2000–3000 senken.

### Maschinelle Übersetzung (MÜ-Dienste)

Auf der Seite **KI einrichten** unter **Maschinelle Übersetzung**: DeepL, Google Cloud Translation, Microsoft Translator
oder das freie LibreTranslate (auf dem eigenen Rechner oder Server) wählen, Schlüssel einfügen (bei Microsoft die Region,
bei LibreTranslate die Adresse) und **Zum Übersetzen verwenden** – der Dienst wird kurz getestet und für das Übersetzen
gewählt. Recherche, Prüfung und Agenten nutzen weiter das Sprachmodell, denn ein MÜ-Dienst kann nur übersetzen. Tags gehen
als XML an den Dienst und kommen unverändert zurück; Datenschutz gilt wie bei der KI (Platzhalter). Anweisungen,
Terminologie und Kontext versteht ein MÜ-Dienst nicht – die Terminologie-Prüfung der QA zeigt, wo nachzubessern ist.

**Offline auf diesem Rechner (OPUS-MT):** kostenlos, ohne Schlüssel und ohne Grafikkarte, auch auf Android. Je
Sprachrichtung lädt das Programm einmalig ein Übersetzungsmodell der Universität Helsinki (rund 300 MB) und prüft
dessen Prüfsummen; danach verlässt beim Übersetzen kein Text den Rechner. Unter **Von** und **Nach** die Richtung wählen
(vorbelegt aus dem offenen Projekt) – die Anzeige sagt, ob es ein direktes Modell gibt, ob der Weg über Englisch geht
(zwei Modelle, schwächer) oder ob es keines gibt, und wie groß der Download ist. **Modell herunterladen** oder gleich
**Zum Übersetzen verwenden**; geladene Modelle lassen sich einzeln **entfernen**. Sie liegen neben der eingebauten KI
(Ordner dort wählbar). Rund 300 Richtungen zwischen europäischen Sprachen, dazu Chinesisch und Japanisch.

Zur Einordnung (eigener Test mit 16 Sätzen aus Vertrag, Technik, Brief, Medizin und Software): ein Satz in etwa
0,3 Sekunden auf dem Prozessor, Qualität ungefähr wie die eingebaute KI Gemma 4 E2B, im Polnischen besser. Auf einem
Rechner mit Grafikkarte übersetzt die eingebaute KI mit Gemma 4 E4B besser und hält sich an Terminologie und Anweisungen.
Typische Schwächen: Redewendungen werden wörtlich übersetzt, Fachbegriffe manchmal falsch, „Sie“ und die Anrede folgen
keiner Vorgabe. Als schneller Entwurf zum Nachbearbeiten gedacht.

### Diktat einrichten

Unter **Diktat** eine Spracherkennung wählen: **Groq** (kostenloser Schlüssel, sehr schnell), **OpenAI** oder **lokal**
(Whisper auf dem eigenen Rechner, z. B. speaches mit faster-whisper – die Aufnahme verlässt das Haus nicht). Adresse,
Modell und Schlüssel werden vorgeschlagen; **Für das Diktat verwenden** speichert.

## Agenten

Reiter **Agenten** (die meisten Funktionen gibt es auch direkt im Editor unter **KI**):

- **Recherche (KI steuert)** – eine Aufgabe oder Frage in eigenen Worten, z. B. „Wie heißt ‚Abkantpresse‘
  (Blechbearbeitung) auf Polnisch? Mit Beleg.“ Die KI entscheidet selbst Schritt für Schritt: in den
  Projekt-Datenbanken (TM, Terminologie, Wissen, frühere Recherchen) nachschlagen, in **Wikipedia**, **Wiktionary**
  oder im Web suchen, einen Artikel samt seinen Sprachversionen oder eine Webseite lesen, einen **Skill** anwenden,
  sich etwas **merken** – das Programm führt jeden Schritt aus und liefert ihr die Inhalte. Wikipedia-Treffer
  enthalten gleich die Titel in den Zielsprachen, oft schon der gesuchte Fachbegriff. Ergebnis: Antwort mit Quellen;
  alle Schritte lassen sich nachlesen. Modelle mit Function Calling (Online-Dienste, eingebaute KI) rufen die
  Werkzeuge direkt auf, andere bekommen ein einfaches Protokoll (auch automatisch, wenn ein Dienst auf
  Werkzeug-Anfragen nicht antwortet). Die Antwort landet auf Wunsch im Gedächtnis (Projekt-Wissen; ohne Projekt
  ein allgemeines KI-Gedächtnis).

  **Beleg-Prüfung:** Nach jeder Recherche prüft das Programm selbst – nicht die KI –, ob der vorgeschlagene Begriff
  (Übersetzungsvorschlag bzw. die hervorgehobenen Begriffe der Antwort) wirklich in einer gelesenen Quelle steht:
  Wikipedia-Artikel samt Sprachversionen, Webseiten, Gesetze, Wörterbücher, Projekt-Datenbanken; gebeugte Formen
  zählen mit. Ergebnis: „✔ … steht in: …“ mit der Fundstelle oder „⚠ … kommt in keiner gelesenen Quelle vor –
  bitte selbst prüfen“. Hinweise des Programms selbst gelten nicht als Beleg.

  Dazu kommen **amtliche Quellen**, die die KI direkt über deren Schnittstellen abfragt:

  | Werkzeug | Quelle |
  |---|---|
  | Gesetze (`law`) | Deutschland: Bundesrecht auf gesetze-im-internet.de (auch Einzelnormen wie „§ 35 VwVfG“); Polen: Rechtsdatenbank des Sejm (Dziennik Ustaw, Monitor Polski, konsolidierte Texte zuerst); Vereinigtes Königreich: legislation.gov.uk; EU: EUR-Lex in allen Amtssprachen |
  | Wörterbuch (`dictionary`) | Deutsch: DWDS (Eintrag, gültige Schreibung, Wortart, Häufigkeitsklasse 0–6, Aussprache); alle Sprachen: Wiktionary; dazu die nationalen Wörterbücher aus dem Profil „Sprache“ |
  | Fachquellen (`sources`) | die Fachseiten je Thema und Sprache samt Rechtsordnung bzw. Fachsystem (siehe *Fachquellen-Profile*) |
  | Seitensuche (`search` mit `site`) | Websuche nur auf einer Domain oder auf allen Fachseiten eines Themas – braucht Brave, Tavily, SearXNG oder Claude |
  | Fachquelle merken (`add_source`) | neu gefundene amtliche Seiten nimmt die KI ins passende Profil auf |

  Lange Texte (Gesetze, Urteile) liest die KI gezielt: Beim Öffnen einer Seite oder eines PDFs mit einem
  Suchwort bekommt sie nur die Stellen, an denen es vorkommt – auch in gebeugter Form. Dieselbe EU-Norm in zwei
  Sprachen ist die amtliche Entsprechung eines Begriffs; EUR-Lex-Texte liest das Programm über die
  Dokumentenschnittstelle des Amts für Veröffentlichungen, weil die EUR-Lex-Seiten Programme abweisen.

  **Rechtsbegriffe** vergleicht die KI zwischen beiden Rechtsordnungen: Sie verankert den Begriff in der Norm des
  Ausgangsrechts, liest das Gegenstück im Zielrecht, vergleicht Umfang, Voraussetzungen und Rechtsfolgen und
  schlägt eine Übersetzung mit Strategie vor. Das Ergebnis zeigt neben der Antwort einen **Übersetzungsvorschlag**
  und eine **Anmerkung des Übersetzers** in der Zielsprache – zum Übernehmen als Fußnote oder „Anm. d. Übers.“.
  Beispiel: „Verwaltungsakt (§ 35 VwVfG) ins Polnische“ ergibt „decyzja administracyjna“ mit dem Hinweis, dass
  der deutsche Begriff weiter ist (Allgemeinverfügung, Maßnahmen ohne förmliches Verfahren).
- **Skills & Gedächtnis** – **In den Datenbanken nachsehen** sucht wie der Agent in TM, Terminologie, Wissen und
  Gedächtnis, z. B. ob eine Formulierung schon übersetzt oder ein Begriff schon recherchiert wurde.
  **Gespeicherte Recherchen ansehen** öffnet das Gedächtnis im Reiter **Wissen**.
  Skills sind bewährte Vorgehensweisen im offenen SKILL.md-Format (Markdown):
  mitgeliefert sind „Fachbegriff recherchieren“, „Usus prüfen“, „Rechtsbegriff vergleichen“, „Eigennamen
  prüfen“ und „Amtliche Quellen finden“. Passt ein Skill eindeutig zur Aufgabe, bekommt die KI sein Vorgehen
  gleich zu Beginn. Neuere Fassungen der mitgelieferten Skills ersetzen ältere, solange man sie nicht selbst
  geändert hat. Nach erfolgreichen, mehrstufigen Recherchen legt die KI selbst neue Skills an („gelernt“) – nach dem
  Vorbild selbstlernender Agenten wie Hermes. Alle Skills lassen sich lesen, ändern, neu anlegen und löschen; sie
  liegen als Dateien im Benutzerordner unter `skills`.
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
  Alternativen, **liest die gefundenen Seiten** und holt den Satz, in dem die Wortfolge steht, zählt wörtliche Belege
  und urteilt anhand der Belege. Reichen sie nicht (weniger als drei), sagt er das und sucht weiter – erst mit dem
  Fachgebiet, dann mit eigenen Ideen der KI (typische Zusammenhänge, Textsorten wie Anleitung, Datenblatt, Norm,
  Forum). Das Ergebnis beginnt mit einer klaren Aussage aus den gezählten Belegen, z. B. „„Drehkraft anziehen“ ist in
  Deutsch nicht üblich: kein wörtlicher Beleg in 12 Treffern. Üblich ist „Anzugsdrehmoment“: 3 wörtliche Belege
  ([1], [2], [3]).“ Ohne einen einzigen Beleg gilt ein Ausdruck nie als üblich, auch wenn die KI das meint. Jeder
  Beleg steht mit Nummer, dem Satz von der Seite und einem Link, der im Browser direkt zu diesem Satz springt; ließ
  sich eine Seite nicht lesen, steht der Ausschnitt aus der Trefferliste da, als solcher gekennzeichnet.
- **Übersetzungen bewerten** – mehrere Varianten nach Richtigkeit, Sprachfluss, Terminologie und Stil benoten,
  mit Begründung und Verbesserungsvorschlag; fragliche Wendungen auf Wunsch im Netz prüfen.

Das **Protokoll der Agenten** zeigt jeden Schritt: Suchanfragen, gelesene Seiten, übersprungene Seiten.
KI-Ergebnisse sind Vorschläge – die fachliche Verantwortung bleibt bei Ihnen.

## Fachquellen-Profile

Unter **Einstellungen → Fachquellen je Thema** steht je Thema (Recht, Medizin, Technik, Sprache, EU und
Verwaltung – frei erweiterbar) eine Zeile pro Sprache:

`Sprache | System | Domains, mit Komma getrennt`

Beispiel: `de-DE | deutsches Recht (BGB, HGB, ZPO, StGB …) | gesetze-im-internet.de, dejure.org`.
`*` gilt für alle Sprachen. Die Agenten suchen zuerst auf diesen Seiten und erst danach allgemein.
**Fachseiten prüfen** testet, ob die Seiten das Auslesen erlauben und Text liefern. Seiten, die automatisches
Auslesen untersagen oder technisch verhindern, gehören nicht in die Liste – das Programm umgeht solche Sperren nicht.

Das Profil **Sprache** enthält Wörterbücher und Sprachberatung je Sprache (z. B. DWDS, Duden, grammis für Deutsch;
PWN, WSJP, Rada Języka Polskiego und NKJP für Polnisch; Cambridge, Oxford, Merriam-Webster für Englisch). Einige
davon erscheinen nur in Suchergebnissen, ihre Seiten liest das Programm nicht: DWDS und Larousse haben einen
TDM-Vorbehalt, Duden ist für Programme gesperrt, die englischen Wörterbücher weisen Programme ab. Das DWDS fragt
die KI stattdessen über seine offizielle Schnittstelle ab.

Die KI kann selbst neue amtliche Seiten ins passende Profil aufnehmen (im Protokoll als „Neue Fachquelle“).
Wo robots.txt einen Mindestabstand zwischen zwei Abrufen verlangt (Crawl-delay), hält das Programm ihn ein.

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

### Arbeitsschritte: Übersetzung, Lektorat, Freigabe

Jede Datei eines gemeinsamen Projekts durchläuft je Zielsprache **Übersetzung → Lektorat → Freigabe → fertig**. Im Reiter
**Projekt** unter **Arbeitsschritte im Team** legt die Verwaltung des Teamservers fest, wer welchen Schritt erledigt, und
kann den Schritt setzen. Bearbeiten darf, wer an der Reihe ist; für alle anderen ist die Datei im Editor nur lesbar (mit
Hinweis, bei wem sie liegt). **Abgeben** gibt die Datei an den nächsten Schritt weiter. Ohne Zuweisung bleibt alles wie
bisher: Jeder mit Schreibrecht darf bearbeiten. Der Server prüft das selbst; ältere Programmversionen und vorhandene
Projekte arbeiten unverändert weiter. Dieselben Regeln gelten für die REST-Schnittstelle (siehe API-Referenz).

## Automatisierung: überwachte Ordner

Unter **Einstellungen → Automatisierung** Eingangsordner anlegen: Ordner, Projekt, Ausgangsordner (leer = Unterordner
„fertig“) und was geschehen soll – **aus dem TM vorübersetzen**, **leere Segmente per KI** (als Entwurf), **Zieldateien
ausgeben**. Neue Dateien im Eingangsordner (fertig geschrieben, unterstütztes Format) kommen alle 10 Sekunden ins Projekt,
die Zieldateien je Zielsprache in den Ausgangsordner, das Original nach „erledigt“. Bestätigt wird nichts – die Übersetzung
bleibt zum Prüfen im Projekt. Das Protokoll steht darunter; **Jetzt prüfen** sieht sofort nach.

## Pakete und Austausch

- **.sdlppx** öffnen: legt ein Projekt mit allen Dateien, TMs und Terminologien des Pakets an.
- **.sdlrpx** erzeugen: Rückgabepaket mit den übersetzten zweisprachigen Dateien.
- **XLIFF** je Datei (Schaltfläche in der Dateiliste) für den Austausch mit anderen Programmen.
- **TMX, TBX, .sdltm** unter **TM & Terminologie**.

Fremde Formate werden nur zur Zusammenarbeit gelesen und geschrieben.

### Prüfdatei für das Lektorat in Word

Für Prüfer ohne Übersetzungsprogramm: **Prüfdatei** neben einer Datei erzeugt eine Word-Tabelle (Querformat) mit Nummer,
Ausgangstext, Übersetzung, Status und Anmerkung; Tags stehen grau im Text. Der Prüfer bearbeitet nur die Spalte
**Übersetzung** – gern mit der Änderungsverfolgung von Word – und schreibt Anmerkungen in die letzte Spalte oder als
Word-Kommentar. **Prüfdatei zurückholen …** liest die Datei wieder ein (sie wird über eine Kennung der richtigen Datei und
Sprache zugeordnet): Änderungen werden als lektoriert übernommen und ins TM geschrieben, die vorige Fassung bleibt im
Verlauf und in der Änderungsverfolgung, Anmerkungen und Word-Kommentare kommen in die Segmentkommentare, verlorene Tags
werden gemeldet. **Unveränderte als lektoriert** markiert auch die nicht beanstandeten Segmente.

## Schnittstelle (API)

Alle Funktionen sind über eine REST-Schnittstelle erreichbar – für eigene Skripte, Automatisierung und andere
Programme. Es gibt sie an zwei Stellen:

- **In der Desktop-App**: **Einstellungen → Schnittstelle (API)** einschalten, Port wählen, Zugangsschlüssel
  erzeugen, **Übernehmen**. Sie ist nur auf diesem Rechner erreichbar (127.0.0.1) und arbeitet mit dem Projekt,
  das im Fenster offen ist; Änderungen erscheinen sofort im Editor.
- **Im Teamserver**: immer an, mit den Benutzern und Rollen des Servers.

Anmeldung mit der Kopfzeile `Authorization: Bearer <Schlüssel>`. Die maschinenlesbare Beschreibung (OpenAPI)
steht unter `/openapi/v2.json`. Alle Endpunkte und Beispiele: Kapitel „API-Referenz“.
Für C# gibt es dieselbe Schnittstelle als .NET-SDK (`SententiamClient`), siehe ebenda.

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
Erweiterungen werden z. B. in C# geschrieben und haben über `context.Api` Zugriff auf alle Funktionen des Programms.

## Datenschutz und Rechtliches

### Vertrauliche Angaben schützen (Anonymisierung)

Im Reiter **Projekt** unter **Datenschutz für dieses Projekt** (ohne Projekt: Voreinstellung für Recherchen und neue
Projekte):

- **Vertraulich**: Geschützte Angaben gehen nur als Platzhalter an eine Online-KI – aus „Herr Müller hat am
  12.03.2024 unter Az. 2 O 123/24 …“ wird „Herr [NAME_1] hat am [DATE_1] unter Az. [CASE_1] …“. Die KI übersetzt mit
  Platzhaltern, das Programm setzt die echten Werte in der Antwort wieder ein. Das gilt für alles, was an eine KI geht:
  Übersetzen, Recherche, Prüfen, Bewerten, Alignment. Suchanfragen der Recherche-KI bleiben maskiert, damit keine Namen an
  Suchmaschinen gehen. KI auf diesem Rechner (eingebaute KI, Ollama …) bekommt die Texte unverändert, außer man
  wählt **auch an KI auf diesem Rechner nur Platzhalter senden**. Das Protokoll meldet, wie viele Angaben ersetzt wurden.
- **Nicht ins TM**: Ins Projekt-TM und in gemeinsame TMs kommen nur Fassungen mit Platzhaltern, auch im gespeicherten
  Satzzusammenhang. Ein späterer Treffer bekommt die Werte des aktuellen Segments („Mr [NAME_1] signs on [DATE_1].“ wird
  bei „Herr Schmidt … 01.04.2025“ zu „Mr Schmidt signs on 01/04/2025.“). **TM anonymisieren** bereinigt vorhandene Einträge.
- **Datum in der Zielsprache**: Weil die KI ein Datum nur als [DATE_1] sieht, schreibt das Programm es selbst in die
  Form der Zielsprache – „3. April 2024“ wird englisch (UK) „3 April 2024“, amerikanisch „April 3, 2024“, polnisch
  „3 kwietnia 2024“, „12.03.2024“ englisch (UK) „12/03/2024“.
- **Namen richtig beugen**: Die KI kennt den Namen nicht, erfährt aber aus der Anrede das grammatische Geschlecht
  („Herr“, „Frau“, „Pan“, „Pani“ …) und hängt Endungen an den Platzhalter an – so wird „von Herrn Müller“ polnisch
  „pana Müllera“.
- **Vorschlag**: Enthalten neu hinzugefügte Dateien Namen, Aktenzeichen, IBAN oder E-Mail-Adressen, fragt das Programm
  einmal, ob das Projekt vertraulich behandelt werden soll (**Ja, vertraulich behandeln** / **Nein, nicht vertraulich**).
- **Was geschützt wird**: Namen nach Anrede oder Titel („Herr Müller“, „Frau Dr. Schmidt“, „Pan Kowalski“), Datum,
  Aktenzeichen („2 O 123/24“, „VI ZR 45/20“, „sygn. akt I C 123/24“, alles nach „Az.“), E-Mail, Telefon, IBAN,
  **Anschriften** („Bahnhofstraße 3a, 12345 Musterstadt“, „Hauptstr. 12“, „ul. Długa 5/3, 00-950 Warszawa“, „221B Baker
  Street“, eine Zeile nur aus Postleitzahl und Ort; Platzhalter [ADDRESS_1]) – einzeln abschaltbar – und die **Schutzliste**: Namen, Firmen, Orte, Kennzeichen, je Zeile einer. Im Editor nimmt **🔒 Schützen**
  markierten Text auf. Begriffe der Schutzliste bleiben in der Übersetzung unverändert (die KI sieht nur [TERM_1]) –
  Orte oder Firmen, die übersetzt werden sollen (Köln → Cologne), deshalb nicht aufnehmen.
- **Namen mit lokaler KI finden**: Eine KI auf diesem Rechner sucht in den Projektdateien Namen, ordnet jeden ein
  (Person, Anschrift, Ort, Firma, eigene Marke/Projekt, Produkt/Software, Einrichtung, Land) und schlägt vor, welche
  geschützt werden sollen – auch die schon in der Schutzliste stehen. In der Liste darunter angehakt = schützen;
  **Angehakte schützen** übernimmt (abgehakte werden aus der Schutzliste entfernt, andere Einträge bleiben). Geschützt
  werden sollte nur, was eine Person erkennbar macht und in der Übersetzung gleich bleibt: Namen, Anschriften, kleine
  Wohnorte, eigene Firmen und Projekte, kleine Arbeitgeber. Software, große Marken, Länder und Arten von Schulen oder
  Einrichtungen schützt das Programm nie – was übersetzt werden muss („Polen“ → „Polska“), käme sonst unübersetzt
  zurück. Der Text verlässt den Rechner dabei nicht. **Erkannte Angaben anzeigen** listet, was geschützt würde – jede
  Angabe mit Haken: Was nicht sensibel ist (etwa ein Datum in einer Norm), abhaken und **Abgehakte nicht schützen**; es
  kommt in die Liste **Nicht schützen** (je Zeile ein Wert, dort auch von Hand und zum Austragen) und wird nicht mehr
  ersetzt. Zusätzliche sensible Angaben kommen in die Schutzliste; die Schutzliste geht immer vor.
- **Nichts Geschütztes ins Netz**: Namen, Anschriften, E-Mail, Telefon, IBAN, Aktenzeichen und die Schutzliste sind nie
  ein Rechercheziel – auch wenn das Projekt nicht als vertraulich markiert ist. Ebenso wenig ein bloßes Datum oder eine
  Zahl („27. April 2016“, „03/2019“): das überträgt sich nach festen Regeln, eine Recherche kostet nur Zeit. Dazu
  prüft das Programm vor jeder Suchanfrage und jedem Seitenabruf, ob eine geschützte Angabe des offenen Dokuments darin
  steht (dazu Anschrift, E-Mail, IBAN und die Schutzliste auch sonst); dann wird die Anfrage nicht gestellt, und das
  Protokoll nennt den Grund. Das gilt auch für eine Suchmaschine auf diesem Rechner (SearXNG fragt ihrerseits
  Suchmaschinen im Netz). Ein Datum bleibt frei – Normen und Urteile werden mit Datum gesucht. Was unter
  **Nicht schützen** steht, bleibt frei. Was tatsächlich gesendet wurde, zeigt **KI einrichten** → **Datenausgang**.
- **🔒 am Segment**: Ist das Projekt vertraulich, trägt im Editor jedes Segment mit geschützten Angaben ein 🔒. Der
  Tooltip nennt jede Angabe mit ihrem Tag, z. B. „Name „Müller“ → [NAME_1]“ – den Platzhalter, den sie beim Anonymisieren
  und für die Online-KI bekommt, im ganzen Dokument gleich nummeriert.

Die Erkennung arbeitet mit Mustern und der Schutzliste; sie kann einzelne Angaben übersehen. Für streng vertrauliche
Aufträge zusätzlich eine KI auf diesem Rechner verwenden.

### Anonymisierte Fassung weitergeben

In der Dateiliste unter **Anonymisiert** → **Ausgangsdatei anonymisiert …** oder **Zieldatei anonymisiert …**: eine
Fassung ohne Namen, Daten, Aktenzeichen, E-Mail, Telefon, IBAN und Begriffe der Schutzliste, im ganzen Dokument
einheitlich ersetzt (derselbe Name überall [NAME_1]). Was erkannt wird, bestimmen die Datenschutz-Einstellungen des
Projekts; das Layout bleibt, das Projekt selbst ändert sich nicht. Die Erkennung kann einzelne Angaben übersehen –
vor dem Weitergeben durchsehen.

### Datenausgang: sehen und bestimmen, was den Rechner verlässt

Auf der Seite **KI einrichten** unter **Datenausgang** steht jede Anfrage an KI, maschinelle Übersetzung,
Spracherkennung, Websuche, Wörterbücher und Rechtsquellen – mit Dienst (Adresse), Zeit, Art, Größe und dem **genauen
Inhalt**, so wie er gesendet wird (geschützte Angaben also schon als Platzhalter, Schlüssel ausgeblendet). Das Protokoll
liegt nur im Arbeitsspeicher und ist nach einem Neustart leer.

- **Vor dem Senden**: *Nur festhalten* (Vorgabe), *beim ersten Senden an einen Dienst fragen* oder *vor jeder Anfrage
  fragen*. Die Rückfrage zeigt den Inhalt; **Senden**, **An diesen Dienst immer senden (bis zum Neustart)**, **Nicht
  senden** oder **Dienst sperren**. Bei abgelehnten Anfragen übernimmt im kostenlosen Verbund ein anderes Modell.
- **Gesperrte Dienste** bekommen nie etwas, auch ohne Rückfrage; **Wieder erlauben** hebt die Sperre auf.
- KI auf diesem Rechner oder im eigenen Netz wird nur festgehalten, nie gefragt. Abrufe von robots.txt enthalten keine
  Nutzerdaten und erscheinen nicht. Der eigene Teamserver ist nicht Teil des Protokolls.

### Ablaufprotokoll

Zum Nachvollziehen und für die Fehlersuche schreibt das Programm ein **Ablaufprotokoll** in den Datenordner (Unterordner
`protokoll`, eine Datei je Tag, `protokoll-JJJJ-MM-TT.jsonl`, eine Zeile je Eintrag):

- jede **Anfrage** an KI, maschinelle Übersetzung, Diktat, Suche und Online-Quellen – Dienst, genauer Inhalt wie im
  Datenausgang – und die **Antwort** dazu mit HTTP-Status, Inhalt und Dauer; Anfrage und Antwort tragen dieselbe
  Kennung (`id`). Große Downloads und Binärdaten nur mit Typ und Größe;
- die **Offline-Übersetzung** (Satz, Ergebnis, Modell, Dauer) und Start oder Absturz der eingebauten KI;
- jeden **Schritt der Oberfläche**: Reiterwechsel und jede Meldung, die das Programm anzeigt, Downloads, Abstürze.

Das Protokoll bleibt auf diesem Rechner, Schlüssel stehen nicht darin – wohl aber die Texte. Einstellung auf der Seite
**KI einrichten** unter **Datenausgang → Ablaufprotokoll**: ein- und ausschalten, **Ordner öffnen**, **Protokolle
löschen**. Protokolle älter als 14 Tage löscht das Programm beim Start.

### Allgemeines

- **KI in der Cloud**: Die Texte verlassen den Rechner. Vereinbarungen mit Kunden und die DSGVO beachten;
  für vertrauliche Aufträge den Datenschutz einschalten oder einen lokalen Zugang verwenden.
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
  recherche.json          Dossiers der Recherche
  recherche-ausdruecke.json gefundene Ausdrücke und Bewertung je Ausgangssatz
  meldungen/<id>.json     Meldungen der KI je Segment (Übersetzung, Recherche, Netzprüfung, Rückmeldung, KI-Prüfung)
  tm/projekt.sctm         Projekt-TM (SQLite)
  terminologie/projekt.sctb
  wissen/projekt.sckb     Projekt-Wissen
  paket/                  Originalpaket, falls das Projekt aus einem Paket stammt
```

Im Datenordner des Programms: `ai-settings.json` (KI, MÜ-Dienste, Diktat), `ui-settings.json` (Oberfläche, lokale API),
`servers.json` (Teamserver), `projektvorlagen.json`, `qa-profile.json`, `ueberwachte-ordner.json`, `woerterbuecher/`,
`erweiterungen/`.
