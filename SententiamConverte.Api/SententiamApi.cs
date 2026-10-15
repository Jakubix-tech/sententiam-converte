using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Api;

/// <summary>
/// Die vollständige REST-Schnittstelle (Version 2): alle Funktionen des Programms – Projekte, Dateien, Segmente,
/// Vorübersetzung, Analyse, QA, TM, Terminologie, Wissen, Alignment, Pakete, Agenten und Erweiterungen.
/// Beschreibung im OpenAPI-Format unter /openapi/v2.json.
/// </summary>
public static class SententiamApi
{
    public const string Version = "2.0";

    public static IServiceCollection AddSententiamApi(this IServiceCollection services, ApiOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(new ProjectCache(options));
        services.AddOpenApi("v2", o => o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v2", StringComparison.OrdinalIgnoreCase) == true);
        services.Configure<Microsoft.AspNetCore.OpenApi.OpenApiOptions>("v2", o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "Sententiam converte API";
            doc.Info.Version = Version;
            doc.Info.Description = "Alle Funktionen des Übersetzungswerkzeugs. Anmeldung: Kopfzeile „Authorization: Bearer <Schlüssel>“.";
            return Task.CompletedTask;
        }));
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    /// <summary>Hält geöffnete Projekte; alle Zugriffe auf ein Projekt laufen nacheinander.</summary>
    public sealed class ProjectCache(ApiOptions options)
    {
        private readonly ConcurrentDictionary<string, TranslationProject> _open = new(StringComparer.OrdinalIgnoreCase);

        public ApiOptions Options => options;

