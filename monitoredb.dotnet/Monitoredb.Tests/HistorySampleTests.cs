using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.Tests;

public class HistorySampleTests
{
    [Fact]
    public void FromSnapshot_MapsAllFields()
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "a1",
            Hostname = "h1",
            Timestamp = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
            Windows = new WindowsSnapshot
            {
                CpuPercent = 33.3f,
                MemoryPercent = 50.0,
                Disks =
                [
                    new DiskInfo { UsagePercent = 40.0 },
                    new DiskInfo { UsagePercent = 60.0 },
                ],
            },
            Iis = new IisSnapshot
            {
                ActiveRequests = [new IisRequest { Url = "/" }, new IisRequest { Url = "/api" }],
            },
            SqlServer = new SqlServerSnapshot
            {
                Performance = new SqlServerPerformance
                {
                    UserConnections = 7,
                    BufferCacheHitRatio = 98.5,
                    PageLifeExpectancy = 1234,
                    DeadlocksPerSec = 2,
                    BatchRequestsPerSec = 999,
                },
            },
        };

        var sample = HistorySample.FromSnapshot(snapshot);

        Assert.Equal(snapshot.Timestamp, sample.Timestamp);
        Assert.Equal(33.3f, sample.CpuPercent);
        Assert.Equal(50.0, sample.MemoryPercent);
        Assert.Equal(50.0, sample.DiskAvgPercent); // média de 40 e 60
        Assert.Equal(2, sample.IisActiveRequests);
        Assert.Equal(7, sample.SqlUserConnections);
        Assert.Equal(98.5, sample.SqlBufferCacheHitRatio);
        Assert.Equal(1234, sample.SqlPageLifeExpectancy);
        Assert.Equal(2, sample.SqlDeadlocksPerSec);
        Assert.Equal(999, sample.SqlBatchRequestsPerSec);
    }

    [Fact]
    public void FromSnapshot_HandlesNullSections()
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "a1",
            Hostname = "h1",
            Timestamp = DateTime.UtcNow,
        };

        var sample = HistorySample.FromSnapshot(snapshot);

        Assert.Null(sample.CpuPercent);
        Assert.Null(sample.MemoryPercent);
        Assert.Equal(0.0, sample.DiskAvgPercent);
        Assert.Null(sample.IisActiveRequests);
        Assert.Null(sample.SqlUserConnections);
    }

    [Fact]
    public void FromSnapshot_EmptyDisks_ReturnsZeroAvg()
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "a1",
            Hostname = "h1",
            Timestamp = DateTime.UtcNow,
            Windows = new WindowsSnapshot { Disks = [] },
        };

        var sample = HistorySample.FromSnapshot(snapshot);

        Assert.Equal(0.0, sample.DiskAvgPercent);
    }

    [Fact]
    public void FromSnapshot_SqlServerWithoutPerformance_ReturnsNulls()
    {
        var snapshot = new AgentSnapshot
        {
            AgentId = "a1",
            Hostname = "h1",
            Timestamp = DateTime.UtcNow,
            SqlServer = new SqlServerSnapshot { ServerName = "sql" },
        };

        var sample = HistorySample.FromSnapshot(snapshot);

        Assert.Null(sample.SqlUserConnections);
        Assert.Null(sample.SqlBufferCacheHitRatio);
    }
}