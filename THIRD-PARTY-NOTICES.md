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
| SQLitePCLRaw / SQLite | SQLite-Anbindung / Datenbank-Engine | Apache-2.0 / Public Domain |
| WeCantSpell.Hunspell | Rechtschreibprüfung (Portierung von Hunspell) | MPL 1.1 / GPL 2.0 / LGPL 2.1 (wahlweise); hier unter MPL 1.1 unverändert genutzt |
| Anthropic (C#-SDK) | Anbindung der Claude-API | MIT |
| ASP.NET Core, Microsoft.AspNetCore.OpenApi | Teamserver und REST-API (auch lokal in der Desktop-App) | MIT |
| AndroidX (Xamarin.AndroidX.*) | Start-Bildschirm auf Android | MIT / Apache-2.0 |
| xUnit, Avalonia.Headless | nur Tests, nicht ausgeliefert | Apache-2.0 / MIT |

## Auf Wunsch geladene KI-Komponenten

Nicht in den Paketen enthalten. Erst wenn der Benutzer es ausdrücklich auswählt, lädt das Programm aus den
offiziellen Quellen und prüft die SHA-256-Prüfsummen:

| Komponente | Quelle | Lizenz |
|---|---|---|
| llama.cpp (llama-server) | github.com/ggml-org/llama.cpp, Release-Pakete | MIT |
| Gemma 4 E2B/E4B (GGUF) | huggingface.co/ggml-org (Modelle von Google) | Apache-2.0 |
| OpenCode | github.com/anomalyco/opencode, Release-Pakete | MIT |

Für die kostenlosen Modelle, die OpenCode anbietet, gelten die Bedingungen von OpenCode und der jeweiligen Anbieter.

## Auf Wunsch geladene Sprachdaten

Ebenfalls nicht enthalten. Auf ausdrücklichen Wunsch lädt das Programm frei verfügbare Translation Memories über die
offizielle Schnittstelle des OPUS-Archivs (opus.nlpl.eu, University of Helsinki) und die frei verfügbare
IATE-Gesamtausgabe (iate.europa.eu, verlinkt im EU-Datenportal). Für jeden Datensatz gilt dessen eigene Lizenz
(im Programm angezeigt, Einzelheiten auf der Korpusseite); bei den meisten ist die Quelle anzugeben.

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
