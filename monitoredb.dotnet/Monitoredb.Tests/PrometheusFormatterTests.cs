using Monitoredb.ApiServer;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.Tests;

public class PrometheusFormatterTests
{
    private static AgentSnapshot MakeFullSnapshot()
    {
        return new AgentSnapshot
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Timestamp = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
            Windows = new WindowsSnapshot
            {
                CpuPercent = 42.5f,
                MemoryPercent = 63.2,
                Disks =
                [
                    new DiskInfo { Mount = "C:", UsagePercent = 55.0, AvailableGb = 100.0 },
                    new DiskInfo { Mount = "D:", UsagePercent = 80.0, AvailableGb = 20.0 },
                ],
                Services = [new ServiceInfo { Name = "W3SVC", Status = "Running" }],
            },
            Iis = new IisSnapshot
            {
                ActiveRequests = [new IisRequest { Url = "/" }],
                AppPools =
                [
                    new IisAppPool { Name = "DefaultAppPool", Pid = 123, CpuPercent = 5.5f, MemoryMb = 200.0, ActiveRequests = 3 },
                ],
            },
            SqlServer = new SqlServerSnapshot
            {
                ServerName = "sql-1",
                Databases = [new SqlServerDatabase { Name = "monitoredb", SizeMb = 16.0 }],
                Jobs = [new SqlServerJob { Name = "BackupJob", LastRunStatus = "Succeeded" }],
                Performance = new SqlServerPerformance
                {
                    UserConnections = 12,
                    BufferCacheHitRatio = 99.1,
                    PageLifeExpectancy = 3600,
                },
            },
        };
    }

    [Fact]
    public void Format_IncludesHelpAndTypeLines()
    {
        var output = PrometheusFormatter.Format([MakeFullSnapshot()]);

        Assert.Contains("# HELP monitoredb_up 1 if agent has collected data", output);
        Assert.Contains("# TYPE monitoredb_up gauge", output);
        Assert.Contains("# TYPE monitoredb_cpu_percent gauge", output);
        Assert.Contains("# TYPE monitoredb_sqlserver_connections gauge", output);
    }

    [Fact]
    public void Format_IncludesAgentLabels()
    {
        var output = PrometheusFormatter.Format([MakeFullSnapshot()]);

        Assert.Contains("agent=\"agent-1\",hostname=\"host-1\"", output);
        Assert.Contains("monitoredb_up{agent=\"agent-1\",hostname=\"host-1\"} 1", output);
    }

    [Fact]
    public void Format_IncludesWindowsMetrics()
    {
        var output = PrometheusFormatter.Format([MakeFullSnapshot()]);

        Assert.Contains("monitoredb_cpu_percent{agent=\"agent-1\",hostname=\"host-1\"} 42.50", output);
        Assert.Contains("monitoredb_memory_percent{agent=\"agent-1\",hostname=\"host-1\"} 63.20", output);
        Assert.Contains("monitoredb_disk_usage_percent{agent=\"agent-1\",hostname=\"host-1\",mount=\"C\"} 55.00", output);
        Assert.Contains("monitoredb_disk_available_gb{agent=\"agent-1\",hostname=\"host-1\",mount=\"D\"} 20.00", output);
        Assert.Contains("monitoredb_service_status{agent=\"agent-1\",hostname=\"host-1\",name=\"W3SVC\"} 1", output);
    }

    [Fact]
    public void Format_IncludesIisMetrics()
    {
        var output = PrometheusFormatter.Format([MakeFullSnapshot()]);

        Assert.Contains("monitoredb_iis_active_requests{agent=\"agent-1\",hostname=\"host-1\"} 1", output);
        Assert.Contains("monitoredb_iis_app_pool_cpu{agent=\"agent-1\",hostname=\"host-1\",pool=\"DefaultAppPool\"} 5.50", output);
        Assert.Contains("monitoredb_iis_app_pool_memory_mb{agent=\"agent-1\",hostname=\"host-1\",pool=\"DefaultAppPool\"} 200.00", output);
        Assert.Contains("monitoredb_iis_app_pool_active_requests{agent=\"agent-1\",hostname=\"host-1\",pool=\"DefaultAppPool\"} 3", output);
    }

    [Fact]
    public void Format_IncludesSqlServerMetrics()
    {
        var output = PrometheusFormatter.Format([MakeFullSnapshot()]);

        Assert.Contains("monitoredb_sqlserver_connections{agent=\"agent-1\",hostname=\"host-1\"} 12", output);
        Assert.Contains("monitoredb_sqlserver_buffer_cache_hit_ratio{agent=\"agent-1\",hostname=\"host-1\"} 99.10", output);
        Assert.Contains("monitoredb_sqlserver_page_life_expectancy{agent=\"agent-1\",hostname=\"host-1\"} 3600", output);
        Assert.Contains("monitoredb_database_size_mb{agent=\"agent-1\",hostname=\"host-1\",database=\"monitoredb\"} 16.00", output);
        Assert.Contains("monitoredb_job_status{agent=\"agent-1\",hostname=\"host-1\",name=\"BackupJob\"} 1", output);
    }

    [Fact]
    public void Format_EmptyAgents_ReturnsOnlyHeaders()
    {
        var output = PrometheusFormatter.Format([]);

        Assert.Contains("# HELP monitoredb_up", output);
        Assert.DoesNotContain("monitoredb_up{", output);
    }

    [Fact]
    public void Format_StoppedService_ReturnsZero()
    {
        var snapshot = MakeFullSnapshot();
        snapshot = snapshot with
        {
            Windows = snapshot.Windows! with
            {
                Services = [new ServiceInfo { Name = "StoppedSvc", Status = "Stopped" }],
            },
        };

        var output = PrometheusFormatter.Format([snapshot]);

        Assert.Contains("monitoredb_service_status{agent=\"agent-1\",hostname=\"host-1\",name=\"StoppedSvc\"} 0", output);
    }

    [Fact]
    public void Format_FailedJob_ReturnsZero()
    {
        var snapshot = MakeFullSnapshot();
        snapshot = snapshot with
        {
            SqlServer = snapshot.SqlServer! with
            {
                Jobs = [new SqlServerJob { Name = "BadJob", LastRunStatus = "Failed" }],
            },
        };

        var output = PrometheusFormatter.Format([snapshot]);

        Assert.Contains("monitoredb_job_status{agent=\"agent-1\",hostname=\"host-1\",name=\"BadJob\"} 0", output);
    }

    [Fact]
    public void Format_EscapesQuotesAndBackslashes()
    {
        var snapshot = MakeFullSnapshot();
        snapshot = snapshot with
        {
            AgentId = "agent\"with\\quote",
        };

        var output = PrometheusFormatter.Format([snapshot]);

        Assert.Contains("agent=\"agent\\\"with\\\\quote\"", output);
    }

    [Fact]
    public void Format_AgentWithoutData_ReturnsUpZero()
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "empty-agent",
            Hostname = "empty-host",
            Timestamp = DateTime.UtcNow,
        };

        var output = PrometheusFormatter.Format([snapshot]);

        Assert.Contains("monitoredb_up{agent=\"empty-agent\",hostname=\"empty-host\"} 0", output);
    }
}