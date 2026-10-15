using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SententiamConverte.Core.Alignment;
using SententiamConverte.Core.Exchange;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Segmentation;
using SententiamConverte.Core.Tm;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

public partial class TmRowViewModel : ObservableObject
{
    public required long Id { get; init; }
    public required string Info { get; init; }
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
}

public partial class AlignRowViewModel : ObservableObject
{
    public required AlignedPair Pair { get; init; }
    public string SourceText => Pair.SourceText;
    public string TargetText => Pair.TargetText;
    public string Confidence => Pair.IsComplete ? $"{Pair.Confidence * 100:0} %" : "–";
    public bool IsComplete => Pair.IsComplete;
    public bool Accepted { get => Pair.Accepted; set { Pair.Accepted = value; OnPropertyChanged(); } }
}

public sealed record AttachedRow(ResourceRef Ref)
{
    public string Kind => ResourceFactory.KindName(Ref.Kind);
    public string Name => Ref.DisplayName;
    public string Languages => Ref.LanguagesText;
    public string WriteBack => Ref.WriteBack ? "schreibt zurück" : "nur lesen";
}

public sealed record ConceptRow(Concept Concept)
{
    public long Id => Concept.Id;
    public string Terms => string.Join("  ·  ", Concept.Terms.Select(t => $"{t.Language}: {(t.Status == TermStatus.Forbidden ? "✗ " : "")}{t.Text}"));
    public string Definition => Concept.Definition ?? "";
}

