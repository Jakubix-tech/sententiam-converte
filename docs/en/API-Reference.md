# API Reference

The REST interface (version 2) makes all functions of Sententiam converte available to scripts and other
programs. It runs in the **team server** (always) and, if enabled, **locally in the desktop app**
(Settings → Interface (API), 127.0.0.1 only).

## Basics

- **Address**: `http://<server>:5280/api/v2/` (team server) or `http://127.0.0.1:5290/api/v2/` (desktop, port
  configurable).
- **Authentication**: header `Authorization: Bearer <access key>`. Without a valid key: `401`.
- **Permissions**: readers may only read (`GET`); editors may make changes; only admins may delete files (otherwise `403`).
  In the desktop app, the key has all permissions.
- **Format**: JSON (UTF-8), field names in camelCase, enumerations as text (e.g. `"status": "Translated"`).
  Files are transferred as `{ "name": "...", "base64": "..." }`.
- **Errors**: `400` with a plain-text message for invalid input, `404` if the project, file or segment does not exist,
  `502` if an AI provider or the team server does not respond.
- **Description**: `GET /openapi/v2.json` (OpenAPI 3) – use it to generate clients for any programming language.
- **Identifiers**: `{project}` is the project name (folder name), `{file}` the file ID or file name,
  `{lang}` the target language, `{id}` the segment number.

In the team server, the API works with the server's shared projects. Changes made through the API reach
everyone who currently has the project open in the program at the next synchronization – just like a change made by a colleague.

## General

| Method | Path | Effect |
|---|---|---|
| GET | `/` | Name, version, areas |
| GET | `/formats` | Supported file formats (including those from extensions) |
| GET | `/languages` | Known language codes |
| GET | `/source-profiles` | Specialist source profiles |

## Projects and files

| Method | Path | Effect |
|---|---|---|
| GET | `/projects` | All projects |
| POST | `/projects` | Create: `{ name, sourceLanguage, targetLanguages[], topic }` |
| GET | `/projects/{project}` | Project with files and resources |
| PATCH | `/projects/{project}` | `{ topic, pretranslateMinScore, confirmContextMatches, sourceProfile }` |
| POST | `/projects/{project}/resources` | Attach a TM, termbase or knowledge base: `{ kind, location, server, languages[], writeBack }` |
| DELETE | `/projects/{project}/resources?kind=&location=&server=` | Detach a resource |
| GET | `/projects/{project}/files` | File list |
| POST | `/projects/{project}/files` | Add a file: `{ name, base64 }` |
| DELETE | `/projects/{project}/files/{file}` | Remove a file (admin) |
| GET | `/projects/{project}/files/{file}/{lang}` | All segments |
| GET | `/projects/{project}/files/{file}/{lang}/target` | Target file in the original format (Base64) |
| GET | `/projects/{project}/files/{file}/{lang}/xliff` | Bilingual XLIFF (Base64) |
| POST | `/projects/{project}/files/{file}/{lang}/replace?find=&replace=&matchCase=` | Find and replace in the target |

## Segments

Path prefix: `/projects/{project}/files/{file}/{lang}/segments/{id}`

| Method | Path | Effect |
|---|---|---|
| GET | `` | Segment (source, target, status, origin, match %, comment, context, history) |
| PUT | `` | `{ target, status, comment }` – each field optional |
| POST | `/confirm` | Confirm: write to the TM (with context), propagate to identical segments; response `{ segment, propagated }` |
| GET | `/matches?minScore=` | TM matches (101 = context match) |
| GET | `/terms` | Recognized terminology |
| GET | `/fragments` | Fragment matches |
| GET | `/knowledge` | Relevant knowledge entries |
| GET | `/suggestions` | AutoSuggest suggestions |
| POST | `/apply-match?index=` | Insert match no. `index` (numbers are adjusted) |
| POST | `/copy-source` | Copy source to target |
| POST | `/split` | `{ sourceIndex }` – split at this position in the source text |
| POST | `/merge` | Merge with the following segment |

