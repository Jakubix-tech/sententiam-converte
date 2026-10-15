using System.Text;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Model;

namespace Beispiel.Erweiterung;

/// <summary>
/// Befehl für das Projekt, der die interne Programmierschnittstelle nutzt (<see cref="ExtensionContext.Api"/>):
/// Dateien, Fortschritt je Sprache und QA-Ergebnis. Über <c>context.Api</c> steht alles zur Verfügung, was das Programm
/// kann – Segmente setzen und bestätigen, TM durchsuchen, vorübersetzen, Terminologie, Wissen, Agenten …
/// </summary>
public sealed class ProjectReportCommand : IExtensionCommand
{
    public string Id => "beispiel.projektbericht";
    public string Name => "Projektbericht";
    public string? Description => "Fasst Dateien, Fortschritt und QA-Meldungen des Projekts zusammen – über die interne Programmierschnittstelle (context.Api).";
    public CommandScope Scope => CommandScope.Project;

    public async Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct)
    {
        var api = context.Api ?? throw new InvalidOperationException("Die Programmierschnittstelle ist hier nicht verfügbar.");
        var name = context.Project?.Name ?? throw new InvalidOperationException("Bitte zuerst ein Projekt öffnen.");

        var project = await api.GetProjectAsync(name, ct);
        var report = new StringBuilder();
        report.AppendLine($"Projekt „{project.Name}“: {project.SourceLanguage} → {string.Join(", ", project.TargetLanguages)}, {project.Files.Count} Datei(en)");
        foreach (var file in project.Files)
            foreach (var language in file.Languages)
            {
                var segments = await api.SegmentsAsync(name, file.Id, language, ct);
                var done = segments.Count(s => s.Status is SegmentStatus.Translated or SegmentStatus.Reviewed or SegmentStatus.Signed);
                report.AppendLine($"  {file.Name} ({language}): {done}/{segments.Count} Segmente fertig");
            }
        context.Log?.Report("Qualitätsprüfung …");
        var qa = await api.QaAsync(name, ct);
        report.Append($"QA: {qa.Count(i => i.Severity == "Error")} Fehler, {qa.Count(i => i.Severity == "Warning")} Warnungen");
        return new ExtensionResult(report.ToString());
    }
}