        public IEnumerable<string> Names() =>
            Directory.Exists(options.ProjectsRoot)
                ? Directory.EnumerateDirectories(options.ProjectsRoot, "*", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, TranslationProject.FileName)))
                    .Select(d => Path.GetRelativePath(options.ProjectsRoot, d).Replace('\\', '/'))
                : [];

        public TranslationProject Get(string name)
        {
            if (options.OpenProject?.Invoke(name) is { } open) return open;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ProjectsRoot)) + Path.DirectorySeparatorChar;
            var folder = Path.GetFullPath(Path.Combine(root, name));
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || folder.Length == root.Length) throw new KeyNotFoundException(name);
            if (!File.Exists(Path.Combine(folder, TranslationProject.FileName))) throw new KeyNotFoundException(name);
            return _open.GetOrAdd(name, _ => TranslationProject.Open(folder));
        }

        public TranslationProject Create(ProjectCreateDto c)
        {
            if (!ResourceNameOk(c.Name)) throw new ArgumentException("Ungültiger Projektname.");
            var folder = Path.Combine(options.ProjectsRoot, c.Name);
            if (File.Exists(Path.Combine(folder, TranslationProject.FileName))) throw new ArgumentException("Ein Projekt mit diesem Namen gibt es schon.");
            var p = TranslationProject.Create(folder, c.Name, c.SourceLanguage, c.TargetLanguages, c.Topic ?? "");
            if (options.OpenProject == null) return _open[c.Name] = p;
            // Der Gastgeber verwaltet seine Projekte selbst – dieselbe Instanz verwenden
            p.Dispose();
            return Get(c.Name);
        }

        private static bool ResourceNameOk(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, @"^[\p{L}\p{Nd}][\p{L}\p{Nd} ._\-]{0,79}$");

        /// <summary>Macht aus einem fremden Namen (z. B. aus einem Paket) einen gültigen Ordnernamen ohne Pfadanteile.</summary>
        public static string SafeName(string name)
        {
            var cleaned = new string(name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '_' or '-' ? ch : '_').ToArray()).Trim('.', ' ');
            if (cleaned.Length == 0 || !char.IsLetterOrDigit(cleaned[0])) cleaned = "Projekt " + cleaned;
            cleaned = (cleaned.Length > 80 ? cleaned[..80] : cleaned).TrimEnd('.', ' ');      // Windows verträgt kein Leerzeichen/Punkt am Ende
            return ResourceNameOk(cleaned) ? cleaned : throw new ArgumentException("Ungültiger Projektname.");
        }
    }

    public static WebApplication MapSententiamApi(this WebApplication app)
    {
        var o = app.Services.GetRequiredService<ApiOptions>();
        var projects = app.Services.GetRequiredService<ProjectCache>();
        app.MapOpenApi("/openapi/{documentName}.json");

        var api = app.MapGroup("/api/v2").AddEndpointFilter(async (ctx, next) =>
        {
            var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
            var user = header.StartsWith("Bearer ") ? o.Authorize(header[7..].Trim()) : null;
            if (user == null) return Results.Unauthorized();
            var method = ctx.HttpContext.Request.Method;
            if (method != "GET" && !user.CanWrite) return Results.StatusCode(403);
            ctx.HttpContext.Items["apiUser"] = user;
            return await next(ctx);
        });

        // ---------- Allgemein ----------
        api.MapGet("/", () => new ApiInfo("Sententiam converte", Version, "/openapi/v2.json",
                ["projects", "files", "segments", "pretranslate", "analysis", "qa", "tm", "terms", "knowledge", "align", "packages", "agents", "extensions"]))
            .WithSummary("Übersicht über die Schnittstelle");
        api.MapGet("/formats", () => FileFilters.All.Select(f => new { f.Id, f.Name, f.Extensions }))
            .WithSummary("Unterstützte Dateiformate");
        api.MapGet("/languages", () => Languages.Common).WithSummary("Bekannte Sprachcodes");
        api.MapGet("/source-profiles", () => o.Ai().SourceProfiles).WithSummary("Fachquellen-Profile (Themen mit Fachseiten je Sprache)");

        // ---------- Projekte ----------
        var pr = api.MapGroup("/projects");
        pr.MapGet("", () => projects.Names()).WithSummary("Alle Projekte");
        pr.MapPost("", (ProjectCreateDto c) => Run(() => Dto.Project(projects.Create(c)))).WithSummary("Projekt anlegen");
        pr.MapGet("/{project}", (string project) => With(projects, project, p => Dto.Project(p))).WithSummary("Projekt mit Dateien und Ressourcen");
        pr.MapPatch("/{project}", (string project, ProjectUpdateDto u) => With(projects, project, p =>
        {
            if (u.Topic != null) p.Topic = u.Topic;
            if (u.PretranslateMinScore is { } m) p.PretranslateMinScore = Math.Clamp(m, 50, 100);
            if (u.ConfirmContextMatches is { } c) p.ConfirmContextMatches = c;
            if (u.SourceProfile != null) p.SourceProfile = u.SourceProfile.Length == 0 ? null : u.SourceProfile;
            p.Save();
            return Dto.Project(p);
        })).WithSummary("Projekteinstellungen ändern");
        pr.MapPost("/{project}/resources", (string project, ResourceRef r) => With(projects, project, p => { p.Attach(r); return Dto.Project(p); }))
            .WithSummary("TM, Terminologie oder Wissensdatenbank anbinden (lokal oder Teamserver, je Sprache)");
        pr.MapDelete("/{project}/resources", (string project, ResourceKind kind, string location, string? server) => With(projects, project, p =>
        {
            var r = p.Resources.FirstOrDefault(x => x.Kind == kind && x.Location == location && x.Server == server) ?? throw new KeyNotFoundException(location);
            p.Detach(r);
            return Dto.Project(p);
        })).WithSummary("Angebundene Ressource lösen");

        // ---------- Dateien ----------
        pr.MapGet("/{project}/files", (string project) => With(projects, project, p => p.Files.Select(f => Dto.File(p, f)).ToList()))
            .WithSummary("Dateien des Projekts");
        pr.MapPost("/{project}/files", (string project, FileUploadDto f) => With(projects, project, p =>
                Dto.File(p, p.AddFile(new MemoryStream(Convert.FromBase64String(f.Base64)), Path.GetFileName(f.Name)))))
            .WithSummary("Datei hinzufügen (Inhalt Base64)");
        pr.MapDelete("/{project}/files/{file}", (HttpContext ctx, string project, string file) => Admin(ctx) ??
                With(projects, project, p => { p.RemoveFile(FileOf(p, file)); return true; }))
            .WithSummary("Datei entfernen");
        pr.MapGet("/{project}/files/{file}/{lang}", (string project, string file, string lang) => With(projects, project, p =>
            {
                var doc = p.LoadDocument(FileOf(p, file), lang);
                return doc.Segments.Select(s => Dto.Segment(doc, s)).ToList();
            }))
            .WithSummary("Alle Segmente einer Datei in einer Zielsprache");
        pr.MapGet("/{project}/files/{file}/{lang}/target", (string project, string file, string lang) => With(projects, project, p =>
            {
                var f = FileOf(p, file);
                var ms = new MemoryStream();
                p.ExportTarget(f, p.LoadDocument(f, lang), ms);
                return new FileUploadDto(p.TargetFileName(f, lang), Convert.ToBase64String(ms.ToArray()));
            }))
            .WithSummary("Zieldatei im Originalformat (Base64)");
        pr.MapGet("/{project}/files/{file}/{lang}/xliff", (string project, string file, string lang) => With(projects, project, p =>
            {
                var ms = new MemoryStream();
                XliffFilter.ExportBilingual(p.LoadDocument(FileOf(p, file), lang), ms);
                return new FileUploadDto(Path.GetFileNameWithoutExtension(FileOf(p, file).OriginalName) + "_" + lang + ".xlf", Convert.ToBase64String(ms.ToArray()));
            }))
            .WithSummary("Zweisprachiges XLIFF zum Austausch (Base64)");

        // ---------- Segmente ----------
        var seg = pr.MapGroup("/{project}/files/{file}/{lang}/segments/{id:int}");
        seg.MapGet("", (string project, string file, string lang, int id) => WithSegment(projects, project, file, lang, id, (p, f, d, s) => Dto.Segment(d, s), false))
            .WithSummary("Ein Segment");
        seg.MapPut("", (string project, string file, string lang, int id, SegmentUpdateDto u) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) =>
                {
                    if (u.Target != null) { s.Target = u.Target; s.Origin = TranslationOrigin.Manual; if (s.Status == SegmentStatus.NotTranslated) s.Status = SegmentStatus.Draft; }
                    if (u.Status is { } st) s.Status = st;
                    if (u.Comment != null) s.Comment = u.Comment.Length == 0 ? null : u.Comment;
                    s.Modified = DateTime.UtcNow;
                    return Dto.Segment(d, s);
                }, true))
            .WithSummary("Übersetzung, Status oder Kommentar eines Segments setzen");
        seg.MapPost("/confirm", (string project, string file, string lang, int id, ConfirmDto c) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) =>
                {
                    var propagated = Workflow.Confirm(p, d, s, c.Status ?? SegmentStatus.Translated);
                    return new { Segment = Dto.Segment(d, s), Propagated = propagated };
                }, true))
            .WithSummary("Segment bestätigen: ins TM schreiben (mit Kontext) und auf gleiche Segmente übertragen");
        seg.MapGet("/matches", (string project, string file, string lang, int id, int? minScore) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => Workflow.Lookup(p, d, s, minScore), false))
            .WithSummary("TM-Treffer mit Kontextbewertung (101 % = Kontexttreffer)");
        seg.MapGet("/terms", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => Workflow.Terms(p, d, s), false))
            .WithSummary("Erkannte Terminologie");
        seg.MapGet("/fragments", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => Workflow.Fragments(p, d, s), false))
            .WithSummary("Fragment-Treffer");
        seg.MapGet("/knowledge", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => Workflow.Knowledge(p, d, s), false))
            .WithSummary("Passende Wissenseinträge");
        seg.MapGet("/suggestions", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => Workflow.Suggestions(p, d, s, Workflow.Lookup(p, d, s)), false))
            .WithSummary("AutoSuggest-Vorschläge");
        seg.MapPost("/apply-match", (string project, string file, string lang, int id, int index) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) =>
                {
                    var m = Workflow.Lookup(p, d, s).ElementAtOrDefault(index - 1) ?? throw new KeyNotFoundException("Treffer");
                    Workflow.ApplyMatch(s, m, d.TargetLanguage);
                    return Dto.Segment(d, s);
                }, true))
            .WithSummary("TM-Treffer Nr. index einsetzen, ab 1 wie Strg+1 (Zahlen werden angepasst)");
        seg.MapPost("/copy-source", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) => { Workflow.CopySourceToTarget(s); return Dto.Segment(d, s); }, true))
            .WithSummary("Quelle ins Ziel kopieren");
        seg.MapPost("/split", (string project, string file, string lang, int id, SplitDto sp) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) =>
                    Workflow.Split(d, s, sp.SourceIndex) is { } second ? new[] { Dto.Segment(d, s), Dto.Segment(d, second) } : throw new ArgumentException("Hier kann nicht geteilt werden."), true))
            .WithSummary("Segment an einer Stelle im Quelltext teilen");
        seg.MapPost("/merge", (string project, string file, string lang, int id) =>
                WithSegment(projects, project, file, lang, id, (p, f, d, s) =>
                    Workflow.MergeWithNext(d, s) ? Dto.Segment(d, s) : throw new ArgumentException("Zusammenführen nicht möglich."), true))
            .WithSummary("Segment mit dem folgenden zusammenführen");

        pr.MapPost("/{project}/files/{file}/{lang}/replace", (string project, string file, string lang, string find, string replace, bool? matchCase) =>
                With(projects, project, p =>
                {
                    var f = FileOf(p, file);
                    var d = p.LoadDocument(f, lang);
                    var n = Workflow.ReplaceInTargets(d, find, replace, matchCase ?? false);
                    p.SaveDocument(f, d);
                    o.DocumentChanged?.Invoke(p, f.Id, lang);
                    return n;
                }))
            .WithSummary("Suchen und Ersetzen im Zieltext");

        // ---------- Stapelaufgaben ----------
        pr.MapPost("/{project}/pretranslate", (string project, PretranslateDto r) => With(projects, project, p =>
            {
                var results = new List<PretranslateResult>();
                foreach (var (f, lang) in p.Documents())
                {
                    var d = p.LoadDocument(f, lang);
                    var (a, c) = Workflow.Pretranslate(p, d, r.ConfirmExact, r.Overwrite);
                    p.SaveDocument(f, d);
                    o.DocumentChanged?.Invoke(p, f.Id, lang);
                    results.Add(new PretranslateResult(f.OriginalName, lang, a, c));
                }
                return results;
            }))
            .WithSummary("Vorübersetzung aus den TMs");
        pr.MapGet("/{project}/analysis", (string project) => With(projects, project, p =>
                p.TargetLanguages.SelectMany(lang =>
                {
                    var r = Analyzer.Analyze(p.AllDocuments(lang), p.TmsFor(lang));
                    return r.Bands.Where(b => b.Value.Segments > 0)
                        .Select(b => new AnalysisRowDto(lang, AnalysisResult.BandName(b.Key), b.Value.Segments, b.Value.Words, b.Value.Characters));
                }).ToList()))
            .WithSummary("Analyse nach Trefferstufen");
        pr.MapGet("/{project}/qa", (string project) => With(projects, project, p =>
                p.Documents().SelectMany(x =>
                    QaChecker.Check(p.LoadDocument(x.File, x.Language), p.Qa, p.ProjectTermbase)
                        .Select(i => new QaIssueDto(x.File.OriginalName, x.Language, i.SegmentId, i.Severity.ToString(), i.Check, i.Message))).ToList()))
            .WithSummary("Qualitätsprüfung aller Dateien");
        pr.MapPut("/{project}/qa-settings", (string project, QaSettings q) => With(projects, project, p => { p.Qa = q; p.Save(); return p.Qa; }))
            .WithSummary("QA-Einstellungen und eigene Regeln setzen");

        // ---------- TM ----------
        pr.MapGet("/{project}/tm/search", (string project, string source, string sourceLanguage, string targetLanguage, int? minScore, string? previous, string? next) =>
                With(projects, project, p => p.TmsFor(targetLanguage)
                    .SelectMany(tm => tm.Search(sourceLanguage, targetLanguage, source, minScore ?? 70, 10, previous == null ? null : new TmContext(previous, next, null)))
                    .OrderByDescending(m => m.Score).Take(10).ToList()))
            .WithSummary("TM-Suche (optional mit Kontext)");
        pr.MapGet("/{project}/tm/concordance", (string project, string text, string sourceLanguage, string targetLanguage, bool? inTarget) =>
                With(projects, project, p => p.TmsFor(targetLanguage)
                    .SelectMany(tm => tm.Concordance(sourceLanguage, targetLanguage, text, inTarget ?? false, 30)).ToList()))
            .WithSummary("Konkordanzsuche");
        pr.MapPost("/{project}/tm", (string project, TmAddDto u) => With(projects, project, p =>
            {
                p.ProjectTm.Add(u.SourceLanguage, u.TargetLanguage, u.Source, u.Target, "API", u.Structure, null, u.Previous, u.Next);
                return true;
            }))
            .WithSummary("Übersetzung ins Projekt-TM schreiben");
        pr.MapGet("/{project}/tm/units", (string project, string? filter, int? offset, int? limit) =>
                With(projects, project, p => p.ProjectTm.Browse(filter, offset ?? 0, Math.Clamp(limit ?? 50, 1, 1000))))
            .WithSummary("Projekt-TM blättern");
        pr.MapPut("/{project}/tm/units/{id:long}", (string project, long id, TmUpdateRequest u) =>
                With(projects, project, p => { p.ProjectTm.Update(id, u.Source, u.Target); return true; }))
            .WithSummary("TM-Eintrag ändern");
        pr.MapDelete("/{project}/tm/units/{id:long}", (string project, long id) => With(projects, project, p => { p.ProjectTm.Delete(id); return true; }))
            .WithSummary("TM-Eintrag löschen");
        pr.MapPost("/{project}/tm/import", (string project, FileUploadDto f) => With(projects, project, p =>
            {
                var bytes = Convert.FromBase64String(f.Base64);
                if (f.Name.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase))
                {
                    var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdltm");
                    File.WriteAllBytes(tmp, bytes);
                    try { return SdltmImporter.Import(tmp, p.ProjectTm); }
                    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(tmp); }
                }
                return TmxIo.Import(new MemoryStream(bytes), p.ProjectTm).Imported;
            }))
            .WithSummary("TM importieren (TMX oder .sdltm, Base64)");
        pr.MapGet("/{project}/tm/export", (string project) => With(projects, project, p =>
            {
                var ms = new MemoryStream();
                TmxIo.Export(p.ProjectTm, ms);
                return new FileUploadDto(p.Name + ".tmx", Convert.ToBase64String(ms.ToArray()));
            }))
            .WithSummary("Projekt-TM als TMX (Base64)");

        // ---------- Terminologie ----------
        pr.MapGet("/{project}/terms", (string project, string? query, string? language) => With(projects, project, p =>
                string.IsNullOrWhiteSpace(query)
                    ? p.ProjectTermbase.AllConcepts().ToList()
                    : p.Termbases.SelectMany(tb => tb.Lookup(language ?? p.SourceLanguage, query)).DistinctBy(c => (c.Id, c.Terms.Count)).ToList()))
            .WithSummary("Terminologie durchsuchen");
        pr.MapPost("/{project}/terms", (string project, ConceptDto c) => With(projects, project, p =>
                p.ProjectTermbase.AddConcept(c.Terms.Select(t => (t.Lang, t.Text, t.Status)), c.Definition, c.Domain, c.Source)))
            .WithSummary("Begriff anlegen");
        pr.MapDelete("/{project}/terms/{id:long}", (string project, long id) => With(projects, project, p => { p.ProjectTermbase.DeleteConcept(id); return true; }))
            .WithSummary("Begriff löschen");
        pr.MapPost("/{project}/terms/import", (string project, FileUploadDto f) =>
                With(projects, project, p => TbxIo.Import(new MemoryStream(Convert.FromBase64String(f.Base64)), p.ProjectTermbase)))
            .WithSummary("Terminologie importieren (TBX/XML, Base64)");
        pr.MapGet("/{project}/terms/export", (string project) => With(projects, project, p =>
            {
                var ms = new MemoryStream();
                TbxIo.Export(p.ProjectTermbase, ms);
                return new FileUploadDto(p.Name + ".tbx", Convert.ToBase64String(ms.ToArray()));
            }))
            .WithSummary("Terminologie als TBX (Base64)");

        // ---------- Wissen ----------
        pr.MapGet("/{project}/knowledge", (string project, string? query, string? language) => With(projects, project, p =>
                string.IsNullOrWhiteSpace(query)
                    ? p.KnowledgeBases.SelectMany(k => k.Browse(language, null, 0, 200)).ToList()
                    : p.KnowledgeBases.SelectMany(k => k.Search(language, query, 20)).ToList()))
            .WithSummary("Wissen durchsuchen (alle angebundenen Datenbanken)");
        pr.MapPost("/{project}/knowledge", (HttpContext ctx, string project, KnowledgeEntry e) => With(projects, project, p =>
                p.ProjectKnowledge.Add(e with { Author = e.Author ?? (ctx.Items["apiUser"] as ApiUser)?.Name })))
            .WithSummary("Wissenseintrag anlegen (Projekt-Wissen)");
        pr.MapPut("/{project}/knowledge/{id:long}", (string project, long id, KnowledgeEntry e) =>
                With(projects, project, p => { p.ProjectKnowledge.Update(e with { Id = id }); return true; }))
            .WithSummary("Wissenseintrag ändern");
        pr.MapDelete("/{project}/knowledge/{id:long}", (string project, long id) => With(projects, project, p => { p.ProjectKnowledge.Delete(id); return true; }))
            .WithSummary("Wissenseintrag löschen");

        // ---------- Pakete, Alignment, Texterkennung ----------
        api.MapPost("/packages/import", (FileUploadDto f, string? topic, string? name) => Run(() =>
            {
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdlppx");
                File.WriteAllBytes(tmp, Convert.FromBase64String(f.Base64));
                try
                {
                    // Der Name im Paket ist fremde Eingabe: nur als bereinigter Ordnername unter dem Projektordner verwenden
                    var folderName = ProjectCache.SafeName(name ?? ProjectPackage.Inspect(tmp).Name);
                    var folder = Path.Combine(o.ProjectsRoot, folderName);
                    if (Directory.Exists(folder)) throw new ArgumentException($"Ein Projekt „{folderName}“ gibt es schon (anderen Namen mit ?name=… angeben).");
                    ProjectPackage.Import(tmp, folder, topic ?? "").Dispose();
                    return Dto.Project(projects.Get(folderName));
                }
                finally { File.Delete(tmp); }
            }))
            .WithSummary("Projektpaket (.sdlppx) als neues Projekt übernehmen");
        pr.MapGet("/{project}/return-package", (string project) => With(projects, project, p =>
            {
                var ms = new MemoryStream();
                ProjectPackage.ExportReturnPackage(p, ms);
                return new FileUploadDto(p.Name + ".sdlrpx", Convert.ToBase64String(ms.ToArray()));
            }))
            .WithSummary("Rückgabepaket (.sdlrpx, Base64)");
        api.MapPost("/align", (AlignRequest r) => Run(() =>
            {
                List<string> Sentences(FileUploadDto f, string lang)
                {
                    var filter = FileFilters.ForFile(f.Name) ?? throw new NotSupportedException(f.Name);
                    var d = filter.Read(new MemoryStream(Convert.FromBase64String(f.Base64)), f.Name, lang, lang, new Segmenter(lang));
                    return d.Segments.Where(s => !s.Locked).Select(s => s.Source).ToList();
                }
                var pairs = Aligner.Align(Sentences(r.Source, r.SourceLanguage), Sentences(r.Target, r.TargetLanguage));
                if (r.ImportIntoProject is { } name)
                {
                    var p = projects.Get(name);
                    lock (p)
                        foreach (var pair in pairs.Where(x => x.Accepted))
                            p.ProjectTm.Add(r.SourceLanguage, r.TargetLanguage, pair.SourceText, pair.TargetText, "Alignment");
                }
                return pairs;
            }))
            .WithSummary("Alte Übersetzungen ausrichten; optional sichere Paare ins Projekt-TM übernehmen");
        pr.MapPost("/{project}/ocr", async (string project, FileUploadDto f, CancellationToken ct) =>
            {
                var p = projects.Get(project);
                var ai = o.Ai();
                var claude = ai.Providers.FirstOrDefault(x => x.Kind == AiProviderKind.Anthropic)
                             ?? throw new InvalidOperationException("Für die Texterkennung wird ein Claude-Zugang benötigt.");
                var text = await new ClaudeChatModel(claude).ExtractPdfTextAsync(Convert.FromBase64String(f.Base64), ct);
                var docx = new MemoryStream();
                PdfFilter.OcrTextToDocx(text, p.SourceLanguage, docx);
                docx.Position = 0;
                lock (p) return Results.Ok(Dto.File(p, p.AddFile(docx, Path.GetFileNameWithoutExtension(f.Name) + "_erkannt.docx")));
            })
            .WithSummary("Gescanntes PDF per KI erkennen und als DOCX hinzufügen");

        // ---------- Agenten ----------
        var ag = api.MapGroup("/agents");
        ag.MapPost("/translate", async (TranslateRequest r, CancellationToken ct) => await RunAsync(async () =>
            {
                var ctx = AgentContext.From(o.Ai(), r.ProviderId ?? o.Ai().TranslateProviderId);
                if (r.Project != null && r.File != null && r.Language != null && r.SegmentId is { } id)
                {
                    var p = projects.Get(r.Project);
                    BilingualDocument d; Segment s; List<TmMatch> m; List<TermHit> t; List<KnowledgeEntry> k; string? prev, next;
                    lock (p)
                    {
                        o.BeforeProjectAccess?.Invoke(p);
                        d = p.LoadDocument(FileOf(p, r.File), r.Language);
                        var all = d.Segments.ToList();
                        s = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Segment");
                        var i = all.IndexOf(s);
                        prev = i > 0 ? all[i - 1].Source : null;
                        next = i < all.Count - 1 ? all[i + 1].Source : null;
                        m = Workflow.Lookup(p, d, s);
                        t = Workflow.Terms(p, d, s);
                        k = Workflow.Knowledge(p, d, s);
                    }
                    return new { Translation = await new AiTranslator(ctx).TranslateAsync(s.Source, d.SourceLanguage, d.TargetLanguage, p.Topic, m, t, prev, next, ct: ct, knowledge: k) };
                }
                if (r.Text == null || r.SourceLanguage == null || r.TargetLanguage == null) throw new ArgumentException("Text und Sprachen angeben (oder Projekt/Datei/Sprache/Segment).");
                return new { Translation = await new AiTranslator(ctx).TranslateAsync(r.Text, r.SourceLanguage, r.TargetLanguage, r.Topic, ct: ct) };
            }))
            .WithSummary("KI-Übersetzung eines Segments (mit TM, Terminologie, Wissen) oder eines freien Texts");
        ag.MapPost("/evaluate", async (EvaluateRequest r, CancellationToken ct) => await RunAsync(async () =>
            {
                var ctx = AgentContext.From(o.Ai(), o.Ai().EvaluateProviderId);
                List<TermHit>? terms = null;
                if (r.Project != null)
                {
                    var p = projects.Get(r.Project);
                    lock (p) terms = p.TermbasesFor(r.TargetLanguage).SelectMany(tb => tb.Recognize(r.Source, r.SourceLanguage, r.TargetLanguage)).ToList();
                }
                return await new EvaluationAgent(ctx).RunAsync(r.Source, r.SourceLanguage, r.TargetLanguage, r.Candidates, r.Topic, terms, r.CheckUsage && ctx.Search != null, ct);
            }))
            .WithSummary("Übersetzungsvarianten bewerten");
        ag.MapPost("/usage", async (UsageRequest r, CancellationToken ct) => await RunAsync(async () =>
                await new UsageCheckAgent(AgentContext.From(o.Ai(), o.Ai().ResearchProviderId))
                    .RunAsync(r.Expression, r.Language, r.Topic, r.Alternatives, r.Alternatives is not { Count: > 0 }, ct)))
            .WithSummary("Usus-Prüfung: ist ein Ausdruck üblich?");
        ag.MapPost("/research", async (ResearchRequest r, CancellationToken ct) => await RunAsync(async () =>
            {
                var result = await new TermResearchAgent(AgentContext.From(o.Ai(), o.Ai().ResearchProviderId))
                    .RunAsync(r.Topic, r.SourceLanguage, r.TargetLanguages, r.SeedTerms, r.PagesPerLanguage, ct, o.Ai().Profile(r.SourceProfile));
                if (r.SaveToProject != null)
                {
                    var p = projects.Get(r.SaveToProject);
                    lock (p)
                        foreach (var c in result.Candidates.Where(c => c.Confidence >= 0.6))
                            p.ProjectTermbase.AddConcept(c.Terms.Select(kv => (kv.Key, kv.Value, TermStatus.Preferred)), c.Definition, r.Topic);
                }
                return result;
            }))
            .WithSummary("Themenrecherche: mehrsprachige Begriffsliste mit Belegen");
        ag.MapPost("/knowledge", async (KnowledgeAgentRequest r, HttpContext http, CancellationToken ct) => await RunAsync(async () =>
            {
                var p = projects.Get(r.Project);
                var kb = r.KnowledgeBase == null ? p.ProjectKnowledge : p.KnowledgeBases.FirstOrDefault(k => k.Name == r.KnowledgeBase) ?? throw new KeyNotFoundException(r.KnowledgeBase);
                return await new KnowledgeAgent(AgentContext.From(o.Ai(), o.Ai().ResearchProviderId))
                    .RunAsync(r.Topic, r.Languages, kb, r.Focus, r.PagesPerLanguage, (http.Items["apiUser"] as ApiUser)?.Name, ct,
                        o.Ai().Profile(r.SourceProfile ?? p.SourceProfile));
            }))
            .WithSummary("Wissens-Agent: Wissensdatenbank aus Online-Quellen füllen");
        ag.MapPost("/compare", async (CompareRequest r, HttpContext http, CancellationToken ct) => await RunAsync(async () =>
            {
                TranslationProject? p = r.Project == null ? null : projects.Get(r.Project);
                var profile = o.Ai().Profile(r.SourceProfile ?? p?.SourceProfile);
                List<TermHit>? known = null;
                if (p != null) lock (p) known = p.TermbasesFor(r.TargetLanguage).SelectMany(tb => tb.Recognize(r.Term, r.SourceLanguage, r.TargetLanguage)).ToList();
                var result = await new TermComparisonAgent(AgentContext.From(o.Ai(), o.Ai().ResearchProviderId))
                    .RunAsync(new TermQuery(r.Term, r.SourceLanguage, r.TargetLanguage, r.Context, r.Topic ?? p?.Topic, profile, r.Candidates, known), 3, ct);
                if (p != null)
                    lock (p)
                    {
                        if (r.SaveToTermbase) TermComparisonAgent.SaveToTermbase(result, p.ProjectTermbase, r.SourceLanguage, r.TargetLanguage, r.Topic ?? p.Topic);
                        if (r.SaveToKnowledge) TermComparisonAgent.SaveToKnowledge(result, p.ProjectKnowledge, r.TargetLanguage, r.Topic ?? p.Topic, (http.Items["apiUser"] as ApiUser)?.Name);
                    }
                return result;
            }))
            .WithSummary("Begriffsvergleich: Bedeutung im Kontext, Fachseiten beider Sprachen, begründete Empfehlung");

        // ---------- Erweiterungen ----------
        api.MapGet("/extensions", () => (o.Extensions?.Extensions ?? []).Select(e => new
            {
                e.Manifest.Id, e.Manifest.Name, e.Manifest.Version, e.Manifest.Author, e.Manifest.Description, e.Manifest.Enabled, e.Loaded, e.Error,
                Commands = e.Commands.Select(c => new { c.Id, c.Name, c.Description, Scope = c.Scope.ToString() }),
                e.Contributions,
            }))
            .WithSummary("Installierte Erweiterungen mit ihren Befehlen");
        api.MapPost("/extensions/commands/{command}", async (string command, ExtensionRunDto r, CancellationToken ct) => await RunAsync(async () =>
            {
                var cmd = o.Extensions?.FindCommand(command) ?? throw new KeyNotFoundException(command);
                var p = r.Project == null ? null : projects.Get(r.Project);
                ProjectFile? f = null;
                BilingualDocument? d = null;
                if (p != null)
                    lock (p)
                    {
                        o.BeforeProjectAccess?.Invoke(p);
                        f = r.File != null ? FileOf(p, r.File) : null;
                        d = f != null && r.Language != null ? p.LoadDocument(f, r.Language) : null;
                    }
                var s = d != null && r.SegmentId is { } id ? d.Segments.FirstOrDefault(x => x.Id == id) : null;
                var ai = o.Ai();
                var ctx = new ExtensionContext
                {
                    Project = p, File = f, Document = d, Segment = s, Arguments = r.Arguments ?? new(),
                    ChatModel = ai.Get(ai.TranslateProviderId) is { } prov ? ChatModelFactory.Create(prov) : null,
                    Search = SearchProviderFactory.Create(ai),
                };
                var result = await cmd.RunAsync(ctx, ct);
                if (result.DocumentChanged && p != null && f != null && d != null)
                {
                    lock (p)
                    {
                        p.SaveDocument(f, d);
                        o.DocumentChanged?.Invoke(p, f.Id, d.TargetLanguage);
                    }
                }
                return result;
            }))
            .WithSummary("Befehl einer Erweiterung ausführen");

        return app;
    }

    // ---------- Hilfen ----------

    private static IResult? Admin(HttpContext ctx) => (ctx.Items["apiUser"] as ApiUser)?.IsAdmin == true ? null : Results.StatusCode(403);

    private static ProjectFile FileOf(TranslationProject p, string idOrName) =>
        p.Files.FirstOrDefault(f => f.Id == idOrName) ?? p.Files.FirstOrDefault(f => string.Equals(f.OriginalName, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(idOrName);

    private static IResult With<T>(ProjectCache cache, string project, Func<TranslationProject, T> action) => Run(() =>
    {
        var p = cache.Get(project);
        lock (p)
        {
            cache.Options.BeforeProjectAccess?.Invoke(p);
            return action(p);
        }
    });

    private static IResult WithSegment<T>(ProjectCache cache, string project, string file, string lang, int id,
        Func<TranslationProject, ProjectFile, BilingualDocument, Segment, T> action, bool save) => Run(() =>
    {
        var p = cache.Get(project);
        lock (p)
        {
            cache.Options.BeforeProjectAccess?.Invoke(p);
            var f = FileOf(p, file);
            var d = p.LoadDocument(f, lang);
            var s = d.Segments.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Segment {id}");
            var result = action(p, f, d, s);
            if (save)
            {
                p.SaveDocument(f, d);
                cache.Options.DocumentChanged?.Invoke(p, f.Id, lang);
            }
            return result;
        }
    });

    private static IResult Run<T>(Func<T> action)
    {
        try { return Results.Ok(action()); }
        catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException or InvalidDataException)
        { return Results.BadRequest(ex.Message); }
        catch (RemoteException ex) { return Results.Problem(ex.Message, statusCode: 502); }
    }

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        catch (AiException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
        { return Results.BadRequest(ex.Message); }
    }
}
