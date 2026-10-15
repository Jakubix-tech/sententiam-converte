# Manual

Sententiam converte is a translation environment for professional translators:
translation memory (TM), terminology, quality assurance and project management – complemented by AI agents
that research on the web, check whether expressions are actually in common use, and evaluate translations with reasoning.

## Overview

The work always follows the same path:

1. **Create a project** – source language, one or more target languages, subject area.
2. **Add files** – they are split into segments (usually sentences); formatting becomes tags.
3. **Pre-translate** – anything the TM already knows is inserted.
4. **Translate in the editor** – with TM matches, terminology, knowledge, concordance and, if desired, AI.
5. **Check** – QA finds missing tags, wrong numbers, terminology violations and more.
6. **Generate the target file** – in the original format, with all formatting.

The tabs at the top: **Project**, **Editor**, **TM & Terminology**, **Knowledge**, **Agents**, **Settings**,
**Extensions** and **Help** (also with F1).

## Installation and first start

The program runs on Windows, Linux, macOS and Android. It does not require a separate .NET installation; the
packages include everything they need.

- **Windows**: Extract the ZIP and start `SententiamConverte.Desktop.exe`.
- **Linux**: Extract the archive and start `SententiamConverte.Desktop` (make it executable if necessary).
- **macOS**: Drag `Sententiam converte.app` into the Applications folder. On first start, right-click →
  “Open” and confirm, as long as the program is not notarized by Apple.
- **Android**: Install the APK (allow installation from unknown sources).

Settings, dictionaries and extensions are stored in `%LOCALAPPDATA%\SententiamConverte` (Windows) or
`~/.local/share/SententiamConverte` (Linux/macOS). The environment variable `SENTENTIAM_HOME` lets you choose a
different location, for example for a portable installation on a USB stick.

Before using AI for the first time, set up an AI connection under **Settings** (see “Setting up AI”).
Everything else – TM, terminology, QA, file formats – works without AI.

## Creating a project

**Project** tab → **New project**:

- **Name** – also becomes the folder name.
- **Source language** and **Target languages** as language codes, e.g. `de-DE` and `en-GB, pl-PL`.
- **Subject area / topic** – helps the TM assess context and helps the AI agents with their research.
- **create as a shared project on the team server** – see “Teamwork”.

Then click **Add files …**. Each file appears in the list once per target language, with its progress.
Further options:

- **Open project folder …** – opens an existing project; the most recent ones are listed under “Recently opened”.
- **Open project package (.sdlppx) …** – takes over a client's package; to send it back, use
  **Return package (.sdlrpx) …**.
- **Specialist sources for the agents** – which specialist source profile (law, medicine, engineering …) applies to this project.
- **Pre-translate from (%)** – minimum match value for pre-translation.
- **Confirm context matches (101%) during pre-translation** – such segments are then considered final.

## File formats

| Format | Notes |
|---|---|
| DOCX, XLSX, PPTX | Formatting becomes tags; images, fields and comments are preserved |
| ODT, ODS, ODP (OpenDocument) | As above; space and tab elements are preserved |
| IDML (InDesign) | Stories with character styles and hyperlinks |
| XLIFF 1.2 / 2.x, .sdlxliff | Bilingual exchange files; segment status is retained |
| PDF | Text is extracted, output as DOCX; scanned PDFs with **Recognize scanned PDF (AI) …** |
| HTML, Markdown | Markup becomes tags |
| RESX, JSON, PO | Software strings; placeholders such as `{0}`, `%s`, `${name}` are protected |
| SRT, VTT | Subtitles; timecodes remain unchanged |
| TXT | Each line is a paragraph |

TMX, TBX and .sdltm are imported under **TM & Terminology**. Extensions can add further formats
(the sample extension, for example, adds CSV).

## The editor

Double-click a file (or click **Open**) to load it into the editor. The source is on the left, the target on the right.

- **Tags** (⟨1⟩ … ⟨/1⟩) represent formatting. They must appear in the target; the program shows missing tags
  below the segment, and **Ctrl+Shift+G** inserts the next missing tag at the cursor position.
