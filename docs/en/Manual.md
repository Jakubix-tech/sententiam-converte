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

- **Windows**: Double-click `SententiamConverte-<version>-win-x64.msi`, accept the license, **Install**. The program
  is set up for the signed-in user only (no administrator rights needed) – in
  `%LOCALAPPDATA%\Programs\Sententiam converte`, with entries in the Start menu and on the desktop; at the end it can
  start right away. A newer version is simply installed over it; remove it under **Settings → Apps**. If you prefer
  not to install anything, use the ZIP version: extract it and start `SententiamConverte.Desktop.exe`. Windows
  SmartScreen may warn because the package is not signed: **More info → Run anyway**.
- **Linux**: Extract the archive and start `SententiamConverte.Desktop` (make it executable if necessary).
- **macOS**: Drag `Sententiam converte.app` into the Applications folder. On first start, right-click →
  “Open” and confirm, as long as the program is not notarized by Apple.
- **Android**: Install the APK (allow installation from unknown sources).

Settings, dictionaries and extensions are stored in `%LOCALAPPDATA%\SententiamConverte` (Windows) or
`~/.local/share/SententiamConverte` (Linux/macOS). The environment variable `SENTENTIAM_HOME` lets you choose a
different location, for example for a portable installation on a USB stick.

The user interface is available in German, English and Polish (**Settings → User interface language**, default: system
language; takes effect after a restart). With the Polish interface, the manual and generated documents (quote, review
file, evaluation report) are in English.

Before using AI for the first time, set up an AI connection on the **Set up AI** page (see “Setting up AI”).
Everything else – TM, terminology, QA, file formats – works without AI.

### Getting started

On first start, the **Project** tab shows **Getting started** at the top – four steps, each ticked once done:

1. **Choose an AI** – **Set up free online AI (one click)** finds the free online models, tests them and sets them up as
   a pool for all tasks. **Choose another AI** leads to the **Set up AI** page (built-in offline AI, own key …).
2. **Create a project** – **Choose files …** (the name is suggested from the first file), source and target languages,
   subject field, **Confidential** (switch privacy on right away) and **Create a shared TM, termbase and knowledge base**
   (recommended). **Create project** does everything in one step.
3. **Use existing translations** (optional) – see “Importing existing translations”.
4. **Translate** – opens the first unfinished file in the editor, with the most important moves in brief.

**Hide** hides the guide; the **Getting started** button at the top of the Project tab brings it back. If no file is
open in the editor yet, it explains what to do next.

## Creating a project

**Project** tab → **New project**:

- **Name** – also becomes the folder name.
- **Source language** and **Target languages** from the language picker: type a name or code (“Polish”, “polski” or
  “pl”) and pick from the list – shown as “Polish (Poland) · pl-PL”. Target languages appear as tags, **✕** removes one.
  About 80 languages and variants are listed; others work as a code (e.g. `eu`). The same picker is used wherever
  languages are chosen (getting started, existing translations, alignment, language data, resources).
- **Subject area / topic** – helps the TM assess context and helps the AI agents with their research.
- **create as a shared project on the team server** – see “Teamwork”.

Then click **Add files …**. Each file appears in the list once per target language, with its progress.
Further options:

- **Open project folder …** – opens an existing project; the most recent ones are listed under “Recently opened”.
- **Switch project** – the open project is shown on the left of the status bar. Click it to see the recently opened
  projects with name and languages (the open one marked ✓); click one to switch – from any tab. On the Project tab the
  other projects are also listed under **Other projects** (expand it); ✕ removes one from the list, the project folder
  stays as it is. If you switch while in the editor, you land in the file and at the segment where you last were in
  that project; opened from another tab, you continue there as soon as you go to the editor. If the AI is still working
  in the editor (translation, check, research), it stops before the switch – whatever is finished stays saved; none of
  it ends up in the new project.
- **Open project package (.sdlppx) …** – takes over a client's package; to send it back, use
  **Return package (.sdlrpx) …**.
- **Specialist sources for the agents** – which specialist source profile (law, medicine, engineering …) applies to this project.
- **Pre-translate from (%)** – minimum match value for pre-translation.
- **Confirm context matches (101%) during pre-translation** – such segments are then considered final.

### Project templates

A project's settings can be saved as a **template for new projects** (**Project** tab, enter a name, **Save**): languages,
subject field, specialist source profile, QA settings, privacy, prices, error scoring and the attached shared databases.
For the next **New project**, choose it under **Template** – languages and subject field are suggested, everything else is
applied when the project is created. Handy per client.

## File formats

| Format | Notes |
|---|---|
| DOCX, XLSX, PPTX | Formatting becomes tags; images, fields and comments are preserved; text boxes and content controls are translated too, page number and date fields stay unchanged |
| ODT, ODS, ODP (OpenDocument) | As above, including text frames, headers and footers; space and tab elements are preserved |
| IDML (InDesign) | Stories with character styles and hyperlinks |
| XLIFF 1.2 / 2.x, .sdlxliff | Bilingual exchange files; segment status is retained |
| PDF | Output is a PDF again, in the layout of the original (see below); scanned PDFs with **Recognize scanned PDF (AI) …** |
| HTML, Markdown | Markup becomes tags |
| DITA (.dita, .ditamap) | Topics and maps: titles, paragraphs, steps, tables, navigation titles; inline markup becomes tags, code and `translate="no"` stay unchanged. The target file keeps its name so that references still work – save each language in its own folder |
| RESX, JSON, PO | Software strings; placeholders such as `{0}`, `%s`, `${name}` are protected |
| SRT, VTT | Subtitles; timecodes remain unchanged |
| TXT | Each line is a paragraph |

Word and OpenDocument files are exported by writing back into the original file: page layout, styles, images,
frames, tables, headers and footers stay as they are – only the text is replaced, and a longer translation flows on
just as it would in Word or Writer.

TMX, TBX and .sdltm are imported under **TM & Terminology**. Extensions can add further formats
(the sample extension, for example, adds CSV).

**PDF files** are exported as PDF again, in the layout of the original: images, lines, coloured areas and the page
format stay unchanged, and every paragraph stays in its place – with the size, colour and alignment of the original,
including bullets and hanging indents. Bold and italic words appear as tags in the editor and are kept in the
translation; wide gaps (e.g. “Berlin  |  Phone”) appear as several spaces in the text. Text is set in Noto Sans or
Noto Serif, depending on whether the original uses a sans-serif or serif typeface. Because these fonts often run a
little wider than the original font (e.g. Arial or Calibri), the program measures how wide the original sets its lines
and matches the font size to it – the translation then looks as large as the original and wraps similarly; line spacing
stays the same. If the translation needs more
lines, the text below moves down – up to the next line, graphic or footer; if there is not enough room, the
paragraph's font becomes slightly smaller. Check the result before passing it on.

## The editor

Double-click a file (or click **Open**) to load it into the editor. The source is on the left, the target on the right –
as a table with a status colour at the edge. **Interlinear** in the toolbar shows source and target one below the other:
the source on top, the target below, both across the full width – the source stays selectable (for copying), the target
editable. Click again to switch back; the choice is remembered. In a narrow window the toolbar wraps to a second line.

The editor works like a word processor:

- **Formatting is visible**: text that is bold, italic, underlined, superscript or subscript in the original
  appears that way – in source and target. The tags (⟨1⟩ … ⟨/1⟩) remain visible as small coloured markers.
- **Ctrl+B / Ctrl+I / Ctrl+U** (or the **B**, **I**, **U** buttons) apply the matching formatting from the
  source around the selection or around what you type next.
- **Spell checking as you type**: unknown words get a red wavy underline; right-click shows suggestions and
  “Add to dictionary”. If the target-language dictionary is missing, the editor offers it at the top with one click
  (free dictionaries from the LibreOffice project). The button with the check mark switches checking off.
- **Undo/Redo** (Ctrl+Z / Ctrl+Y), **Larger/Smaller** (magnifier) for the font size.
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
- **Concordance** – free-text search in the TM, in the source or the target. **How often?** counts in how many
  sentences of your own data (project TM, shared TMs, downloaded free language data) the phrase occurs exactly like
  this, per database; separate variants with “|” to compare them (“Benutzer | Nutzer”). For single German words the
  DWDS frequency class is added. **On the web by country …** checks the phrase per country under “Resources → Usage on
  the web”.
- **Messages** (formerly “QA”) – everything there is about a segment, in one place: QA (including grammar and the
  AI check), errors from review and AI review, the smart assessment, the research on the expressions of the sentence
  (term and grade), protected data and the AI's messages from translation, research, web check and feedback. Errors
  come first, then warnings, then notes; the tab header shows the number. Long messages are shortened; hover over
  one to see all of it. **Selected segment only** is on: clicking a
  segment shows its messages. Off, all messages of the document are listed by segment, and clicking a message jumps
  to the segment. Above them are the QA buttons and the segment's spelling. The AI's messages and AI checks are stored
  with the document (`meldungen/`), the research in its dossiers – after closing and reopening, everything is there
  again.
- **Markers on the segment** show their messages when you hover over them: ⚠ the QA messages (“Numbers: 12 missing in
  target”), ✗ the recorded errors (category, severity, note), ⓘ the AI's messages (newest first), ● the assessment,
  🔒 the protected data.
- **Knowledge** – relevant entries from the knowledge bases.
- **Note** – comment on the segment and **History** of all changes with word-level comparison.
- **AI** – Translate, Evaluate variants, Check on the web, Clarify term in context.
  **Check on the web** (Ctrl+Shift+U): the AI picks the wordings of the translation that show whether it is correct and
  natural (technical terms, collocations, set phrases) and names alternatives. Each variant is counted in
  target-language texts – Wikipedia articles with the exact wording, plus web hits with a search service – then the AI
  judges in context (✔ fits, ⚠ questionable, ✖ wrong), cites the numbers and the evidence with addresses and, when it
  is worth it, suggests an improved version (**use** inserts it as a draft). Frequency is a hint, not proof. Names,
  numbers and placeholders are never searched. With a selection in the target text only that expression is checked.
  AI results and suggestions always belong to the segment they were made for: they disappear when you move to another
  segment and come back when you return – “use” never acts on the wrong segment.

While you type, **AutoSuggest** offers words and phrases from the TM and the termbase.

### Review: tracking changes and scoring errors

In the side tab **Review**:

- **Track changes to confirmed segments** – when the reviewer changes a confirmed translation, the previous version is
  kept. Below the target field the change is shown word by word (red deleted, green inserted). **Accept** / **Reject**
  applies to the selected segment, **Accept all** / **Reject all** to the file; the filter **Changed by reviewer** shows only
  those segments. Changes coming back from a review file (Word) are always tracked this way.
- **Error scoring** – record errors in the selected segment with a category (accuracy, fluency, terminology, style, locale,
  formatting – each with a subtype) and a severity (minor 1, major 5, critical 25 penalty points, neutral 0). The score is
  100 − penalty points × 100 / words; **Pass from** sets the threshold (default 98), and a critical error always fails.
  **Evaluation report (Word)** lists the result, the categories and every error with its sentence and comment. The filter
  **With error scoring** shows the affected segments.