Status values: `NotTranslated`, `Draft`, `Translated`, `TranslationRejected`, `Reviewed`, `ReviewRejected`, `Signed`. Locked segments have `locked: true`.

## Batch tasks and quality

| Method | Path | Effect |
|---|---|---|
| POST | `/projects/{project}/pretranslate` | `{ confirmExact, overwrite }` – pre-translate all files |
| GET | `/projects/{project}/analysis` | Analysis per language and match band |
| GET | `/projects/{project}/qa` | QA messages for all files |
| PUT | `/projects/{project}/qa-settings` | QA settings and custom rules |

## Translation memory

| Method | Path | Effect |
|---|---|---|
| GET | `/projects/{project}/tm/search?source=&sourceLanguage=&targetLanguage=&minScore=&previous=&next=` | Search, optionally with context |
| GET | `/projects/{project}/tm/concordance?text=&sourceLanguage=&targetLanguage=&inTarget=` | Concordance |
| POST | `/projects/{project}/tm` | `{ sourceLanguage, targetLanguage, source, target, previous, next, structure }` |
| GET | `/projects/{project}/tm/units?filter=&offset=&limit=` | Browse the project TM |
| PUT | `/projects/{project}/tm/units/{id}` | `{ source, target }` |
| DELETE | `/projects/{project}/tm/units/{id}` | Delete an entry |
| POST | `/projects/{project}/tm/import` | TMX or .sdltm (`{ name, base64 }`), response: number of units |
| GET | `/projects/{project}/tm/export` | Project TM as TMX |

## Terminology and knowledge

| Method | Path | Effect |
|---|---|---|
| GET | `/projects/{project}/terms?query=&language=` | Search (without `query`: all terms of the project) |
| POST | `/projects/{project}/terms` | `{ terms: [{ lang, text, status }], definition, domain, source }` |
| DELETE | `/projects/{project}/terms/{id}` | Delete a term entry |
| POST | `/projects/{project}/terms/import` | TBX/XML (`{ name, base64 }`) |
| GET | `/projects/{project}/terms/export` | TBX |
| GET | `/projects/{project}/knowledge?query=&language=` | Knowledge from all attached knowledge bases |
| POST | `/projects/{project}/knowledge` | `{ language, topic, kind, title, content, quote, sourceUrl, sourceTitle }` |
| PUT | `/projects/{project}/knowledge/{id}` | Edit an entry |
| DELETE | `/projects/{project}/knowledge/{id}` | Delete an entry |

Status values for terms: `Preferred`, `Admitted`, `Forbidden`. Kinds of knowledge: `Fact`, `Definition`, `Usage`, `Style`, `Note`.

## Packages, alignment, text recognition

| Method | Path | Effect |
|---|---|---|
| POST | `/packages/import?topic=&name=` | .sdlppx as a new project (`{ name, base64 }`) |
| GET | `/projects/{project}/return-package` | Return package .sdlrpx |
| POST | `/align` | `{ source, target, sourceLanguage, targetLanguage, importIntoProject }` – files as `{ name, base64 }` |
| POST | `/projects/{project}/ocr` | Recognize a scanned PDF with AI and add it as DOCX |

## Agents

| Method | Path | Effect |
|---|---|---|
| POST | `/agents/translate` | Segment (`project, file, language, segmentId`) or free text (`text, sourceLanguage, targetLanguage, topic`) |
| POST | `/agents/evaluate` | `{ source, sourceLanguage, targetLanguage, candidates: [{ label, text }], topic, checkUsage, project }` |
| POST | `/agents/usage` | `{ expression, language, topic, alternatives[] }` |
| POST | `/agents/research` | `{ topic, sourceLanguage, targetLanguages[], seedTerms[], pagesPerLanguage, sourceProfile, saveToProject }` |
| POST | `/agents/knowledge` | `{ project, topic, languages[], focus, pagesPerLanguage, knowledgeBase, sourceProfile }` |
| POST | `/agents/compare` | Term comparison: `{ term, sourceLanguage, targetLanguage, context, topic, sourceProfile, candidates[], project, saveToTermbase, saveToKnowledge }` |

