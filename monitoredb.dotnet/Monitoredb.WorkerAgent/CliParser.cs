using Microsoft.Extensions.Configuration;

namespace Monitoredb.WorkerAgent;

/// <summary>Parse de argumentos de linha de comando (compatível com o agente Rust).
/// Ordem de precedência: flags CLI > appsettings.json (Monitoredb:*) > variáveis MONITOREDB_*.</summary>
public static class CliParser
{
    public static AgentOptions Parse(string[] args, IConfiguration? config = null)
    {
        string? Get(string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == key)
                    return args[i + 1];
            }
            return null;
        }

        string? Cfg(string key) => string.IsNullOrWhiteSpace(config?[key]) ? null : config![key];

        var server = Get("--server") ?? Cfg("Monitoredb:Server") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_SERVER") ?? "http://localhost:3000";
        var agentId = Get("--agent-id") ?? Cfg("Monitoredb:AgentId") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_ID");
        var intervalRaw = Get("--interval") ?? Cfg("Monitoredb:Interval") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_INTERVAL");
        var interval = int.TryParse(intervalRaw, out var iv) ? iv : 30;
        var token = Get("--token") ?? Cfg("Monitoredb:Token") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_TOKEN");
        var sqlHost = Get("--sql-host") ?? Cfg("Monitoredb:SqlHost") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_HOST");
        var sqlPortRaw = Get("--sql-port") ?? Cfg("Monitoredb:SqlPort") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_PORT");
        var sqlPort = int.TryParse(sqlPortRaw, out var sp) ? sp : 1433;
        var sqlUser = Get("--sql-user") ?? Cfg("Monitoredb:SqlUser") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_USERNAME");
        var sqlPass = Get("--sql-pass") ?? Get("--sql-password") ?? Cfg("Monitoredb:SqlPassword") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_PASSWORD");

        return new AgentOptions
        {
            Server = server,
            AgentId = agentId,
            IntervalSeconds = interval,
            Token = token,
            SqlHost = sqlHost,
            SqlPort = sqlPort,
            SqlUser = sqlUser,
            SqlPassword = sqlPass,
            Once = args.Contains("--once"),
        };
    }
}