- **AI evaluates the file** – the AI goes through all translated segments and suggests errors in the same scheme
  (category, severity, span, correction, reason; based on MQM), also measured against the document brief. The
  suggestions appear as **AI suggestion** in the segment's list and count towards the score; remove single ones with ✕
  or all with **Remove AI suggestions**. Running it again replaces the earlier AI suggestions; errors recorded by human
  reviewers stay. This also gives a quick assessment of someone else's finished translation. The AI writes the reason in
  the language set for explanations under **Set up AI**.
- **Evaluation report in the program** – in the **Review** tab, **Evaluation report – all errors** expands the penalty
  points per category and every error of all segments: segment, category, severity, reviewer, source → translation and
  the comment (from the AI: “wrong wording” → “better version”: reason). **Go to segment** jumps there; **Apply
  suggestion** replaces the criticised wording with the better version (as a draft) and the error drops out of the
  evaluation. If the wording is not (or no longer) in the translation – e.g. an omission – the button is missing: then
  correct it by hand. **Show report large** (also **View report** in the **AI** tab) opens the same report large over
  the editor, together with the steps of the last automatic run, the corrections, the expressions for more thorough
  research and the log – each part can be expanded.

In shared projects the team server synchronises both: the translator sees the reviewer's changes and the recorded errors
at the next synchronisation and accepts or rejects them there. Older program versions do not interfere – they leave the
review state on the server unchanged.

### Dictation

**🎤 Dictate** (Ctrl+Shift+D) records from the microphone; a second click stops the recording and the recognised text
appears at the cursor of the target field. The speech recognition is chosen under **Set up AI → Dictation**. On Windows,
the built-in voice typing (Windows+H) also works in every text field; on Linux dictation needs `arecord`, on macOS `sox`.

### Maximum length

If a segment has a maximum length (from the file, e.g. XLIFF `maxwidth`, or for all segments in the QA settings), a
counter such as “23/40” appears next to its number, red once the translation is too long; QA reports it as an error.

### Research for the document

In the side tab **Research** the AI prepares the translation. **From** and **To** are a language *with a country*
(preset from the project, e.g. es-ES → es-MX); Spanish in Spain is not Spanish in Mexico, and a law or authority of
country A has a different counterpart in country B.

- **Scan document** – the AI reads the **whole document** in parts of about 4,000 characters (several at once in the
  free pool) and names, for each part, all expressions that need research: technical terms, legal concepts and
  institutions, realia (animals, plants, food, customs …), idioms, abbreviations – with a reason and a first estimate of
  how difficult they are. In addition the program itself finds **abbreviations** ("GLRD", "DIN"), **section
  references** ("§ 242 StGB", "Art. 6 DSGVO") and **EU legal acts** ("Verordnung (EU) 2016/679"). Only what is literally
  in the document counts; if several parts name the same expression, the higher difficulty applies. Medium and hard
  expressions are ticked, easy ones are not – the list stays complete without everything having to be researched.
- **Research keeps running**: research jobs wait in a **queue** and run one after another in the background – even when
  you switch tabs (TM, Terms, QA …) or segments. Every dossier appears as soon as it is ready; the tab shows
  "Research ⏳" (likewise "AI ⏳") while work is going on there. New jobs are added without stopping the current one;
  only **Cancel** stops. Long jobs such as AI evaluation, grammar or AI checks no longer pull you back into their tab
  at the end if you have moved elsewhere in the meantime – the status bar reports the result.
- **This segment**: while the Research tab is open, it shows at the top the expressions of the selected segment with
  their **meaning** and **target-language equivalent** ("Gleitringdichtung → mechanical seal"). Anything without a
  dossier is plainly marked "not researched yet" ("I have nothing on “GLRD” yet – I am researching it now.") and, with
  **Research the expressions of the selected segment right away** (on by default), goes to the front of the queue; if
  the document has not been scanned yet, the AI reads just this segment for it.
- **Explaining marked text**: if you mark text in the source or the translation while the Research tab is open, the AI
  explains it above: **Meaning in the sentence**, **Grammar** (word class, form, syntactic function, construction,
  valency), **Linguistic notes** (register, collocation or fixed expression, word formation, ambiguity, false friends),
  the corresponding words **in the translation** or **in the source text** (only if they literally occur there), an
  **Assessment** of whether they fit, **Alternatives**, and what the research already has **from the research** on these
  words (evidenced or not, with source). This is the AI's assessment without its own sources; **Research with evidence**
  researches the marked text (for a marking in the translation: the corresponding words in the source). The explanation
  stays when you click elsewhere; if you remove the marking in the field or select another segment, the usual view
  returns. The same marking is not asked twice.
- **Research checked items** – a **dossier** for each expression via the term comparison (specialist sites of both
  countries, verbatim evidence): what it means in the source language and country, which equivalents exist in the target
  language and country (full, partial, none), when and why which variant fits, background, recommendation with a
  strategy (equivalent, paraphrase, original with explanation), warnings, sources – and **how difficult** it is
  (re-assessed after the research: without an equivalent in the target country it is difficult).
- **Own terms** – type them in the field below (separate several with `;`) and click **Add** or press Enter: they go to
  the top of the list, checked, with a note on how often they occur in the document – including terms the AI did not
  suggest or that do not occur in the document at all. They stay when you scan the document again.
- **Concept field with advice**: for technical terms, legal terms and realia, research clarifies not just the one
  expression but its **family of concepts in both languages** – for "Spitzdach" e.g. the superordinate concept roof shape
  and the siblings gable roof, hip roof, pyramid roof, shed roof … with their distinguishing features and equivalents. The
  equivalents are evidenced: via the Wikipedia language link, pages in both languages and your own reference texts; if
  another term appears more often in the sources, that one is taken. The dossier then shows the superordinate concept,
  all concepts (the one meant in the sentence with ▶), **How to tell them apart** (questions such as "How many roof
  slopes? Are there gables?"), **For this sentence** (what is meant and which term therefore fits – a colloquial
  "Spitzdach" may be a pyramid roof) and, if the context is not enough, a **question for the client**. The equivalent of
  the concept meant goes into the term comparison as a candidate. Idioms and abbreviations have no concept field.
