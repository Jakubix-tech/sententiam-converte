using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SententiamConverte.Core.Knowledge;
using SententiamConverte.Core.Remote;
using SententiamConverte.Core.Resources;
using SententiamConverte.Core.Terminology;
using SententiamConverte.Core.Tm;

namespace SententiamConverte.Server;

public enum Role { Reader, Editor, Admin }

public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public Role Role { get; set; } = Role.Editor;
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

/// <summary>Benutzer mit Zugangsschlüsseln. Gespeichert werden nur SHA-256-Prüfsummen der Schlüssel.</summary>
public sealed class UserStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<UserAccount> _users;

    public UserStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "benutzer.json");
        _users = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(_path), RemoteApi.Json) ?? []
            : [];
    }

    public IReadOnlyList<UserAccount> All { get { lock (_lock) return _users.ToList(); } }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Legt einen Benutzer an (oder erneuert seinen Schlüssel) und gibt den neuen Schlüssel einmalig zurück.</summary>
    public string Add(string name, Role role)
    {
        if (!Regex.IsMatch(name, @"^[\p{L}\p{Nd} ._\-]{1,60}$")) throw new ArgumentException("Ungültiger Benutzername.");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        lock (_lock)
        {
            _users.RemoveAll(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            _users.Add(new UserAccount { Name = name, Role = role, TokenHash = Hash(token) });
            Save();
        }
        return token;
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var n = _users.RemoveAll(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            Save();
            return n > 0;
        }
    }

    public UserAccount? Authenticate(string token)
    {
        var hash = Hash(token);
        lock (_lock)
            return _users.FirstOrDefault(u => CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(u.TokenHash), Encoding.ASCII.GetBytes(hash)));
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_users, RemoteApi.Json));
    }
}

/// <summary>
/// Die gemeinsamen Ressourcen. Jede Ressource hat eine eigene Sperre: gleichzeitige Zugriffe vieler Benutzer
/// werden nacheinander abgearbeitet, sodass nichts verloren geht.
/// </summary>
public sealed class ResourceStore : IDisposable
{
    private readonly string _dataDir;
    private readonly ConcurrentDictionary<(ResourceKind, string), (ILanguageResource Resource, object Lock)> _open = new();
    private readonly ConcurrentDictionary<(ResourceKind, string), string> _descriptions = new();

    public ResourceStore(string dataDir)
    {
        _dataDir = dataDir;
        foreach (var kind in Enum.GetValues<ResourceKind>()) Directory.CreateDirectory(Folder(kind));
        var meta = Path.Combine(_dataDir, "beschreibungen.json");
        if (File.Exists(meta))
            foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(meta)) ?? [])
            {
                var parts = kv.Key.Split('|', 2);
                if (parts.Length == 2 && Enum.TryParse<ResourceKind>(parts[0], out var k)) _descriptions[(k, parts[1])] = kv.Value;
            }
    }

    private string Folder(ResourceKind kind) => Path.Combine(_dataDir, kind switch
    {
        ResourceKind.Tm => "tm",
        ResourceKind.Termbase => "terminologie",
        _ => "wissen",
    });

    /// <summary>Nur einfache Namen – verhindert Zugriffe außerhalb des Datenordners.</summary>
    public static bool ValidName(string name) => Regex.IsMatch(name, @"^[\p{L}\p{Nd}][\p{L}\p{Nd} ._\-]{0,79}$") && !name.Contains("..");

    private string FilePath(ResourceKind kind, string name) => Path.Combine(Folder(kind), name + ResourceFactory.Extension(kind));

    public bool Exists(ResourceKind kind, string name) => ValidName(name) && File.Exists(FilePath(kind, name));

    public void Create(ResourceKind kind, string name, string? description)
    {
        if (!ValidName(name)) throw new ArgumentException("Ungültiger Name. Erlaubt: Buchstaben, Ziffern, Leerzeichen, . _ -");
        Get(kind, name, create: true);
        if (!string.IsNullOrWhiteSpace(description))
        {
            _descriptions[(kind, name)] = description;
            File.WriteAllText(Path.Combine(_dataDir, "beschreibungen.json"),
                JsonSerializer.Serialize(_descriptions.ToDictionary(kv => $"{kv.Key.Item1}|{kv.Key.Item2}", kv => kv.Value)));
        }
    }

    public List<ResourceInfo> List()
    {
        var list = new List<ResourceInfo>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
            foreach (var file in Directory.GetFiles(Folder(kind), "*" + ResourceFactory.Extension(kind)))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var count = With<ILanguageResource, long>(kind, name, r => r switch
                {
                    ITranslationMemory tm => tm.Count(),
                    ITermbase tb => tb.ConceptCount(),
                    IKnowledgeBase kb => kb.Count(),
                    _ => 0,
                });
                list.Add(new ResourceInfo(kind, name, _descriptions.GetValueOrDefault((kind, name)), count));
            }
        return list;
    }

    private (ILanguageResource Resource, object Lock) Get(ResourceKind kind, string name, bool create = false)
    {
        if (!ValidName(name)) throw new KeyNotFoundException(name);
        if (!create && !File.Exists(FilePath(kind, name))) throw new KeyNotFoundException(name);
        return _open.GetOrAdd((kind, name), key => (key.Item1 switch
        {
            ResourceKind.Tm => new TranslationMemory(FilePath(kind, name), name),
            ResourceKind.Termbase => new Termbase(FilePath(kind, name), name),
            _ => (ILanguageResource)new KnowledgeBase(FilePath(kind, name), name),
        }, new object()));
    }

    /// <summary>Führt eine Aktion unter der Sperre der Ressource aus.</summary>
    public TResult With<T, TResult>(ResourceKind kind, string name, Func<T, TResult> action) where T : class, ILanguageResource
    {
        var (res, gate) = Get(kind, name);
        lock (gate) return action((T)res);
    }

    public void Dispose()
    {
        foreach (var (res, _) in _open.Values) res.Dispose();
    }
}

/// <summary>Was zuletzt im Team passiert ist – für den Wissensaustausch sichtbar.</summary>
public sealed class ActivityLog
{
    private readonly string _path;
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly object _lock = new();
    private const int Keep = 2000;

    public ActivityLog(string dataDir)
    {
        _path = Path.Combine(dataDir, "aktivitaet.jsonl");
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path).TakeLast(Keep))
            try { if (JsonSerializer.Deserialize<ActivityEntry>(line, RemoteApi.Json) is { } e) _entries.AddFirst(e); }
            catch (JsonException) { }
    }

    public void Add(string user, ResourceKind kind, string resource, string text)
    {
        var e = new ActivityEntry(DateTime.UtcNow, user, kind, resource, text.Length > 200 ? text[..200] + " …" : text);
        lock (_lock)
        {
            _entries.AddFirst(e);
            while (_entries.Count > Keep) _entries.RemoveLast();
            File.AppendAllText(_path, JsonSerializer.Serialize(e, new JsonSerializerOptions(RemoteApi.Json) { WriteIndented = false }) + "\n");
        }
    }

    public List<ActivityEntry> Latest(int max)
    {
        lock (_lock) return _entries.Take(Math.Clamp(max, 1, 500)).ToList();
    }
}
