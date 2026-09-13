using System.Text;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.ApiServer;

/// <summary>Converte o estado em texto no formato Prometheus (compatível com o Rust original).</summary>
public static class PrometheusFormatter
{
    public static string Format(IEnumerable<AgentSnapshot> agents)
    {
        var sb = new StringBuilder();

        void Fam(string name, string help)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        }

        Fam("monitoredb_up", "1 if agent has collected data");
        Fam("monitoredb_agent_last_seen_timestamp", "Unix timestamp of last agent snapshot");
        Fam("monitoredb_cpu_percent", "CPU usage percent");
        Fam("monitoredb_memory_percent", "Memory usage percent");
        Fam("monitoredb_disk_usage_percent", "Disk usage percent per mount");
        Fam("monitoredb_disk_available_gb", "Disk available GB per mount");
        Fam("monitoredb_service_status", "Windows service running (1) or not (0)");
        Fam("monitoredb_iis_active_requests", "Active IIS requests");
        Fam("monitoredb_iis_app_pool_cpu", "IIS app pool CPU percent");
        Fam("monitoredb_iis_app_pool_memory_mb", "IIS app pool memory MB");
        Fam("monitoredb_iis_app_pool_active_requests", "Active requests per IIS app pool");
        Fam("monitoredb_sqlserver_connections", "SQL Server user connections");
        Fam("monitoredb_sqlserver_buffer_cache_hit_ratio", "SQL Server buffer cache hit ratio percent");
        Fam("monitoredb_sqlserver_page_life_expectancy", "SQL Server page life expectancy seconds");
        Fam("monitoredb_database_size_mb", "SQL Server database size MB");
        Fam("monitoredb_job_status", "SQL Agent job last run succeeded (1) or not (0)");

        foreach (var a in agents)
        {
            var lbl = $"agent=\"{Escape(a.AgentId)}\",hostname=\"{Escape(a.Hostname)}\"";
            var hasData = a.Windows is not null || a.Iis is not null || a.SqlServer is not null;
            sb.Append("monitoredb_up{").Append(lbl).Append("} ").Append(hasData ? 1 : 0).Append('\n');
            sb.Append("monitoredb_agent_last_seen_timestamp{").Append(lbl).Append("} ")
              .Append(new DateTimeOffset(a.Timestamp).ToUnixTimeSeconds()).Append('\n');

            if (a.Windows is { } w)
            {
                sb.Append("monitoredb_cpu_percent{").Append(lbl).Append("} ").Append(w.CpuPercent.ToString("F2")).Append('\n');
                sb.Append("monitoredb_memory_percent{").Append(lbl).Append("} ").Append(w.MemoryPercent.ToString("F2")).Append('\n');
                foreach (var disk in w.Disks)
                {
                    var m = Escape(disk.Mount.Replace(" ", "_").Replace(":", ""));
                    sb.Append("monitoredb_disk_usage_percent{").Append(lbl).Append(",mount=\"").Append(m).Append("\"} ")
                      .Append(disk.UsagePercent.ToString("F2")).Append('\n');
                    sb.Append("monitoredb_disk_available_gb{").Append(lbl).Append(",mount=\"").Append(m).Append("\"} ")
                      .Append(disk.AvailableGb.ToString("F2")).Append('\n');
                }
                foreach (var svc in w.Services)
                {
                    var n = Escape(svc.Name.Replace(" ", "_"));
                    sb.Append("monitoredb_service_status{").Append(lbl).Append(",name=\"").Append(n).Append("\"} ")
                      .Append(svc.Status == "Running" ? 1 : 0).Append('\n');
                }
            }

            if (a.Iis is { } i)
            {
                sb.Append("monitoredb_iis_active_requests{").Append(lbl).Append("} ").Append(i.ActiveRequests.Count).Append('\n');
                foreach (var pool in i.AppPools)
                {
                    var n = Escape(pool.Name.Replace(" ", "_"));
                    sb.Append("monitoredb_iis_app_pool_cpu{").Append(lbl).Append(",pool=\"").Append(n).Append("\"} ")
                      .Append(pool.CpuPercent.ToString("F2")).Append('\n');
                    sb.Append("monitoredb_iis_app_pool_memory_mb{").Append(lbl).Append(",pool=\"").Append(n).Append("\"} ")
                      .Append(pool.MemoryMb.ToString("F2")).Append('\n');
                    if (pool.ActiveRequests is { } c)
                    {
                        sb.Append("monitoredb_iis_app_pool_active_requests{").Append(lbl).Append(",pool=\"").Append(n).Append("\"} ")
                          .Append(c).Append('\n');
                    }
                }
            }

            if (a.SqlServer is { } s)
            {
                if (s.Performance is { } p)
                {
                    sb.Append("monitoredb_sqlserver_connections{").Append(lbl).Append("} ").Append(p.UserConnections).Append('\n');
                    sb.Append("monitoredb_sqlserver_buffer_cache_hit_ratio{").Append(lbl).Append("} ")
                      .Append(p.BufferCacheHitRatio.ToString("F2")).Append('\n');
                    sb.Append("monitoredb_sqlserver_page_life_expectancy{").Append(lbl).Append("} ").Append(p.PageLifeExpectancy).Append('\n');
                }
                foreach (var db in s.Databases)
                {
                    var n = Escape(db.Name.Replace(" ", "_"));
                    sb.Append("monitoredb_database_size_mb{").Append(lbl).Append(",database=\"").Append(n).Append("\"} ")
                      .Append(db.SizeMb.ToString("F2")).Append('\n');
                }
                foreach (var j in s.Jobs)
                {
                    var n = Escape(j.Name.Replace(" ", "_"));
                    sb.Append("monitoredb_job_status{").Append(lbl).Append(",name=\"").Append(n).Append("\"} ")
                      .Append(j.LastRunStatus == "Succeeded" ? 1 : 0).Append('\n');
                }
            }
        }

        sb.Append('\n');
        return sb.ToString();
    }

    private static string Escape(string v) =>
        v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}