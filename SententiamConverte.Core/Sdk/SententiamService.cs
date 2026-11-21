using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Analysis;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Core.Projects;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Core.Sdk;

/// <summary>Hält geöffnete Projekte; alle Zugriffe auf ein Projekt laufen nacheinander (Sperre auf die Projektinstanz).</summary>
public sealed class ProjectCache(SdkOptions options)
{
    private readonly ConcurrentDictionary<string, TranslationProject> _open = new(StringComparer.OrdinalIgnoreCase);

    public SdkOptions Options => options;

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
        if (!NameOk(c.Name)) throw new ArgumentException("Ungültiger Projektname.");
        var folder = Path.Combine(options.ProjectsRoot, c.Name);
        if (File.Exists(Path.Combine(folder, TranslationProject.FileName))) throw new ArgumentException("Ein Projekt mit diesem Namen gibt es schon.");
        var p = TranslationProject.Create(folder, c.Name, c.SourceLanguage, c.TargetLanguages, c.Topic ?? "");
        if (options.OpenProject == null) return _open[c.Name] = p;
        // Der Gastgeber verwaltet seine Projekte selbst – dieselbe Instanz verwenden
        p.Dispose();
        return Get(c.Name);
    }

    private static bool NameOk(string n) => Regex.IsMatch(n, @"^[\p{L}\p{Nd}][\p{L}\p{Nd} ._\-]{0,79}$");

    /// <summary>Macht aus einem fremden Namen (z. B. aus einem Paket) einen gültigen Ordnernamen ohne Pfadanteile.</summary>
    public static string SafeName(string name)
    {
        var cleaned = new string(name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '_' or '-' ? ch : '_').ToArray()).Trim('.', ' ');
        if (cleaned.Length == 0 || !char.IsLetterOrDigit(cleaned[0])) cleaned = "Projekt " + cleaned;
        cleaned = (cleaned.Length > 80 ? cleaned[..80] : cleaned).TrimEnd('.', ' ');      // Windows verträgt kein Leerzeichen/Punkt am Ende
        return NameOk(cleaned) ? cleaned : throw new ArgumentException("Ungültiger Projektname.");
    }
}

/// <summary>
/// Die interne Umsetzung von <see cref="ISententiam"/>: arbeitet direkt mit den Projekten auf diesem Rechner bzw. Server.
/// Die REST-API, die Desktop-App und Erweiterungen nutzen dieselbe Umsetzung.
/// </summary>
public sealed class SententiamService : ISententiam
{
    public const string Version = "2.1";

    private readonly SdkOptions _o;
    private readonly ProjectCache _projects;

    /// <param name="user">Wer arbeitet – erscheint als Autor neuer Wissenseinträge.</param>
    public SententiamService(SdkOptions options, string? user = null) : this(options, new ProjectCache(options), user, false) { }

    private SententiamService(SdkOptions options, ProjectCache projects, string? user, bool admin)
    {
        _o = options;
        _projects = projects;
        User = user;
        IsAdmin = admin;
    }

    public string? User { get; }

    /// <summary>Verwaltung: darf Arbeitsschritte setzen und Dateien unabhängig vom Schritt bearbeiten.</summary>
    public bool IsAdmin { get; }
    public ProjectCache Projects => _projects;

    /// <summary>Dieselben Projekte, aber im Namen einer anderen Person (z. B. je Anfrage an die REST-API).</summary>
    public SententiamService ForUser(string? user, bool admin = false) =>
        user == User && admin == IsAdmin ? this : new SententiamService(_o, _projects, user, admin);

    /// <summary>
    /// Team: Darf der angemeldete Benutzer diese Datei gerade ändern? (Arbeitsschritte; intern ohne Benutzer immer.)
    /// </summary>
    private void CheckWrite(TranslationProject p, string fileId, string language)
    {
        if (User != null) TeamWorkflow.CheckEditable(p, fileId, language, User, IsAdmin);
    }

    private bool MayWrite(TranslationProject p, string fileId, string language) =>
        User == null || TeamWorkflow.StepOf(p, fileId, language).MayEdit(User, IsAdmin);

    // ---------- Allgemein ----------

    public Task<ApiInfo> InfoAsync(CancellationToken ct = default) => Task.FromResult(new ApiInfo("Sententiam converte", Version, "/openapi/v2.json",
        ["projects", "files", "segments", "pretranslate", "analysis", "qa", "tm", "terms", "knowledge", "align", "packages", "agents", "extensions",
         "review", "evaluation", "quote", "templates", "qa-profiles", "tm-maintenance", "term-extraction", "import-translations", "privacy",
         "workflow", "automation", "language-data", "dictation"]));

    public Task<IReadOnlyList<FormatInfo>> FormatsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FormatInfo>>(FileFilters.All.Select(f => new FormatInfo(f.Id, f.Name, f.Extensions)).ToList());