/// <summary>Bereich „TM & Terminologie“.</summary>
public partial class ResourcesViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    public ResourcesViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<string> TmInfo { get; } = new();
    public ObservableCollection<string> TermbaseInfo { get; } = new();
    public ObservableCollection<ConceptRow> Concepts { get; } = new();

    [ObservableProperty] public partial string TermSearch { get; set; } = "";
    [ObservableProperty] public partial string NewSourceTerm { get; set; } = "";
    [ObservableProperty] public partial string NewTargetTerm { get; set; } = "";
    [ObservableProperty] public partial string NewTargetLanguage { get; set; } = "";
    [ObservableProperty] public partial string NewDefinition { get; set; } = "";
    [ObservableProperty] public partial bool NewTargetForbidden { get; set; }

    // Angebundene Ressourcen
    public ObservableCollection<AttachedRow> Attached { get; } = new();
    public ObservableCollection<string> RemoteNames { get; } = new();
    public IReadOnlyList<string> KindNames { get; } = ["Translation Memory", "Terminologie", "Wissensdatenbank"];
    [ObservableProperty] public partial int AttachKind { get; set; }
    [ObservableProperty] public partial string? AttachServer { get; set; }
    [ObservableProperty] public partial string? AttachRemoteName { get; set; }
    [ObservableProperty] public partial string AttachLanguages { get; set; } = "";
    [ObservableProperty] public partial bool AttachWriteBack { get; set; } = true;
    [ObservableProperty] public partial string ResourceErrors { get; set; } = "";
    public IEnumerable<string> ServerNames => AppServices.Servers.Servers.Select(s => s.Name);

    // TM-Pflege
    public ObservableCollection<TmRowViewModel> TmRows { get; } = new();
    [ObservableProperty] public partial string TmFilter { get; set; } = "";
    [ObservableProperty] public partial int TmPage { get; set; }
    private const int PageSize = 50;

    // Alignment
    public ObservableCollection<AlignRowViewModel> AlignRows { get; } = new();
    [ObservableProperty] public partial string AlignSourceName { get; set; } = "";
    [ObservableProperty] public partial string AlignTargetName { get; set; } = "";
    [ObservableProperty] public partial string AlignTargetLanguage { get; set; } = "";
    private PickedFile? _alignSource, _alignTarget;

    public void Refresh()
    {
        TmInfo.Clear();
        TermbaseInfo.Clear();
        var p = _main.Project;
        if (p == null) return;
        foreach (var tm in p.Tms) TmInfo.Add(SafeInfo(() => $"{tm.Name}: {tm.Count()} Einheiten  ({tm.Location})", tm.Name));
        foreach (var tb in p.Termbases) TermbaseInfo.Add(SafeInfo(() => $"{tb.Name}: {tb.ConceptCount()} Einträge  ({tb.Location})", tb.Name));
        Attached.Clear();
        foreach (var r in p.Resources) Attached.Add(new AttachedRow(r));
        ResourceErrors = string.Join("\n", p.ResourceErrors);
        OnPropertyChanged(nameof(ServerNames));
        if (NewTargetLanguage.Length == 0) NewTargetLanguage = p.TargetLanguages.FirstOrDefault() ?? "";
        if (AlignTargetLanguage.Length == 0) AlignTargetLanguage = p.TargetLanguages.FirstOrDefault() ?? "";
        SearchTerms();
        LoadTmPage();
    }

    partial void OnTermSearchChanged(string value) => SearchTerms();

    private void SearchTerms()
    {
        Concepts.Clear();
        var p = _main.Project;
        if (p == null) return;
        var list = TermSearch.Trim().Length == 0
            ? p.ProjectTermbase.AllConcepts().TakeLast(200)
            : p.Termbases.SelectMany(tb => p.TargetLanguages.Append(p.SourceLanguage).SelectMany(l => tb.Lookup(l, TermSearch)))
                .DistinctBy(c => c.Id);
        foreach (var c in list) Concepts.Add(new ConceptRow(c));
    }

    [RelayCommand]
    private void AddConcept()
    {
        var p = _main.Project;
        if (p == null || NewSourceTerm.Trim().Length == 0) return;
        p.ProjectTermbase.AddConcept([
            (p.SourceLanguage, NewSourceTerm.Trim(), TermStatus.Preferred),
            (NewTargetLanguage.Trim(), NewTargetTerm.Trim(), NewTargetForbidden ? TermStatus.Forbidden : TermStatus.Preferred)],
            NewDefinition.Trim().Length > 0 ? NewDefinition.Trim() : null, p.Topic);
        NewSourceTerm = NewTargetTerm = NewDefinition = "";
        NewTargetForbidden = false;
        Refresh();
    }

    [RelayCommand]
    private void DeleteConcept(ConceptRow? row)
    {
        if (row == null || _main.Project == null) return;
        _main.Project.ProjectTermbase.DeleteConcept(row.Id);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportTmx()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("TMX importieren", ["*.tmx"], true);
        await _main.RunBusy("Importiere TMX …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                await using var s = await f.OpenRead();
                var buffer = new MemoryStream();
                await s.CopyToAsync(buffer);
                buffer.Position = 0;
                total += (await Task.Run(() => TmxIo.Import(buffer, p.ProjectTm))).Imported;
            }
            Refresh();
            _main.Status = $"{total} Übersetzungseinheiten ins Projekt-TM importiert.";
        });
    }

    [RelayCommand]
    private async Task ExportTmx()
    {
        var p = _main.Project;
        if (p == null) return;
        await using var output = await AppServices.Platform!.SaveFileAsync("TMX exportieren", p.Name + ".tmx");
        if (output == null) return;
        var buffer = new MemoryStream();
        var n = await Task.Run(() => TmxIo.Export(p.ProjectTm, buffer));
        buffer.Position = 0;
        await buffer.CopyToAsync(output);
        _main.Status = $"{n} Einheiten exportiert.";
    }

    [RelayCommand]
    private async Task ImportTbx()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("Terminologie importieren (TBX / Terminologie-XML)", ["*.tbx", "*.xml", "*.mtf"], true);
        await _main.RunBusy("Importiere Terminologie …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                await using var s = await f.OpenRead();
                var buffer = new MemoryStream();
                await s.CopyToAsync(buffer);
                buffer.Position = 0;
                total += await Task.Run(() => TbxIo.Import(buffer, p.ProjectTermbase));
            }
            Refresh();
            _main.Status = $"{total} Einträge importiert.";
        });
    }

    [RelayCommand]
    private async Task ExportTbx()
    {
        var p = _main.Project;
        if (p == null) return;
        await using var output = await AppServices.Platform!.SaveFileAsync("TBX exportieren", p.Name + ".tbx");
        if (output == null) return;
        var buffer = new MemoryStream();
        var n = await Task.Run(() => TbxIo.Export(p.ProjectTermbase, buffer));
        buffer.Position = 0;
        await buffer.CopyToAsync(output);
        _main.Status = $"{n} Einträge exportiert.";
    }

    private static string SafeInfo(Func<string> f, string name)
    {
        try { return f(); }
        catch (RemoteException ex) { return $"{name}: {ex.Message}"; }
    }

    // ---------- Ressourcen anbinden ----------

    partial void OnAttachServerChanged(string? value) => _ = LoadRemoteNames();
    partial void OnAttachKindChanged(int value) => _ = LoadRemoteNames();

    private async Task LoadRemoteNames()
    {
        RemoteNames.Clear();
        var account = AttachServer == null ? null : AppServices.Servers.Get(AttachServer);
        if (account == null) return;
        try
        {
            foreach (var r in (await ResourceFactory.Api(account).ResourcesAsync()).Where(r => (int)r.Kind == AttachKind))
                RemoteNames.Add(r.Name);
            AttachRemoteName = RemoteNames.FirstOrDefault();
        }
        catch (RemoteException ex) { _main.Status = ex.Message; }
    }

    private List<string> ParseLanguages() =>
        AttachLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

    [RelayCommand]
    private void AttachRemote()
    {
        var p = _main.Project;
        if (p == null || AttachServer == null || string.IsNullOrEmpty(AttachRemoteName)) { _main.Status = "Bitte Server und Ressource wählen."; return; }
        p.Attach(new ResourceRef
        {
            Kind = (ResourceKind)AttachKind, Server = AttachServer, Location = AttachRemoteName,
            Languages = ParseLanguages(), WriteBack = AttachWriteBack,
        });
        AfterAttach(p);
    }

    [RelayCommand]
    private async Task AttachLocal()
    {
        var p = _main.Project;
        if (p == null) return;
        var ext = ResourceFactory.Extension((ResourceKind)AttachKind);
        var folder = await AppServices.Platform!.PickFolderAsync($"Ordner mit {ext}-Dateien wählen");
        if (folder == null) return;
        foreach (var f in Directory.GetFiles(folder, "*" + ext))
            p.Attach(new ResourceRef { Kind = (ResourceKind)AttachKind, Location = f, Languages = ParseLanguages(), WriteBack = AttachWriteBack });
        AfterAttach(p);
    }

    [RelayCommand]
    private void CreateLocal()
    {
        var p = _main.Project;
        if (p == null) return;
        var kind = (ResourceKind)AttachKind;
        var name = AttachRemoteName is { Length: > 0 } n ? n : "Gemeinsam";
        var path = Path.Combine(Core.Ai.AppPaths.DataRoot, "ressourcen", name + ResourceFactory.Extension(kind));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Datei anlegen, indem die Ressource einmal geöffnet wird
        if (!File.Exists(path))
        {
            ILanguageResource r = kind switch
            {
                ResourceKind.Tm => new TranslationMemory(path),
                ResourceKind.Termbase => new Core.Terminology.Termbase(path),
                _ => new Core.Knowledge.KnowledgeBase(path),
            };
            r.Dispose();
        }
        p.Attach(new ResourceRef { Kind = kind, Location = path, Languages = ParseLanguages(), WriteBack = AttachWriteBack });
        AfterAttach(p);
    }

    [RelayCommand]
    private void Detach(AttachedRow? row)
    {
        var p = _main.Project;
        if (p == null || row == null) return;
        p.Detach(row.Ref);
        AfterAttach(p);
    }

    private void AfterAttach(Core.Projects.TranslationProject p)
    {
        Refresh();
        _main.Knowledge.OnProjectChanged();
        _main.Status = p.ResourceErrors.Count > 0 ? "Nicht erreichbar: " + string.Join("; ", p.ResourceErrors) : "Ressourcen aktualisiert.";
    }

    // ---------- TM-Pflege ----------

    partial void OnTmFilterChanged(string value) { TmPage = 0; LoadTmPage(); }

    private void LoadTmPage()
    {
        TmRows.Clear();
        var p = _main.Project;
        if (p == null) return;
        foreach (var u in p.ProjectTm.Browse(TmFilter, TmPage * PageSize, PageSize))
            TmRows.Add(new TmRowViewModel
            {
                Id = u.Id, Source = u.Source, Target = u.Target,
                Info = $"{u.SourceLanguage} → {u.TargetLanguage} · {u.Modified.ToLocalTime():g} · {u.Origin}",
            });
    }

    [RelayCommand]
    private void NextTmPage() { if (TmRows.Count == PageSize) { TmPage++; LoadTmPage(); } }

    [RelayCommand]
    private void PreviousTmPage() { if (TmPage > 0) { TmPage--; LoadTmPage(); } }

    [RelayCommand]
    private void SaveTmRow(TmRowViewModel? row)
    {
        if (row == null || _main.Project == null) return;
        _main.Project.ProjectTm.Update(row.Id, row.Source, row.Target);
        _main.Status = "TM-Eintrag gespeichert.";
    }

    [RelayCommand]
    private void DeleteTmRow(TmRowViewModel? row)
    {
        if (row == null || _main.Project == null) return;
        _main.Project.ProjectTm.Delete(row.Id);
        TmRows.Remove(row);
        Refresh();
    }

    [RelayCommand]
    private async Task ImportSdltm()
    {
        var p = _main.Project;
        if (p == null) return;
        var files = await AppServices.Platform!.PickFilesAsync("TM-Datei (.sdltm) übernehmen", ["*.sdltm"], true);
        await _main.RunBusy("Übernehme TM …", async () =>
        {
            var total = 0;
            foreach (var f in files)
            {
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdltm");
                await using (var s = await f.OpenRead())
                await using (var fs = File.Create(tmp))
                    await s.CopyToAsync(fs);
                try { total += await Task.Run(() => SdltmImporter.Import(tmp, p.ProjectTm)); }
                finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(tmp); }
            }
            Refresh();
            _main.Status = $"{total} Übersetzungseinheiten übernommen.";
        });
    }

    // ---------- Alignment ----------

    [RelayCommand]
    private async Task PickAlignSource()
    {
        var f = await AppServices.Platform!.PickFilesAsync("Ausgangstext wählen", FileFilters.SupportedExtensions.Select(e => "*" + e).ToList(), false);
        if (f.Count > 0) { _alignSource = f[0]; AlignSourceName = f[0].Name; }
    }

    [RelayCommand]
    private async Task PickAlignTarget()
    {
        var f = await AppServices.Platform!.PickFilesAsync("Übersetzung wählen", FileFilters.SupportedExtensions.Select(e => "*" + e).ToList(), false);
        if (f.Count > 0) { _alignTarget = f[0]; AlignTargetName = f[0].Name; }
    }

    private static async Task<List<string>> Sentences(PickedFile file, string language)
    {
        var filter = FileFilters.ForFile(file.Name) ?? throw new NotSupportedException($"{file.Name}: Dateityp wird nicht unterstützt.");
        var ms = new MemoryStream();
        await using (var s = await file.OpenRead()) await s.CopyToAsync(ms);
        ms.Position = 0;
        var doc = await Task.Run(() => filter.Read(ms, file.Name, language, language, new Segmenter(language)));
        return doc.Segments.Where(x => !x.Locked).Select(x => x.Source).ToList();
    }

    [RelayCommand]
    private async Task RunAlignment()
    {
        var p = _main.Project;
        if (p == null || _alignSource == null || _alignTarget == null) { _main.Status = "Bitte Ausgangstext und Übersetzung wählen."; return; }
        await _main.RunBusy("Richte aus …", async () =>
        {
            var src = await Sentences(_alignSource, p.SourceLanguage);
            var tgt = await Sentences(_alignTarget, AlignTargetLanguage);
            var pairs = await Task.Run(() => Aligner.Align(src, tgt));
            AlignRows.Clear();
            foreach (var pair in pairs) AlignRows.Add(new AlignRowViewModel { Pair = pair });
            _main.Status = $"{pairs.Count(x => x.IsComplete)} Paare gefunden, {pairs.Count(x => x.Accepted)} vorausgewählt. Bitte prüfen und übernehmen.";
        });
    }

    [RelayCommand]
    private void ImportAlignment()
    {
        var p = _main.Project;
        if (p == null) return;
        var n = 0;
        foreach (var row in AlignRows.Where(r => r.Accepted && r.IsComplete))
        {
            p.ProjectTm.Add(p.SourceLanguage, AlignTargetLanguage, row.SourceText, row.TargetText, "Alignment");
            n++;
        }
        AlignRows.Clear();
        Refresh();
        _main.Status = $"{n} ausgerichtete Paare ins Projekt-TM übernommen.";
    }

    /// <summary>Vorhandene TM-/Terminologie-Datei (z. B. ein Haupt-TM) zusätzlich einbinden – nur am Desktop mit echtem Pfad.</summary>
    [RelayCommand]
    private async Task AttachTm()
    {
        var p = _main.Project;
        if (p == null) return;
        var folder = await AppServices.Platform!.PickFolderAsync("Ordner mit .sctm/.sctb-Dateien wählen");
        if (folder == null) return;
        foreach (var f in Directory.GetFiles(folder, "*.sctm")) p.AttachTm(f);
        foreach (var f in Directory.GetFiles(folder, "*.sctb")) p.AttachTermbase(f);
        Refresh();
    }
}
