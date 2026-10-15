using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SententiamConverte.Core.Agents;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Extensions;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Sdk;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Sdk;

/// <summary>
/// Client der REST-API (Teamserver oder lokale API der Desktop-App) mit derselben Schnittstelle wie intern.
/// <code>
/// using var sc = new SententiamClient("http://127.0.0.1:5290", Environment.GetEnvironmentVariable("SC_KEY")!);
/// var project = await sc.CreateProjectAsync(new ProjectCreateDto("Handbuch", "de-DE", ["en-GB"]));
/// await sc.AddFileAsync("Handbuch", FileContent.FromFile("anleitung.docx"));
/// await sc.PretranslateAsync("Handbuch");
/// </code>
/// Fehler kommen als dieselben Ausnahmen wie intern: <see cref="KeyNotFoundException"/>, <see cref="ArgumentException"/>,
/// <see cref="UnauthorizedAccessException"/>, <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class SententiamClient : ISententiam, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="baseUrl">z. B. <c>http://127.0.0.1:5290</c> (Desktop-App) oder <c>https://team.firma.de</c> (Teamserver).</param>
    /// <param name="token">Zugangsschlüssel.</param>
    /// <param name="http">Optional ein eigener HttpClient (z. B. mit Proxy); Zeitlimit für lange Agenten selbst setzen.</param>
    public SententiamClient(string baseUrl, string token, HttpClient? http = null)
    {
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v2/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---------- Allgemein ----------

    public Task<ApiInfo> InfoAsync(CancellationToken ct = default) => Get<ApiInfo>("", ct);
    public Task<IReadOnlyList<FormatInfo>> FormatsAsync(CancellationToken ct = default) => List<FormatInfo>("formats", ct);
    public Task<IReadOnlyList<Language>> LanguagesAsync(CancellationToken ct = default) => List<Language>("languages", ct);
    public Task<IReadOnlyList<SourceProfile>> SourceProfilesAsync(CancellationToken ct = default) => List<SourceProfile>("source-profiles", ct);

    // ---------- Projekte ----------

    public Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken ct = default) => List<string>("projects", ct);
    public Task<ProjectDto> CreateProjectAsync(ProjectCreateDto project, CancellationToken ct = default) => Send<ProjectDto>(HttpMethod.Post, "projects", project, ct);
    public Task<ProjectDto> GetProjectAsync(string project, CancellationToken ct = default) => Get<ProjectDto>(P(project), ct);
    public Task<ProjectDto> UpdateProjectAsync(string project, ProjectUpdateDto changes, CancellationToken ct = default) =>
        Send<ProjectDto>(HttpMethod.Patch, P(project), changes, ct);
    public Task<ProjectDto> AttachResourceAsync(string project, ResourceRef resource, CancellationToken ct = default) =>
        Send<ProjectDto>(HttpMethod.Post, P(project) + "/resources", resource, ct);
    public Task<ProjectDto> DetachResourceAsync(string project, ResourceKind kind, string location, string? server = null, CancellationToken ct = default) =>
        Send<ProjectDto>(HttpMethod.Delete, P(project) + "/resources" + Q(("kind", kind.ToString()), ("location", location), ("server", server)), null, ct);

    // ---------- Dateien ----------

    public Task<IReadOnlyList<FileDto>> FilesAsync(string project, CancellationToken ct = default) => List<FileDto>(P(project) + "/files", ct);
    public Task<FileDto> AddFileAsync(string project, FileContent file, CancellationToken ct = default) => Send<FileDto>(HttpMethod.Post, P(project) + "/files", file, ct);
    public Task RemoveFileAsync(string project, string file, CancellationToken ct = default) => Send<bool>(HttpMethod.Delete, F(project, file), null, ct);
    public Task<IReadOnlyList<SegmentDto>> SegmentsAsync(string project, string file, string language, CancellationToken ct = default) =>
        List<SegmentDto>(F(project, file) + "/" + E(language), ct);
    public Task<FileContent> TargetFileAsync(string project, string file, string language, CancellationToken ct = default) =>
        Get<FileContent>(F(project, file) + "/" + E(language) + "/target", ct);
    public Task<FileContent> XliffAsync(string project, string file, string language, CancellationToken ct = default) =>
        Get<FileContent>(F(project, file) + "/" + E(language) + "/xliff", ct);
    public Task<int> ReplaceAsync(string project, string file, string language, string find, string replace, bool matchCase = false, CancellationToken ct = default) =>
        Send<int>(HttpMethod.Post, F(project, file) + "/" + E(language) + "/replace" + Q(("find", find), ("replace", replace), ("matchCase", matchCase ? "true" : "false")), null, ct);

    // ---------- Segmente ----------

    public Task<SegmentDto> GetSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        Get<SegmentDto>(S(project, file, language, id), ct);
    public Task<SegmentDto> UpdateSegmentAsync(string project, string file, string language, int id, SegmentUpdateDto update, CancellationToken ct = default) =>
        Send<SegmentDto>(HttpMethod.Put, S(project, file, language, id), update, ct);
    public Task<ConfirmResult> ConfirmSegmentAsync(string project, string file, string language, int id, SegmentStatus? status = null, CancellationToken ct = default) =>
        Send<ConfirmResult>(HttpMethod.Post, S(project, file, language, id) + "/confirm", new ConfirmDto(status), ct);
    public Task<IReadOnlyList<TmMatch>> MatchesAsync(string project, string file, string language, int id, int? minScore = null, CancellationToken ct = default) =>
        List<TmMatch>(S(project, file, language, id) + "/matches" + Q(("minScore", minScore?.ToString())), ct);
    public Task<IReadOnlyList<TermHit>> SegmentTermsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        List<TermHit>(S(project, file, language, id) + "/terms", ct);
    public Task<IReadOnlyList<FragmentMatch>> FragmentsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        List<FragmentMatch>(S(project, file, language, id) + "/fragments", ct);
    public Task<IReadOnlyList<KnowledgeEntry>> SegmentKnowledgeAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        List<KnowledgeEntry>(S(project, file, language, id) + "/knowledge", ct);
    public Task<IReadOnlyList<string>> SuggestionsAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        List<string>(S(project, file, language, id) + "/suggestions", ct);
    public Task<SegmentDto> ApplyMatchAsync(string project, string file, string language, int id, int number = 1, CancellationToken ct = default) =>
        Send<SegmentDto>(HttpMethod.Post, S(project, file, language, id) + "/apply-match" + Q(("index", number.ToString())), null, ct);
    public Task<SegmentDto> CopySourceAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        Send<SegmentDto>(HttpMethod.Post, S(project, file, language, id) + "/copy-source", null, ct);
    public async Task<IReadOnlyList<SegmentDto>> SplitSegmentAsync(string project, string file, string language, int id, int sourceIndex, CancellationToken ct = default) =>
        await Send<List<SegmentDto>>(HttpMethod.Post, S(project, file, language, id) + "/split", new SplitDto(sourceIndex), ct);
    public Task<SegmentDto> MergeSegmentAsync(string project, string file, string language, int id, CancellationToken ct = default) =>
        Send<SegmentDto>(HttpMethod.Post, S(project, file, language, id) + "/merge", null, ct);

    // ---------- Stapelaufgaben und Qualität ----------

    public async Task<IReadOnlyList<PretranslateResult>> PretranslateAsync(string project, PretranslateDto? options = null, CancellationToken ct = default) =>
        await Send<List<PretranslateResult>>(HttpMethod.Post, P(project) + "/pretranslate", options ?? new PretranslateDto(), ct);
    public Task<IReadOnlyList<AnalysisRowDto>> AnalysisAsync(string project, CancellationToken ct = default) => List<AnalysisRowDto>(P(project) + "/analysis", ct);
    public Task<IReadOnlyList<QaIssueDto>> QaAsync(string project, CancellationToken ct = default) => List<QaIssueDto>(P(project) + "/qa", ct);
    public Task<QaSettings> SetQaSettingsAsync(string project, QaSettings settings, CancellationToken ct = default) =>
        Send<QaSettings>(HttpMethod.Put, P(project) + "/qa-settings", settings, ct);

    // ---------- Translation Memory ----------

    public Task<IReadOnlyList<TmMatch>> SearchTmAsync(string project, string source, string sourceLanguage, string targetLanguage,
        int? minScore = null, string? previous = null, string? next = null, CancellationToken ct = default) =>
        List<TmMatch>(P(project) + "/tm/search" + Q(("source", source), ("sourceLanguage", sourceLanguage), ("targetLanguage", targetLanguage),
            ("minScore", minScore?.ToString()), ("previous", previous), ("next", next)), ct);
    public Task<IReadOnlyList<TmUnit>> ConcordanceAsync(string project, string text, string sourceLanguage, string targetLanguage, bool inTarget = false, CancellationToken ct = default) =>
        List<TmUnit>(P(project) + "/tm/concordance" + Q(("text", text), ("sourceLanguage", sourceLanguage), ("targetLanguage", targetLanguage),
            ("inTarget", inTarget ? "true" : "false")), ct);
    public Task AddToTmAsync(string project, TmAddDto unit, CancellationToken ct = default) => Send<bool>(HttpMethod.Post, P(project) + "/tm", unit, ct);
    public Task<IReadOnlyList<TmUnit>> TmUnitsAsync(string project, string? filter = null, int offset = 0, int limit = 50, CancellationToken ct = default) =>
        List<TmUnit>(P(project) + "/tm/units" + Q(("filter", filter), ("offset", offset.ToString()), ("limit", limit.ToString())), ct);
    public Task UpdateTmUnitAsync(string project, long id, string source, string target, CancellationToken ct = default) =>
        Send<bool>(HttpMethod.Put, P(project) + "/tm/units/" + id, new TmUpdateRequest(source, target), ct);
    public Task DeleteTmUnitAsync(string project, long id, CancellationToken ct = default) => Send<bool>(HttpMethod.Delete, P(project) + "/tm/units/" + id, null, ct);
    public Task<int> ImportTmAsync(string project, FileContent file, CancellationToken ct = default) => Send<int>(HttpMethod.Post, P(project) + "/tm/import", file, ct);
    public Task<FileContent> ExportTmAsync(string project, CancellationToken ct = default) => Get<FileContent>(P(project) + "/tm/export", ct);

    // ---------- Terminologie ----------

    public Task<IReadOnlyList<Concept>> SearchTermsAsync(string project, string? query = null, string? language = null, CancellationToken ct = default) =>
        List<Concept>(P(project) + "/terms" + Q(("query", query), ("language", language)), ct);
    public Task<long> AddConceptAsync(string project, ConceptDto concept, CancellationToken ct = default) => Send<long>(HttpMethod.Post, P(project) + "/terms", concept, ct);
    public Task DeleteConceptAsync(string project, long id, CancellationToken ct = default) => Send<bool>(HttpMethod.Delete, P(project) + "/terms/" + id, null, ct);
    public Task<int> ImportTermsAsync(string project, FileContent file, CancellationToken ct = default) => Send<int>(HttpMethod.Post, P(project) + "/terms/import", file, ct);
    public Task<FileContent> ExportTermsAsync(string project, CancellationToken ct = default) => Get<FileContent>(P(project) + "/terms/export", ct);

    // ---------- Wissen ----------

    public Task<IReadOnlyList<KnowledgeEntry>> SearchKnowledgeAsync(string project, string? query = null, string? language = null, CancellationToken ct = default) =>
        List<KnowledgeEntry>(P(project) + "/knowledge" + Q(("query", query), ("language", language)), ct);
    public Task<long> AddKnowledgeAsync(string project, KnowledgeEntry entry, CancellationToken ct = default) =>
        Send<long>(HttpMethod.Post, P(project) + "/knowledge", entry, ct);
    public Task UpdateKnowledgeAsync(string project, long id, KnowledgeEntry entry, CancellationToken ct = default) =>
        Send<bool>(HttpMethod.Put, P(project) + "/knowledge/" + id, entry, ct);
    public Task DeleteKnowledgeAsync(string project, long id, CancellationToken ct = default) => Send<bool>(HttpMethod.Delete, P(project) + "/knowledge/" + id, null, ct);

    // ---------- Pakete, Alignment, Texterkennung ----------

    public Task<ProjectDto> ImportPackageAsync(FileContent package, string? topic = null, string? name = null, CancellationToken ct = default) =>
        Send<ProjectDto>(HttpMethod.Post, "packages/import" + Q(("topic", topic), ("name", name)), package, ct);
    public Task<FileContent> ReturnPackageAsync(string project, CancellationToken ct = default) => Get<FileContent>(P(project) + "/return-package", ct);
    public async Task<IReadOnlyList<AlignedPair>> AlignAsync(AlignRequest request, CancellationToken ct = default) =>
        await Send<List<AlignedPair>>(HttpMethod.Post, "align", request, ct);
    public Task<FileDto> RecognizePdfAsync(string project, FileContent pdf, CancellationToken ct = default) => Send<FileDto>(HttpMethod.Post, P(project) + "/ocr", pdf, ct);

    // ---------- Agenten ----------

    public Task<TranslationResult> TranslateAsync(TranslateRequest request, CancellationToken ct = default) => Send<TranslationResult>(HttpMethod.Post, "agents/translate", request, ct);
    public Task<EvaluationResult> EvaluateAsync(EvaluateRequest request, CancellationToken ct = default) => Send<EvaluationResult>(HttpMethod.Post, "agents/evaluate", request, ct);
    public Task<UsageResult> CheckUsageAsync(UsageRequest request, CancellationToken ct = default) => Send<UsageResult>(HttpMethod.Post, "agents/usage", request, ct);
    public Task<ResearchResult> ResearchAsync(ResearchRequest request, CancellationToken ct = default) => Send<ResearchResult>(HttpMethod.Post, "agents/research", request, ct);
    public Task<KnowledgeRunResult> FillKnowledgeAsync(KnowledgeAgentRequest request, CancellationToken ct = default) =>
        Send<KnowledgeRunResult>(HttpMethod.Post, "agents/knowledge", request, ct);
    public Task<TermComparisonResult> CompareTermAsync(CompareRequest request, CancellationToken ct = default) =>
        Send<TermComparisonResult>(HttpMethod.Post, "agents/compare", request, ct);

    // ---------- Erweiterungen ----------

    public Task<IReadOnlyList<ExtensionInfo>> ExtensionsAsync(CancellationToken ct = default) => List<ExtensionInfo>("extensions", ct);
    public Task<ExtensionResult> RunCommandAsync(string command, ExtensionRunDto request, CancellationToken ct = default) =>
        Send<ExtensionResult>(HttpMethod.Post, "extensions/commands/" + E(command), request, ct);

    // ---------- Hilfen ----------

    private static string E(string s) => Uri.EscapeDataString(s);
    private static string P(string project) => "projects/" + E(project);
    private static string F(string project, string file) => P(project) + "/files/" + E(file);
    private static string S(string project, string file, string language, int id) => F(project, file) + "/" + E(language) + "/segments/" + id;

    private static string Q(params (string Name, string? Value)[] parameters)
    {
        var parts = parameters.Where(p => p.Value != null).Select(p => p.Name + "=" + E(p.Value!)).ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private Task<T> Get<T>(string path, CancellationToken ct) => Send<T>(HttpMethod.Get, path, null, ct);

    private async Task<IReadOnlyList<T>> List<T>(string path, CancellationToken ct) => await Send<List<T>>(HttpMethod.Get, path, null, ct);

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    /// <summary>HTTP-Fehler in dieselben Ausnahmen übersetzen, die die interne Schnittstelle wirft.</summary>
    private static async Task<Exception> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        var message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            message = doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString()!,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String => d.GetString()!,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String => t.GetString()!,
                _ => text,
            };
        }
        catch (JsonException) { }
        if (message.Length == 0) message = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new KeyNotFoundException(message),
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException("Zugangsschlüssel ungültig."),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(message.Length > 0 && !message.StartsWith("403") ? message : "Keine Berechtigung."),
            HttpStatusCode.BadRequest => new ArgumentException(message),
            _ => new InvalidOperationException(message),
        };
    }
}
