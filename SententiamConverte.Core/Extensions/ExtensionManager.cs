using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Filters;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Extensions;

/// <summary>Beschreibung einer installierten Erweiterung (extension.json in ihrem Ordner).</summary>
public sealed class ExtensionManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }

    /// <summary>Datei der Bibliothek, z. B. „Beispiel.Erweiterung.dll“.</summary>
    public string Assembly { get; set; } = "";

    /// <summary>Neu installierte Erweiterungen sind ausgeschaltet, bis der Benutzer sie bewusst einschaltet.</summary>
    public bool Enabled { get; set; }
}

/// <summary>Eine installierte Erweiterung mit Ladezustand.</summary>
public sealed class InstalledExtension
{
    public required ExtensionManifest Manifest { get; init; }
    public required string Folder { get; init; }
    public bool Loaded { get; internal set; }
    public string? Error { get; internal set; }
    public List<IExtensionCommand> Commands { get; } = new();
    public List<string> Contributions { get; } = new();
}

/// <summary>
/// Installiert, lädt und verwaltet Erweiterungen. Jede Erweiterung liegt in einem eigenen Ordner und wird in einem
/// eigenen Ladekontext geladen; die Schnittstellen (SententiamConverte.Core) teilt sie mit dem Programm.
/// Erweiterungen führen Code mit den Rechten des Benutzers aus – deshalb sind sie nach der Installation ausgeschaltet.
/// </summary>
public sealed class ExtensionManager
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly List<InstalledExtension> _extensions = new();

    public string Folder { get; }
    public IReadOnlyList<InstalledExtension> Extensions => _extensions;
    public IEnumerable<IExtensionCommand> Commands => _extensions.Where(e => e.Loaded).SelectMany(e => e.Commands);

    public ExtensionManager(string? folder = null)
    {
        Folder = folder ?? Path.Combine(AppPaths.DataRoot, "erweiterungen");
        Directory.CreateDirectory(Folder);
    }

    /// <summary>Liest alle installierten Erweiterungen und lädt die eingeschalteten.</summary>
    public void LoadAll()
    {
        _extensions.Clear();
        foreach (var dir in Directory.GetDirectories(Folder))
        {
            var manifestPath = Path.Combine(dir, "extension.json");
            if (!File.Exists(manifestPath)) continue;
            ExtensionManifest? m;
            try { m = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(manifestPath), Json); }
            catch (JsonException) { continue; }
            if (m == null) continue;
            var ext = new InstalledExtension { Manifest = m, Folder = dir };
            _extensions.Add(ext);
            if (m.Enabled) Load(ext);
        }
    }

    private void Load(InstalledExtension ext)
    {
        if (ext.Loaded) return;
        try
        {
            var path = Path.Combine(ext.Folder, ext.Manifest.Assembly);
            if (!File.Exists(path)) throw new FileNotFoundException("Bibliothek fehlt", path);
            var context = new ExtensionLoadContext(path);
            var asm = context.LoadUnlocked(Path.GetFullPath(path));
            var types = asm.GetExportedTypes().Where(t => typeof(IExtension).IsAssignableFrom(t) && !t.IsAbstract).ToList();
            if (types.Count == 0) throw new InvalidOperationException("Keine Klasse mit IExtension gefunden.");
            foreach (var t in types)
            {
                var instance = (IExtension)Activator.CreateInstance(t)!;
                instance.Register(new Registry(ext));
            }
            ext.Loaded = true;
            ext.Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ext.Error = ex.Message;
        }
    }

    /// <summary>Installiert eine Erweiterung aus einem Paket (.scext/.zip mit extension.json) oder einer einzelnen DLL.</summary>
    public InstalledExtension Install(string path)
    {
        var temp = Path.Combine(Path.GetTempPath(), "sc-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ExtensionManifest manifest;
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, Path.Combine(temp, Path.GetFileName(path)));
                manifest = new ExtensionManifest
                {
                    Id = Path.GetFileNameWithoutExtension(path), Name = Path.GetFileNameWithoutExtension(path), Version = "?",
                    Assembly = Path.GetFileName(path),
                };
            }
            else
            {
                ZipFile.ExtractToDirectory(path, temp);
                var mp = Path.Combine(temp, "extension.json");
                manifest = File.Exists(mp)
                    ? JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(mp), Json) ?? throw new InvalidDataException("extension.json ist leer.")
                    : throw new InvalidDataException("Das Paket enthält keine extension.json.");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[A-Za-z0-9._\-]{1,80}$"))
                throw new InvalidDataException("Ungültige Kennung in extension.json.");
            manifest.Enabled = false;                    // Sicherheit: erst nach bewusstem Einschalten aktiv
            var target = Path.Combine(Folder, manifest.Id);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temp, target);
            File.WriteAllText(Path.Combine(target, "extension.json"), JsonSerializer.Serialize(manifest, Json));
            var ext = new InstalledExtension { Manifest = manifest, Folder = target };
            _extensions.RemoveAll(e => e.Manifest.Id == manifest.Id);
            _extensions.Add(ext);
            return ext;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    /// <summary>Einschalten lädt sofort; Ausschalten wirkt nach einem Neustart vollständig.</summary>
    public void SetEnabled(string id, bool enabled)
    {
        var ext = _extensions.FirstOrDefault(e => e.Manifest.Id == id) ?? throw new KeyNotFoundException(id);
        ext.Manifest.Enabled = enabled;
        File.WriteAllText(Path.Combine(ext.Folder, "extension.json"), JsonSerializer.Serialize(ext.Manifest, Json));
        if (enabled) Load(ext);
    }

    public void Remove(string id)
    {
        var ext = _extensions.FirstOrDefault(e => e.Manifest.Id == id) ?? throw new KeyNotFoundException(id);
        ext.Manifest.Enabled = false;
        _extensions.Remove(ext);
        try { Directory.Delete(ext.Folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bibliothek ist noch geladen – beim nächsten Start entfernen
            File.WriteAllText(Path.Combine(ext.Folder, "extension.json"), JsonSerializer.Serialize(ext.Manifest, Json));
            File.WriteAllText(Path.Combine(ext.Folder, ".entfernen"), "");
        }
    }

    /// <summary>Beim Start: Ordner entfernen, die beim letzten Mal noch gesperrt waren.</summary>
    public void CleanUp()
    {
        foreach (var dir in Directory.GetDirectories(Folder).Where(d => File.Exists(Path.Combine(d, ".entfernen"))))
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public IExtensionCommand? FindCommand(string id) => Commands.FirstOrDefault(c => c.Id == id);

    private sealed class Registry(InstalledExtension ext) : IExtensionRegistry
    {
        public void AddFileFilter(IFileFilter filter)
        {
            FileFilters.Register(filter);
            ext.Contributions.Add($"Dateiformat: {filter.Name} ({string.Join(", ", filter.Extensions)})");
        }

        public void AddQaCheck(IQaCheck check)
        {
            QaChecker.Register(check);
            ext.Contributions.Add($"QA-Prüfung: {check.Name}");
        }

        public void AddCommand(IExtensionCommand command)
        {
            ext.Commands.Add(command);
            ext.Contributions.Add($"Befehl: {command.Name}");
        }
    }

    /// <summary>
    /// Eigener Ladekontext: Abhängigkeiten aus dem Erweiterungsordner, die Programm-Schnittstellen vom Programm.
    /// Bibliotheken werden aus dem Speicher geladen, damit die Dateien nicht gesperrt sind (Entfernen und Aktualisieren
    /// ohne Neustart möglich).
    /// </summary>
    private sealed class ExtensionLoadContext(string mainPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainPath);

        public Assembly LoadUnlocked(string path)
        {
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            using var pdb = File.Exists(pdbPath) ? new MemoryStream(File.ReadAllBytes(pdbPath)) : null;
            return LoadFromStream(dll, pdb);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Gemeinsame Typen (IExtension usw.) müssen aus dem Programm kommen, sonst passen sie nicht zusammen
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadUnlocked(path) : null;
        }
    }
}
