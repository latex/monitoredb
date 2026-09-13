namespace Monitoredb.CommonCollectors.Models;

/// <summary>Amostra de histórico derivada de um snapshot para gráficos de tendência.</summary>
public sealed record HistorySample
{
    public DateTime Timestamp { get; init; }
    public float? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskAvgPercent { get; init; }
    public int? IisActiveRequests { get; init; }
    public int? SqlUserConnections { get; init; }
    public double? SqlBufferCacheHitRatio { get; init; }
    public long? SqlPageLifeExpectancy { get; init; }
    public long? SqlDeadlocksPerSec { get; init; }
    public long? SqlBatchRequestsPerSec { get; init; }

    public static HistorySample FromSnapshot(AgentSnapshot s)
    {
        var perf = s.SqlServer?.Performance;
        return new HistorySample
        {
            Timestamp = s.Timestamp,
            CpuPercent = s.Windows?.CpuPercent,
            MemoryPercent = s.Windows?.MemoryPercent,
            DiskAvgPercent = s.Windows is { Disks.Count: > 0 } w
                ? w.Disks.Average(d => d.UsagePercent)
                : 0.0,
            IisActiveRequests = s.Iis?.ActiveRequests.Count,
            SqlUserConnections = perf?.UserConnections,
            SqlBufferCacheHitRatio = perf?.BufferCacheHitRatio,
            SqlPageLifeExpectancy = perf?.PageLifeExpectancy,
            SqlDeadlocksPerSec = perf?.DeadlocksPerSec,
            SqlBatchRequestsPerSec = perf?.BatchRequestsPerSec,
        };
    }
}