- **Where the evidence comes from**: first from your **own reference texts** (see there), then via the **Wikipedia
  language link** – the article "Gleitringdichtung" links e.g. to "Mechanical seal"; the counterpart is taken as a
  candidate with its first sentence as evidence –, then via web search. With the default **Wikipedia** search the
  dossiers are thin: in a test with 16 sentences, research with Gemma 4 E4B often recommended a literal mistranslation
  for technical terms ("sliding ring seal"), and translations got worse overall (E4B 76.9 → 75.0 chrF, EuroLLM 74.7 →
  73.1). That is why AI translation only receives dossiers whose recommendation appears verbatim in a target-language
  source; with this the damage disappeared (E4B 76.6, EuroLLM 74.9). Every dossier shows whether this is the case; an
  unevidenced recommendation that is right can be taken over with **→ Terminology**. Research only brings a real gain
  with better sources: if your own web search (SearXNG) runs on the computer, the program switches to it at
  start when only Wikipedia was searched (Wikipedia stays as a second source; if you switched SearXNG off, that stays).
  Otherwise set up a web search (**AI setup → Web search**: free **Tavily** with 1000 searches a month and no
  credit card; **Brave** with $5 credit a month, about 1000 searches, credit card required when signing up; your own
  SearXNG instance or web search via Claude) and add own reference texts. Google's web search is not open to programs
  (closed to new customers, ending 1 January 2027), and querying Google automatically is not allowed. The Google Search
  of the Gemini API ("grounding") is not included either: under Google's terms its links must not be used to find pages
  to read, and results must not be stored or analysed – which is exactly what research does. Personal names ("Ms
  Kowalska") and entries of the protection list are not researched.
- **Searching on like a human**: if after the first search no candidate appears in **two independent target-language
  sources** (one per website or own file), research thoroughly searches differently – up to six rounds. If the AI runs
  out of new approaches, the program continues with simple queries (each candidate on its own, with the subject field
  and with "glossary", the source term with the name of the target language) until everything obvious has been tried.
  The AI gets what has already been searched and found and picks new approaches: describing the concept, bilingual glossaries and
  dictionaries, manufacturer documentation, standards, authorities and professional bodies of the target country,
  synonyms, broader and narrower terms; nothing is searched twice. The program itself looks for every candidate on the
  pages read (small models often miss it). If a page names a **language version** – e.g. the English version of a
  German manufacturer page – it reads that too and checks which term it uses in the same place. The dossier shows how
  many rounds and pages it took and whether the evidence is clear; what took long to find counts as harder. If your own
  reference texts already settle it, no web search is done.
- **Version of the sources**: every source in the dossier shows when it was read ("retrieved on …") or which version the
  document states – web pages and laws change; an older dossier can be **researched again**.
- **Clicking** a segment shows the dossiers for the expressions in it; **Selection / segment** or **Ctrl+Shift+R**
  shows the dossier for the selected word or sentence, or researches it if there is none yet.
- **Evidence**: every source is numbered with language, page, address and the **verbatim quote**; the equivalents refer to
  it with [1], [2] … A click opens the page and jumps straight to the quoted passage (the browser highlights it). Without
  evidence a warning appears: the information then comes only from the model's own knowledge.
- **Provenance**: below each dossier, which AI created it (in the free pool, the models that answered), which search and
  when. The top of the tab shows what the next research will use. By default the AI searches Wikipedia only; for
  specialist sites set up a search service under **Set up AI → Web search for the agents**.
- Per dossier: **Insert** (suggested wording at the cursor), **→ Terminology**, **→ Knowledge**, **Usage examples**
  (the usage check for the recommended term against the other candidates: real sentences in the target language with a
  link to the spot, and a plain statement of what is usual and what is not), **Research again**
  (perhaps the AI now finds better evidence – the previous version is kept and comes back with **Earlier version**), ✕.
  A click on the header collapses or expands a dossier; in the list of all dossiers they are collapsed. The dossiers are
  stored in the project (`recherche.json`) and apply to all files.
- **What was found is stored**: which expressions the AI found in a sentence and how it assessed it is also stored in
  the project (`recherche-ausdruecke.json`, per source sentence). Close and reopen a document, or have the same
  sentence elsewhere or in another document of the project, and the expressions, dossiers and assessment are there at
  once – the AI does not read the sentence again, and nothing is researched again. A changed sentence is a new
  sentence. If the AI captures an expression in another sentence slightly differently, it counts as the same when only
  articles, capitalisation, punctuation or inflection differ (“der Spaltring”, “Spaltringe” = “Spaltring”;
  “uszczelnienia mechanicznego” = “uszczelnienie mechaniczne”). Short words must be identical (“Druck” is not
  “Drucker”). The same applies to the research memory.
- **Source quality and grades**: every page read is classified by its kind – from the page itself, not from a list of
  known sites, so it works in every field, even for sites never seen before: what is reliably recognisable from the
  address (forums, Wikipedia, legislation portals, standards bodies, official addresses such as .gv.at, .admin.ch,
  bund.de, europa.eu, universities, dictionaries), otherwise the AI classifies the page while reading it. The ranking of
  kinds is almost the same everywhere and gives the weight of a piece of evidence: termbank or glossary, legislation,
  standard, authority 1.1 – own reference texts 1.0 – professional body, manufacturer, company website, university,
  dictionary 0.85 –
  encyclopedia, press, other page 0.7 – blog 0.45 – forum 0.35. A candidate is **conclusively evidenced** from two
  independent websites with a total of at least 1.4: two ordinary pages are enough as before; a **forum** counts, but is
  not enough on its own – together with an authority, standard or termbank it is, and so are four independent forums that
  agree (that is real usage). For the own names, products and terms of a company or institution, **its own website** is
  the strongest source and counts like an authority: if the name searched for is a whole part of the address
  (“Raindancer” → raindancer.com, ab-raindancer.de), the site weighs 1.1 for it – a word that is only part of a longer
  part (“Pumpe” in pumpen-shop.de) is not enough. If only a forum names a term, the next round looks specifically for confirmation in
  termbanks, standards, authorities, professional bodies or manufacturers. Every dossier carries a **grade**: **A**
  conclusively evidenced with a strong source, **B** conclusively evidenced, **C** evidenced but not conclusive (one source
  or only weak ones), **D** not evidenced – shown on the dossier, in “This segment” and in the explanation of marked text.
  Older dossiers are classified by the addresses of their sources. **Saturation**: if two search rounds in a row bring
  neither new evidence nor new candidates, research stops – searching further hardly pays off, and the dossier says so.
- **Feedback from confirming**: on confirming, the program checks for each researched expression of the segment whether
  the recommended term stayed (**taken over**) or something else replaced it (**replaced**); a terminology error a
  reviewer records in the review counts as replaced. It is recognised in three stages: verbatim; inflected, with umlaut
  or in a different word order (“uszczelnienia mechanicznego” for “uszczelnienie mechaniczne”, “Laufräder” for
  “Laufrad”) – both at once; and if the sentence was **reworded** and none of the terms stands there like that, the
  program asks the review AI in the background which words of the translation render the expression. Its answer only
  counts if it can be confirmed against the text (the words named are in the translation, and “the recommendation” also
  resembles it). A translator's **own term** that research did not suggest counts as replaced; if the expression is only
  **paraphrased** (a pronoun, dissolved into other words, left out), it does not count – that says nothing about the
  term. Every assignment is in the AI log (“Feedback segment 4: “GLRD” – replaced by the translator's own term “seal
  unit” (AI assignment)”). Without AI access only the first two stages apply. Each segment and expression counts once; if
  it changes on confirming again, the earlier feedback is withdrawn – also when the expression is now only paraphrased. The program learns two things from this: the **Research** tab shows how often
  recommendations of each grade were taken over (“A 23/24 · B 8/10 · C 3/7”) – which shows whether the grades are right.
  And the sites that supported a term taken over gain **reputation** in the source database, replaced ones lose it: later
  research searches the proven sites with the best reputation first (kind of source × evidence, taken over, replaced;
  whatever has delivered nothing for half a year counts only half), and sites with more replaced than used terms drop
  out. The source database thus grows by itself with every research – for recurring topics; a new topic does not need it,
  the classification by kind applies from the first page. Everything stays in the data folder of this computer
  (`recherche-rueckmeldung.json`, `quellen-datenbank.json`).
- **Research memory (all projects)**: every dossier a project saves also goes into a memory per language pair (with
  country) in the data folder of this computer – never onto a server. It keeps the expression, meaning, candidates,
  recommendation, reasoning and the evidence from the web, plus project, topic and date; it does not keep the sentence
  context, file and segment numbers, or quotes from your own reference texts. From confidential projects something only
  goes in if “Still add research to the research memory” is ticked under **Privacy**. An evidenced entry is never
  replaced by an unevidenced one. This way every project uses what others have already researched: before every research
  (batch, queue, follow-up research) the program looks there first – if the expression is **evidenced** there and the
  **topic** matches (equal or a shared word; without a topic it always matches), the result is taken over without new
  research, and the dossier says “From the research memory: project (date)”; otherwise the research builds on it.
  Translation uses evidenced entries on the same topic even if the project itself has no dossier for them yet. In the
  **Research** tab, **Research memory (all projects)** shows the entries of the language pair – searchable, with ✕ to
  remove one (the projects' dossiers stay).
  Entries are separated by language pair and by **explanation language** (the AI writes meaning, reasoning and notes in
  the language set for explanations under **Set up AI**): only what is written in the current explanation language is
  taken over and shown – if explanations are set to Polish, no German or French explanations appear. If an expression
  only exists with explanations in another language, translation uses just its evidenced term (without explanation
  text), and new research builds on its candidates, queries and evidence. Older dossiers without this information count
  as explained in German.
- **Researching again builds on the previous research** (**Research again**, **Research thoroughly** in the report,
  the follow-up research of **Translate automatically**): the candidates and the recommendation of the previous dossier
  are the starting point, its search queries are not repeated (the AI is told about them and looks for other ways), its
  pages are not read again, and its verified evidence still counts – with one new independent source an expression can
  thus be evidenced sooner. Every dossier remembers all queries made so far, including those of earlier research.
  Every research knows the existing termbase entries of the segments in which the expression occurs – also in the batch
  and in the automatic run. The log in the **AI** tab gives, per expression, the duration, the rounds, the pages read and
  whether it is evidenced (⏱ “expression”: 2 min 13 s, 3 round(s), 9 pages – evidenced).
- **Translation uses them by itself**: when the AI translates a sentence (single, all empty segments, watch folders,
  API), the program picks the dossiers for the expressions in it and passes on recommendation, meaning, strategy and
  the variants without an equivalent ("do not use") – at most five per sentence, only dossiers whose recommendation is
  evidenced in a target-language source (see below).
  Likewise it gets the definition of every recognized term from the terminology. Because the program selects what fits
  instead of the AI searching the database itself, this also helps small local models such as EuroLLM or Gemma
  without slowing them down.
- **Where to find this**: for each dossier the AI names places for this kind of information in both countries
  (authorities, official termbases, associations, dictionaries).

### Document type and wordings used in the country

At the top of the **Research** tab, **Check document type** determines what exactly the document is (e.g. “CV of a
software developer for a job application”), which conventions such documents follow in the target country (structure,
mandatory parts, formats) and how the headings, job titles and fixed phrases of this document are actually written there
– including where a technical term usually stays untranslated. Every suggestion is counted in the target language
(Wikipedia; with Brave or Tavily only hits from the target country). If one candidate is clearly more frequent it is
used (“chosen by frequency”), otherwise the AI's first suggestion. The checked wordings go into the document brief and
into every AI translation of this document; unchecked ones stay unchecked when you check again. If the target language
has no country (e.g. just “pl”), the main country of the language is used.

### Source database

Under **Resources → Source database** the program collects the websites on which research found evidence, and the
places the AI names – with language, subject, number of pieces of evidence and what can be found there. The database
applies to all projects and grows with every research. Future research on the same subject also searches these sites
specifically (sites with evidence first; this requires a search service under **Set up AI → Web search for the
agents**). Filter, **Open** and remove with ✕.

For confidential projects, only placeholders go to the AI and into the search queries here as well.

### Adapting fuzzy matches selectively

For a TM match of 70–99 %, the side tab **TM** shows **Adapt (AI)** next to **Insert**: the AI receives the old and the
new source text, the approved translation and the differences, and changes only the words that changed – the rest of
the client's wording stays word for word. Below, the change compared with the match is shown (red deleted, green
inserted); if the AI changed considerably more than necessary, a note appears. **Fill empty with AI** does this automatically
when **With “Fill empty with AI”, adapt fuzzy matches (70–99 %) selectively** is switched on in the side tab **AI** (default);
otherwise it translates freely, with the match as an example. Classic machine translation cannot follow instructions –
in that case the language model for research takes over.

### Document brief: read the document first

When a document is opened (otherwise at the latest before its first AI translation), the AI reads it once in full
(long documents: beginning, middle and end) in the background and records in the **document brief**: topic, text type, audience, tone, recurring names – and above all **who is
addressed** and **who is speaking**. Both apply to the whole document, not to a single sentence: in Polish, for
example, “you” becomes *Państwo*, *Pan*, *Pani* or *Pan/Pani* depending on the recipient, in manuals often the
imperative (*Kliknij*) or an impersonal form (*Należy kliknąć*); and “I have done” is *zrobiłem* or *zrobiłam* depending
on the author.

- If the AI can tell for sure (salutation, text type), it takes that over. If it is unsure, **the program asks once
  per document**: choose the form of address and the speaker, **Apply and translate** – or **Decide later**.
- Every AI translation of the document receives the brief; the AI makes all dependent forms agree
  (*Pani była*, *Pan był*, *Państwo byli*).
- In the side tab **AI** under **Document brief** it can be viewed and changed (**Apply**) or completed (**Read
  document**). Reading fills only **empty fields**: what you entered or confirmed (e.g. topic or speaker) stays; names
  are merged. An incomplete brief that the AI has never read is completed automatically when the document is opened.
  The AI writes the entries in the configured explanation language. On opening, reading only happens if an AI is ready
  (the built-in one must be downloaded); the question comes only before the AI translation. **Read the document before
  AI translation** switches the reading off (one request per document and target language).
- QA reports forms under **Form of address** that do not match the brief – without a brief, mixed forms within the
  document (Polish Pan/Pani/Państwo, German Sie/du, French vous/tu).

### Learning from corrections

When you confirm, the program compares the translation with the suggestion that was inserted (AI, machine translation,
TM match). If you change the same thing in the same way a second time – say “Benutzer” to “Nutzer” –, the editor shows
**Learned: you change … Always do so from now on?** With **Adopt as rule** the AI receives the rule with every
translation into this target language, and QA reports deviations under **Learned rule**; **No** never suggests it again,
**Later** hides the hint.

If you only change the form of address (Pan → Pani, Sie → du) or the gender of the first person (zrobiłem → zrobiłam),
no rule is created – that applies to one document: the hint asks whether it applies to the whole document and then
adjusts its brief. Confirmed briefs also remember the form of address and speaker per client and text type; for the
next manual of the same client the usual form of address is already set.

**Settings → Learn from corrections** switches it all off (rules then no longer apply either) and lists the rules with
**Activate** and **Remove**. Everything is stored only in the data folder (`gelernt.json`).

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
| Ctrl+B / Ctrl+I / Ctrl+U | Apply the source's formatting (bold, italic, underline) |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Right-click a word with a red underline | Spelling suggestions |
| Ctrl+Shift+G | Insert next missing tag |
| F3 | Search for the selected text in the concordance |
| Ctrl+Shift+L | Look up the selected text under “Resources” (dictionaries, termbases) |
| Ctrl+Shift+R | Research the selection or segment (show or create the dossier) |
| Ctrl+F2 | Add the selected source and target text as a term |
| Ctrl+Shift+T / E / U | AI translation / evaluate variants / check on the web |
| Ctrl+Shift+B | Clarify the selected term in context (term comparison) |
| Ctrl+Shift+D | Start / stop dictation |
| Ctrl+S | Save |
| F1 | Help |

## Databases: recommendations and overview

At the top of the **TM & Terminology** tab is the overview of all databases:

- **Which database for what:** **translation memory** (TM) – whole sentences with their translation for reuse;
  **terminology** – technical terms that are always translated the same way; **knowledge base** – style rules, subject
  knowledge and research results for the AI.
- **Recommendations:** if a project lacks a shared TM and terminology per language pair or a knowledge base per target
  language, the program names the type, the name (e.g. “TM de-DE–pl-PL”), the reason and the path. **Create** or
  **Create all recommended** creates them and binds them to the languages – they then apply to every project with these
  languages.
- **Notice when opening:** if such databases are missing, the project page shows a notice with **Create recommended**,
  **Always automatically from now on** and **View recommendations and paths**. With “automatically” the program creates
  them itself when a project is opened.
- **All databases** with type, origin, languages, entries and path: the shared folder (**Open folder**), all
  language-bound databases and the project databases of recently opened projects.
- **Use in this project** attaches a database – including the TM, terminology or knowledge of another project – to the
  current project (other projects' databases read-only). **Share for all projects with these languages** binds it to the
  languages. **Merge** copies all entries into another database of the same type; the source stays unchanged, duplicates
  are skipped.

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
- **Import existing translations** – see below.
- **Alignment by hand** – **Alignment editor (by hand)**: choose the source text and an existing translation (with languages);
  **Align** matches the sentences by length and numbers; confident pairs are preselected, uncertain ones yellow.
  Correct by hand: **Join ↓**, **Split** (sentence by sentence), **↑/↓** moves the first or last sentence of a side into
  the previous or next pair, edit the text directly – each line is a sentence, a line break splits a sentence
  (resegmenting). **✓** confirms a pair: it stays as a fixed anchor when you **Realign**. With AI: **AI checks pairs**
  rates every pair and deselects doubtful ones, **AI: realign uncertain parts** rematches uncertain sections by meaning,
  **Automatically with AI** does both and checks again. **Keep formatting as tags** matches the translation's tags to
  the source sentence so that TM matches bring the formatting along. **Selected into the TM** stores the pairs in the
  chosen TM or in a **new TM for this language pair**.

### Importing existing translations

Texts you have already translated become usable right away with **Import existing translations …** (**Project** or
**TM & Terminology** tab) – three steps:

1. **Choose files …** – all at once, several pairs and several target languages too. The program matches what belongs
   together: by file name (“Contract_DE.docx” ↔ “Contract_EN.docx”, “EN-Contract”, “Manual.en-GB”, “Report (polski)”),
   otherwise by the language detected in the content and shared numbers. One original can have several translations.
   Bilingual files (XLIFF, also .sdlxliff) contain both and need no alignment. Each row shows original, translation,
   languages and how they were matched; **⇄** swaps, **✕** removes. Files without a counterpart are listed.
2. **Where and how** – recommended is the **shared TM and termbase of the language pair**: it applies to every project
   with these languages and is created in the standard folder if needed. Alternatively **only into this project**.
   **AI checks the pairs** rates every sentence pair and realigns uncertain parts by meaning; **Suggest terms** has the
   AI name specialist terms with the translation used there. Without AI, sentence length and numbers decide.
3. **Import** – the confident sentence pairs go into the TM, with the neighbouring sentences as context (101% matches
   later) and – for confidential projects – only with placeholders. Uncertain pairs are left out; **Review** opens them in
   the alignment editor, **← Back** returns here.

The **suggested terms** are checked against the sentences: only what really occurs on both sides is kept (the
translation also inflected, e.g. “rachunek oszczędnościowy” in “na rachunku oszczędnościowym”). Invented pairs,
placeholders and terms already in the termbase are dropped. Each term shows its frequency and an example sentence; the
target term can be corrected before adding. **Add selected to the termbase** creates them.

### TM maintenance

Under **Edit and maintain TM entries**, choose the TM (project TM or a writable attached one) and:

- **Find and replace in all entries** – in the source and/or the translation, optionally matching case, whole words or a
  regular expression (`$1` inserts groups). **Preview** shows the changed entries, **Replace all** applies them. Tags
  (⟨1⟩ …) are never changed. If an entry becomes identical to another one, both are merged; their contexts are kept.
- **Clean up** – merge duplicates (differing only in whitespace), delete entries without translation and optionally those
  with target = source (identifiers and numbers stay).
- **Show inconsistent translations** – identical source sentences with different translations, to make them consistent or
  delete outdated versions.

### Own reference texts (PDF folder)

Under **TM & Terminology → Own reference texts** you add folders with your own texts: PDFs, Word and Writer files,
presentations, spreadsheets, text, Markdown or HTML – for example standards you own, manuals, data sheets, legal texts or
earlier translations. The program reads them itself (including subfolders) and builds a search index. Nothing leaves the
computer; only AI on this computer gets excerpts unless you tick **Also give excerpts to online AI**.

- **Add folder …** reads the folder right away; **Read in now** later reads only new and changed files and drops missing
  ones. If a folder is currently unavailable (unplugged drive), its files stay in the index. ✕ removes a folder; the
  files themselves are not touched.
- For each file the program detects the **language**, for PDF the **page**, and the **version**: what the document
  states itself ("Stand: 01.03.2021", "Ausgabe 2019-03", "Edition 2020", "Wydanie 2018", "DIN EN ISO 12100:2011-03"),
  otherwise the date from the PDF properties or the file. **Files read in** shows all of this; scanned PDFs without text
  are listed as not readable – they need text recognition (OCR) first.
- **Search index:** where the index is; **Folder …** moves it, ideally to a drive with plenty of space. It needs about as
  much as the plain text of the files (usually a fraction of the PDF size).
- The **test search** shows where a phrase occurs (file, page, sentence; a click opens the file) and how often.

**Research** (dossiers and term comparison) searches here first: for the source term and each candidate, verbatim
passages with file, page and version go into the dossier, and the AI learns how often each candidate occurs in your texts
(e.g. "mechanical seal: 14, sliding ring seal: 0") – a strong signal that small local models can use too. "Learned"
means looked up, not retraining the model: it stays current, traceable and deletable. If the same documents exist in two
languages, alignment (translated files into the TM) turns them into sentence pairs for the translation memory;
translation then gets them as TM matches.

### Downloading free language data

Under **TM & Terminology → Download free language data** the program finds freely available data: translation
memories from the OPUS archive (including the EU translation memory **DGT**, **Europarl**, EU law, EMA medicine
information, ECB publications, administrative texts from the ELRC initiative, user interface texts of free software,
everyday sentences from Tatoeba) and the EU terminology **IATE** (freely available complete edition, as of 2019). OPUS
covers more than 1000 languages; per language pair there are often dozens of datasets (German–English over 120,
German–Polish about 50, German–French almost 90). For English↔French the Government of Canada's termbase **TERMIUM
Plus** is offered as well – 23 subject fields, each downloadable separately (law, administration, medicine, economy,
informatics …, up to 30 MB each), with status (preferred, admitted, do not use), synonyms, abbreviations and
definitions; licence: Open Government Licence – Canada. For Romansh, OPUS has little (German–Romansh about 10,000
sentence pairs from Wikipedia); dictionaries and TERMDAT are listed under **Resources**.

How it works:

1. Choose **From** / **to** (suggested: the open project's languages; it also works without a project) and **Search**.
2. Narrow down: search text (name or description), kind (translation memory or terminology), subject field (law and
   administration, medicine, technology and software, business, general, subtitles and literature, collected from the
   web), **only licences for professional use** (hides “research only”, “non-commercial” and unclear ones) and the
   order (recommended, largest or smallest first, name). Recommended, freely usable datasets are shown first; **Show
   all** also lists very large, web-crawled or restricted ones. The line above the list says how many are shown.
3. Click a dataset: description, licence and disk space appear below; **Info** opens the dataset's page.
4. **Download and attach** – the program downloads from the official source, takes only the chosen language pair (even
   if the data is in the opposite direction) and attaches the new database: **for all projects with these languages**
   (default) or only for the open project. It does not write back. Afterwards the entries appear in the editor as TM
   matches or terms.

A TM with its search index needs about 2.5 KB per sentence pair (DGT German–English about 9 GB); **Folder …** chooses a
drive with more space. Most licences require attribution.

## Terminology

Each entry (concept) consists of terms in several languages, each with a status (preferred, admitted, forbidden),
plus a definition and a subject area.

- Create entries under **TM & Terminology → New entry** or in the editor with Ctrl+F2.
- **Import TBX / terminology XML**, **Export TBX**.
- In the editor, recognized terms appear under **Terms**; QA reports when a prescribed
  term is missing or a forbidden term has been used.

### Extracting terms from the project files

Before translating, under **TM & Terminology → Extract terms from the project files**: choose the target language and
**Find terms**. The program finds recurring terms and phrases in the source texts (in German especially nouns and long
compounds; phrases that only occur as part of a longer one are dropped). With **AI picks terms and suggests translations**
the real specialist terms remain, each with its usual translation – only terms that occur in the texts and none that are
already in the termbase. Check, correct the translation, **Add selected to the termbase** (shared termbase of the language
pair or only the project). Without AI the candidates appear by frequency for ticking and translating by hand.

## Resources: dictionaries, termbases, legal sources

The **Resources** tab collects termbases, dictionaries, corpora, legal sources and language advice from public and
non-profit publishers – for example IATE, TERMIUM Plus, TERMDAT, FranceTerme, UNTERM, DWDS, Wiktionary, CNRTL, the
dictionary of the Polish Academy of Sciences, Pledari Grond (Romansh), EUR-Lex, Fedlex, ISAP and the EU style guide.
Each entry names its kind, languages, publisher and terms of use and says how to use it: *built into the program* (the
agents use it), *downloadable as a database* (**Download …** leads to “Download free language data”) or *in the
browser* (**Open**).

- **Recommended for this project** (also in the Project tab): matching the source and target languages and the subject
  field, every project language with at least its own dictionary.
- **Look up**: enter a term or select it in the editor and press **Ctrl+Shift+L**, choose the language, click a
  dictionary – the browser opens the page with the term.
- **All resources**: filter by kind, language and search word.
- **Usage on the web**: is an expression used like this in certain countries at all – and how often? Enter the
  expression and variants separated by “|” (“Jänner | Januar”), choose the language and countries (suggested: for German
  DE, AT, CH; for French FR, BE, CH, CA …) and click **Check**. For each variant and country the exact phrase is searched
  through an official search interface with a country filter – Brave (up to 10 pages of 20 results) or Tavily (20
  results), set up under **Set up AI → Web search for the agents** with your own key. Counted are the results that
  contain the phrase verbatim, and how many of them are on websites of that country (.at, .ch …); “≥ 40” means all
  checked results matched and there would be more. Example pages to open are listed below. Before checking, the number
  of requests is shown (variants × countries × pages). Estimated totals (“about 1,230,000 results”) are not provided by
  any official interface; for the question “does it occur at all?” the counted evidence is more reliable anyway. In the
  editor, **Concordance → On the web by country …** takes over the phrase.

The program does not read these pages itself; opening one in the browser appears in the outgoing-data log.

## Quality assurance

**QA all files** (Project tab) or **Check document** (editor) checks for:
empty segments, target = source, tags, numbers, spaces, end punctuation, brackets, terminology,
repeated words, consistency (same source – different translations), form of address (see document brief), length, spelling,
unwanted words and **custom rules** with regular expressions (optional source pattern, target pattern,
severity). Settings are under **QA settings and custom rules**; extensions can contribute further
checks.

Spelling: under **Settings → Spell check**, download the dictionary for the target language
(free dictionaries from the LibreOffice project).

### Grammar check

In the editor under **Messages → Check grammar** all translations are checked for grammar, case and agreement, verb forms,
spelling and punctuation; the findings appear under “Grammar” in the message list, with a suggestion. The engine is chosen
on the **Set up AI → Grammar check** page:

- **AI (language model)** – default; good for Polish too. Confidential details go out only as placeholders, as
  elsewhere; only what really is in the text counts.
- **LanguageTool on your own server** – free software (LGPL); the texts stay in-house. Start it e.g. with Java:
  `java -cp languagetool-server.jar org.languagetool.server.HTTPServer --port 8081` (package from languagetool.org)
  or with Docker; enter the address (default `http://localhost:8081`).
- **LanguageTool public** – free but limited (20 requests and 75 KB per minute) and not meant for automated bulk
  checks: the program uses it only when you click, bundles the segments and stays within the limits. It is not used
  for confidential projects.

### Number format, maximum length, QA profiles

- **Target language number format** reports numbers and dates still written in the source language form (“1.234,5 kg” in
  an English translation). **Adapt numbers and dates** in the Project tab rewrites them in all files in the target
  language form (1,234.5; 12.03.2024 → 12/03/2024); with **Adapt numbers and dates automatically** this happens by itself
  for TM matches and AI translations. Version numbers, times and IP addresses stay; units are not converted.
- **Maximum length of each translation** (characters) applies to all segments; limits from the file (XLIFF `maxwidth`)
  apply as well.
- **QA profile** – save a project's QA settings under a name (e.g. per client) and **Apply** them in other projects.

### Does the translation convey the meaning? (AI check)

The formal QA finds numbers, tags and terminology, but not errors of meaning. Two methods cover these:

- **Back-translation:** a model translates the translation back into the source language without seeing the original;
  the AI then compares the meaning with the original and names what is missing, added or changed (numbers, negation,
  who does what, wrong terms).
- **Comparison of several models:** several models translate independently; if the translation deviates from them in
  meaning, it is suspicious. With the free group the suggestions come from different models at once; with a single
  model the comparison is less meaningful.

In the editor under **AI → Check translation** (for the selected segment): verdict (plausible, please check, probably
wrong) with the findings, the back-translation and the independent translations – each can be inserted as a draft with
**use**. **Messages → Check with AI (meaning)** checks the whole document (back-translation; **also compare several models** is
more thorough but needs about twice as many requests); doubtful segments appear as “AI check” issues and can be reached
with the “with issues” filter. The AI check is a hint, not a replacement for review by a human.

## Pre-translation and analysis

- **Pre-translate (TM)** inserts matches at or above the configured threshold; context matches are confirmed if you choose.
- **Analysis** counts segments, words and characters per match band: context, repetitions, 100%, 95–99%,
  85–94%, 75–84%, 50–74%, no match – the basis for quotes.
- **Fill empty with AI** (editor) translates all empty segments with AI; the result is a draft that must be
  reviewed and confirmed. First the program asks **Research as well?**:
  - **Yes, with research**: before each segment the AI finds its expressions that need research (if "Scan document" has
    not found them yet, it reads the segment), researches those without a dossier – specialist websites first, then
    general – and translates the segment with the evidenced results. An expression in several segments is researched
    only once. Failed research does not stop the translation.
    You choose the **time per expression** in the same dialog (the field with the arrow). The default is **Smart – the AI
    assesses each segment and allocates the time**: before researching, the AI assesses each segment – how difficult
    (easy, medium, hard, very hard, with a reason), what to do with each expression (research with 0.5 to 15 minutes –
    more for difficult terms, less for well-known ones; **keep unchanged** for proper names, brands and products that
    stay the same in both languages; **not needed** if context or termbase already settle it) and how many neighbouring
    segments before and after (0 to 3) it needs as context – for the research and for the translation. Something simple
    like “Hello” needs nothing at all. After each research the program checks whether it is enough: evidenced and
    conclusive is enough without asking; otherwise the AI says what is missing (e.g. the definition in the target
    country, the usage in this kind of text), and a **second round** with double the time (3 to 15 minutes) searches
    specifically for it – building on what was found, at most once per expression. Whatever is still uncertain afterwards
    is listed in the report under **Research more thoroughly?**. Every research stops as soon as it is enough: once a
    candidate is in two independent target-language sources, no further page is read – the time budget is only the upper
    limit. The log shows the assessment per segment (“Segment 3 – assessment: hard … “Raindancer” stays unchanged ·
    “GLRD” 4 min”); in the segment list a coloured dot appears under the number (green easy, yellow medium, orange hard,
    red very hard), its tooltip shows the same line. Whatever is rated “stays unchanged” goes into the translation as a
    “keep unchanged” hint – without research, because a name needs no evidence. **The assessment learns the speed of
    this computer**: every research is recorded (AI, difficulty level, time budget, duration, evidenced or not – numbers
    only, no expressions; the last 500, in `recherche-statistik.json` in the data folder). The assessment is told how long
    expressions of each level typically take to be evidenced with the configured AI and sets the minutes accordingly;
    from four evidenced researches of a level, the measured time (with some margin) also applies when the AI names no
    time itself. A slow computer thus gets more time and a fast online AI less. Alternatively a fixed time: 2, 5, 10 or 20 minutes or no time limit (then at most 4 expressions per
    segment, the most difficult first; for a single segment all of them).
    **Several expressions at the same time**: while one waits for web pages, the other uses the AI – 2 at once with the
    built-in AI, 3 with an online AI, with a pool as many as models are available (at most 4). Each log line then starts
    with the expression in square brackets; the ⏱ lines show what it gains.
    So that the translation does not get stuck on a difficult expression, each step (finding expressions, assessing,
    researching, checking on the web) is cancelled after 4 minutes at the latest (with a longer time budget after twice
    the budget); the log in the **AI** tab notes it and the work continues without it. **Research** in the
    **Research** tab searches more thoroughly; its queue pauses while **Fill empty with AI** runs, because the built-in
    AI answers one request after another. Names, addresses and other protected data as well as mere dates and numbers are
    never researched (see privacy); the log lists them.
  - **No, just translate**: as before, with the existing dossiers.
  - **Then check every translation on the web** (on by default): after translating, the AI checks every translation
    like **Check on the web** – are sentence structure and word combinations usual in the target language, are the words
    used like this? – knowing the research for each part of the sentence (evidenced or not). Questionable wording is
    replaced by the better version (as a draft).
- **Selected with AI** (editor, next to **Fill empty with AI**): the same for the selected segments – ones already
  translated are translated again (as a draft), confirmed and locked ones stay unchanged. Select several segments with
  **Ctrl+click** or **Shift+click** on the segment's number or status (a click into source or target selects only that
  one); without such a selection the current segment is used. The same question **Research as well?** appears with the
  same choice of **time per expression** (smart, 2, 5, 10 or 20 minutes or **no time limit**); for a single segment with a
  fixed time all its expressions are researched, not just the four most difficult.
- **Translate automatically** (editor, next to **Selected with AI**): everything in turn, the way a careful translator
  works. First the same question **Research as well?** appears; the AI review and the follow-up research can be
  deselected there. The steps:
  1. **Read the document profile** (if still empty) – text type, audience, tone, form of address.
  2. **Translate** – the empty segments, with research per segment and web check if wanted (like **Fill empty with AI**).
  3. **QA check** – numbers, tags, terminology, length (result in the **Messages** tab).
  4. **AI review** – the AI evaluates all translations (like **AI evaluates the file**).
  5. **Follow-up research of uncertain expressions** (only with research) – up to five expressions that need more
     research (see below) are researched more thoroughly (5 minutes per expression); if this changes the evidenced
     translation, the affected segments are translated again.
  6. **Apply the review's improvements** – where the AI suggests a better version and the wording is in the translation,
     it is inserted; the rest is left to check by hand.
  7. **Re-evaluate changed segments** – the score before → after.

  Confirmed and locked segments stay unchanged; everything new is a draft. A step that fails does not stop the others;
  **Cancel** ends the run. At the end the **Report** opens with the log expanded.

  **How the program recognises that an expression needs more research** (section **Research more thoroughly?** in the
  report, per expression with the reasons and the button **Research thoroughly**, which queues it without a time limit
  in the **Research** tab): not evidenced (no target-language source confirms the translation), criticised by the review
  (terminology or mistranslation in a segment with the expression – weighs more if the comment names it), rated
  questionable by the web check, not conclusive (no candidate in two independent sources), time budget reached. If the
  expression occurs in three or more segments, that weighs more – a wrong choice has several effects; frequency alone is
  not a reason, though.
- **Improve wording** (side tab **AI**): the same for existing translations – for the selected segment, or with
  **Improve all segments** for all translated segments that are not confirmed yet (confirmed ones stay untouched).
- **What the AI collected**: for each segment, the **AI** tab (when you click it) shows what was researched and checked:
  every expression with its equivalent, meaning, reasoning and source (the link jumps to the spot), whether it was
  evidenced and went into the translation, the web check with numbers, verdict and explanation, and if applicable
  "Improved: old → new". The overview **What the AI collected (all segments)** lists all segments with a summary; a
  click jumps to the segment.
- **Create report** (**AI** tab): an HTML page in the project folder under `berichte` that opens in the browser: which
  sentences and expressions were translated how and why – all researched expressions with translation, evidenced or
  not, meaning, strategy, reasoning and clickable evidence; then every sentence with source, translation, origin (AI, TM
  match, by hand), research, web check and improvement. The web checks come from the current session, the dossiers from
  the project.

### Quote from the analysis

After **Analysis**, the price appears under **Quote**: base price per word, character or standard line (55 characters),
currency, VAT and minimum fee; under **Share of the price per match band**, e.g. 25% for repetitions and 100% matches, 0%
for locked segments. For each target language you see units, weighted units, the savings through matches and the amount,
below it net, VAT and total. **Save quote as Word** creates the quotation. Prices are saved in the project and taken over
by new projects.

### Pseudo-translation

**Pseudo-translation …** creates the target file of the selected file with accented letters (“[Ɓéţŕîéƀšàñĺéîţüñĝ ~~~~~~]”)
and 30% longer text, without changing the project. Opened in the original program, it shows before translating whether
special characters come through, longer texts fit the layout and no text position is missing (untranslated text stands
out immediately).

## Knowledge bases

In addition to TM and terminology, there is **Knowledge**: subject knowledge, definitions, language usage and style rules,
each with language, topic, source and author. Every project has its own project knowledge base; further databases can be
attached per project and per language – locally or on the team server. Without an open project you can choose the general
**AI memory** (where the agents store their research) and all knowledge bases in the user folder.

**Language-bound databases:** when attaching under **TM & Terminology → Attach resource**, **for all projects with these
languages** binds a TM, termbase or knowledge base to the languages instead of the project. It then applies automatically
to every project with these languages (without languages: those of the current project) and to the agents even without a
project; the list marks it “for all projects with these languages”. **Detach** detaches it for all projects.

- **Knowledge → Browse** tab: search, create and edit entries.
- In the editor, the **Knowledge** sidebar shows relevant entries; they are also included in every AI translation and
  evaluation. Style rules for the target language always apply.
- **Knowledge agent**: choose the target database or create one under **Create new database** (with a project open it is
  attached right away), specify a topic and languages (more languages in the field next to them). The agent gets to grips
  with the subject like a person: it **breaks the subject into aspects** (for welding e.g. processes, machines and how they
  are built, materials, standards and safety), searches and reads reliable sources for each **in every selected language**
  and creates entries backed by a **verbatim quote** (topic "Welding – Materials"). If the quote does not actually appear
  on the page that was read, the entry is discarded. Then it checks which aspects are still empty in which language and
  **searches there with new queries** (up to three rounds); at the end the log shows the coverage per aspect and
  language. The program remembers specialist sites that delivered entries and searches them first next time.
- **Ask the knowledge** (**Knowledge → Ask** tab): after research – e.g. on welding: what it is, which processes, which
  machines, how they are built, which materials – you ask questions such as "How is a MIG/MAG welding machine built?".
  The program has the AI form search terms in the languages of the knowledge (a German question also finds English
  entries), picks the matching entries of all knowledge bases shown (or, with **search only in this database**, only the
  selected one) and passages from your own reference texts, and lets the AI answer **only from them**, citing every
  statement with [n]. Below are the **sources of the answer** – the cited ones first, with quote and link. If something
  is not in the knowledge, the AI says so and names what is missing. Every statement should have a source: sentences of
  the answer without [n] are reported ("Caution: 1 statements without a source"), and so are cited notes without a link
  ("cannot be checked"); each note shows whether it has a source. If the knowledge has nothing yet on a term from the
  question, the program says so plainly ("I do not have any information on “Gleitringdichtung” yet."). With **Research
  missing information on the web right away** (on by default) the knowledge agent immediately searches exactly that,
  stores it in the selected database and answers the question again; if it finds nothing supported either, the answer
  says so. Without the tick, **Research further** does it on request. Because the program selects the notes, a small
  local model is enough.
- **Study a law** (**Knowledge → Study** tab): enter the abbreviation of a German federal law as used on
  gesetze-im-internet.de (StGB, StPO, BGB, ZPO, HGB, GG …) and the target language. The program downloads the
  **official version** with its **version date** ("last amended by …") – official works are free to use – and studies
  it in three steps:
  1. Every section goes into your **own reference texts** (listed under **TM & Terminology** as a source with its web
     address; scanning the folders leaves it in place). Research and **Ask the knowledge** find passages in it with a
     reference such as "StGB – Strafgesetzbuch, § 242 Diebstahl".
  2. The **terms** are extracted: **legal definitions** ("Notwehr ist die Verteidigung, die …", "Verbrechen sind
     rechtswidrige Taten, die …") and **definition lists** as in section 11 of the Criminal Code ("Amtsträger: wer nach
     deutschem Recht …"), plus the **headings** with the first sentence of the section as definition. Sentence openings
     such as "Jedoch ist …" or "Rechtswidrig ist …" do not count as terms. For the Criminal Code that is about 460 terms.
  3. They are stored with the section as reference in the **shared termbase of the language pair** (e.g. "Terminologie
     de-DE–en-GB", created if missing) and as definitions in the selected **knowledge base** (with none selected, in
     "Wissen de-DE", bound to German and therefore used in every project with German). Nothing is created twice;
     studying the law again after an amendment only adds what is new, and your own reference texts get the new version.

  With **English** as the target language, the English terms from the translation published by the Federal Ministry
  of Justice are added: from the headings ("§ 242 Diebstahl" → "Theft") and the definition lists ("Amtsträger" →
  "public official"). That translation is **not official and is protected by copyright**; the program therefore takes
  over only single terms – as **admitted**, not preferred, with a note on the unofficial translation – and no sentences
  or sentence pairs. For other target languages only the German terms with definitions are stored; research then
  finds the equivalents.
- **Study own documents** (also under **Study**): the list shows the files indexed under **TM & Terminology → Own
  reference texts** (PDF, Word, Writer, text …); tick what is to be studied and set the language pair of the termbase.
  **Extract terms** looks for the terms that are explained there:
  - **Defining sentences**: "Unter einer Gleitringdichtung versteht man …", "Als Kavitation bezeichnet man …",
    "Der Spaltring ist ein …", in English "X is a …", "X means …", "The term X refers to …", in Polish "X to …",
    "X oznacza …". Sentence openings such as "Ziel ist eine …" do not count.
  - **Abbreviations**: "Gleitringdichtung (GLRD)", "Bundesamt für Sicherheit in der Informationstechnik (BSI)" – the
    long form becomes the term, the abbreviation an admitted second term.
  - **Glossary lists**: lines such as "Laufrad: rotierendes Bauteil …" (from three such lines, or after a heading such
    as "Begriffe", "Glossary", "Terms and definitions") and the standards style "3.1 Saugstutzen" with the explanation
    on the next line.
  - With **AI reads along** the AI also looks for explained terms. Its suggestion only counts if the supporting
    sentence appears **word for word** in the passage and contains the term – invented sentences are dropped. Online
    AI reads the documents only if this is allowed for the own reference texts; otherwise only the rules apply.

  The definition is always the sentence from the document. Each term shows file and page, how often it occurs in all
  your own reference texts and where it comes from; **Open** shows the document at the reference (for PDFs on the
  page). Nothing is saved until **Apply**, and only what is ticked: into the shared termbase of the language pair
  (terms in one of the two languages, definition with reference) and into the knowledge base selected above (with none
  selected, "Wissen" of the source language) – nothing twice.
- **Topic → TM (EU law)** (also under **Study**): enter a topic and the language pair, e.g. "Maschinen", de-DE →
  en-GB, and click **Search legal acts**. Via the SPARQL interface of the Publications Office the program searches
  for EU legal acts whose title in the source language matches the topic and which also exist in the target language.
  Words are cut to their stem ("Maschinen" also finds "Maschinenrichtlinie"), several words must all appear in the
  title, and a comma separates alternatives ("Maschinen, Aufzüge"); a CELEX number such as 32023R1230 searches exactly
  that act. Regulations and directives come first, newer before older, amending and implementing acts further down;
  the first three are ticked, **Open** shows the act on EUR-Lex. **Build TM** downloads the official versions in both
  languages and pairs them: every language version is authentic and structured the same way, so paragraphs are paired
  one by one – but only if both versions have the same number of paragraphs of the same kind; otherwise the act is
  skipped. Within a paragraph, sentences are paired if both have the same number, otherwise the whole paragraph (up to
  600 characters). Footnotes, bare headings such as "Article 46" and pairs with differing numbers are dropped. The
  Machinery Regulation yields about 1,800 sentence pairs this way. They go with the **CELEX number as origin** and the
  neighbouring sentences as context into a TM of their own ("TM Maschinen (EU-Recht) de-DE–en-GB", name can be
  changed), which is bound to the language pair and therefore gives matches in every project with these languages;
  with an open project of the same language pair it is attached right away. Nothing is created twice. Reuse is
  permitted (Decision 2011/833/EU), source: EUR-Lex, © European Union; only the version published in the Official
  Journal is authentic. Only official EU languages are possible. For very general collections (all EU translations of
  a language pair) the large OPUS memories are available (**Download free language data**); "Topic → TM" instead
  delivers the current legal acts of one subject area.
- **Team activity**: who in the team has recently contributed what (only with a team server).

All agents keep running in the background when you switch tabs or projects: they use their own connections to their
databases and store their result in the database they were started with. The status bar reports when they are done;
while they wait for the model, the log shows “⏳ Waiting for …” every 20 seconds. Closing the program stops them.

## Setting up AI

Everything about AI is on its own **Set up AI** page (in the bar on the left): choose a way and click **Use** – the
program creates the connection, tests it and (optionally) uses it for translation, research and evaluation. The page
also holds the connections for editing, **Who does what** and the **web search**.

- **Free – several models at once** (at the top): **Use the ticked models together** looks for all free models that
  need no account (OVHcloud, Kilo), plus the built-in AI (if downloaded) and free connections you already set up
  (OpenCode, local servers), ticks the recommended ones, checks each of them at the same time with a short translation
  and creates a **group** from all that answer. **Find free models** lets you adjust the selection first. The group
  spreads a whole file over all models at the same time (Editor → AI → translate all); if a model hits its provider's
  rate limit it pauses as long as requested and another takes over, if one fails the next one tries the same segment.
  In a test, a group of five models translated 12 segments in about 50 seconds – a single OVHcloud model would need
  about six minutes because of its limit of 2 requests per minute. Because free models like to add things, the
  program checks every translation: if it has more sentences than the original or is noticeably longer, another
  model of the group translates the segment again. Services with a free key (NVIDIA, Groq, Gemini, Mistral, Cerebras,
  OpenRouter) join as soon as their key has been entered and connected under **Free online**.

- **Free on this computer** (built-in AI, Windows, macOS, Linux): an open language model (Google's Gemma 4 or
  EuroLLM, both Apache-2.0) runs directly on the computer with the llama.cpp runtime – no account, no key, no daily
  limit, and the texts never leave the computer. Choose **Gemma 4 E2B** (2.8 GB, fast, also without a graphics
  card), **Gemma 4 E4B** (4.6 GB, better translations), **Gemma 4 12B** (7.2 GB, the most accurate translations of
  the built-in AI; needs a graphics card with at least 10 GB – on a 12 GB card it sits entirely in graphics memory and
  takes about three times as long per sentence as E4B) or **EuroLLM 9B** (5.6 GB): a model by European universities
  and research institutes, trained for all 24 official EU languages and built mainly for translation. It takes
  about 7 GB of graphics memory (so the card should have at least 8 GB) and needs about twice as long per sentence as
  E4B; on the processor alone it is very slow. In a test with 16 sentences (chrF against one reference each) EuroLLM
  was better than Gemma 4 E4B into Polish (73 instead of 70 points) and into German (79 instead of 78), weaker into
  English (75 instead of 79) and into French (72 instead of 81). Gemma 4 12B was best in all four directions of the
  same test: 79.6 points on average against 76.9 (E4B) and 74.7 (EuroLLM), most clearly into German (82 instead of 78)
  and into Polish (73 instead of 70); with evidenced research dossiers 79.5. With a graphics card of at least 10 GB,
  choose the 12B; otherwise E4B, and EuroLLM for Polish. **Download and use** fetches the runtime and model once from
  the official
  sources (GitHub, Hugging Face) and verifies their checksums; an interrupted download continues where it stopped.
  **Folder …** chooses a drive with more space; **Delete model** frees the space again. A graphics card is used if
  available. New installations use the built-in AI by default: the editor then shows a notice with **Download now**
  until it is ready. Small local models translate solidly but do not reach the quality of large cloud models – for
  demanding texts choose the larger model or another connection.
- **Free via OpenCode**: the program starts OpenCode in the background and offers its free models (free ones first,
  with a note on data use). If OpenCode is not installed, **Download OpenCode** fetches it from the official release
  (MIT license, about 80 MB). The requests go through OpenCode itself, as OpenCode requires for its free models; no
  account is needed. The free models have a daily quota per internet connection; when it is used up, the program
  says when it is available again.
- **Local on this computer**: **Search** finds Ollama, LM Studio, llama.cpp and similar servers automatically,
  including their models. Free and confidential – the texts never leave the computer.
- **Free online**: without signing up, **OVHcloud AI Endpoints** (data centres in the EU; without a key 2 requests
  per minute and model; proven: gpt-oss-120b) and the **Kilo Gateway** (free models, about 200 requests per hour; the
  providers behind it may log) work: choose the service and click **Use** (it connects by itself if needed). With a free key there are also
  OpenRouter (models ending in “:free”), Google Gemini, Groq, Mistral, NVIDIA NIM and Cerebras – **Get key** opens the
  provider's page. If a service hits its rate limit, the program waits briefly and tries again. Free cloud tiers
  partly store inputs – not for confidential texts.

Manual setup is under **Advanced: edit AI connections manually**.

**Set up AI → Advanced: edit AI connections manually**: choose a template (Claude, local with llama.cpp/Ollama/LM Studio, OpenAI,
Mistral, OpenRouter, custom server …), adjust the address and model, click **Test selected connection**, and save.

- API keys can be entered as `env:NAME` (e.g. `env:ANTHROPIC_API_KEY`); the key itself is then not stored
  in the settings file.
- **Who does what**: choose a separate connection for translation, research and evaluation –
  e.g. translate locally, research in the cloud.
- **Local or cloud**: for each connection, the settings show whether the texts leave your computer.
  Use a local connection for confidential jobs.
- **Web search for the agents**: **several services can be ticked at once** – **Wikipedia** (free, no key, default
  for new installations), a **SearXNG** metasearch (own server or set up with one click on this computer, see below),
  **Tavily**, **Brave Search** and **Claude web search**, each with its own address or key. Like a person looking things
  up in different sources, research asks all ticked services at the same time, mixes the results in turn and takes
  duplicate pages only once; if one fails (no network, Docker stopped, quota used up), the others keep searching.
  **Test web search** checks each service on its own. Services without key or address are left out; if none remains,
  the program searches Wikipedia. For Brave and Tavily, **Get a key** opens the provider’s sign-up; both have a free
  monthly allowance (Tavily: 1000 queries without a credit card; Brave: $5 credit, about 1000 searches, credit card
  required). Only with such a search can the AI search individual specialist websites. Local models cannot search on
  their own; this service does the searching for them.
- **Free web search on this computer (SearXNG)**: one click sets everything up. The program checks whether Docker is
  installed and running (otherwise it starts Docker Desktop), sets up the SearXNG metasearch in a container – reachable
  only at `127.0.0.1:8888`, not from the network –, switches web search to it and tests it. If Docker is missing, on
  Windows the program downloads Docker Desktop from the official source (about 600 MB, needs about 6 GB) and installs
  it for this user only; program and data go into the **Docker folder** shown (default: next to the built-in AI, so
  the system drive stays free). By clicking **Install Docker and set up web search** you accept the Docker license:
  free for individuals, education and companies under 250 employees and $10M revenue, larger companies need a
  subscription. WSL 2 is required; if it is missing, run `wsl --install` once with administrator rights and restart.
  On macOS and Linux the Docker guide opens; click again afterwards. Docker itself may not be redistributed with the
  program and is therefore only downloaded on request. If the instance is stopped or Docker has quit, the next search
  starts it by itself. **Stop**, **Remove** (container and image; Docker stays) and **Check status** are next to it.
  If Docker does not start (e.g. after an incomplete installation), **Reinstall Docker** appears: it downloads Docker
  again from the official source and then continues the setup. SearXNG queries search engines such as Google, Bing or
  DuckDuckGo – their terms apply, and they refuse requests at times; best tick it together with Tavily or Brave.
- For small local models, reduce **Characters per web page** to about 2000–3000.

### Machine translation (MT services)

On the **Set up AI** page under **Machine translation**: choose DeepL, Google Cloud Translation, Microsoft Translator or
the free LibreTranslate (on your own computer or server), paste the key (for Microsoft also the region, for LibreTranslate
the address) and click **Use for translating** – the service is tested briefly and chosen for translating. Research,
checks and agents keep using the language model, because an MT service can only translate. Tags travel to the service as
XML and come back unchanged; privacy applies as for AI (placeholders). An MT service does not understand instructions,
terminology or context – the terminology check of QA shows where to improve.

**Offline on this computer (OPUS-MT):** free, no key and no graphics card, also on Android. For each language direction
the program downloads a translation model from the University of Helsinki once (about 300 MB) and checks its
checksums; after that no text leaves the computer while translating. Choose the direction under **From** and **To**
(preset from the open project) – the display says whether there is a direct model, whether the way goes via English (two
models, weaker) or whether there is none, and how big the download is. **Download model** or go straight to **Use for
translating**; downloaded models can be **removed** one by one. They are stored next to the built-in AI (folder
selectable there). About 300 directions between European languages, plus Chinese and Japanese.

For orientation (own test with 16 sentences from contracts, engineering, letters, medicine and software): one sentence in
about 0.3 seconds on the processor, quality roughly like the built-in AI Gemma 4 E2B, better in Polish. On a computer with a
graphics card the built-in AI with Gemma 4 E4B translates better and follows terminology and instructions. Typical
weaknesses: idioms are translated literally, technical terms are sometimes wrong, forms of address follow no instruction.
Meant as a quick draft for post-editing.

### Setting up dictation

Under **Dictation**, choose a speech recognition: **Groq** (free key, very fast), **OpenAI** or **local** (Whisper on your
own computer, e.g. speaches with faster-whisper – the recording never leaves the house). Address, model and key are
suggested; **Use for dictation** saves.

## Agents

**Agents** tab (most functions are also available directly in the editor under **AI**):

- **Research (AI in control)** – a task or question in your own words, e.g. “What is ‘Abkantpresse’ (sheet metal
  working) in Polish? With evidence.” The AI decides step by step on its own: look things up in the project databases
  (TM, terminology, knowledge, earlier research), search **Wikipedia**, **Wiktionary** or the web, read an article
  with its language versions or a web page, apply a **skill**, **remember** something – the program carries out each
  step and supplies the content. Wikipedia results already contain the titles in the target languages, often the
  very term you are looking for. Result: an answer with sources; every step can be reviewed. Models with function
  calling (online services, built-in AI) call the tools directly, others get a simple protocol. Optionally the
  answer is stored in memory (project knowledge; without a project a general AI memory). If a service does not answer
  tool requests, the agent switches to the simple protocol automatically.

  **Evidence check:** after every research the program itself – not the AI – checks whether the proposed term (the
  proposed translation or the highlighted terms of the answer) really appears in a source that was read: Wikipedia
  articles with their language versions, web pages, legislation, dictionaries, project databases; inflected forms count.
  Result: “✔ … appears in: …” with the passage, or “⚠ … does not appear in any source that was read – please check
  yourself”. The program's own hints never count as evidence.

  On top of that come **official sources** that the AI queries directly through their interfaces:

  | Tool | Source |
  |---|---|
  | Legislation (`law`) | Germany: federal law on gesetze-im-internet.de (including single provisions such as “§ 35 VwVfG”); Poland: the Sejm legal database (Dziennik Ustaw, Monitor Polski, consolidated texts first); United Kingdom: legislation.gov.uk; EU: EUR-Lex in every official language |
  | Dictionary (`dictionary`) | German: DWDS (entry, valid spelling, part of speech, frequency class 0–6, pronunciation); every language: Wiktionary; plus the national dictionaries of the “Sprache” (language) profile |
  | Specialist sources (`sources`) | the specialist websites per field and language together with the legal or professional system (see *Specialist source profiles*) |
  | Site search (`search` with `site`) | web search restricted to one domain or to all specialist sites of a field – needs Brave, Tavily, SearXNG or Claude |
  | Remember a source (`add_source`) | the AI adds newly found official sites to the matching profile |

  The AI reads long texts (laws, judgments) selectively: when it opens a page or PDF with a search word it only
  receives the passages containing it – inflected forms included. The same EU act in two languages is the official
  equivalent of a term; the program reads EUR-Lex texts through the document interface of the Publications Office,
  because the EUR-Lex pages turn programs away.

  **Legal terms** are compared between both legal systems: the AI anchors the term in the provision of the source
  law, reads the counterpart in the target law, compares scope, requirements and legal effects and proposes a
  translation with a strategy. Besides the answer, the result shows a **proposed translation** and a **translator’s
  note** in the target language – ready to use as a footnote or “translator’s note”. Example: “Verwaltungsakt
  (§ 35 VwVfG) into Polish” gives “decyzja administracyjna”, noting that the German term is broader (general
  orders, measures without formal proceedings).
- **Skills & memory** – **Look up in the databases** searches TM, terminology, knowledge and memory like the agent does,
  e.g. whether a phrase has already been translated or a term already researched. **View stored research** opens the
  memory in the **Knowledge** tab. Skills are proven procedures in the open SKILL.md format (Markdown): built in are
  “research a technical term”, “check usage”, “compare legal terms”, “check proper names” and “find official
  sources”. If a skill clearly fits the task, the AI receives its procedure right at the start. Newer versions of
  the built-in skills replace older ones unless you changed them yourself. After successful
  multi-step research the AI creates new skills itself (“learned”) – modelled on self-improving agents such as
  Hermes. All skills can be read, changed, created and deleted; they are files in the user folder under `skills`.
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
  alternatives, **reads the pages found** and takes the sentence containing the phrase, counts verbatim occurrences and
  bases its verdict on the evidence. If there are too few (fewer than three), it says so and searches on – first with
  the subject field, then with the AI's own ideas (typical contexts, kinds of texts such as manuals, data sheets,
  standards, forums). The result starts with a plain statement from the counted evidence, e.g. "“Drehkraft anziehen”
  is not usual in German: no verbatim example in 12 results. The usual form is “Anzugsdrehmoment”: 3 verbatim examples
  ([1], [2], [3])." Without a single example an expression never counts as usual, even if the AI thinks so. Every
  example is listed with its number, the sentence from the page and a link that jumps straight to that sentence in the
  browser; if a page could not be read, the excerpt from the result list is shown and marked as such.
- **Evaluate translations** – scores several variants for accuracy, fluency, terminology and style,
  with reasoning and a suggested improvement; questionable phrases can optionally be checked on the web.

The **Agent log** shows every step: search queries, pages read, pages skipped.
AI results are suggestions – professional responsibility remains with you.

## Specialist source profiles

Under **Settings → Specialist sources by topic**, each topic (law, medicine, engineering, language, EU and public
administration – freely extensible) has one line per language:

`Language | System | Domains, comma-separated`

Example: `de-DE | German law (BGB, HGB, ZPO, StGB …) | gesetze-im-internet.de, dejure.org`.
`*` applies to all languages. The agents search these sites first and only then search the web in general.
**Check specialist sites** tests whether the sites permit reading and return text. Sites that prohibit automated
reading or technically prevent it do not belong in the list – the program does not circumvent such restrictions.

The **Sprache** (language) profile lists dictionaries and language advice per language (e.g. DWDS, Duden, grammis
for German; PWN, WSJP, Rada Języka Polskiego and NKJP for Polish; Cambridge, Oxford, Merriam-Webster for English).
Some of them only appear in search results and the program does not read their pages: DWDS and Larousse have a TDM
reservation, Duden is closed to programs, the English dictionaries turn programs away. The AI queries the DWDS
through its official interface instead.

The AI can add new official sites to the matching profile itself (shown in the log as “New specialist source”).
Where robots.txt asks for a minimum interval between two requests (crawl-delay), the program keeps to it.

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

### Workflow steps: translation, review, approval

Each file of a shared project goes through **translation → review → approval → done** per target language. In the
**Project** tab under **Team workflow steps**, the team server's administrators set who does which step and can set the
step. Whoever's turn it is may edit; for everyone else the file is read-only in the editor (with a note on who has it).
**Hand over** passes the file to the next step. Without assignment everything stays as before: everyone with write access
may edit. The server enforces this itself; older program versions and existing projects keep working unchanged. The same
rules apply to the REST interface (see the API reference).

## Automation: watched folders

Under **Settings → Automation**, add inbox folders: folder, project, output folder (empty = subfolder “fertig”) and what
should happen – **pre-translate from the TM**, **empty segments by AI** (as drafts), **write target files**. New files in
the inbox folder (completely written, supported format) go into the project every 10 seconds, the target files per target
language into the output folder, the original into “erledigt”. Nothing is confirmed – the translation stays in the project
for checking. The log is shown below; **Check now** looks immediately.

## Packages and exchange

- Opening a **.sdlppx**: creates a project with all files, TMs and termbases from the package.
- Creating a **.sdlrpx**: return package with the translated bilingual files.
- **XLIFF** per file (button in the file list) for exchange with other programs.
- **TMX, TBX, .sdltm** under **TM & Terminology**.

Third-party formats are read and written only for the purpose of collaboration.

### Review file in Word

For reviewers without a translation tool: **Review file** next to a file creates a Word table (landscape) with number,
source text, translation, status and comment; tags appear grey in the text. The reviewer edits only the **Translation**
column – Word's track changes are welcome – and writes remarks in the last column or as Word comments. **Import review
file …** reads it back (an identifier matches it to the right file and language): changes are taken over as reviewed and
written to the TM, the previous version stays in the history and in change tracking, remarks and Word comments go into
the segment comments, lost tags are reported. **Unchanged as reviewed** also marks the segments without objection.

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

### Protecting confidential data (anonymisation)

On the **Project** tab under **Privacy for this project** (without a project: default for research and new projects):

- **Confidential**: protected data only goes to an online AI as placeholders – “Herr Müller hat am 12.03.2024 unter Az.
  2 O 123/24 …” becomes “Herr [NAME_1] hat am [DATE_1] unter Az. [CASE_1] …”. The AI translates with placeholders, the
  program puts the real values back into the answer. This applies to everything sent to an AI: translating, research,
  checking, evaluating, alignment. Search queries of the research AI stay masked so that no names reach search engines.
  AI on this computer (built-in AI, Ollama …) receives the texts unchanged unless you choose **send only placeholders to
  AI on this computer too**. The log reports how many items were replaced.
- **Not in the TM**: the project TM and shared TMs only receive versions with placeholders, including the stored context.
  A later match receives the values of the current segment. **Anonymise TM** cleans existing entries.
- **Dates in the target language**: because the AI only sees a date as [DATE_1], the program writes it in the target
  language's form itself – “3. April 2024” becomes “3 April 2024” in UK English, “April 3, 2024” in US English,
  “3 kwietnia 2024” in Polish, and “12.03.2024” becomes “12/03/2024” in UK English.
- **Inflecting names correctly**: the AI does not know the name, but learns its grammatical gender from the form of
  address (“Herr”, “Frau”, “Pan”, “Pani” …) and appends endings to the placeholder – so “von Herrn Müller” becomes
  “pana Müllera” in Polish.
- **Suggestion**: if newly added files contain names, case numbers, IBANs or email addresses, the program asks once
  whether the project should be treated as confidential (**Yes, treat as confidential** / **No, not confidential**).
- **What is protected**: names after a form of address or title (“Herr Müller”, “Frau Dr. Schmidt”, “Pan Kowalski”),
  dates, case numbers (“2 O 123/24”, “VI ZR 45/20”, “sygn. akt I C 123/24”, anything after “Az.”), email, phone, IBAN,
  **addresses** (“Bahnhofstraße 3a, 12345 Musterstadt”, “Hauptstr. 12”, “ul. Długa 5/3, 00-950 Warszawa”, “221B Baker
  Street”, a line consisting only of postcode and town; placeholder [ADDRESS_1]) – each can be switched off – and the
  **protection list**: names, companies, places, plates, one per line. In the editor,
  **🔒 Protect** adds the selected text. Terms on the protection list stay unchanged in the translation (the AI only sees
  [TERM_1]) – so do not add places or companies that should be translated (Köln → Cologne).
- **Find names with local AI**: an AI on this computer looks for names in the project files, classifies each (person,
  address, place, company, own brand/project, product/software, institution, country) and suggests which ones to
  protect – including those already on the protection list. In the list below, checked = protect; **Protect checked**
  applies it (unchecked ones are removed from the protection list, other entries stay). Only protect what identifies a
  person and stays the same in a translation: names, addresses, small places of residence, own companies and projects,
  small employers. The program never protects software, big brands, countries or kinds of schools or institutions –
  whatever must be translated (“Polen” → “Polska”) would otherwise come back untranslated. The text does not leave the
  computer. **Show detected data** lists what would be protected – each item with a tick box: untick what is not
  sensitive (e.g. a date in a standard) and click **Do not protect unticked**; it goes into the **Do not protect** list
  (one value per line, editable there by hand and to undo) and is no longer replaced. Additional sensitive data goes into
  the protection list; the protection list always wins.
- **Nothing protected goes onto the web**: names, addresses, email, phone, IBAN, case numbers and the protection list are
  never a research target – even if the project is not marked as confidential. Nor is a mere date or number
  (“27 April 2016”, “03/2019”): it is transferred by fixed rules, research only costs time. In addition, before every
  search query and page request the program checks whether it contains protected data of the open document (and
  addresses, email, IBAN and the protection list in general); if so, the request is not made and the log gives the
  reason. This also applies to a search engine on this computer (SearXNG in turn asks search engines on the web). Dates
  stay allowed – standards and judgments are searched with their date. Whatever is on the **Do not protect** list stays
  allowed. What was actually sent is shown under **Set up AI** → **Outgoing data**.
- **🔒 at the segment**: in a confidential project, every segment with protected data carries a 🔒 in the editor. The
  tooltip names each item with its tag, e.g. "Name “Müller” → [NAME_1]" – the placeholder it gets when anonymising and
  for the online AI, numbered consistently across the document.

Detection works with patterns and the protection list; it can miss individual items. For strictly confidential jobs
additionally use an AI on this computer.

### Passing on an anonymised version

In the file list under **Anonymised** → **Source file anonymised …** or **Target file anonymised …**: a version without
names, dates, case numbers, e-mail, phone, IBAN and terms of the protection list, consistently replaced throughout the
document (the same name is [NAME_1] everywhere). What is detected follows the project's privacy settings; the layout is
kept and the project itself does not change. Detection can miss individual details – review before passing it on.

### Outgoing data: see and decide what leaves the computer

On the **Set up AI** page under **Outgoing data**, every request to AI, machine translation, speech recognition, web
search, dictionaries and legal sources is listed – with the service (address), time, kind, size and the **exact
content** as it is sent (protected details already replaced by placeholders, keys hidden). The log is kept in memory
only and is empty after a restart.

- **Before sending**: *only record* (default), *ask before the first request to a service* or *ask before every
  request*. The prompt shows the content; **Send**, **Always send to this service (until restart)**, **Don't send** or
  **Block service**. If a request is declined, another model of the free pool takes over.
- **Blocked services** never receive anything, even without a prompt; **Allow again** lifts the block.
- AI on this computer or in your own network is only recorded, never asked about. Requests for robots.txt contain no
  user data and are not listed. Your own team server is not part of the log.

### Activity log

For tracing what happened and for troubleshooting, the program writes an **activity log** to the data folder (subfolder
`protokoll`, one file per day, `protokoll-YYYY-MM-DD.jsonl`, one line per entry):

- every **request** to AI, machine translation, dictation, search and online sources – service and exact content as in
  the outgoing-data log – and the **answer** with HTTP status, content and duration; request and answer carry the same
  identifier (`id`). Large downloads and binary data only with type and size;
- the **offline translation** (sentence, result, model, duration) and start or crash of the built-in AI;
- every **step of the interface**: tab changes and every message the program shows, downloads, crashes.

The log stays on this computer and contains no keys – but it does contain the texts. Setting on the **Set up AI** page
under **Outgoing data → Activity log**: switch it on or off, **Open folder**, **Delete logs**. Logs older than 14 days are
deleted when the program starts.


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
  recherche.json          research dossiers
  recherche-ausdruecke.json expressions found and assessment per source sentence
  meldungen/<id>.json     the AI's messages per segment (translation, research, web check, feedback, AI check)
  tm/projekt.sctm         project TM (SQLite)
  terminologie/projekt.sctb
  wissen/projekt.sckb     project knowledge base
  paket/                  original package, if the project was created from a package
```

In the program's data folder: `ai-settings.json` (AI, MT services, dictation), `ui-settings.json` (user interface, local
API), `servers.json` (team server), `projektvorlagen.json` (project templates), `qa-profile.json` (QA profiles),
`ueberwachte-ordner.json` (watched folders), `woerterbuecher/` (dictionaries), `erweiterungen/` (extensions).
