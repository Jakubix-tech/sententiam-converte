# Developing Extensions

An extension adds file formats, QA checks and commands to Sententiam converte. It is a
.NET library that is installed in the **Extensions** tab. A complete template is included in the source code
under `Erweiterungen/Beispiel.Erweiterung`.

The examples below keep the German identifiers and texts of the template (names, descriptions, messages). They are
just examples; use whatever names and texts suit your extension.

## Structure

An extension consists of

- a **.NET 10 library**, compiled against `SententiamConverte.Core.dll`,
- a **public class implementing `IExtension`**,
- an **`extension.json`** file with the extension's details.

The library and `extension.json`, packed together as a ZIP with the file extension **`.scext`**, form the installation package. A single DLL
can also be installed; in that case the details are derived from the file name.

```json
{
  "id": "firma.qa-pruefungen",
  "name": "QA-Prüfungen der Firma",
  "version": "1.2.0",
  "author": "Firma GmbH",
  "description": "Prüft Hausregeln für Maßeinheiten und Produktnamen.",
  "assembly": "Firma.QaPruefungen.dll"
}
```

The `id` may contain only letters, digits, periods, underscores and hyphens; it is also used as the folder name.

## Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The SDK: interfaces and data types. The program ships the library itself – do not copy it into the output -->
    <PackageReference Include="SententiamConverte.Core" Version="1.0.0" ExcludeAssets="runtime;native" />
  </ItemGroup>
</Project>
```

The NuGet package `SententiamConverte.Core` is created with `dotnet run --project build/Paketbau -- sdk` in the
folder `artifacts/<version>/`. Add this folder as a package source (`nuget.config`):

```xml
<configuration>
  <packageSources>
    <add key="sententiam" value="path/to/artifacts/1.0.0" />
  </packageSources>
</configuration>
```

Without the package, a direct reference to `SententiamConverte.Core.dll` from the program folder also works
(`<Reference>` with `<Private>false</Private>`); within the source tree, a `ProjectReference` with
`Private="false"` is sufficient. The template also contains an MSBuild target that automatically creates the
`.scext` package after the build.

Your own dependencies (NuGet packages) belong in the package. Libraries that the program itself already
ships with (e.g. `SententiamConverte.Core`, `Microsoft.Data.Sqlite`) are always loaded from the program
so that the types match.

## Entry point

```csharp
using SententiamConverte.Core.Extensions;

public sealed class MeineErweiterung : IExtension
{
    public string Id => "firma.qa-pruefungen";
    public string Name => "QA-Prüfungen der Firma";
    public string Version => "1.2.0";
    public string? Author => "Firma GmbH";
    public string? Description => "Prüft Hausregeln für Maßeinheiten und Produktnamen.";

    public void Register(IExtensionRegistry registry)
    {
        registry.AddQaCheck(new MasseinheitenPruefung());
        registry.AddCommand(new ProduktnamenBefehl());
    }
}
```

`Register` is called once when the extension is loaded – at program start or as soon as the extension is enabled.

## File format

`IFileFilter` reads a file into a bilingual document and writes the translation back:

```csharp
public sealed class MeinFormat : IFileFilter
{
    public string Id => "firma.meinformat";
    public string Name => "Mein Format";
    public IReadOnlyList<string> Extensions { get; } = [".mf"];

    public BilingualDocument Read(Stream input, string fileName, string sourceLanguage, string targetLanguage, Segmenter segmenter)
    {
        var doc = new BilingualDocument { FileName = fileName, FilterId = Id, SourceLanguage = sourceLanguage, TargetLanguage = targetLanguage };
        // create one paragraph per translatable piece of text; Locator remembers the position in the file
        var p = new Paragraph { Locator = "0" };
        segmenter.Segment(p, "Text aus der Datei");
        doc.Paragraphs.Add(p);
        doc.Renumber();
        return doc;
    }

    public void Write(BilingualDocument document, Stream original, Stream output)
    {
        // read the original, insert p.JoinTarget() at position p.Locator for each paragraph, write the result
    }
}
```

Formatting is represented in the text as tags: `⟨1⟩bold⟨/1⟩` (pair) and `⟨2/⟩` (placeholder). The filter itself
stores what the tags mean (e.g. in `Paragraph.TagData`). Helpers: `TagText.StripTags`,
`TagText.Tags`, `Segmenter.HasTranslatableText`. A filter with the same file extension as a built-in filter takes precedence.

## QA check

```csharp
public sealed class MasseinheitenPruefung : IQaCheck
{
    public string Name => "Maßeinheiten";

    public IEnumerable<QaIssue> Check(Segment segment, BilingualDocument document)
    {
        if (segment.Target.Contains("Kilo ") )
            yield return new QaIssue(segment.Id, QaSeverity.Warning, Name, "„kg“ statt „Kilo“ schreiben.");
    }
}
```

The check runs during every quality check for every segment that is not locked. If it throws an exception,
the exception is shown as a notice – the rest of the check continues.

## Commands

```csharp
public sealed class ProduktnamenBefehl : IExtensionCommand
{
    public string Id => "firma.produktnamen";
    public string Name => "Produktnamen vereinheitlichen";
    public string? Description => "Ersetzt alte Produktnamen im Zieltext. Argument: alt=neu";
    public CommandScope Scope => CommandScope.Document;