- **Status**: not translated, draft, translated, translation rejected, reviewed, review rejected, signed off. Locked segments (e.g. numbers only) cannot be edited.
- **✔ Confirm** (Ctrl+Enter) saves the segment to the TM and jumps to the next open segment.
  Identical segments further down are filled in automatically.
- **Split / Merge** changes the segment boundaries (not for XLIFF files and shared projects).
- **Filter** at the top: all segments, not translated, not confirmed, with QA messages.
- **Find/Replace** applies to the target text.

The sidebar on the right:

- **TM** – matches with percentage and origin; numbers are adjusted when a match is inserted. Below them,
  **Fragment matches**: shorter TM entries that occur within the segment.
- **Terms** – recognized terms from the termbase (including inflected forms), forbidden terms in red.
- **Concordance** – free-text search in the TM, in the source or the target.
- **QA** – messages for the document and spelling of the segment.
- **Knowledge** – relevant entries from the knowledge bases.
- **Note** – comment on the segment and **History** of all changes with word-level comparison.
- **AI** – Translate, Evaluate variants, Check usage, Clarify term in context.

While you type, **AutoSuggest** offers words and phrases from the TM and the termbase.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Enter | Confirm segment, write it to the TM, go to the next open segment |
| Ctrl+Alt+Enter | Confirm as reviewed |
| Ctrl+↓ / Ctrl+↑ | Next / previous segment |
| Ctrl+1 … 9 | Insert TM match no. n |
| Ctrl+Ins / Ctrl+Del | Copy source to target / clear target |
| Alt+T / Alt+Z | Split segment at the cursor position in the source text / merge with the next segment |
| Tab or Enter | Accept AutoSuggest suggestion (↑/↓ to select, Esc to close) |
| Ctrl+Shift+G | Insert next missing tag |
| F3 | Search for the selected text in the concordance |
| Ctrl+F2 | Add the selected source and target text as a term |
| Ctrl+Shift+T / E / U | AI translation / evaluate variants / check usage |
| Ctrl+Shift+B | Clarify the selected term in context (term comparison) |
| Ctrl+S | Save |
| F1 | Help |

## Translation memory and context

Every confirmed segment is stored in the **Project TM** – together with the previous and the next sentence and
its position in the document. This allows the program to distinguish:

- **101% context match** – same sentence in the same context (for software strings: same ID).
- **100%** – same sentence, different context.
- **Fuzzy matches** (e.g. 85%) – similar sentence; the differences are highlighted.

The same sentence may be translated differently in different contexts. If there are several translations and none
fits the context, the match is lowered by 1% – a hint to take a closer look.

Under **TM & Terminology**:

- **Import TMX** / **Export TMX**, **Import .sdltm** (existing TMs from other programs).
- **Include more TMs/termbases** – additional databases, optionally only for certain languages.
- **Edit TM entries** – browse, filter, edit, delete.
- **Alignment** – select a source text and its existing translation; the sentences are paired by length and
  numbers. Reliable pairs are preselected; **Selected to TM** adds them to the TM.

## Terminology

Each entry (concept) consists of terms in several languages, each with a status (preferred, admitted, forbidden),
plus a definition and a subject area.

- Create entries under **TM & Terminology → New entry** or in the editor with Ctrl+F2.
- **Import TBX / terminology XML**, **Export TBX**.
- In the editor, recognized terms appear under **Terms**; QA reports when a prescribed
  term is missing or a forbidden term has been used.

## Quality assurance

**QA all files** (Project tab) or **Check document** (editor) checks for:
empty segments, target = source, tags, numbers, spaces, end punctuation, brackets, terminology,
repeated words, consistency (same source – different translations), length, spelling,
unwanted words and **custom rules** with regular expressions (optional source pattern, target pattern,
severity). Settings are under **QA settings and custom rules**; extensions can contribute further
checks.

Spelling: under **Settings → Spell check**, download the dictionary for the target language
(free dictionaries from the LibreOffice project).

## Pre-translation and analysis

