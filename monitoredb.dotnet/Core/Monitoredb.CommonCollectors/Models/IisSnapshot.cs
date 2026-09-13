namespace Monitoredb.CommonCollectors.Models;

public sealed record IisSnapshot
{
    public List<IisRequest> ActiveRequests { get; init; } = [];
    public List<IisAppPool> AppPools { get; init; } = [];
    public string ServerName { get; init; } = "";
}

public sealed record IisRequest
{
    public string Url { get; init; } = "";
    public string Verb { get; init; } = "";
    public string ClientIp { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Module { get; init; } = "";
    public long? TimeMs { get; init; }
    public int? SiteId { get; init; }
    public string AppPool { get; init; } = "";
    public uint? Pid { get; init; }
    public float? CpuPercent { get; init; }
    public double? MemoryMb { get; init; }
}

public sealed record IisAppPool
{
    public string Name { get; init; } = "";
    public uint Pid { get; init; }
    public float CpuPercent { get; init; }
    public double MemoryMb { get; init; }
    public double? UptimeMinutes { get; init; }
    public string State { get; init; } = "";
    public int? ActiveRequests { get; init; }
}