The agents use the AI settings of the host (desktop: Settings → AI; team server:
`<daten>/ki-einstellungen.json`, where `<daten>` is the `--daten` folder, in the same format as `ai-settings.json`). Depending on the scope, they can run for
several minutes; clients should set an appropriate timeout.

## Extensions

| Method | Path | Effect |
|---|---|---|
| GET | `/extensions` | Installed extensions with their commands |
| POST | `/extensions/commands/{command}` | `{ project, file, language, segmentId, arguments: { name: value } }` |

If a command changes the document (`documentChanged: true`), the document is saved and all participants are notified.
On the team server, the administrator installs extensions on the command line:
`SententiamConverte.Server erweiterung installieren <file.scext>` and `erweiterung ein <id>`.

## .NET SDK

For C# and other .NET languages the same interface is available in typed form: `ISententiam` (package
`SententiamConverte.Core`) with the client `SententiamClient` (package `SententiamConverte.Sdk`). Every REST API
function is a method; errors are raised as `KeyNotFoundException`, `ArgumentException`,
`UnauthorizedAccessException` or `InvalidOperationException`.

```csharp
using SententiamConverte.Core.Sdk;
using SententiamConverte.Sdk;

using var sc = new SententiamClient("http://127.0.0.1:5290", Environment.GetEnvironmentVariable("SC_KEY")!);
await sc.CreateProjectAsync(new ProjectCreateDto("Manual", "de-DE", ["en-GB"], "Technology"));
var file = await sc.AddFileAsync("Manual", FileContent.FromFile("instructions.docx"));
await sc.PretranslateAsync("Manual", new PretranslateDto(ConfirmExact: true));
foreach (var s in await sc.SegmentsAsync("Manual", file.Id, "en-GB"))
    if (s.Status == SegmentStatus.NotTranslated)
    {
        var ai = await sc.TranslateAsync(new TranslateRequest("Manual", file.Id, "en-GB", s.Id));
        await sc.UpdateSegmentAsync("Manual", file.Id, "en-GB", s.Id, new SegmentUpdateDto(ai.Translation));
    }
var target = await sc.TargetFileAsync("Manual", file.Id, "en-GB");
File.WriteAllBytes(target.Name, target.Content);
```

The packages are created with `dotnet run --project build/Paketbau -- sdk` in the folder `artifacts/<version>/`.
Inside the program – in extensions – the same interface is available as `context.Api`, without going through
the network (see “Developing Extensions”).

## Examples

**curl** – create a project, upload a file, pre-translate, download the target file:

```bash
API=http://127.0.0.1:5290/api/v2
KEY="Authorization: Bearer $SC_KEY"
curl -s -H "$KEY" -H "Content-Type: application/json" -d '{"name":"Handbuch","sourceLanguage":"de-DE","targetLanguages":["en-GB"],"topic":"Technik"}' $API/projects
curl -s -H "$KEY" -H "Content-Type: application/json" -d "{\"name\":\"anleitung.docx\",\"base64\":\"$(base64 -w0 anleitung.docx)\"}" $API/projects/Handbuch/files
curl -s -H "$KEY" -H "Content-Type: application/json" -d '{"confirmExact":true}' $API/projects/Handbuch/pretranslate
curl -s -H "$KEY" $API/projects/Handbuch/files/anleitung.docx/en-GB/target | jq -r .base64 | base64 -d > anleitung_en-GB.docx
```

**PowerShell** – list all empty segments:

```powershell
$h = @{ Authorization = "Bearer $env:SC_KEY" }
$api = "http://127.0.0.1:5290/api/v2"
Invoke-RestMethod "$api/projects/Handbuch/files/anleitung.docx/en-GB" -Headers $h |
  Where-Object { $_.status -eq "NotTranslated" } | Select-Object id, source
```

**Python** – insert and confirm the 101% match for every segment that has one:

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

Note: `apply-match` counts from 1, like Ctrl+1 in the editor.
