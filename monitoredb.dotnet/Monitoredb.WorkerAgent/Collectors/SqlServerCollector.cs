using Microsoft.Data.SqlClient;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.CommonCollectors.Collectors;

/// <summary>Coleta métricas do SQL Server via Microsoft.Data.SqlClient (sem depender de sqlcmd).</summary>
public static class SqlServerCollector
{
    public static SqlServerSnapshot? Collect(string host, int port, string? username, string? password)
    {
        var csb = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        };
        if (!string.IsNullOrEmpty(username))
        {
            csb.UserID = username;
            csb.Password = password ?? "";
        }
        else
        {
            csb.IntegratedSecurity = true;
        }

        try
        {
            using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();

            var version = Scalar(conn, "SELECT @@VERSION")?.ToString()?.Trim() ?? "";
            if (version.Length == 0)
                return null;

            var edition = Scalar(conn, "SELECT SERVERPROPERTY('Edition')")?.ToString()?.Trim() ?? "";
            var uptime = Scalar(conn,
                "SELECT CAST(DATEDIFF(MINUTE, sqlserver_start_time, GETDATE()) AS FLOAT) / 60 FROM sys.dm_os_sys_info") is double u
                ? u
                : 0.0;

            return new SqlServerSnapshot
            {
                ServerName = host,
                Version = version,
                Edition = edition,
                UptimeHours = uptime,
                Databases = CollectDatabases(conn),
                Connections = CollectConnections(conn),
                RunningQueries = CollectQueries(conn),
                Backups = CollectBackups(conn),
                Jobs = CollectJobs(conn),
                Performance = CollectPerformance(conn),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sqlserver] falha na coleta: {ex.Message}");
            return null;
        }
    }

    private static object? Scalar(SqlConnection conn, string sql)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        return cmd.ExecuteScalar();
    }

    private static List<T> Query<T>(SqlConnection conn, string sql, Func<SqlDataReader, T> map)
    {
        var list = new List<T>();
        try
        {
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(map(reader));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sqlserver] query falhou: {ex.Message}");
        }
        return list;
    }

    private static string Str(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static double Dbl(SqlDataReader r, int i) => r.IsDBNull(i) ? 0.0 : Convert.ToDouble(r.GetValue(i));
    private static long Lng(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
    private static int Int(SqlDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));

    private static List<SqlServerDatabase> CollectDatabases(SqlConnection conn) => Query(conn, """
        SELECT d.name, d.state_desc,
               CAST(SUM(CAST(mf.size AS BIGINT))*8.0/1024.0 AS DECIMAL(18,2)) AS size_mb,
               d.recovery_model_desc
        FROM sys.databases d
        LEFT JOIN sys.master_files mf ON d.database_id = mf.database_id
        WHERE d.database_id > 4
        GROUP BY d.name, d.state_desc, d.recovery_model_desc
        ORDER BY d.name
        """, r => new SqlServerDatabase
        {
            Name = r.GetString(0),
            State = r.GetString(1),
            SizeMb = Dbl(r, 2),
            RecoveryModel = r.GetString(3),
        });

    private static List<SqlServerConnection> CollectConnections(SqlConnection conn) => Query(conn, """
        SELECT s.session_id, ISNULL(s.host_name,''), ISNULL(s.program_name,''),
               ISNULL(s.login_name,''), ISNULL(DB_NAME(s.database_id),''),
               s.status, s.cpu_time
        FROM sys.dm_exec_sessions s
        WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
        ORDER BY s.cpu_time DESC
        """, r => new SqlServerConnection
        {
            SessionId = Int(r, 0),
            Hostname = Str(r, 1),
            ProgramName = Str(r, 2),
            LoginName = Str(r, 3),
            Database = Str(r, 4),
            Status = Str(r, 5),
            CpuTimeMs = Lng(r, 6),
        });

    private static List<SqlServerQuery> CollectQueries(SqlConnection conn) => Query(conn, """
        SELECT r.session_id, ISNULL(DB_NAME(r.database_id),''),
               ISNULL(s.login_name,''), r.status, ISNULL(r.wait_type,''),
               r.cpu_time, r.total_elapsed_time,
               ISNULL((SELECT TOP 1 SUBSTRING(text,(r.statement_start_offset/2)+1,
                       CASE WHEN r.statement_end_offset=-1 THEN 4000
                            ELSE (r.statement_end_offset-r.statement_start_offset)/2 END)
                       FROM sys.dm_exec_sql_text(r.sql_handle)),'')
        FROM sys.dm_exec_requests r
        JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
        WHERE s.is_user_process = 1 AND r.session_id <> @@SPID
        ORDER BY r.cpu_time DESC
        """, r => new SqlServerQuery
        {
            SessionId = Int(r, 0),
            Database = Str(r, 1),
            User = Str(r, 2),
            Status = Str(r, 3),
            WaitType = Str(r, 4),
            CpuTimeMs = Lng(r, 5),
            TotalElapsedMs = Lng(r, 6),
            QueryText = Str(r, 7),
        });

    private static List<SqlServerBackup> CollectBackups(SqlConnection conn) => Query(conn, """
        SELECT d.name,
               CASE bs.type WHEN 'D' THEN 'FULL' WHEN 'I' THEN 'DIFF' WHEN 'L' THEN 'LOG' ELSE 'OTHER' END,
               ISNULL(CONVERT(VARCHAR,MAX(bs.backup_finish_date),120),''),
               ISNULL(CAST(MAX(bs.backup_size)/1024.0/1024.0 AS DECIMAL(18,2)),0)
        FROM sys.databases d
        LEFT JOIN msdb.dbo.backupset bs ON d.name = bs.database_name
        WHERE d.database_id > 4
        GROUP BY d.name, bs.type
        ORDER BY d.name
        """, r => new SqlServerBackup
        {
            Database = r.GetString(0),
            BackupType = Str(r, 1),
            LastBackup = r.IsDBNull(2) ? null : r.GetString(2),
            SizeMb = Dbl(r, 3),
        });

    private static List<SqlServerJob> CollectJobs(SqlConnection conn) => Query(conn, """
        SELECT j.name,
               ISNULL(CONVERT(VARCHAR,MAX(jh.run_date),120),''),
               ISNULL(CASE jh.run_status WHEN 1 THEN 'Succeeded' WHEN 0 THEN 'Failed'
                      WHEN 2 THEN 'Retry' WHEN 3 THEN 'Cancelled' ELSE 'Unknown' END,'Never'),
               j.enabled
        FROM msdb.dbo.sysjobs j
        LEFT JOIN msdb.dbo.sysjobhistory jh ON j.job_id = jh.job_id
            AND jh.instance_id = (SELECT MAX(instance_id) FROM msdb.dbo.sysjobhistory WHERE job_id = j.job_id)
        GROUP BY j.name, j.enabled, jh.run_status
        ORDER BY j.name
        """, r => new SqlServerJob
        {
            Name = r.GetString(0),
            LastRun = r.IsDBNull(1) ? null : r.GetString(1),
            LastRunStatus = Str(r, 2),
            Enabled = r.GetBoolean(3),
        });

    private static SqlServerPerformance? CollectPerformance(SqlConnection conn)
    {
        try
        {
            using var cmd = new SqlCommand("""
                SELECT
                  (SELECT TOP 1 CAST(cntr_value AS FLOAT) FROM sys.dm_os_performance_counters
                   WHERE counter_name='Buffer cache hit ratio' AND object_name LIKE '%Buffer Manager%')
                  / NULLIF((SELECT TOP 1 CAST(cntr_value AS FLOAT) FROM sys.dm_os_performance_counters
                   WHERE counter_name='Buffer cache hit ratio base' AND object_name LIKE '%Buffer Manager%'),0)*100,
                  (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters
                   WHERE counter_name='Page life expectancy' AND object_name LIKE '%Buffer Manager%'),
                  (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters
                   WHERE counter_name='Batch Requests/sec' AND object_name LIKE '%SQL Statistics%'),
                  (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters
                   WHERE counter_name='Number of Deadlocks/sec' AND object_name LIKE '%Locks%'),
                  (SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters
                   WHERE counter_name='User Connections' AND object_name LIKE '%General Statistics%')
                """, conn) { CommandTimeout = 15 };
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new SqlServerPerformance
            {
                BufferCacheHitRatio = Dbl(reader, 0),
                PageLifeExpectancy = Lng(reader, 1),
                BatchRequestsPerSec = Lng(reader, 2),
                DeadlocksPerSec = Lng(reader, 3),
                UserConnections = Int(reader, 4),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sqlserver] performance falhou: {ex.Message}");
            return null;
        }
    }
}