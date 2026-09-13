using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.Tests;

/// <summary>Factory com data file isolado em diretório temporário (evita colisão com estado real).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DataFile { get; } = Path.Combine(Path.GetTempPath(), $"monitoredb-test-{Guid.NewGuid():N}", "state.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("MONITOREDB_DATA_FILE", DataFile);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var dir = Path.GetDirectoryName(DataFile);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}

public class ApiIntegrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiIntegrationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions SnakeCaseOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private HttpClient CreateClient(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static StringContent ToSnakeJson(object value) =>
        new(JsonSerializer.Serialize(value, SnakeCaseOpts), Encoding.UTF8, "application/json");

    private static AgentSnapshot MakeSnapshot(string agentId = "test-agent")
    {
        return new AgentSnapshot
        {
            AgentId = agentId,
            Hostname = "test-host",
            Timestamp = DateTime.UtcNow,
            Windows = new WindowsSnapshot { CpuPercent = 10.0f, MemoryPercent = 20.0 },
            SqlServer = new SqlServerSnapshot
            {
                ServerName = "sql-test",
                Performance = new SqlServerPerformance { UserConnections = 5 },
            },
        };
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task Ingest_Then_GetAgent_ReturnsSnapshot()
    {
        var client = CreateClient();
        var ingestResp = await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot()));
        Assert.Equal(HttpStatusCode.OK, ingestResp.StatusCode);

        var agentResp = await client.GetAsync("/api/agent/test-agent");
        Assert.Equal(HttpStatusCode.OK, agentResp.StatusCode);
        var body = await agentResp.Content.ReadAsStringAsync();
        Assert.Contains("\"agent_id\":\"test-agent\"", body);
        Assert.Contains("\"hostname\":\"test-host\"", body);
    }

    [Fact]
    public async Task Ingest_Then_Agents_ShowsAgent()
    {
        var client = CreateClient();
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("agent-list")));

        var resp = await client.GetAsync("/api/agents");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"agent_id\":\"agent-list\"", body);
        Assert.Contains("\"has_windows\":true", body);
        Assert.Contains("\"has_sql_server\":true", body);
        Assert.Contains("\"has_iis\":false", body);
    }

    [Fact]
    public async Task Ingest_Then_History_ReturnsSamples()
    {
        var client = CreateClient();
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("agent-hist")));
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("agent-hist")));

        var resp = await client.GetAsync("/api/agent/agent-hist/history");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("\"agent_id\":\"agent-hist\"", body);
        Assert.Contains("\"samples\":[", body);
        Assert.Contains("\"cpu_percent\":10", body);
    }

    [Fact]
    public async Task GetAgent_NotFound_Returns404()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/api/agent/nao-existe");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Latest_ReturnsMostRecent()
    {
        var client = CreateClient();
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("latest-agent")));

        var resp = await client.GetAsync("/api/latest");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"agent_id\":\"latest-agent\"", body);
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusText()
    {
        var client = CreateClient();
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("metrics-agent")));

        var resp = await client.GetAsync("/api/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", resp.Content.Headers.ContentType?.ToString());

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("# HELP monitoredb_up", body);
        Assert.Contains("monitoredb_up{agent=\"metrics-agent\",hostname=\"test-host\"} 1", body);
        Assert.Contains("monitoredb_cpu_percent{agent=\"metrics-agent\",hostname=\"test-host\"} 10.00", body);
    }

    [Fact]
    public async Task RegisterAgent_Then_List()
    {
        var client = CreateClient();
        var payload = new { agent_id = "registered-agent", hostname = "reg-host" };
        var resp = await client.PostAsync("/api/agents", ToSnakeJson(payload));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var listResp = await client.GetAsync("/api/agents");
        var body = await listResp.Content.ReadAsStringAsync();
        Assert.Contains("\"agent_id\":\"registered-agent\"", body);
        Assert.Contains("\"hostname\":\"reg-host\"", body);
    }

    [Fact]
    public async Task RegisterAgent_MissingId_Returns400()
    {
        var client = CreateClient();
        var payload = new { hostname = "no-id" };
        var resp = await client.PostAsync("/api/agents", ToSnakeJson(payload));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteAgent_RemovesAgent()
    {
        var client = CreateClient();
        await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("delete-me")));

        var delResp = await client.DeleteAsync("/api/agent/delete-me");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

        var getResp = await client.GetAsync("/api/agent/delete-me");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [Fact]
    public async Task DeleteAgent_NotFound_Returns404()
    {
        var client = CreateClient();
        var resp = await client.DeleteAsync("/api/agent/nao-existe");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Ingest_WithoutToken_Returns401_WhenTokenConfigured()
    {
        // Factory com token configurado e data file isolado
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("MONITOREDB_TOKEN", "secret-token");
            b.UseSetting("MONITOREDB_DATA_FILE", Path.Combine(Path.GetTempPath(), $"monitoredb-token-{Guid.NewGuid():N}", "state.json"));
        });
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("no-token")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Ingest_WithValidToken_Succeeds()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("MONITOREDB_TOKEN", "secret-token");
            b.UseSetting("MONITOREDB_DATA_FILE", Path.Combine(Path.GetTempPath(), $"monitoredb-token-{Guid.NewGuid():N}", "state.json"));
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "secret-token");

        var resp = await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("with-token")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ingest_WithWrongToken_Returns401()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("MONITOREDB_TOKEN", "secret-token");
            b.UseSetting("MONITOREDB_DATA_FILE", Path.Combine(Path.GetTempPath(), $"monitoredb-token-{Guid.NewGuid():N}", "state.json"));
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");

        var resp = await client.PostAsync("/api/ingest", ToSnakeJson(MakeSnapshot("wrong-token")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_IsServed()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("MonitoreDB", body);
    }

    [Fact]
    public async Task Ingest_SnakeCaseJson_IsAccepted()
    {
        var client = CreateClient();
        var json = """
        {
          "agent_id": "snake-agent",
          "hostname": "snake-host",
          "timestamp": "2026-09-13T10:00:00Z",
          "windows": {
            "cpu_percent": 15.5,
            "memory_percent": 30.0
          }
        }
        """;

        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/ingest", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var agentResp = await client.GetAsync("/api/agent/snake-agent");
        var body = await agentResp.Content.ReadAsStringAsync();
        Assert.Contains("\"cpu_percent\":15.5", body);
    }
}