using System.IO.Compression;
using System.Xml.Linq;
using SententiamConverte.Core.Projects;

namespace SententiamConverte.Core.Exchange;

/// <summary>
/// Projektpakete im Format .sdlppx öffnen und Rückgabepakete (.sdlrpx) erzeugen – zur Interoperabilität.
/// Übernommen werden die SDLXLIFF-Dateien je Zielsprache und mitgelieferte TMs (.sdltm).
/// </summary>
public static class ProjectPackage
{
    public sealed record PackageInfo(string Name, string SourceLanguage, List<string> TargetLanguages,
        List<(string Path, string Language)> BilingualFiles, List<string> TmFiles);

    public static PackageInfo Inspect(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        var proj = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".sdlproj", StringComparison.OrdinalIgnoreCase));
        string name = Path.GetFileNameWithoutExtension(packagePath);
        string? src = null;
        var targets = new List<string>();
        if (proj != null)
        {
            using var s = proj.Open();
            var x = XDocument.Load(s);
            name = (string?)x.Root?.Attribute("Name") ?? name;
            foreach (var dir in x.Descendants().Where(e => e.Name.LocalName == "LanguageDirection"))
            {
                src ??= (string?)dir.Attribute("SourceLanguageCode");
                if ((string?)dir.Attribute("TargetLanguageCode") is { } t && !targets.Contains(t)) targets.Add(t);
            }
        }
        var files = zip.Entries
            .Where(e => e.FullName.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.FullName, Language: e.FullName.Replace('\\', '/').Split('/')[0]))
            .ToList();
        if (targets.Count == 0) targets = files.Select(f => f.Language).Distinct().ToList();
        src ??= "de-DE";
        var tms = zip.Entries.Where(e => e.FullName.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase)).Select(e => e.FullName).ToList();
        return new PackageInfo(name, src, targets, files, tms);
    }

    /// <summary>Legt aus einem Paket ein neues Projekt an.</summary>
    public static TranslationProject Import(string packagePath, string projectFolder, string topic = "")
    {
        var info = Inspect(packagePath);
        var project = TranslationProject.Create(projectFolder, info.Name, info.SourceLanguage, info.TargetLanguages, topic);
        Directory.CreateDirectory(Path.Combine(projectFolder, "paket"));
        File.Copy(packagePath, Path.Combine(projectFolder, "paket", "original" + Path.GetExtension(packagePath)), overwrite: true);

        using var zip = ZipFile.OpenRead(packagePath);
        foreach (var (path, lang) in info.BilingualFiles)
        {
            using var s = zip.GetEntry(path)!.Open();
            var buffer = new MemoryStream();
            s.CopyTo(buffer);
            buffer.Position = 0;
            var targetLang = info.TargetLanguages.FirstOrDefault(t => string.Equals(t, lang, StringComparison.OrdinalIgnoreCase)) ?? lang;
            project.AddFile(buffer, Path.GetFileName(path), onlyLanguage: targetLang, packagePath: path);
        }
        foreach (var tmPath in info.TmFiles)
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sdltm");
            zip.GetEntry(tmPath)!.ExtractToFile(tmp);
            try { SdltmImporter.Import(tmp, project.ProjectTm); }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(tmp); }
        }
        project.Save();
        return project;
    }

    /// <summary>Rückgabepaket: das Original-Paket mit den übersetzten SDLXLIFF-Dateien an denselben Stellen.</summary>
    public static void ExportReturnPackage(TranslationProject project, Stream output)
    {
        var original = Directory.GetFiles(Path.Combine(project.Folder, "paket"), "original.*").FirstOrDefault()
                       ?? throw new InvalidOperationException("Das Projekt stammt nicht aus einem Projektpaket.");
        using var source = ZipFile.OpenRead(original);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var replaced = project.Files.Where(f => f.PackagePath != null).ToDictionary(f => f.PackagePath!.Replace('\\', '/'));

        foreach (var entry in source.Entries)
        {
            var key = entry.FullName.Replace('\\', '/');
            // Rückgabepakete enthalten keine TMs und keine Quelldateien-Kopien
            if (key.EndsWith(".sdltm", StringComparison.OrdinalIgnoreCase)) continue;
            var target = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var ws = target.Open();
            if (replaced.TryGetValue(key, out var file))
            {
                var doc = project.LoadDocument(file, file.OnlyLanguage!);
                project.ExportTarget(file, doc, ws);
            }
            else
            {
                using var rs = entry.Open();
                rs.CopyTo(ws);
            }
        }
    }
}