- **Pre-translate (TM)** inserts matches at or above the configured threshold; context matches are confirmed if you choose.
- **Analysis** counts segments, words and characters per match band: context, repetitions, 100%, 95–99%,
  85–94%, 75–84%, 50–74%, no match – the basis for quotes.
- **Fill empty with AI** (editor) translates all empty segments with AI; the result is a draft that must be
  reviewed and confirmed.

## Knowledge bases

In addition to TM and terminology, there is **Knowledge**: subject knowledge, definitions, language usage and style rules,
each with language, topic, source and author. Every project has its own project knowledge base; further databases can be
attached per project and per language – locally or on the team server.

- **Knowledge → Browse** tab: search, create and edit entries.
- In the editor, the **Knowledge** sidebar shows relevant entries; they are also included in every AI translation and
  evaluation. Style rules for the target language always apply.
- **Knowledge agent**: specify a topic and languages – the agent searches for reliable sources, reads them and creates
  entries backed by a **verbatim quote**. If the quote does not actually appear on the page that was read, the
  entry is discarded.
- **Team activity**: who in the team has recently contributed what.

## Setting up AI

**Settings → AI connections**: choose a template (Claude, local with llama.cpp/Ollama/LM Studio, OpenAI,
Mistral, OpenRouter, custom server …), adjust the address and model, click **Test selected connection**, and save.

- API keys can be entered as `env:NAME` (e.g. `env:ANTHROPIC_API_KEY`); the key itself is then not stored
  in the settings file.
- **Who does what**: choose a separate connection for translation, research and evaluation –
  e.g. translate locally, research in the cloud.
- **Local or cloud**: for each connection, the settings show whether the texts leave your computer.
  Use a local connection for confidential jobs.
- **Web search for the agents**: Claude web search, Brave Search, Tavily or your own SearXNG instance.
  Local models cannot search on their own; this service does the searching for them.
- For small local models, reduce **Characters per web page** to about 2000–3000.

## Agents

**Agents** tab (most functions are also available directly in the editor under **AI**):

- **Term comparison** – for context-dependent technical terms, for example in law. The assistant clarifies the meaning
  in the given sentence, searches specialist websites in both languages for definitions and compares the concepts
  of the two systems (e.g. legal systems): full, partial or no equivalence, similarities,
  differences, when a solution fits and when it does not. It recommends a solution with a strategy (functional
  equivalent, literal translation with explanation, original term with explanation, paraphrase) and supports it with
  numbered, **verified citations**. **Recommendation → Termbase** and **Reasoning → Knowledge**
  save the result. In the editor: select the term in the source text and press Ctrl+Shift+B.
- **Topic research** – searches for specialist texts in each selected language and compiles a multilingual term list
  with source references; good terms go into the project termbase with one click.
- **Usage check** – is an expression in common use? The agent searches for the exact phrase and for synonymous
  alternatives, counts verbatim occurrences and bases its verdict on the evidence.
- **Evaluate translations** – scores several variants for accuracy, fluency, terminology and style,
  with reasoning and a suggested improvement; questionable phrases can optionally be checked on the web.

The **Agent log** shows every step: search queries, pages read, pages skipped.
AI results are suggestions – professional responsibility remains with you.

## Specialist source profiles

Under **Settings → Specialist sources by topic**, each topic (law, medicine, engineering, EU and public administration –
freely extensible) has one line per language:

`Language | System | Domains, comma-separated`

Example: `de-DE | German law (BGB, HGB, ZPO, StGB …) | gesetze-im-internet.de, dejure.org`.
`*` applies to all languages. The agents search these sites first and only then search the web in general.
**Check specialist sites** tests whether the sites permit reading and return text. Sites that prohibit automated
reading or technically prevent it do not belong in the list – the program does not circumvent such restrictions.

## Teamwork

Several people collaborate through their own **team server**.

**Setting up the server** (administrator):

```
SententiamConverte.Server --daten ./teamdaten
SententiamConverte.Server --daten ./teamdaten benutzer neu Anna bearbeiter
SententiamConverte.Server --daten ./teamdaten ressource neu tm Maschinenbau
```

