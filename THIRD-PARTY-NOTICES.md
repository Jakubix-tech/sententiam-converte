# Verwendete Fremdsoftware

Sententiam converte nutzt folgende Bibliotheken. Ihre Lizenzen gelten für den jeweiligen Teil.

| Bibliothek | Zweck | Lizenz |
|---|---|---|
| Avalonia (inkl. Themes.Fluent, Fonts.Inter, Desktop, Android) | Oberfläche | MIT |
| SkiaSharp, HarfBuzzSharp | Darstellung (über Avalonia) | MIT |
| CommunityToolkit.Mvvm | MVVM-Hilfen | MIT |
| AvaloniaEdit | Texteditor im Editor (Formatierung, Rechtschreib-Unterstreichung, Rückgängig) | MIT |
| Fluent UI System Icons (Microsoft) | Symbole der Oberfläche | MIT |
| DocumentFormat.OpenXml | DOCX, XLSX, PPTX lesen und schreiben | MIT |
| PdfPig | PDF lesen und übersetzte PDFs schreiben | Apache-2.0 |
| Noto Sans, Noto Serif (The Noto Project Authors) | Schrift für übersetzte PDFs; eingebettet werden nur die benutzten Zeichen | SIL Open Font License 1.1 (Lizenztext: OFL-Noto.txt) |
| AngleSharp | HTML lesen und schreiben | MIT |
| Microsoft.Data.Sqlite | Datenbank für TM und Terminologie | MIT |
| Microsoft.ML.Tokenizers, Google.Protobuf | Zerlegung in Wortteile (SentencePiece) für die Offline-Übersetzung | MIT / BSD-3-Clause |
| SQLitePCLRaw / SQLite | SQLite-Anbindung / Datenbank-Engine | Apache-2.0 / Public Domain |
| WeCantSpell.Hunspell | Rechtschreibprüfung (Portierung von Hunspell) | MPL 1.1 / GPL 2.0 / LGPL 2.1 (wahlweise); hier unter MPL 1.1 unverändert genutzt |
| Anthropic (C#-SDK) | Anbindung der Claude-API | MIT |
| ASP.NET Core, Microsoft.AspNetCore.OpenApi | Teamserver und REST-API (auch lokal in der Desktop-App) | MIT |
| AndroidX (Xamarin.AndroidX.*) | Start-Bildschirm auf Android | MIT / Apache-2.0 |
| xUnit, Avalonia.Headless | nur Tests, nicht ausgeliefert | Apache-2.0 / MIT |
| WiX Toolset 5 (inkl. WixUI, WixUtilExtension) | baut die Windows-Installationsdatei (.msi); in ihr stecken die Installationsdialoge und die Hilfe zum Starten nach der Installation | Microsoft Reciprocal License (MS-RL), https://github.com/wixtoolset/wix |

## Auf Wunsch geladene KI-Komponenten

Nicht in den Paketen enthalten. Erst wenn der Benutzer es ausdrücklich auswählt, lädt das Programm aus den
offiziellen Quellen und prüft die Prüfsummen (SHA-256, bei kleinen Dateien die Git-Prüfsumme):

| Komponente | Quelle | Lizenz |
|---|---|---|
| llama.cpp (llama-server) | github.com/ggml-org/llama.cpp, Release-Pakete | MIT |
| Gemma 4 E2B/E4B/12B (GGUF) | huggingface.co/ggml-org (Modelle von Google) | Apache-2.0 |
| EuroLLM 9B Instruct 2512 (GGUF, Q4_K_M) | huggingface.co/mradermacher, quantisiert aus huggingface.co/utter-project (Modell des EuroLLM-Projekts europäischer Universitäten und Forschungseinrichtungen) | Apache-2.0 |
| OpenCode | github.com/anomalyco/opencode, Release-Pakete | MIT |
| OPUS-MT-Übersetzungsmodelle (Marian) | huggingface.co/Helsinki-NLP – Language Technology Research Group, Universität Helsinki (J. Tiedemann, S. Thottingal: OPUS-MT – Building open translation services for the World, EAMT 2020); Gewichte im safetensors-Format aus der Konvertierung von Hugging Face | je Modell Apache-2.0 oder CC-BY-4.0 (im Programm angezeigt) |

Für die kostenlosen Modelle, die OpenCode anbietet, gelten die Bedingungen von OpenCode und der jeweiligen Anbieter.

## Auf Wunsch eingerichtete Websuche auf diesem Rechner

Nicht in den Paketen enthalten und nicht weitergegeben. Erst wenn der Benutzer unter **KI einrichten → Websuche** auf
„Websuche einrichten“ klickt, lädt das Programm aus den offiziellen Quellen und führt die Programme unverändert als
eigenständige Programme aus:

| Komponente | Quelle | Lizenz |
|---|---|---|
| Docker Desktop (nur Windows, nur wenn Docker fehlt) | desktop.docker.com, offizielles Installationsprogramm | Docker Subscription Service Agreement – kostenlos für Privatleute, Ausbildung und Firmen unter 250 Mitarbeitern und 10 Mio. $ Umsatz; die Zustimmung gibt der Benutzer mit dem Klick |
| SearXNG (Container-Abbild `searxng/searxng`) | hub.docker.com/r/searxng/searxng, Quelltext github.com/searxng/searxng | AGPL-3.0 |

SearXNG fragt andere Suchmaschinen ab; für deren Nutzung gelten deren Bedingungen.

## Auf Wunsch geladene Sprachdaten

Ebenfalls nicht enthalten. Auf ausdrücklichen Wunsch lädt das Programm frei verfügbare Translation Memories über die
offizielle Schnittstelle des OPUS-Archivs (opus.nlpl.eu, University of Helsinki) und die frei verfügbare
IATE-Gesamtausgabe (iate.europa.eu, verlinkt im EU-Datenportal). Für jeden Datensatz gilt dessen eigene Lizenz
(im Programm angezeigt, Einzelheiten auf der Korpusseite); bei den meisten ist die Quelle anzugeben.

Für Englisch–Französisch außerdem TERMIUM Plus aus den offenen Daten der Regierung von Kanada (open.canada.ca): enthält Informationen, die unter der Open Government Licence – Canada lizenziert sind.

## Wikipedia und Wiktionary

Die Agenten nutzen die offizielle MediaWiki-Schnittstelle (`/w/api.php`) der Wikimedia-Projekte mit einer eigenen
Kennung. Die Inhalte stehen unter CC BY-SA; in Antworten werden nur kurze Belege zitiert und die Quelle verlinkt.

## Amtliche Rechtsdatenbanken und DWDS

Die Recherche-KI fragt nur offizielle Schnittstellen ab, mit eigener Kennung und unter Beachtung von robots.txt
(einschließlich Crawl-delay) und TDM-Vorbehalten:

- **gesetze-im-internet.de** (Bundesministerium der Justiz): Inhaltsverzeichnis, Einzelnormen und – beim
  Einarbeiten eines Gesetzes – die XML-Gesamtfassung. Gesetze sind amtliche Werke (§ 5 UrhG). Die englischen
  Übersetzungen dort sind nicht amtlich und urheberrechtlich geschützt; übernommen werden daraus nur einzelne
  Benennungen aus Überschriften und Begriffslisten, keine Sätze.
- **ELI-Schnittstelle des Sejm** (api.sejm.gov.pl): Rechtsakte aus Dziennik Ustaw und Monitor Polski; Rechtsakte
  sind nach Art. 4 des polnischen Urheberrechtsgesetzes nicht geschützt.
- **legislation.gov.uk** (The National Archives): Atom-Feed der Rechtsakte; Open Government Licence v3.0, Crown
  Copyright.
- **EUR-Lex** und die **SPARQL- und Dokumentenschnittstelle des Amts für Veröffentlichungen der EU**
  (publications.europa.eu): Weiterverwendung nach Beschluss 2011/833/EU, Quelle ist anzugeben; nur die
  amtlichen Fassungen im Amtsblatt sind verbindlich. „Thema → TM“ paart die Sprachfassungen von Rechtsakten zu
  Satzpaaren; jede Einheit trägt die CELEX-Nummer als Herkunft (Quelle: EUR-Lex, © Europäische Union).
- **DWDS** (Berlin-Brandenburgische Akademie der Wissenschaften): Schnittstellen für Wörterbucheinträge,
  Worthäufigkeit und Aussprache. Die Artikelseiten tragen einen TDM-Vorbehalt und werden nicht gelesen, nur verlinkt.

Nicht angebunden, weil ihre robots.txt Programme aussperrt: RIS (Österreich) und die Testphase des
Rechtsinformationsportals des Bundes.

## Online-KI-Dienste

Die Dienste unter „Kostenlos online“ (u. a. OVHcloud AI Endpoints, Kilo Gateway) werden nur über ihre offiziellen
Schnittstellen und nur auf Wunsch des Benutzers angesprochen; es gelten deren Bedingungen.

## Wörterbücher

Hunspell-Wörterbücher werden **nicht mitgeliefert**. Sie werden erst auf ausdrücklichen Wunsch des Benutzers
aus dem LibreOffice-Wörterbuchprojekt (github.com/LibreOffice/dictionaries) geladen und gelten unter der Lizenz,
die im jeweiligen Wörterbuchordner dort angegeben ist (z. B. GPL, LGPL, MPL oder LGPL/OASIS).

## Dateiformate und Namen

Das Programm liest und schreibt offene Standards (XLIFF, TMX, TBX, Office Open XML, HTML, gettext PO)
sowie zur Interoperabilität einige verbreitete Austauschformate anderer Hersteller, die ausschließlich über ihre
Dateiendungen bezeichnet werden (.sdlxliff, .sdlppx, .sdlrpx, .sdltm). Es enthält keinen Code dieser Hersteller.
Genannte Produkt- und Dienstnamen (z. B. bei den KI-Anbietern) dienen nur der Angabe, womit eine Verbindung
möglich ist; sie gehören ihren jeweiligen Inhabern.

## Programmsymbol

Eigene Gestaltung. Die Buchstaben sind mit der Schrift DejaVu Serif gesetzt (freie Lizenz, Bitstream-Vera-/DejaVu-Lizenz)
und als Bild gespeichert; die Schrift selbst wird nicht mitgeliefert.
