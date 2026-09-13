using System.Collections.Concurrent;
using System.Text.Json;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.ApiServer;

/// <summary>Estado em memória do servidor: agentes, histórico e persistência em JSON.</summary>
public sealed class AppState
{
    public const int MaxHistory = 2880;
    private const string DataDir = "data";
    private const string DataFile = "data/state.json";

    private readonly ConcurrentDictionary<string, AgentSnapshot> _agents = new();
    private readonly ConcurrentDictionary<string, Queue<HistorySample>> _history = new();
    private readonly object _lock = new();
    private readonly string _dataFile;
    private bool _dirty;

    public string? Token { get; }

    public AppState(string? token, string? dataFile = null)
    {
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        _dataFile = dataFile ?? Environment.GetEnvironmentVariable("MONITOREDB_DATA_FILE") ?? DataFile;
        Load();
    }

    public void Ingest(AgentSnapshot snapshot)
    {
        var id = snapshot.AgentId;
        var queue = _history.GetOrAdd(id, _ => new Queue<HistorySample>());
        lock (_lock)
        {
            queue.Enqueue(HistorySample.FromSnapshot(snapshot));
            while (queue.Count > MaxHistory)
                queue.Dequeue();
            _agents[id] = snapshot;
            _dirty = true;
        }
    }

    public void Register(string agentId, string hostname)
    {
        _agents.TryAdd(agentId, new AgentSnapshot
        {
            AgentId = agentId,
            Hostname = string.IsNullOrEmpty(hostname) ? agentId : hostname,
            Timestamp = DateTime.UtcNow,
        });
        lock (_lock)
            _dirty = true;
    }

    public bool Remove(string agentId)
    {
        _history.TryRemove(agentId, out _);
        lock (_lock)
            _dirty = true;
        return _agents.TryRemove(agentId, out _);
    }

    public IReadOnlyList<HistorySample> History(string agentId) =>
        _history.TryGetValue(agentId, out var q) ? q.ToList() : [];

    public AgentSnapshot? Latest() =>
        _agents.Values.OrderByDescending(a => a.Timestamp).FirstOrDefault();

    public IReadOnlyList<AgentSnapshot> AllAgents() =>
        _agents.Values.OrderBy(a => a.Hostname).ToList();

    public AgentSnapshot? GetAgent(string id) =>
        _agents.TryGetValue(id, out var a) ? a : null;

    public bool IsAuthorized(string? authHeader)
    {
        if (Token is null)
            return true;
        return authHeader == $"Bearer {Token}";
    }

    public void Save()
    {
        lock (_lock)
        {
            var payload = new PersistedState
            {
                Agents = _agents.Values.ToList(),
                History = _history.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            };
            var dir = Path.GetDirectoryName(_dataFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            File.WriteAllText(_dataFile, json);
            _dirty = false;
        }
    }

    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty)
                return;
        }
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_dataFile))
                return;
            var json = File.ReadAllText(_dataFile);
            var persisted = JsonSerializer.Deserialize<PersistedState>(json, JsonOpts);
            if (persisted is null)
                return;
            foreach (var a in persisted.Agents)
                _agents[a.AgentId] = a;
            foreach (var (id, samples) in persisted.History)
                _history[id] = new Queue<HistorySample>(samples);
            Console.WriteLine($"[server] estado restaurado de {_dataFile} ({_agents.Count} agentes)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[server] falha ao carregar {_dataFile}: {ex.Message}");
        }
    }

    private sealed record PersistedState
    {
        public List<AgentSnapshot> Agents { get; init; } = [];
        public Dictionary<string, List<HistorySample>> History { get; init; } = [];
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}