    public Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct = default) => Task.FromResult(Languages.Common);

    public Task<IReadOnlyList<SourceProfile>> SourceProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceProfile>>(_o.Ai().SourceProfiles);

    // ---------- Projekte ----------

    public Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() => _projects.Names().ToList(), ct);

    public Task<ProjectDto> CreateProjectAsync(ProjectCreateDto project, CancellationToken ct = default) => Task.Run(() =>
    {
        var template = project.Template is { Length: > 0 } name
            ? ProjectTemplates.Load().FirstOrDefault(t => t.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) ?? throw new KeyNotFoundException(name)
            : null;
        var p = _projects.Create(project);
        if (template != null) lock (p) ProjectTemplates.Apply(template, p);
        return Dto.Project(p);
    }, ct);

    public Task<ProjectDto> GetProjectAsync(string project, CancellationToken ct = default) => With(project, Dto.Project, ct);

    public Task<ProjectDto> UpdateProjectAsync(string project, ProjectUpdateDto u, CancellationToken ct = default) => With(project, p =>
    {
        if (u.Topic != null) p.Topic = u.Topic;
        if (u.PretranslateMinScore is { } m) p.PretranslateMinScore = Math.Clamp(m, 50, 100);
        if (u.ConfirmContextMatches is { } c) p.ConfirmContextMatches = c;
        if (u.SourceProfile != null) p.SourceProfile = u.SourceProfile.Length == 0 ? null : u.SourceProfile;
        if (u.AutoLocalize is { } al) p.AutoLocalize = al;
        if (u.Client != null) p.Client = u.Client;
        p.Save();
        return Dto.Project(p);
    }, ct);

    public Task<ProjectDto> AttachResourceAsync(string project, ResourceRef resource, CancellationToken ct = default) =>
        With(project, p => { p.Attach(resource); return Dto.Project(p); }, ct);

    public Task<ProjectDto> DetachResourceAsync(string project, ResourceKind kind, string location, string? server = null, CancellationToken ct = default) =>
        With(project, p =>
        {
            var r = p.Resources.FirstOrDefault(x => x.Kind == kind && x.Location == location && x.Server == server) ?? throw new KeyNotFoundException(location);
            p.Detach(r);
            return Dto.Project(p);
        }, ct);

    // ---------- Dateien ----------

    public Task<IReadOnlyList<FileDto>> FilesAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<FileDto>>(project, p => p.Files.Select(f => Dto.File(p, f)).ToList(), ct);

    public Task<FileDto> AddFileAsync(string project, FileContent file, CancellationToken ct = default) =>
        With(project, p => Dto.File(p, p.AddFile(new MemoryStream(file.Content), Path.GetFileName(file.Name))), ct);

    public Task RemoveFileAsync(string project, string file, CancellationToken ct = default) =>
        With(project, p => { p.RemoveFile(FileOf(p, file)); return true; }, ct);

    public Task<IReadOnlyList<SegmentDto>> SegmentsAsync(string project, string file, string language, CancellationToken ct = default) =>
        With<IReadOnlyList<SegmentDto>>(project, p =>
        {
            var doc = p.LoadDocument(FileOf(p, file), language);
            return doc.Segments.Select(s => Dto.Segment(doc, s)).ToList();
        }, ct);

    public Task<FileContent> TargetFileAsync(string project, string file, string language, CancellationToken ct = default) => With(project, p =>
    {
        var f = FileOf(p, file);
        var ms = new MemoryStream();
        p.ExportTarget(f, p.LoadDocument(f, language), ms);
        return new FileContent(p.TargetFileName(f, language), ms.ToArray());
    }, ct);

    public Task<FileContent> XliffAsync(string project, string file, string language, CancellationToken ct = default) => With(project, p =>
    {
        var f = FileOf(p, file);
        var ms = new MemoryStream();
        XliffFilter.ExportBilingual(p.LoadDocument(f, language), ms);
        return new FileContent(Path.GetFileNameWithoutExtension(f.OriginalName) + "_" + language + ".xlf", ms.ToArray());
    }, ct);

    public Task<int> ReplaceAsync(string project, string file, string language, string find, string replace, bool matchCase = false, CancellationToken ct = default) =>
        With(project, p =>
        {
            var f = FileOf(p, file);
            CheckWrite(p, f.Id, language);
            var d = p.LoadDocument(f, language);
            var n = Workflow.ReplaceInTargets(d, find, replace, matchCase);
            p.SaveDocument(f, d);
            _o.DocumentChanged?.Invoke(p, f.Id, language);
            return n;
        }, ct);

    // ---------- Segmente ----------

    public Task<SegmentDto> GetSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) => Dto.Segment(d, s), false, ct);

    public Task<SegmentDto> UpdateSegmentAsync(string project, string file, string language, int id, SegmentUpdateDto u, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
        {
            if (u.Target != null)
            {
                s.Target = u.Target;
                s.Origin = TranslationOrigin.Manual;
                if (s.Status == SegmentStatus.NotTranslated) s.Status = SegmentStatus.Draft;
            }
            if (u.Status is { } st) s.Status = st;
            if (u.Comment != null) s.Comment = u.Comment.Length == 0 ? null : u.Comment;
            s.Modified = DateTime.UtcNow;
            return Dto.Segment(d, s);
        }, true, ct);

    public Task<ConfirmResult> ConfirmSegmentAsync(string project, string file, string language, int id, SegmentStatus? status = null, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
        {
            var propagated = Workflow.Confirm(p, d, s, status ?? SegmentStatus.Translated);
            return new ConfirmResult(Dto.Segment(d, s), propagated);
        }, true, ct);

    public Task<IReadOnlyList<TmMatch>> MatchesAsync(string project, string file, string language, int id, int? minScore = null, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<TmMatch>>(project, file, language, id, (p, f, d, s) => Workflow.Lookup(p, d, s, minScore), false, ct);

    public Task<IReadOnlyList<TermHit>> SegmentTermsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<TermHit>>(project, file, language, id, (p, f, d, s) => Workflow.Terms(p, d, s), false, ct);

    public Task<IReadOnlyList<FragmentMatch>> FragmentsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<FragmentMatch>>(project, file, language, id, (p, f, d, s) => Workflow.Fragments(p, d, s), false, ct);

    public Task<IReadOnlyList<KnowledgeEntry>> SegmentKnowledgeAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<KnowledgeEntry>>(project, file, language, id, (p, f, d, s) => Workflow.Knowledge(p, d, s), false, ct);

    public Task<IReadOnlyList<string>> SuggestionsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<string>>(project, file, language, id, (p, f, d, s) => Workflow.Suggestions(p, d, s, Workflow.Lookup(p, d, s)), false, ct);

    public Task<SegmentDto> ApplyMatchAsync(string project, string file, string language, int id, int number = 1, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
        {
            var m = Workflow.Lookup(p, d, s).ElementAtOrDefault(number - 1) ?? throw new KeyNotFoundException($"Treffer {number}");
            Workflow.ApplyMatch(s, m, d.TargetLanguage);
            return Dto.Segment(d, s);
        }, true, ct);

    public Task<SegmentDto> CopySourceAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) => { Workflow.CopySourceToTarget(s); return Dto.Segment(d, s); }, true, ct);

    public Task<IReadOnlyList<SegmentDto>> SplitSegmentAsync(string project, string file, string language, int id, int sourceIndex, CancellationToken ct = default) =>
        WithSegment<IReadOnlyList<SegmentDto>>(project, file, language, id, (p, f, d, s) =>
            Workflow.Split(d, s, sourceIndex) is { } second
                ? [Dto.Segment(d, s), Dto.Segment(d, second)]
                : throw new ArgumentException("Hier kann nicht geteilt werden."), true, ct);

    public Task<SegmentDto> MergeSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
            Workflow.MergeWithNext(d, s) ? Dto.Segment(d, s) : throw new ArgumentException("Zusammenführen nicht möglich."), true, ct);

    // ---------- Stapelaufgaben und Qualität ----------

    public Task<IReadOnlyList<PretranslateResult>> PretranslateAsync(string project, PretranslateDto? options = null, CancellationToken ct = default) =>
        With<IReadOnlyList<PretranslateResult>>(project, p =>
        {
            var r = options ?? new PretranslateDto();
            var results = new List<PretranslateResult>();
            foreach (var (f, lang) in p.Documents())
            {
                ct.ThrowIfCancellationRequested();
                if (!MayWrite(p, f.Id, lang)) continue;                 // Team: nicht an der Reihe
                var d = p.LoadDocument(f, lang);
                var (applied, confirmed) = Workflow.Pretranslate(p, d, r.ConfirmExact, r.Overwrite);
                p.SaveDocument(f, d);
                _o.DocumentChanged?.Invoke(p, f.Id, lang);
                results.Add(new PretranslateResult(f.OriginalName, lang, applied, confirmed));
            }
            return results;
        }, ct);

    public Task<IReadOnlyList<AnalysisRowDto>> AnalysisAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<AnalysisRowDto>>(project, p => p.TargetLanguages.SelectMany(lang =>
        {
            var r = Analyzer.Analyze(p.AllDocuments(lang), p.TmsFor(lang));
            return r.Bands.Where(b => b.Value.Segments > 0)
                .Select(b => new AnalysisRowDto(lang, AnalysisResult.BandName(b.Key), b.Value.Segments, b.Value.Words, b.Value.Characters));
        }).ToList(), ct);

    public Task<IReadOnlyList<QaIssueDto>> QaAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<QaIssueDto>>(project, p => p.Documents().SelectMany(x =>
            QaChecker.Check(p.LoadDocument(x.File, x.Language), p.Qa, p.ProjectTermbase, brief: p.Brief(x.File, x.Language))
                .Select(i => new QaIssueDto(x.File.OriginalName, x.Language, i.SegmentId, i.Severity.ToString(), i.Check, i.Message))).ToList(), ct);

    public Task<QaSettings> SetQaSettingsAsync(string project, QaSettings settings, CancellationToken ct = default) =>
        With(project, p => { p.Qa = settings; p.Save(); return p.Qa; }, ct);

    // ---------- Translation Memory ----------

    public Task<IReadOnlyList<TmMatch>> SearchTmAsync(string project, string source, string sourceLanguage, string targetLanguage,
        int? minScore = null, string? previous = null, string? next = null, CancellationToken ct = default) =>
        With<IReadOnlyList<TmMatch>>(project, p => p.TmsFor(targetLanguage)
            .SelectMany(tm => tm.Search(sourceLanguage, targetLanguage, source, minScore ?? 70, 10, previous == null ? null : new TmContext(previous, next, null)))
            .OrderByDescending(m => m.Score).Take(10).ToList(), ct);

    public Task<IReadOnlyList<TmUnit>> ConcordanceAsync(string project, string text, string sourceLanguage, string targetLanguage, bool inTarget = false, CancellationToken ct = default) =>
        With<IReadOnlyList<TmUnit>>(project, p => p.TmsFor(targetLanguage)
            .SelectMany(tm => tm.Concordance(sourceLanguage, targetLanguage, text, inTarget, 30)).ToList(), ct);

    public Task AddToTmAsync(string project, TmAddDto u, CancellationToken ct = default) => With(project, p =>
    {
        var (src, tgt) = p.MasksTm ? Privacy.PrivacyScanner.MaskPair(u.Source, u.Target, p.Privacy) : (u.Source, u.Target);
        p.ProjectTm.Add(u.SourceLanguage, u.TargetLanguage, src, tgt, User ?? "API", u.Structure, null,
            p.MasksTm && u.Previous != null ? Privacy.TmPrivacy.Mask(u.Previous, p.Privacy) : u.Previous,
            p.MasksTm && u.Next != null ? Privacy.TmPrivacy.Mask(u.Next, p.Privacy) : u.Next);
        return true;
    }, ct);

    public Task<IReadOnlyList<TmUnit>> TmUnitsAsync(string project, string? filter = null, int offset = 0, int limit = 50, CancellationToken ct = default) =>
        With<IReadOnlyList<TmUnit>>(project, p => p.ProjectTm.Browse(filter, Math.Max(0, offset), Math.Clamp(limit, 1, 1000)), ct);

    public Task UpdateTmUnitAsync(string project, long id, string source, string target, CancellationToken ct = default) =>
        With(project, p => { p.ProjectTm.Update(id, source, target); return true; }, ct);

    public Task DeleteTmUnitAsync(string project, long id, CancellationToken ct = default) =>
        With(project, p => { p.ProjectTm.Delete(id); return true; }, ct);

    public Task<int> ImportTmAsync(string project, FileContent file, CancellationToken ct = default) => With(project, p =>
    {
        if (!file.Name.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase))
            return TmxIo.Import(new MemoryStream(file.Content), p.ProjectTm).Imported;
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdltm");
        File.WriteAllBytes(tmp, file.Content);
        try { return SdltmImporter.Import(tmp, p.ProjectTm); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(tmp);
        }
    }, ct);

    public Task<FileContent> ExportTmAsync(string project, CancellationToken ct = default) => With(project, p =>
    {
        var ms = new MemoryStream();
        TmxIo.Export(p.ProjectTm, ms);
        return new FileContent(p.Name + ".tmx", ms.ToArray());
    }, ct);

    // ---------- Terminologie ----------

    public Task<IReadOnlyList<Concept>> SearchTermsAsync(string project, string? query = null, string? language = null, CancellationToken ct = default) =>
        With<IReadOnlyList<Concept>>(project, p => string.IsNullOrWhiteSpace(query)
            ? p.ProjectTermbase.AllConcepts().ToList()
            : p.Termbases.SelectMany(tb => tb.Lookup(language ?? p.SourceLanguage, query)).DistinctBy(c => (c.Id, c.Terms.Count)).ToList(), ct);

    public Task<long> AddConceptAsync(string project, ConceptDto c, CancellationToken ct = default) =>
        With(project, p => p.ProjectTermbase.AddConcept(c.Terms.Select(t => (t.Lang, t.Text, t.Status)), c.Definition, c.Domain, c.Source), ct);

    public Task DeleteConceptAsync(string project, long id, CancellationToken ct = default) =>
        With(project, p => { p.ProjectTermbase.DeleteConcept(id); return true; }, ct);

    public Task<int> ImportTermsAsync(string project, FileContent file, CancellationToken ct = default) =>
        With(project, p => TbxIo.Import(new MemoryStream(file.Content), p.ProjectTermbase), ct);

    public Task<FileContent> ExportTermsAsync(string project, CancellationToken ct = default) => With(project, p =>
    {
        var ms = new MemoryStream();
        TbxIo.Export(p.ProjectTermbase, ms);
        return new FileContent(p.Name + ".tbx", ms.ToArray());
    }, ct);

    // ---------- Wissen ----------

    public Task<IReadOnlyList<KnowledgeEntry>> SearchKnowledgeAsync(string project, string? query = null, string? language = null, CancellationToken ct = default) =>
        With<IReadOnlyList<KnowledgeEntry>>(project, p => string.IsNullOrWhiteSpace(query)
            ? p.KnowledgeBases.SelectMany(k => k.Browse(language, null, 0, 200)).ToList()
            : p.KnowledgeBases.SelectMany(k => k.Search(language, query, 20)).ToList(), ct);

    public Task<long> AddKnowledgeAsync(string project, KnowledgeEntry entry, CancellationToken ct = default) =>
        With(project, p => p.ProjectKnowledge.Add(entry with { Author = entry.Author ?? User }), ct);

    public Task UpdateKnowledgeAsync(string project, long id, KnowledgeEntry entry, CancellationToken ct = default) =>
        With(project, p => { p.ProjectKnowledge.Update(entry with { Id = id }); return true; }, ct);

    public Task DeleteKnowledgeAsync(string project, long id, CancellationToken ct = default) =>
        With(project, p => { p.ProjectKnowledge.Delete(id); return true; }, ct);

    // ---------- Pakete, Alignment, Texterkennung ----------

    public Task<ProjectDto> ImportPackageAsync(FileContent package, string? topic = null, string? name = null, CancellationToken ct = default) => Task.Run(() =>
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdlppx");
        File.WriteAllBytes(tmp, package.Content);
        try
        {
            // Der Name im Paket ist fremde Eingabe: nur als bereinigter Ordnername unter dem Projektordner verwenden
            var folderName = ProjectCache.SafeName(name ?? ProjectPackage.Inspect(tmp).Name);
            var folder = Path.Combine(_o.ProjectsRoot, folderName);
            if (Directory.Exists(folder)) throw new ArgumentException($"Ein Projekt „{folderName}“ gibt es schon (anderen Namen angeben).");
            ProjectPackage.Import(tmp, folder, topic ?? "").Dispose();
            return Dto.Project(_projects.Get(folderName));
        }
        finally { File.Delete(tmp); }
    }, ct);

    public Task<FileContent> ReturnPackageAsync(string project, CancellationToken ct = default) => With(project, p =>
    {
        var ms = new MemoryStream();
        ProjectPackage.ExportReturnPackage(p, ms);
        return new FileContent(p.Name + ".sdlrpx", ms.ToArray());
    }, ct);

    public Task<IReadOnlyList<AlignedPair>> AlignAsync(AlignRequest r, CancellationToken ct = default) => Task.Run<IReadOnlyList<AlignedPair>>(() =>
    {
        List<string> Sentences(FileContent f, string lang)
        {
            var filter = FileFilters.ForFile(f.Name) ?? throw new NotSupportedException($"Dateityp {Path.GetExtension(f.Name)} wird nicht unterstützt.");
            var d = filter.Read(new MemoryStream(f.Content), f.Name, lang, lang, new Segmenter(lang));
            return d.Segments.Where(s => !s.Locked).Select(s => s.Source).ToList();
        }
        var pairs = Aligner.Align(Sentences(r.Source, r.SourceLanguage), Sentences(r.Target, r.TargetLanguage));
        if (r.ImportIntoProject is { } name)
        {
            var p = _projects.Get(name);
            lock (p)
                foreach (var pair in pairs.Where(x => x.Accepted))
                {
                    var (src, tgt) = p.MasksTm ? Privacy.PrivacyScanner.MaskPair(pair.SourceText, pair.TargetText, p.Privacy) : (pair.SourceText, pair.TargetText);
                    p.ProjectTm.Add(r.SourceLanguage, r.TargetLanguage, src, tgt, "Alignment");
                }
        }
        return pairs;
    }, ct);

    public async Task<FileDto> RecognizePdfAsync(string project, FileContent pdf, CancellationToken ct = default)
    {
        var p = _projects.Get(project);
        var claude = _o.Ai().Providers.FirstOrDefault(x => x.Kind == AiProviderKind.Anthropic)
                     ?? throw new InvalidOperationException("Für die Texterkennung wird ein Claude-Zugang benötigt.");
        var text = await new ClaudeChatModel(claude).ExtractPdfTextAsync(pdf.Content, ct);
        var docx = new MemoryStream();
        PdfFilter.OcrTextToDocx(text, p.SourceLanguage, docx);
        docx.Position = 0;
        return await With(project, x => Dto.File(x, x.AddFile(docx, Path.GetFileNameWithoutExtension(pdf.Name) + "_erkannt.docx")), ct);
    }

    // ---------- Agenten ----------

    public async Task<TranslationResult> TranslateAsync(TranslateRequest r, CancellationToken ct = default)
    {
        var ai = _o.Ai();
        var ctx = AgentContext.From(ai, r.ProviderId ?? ai.TranslateProviderId);
        if (r.Project != null && r.File != null && r.Language != null && r.SegmentId is { } id)
        {
            Projects.DocumentBrief? brief = null;
            var (p, d, s, m, t, k, rs, prev, next) = await With(r.Project, project =>
            {
                var file = FileOf(project, r.File);
                brief = project.Brief(file, r.Language);                    // Steckbrief, falls im Editor erstellt
                var doc = project.LoadDocument(file, r.Language);
                var all = doc.Segments.ToList();
                var seg = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Segment {id}");
                var i = all.IndexOf(seg);
                return (project, doc, seg, Workflow.Lookup(project, doc, seg), Workflow.Terms(project, doc, seg), Workflow.Knowledge(project, doc, seg),
                    Workflow.Research(project, doc, seg), i > 0 ? all[i - 1].Source : null, i < all.Count - 1 ? all[i + 1].Source : null);
            }, ct);
            return new TranslationResult(await new AiTranslator(ctx).TranslateAsync(s.Source, d.SourceLanguage, d.TargetLanguage, p.Topic, m, t, prev, next,
                ct: ct, knowledge: k, brief: brief, research: rs));
        }
        if (r.Text == null || r.SourceLanguage == null || r.TargetLanguage == null)
            throw new ArgumentException("Text und Sprachen angeben (oder Projekt, Datei, Sprache und Segment).");
        return new TranslationResult(await new AiTranslator(ctx).TranslateAsync(r.Text, r.SourceLanguage, r.TargetLanguage, r.Topic, ct: ct));
    }

    public async Task<EvaluationResult> EvaluateAsync(EvaluateRequest r, CancellationToken ct = default)
    {
        var ctx = AgentContext.From(_o.Ai(), _o.Ai().EvaluateProviderId);
        List<TermHit>? terms = r.Project == null ? null : await With(r.Project, p =>
            p.TermbasesFor(r.TargetLanguage).SelectMany(tb => tb.Recognize(r.Source, r.SourceLanguage, r.TargetLanguage)).ToList(), ct);
        return await new EvaluationAgent(ctx).RunAsync(r.Source, r.SourceLanguage, r.TargetLanguage, r.Candidates, r.Topic, terms, r.CheckUsage && ctx.Search != null, ct);
    }

    public Task<UsageResult> CheckUsageAsync(UsageRequest r, CancellationToken ct = default) =>
        new UsageCheckAgent(AgentContext.From(_o.Ai(), _o.Ai().ResearchProviderId))
            .RunAsync(r.Expression, r.Language, r.Topic, r.Alternatives, r.Alternatives is not { Count: > 0 }, ct);

    public async Task<ResearchResult> ResearchAsync(ResearchRequest r, CancellationToken ct = default)
    {
        var ai = _o.Ai();
        var result = await new TermResearchAgent(AgentContext.From(ai, ai.ResearchProviderId))
            .RunAsync(r.Topic, r.SourceLanguage, r.TargetLanguages, r.SeedTerms, r.PagesPerLanguage, ct, ai.Profile(r.SourceProfile));
        if (r.SaveToProject != null)
            await With(r.SaveToProject, p =>
            {
                foreach (var c in result.Candidates.Where(c => c.Confidence >= 0.6))
                    p.ProjectTermbase.AddConcept(c.Terms.Select(kv => (kv.Key, kv.Value, TermStatus.Preferred)), c.Definition, r.Topic);
                return true;
            }, ct);
        return result;
    }

    public async Task<KnowledgeRunResult> FillKnowledgeAsync(KnowledgeAgentRequest r, CancellationToken ct = default)
    {
        var p = _projects.Get(r.Project);
        var kb = r.KnowledgeBase == null
            ? p.ProjectKnowledge
            : p.KnowledgeBases.FirstOrDefault(k => k.Name == r.KnowledgeBase) ?? throw new KeyNotFoundException(r.KnowledgeBase);
        var ai = _o.Ai();
        return await new KnowledgeAgent(AgentContext.From(ai, ai.ResearchProviderId))
            .RunAsync(r.Topic, r.Languages, kb, r.Focus, r.PagesPerLanguage, User, ct, ai.Profile(r.SourceProfile ?? p.SourceProfile));
    }

    public async Task<TermComparisonResult> CompareTermAsync(CompareRequest r, CancellationToken ct = default)
    {
        var p = r.Project == null ? null : _projects.Get(r.Project);
        var ai = _o.Ai();
        var profile = ai.Profile(r.SourceProfile ?? p?.SourceProfile);
        List<TermHit>? known = p == null ? null : await With(r.Project!, x =>
            x.TermbasesFor(r.TargetLanguage).SelectMany(tb => tb.Recognize(r.Term, r.SourceLanguage, r.TargetLanguage)).ToList(), ct);
        var result = await new TermComparisonAgent(AgentContext.From(ai, ai.ResearchProviderId))
            .RunAsync(new TermQuery(r.Term, r.SourceLanguage, r.TargetLanguage, r.Context, r.Topic ?? p?.Topic, profile, r.Candidates, known), 3, ct);
        if (p != null && (r.SaveToTermbase || r.SaveToKnowledge))
            await With(r.Project!, x =>
            {
                if (r.SaveToTermbase) TermComparisonAgent.SaveToTermbase(result, x.ProjectTermbase, r.SourceLanguage, r.TargetLanguage, r.Topic ?? x.Topic);
                if (r.SaveToKnowledge) TermComparisonAgent.SaveToKnowledge(result, x.ProjectKnowledge, r.TargetLanguage, r.Topic ?? x.Topic, User);
                return true;
            }, ct);
        return result;
    }

    // ---------- Erweiterungen ----------

    public Task<IReadOnlyList<ExtensionInfo>> ExtensionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExtensionInfo>>((_o.Extensions?.Extensions ?? []).Select(e => new ExtensionInfo(
            e.Manifest.Id, e.Manifest.Name, e.Manifest.Version, e.Manifest.Author, e.Manifest.Description, e.Manifest.Enabled, e.Loaded, e.Error,
            e.Commands.Select(c => new ExtensionCommandInfo(c.Id, c.Name, c.Description, c.Scope.ToString())).ToList(),
            e.Contributions.ToList())).ToList());

    public async Task<ExtensionResult> RunCommandAsync(string command, ExtensionRunDto r, CancellationToken ct = default)
    {
        var cmd = _o.Extensions?.FindCommand(command) ?? throw new KeyNotFoundException(command);
        TranslationProject? p = null;
        ProjectFile? f = null;
        BilingualDocument? d = null;
        if (r.Project != null)
            (p, f, d) = await With(r.Project, x =>
            {
                var file = r.File != null ? FileOf(x, r.File) : null;
                return (x, file, file != null && r.Language != null ? x.LoadDocument(file, r.Language) : null);
            }, ct);
        var ai = _o.Ai();
        IChatModel? model = null;
        ISearchProvider? search = null;
        try
        {
            model = ai.Get(ai.TranslateProviderId) is { } provider ? ChatModelFactory.Create(provider) : null;
            search = Privacy.PrivacyGuard.Wrap(SearchProviderFactory.Create(ai));
        }
        catch (Exception ex) when (ex is AiException or ArgumentException) { }
        var context = new ExtensionContext
        {
            Project = p, File = f, Document = d,
            Segment = d != null && r.SegmentId is { } id ? d.Segments.FirstOrDefault(x => x.Id == id) : null,
            Arguments = r.Arguments ?? new(),
            ChatModel = model, Search = search, Api = this,
        };
        var result = await cmd.RunAsync(context, ct);
        if (result.DocumentChanged && p != null && f != null && d != null)
            lock (p)
            {
                p.SaveDocument(f, d);
                _o.DocumentChanged?.Invoke(p, f.Id, d.TargetLanguage);
            }
        return result;
    }

    // ---------- Lektorat und Fehlerbewertung ----------

    public Task<FileContent> ReviewFileAsync(string project, string file, string language, CancellationToken ct = default) => With(project, p =>
    {
        var f = FileOf(p, file);
        var ms = new MemoryStream();
        ReviewDocx.Export(p.LoadDocument(f, language), ms);
        return new FileContent(Path.GetFileNameWithoutExtension(f.OriginalName) + "_" + language + "_Pruefung.docx", ms.ToArray());
    }, ct);

    public Task<ReviewImportResult> ImportReviewAsync(string project, string file, string language, ReviewImportDto review, CancellationToken ct = default) =>
        With(project, p =>
        {
            var f = FileOf(p, file);
            CheckWrite(p, f.Id, language);
            var d = p.LoadDocument(f, language);
            var result = ReviewDocx.Import(new MemoryStream(review.File.Content), d, p, markReviewed: true, markUnchangedReviewed: review.MarkUnchangedReviewed);
            p.SaveDocument(f, d);
            _o.DocumentChanged?.Invoke(p, f.Id, language);
            return result;
        }, ct);

    public Task<SegmentDto> AcceptChangeAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) => { TrackChanges.Accept(s); return Dto.Segment(d, s); }, true, ct);

    public Task<SegmentDto> RejectChangeAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) => { TrackChanges.Reject(s); return Dto.Segment(d, s); }, true, ct);

    public Task<SegmentDto> AddErrorAsync(string project, string file, string language, int id, LqaErrorDto error, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
        {
            if (string.IsNullOrWhiteSpace(error.Category)) throw new ArgumentException("Kategorie fehlt.");
            (s.Errors ??= new()).Add(new LqaError(error.Category.Trim(), error.Severity, error.Note?.Trim() ?? "", User ?? Environment.UserName, DateTime.UtcNow));
            return Dto.Segment(d, s);
        }, true, ct);

    public Task<SegmentDto> RemoveErrorAsync(string project, string file, string language, int id, int index, CancellationToken ct = default) =>
        WithSegment(project, file, language, id, (p, f, d, s) =>
        {
            if (s.Errors == null || index < 0 || index >= s.Errors.Count) throw new KeyNotFoundException($"Fehler {index}");
            s.Errors.RemoveAt(index);
            if (s.Errors.Count == 0) s.Errors = null;
            return Dto.Segment(d, s);
        }, true, ct);

    public Task<LqaReport> EvaluationAsync(string project, string? file = null, string? language = null, CancellationToken ct = default) => With(project, p =>
    {
        var docs = file != null
            ? (language != null ? [language] : p.LanguagesOf(FileOf(p, file))).Select(l => p.LoadDocument(FileOf(p, file), l)).ToList()
            : p.Documents().Where(x => language == null || x.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                .Select(x => p.LoadDocument(x.File, x.Language)).ToList();
        return Lqa.Evaluate(docs, p.Lqa);
    }, ct);

    public Task<FileContent> EvaluationReportAsync(string project, string file, string language, CancellationToken ct = default) => With(project, p =>
    {
        var f = FileOf(p, file);
        var ms = new MemoryStream();
        Lqa.ExportDocx($"{f.OriginalName} ({language})", [p.LoadDocument(f, language)], p.Lqa, ms);
        return new FileContent(Path.GetFileNameWithoutExtension(f.OriginalName) + "_" + language + "_Bewertung.docx", ms.ToArray());
    }, ct);

    public Task<LqaSettings> SetLqaSettingsAsync(string project, LqaSettings settings, CancellationToken ct = default) =>
        With(project, p => { p.Lqa = settings; p.Save(); return p.Lqa; }, ct);

    // ---------- Zahlen, Pseudoübersetzung, Angebot ----------

    public Task<int> LocalizeNumbersAsync(string project, CancellationToken ct = default) => With(project, p =>
    {
        var changed = 0;
        foreach (var (f, lang) in p.Documents())
        {
            ct.ThrowIfCancellationRequested();
            if (!MayWrite(p, f.Id, lang)) continue;
            var d = p.LoadDocument(f, lang);
            var before = changed;
            foreach (var s in d.Segments.Where(s => !s.Locked && s.Target.Length > 0))
            {
                var localized = AutoLocalizer.Localize(s.Source, s.Target, d.SourceLanguage, d.TargetLanguage);
                if (localized == s.Target) continue;
                s.Target = localized;
                s.Modified = DateTime.UtcNow;
                changed++;
            }
            if (changed == before) continue;
            p.SaveDocument(f, d);
            _o.DocumentChanged?.Invoke(p, f.Id, lang);
        }
        return changed;
    }, ct);

    public Task<FileContent> PseudoTranslationAsync(string project, string file, string language, CancellationToken ct = default) => With(project, p =>
    {
        var f = FileOf(p, file);
        var d = p.LoadDocument(f, language);
        foreach (var s in d.Segments.Where(s => !s.Locked)) s.Target = PseudoTranslator.Translate(s.Source);
        var ms = new MemoryStream();
        p.ExportTarget(f, d, ms);                                      // nur die Zieldatei, das Projekt bleibt unverändert
        var target = p.TargetFileName(f, language);
        return new FileContent(Path.GetFileNameWithoutExtension(target) + "_pseudo" + Path.GetExtension(target), ms.ToArray());
    }, ct);

    private static QuoteDto Quote(TranslationProject p)
    {
        var quotes = p.TargetLanguages.Select(l => QuoteCalculator.Calculate(Analyzer.Analyze(p.AllDocuments(l), p.TmsFor(l)), p.Rates, l)).ToList();
        var net = quotes.Sum(q => q.Net);
        var vat = Math.Round(net * p.Rates.VatPercent / 100m, 2);
        return new QuoteDto(quotes, net, vat, net + vat, p.Rates.Currency, p.Rates.Unit);
    }

    public Task<QuoteDto> QuoteAsync(string project, RateSettings? rates = null, CancellationToken ct = default) => With(project, p =>
    {
        if (rates != null)
        {
            p.Rates = rates;
            p.Save();
        }
        return Quote(p);
    }, ct);

    public Task<FileContent> QuoteDocumentAsync(string project, CancellationToken ct = default) => With(project, p =>
    {
        var ms = new MemoryStream();
        QuoteCalculator.ExportDocx(p.Name, p.SourceLanguage, Quote(p).Quotes, p.Rates, ms);
        return new FileContent(p.Name + "_Angebot.docx", ms.ToArray());
    }, ct);

    // ---------- Vorlagen und QA-Profile ----------

    public Task<IReadOnlyList<ProjectTemplate>> TemplatesAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<ProjectTemplate>>(ProjectTemplates.Load, ct);

    public Task<ProjectTemplate> SaveTemplateAsync(string project, string name, CancellationToken ct = default) =>
        With(project, p => ProjectTemplates.SaveFrom(p, name.Trim().Length > 0 ? name : throw new ArgumentException("Name fehlt.")), ct);

    public Task<IReadOnlyList<QaProfile>> QaProfilesAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<QaProfile>>(QaProfiles.Load, ct);

    public Task<QaProfile> SaveQaProfileAsync(string project, string name, CancellationToken ct = default) => With(project, p =>
    {
        if (name.Trim().Length == 0) throw new ArgumentException("Name fehlt.");
        QaProfiles.Save(name, p.Qa);
        return QaProfiles.Load().First(x => x.Name == name.Trim());
    }, ct);

    public Task<QaSettings> ApplyQaProfileAsync(string project, string name, CancellationToken ct = default) => With(project, p =>
    {
        var profile = QaProfiles.Load().FirstOrDefault(x => x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)) ?? throw new KeyNotFoundException(name);
        p.Qa = QaProfiles.CloneSettings(profile.Settings);
        p.Save();
        return p.Qa;
    }, ct);

    // ---------- TM-Pflege, Fachbegriffe, alte Übersetzungen ----------

    public Task<TmReplaceResult> ReplaceInTmAsync(string project, TmReplaceDto r, CancellationToken ct = default) => With(project, p =>
    {
        if (r.Find.Length == 0) throw new ArgumentException("Suchtext fehlt.");
        var changes = TmMaintenance.Preview(p.ProjectTm, r.Find, r.Replace, r.InSource, r.InTarget, r.MatchCase, r.Regex, r.WholeWords);
        if (!r.Preview) foreach (var c in changes) p.ProjectTm.Update(c.Unit.Id, c.Source, c.Target);
        return new TmReplaceResult(changes.Count, changes.Take(200).ToList());
    }, ct);

    public Task<TmCleanResult> CleanTmAsync(string project, TmCleanDto r, CancellationToken ct = default) =>
        With(project, p => TmMaintenance.Clean(p.ProjectTm, r.Duplicates, r.EmptyTargets, r.Identical), ct);

    public Task<IReadOnlyList<InconsistentDto>> InconsistentTmAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<InconsistentDto>>(project, p => TmMaintenance.Inconsistent(p.ProjectTm).Select(x => new InconsistentDto(x.Source, x.Units)).ToList(), ct);

    /// <summary>Ein Modell für Aufgaben, die ein Sprachmodell brauchen (bei einem MÜ-Dienst fürs Übersetzen: das der Recherche).</summary>
    private IChatModel LanguageModel()
    {
        var ai = _o.Ai();
        var provider = ai.Get(ai.TranslateProviderId) is { Kind: not AiProviderKind.MachineTranslation } t ? t : ai.Get(ai.ResearchProviderId);
        return provider == null ? throw new AiException("Kein KI-Zugang eingerichtet (Seite „KI einrichten“).") : ChatModelFactory.Create(provider);
    }

    public async Task<IReadOnlyList<TermSuggestion>> ExtractTermsAsync(string project, ExtractTermsDto r, CancellationToken ct = default)
    {
        var (sentences, candidates, p) = await With(project, p =>
        {
            var lang = p.TargetLanguages.FirstOrDefault(l => l.Equals(r.Language, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException(r.Language);
            var sentences = p.Files.Where(f => p.LanguagesOf(f).Contains(lang)).SelectMany(f => p.LoadDocument(f, lang).Segments)
                .Where(s => !s.Locked).Select(s => s.Source).ToList();
            var termbases = p.TermbasesFor(lang);
            bool Known(string term) => termbases.Any(tb => tb.Lookup(p.SourceLanguage, term, 3)
                .Any(c => c.Terms.Any(t => Languages.Matches(t.Language, p.SourceLanguage) && string.Equals(t.Text, term, StringComparison.CurrentCultureIgnoreCase))));
            return (sentences, TermExtractor.Candidates(sentences, p.SourceLanguage, Math.Clamp(r.Max, 10, 300), Known), p);
        }, ct);
        if (!r.UseAi) return candidates.Select(c => new TermSuggestion(c.Term, "", c.Count, c.Example)).ToList();
        return await TermExtractor.RefineAsync(LanguageModel(), candidates, sentences, p.SourceLanguage, r.Language, p.Topic, ct);
    }

    public async Task<ImportTranslationsResult> ImportTranslationsAsync(string project, ImportTranslationsDto r, CancellationToken ct = default)
    {
        var p = _projects.Get(project);
        var source = r.SourceLanguage ?? p.SourceLanguage;
        var targets = r.TargetLanguages is { Count: > 0 } t ? t : p.TargetLanguages;
        var pairing = TranslatedFiles.Pair(r.Files.Select(f => new ImportFile(f.Name, f.Content)).ToList(), source, targets);
        IChatModel? model = null;
        if (r.UseAi || r.SuggestTerms) model = LanguageModel();
        var results = new List<ImportedPairDto>();
        var accepted = new List<(string Src, string Tgt, string SrcLang, string TgtLang)>();
        foreach (var pair in pairing.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            var session = TranslatedFiles.Align(pair);
            if (r.UseAi && model != null && !pair.IsBilingual) await new AiAligner(model).AutoAsync(session, ct);
            string tmName;
            int imported;
            lock (p)
            {
                if (!r.Shared)
                {
                    tmName = "Projekt-TM";
                    imported = TranslatedFiles.ImportToTm(session, p.ProjectTm, pair.Source.Name, r.KeepTags, p.Privacy);
                }
                else
                {
                    var (name, path, _) = TranslatedFiles.SharedDatabase(ResourceKind.Tm, session.SourceLanguage, session.TargetLanguage, p);
                    tmName = name;
                    if (p.Tms.FirstOrDefault(x => !x.IsRemote && string.Equals(Path.GetFullPath(x.Location), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } open)
                        imported = TranslatedFiles.ImportToTm(session, open, pair.Source.Name, r.KeepTags, p.Privacy);
                    else
                        using (var tm = new TranslationMemory(path)) imported = TranslatedFiles.ImportToTm(session, tm, pair.Source.Name, r.KeepTags, p.Privacy);
                }
            }
            accepted.AddRange(session.Accepted.Select(a => (a.SourceText, a.TargetText, session.SourceLanguage, session.TargetLanguage)));
            results.Add(new ImportedPairDto(pair.Source.Name, pair.Target?.Name, pair.SourceLanguage, pair.TargetLanguage, pair.How, imported,
                session.Pairs.Count(x => !(x.Accepted && x.IsComplete)), tmName));
        }
        var terms = new List<TermSuggestion>();
        if (r.SuggestTerms && model != null)
            foreach (var g in accepted.GroupBy(a => (a.SrcLang, a.TgtLang)))
                terms.AddRange(await new TermSuggester(model).SuggestAsync(g.Select(a => (a.Src, a.Tgt)).ToList(), g.Key.SrcLang, g.Key.TgtLang, p.Topic, ct: ct));
        return new ImportTranslationsResult(results, pairing.Unpaired.Select(f => f.Name).ToList(), terms);
    }

    // ---------- Datenschutz ----------

    public Task<PrivacySettings> SetPrivacyAsync(string project, PrivacySettings settings, CancellationToken ct = default) =>
        With(project, p => { p.Privacy = settings; p.Save(); return p.Privacy; }, ct);

    public Task<IReadOnlyList<DetectedDto>> DetectSensitiveAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<DetectedDto>>(project, p =>
        {
            var texts = p.Documents().SelectMany(d => p.LoadDocument(d.File, d.Language).Segments).SelectMany(s => new[] { s.Source, s.Target })
                .Where(t => t.Length > 0).Distinct();
            var settings = p.Privacy.Clone();
            settings.Enabled = true;
            return PrivacyScanner.Detect(texts, settings).Select(x => new DetectedDto(x.Kind, x.Value, x.Count)).ToList();
        }, ct);

    // ---------- Team: Arbeitsschritte ----------

    public Task<IReadOnlyList<FileStepDto>> WorkflowAsync(string project, CancellationToken ct = default) =>
        With<IReadOnlyList<FileStepDto>>(project, p => p.Files.SelectMany(f => p.LanguagesOf(f)
            .Select(l => new FileStepDto(f.Id, f.OriginalName, TeamWorkflow.StepOf(p, f.Id, l)))).ToList(), ct);

    public Task<FileStep> SetWorkflowAsync(string project, string file, string language, WorkflowUpdate update, CancellationToken ct = default) =>
        With(project, p => TeamWorkflow.Set(p, FileOf(p, file).Id, language, update, User ?? "", User == null || IsAdmin), ct);

    // ---------- Automatisierung, Sprachdaten, Diktat ----------

    public async Task<IReadOnlyList<WatchResult>> ProcessFolderAsync(string project, WatchRunDto r, CancellationToken ct = default)
    {
        if (User != null && !IsAdmin) throw new UnauthorizedAccessException("Eingangsordner verarbeitet nur die Verwaltung.");
        if (!Directory.Exists(r.Folder)) throw new KeyNotFoundException(r.Folder);
        var p = _projects.Get(project);
        var rule = new WatchRule
        {
            Folder = r.Folder, ProjectFolder = p.Folder, Pretranslate = r.Pretranslate, AiTranslate = r.AiTranslate,
            ExportTargets = r.ExportTargets, OutputFolder = r.OutputFolder ?? "",
        };
        AgentContext? ai = null;
        if (r.AiTranslate) ai = AgentContext.From(_o.Ai(), _o.Ai().TranslateProviderId);
        var results = new List<WatchResult>();
        foreach (var file in WatchProcessor.PendingFiles(rule, TimeSpan.FromSeconds(1)))
        {
            ct.ThrowIfCancellationRequested();
            // unter der Projektsperre, auf einem eigenen Thread (eine Sperre darf nicht über ein await reichen)
            results.Add(await Task.Run(() =>
            {
                lock (p) return WatchProcessor.ProcessAsync(rule, file, p, ai, ct: ct).GetAwaiter().GetResult();
            }, ct));
        }
        return results;
    }

    public Task<IReadOnlyList<LanguageDataSet>> LanguageDataAsync(string sourceLanguage, string targetLanguage, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<LanguageDataSet>>(async () => await LanguageData.SearchAsync(sourceLanguage, targetLanguage, ct), ct);

    public async Task<ProjectDto> InstallLanguageDataAsync(string project, LanguageDataInstallDto r, CancellationToken ct = default)
    {
        if (User != null && !IsAdmin) throw new UnauthorizedAccessException("Sprachdaten lädt nur die Verwaltung.");
        var set = (await LanguageData.SearchAsync(r.SourceLanguage, r.TargetLanguage, ct)).FirstOrDefault(d => d.Id == r.Id) ?? throw new KeyNotFoundException(r.Id);
        var result = await LanguageData.InstallAsync(set, r.SourceLanguage, r.TargetLanguage, LanguageData.DefaultFolder, ct: ct);
        return await With(project, p =>
        {
            var reference = new ResourceRef { Kind = set.Kind, Location = result.Path, WriteBack = false, Languages = [r.SourceLanguage, r.TargetLanguage] };
            if (r.ForLanguages) p.AttachForLanguages(reference);
            else p.Attach(reference);
            return Dto.Project(p);
        }, ct);
    }

    public Task<string> TranscribeAsync(TranscribeDto r, CancellationToken ct = default) =>
        Transcriber.TranscribeAsync(r.Audio.Content, r.Language, _o.Ai().Transcription, ct);

    // ---------- Hilfen ----------

    private static ProjectFile FileOf(TranslationProject p, string idOrName) =>
        p.Files.FirstOrDefault(f => f.Id == idOrName)
        ?? p.Files.FirstOrDefault(f => string.Equals(f.OriginalName, idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(idOrName);

    /// <summary>Arbeit an einem Projekt: im Hintergrund, unter der Projektsperre, nach dem Speichern offener Editor-Inhalte.</summary>
    private Task<T> With<T>(string project, Func<TranslationProject, T> action, CancellationToken ct) => Task.Run(() =>
    {
        var p = _projects.Get(project);
        lock (p)
        {
            _o.BeforeProjectAccess?.Invoke(p);
            return action(p);
        }
    }, ct);

    private Task<T> WithSegment<T>(string project, string file, string language, int id,
        Func<TranslationProject, ProjectFile, BilingualDocument, Segment, T> action, bool save, CancellationToken ct) => With(project, p =>
    {
        var f = FileOf(p, file);
        if (save) CheckWrite(p, f.Id, language);
        var d = p.LoadDocument(f, language);
        var s = d.Segments.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Segment {id}");
        var result = action(p, f, d, s);
        if (save)
        {
            p.SaveDocument(f, d);
            _o.DocumentChanged?.Invoke(p, f.Id, language);
        }
        return result;
    }, ct);
}
