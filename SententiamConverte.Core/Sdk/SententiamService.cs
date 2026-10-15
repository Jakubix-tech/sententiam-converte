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
    public const string Version = "2.0";

    private readonly SdkOptions _o;
    private readonly ProjectCache _projects;

    /// <param name="user">Wer arbeitet – erscheint als Autor neuer Wissenseinträge.</param>
    public SententiamService(SdkOptions options, string? user = null) : this(options, new ProjectCache(options), user) { }

    private SententiamService(SdkOptions options, ProjectCache projects, string? user)
    {
        _o = options;
        _projects = projects;
        User = user;
    }

    public string? User { get; }
    public ProjectCache Projects => _projects;

    /// <summary>Dieselben Projekte, aber im Namen einer anderen Person (z. B. je Anfrage an die REST-API).</summary>
    public SententiamService ForUser(string? user) => user == User ? this : new SententiamService(_o, _projects, user);

    // ---------- Allgemein ----------

    public Task<ApiInfo> InfoAsync(CancellationToken ct = default) => Task.FromResult(new ApiInfo("Sententiam converte", Version, "/openapi/v2.json",
        ["projects", "files", "segments", "pretranslate", "analysis", "qa", "tm", "terms", "knowledge", "align", "packages", "agents", "extensions"]));

    public Task<IReadOnlyList<FormatInfo>> FormatsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FormatInfo>>(FileFilters.All.Select(f => new FormatInfo(f.Id, f.Name, f.Extensions)).ToList());

    public Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct = default) => Task.FromResult(Languages.Common);

    public Task<IReadOnlyList<SourceProfile>> SourceProfilesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SourceProfile>>(_o.Ai().SourceProfiles);

    // ---------- Projekte ----------

    public Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() => _projects.Names().ToList(), ct);

    public Task<ProjectDto> CreateProjectAsync(ProjectCreateDto project, CancellationToken ct = default) =>
        Task.Run(() => Dto.Project(_projects.Create(project)), ct);

    public Task<ProjectDto> GetProjectAsync(string project, CancellationToken ct = default) => With(project, Dto.Project, ct);

    public Task<ProjectDto> UpdateProjectAsync(string project, ProjectUpdateDto u, CancellationToken ct = default) => With(project, p =>
    {
        if (u.Topic != null) p.Topic = u.Topic;
        if (u.PretranslateMinScore is { } m) p.PretranslateMinScore = Math.Clamp(m, 50, 100);
        if (u.ConfirmContextMatches is { } c) p.ConfirmContextMatches = c;
        if (u.SourceProfile != null) p.SourceProfile = u.SourceProfile.Length == 0 ? null : u.SourceProfile;
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
            QaChecker.Check(p.LoadDocument(x.File, x.Language), p.Qa, p.ProjectTermbase)
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
        p.ProjectTm.Add(u.SourceLanguage, u.TargetLanguage, u.Source, u.Target, User ?? "API", u.Structure, null, u.Previous, u.Next);
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
                    p.ProjectTm.Add(r.SourceLanguage, r.TargetLanguage, pair.SourceText, pair.TargetText, "Alignment");
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
            var (p, d, s, m, t, k, prev, next) = await With(r.Project, project =>
            {
                var doc = project.LoadDocument(FileOf(project, r.File), r.Language);
                var all = doc.Segments.ToList();
                var seg = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Segment {id}");
                var i = all.IndexOf(seg);
                return (project, doc, seg, Workflow.Lookup(project, doc, seg), Workflow.Terms(project, doc, seg), Workflow.Knowledge(project, doc, seg),
                    i > 0 ? all[i - 1].Source : null, i < all.Count - 1 ? all[i + 1].Source : null);
            }, ct);
            return new TranslationResult(await new AiTranslator(ctx).TranslateAsync(s.Source, d.SourceLanguage, d.TargetLanguage, p.Topic, m, t, prev, next,
                ct: ct, knowledge: k));
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
            search = SearchProviderFactory.Create(ai);
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
