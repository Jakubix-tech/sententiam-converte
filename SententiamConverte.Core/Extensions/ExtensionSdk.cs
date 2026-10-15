using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Extensions;

/// <summary>
/// Einstiegspunkt einer Erweiterung. Eine Erweiterung ist eine .NET-Bibliothek (DLL), die gegen
/// SententiamConverte.Core kompiliert ist und genau eine öffentliche Klasse mit dieser Schnittstelle enthält.
/// </summary>
public interface IExtension
{
    /// <summary>Eindeutige Kennung, z. B. „firma.qa-pruefungen“.</summary>
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string? Author { get; }
    string? Description { get; }

    /// <summary>Meldet die Bausteine der Erweiterung an (Dateiformate, QA-Prüfungen, Befehle).</summary>
    void Register(IExtensionRegistry registry);
}

/// <summary>Hier meldet eine Erweiterung an, was sie beisteuert.</summary>
public interface IExtensionRegistry
{
    /// <summary>Neues Dateiformat (lesen und zurückschreiben).</summary>
    void AddFileFilter(IFileFilter filter);

    /// <summary>Zusätzliche Prüfung für die Qualitätssicherung.</summary>
    void AddQaCheck(IQaCheck check);

    /// <summary>Befehl, aufrufbar im Reiter „Erweiterungen“ (wirkt auf das im Editor Geöffnete) und über die API.</summary>
    void AddCommand(IExtensionCommand command);
}

/// <summary>Eigene QA-Prüfung für ein Segment.</summary>
public interface IQaCheck
{
    string Name { get; }
    IEnumerable<QaIssue> Check(Segment segment, BilingualDocument document);
}

/// <summary>Worauf ein Befehl wirkt.</summary>
public enum CommandScope { Global, Project, Document, Segment }

/// <summary>Ein Befehl einer Erweiterung.</summary>
public interface IExtensionCommand
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
    CommandScope Scope { get; }
    Task<ExtensionResult> RunAsync(ExtensionContext context, CancellationToken ct);
}

/// <summary>Alles, was ein Befehl beim Ausführen zur Verfügung hat.</summary>
public sealed class ExtensionContext
{
    public TranslationProject? Project { get; init; }
    public ProjectFile? File { get; init; }
    public BilingualDocument? Document { get; init; }
    public Segment? Segment { get; init; }
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Alle Funktionen des Programms (Projekte, Dateien, Segmente, TM, Terminologie, Wissen, QA, Agenten …) –
    /// dieselbe Schnittstelle wie die REST-API, hier direkt im Programm. Beispiel:
    /// <c>var rows = await context.Api!.AnalysisAsync(context.Project!.Name, ct);</c>
    /// </summary>
    public Sdk.ISententiam? Api { get; init; }

    /// <summary>Das eingestellte Sprachmodell (falls eingerichtet) – für Befehle, die KI nutzen.</summary>
    public IChatModel? ChatModel { get; init; }
    public ISearchProvider? Search { get; init; }
    public IProgress<string>? Log { get; init; }
}

/// <summary>Ergebnis eines Befehls. <see cref="DocumentChanged"/>: das Dokument wurde verändert und wird gespeichert.</summary>
public sealed record ExtensionResult(string Message, bool DocumentChanged = false);
