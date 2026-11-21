namespace SententiamConverte.Testing;

/// <summary>
/// Ein Testlauf legt alles in einem eigenen Temp-Ordner ab. Der wird beim Prozessende gelöscht; was dabei noch offen
/// war (SQLite, Protokoll), räumt der nächste Lauf weg – sonst läuft C: über Hunderte Läufe voll. Ein laufender Lauf
/// hält seine Sperrdatei offen, sein Ordner bleibt also stehen, auch wenn zwei Läufe gleichzeitig arbeiten.
/// SC_KEEP_TEST_DATA=1 behält den Ordner (z. B. um das Ablaufprotokoll eines Live-Tests zu lesen).
/// </summary>
internal static class TempCleanup
{
    private const string LockName = ".laeuft";
    private static FileStream? _lock;

    public static void Start(string root, string prefix)
    {
        _lock = new FileStream(Path.Combine(root, LockName), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            var young = DateTime.UtcNow.AddSeconds(-10);
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), prefix + "*"))
                if (dir != root && Directory.GetCreationTimeUtc(dir) < young && !InUse(dir)) Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (Environment.GetEnvironmentVariable("SC_KEEP_TEST_DATA") is { Length: > 0 }) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _lock?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Delete(root);
        };
    }

    private static bool InUse(string dir)
    {
        var file = Path.Combine(dir, LockName);
        if (!File.Exists(file)) return false;
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
