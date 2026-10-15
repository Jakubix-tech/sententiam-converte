# Verwendete Fremdsoftware

Sententiam converte nutzt folgende Bibliotheken. Ihre Lizenzen gelten für den jeweiligen Teil.

| Bibliothek | Zweck | Lizenz |
|---|---|---|
| Avalonia (inkl. Themes.Fluent, Fonts.Inter, Desktop, Android) | Oberfläche | MIT |
| SkiaSharp, HarfBuzzSharp | Darstellung (über Avalonia) | MIT |
| CommunityToolkit.Mvvm | MVVM-Hilfen | MIT |
| DocumentFormat.OpenXml | DOCX, XLSX, PPTX lesen und schreiben | MIT |
| PdfPig | Text aus PDF lesen | Apache-2.0 |
| AngleSharp | HTML lesen und schreiben | MIT |
| Microsoft.Data.Sqlite | Datenbank für TM und Terminologie | MIT |
| SQLitePCLRaw / SQLite | SQLite-Anbindung / Datenbank-Engine | Apache-2.0 / Public Domain |
| WeCantSpell.Hunspell | Rechtschreibprüfung (Portierung von Hunspell) | MPL 1.1 / GPL 2.0 / LGPL 2.1 (wahlweise); hier unter MPL 1.1 unverändert genutzt |
| Anthropic (C#-SDK) | Anbindung der Claude-API | MIT |
| ASP.NET Core | Teamserver | MIT |
| xUnit, Avalonia.Headless | nur Tests, nicht ausgeliefert | Apache-2.0 / MIT |

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
