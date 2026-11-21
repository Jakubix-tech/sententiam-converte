namespace SententiamConverte.Core.Ai;

/// <summary>Ein Zugang, der mehrere Anfragen gleichzeitig bearbeiten kann (z. B. ein Verbund aus mehreren Modellen).</summary>
public interface IParallelChatModel : IChatModel
{
    /// <summary>Wie viele Anfragen sinnvoll gleichzeitig laufen.</summary>
    int Parallelism { get; }
}

/// <summary>
/// Mehrere Modelle als ein Zugang (Verbund): Jede Anfrage geht an das freie Modell mit den wenigsten laufenden
/// Anfragen, reihum. Stößt ein Modell an die Mengenbegrenzung seines Anbieters, pausiert es so lange wie verlangt und
/// ein anderes übernimmt; fällt eines aus (Fehler, leere Antwort, Zeitüberschreitung), versucht dieselbe Anfrage das
/// nächste. Stapelübersetzungen schicken so viele Anfragen gleichzeitig, wie der Verbund Modelle hat.
/// Gedacht für kostenlose Stufen mit engen Grenzen (z. B. 2 Anfragen pro Minute und Modell).
/// </summary>
public sealed class PooledChatModel : IToolChatModel, IParallelChatModel
{
    private sealed class Member(AiProviderConfig config, IChatModel model)
    {
        public AiProviderConfig Config { get; } = config;
        public IChatModel Model { get; } = model;
        public int Active;
        public int Done;
        public int Failed;
        public bool NoTools;
        public DateTime PausedUntil;
    }

    private readonly List<Member> _members;
    private readonly object _lock = new();
    private readonly string _name;
    private int _next;

    /// <summary>Längste Wartezeit, wenn gerade alle Modelle pausieren.</summary>
    internal static TimeSpan MaxWait = TimeSpan.FromSeconds(90);

    /// <summary>Pause nach einem Fehler, der keine Mengenbegrenzung ist.</summary>
    internal static TimeSpan FailurePause = TimeSpan.FromSeconds(30);

    /// <summary>Meldungen wie „Modell X ausgelastet – weiter mit Y“ (für das Protokoll).</summary>
    public Action<string>? Report { get; set; }

    public PooledChatModel(AiProviderConfig config, Func<AiProviderConfig, IChatModel>? create = null)
    {
        _name = config.Name;
        create ??= ChatModelFactory.CreateRaw;
        _members = (config.Members ?? [])
            .Where(m => m.Kind != AiProviderKind.Pool)
            .Select(m =>
            {
                var copy = m.Clone();
                copy.FailFast = true;              // bei Mengenbegrenzung nicht warten, sondern ein anderes Modell nehmen
                return new Member(copy, create(copy));
            }).ToList();
        if (_members.Count == 0) throw new AiException($"{config.Name}: Der Verbund enthält keine Modelle.");
    }

    public int Parallelism => _members.Count;

    public string DisplayName => $"{_name} ({_members.Count} Modelle)";

    /// <summary>Wie viele Antworten jedes Modell geliefert hat, z. B. für die Statuszeile.</summary>
    public IReadOnlyList<(string Name, int Done, int Failed)> Usage
    {
        get
        {
            lock (_lock) return _members.Select(m => (m.Config.Name, m.Done, m.Failed)).ToList();
        }
    }

    public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default) =>
        RunAsync(m => m.CompleteAsync(system, messages, ct), tools: false, ct);

    public Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default) =>
        RunAsync(m => m is IToolChatModel t ? t.CompleteWithToolsAsync(system, messages, tools, ct) : throw new NotSupportedException(), tools: true, ct);

    private async Task<T> RunAsync<T>(Func<IChatModel, Task<T>> call, bool tools, CancellationToken ct)
    {
        var tried = new HashSet<Member>();
        Exception? last = null;
        for (var attempt = 0; attempt < _members.Count * 4 + 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            Member? member = null;
            var wait = TimeSpan.Zero;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var candidates = _members.Where(m => !tried.Contains(m) && !(tools && m.NoTools)).ToList();
                var ready = candidates.Where(m => m.PausedUntil <= now).ToList();
                if (ready.Count > 0)
                {
                    // Die wenigsten laufenden Anfragen zuerst, bei Gleichstand reihum
                    member = ready.OrderBy(m => m.Active)
                        .ThenBy(m => (_members.IndexOf(m) - _next + _members.Count) % _members.Count).First();
                    _next = (_members.IndexOf(member) + 1) % _members.Count;
                    member.Active++;
                }
                else if (candidates.Count > 0) wait = candidates.Min(m => m.PausedUntil) - now;
            }
            if (member == null)
            {
                if (wait > TimeSpan.Zero && wait <= MaxWait)
                {
                    await Task.Delay(wait, ct);                 // alle pausieren gerade: auf das erste freie warten
                    continue;
                }
                break;
            }
            try
            {
                var result = await call(member.Model);
                lock (_lock) member.Done++;
                return result;
            }
            catch (NotSupportedException) when (tools)
            {
                lock (_lock) member.NoTools = true;
                tried.Add(member);
                last ??= new NotSupportedException();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is AiException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                last = ex;
                var limit = ex as RateLimitException;
                lock (_lock)
                {
                    member.Failed++;
                    member.PausedUntil = DateTime.UtcNow + (limit != null ? limit.RetryAfter ?? TimeSpan.FromSeconds(60) : FailurePause);
                }
                if (limit == null) tried.Add(member);        // echter Fehler: diese Anfrage nicht noch einmal an dasselbe Modell
                Report?.Invoke(limit != null
                    ? $"↪ {member.Config.Name}: ausgelastet – ein anderes Modell übernimmt"
                    : $"↪ {member.Config.Name}: {ex.Message} – ein anderes Modell übernimmt");
            }
            finally
            {
                lock (_lock) member.Active--;
            }
        }
        if (last is NotSupportedException && tools) throw new NotSupportedException("Kein Modell im Verbund nimmt Werkzeuge an.");
        throw last as AiException
              ?? new AiException($"{_name}: Kein Modell im Verbund hat geantwortet" + (last != null ? $" ({last.Message})." : "."), last);
    }
}
