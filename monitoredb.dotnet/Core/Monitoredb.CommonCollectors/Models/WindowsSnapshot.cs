namespace Monitoredb.CommonCollectors.Models;

public sealed record WindowsSnapshot
{
    public float CpuPercent { get; init; }
    public int CoreCount { get; init; }
    public string CpuName { get; init; } = "";
    public double MemoryTotalGb { get; init; }
    public double MemoryUsedGb { get; init; }
    public double MemoryPercent { get; init; }
    public ulong UptimeSeconds { get; init; }
    public string OsVersion { get; init; } = "";
    public List<DiskInfo> Disks { get; init; } = [];
    public List<ProcessInfo> TopProcesses { get; init; } = [];
    public List<ServiceInfo> Services { get; init; } = [];
    public List<EventLogEntry> Events { get; init; } = [];
    public List<NetworkInfo> Network { get; init; } = [];
}

public sealed record DiskInfo
{
    public string Mount { get; init; } = "";
    public double TotalGb { get; init; }
    public double UsedGb { get; init; }
    public double AvailableGb { get; init; }
    public double UsagePercent { get; init; }
}

public sealed record ProcessInfo
{
    public uint Pid { get; init; }
    public string Name { get; init; } = "";
    public float CpuPercent { get; init; }
    public double MemoryMb { get; init; }
}

public sealed record ServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed record EventLogEntry
{
    public string Time { get; init; } = "";
    public string Level { get; init; } = "";
    public string Source { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record NetworkInfo
{
    public string Interface { get; init; } = "";
    public ulong ReceivedBytes { get; init; }
    public ulong TransmittedBytes { get; init; }
}