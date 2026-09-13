namespace Monitoredb.CommonCollectors.Models;

/// <summary>Snapshot completo enviado pelo agente ao servidor.</summary>
public sealed record AgentSnapshot
{
    public string AgentId { get; init; } = "";
    public string Hostname { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public WindowsSnapshot? Windows { get; init; }
    public IisSnapshot? Iis { get; init; }
    public SqlServerSnapshot? SqlServer { get; init; }
}