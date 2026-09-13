using Monitoredb.ApiServer;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.Tests;

public class AppStateTests
{
    private static AppState NewState(string? token = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"monitoredb-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new AppState(token, Path.Combine(dir, "state.json"));
    }

    private static AgentSnapshot MakeSnapshot(string agentId = "agent-1", string hostname = "host-1")
    {
        return new AgentSnapshot
        {
            AgentId = agentId,
            Hostname = hostname,
            Timestamp = DateTime.UtcNow,
            Windows = new WindowsSnapshot
            {
                CpuPercent = 42.5f,
                MemoryPercent = 63.2,
                Disks = [new DiskInfo { Mount = "C:", UsagePercent = 55.0, AvailableGb = 100.0 }],
            },
            SqlServer = new SqlServerSnapshot
            {
                ServerName = "sql-1",
                Performance = new SqlServerPerformance
                {
                    UserConnections = 12,
                    BufferCacheHitRatio = 99.1,
                    PageLifeExpectancy = 3600,
                    DeadlocksPerSec = 0,
                    BatchRequestsPerSec = 500,
                },
            },
        };
    }

    [Fact]
    public void Ingest_AddsAgent_And_HistorySample()
    {
        var state = NewState();
        state.Ingest(MakeSnapshot());

        Assert.Single(state.AllAgents());
        Assert.Single(state.History("agent-1"));
        Assert.NotNull(state.GetAgent("agent-1"));
    }

    [Fact]
    public void Ingest_UpdatesExistingAgent_And_AppendsHistory()
    {
        var state = NewState();
        state.Ingest(MakeSnapshot());
        state.Ingest(MakeSnapshot());

        Assert.Single(state.AllAgents());
        Assert.Equal(2, state.History("agent-1").Count);
    }

    [Fact]
    public void Ingest_History_IsCapped_AtMaxHistory()
    {
        var state = NewState();
        for (var i = 0; i < AppState.MaxHistory + 100; i++)
            state.Ingest(MakeSnapshot());

        Assert.Equal(AppState.MaxHistory, state.History("agent-1").Count);
    }

    [Fact]
    public void Register_AddsAgent_WithHostnameFallback()
    {
        var state = NewState();
        state.Register("agent-x", "host-x");
        state.Register("agent-y", "");

        var agentX = state.GetAgent("agent-x");
        var agentY = state.GetAgent("agent-y");

        Assert.NotNull(agentX);
        Assert.Equal("host-x", agentX!.Hostname);
        Assert.NotNull(agentY);
        Assert.Equal("agent-y", agentY!.Hostname);
    }

    [Fact]
    public void Remove_DeletesAgent_And_History()
    {
        var state = NewState();
        state.Ingest(MakeSnapshot());

        Assert.True(state.Remove("agent-1"));
        Assert.Null(state.GetAgent("agent-1"));
        Assert.Empty(state.History("agent-1"));
        Assert.False(state.Remove("agent-1"));
    }

    [Fact]
    public void Latest_ReturnsMostRecentSnapshot()
    {
        var state = NewState();
        var older = MakeSnapshot() with { Timestamp = DateTime.UtcNow.AddMinutes(-5) };
        var newer = MakeSnapshot() with { Timestamp = DateTime.UtcNow };

        state.Ingest(older);
        state.Ingest(newer);

        Assert.Equal(newer.Timestamp, state.Latest()!.Timestamp);
    }

    [Fact]
    public void Latest_ReturnsNull_WhenEmpty()
    {
        var state = NewState();
        Assert.Null(state.Latest());
    }

    [Fact]
    public void IsAuthorized_ReturnsTrue_WhenNoTokenConfigured()
    {
        var state = NewState();
        Assert.True(state.IsAuthorized(null));
        Assert.True(state.IsAuthorized("Bearer anything"));
    }

    [Fact]
    public void IsAuthorized_ValidatesBearerToken()
    {
        var state = NewState("secret-token");
        Assert.True(state.IsAuthorized("Bearer secret-token"));
        Assert.False(state.IsAuthorized("Bearer wrong-token"));
        Assert.False(state.IsAuthorized(null));
        Assert.False(state.IsAuthorized("secret-token"));
    }

    [Fact]
    public void Save_And_Reload_RestoresState()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"monitoredb-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dataFile = Path.Combine(dir, "state.json");

        try
        {
            var state = new AppState(null, dataFile);
            state.Ingest(MakeSnapshot());
            state.Save();

            var reloaded = new AppState(null, dataFile);
            Assert.Single(reloaded.AllAgents());
            Assert.Single(reloaded.History("agent-1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AllAgents_IsSortedByHostname()
    {
        var state = NewState();
        state.Ingest(MakeSnapshot("b", "zebra"));
        state.Ingest(MakeSnapshot("a", "alpha"));
        state.Ingest(MakeSnapshot("c", "mango"));

        var names = state.AllAgents().Select(a => a.Hostname).ToList();
        Assert.Equal(["alpha", "mango", "zebra"], names);
    }
}