namespace Monitoredb.CommonCollectors.Models;

public sealed record SqlServerSnapshot
{
    public string ServerName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Edition { get; init; } = "";
    public double UptimeHours { get; init; }
    public List<SqlServerDatabase> Databases { get; init; } = [];
    public List<SqlServerConnection> Connections { get; init; } = [];
    public List<SqlServerQuery> RunningQueries { get; init; } = [];
    public List<SqlServerBackup> Backups { get; init; } = [];
    public List<SqlServerJob> Jobs { get; init; } = [];
    public SqlServerPerformance? Performance { get; init; }
}

public sealed record SqlServerDatabase
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public double SizeMb { get; init; }
    public string RecoveryModel { get; init; } = "";
}

public sealed record SqlServerConnection
{
    public int SessionId { get; init; }
    public string Hostname { get; init; } = "";
    public string ProgramName { get; init; } = "";
    public string LoginName { get; init; } = "";
    public string Database { get; init; } = "";
    public string Status { get; init; } = "";
    public long CpuTimeMs { get; init; }
}

public sealed record SqlServerQuery
{
    public int SessionId { get; init; }
    public string Database { get; init; } = "";
    public string User { get; init; } = "";
    public string Status { get; init; } = "";
    public string WaitType { get; init; } = "";
    public long CpuTimeMs { get; init; }
    public long TotalElapsedMs { get; init; }
    public string QueryText { get; init; } = "";
}

public sealed record SqlServerBackup
{
    public string Database { get; init; } = "";
    public string BackupType { get; init; } = "";
    public string? LastBackup { get; init; }
    public double SizeMb { get; init; }
}

public sealed record SqlServerJob
{
    public string Name { get; init; } = "";
    public string? LastRun { get; init; }
    public string LastRunStatus { get; init; } = "";
    public bool Enabled { get; init; }
}

public sealed record SqlServerPerformance
{
    public double BufferCacheHitRatio { get; init; }
    public long PageLifeExpectancy { get; init; }
    public long BatchRequestsPerSec { get; init; }
    public long DeadlocksPerSec { get; init; }
    public int UserConnections { get; init; }
}