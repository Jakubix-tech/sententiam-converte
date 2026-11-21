using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Sdk;

namespace SententiamConverte.Api;

/// <summary>
/// Die REST-Schnittstelle (Version 2): ein dünner Aufsatz auf <see cref="SententiamService"/> – jede Funktion der
/// Programmierschnittstelle <see cref="ISententiam"/> ist hier als HTTP-Aufruf erreichbar.
/// Beschreibung im OpenAPI-Format unter /openapi/v2.json.
/// </summary>
public static class SententiamApi
{
    public const string Version = SententiamService.Version;

    /// <param name="service">Eine schon vorhandene interne Schnittstelle (Desktop-App), damit alle Wege dieselben Projekte nutzen.</param>
    public static IServiceCollection AddSententiamApi(this IServiceCollection services, ApiOptions options, SententiamService? service = null)
    {
        services.AddSingleton(options);
        services.AddSingleton(service ?? new SententiamService(options));
        services.AddOpenApi("v2", o => o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v2", StringComparison.OrdinalIgnoreCase) == true);
        services.Configure<Microsoft.AspNetCore.OpenApi.OpenApiOptions>("v2", o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "Sententiam converte API";
            doc.Info.Version = Version;
            doc.Info.Description = "Alle Funktionen des Übersetzungswerkzeugs. Anmeldung: Kopfzeile „Authorization: Bearer <Schlüssel>“. " +
                                   "Für .NET gibt es dieselbe Schnittstelle typisiert: SententiamClient im Paket SententiamConverte.Sdk.";
            return Task.CompletedTask;
        }));
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    public static WebApplication MapSententiamApi(this WebApplication app)
    {
        var o = app.Services.GetRequiredService<ApiOptions>();
        var service = app.Services.GetRequiredService<SententiamService>();
        app.MapOpenApi("/openapi/{documentName}.json");

        var api = app.MapGroup("/api/v2").AddEndpointFilter(async (ctx, next) =>
        {
            var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
            var user = header.StartsWith("Bearer ") ? o.Authorize(header[7..].Trim()) : null;
            if (user == null) return Results.Unauthorized();
            if (ctx.HttpContext.Request.Method != "GET" && !user.CanWrite) return Results.StatusCode(403);
            ctx.HttpContext.Items["apiUser"] = user;
            return await next(ctx);
        });
        // Jede Anfrage arbeitet im Namen des angemeldeten Benutzers (z. B. als Autor neuer Wissenseinträge)
        ISententiam S(HttpContext h) => h.Items["apiUser"] is ApiUser u ? service.ForUser(u.Name, u.IsAdmin) : service;

        // ---------- Allgemein ----------
        api.MapGet("/", (HttpContext h, CancellationToken ct) => Call(() => S(h).InfoAsync(ct))).WithSummary("Übersicht über die Schnittstelle");
        api.MapGet("/formats", (HttpContext h, CancellationToken ct) => Call(() => S(h).FormatsAsync(ct))).WithSummary("Unterstützte Dateiformate");
        api.MapGet("/languages", (HttpContext h, CancellationToken ct) => Call(() => S(h).LanguagesAsync(ct))).WithSummary("Bekannte Sprachcodes");
        api.MapGet("/source-profiles", (HttpContext h, CancellationToken ct) => Call(() => S(h).SourceProfilesAsync(ct)))
            .WithSummary("Fachquellen-Profile (Themen mit Fachseiten je Sprache)");

        // ---------- Projekte ----------
        var pr = api.MapGroup("/projects");
        pr.MapGet("", (HttpContext h, CancellationToken ct) => Call(() => S(h).ProjectsAsync(ct))).WithSummary("Alle Projekte");
        pr.MapPost("", (HttpContext h, ProjectCreateDto c, CancellationToken ct) => Call(() => S(h).CreateProjectAsync(c, ct))).WithSummary("Projekt anlegen");
        pr.MapGet("/{project}", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).GetProjectAsync(project, ct)))
            .WithSummary("Projekt mit Dateien und Ressourcen");
        pr.MapPatch("/{project}", (HttpContext h, string project, ProjectUpdateDto u, CancellationToken ct) => Call(() => S(h).UpdateProjectAsync(project, u, ct)))
            .WithSummary("Projekteinstellungen ändern");
        pr.MapPost("/{project}/resources", (HttpContext h, string project, ResourceRef r, CancellationToken ct) => Call(() => S(h).AttachResourceAsync(project, r, ct)))
            .WithSummary("TM, Terminologie oder Wissensdatenbank anbinden (lokal oder Teamserver, je Sprache)");
        pr.MapDelete("/{project}/resources", (HttpContext h, string project, ResourceKind kind, string location, string? server, CancellationToken ct) =>
                Call(() => S(h).DetachResourceAsync(project, kind, location, server, ct)))
            .WithSummary("Angebundene Ressource lösen");

        // ---------- Dateien ----------
        pr.MapGet("/{project}/files", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).FilesAsync(project, ct)))
            .WithSummary("Dateien des Projekts");
        pr.MapPost("/{project}/files", (HttpContext h, string project, FileContent f, CancellationToken ct) => Call(() => S(h).AddFileAsync(project, f, ct)))
            .WithSummary("Datei hinzufügen: { name, base64 }");
        pr.MapDelete("/{project}/files/{file}", async (HttpContext h, string project, string file, CancellationToken ct) =>
                Admin(h) ?? await Call(() => S(h).RemoveFileAsync(project, file, ct)))
            .WithSummary("Datei entfernen (nur Admin)");
        pr.MapGet("/{project}/files/{file}/{lang}", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).SegmentsAsync(project, file, lang, ct)))
            .WithSummary("Alle Segmente einer Datei in einer Zielsprache");
        pr.MapGet("/{project}/files/{file}/{lang}/target", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).TargetFileAsync(project, file, lang, ct)))
            .WithSummary("Zieldatei im Originalformat: { name, base64 }");
        pr.MapGet("/{project}/files/{file}/{lang}/xliff", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).XliffAsync(project, file, lang, ct)))
            .WithSummary("Zweisprachiges XLIFF zum Austausch: { name, base64 }");
        pr.MapPost("/{project}/files/{file}/{lang}/replace", (HttpContext h, string project, string file, string lang, string find, string replace, bool? matchCase,
                CancellationToken ct) => Call(() => S(h).ReplaceAsync(project, file, lang, find, replace, matchCase ?? false, ct)))
            .WithSummary("Suchen und Ersetzen im Zieltext");

        // ---------- Segmente ----------
        var seg = pr.MapGroup("/{project}/files/{file}/{lang}/segments/{id:int}");
        seg.MapGet("", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).GetSegmentAsync(project, file, lang, id, ct)))
            .WithSummary("Ein Segment");
        seg.MapPut("", (HttpContext h, string project, string file, string lang, int id, SegmentUpdateDto u, CancellationToken ct) =>
                Call(() => S(h).UpdateSegmentAsync(project, file, lang, id, u, ct)))
            .WithSummary("Übersetzung, Status oder Kommentar eines Segments setzen");
        seg.MapPost("/confirm", (HttpContext h, string project, string file, string lang, int id, ConfirmDto c, CancellationToken ct) =>
                Call(() => S(h).ConfirmSegmentAsync(project, file, lang, id, c.Status, ct)))
            .WithSummary("Segment bestätigen: ins TM schreiben (mit Kontext) und auf gleiche Segmente übertragen");
        seg.MapGet("/matches", (HttpContext h, string project, string file, string lang, int id, int? minScore, CancellationToken ct) =>
                Call(() => S(h).MatchesAsync(project, file, lang, id, minScore, ct)))
            .WithSummary("TM-Treffer mit Kontextbewertung (101 % = Kontexttreffer)");
        seg.MapGet("/terms", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).SegmentTermsAsync(project, file, lang, id, ct)))
            .WithSummary("Erkannte Terminologie");
        seg.MapGet("/fragments", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).FragmentsAsync(project, file, lang, id, ct)))
            .WithSummary("Fragment-Treffer");
        seg.MapGet("/knowledge", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).SegmentKnowledgeAsync(project, file, lang, id, ct)))
            .WithSummary("Passende Wissenseinträge");
        seg.MapGet("/suggestions", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).SuggestionsAsync(project, file, lang, id, ct)))
            .WithSummary("AutoSuggest-Vorschläge");
        seg.MapPost("/apply-match", (HttpContext h, string project, string file, string lang, int id, int index, CancellationToken ct) =>
                Call(() => S(h).ApplyMatchAsync(project, file, lang, id, index, ct)))
            .WithSummary("TM-Treffer Nr. index einsetzen, ab 1 wie Strg+1 (Zahlen werden angepasst)");
        seg.MapPost("/copy-source", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).CopySourceAsync(project, file, lang, id, ct)))
            .WithSummary("Quelle ins Ziel kopieren");
        seg.MapPost("/split", (HttpContext h, string project, string file, string lang, int id, SplitDto sp, CancellationToken ct) =>
                Call(() => S(h).SplitSegmentAsync(project, file, lang, id, sp.SourceIndex, ct)))
            .WithSummary("Segment an einer Stelle im Quelltext teilen");
        seg.MapPost("/merge", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).MergeSegmentAsync(project, file, lang, id, ct)))
            .WithSummary("Segment mit dem folgenden zusammenführen");

        // ---------- Stapelaufgaben und Qualität ----------
        pr.MapPost("/{project}/pretranslate", (HttpContext h, string project, PretranslateDto r, CancellationToken ct) => Call(() => S(h).PretranslateAsync(project, r, ct)))
            .WithSummary("Vorübersetzung aus den TMs");
        pr.MapGet("/{project}/analysis", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).AnalysisAsync(project, ct)))
            .WithSummary("Analyse nach Trefferstufen");
        pr.MapGet("/{project}/qa", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).QaAsync(project, ct)))
            .WithSummary("Qualitätsprüfung aller Dateien");
        pr.MapPut("/{project}/qa-settings", (HttpContext h, string project, QaSettings q, CancellationToken ct) => Call(() => S(h).SetQaSettingsAsync(project, q, ct)))
            .WithSummary("QA-Einstellungen und eigene Regeln setzen");

        // ---------- Translation Memory ----------
        pr.MapGet("/{project}/tm/search", (HttpContext h, string project, string source, string sourceLanguage, string targetLanguage, int? minScore,
                string? previous, string? next, CancellationToken ct) =>
                Call(() => S(h).SearchTmAsync(project, source, sourceLanguage, targetLanguage, minScore, previous, next, ct)))
            .WithSummary("TM-Suche (optional mit Kontext)");
        pr.MapGet("/{project}/tm/concordance", (HttpContext h, string project, string text, string sourceLanguage, string targetLanguage, bool? inTarget,
                CancellationToken ct) => Call(() => S(h).ConcordanceAsync(project, text, sourceLanguage, targetLanguage, inTarget ?? false, ct)))
            .WithSummary("Konkordanzsuche");
        pr.MapPost("/{project}/tm", (HttpContext h, string project, TmAddDto u, CancellationToken ct) => Call(() => S(h).AddToTmAsync(project, u, ct)))
            .WithSummary("Übersetzung ins Projekt-TM schreiben");
        pr.MapGet("/{project}/tm/units", (HttpContext h, string project, string? filter, int? offset, int? limit, CancellationToken ct) =>
                Call(() => S(h).TmUnitsAsync(project, filter, offset ?? 0, limit ?? 50, ct)))
            .WithSummary("Projekt-TM blättern");
        pr.MapPut("/{project}/tm/units/{id:long}", (HttpContext h, string project, long id, TmUpdateRequest u, CancellationToken ct) =>
                Call(() => S(h).UpdateTmUnitAsync(project, id, u.Source, u.Target, ct)))
            .WithSummary("TM-Eintrag ändern");
        pr.MapDelete("/{project}/tm/units/{id:long}", (HttpContext h, string project, long id, CancellationToken ct) => Call(() => S(h).DeleteTmUnitAsync(project, id, ct)))
            .WithSummary("TM-Eintrag löschen");
        pr.MapPost("/{project}/tm/import", (HttpContext h, string project, FileContent f, CancellationToken ct) => Call(() => S(h).ImportTmAsync(project, f, ct)))
            .WithSummary("TM importieren (TMX oder .sdltm): { name, base64 }");
        pr.MapGet("/{project}/tm/export", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).ExportTmAsync(project, ct)))
            .WithSummary("Projekt-TM als TMX: { name, base64 }");

        // ---------- Terminologie ----------
        pr.MapGet("/{project}/terms", (HttpContext h, string project, string? query, string? language, CancellationToken ct) =>
                Call(() => S(h).SearchTermsAsync(project, query, language, ct)))
            .WithSummary("Terminologie durchsuchen");
        pr.MapPost("/{project}/terms", (HttpContext h, string project, ConceptDto c, CancellationToken ct) => Call(() => S(h).AddConceptAsync(project, c, ct)))
            .WithSummary("Begriff anlegen");
        pr.MapDelete("/{project}/terms/{id:long}", (HttpContext h, string project, long id, CancellationToken ct) => Call(() => S(h).DeleteConceptAsync(project, id, ct)))
            .WithSummary("Begriff löschen");
        pr.MapPost("/{project}/terms/import", (HttpContext h, string project, FileContent f, CancellationToken ct) => Call(() => S(h).ImportTermsAsync(project, f, ct)))
            .WithSummary("Terminologie importieren (TBX/XML): { name, base64 }");
        pr.MapGet("/{project}/terms/export", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).ExportTermsAsync(project, ct)))
            .WithSummary("Terminologie als TBX: { name, base64 }");

        // ---------- Wissen ----------
        pr.MapGet("/{project}/knowledge", (HttpContext h, string project, string? query, string? language, CancellationToken ct) =>
                Call(() => S(h).SearchKnowledgeAsync(project, query, language, ct)))
            .WithSummary("Wissen durchsuchen (alle angebundenen Datenbanken)");
        pr.MapPost("/{project}/knowledge", (HttpContext h, string project, KnowledgeEntry e, CancellationToken ct) => Call(() => S(h).AddKnowledgeAsync(project, e, ct)))
            .WithSummary("Wissenseintrag anlegen (Projekt-Wissen)");
        pr.MapPut("/{project}/knowledge/{id:long}", (HttpContext h, string project, long id, KnowledgeEntry e, CancellationToken ct) =>
                Call(() => S(h).UpdateKnowledgeAsync(project, id, e, ct)))
            .WithSummary("Wissenseintrag ändern");
        pr.MapDelete("/{project}/knowledge/{id:long}", (HttpContext h, string project, long id, CancellationToken ct) => Call(() => S(h).DeleteKnowledgeAsync(project, id, ct)))
            .WithSummary("Wissenseintrag löschen");

        // ---------- Pakete, Alignment, Texterkennung ----------
        api.MapPost("/packages/import", (HttpContext h, FileContent f, string? topic, string? name, CancellationToken ct) =>
                Call(() => S(h).ImportPackageAsync(f, topic, name, ct)))
            .WithSummary("Projektpaket (.sdlppx) als neues Projekt übernehmen");
        pr.MapGet("/{project}/return-package", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).ReturnPackageAsync(project, ct)))
            .WithSummary("Rückgabepaket (.sdlrpx): { name, base64 }");
        api.MapPost("/align", (HttpContext h, AlignRequest r, CancellationToken ct) => Call(() => S(h).AlignAsync(r, ct)))
            .WithSummary("Alte Übersetzungen ausrichten; optional sichere Paare ins Projekt-TM übernehmen");
        pr.MapPost("/{project}/ocr", (HttpContext h, string project, FileContent f, CancellationToken ct) => Call(() => S(h).RecognizePdfAsync(project, f, ct)))
            .WithSummary("Gescanntes PDF per KI erkennen und als DOCX hinzufügen");

        // ---------- Agenten ----------
        var ag = api.MapGroup("/agents");
        ag.MapPost("/translate", (HttpContext h, TranslateRequest r, CancellationToken ct) => Call(() => S(h).TranslateAsync(r, ct)))
            .WithSummary("KI-Übersetzung eines Segments (mit TM, Terminologie, Wissen) oder eines freien Texts");
        ag.MapPost("/evaluate", (HttpContext h, EvaluateRequest r, CancellationToken ct) => Call(() => S(h).EvaluateAsync(r, ct)))
            .WithSummary("Übersetzungsvarianten bewerten");
        ag.MapPost("/usage", (HttpContext h, UsageRequest r, CancellationToken ct) => Call(() => S(h).CheckUsageAsync(r, ct)))
            .WithSummary("Usus-Prüfung: ist ein Ausdruck üblich?");
        ag.MapPost("/research", (HttpContext h, ResearchRequest r, CancellationToken ct) => Call(() => S(h).ResearchAsync(r, ct)))
            .WithSummary("Themenrecherche: mehrsprachige Begriffsliste mit Belegen");
        ag.MapPost("/knowledge", (HttpContext h, KnowledgeAgentRequest r, CancellationToken ct) => Call(() => S(h).FillKnowledgeAsync(r, ct)))
            .WithSummary("Wissens-Agent: Wissensdatenbank aus Online-Quellen füllen");
        ag.MapPost("/compare", (HttpContext h, CompareRequest r, CancellationToken ct) => Call(() => S(h).CompareTermAsync(r, ct)))
            .WithSummary("Begriffsvergleich: Bedeutung im Kontext, Fachseiten beider Sprachen, begründete Empfehlung");

        // ---------- Lektorat und Fehlerbewertung ----------
        var fl = pr.MapGroup("/{project}/files/{file}/{lang}");
        fl.MapGet("/review", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).ReviewFileAsync(project, file, lang, ct)))
            .WithSummary("Prüfdatei (Word-Tabelle) für das Lektorat: { name, base64 }");
        fl.MapPost("/review", (HttpContext h, string project, string file, string lang, ReviewImportDto r, CancellationToken ct) =>
                Call(() => S(h).ImportReviewAsync(project, file, lang, r, ct)))
            .WithSummary("Korrigierte Prüfdatei einlesen");
        fl.MapGet("/evaluation", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).EvaluationAsync(project, file, lang, ct)))
            .WithSummary("Fehlerbewertung einer Datei");
        fl.MapGet("/evaluation/report", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).EvaluationReportAsync(project, file, lang, ct)))
            .WithSummary("Bewertungsbericht als Word: { name, base64 }");
        fl.MapGet("/pseudo", (HttpContext h, string project, string file, string lang, CancellationToken ct) =>
                Call(() => S(h).PseudoTranslationAsync(project, file, lang, ct)))
            .WithSummary("Zieldatei mit Pseudoübersetzung (das Projekt bleibt unverändert)");
        fl.MapPut("/workflow", (HttpContext h, string project, string file, string lang, WorkflowUpdate u, CancellationToken ct) =>
                Call(() => S(h).SetWorkflowAsync(project, file, lang, u, ct)))
            .WithSummary("Arbeitsschritt setzen (Verwaltung) oder abgeben ({ advance: true }, wer an der Reihe ist)");
        seg.MapPost("/accept-change", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).AcceptChangeAsync(project, file, lang, id, ct)))
            .WithSummary("Änderung des Prüfers annehmen");
        seg.MapPost("/reject-change", (HttpContext h, string project, string file, string lang, int id, CancellationToken ct) =>
                Call(() => S(h).RejectChangeAsync(project, file, lang, id, ct)))
            .WithSummary("Änderung des Prüfers ablehnen (vorige Fassung gilt wieder)");
        seg.MapPost("/errors", (HttpContext h, string project, string file, string lang, int id, LqaErrorDto e, CancellationToken ct) =>
                Call(() => S(h).AddErrorAsync(project, file, lang, id, e, ct)))
            .WithSummary("Fehler erfassen (Kategorie, Schwere, Anmerkung)");
        seg.MapDelete("/errors/{index:int}", (HttpContext h, string project, string file, string lang, int id, int index, CancellationToken ct) =>
                Call(() => S(h).RemoveErrorAsync(project, file, lang, id, index, ct)))
            .WithSummary("Erfassten Fehler entfernen");
        pr.MapGet("/{project}/evaluation", (HttpContext h, string project, string? language, CancellationToken ct) =>
                Call(() => S(h).EvaluationAsync(project, null, language, ct)))
            .WithSummary("Fehlerbewertung des ganzen Projekts");
        pr.MapPut("/{project}/evaluation-settings", (HttpContext h, string project, LqaSettings l, CancellationToken ct) =>
                Call(() => S(h).SetLqaSettingsAsync(project, l, ct)))
            .WithSummary("Strafpunkte und Bestehensgrenze setzen");

        // ---------- Zahlen, Angebot, Vorlagen, QA-Profile ----------
        pr.MapPost("/{project}/localize-numbers", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).LocalizeNumbersAsync(project, ct)))
            .WithSummary("Zahlen und Datum in die Schreibweise der Zielsprache bringen");
        pr.MapGet("/{project}/quote", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).QuoteAsync(project, null, ct)))
            .WithSummary("Angebot aus der Analyse");
        pr.MapPut("/{project}/quote", (HttpContext h, string project, Core.Analysis.RateSettings r, CancellationToken ct) =>
                Call(() => S(h).QuoteAsync(project, r, ct)))
            .WithSummary("Preise setzen und Angebot berechnen");
        pr.MapGet("/{project}/quote/document", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).QuoteDocumentAsync(project, ct)))
            .WithSummary("Angebot als Word: { name, base64 }");
        api.MapGet("/templates", (HttpContext h, CancellationToken ct) => Call(() => S(h).TemplatesAsync(ct))).WithSummary("Projektvorlagen");
        pr.MapPost("/{project}/template", (HttpContext h, string project, string name, CancellationToken ct) =>
                Call(() => S(h).SaveTemplateAsync(project, name, ct)))
            .WithSummary("Projekt als Vorlage speichern");
        api.MapGet("/qa-profiles", (HttpContext h, CancellationToken ct) => Call(() => S(h).QaProfilesAsync(ct))).WithSummary("QA-Profile");
        pr.MapPost("/{project}/qa-profile", (HttpContext h, string project, string name, CancellationToken ct) =>
                Call(() => S(h).SaveQaProfileAsync(project, name, ct)))
            .WithSummary("QA-Einstellungen des Projekts als Profil speichern");
        pr.MapPut("/{project}/qa-profile", (HttpContext h, string project, string name, CancellationToken ct) =>
                Call(() => S(h).ApplyQaProfileAsync(project, name, ct)))
            .WithSummary("QA-Profil ins Projekt übernehmen");

        // ---------- TM-Pflege, Fachbegriffe, alte Übersetzungen ----------
        pr.MapPost("/{project}/tm/replace", (HttpContext h, string project, TmReplaceDto r, CancellationToken ct) => Call(() => S(h).ReplaceInTmAsync(project, r, ct)))
            .WithSummary("Suchen und Ersetzen im Projekt-TM (auch Vorschau)");
        pr.MapPost("/{project}/tm/clean", (HttpContext h, string project, TmCleanDto r, CancellationToken ct) => Call(() => S(h).CleanTmAsync(project, r, ct)))
            .WithSummary("Projekt-TM bereinigen");
        pr.MapGet("/{project}/tm/inconsistent", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).InconsistentTmAsync(project, ct)))
            .WithSummary("Uneinheitliche Übersetzungen im Projekt-TM");
        pr.MapPost("/{project}/terms/extract", (HttpContext h, string project, ExtractTermsDto r, CancellationToken ct) => Call(() => S(h).ExtractTermsAsync(project, r, ct)))
            .WithSummary("Fachbegriffe aus den Projektdateien gewinnen");
        pr.MapPost("/{project}/import-translations", (HttpContext h, string project, ImportTranslationsDto r, CancellationToken ct) =>
                Call(() => S(h).ImportTranslationsAsync(project, r, ct)))
            .WithSummary("Alte Übersetzungen zuordnen, ausrichten und ins TM übernehmen");

        // ---------- Datenschutz, Team, Automatisierung, Sprachdaten, Diktat ----------
        pr.MapPut("/{project}/privacy", (HttpContext h, string project, Core.Privacy.PrivacySettings ps, CancellationToken ct) =>
                Call(() => S(h).SetPrivacyAsync(project, ps, ct)))
            .WithSummary("Datenschutz-Einstellungen setzen");
        pr.MapGet("/{project}/privacy/detected", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).DetectSensitiveAsync(project, ct)))
            .WithSummary("Was in den Dateien geschützt würde");
        pr.MapGet("/{project}/workflow", (HttpContext h, string project, CancellationToken ct) => Call(() => S(h).WorkflowAsync(project, ct)))
            .WithSummary("Arbeitsschritte aller Dateien");
        pr.MapPost("/{project}/process-folder", async (HttpContext h, string project, WatchRunDto r, CancellationToken ct) =>
                Admin(h) ?? await Call(() => S(h).ProcessFolderAsync(project, r, ct)))
            .WithSummary("Eingangsordner einmal verarbeiten (nur Admin)");
        api.MapGet("/language-data", (HttpContext h, string sourceLanguage, string targetLanguage, CancellationToken ct) =>
                Call(() => S(h).LanguageDataAsync(sourceLanguage, targetLanguage, ct)))
            .WithSummary("Freie Sprachdaten für ein Sprachpaar");
        pr.MapPost("/{project}/language-data", async (HttpContext h, string project, LanguageDataInstallDto r, CancellationToken ct) =>
                Admin(h) ?? await Call(() => S(h).InstallLanguageDataAsync(project, r, ct)))
            .WithSummary("Freien Sprachdatensatz laden und anbinden (nur Admin)");
        api.MapPost("/transcribe", (HttpContext h, TranscribeDto r, CancellationToken ct) => Call(() => S(h).TranscribeAsync(r, ct)))
            .WithSummary("Aufnahme (WAV) in Text umwandeln");

        // ---------- Erweiterungen ----------
        api.MapGet("/extensions", (HttpContext h, CancellationToken ct) => Call(() => S(h).ExtensionsAsync(ct)))
            .WithSummary("Installierte Erweiterungen mit ihren Befehlen");
        api.MapPost("/extensions/commands/{command}", (HttpContext h, string command, ExtensionRunDto r, CancellationToken ct) =>
                Call(() => S(h).RunCommandAsync(command, r, ct)))
            .WithSummary("Befehl einer Erweiterung ausführen");

        return app;
    }

    // ---------- Hilfen ----------

    private static IResult? Admin(HttpContext ctx) => (ctx.Items["apiUser"] as ApiUser)?.IsAdmin == true ? null : Results.StatusCode(403);

    private static Task<IResult> Call(Func<Task> action) => Call(async () =>
    {
        await action();
        return true;
    });

    /// <summary>Ergebnis als JSON; Fehler der Schnittstelle werden zu passenden HTTP-Antworten (der SDK-Client macht daraus wieder dieselben Ausnahmen).</summary>
    private static async Task<IResult> Call<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        catch (UnauthorizedAccessException ex) { return Results.Problem(ex.Message, statusCode: 403); }
        catch (Exception ex) when (ex is AiException or RemoteException) { return Results.Problem(ex.Message, statusCode: 502); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException or InvalidDataException)
        { return Results.BadRequest(ex.Message); }
    }
}
