namespace Monitoredb.WorkerAgent;

/// <summary>Parse de argumentos de linha de comando (compatível com o agente Rust).</summary>
public static class CliParser
{
    public static AgentOptions Parse(string[] args)
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

        var server = Get("--server") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_SERVER") ?? "http://localhost:3000";
        var agentId = Get("--agent-id") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_ID");
        var interval = int.TryParse(Get("--interval") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_INTERVAL"), out var iv) ? iv : 30;
        var token = Get("--token") ?? Environment.GetEnvironmentVariable("MONITOREDB_AGENT_TOKEN");
        var sqlHost = Get("--sql-host") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_HOST");
        var sqlPort = int.TryParse(Get("--sql-port") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_PORT"), out var sp) ? sp : 1433;
        var sqlUser = Get("--sql-user") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_USERNAME");
        var sqlPass = Get("--sql-pass") ?? Environment.GetEnvironmentVariable("MONITOREDB_SQL_PASSWORD");

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