    public Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var doc = context.Document ?? throw new InvalidOperationException("Bitte ein Dokument öffnen.");
        var n = 0;
        foreach (var (alt, neu) in context.Arguments)
            foreach (var s in doc.Segments.Where(s => s.Target.Contains(alt)))
            {
                s.Target = s.Target.Replace(alt, neu);
                n++;
            }
        return Task.FromResult(new ExtensionResult($"{n} Ersetzungen.", DocumentChanged: n > 0));
    }
}
```

`Scope` specifies what the command acts on: `Segment`, `Document`, `Project` or `Global`. Before running the command,
the program checks whether the required item is open or selected.

`ExtensionContext` contains:

| Property | Content |
|---|---|
| `Project` | The open project (TMs, termbases, knowledge, files) |
| `File`, `Document` | The file in the editor and its bilingual document |
| `Segment` | The selected segment |
| `Arguments` | Arguments from the tab (one `name=value` per line) or from the API |
| `ChatModel` | The configured language model (`AskAsync(system, user)`), otherwise `null` |
| `Search` | The configured web search, otherwise `null` |
| `Api` | The internal programming interface with **all** functions (see below) |
| `Log` | Progress messages, shown in the tab |

With `DocumentChanged: true`, the program saves the document and reloads the editor.
Commands can also be called through the API: `POST /api/v2/extensions/commands/{id}`.

## The internal programming interface (context.Api)

`context.Api` is of type `ISententiam` (namespace `SententiamConverte.Core.Sdk`) and offers everything the
program can do – typed and directly inside the program, without going through the network:

| Area | Examples |
|---|---|
| Projects and files | `ProjectsAsync`, `CreateProjectAsync`, `AddFileAsync`, `SegmentsAsync`, `TargetFileAsync`, `XliffAsync` |
| Segments | `UpdateSegmentAsync`, `ConfirmSegmentAsync`, `MatchesAsync`, `SegmentTermsAsync`, `ApplyMatchAsync`, `SplitSegmentAsync` |
| Batch tasks and quality | `PretranslateAsync`, `AnalysisAsync`, `QaAsync`, `SetQaSettingsAsync` |
| TM | `SearchTmAsync`, `ConcordanceAsync`, `AddToTmAsync`, `ImportTmAsync`, `ExportTmAsync` |
| Terminology and knowledge | `SearchTermsAsync`, `AddConceptAsync`, `SearchKnowledgeAsync`, `AddKnowledgeAsync` |
| Packages | `ImportPackageAsync`, `ReturnPackageAsync`, `AlignAsync`, `RecognizePdfAsync` |
| Agents | `TranslateAsync`, `EvaluateAsync`, `CheckUsageAsync`, `ResearchAsync`, `FillKnowledgeAsync`, `CompareTermAsync` |
| Extensions | `ExtensionsAsync`, `RunCommandAsync` (commands of other extensions) |

```csharp
public async Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
{
    var api = context.Api!;
    var name = context.Project!.Name;
    await api.PretranslateAsync(name, new PretranslateDto(ConfirmExact: true), ct);
    var open = 0;
    foreach (var file in (await api.GetProjectAsync(name, ct)).Files)
        foreach (var lang in file.Languages)
            open += (await api.SegmentsAsync(name, file.Id, lang, ct)).Count(s => s.Status == SegmentStatus.NotTranslated);
    return new ExtensionResult($"Pre-translated; {open} segments still open.");
}
```

The same interface exists outside the program as a .NET client for the REST API (`SententiamClient` in the
package `SententiamConverte.Sdk`, see API Reference). Code written against `ISententiam` therefore runs unchanged
as an extension, as a separate program or as a script.

Edit a document either through `context.Document` (directly in memory) or through `context.Api`, not both at once:
the interface saves the open document first and reloads the editor afterwards. The “Project report” command of
the example extension shows how to use it.

## Security and good practice

- Extensions run with the user's permissions. After installation they are disabled; the user
  must deliberately enable them. Describe honestly in `description` what the extension does.
- Send data only where the user expects it; read websites only through `context.Search` or while
  respecting robots.txt and reservations of rights.
- Long-running commands should honor `ct` (**Cancel** button).
- Libraries are loaded from memory, so `Assembly.Location` is empty. Your own files in the
  extension folder are located under `<data folder>/erweiterungen/<id>/`.

## Testing

```bash
cd Erweiterungen/Beispiel.Erweiterung
dotnet build -c Release
```

Install the package `bin/Release/net10.0/Beispiel.Erweiterung.scext` in the **Extensions** tab,
enable it and try out the commands. In automated tests, `ExtensionManager` can be used directly
(`Install`, `SetEnabled`, `FindCommand`) – see `SententiamConverte.Core.Tests/ExtensionTests.cs`.