On first start, the user `admin` is created; its access key is displayed once. Roles: Admin,
Editor (`bearbeiter`), Reader (`leser`). On the internet, run the server only over HTTPS (e.g. behind a reverse proxy).

**In the program**: under **Settings → Team server**, enter the name, address and access key, then click
**Test connection**. After that:

- **Shared resources**: under **TM & Terminology → Attach resource**, choose a TM, a termbase or
  a knowledge base on the server, optionally only for certain languages. **writes back**:
  confirmed translations and new terms are immediately stored there as well.
- **Shared projects**: several people edit **the same files** at the same time. Changes are
  synchronized every few seconds. A segment that someone is working on is locked for everyone else
  (shown with that person's name). If two people have changed the same segment, the version that reached the
  server first wins; your own version goes into the segment's **History** and is not lost.
  Create one with **create as a shared project on the team server**, join one via
  **Shared project (team) …**.

If the server cannot be reached, a project continues to work with its local data.

## Packages and exchange

- Opening a **.sdlppx**: creates a project with all files, TMs and termbases from the package.
- Creating a **.sdlrpx**: return package with the translated bilingual files.
- **XLIFF** per file (button in the file list) for exchange with other programs.
- **TMX, TBX, .sdltm** under **TM & Terminology**.

Third-party formats are read and written only for the purpose of collaboration.

## Interface (API)

All functions are accessible through a REST interface – for your own scripts, automation and other
programs. It is available in two places:

- **In the desktop app**: under **Settings → Interface (API)**, enable it, choose a port, generate an access key
  and click **Apply**. It is reachable only from this computer (127.0.0.1) and works with the project
  that is open in the window; changes appear immediately in the editor.
- **In the team server**: always on, with the server's users and roles.

Authenticate with the header `Authorization: Bearer <key>`. The machine-readable description (OpenAPI)
is available at `/openapi/v2.json`. All endpoints and examples: see the chapter “API Reference”.
For C# the same interface is available as a .NET SDK (`SententiamClient`), see there.

## Extensions

In the **Extensions** tab, you install extensions from other developers: file formats, QA checks and
commands.

1. Click **Install extension …** and choose the file (`.scext`, `.zip` or `.dll`).
2. The extension is initially **disabled**. **Enable …** asks for confirmation: an extension is
   program code and runs with your permissions – only enable it if it comes from a trusted source.
3. Its commands appear under **Commands**. They act on whatever is open or selected in the editor;
   enter arguments one per line as `name=value`. **Run** shows the result below.

**Disable** and **Remove** take full effect after a restart. How to write your own extensions
is described in the chapter “Developing Extensions”.
Extensions are written in C#, for example, and have access to all functions of the program through `context.Api`.

## Privacy and legal notes

- **AI in the cloud**: the texts leave your computer. Observe your agreements with clients and the GDPR;
  use a local connection for confidential jobs.
- **Websites**: the program uses only official search interfaces, identifies itself with its own user agent when
  reading pages, respects robots.txt and reservations of rights for text and data mining (Section 44b of the
  German Copyright Act (UrhG)), and takes over only short quotes as evidence.
- **Team server**: the data resides on your own server; only hashes of the
  access keys are stored. Contributions are logged by name – everyone involved should be aware of this.
- **Third-party formats** are referred to only by their file extension and are processed solely for interoperability.

These notes do not constitute legal advice.

## Files and folders

```
<project>/
  project.json            settings, file list, QA rules
  quelle/                 original files
  arbeit/<id>.<lang>.json bilingual working copies (with comments and history)
  tm/projekt.sctm         project TM (SQLite)
  terminologie/projekt.sctb
  wissen/projekt.sckb     project knowledge base
  paket/                  original package, if the project was created from a package
```

In the program's data folder: `ai-settings.json` (AI), `ui-settings.json` (user interface, local API),
`servers.json` (team server), `woerterbuecher/` (dictionaries), `erweiterungen/` (extensions).
