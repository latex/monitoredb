using System.Diagnostics;
using System.Management;
using Monitoredb.CommonCollectors.Models;

#pragma warning disable CA1416 // EventLog é Windows-only, guardado por OperatingSystem.IsWindows()

namespace Monitoredb.CommonCollectors.Collectors;

/// <summary>Coleta métricas do sistema operacional (Windows/Linux).</summary>
public static class WindowsCollector
{
    public static WindowsSnapshot Collect()
    {
        var cpu = GetCpuInfo();
        var mem = GetMemoryInfo();
        var uptime = (ulong)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;

        return new WindowsSnapshot
        {
            CpuPercent = cpu.percent,
            CoreCount = cpu.cores,
            CpuName = cpu.name,
            MemoryTotalGb = mem.totalGb,
            MemoryUsedGb = mem.usedGb,
            MemoryPercent = mem.percent,
            UptimeSeconds = uptime,
            OsVersion = GetOsVersion(),
            Disks = GetDisks(),
            TopProcesses = GetTopProcesses(),
            Services = GetServices(),
            Events = GetEvents(),
            Network = GetNetwork(),
        };
    }

    private static (float percent, int cores, string name) GetCpuInfo()
    {
        try
        {
            var cores = Environment.ProcessorCount;
            var name = "";
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (var o in searcher.Get())
                    name = o["Name"]?.ToString() ?? "";
            }
            else
            {
                name = ReadFirstLine("/proc/cpuinfo", "model name") ?? "";
            }

            var percent = GetCpuPercent();
            return (percent, cores, name);
        }
        catch
        {
            return (0f, Environment.ProcessorCount, "");
        }
    }

    private static float GetCpuPercent()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT LoadPercentage FROM Win32_Processor WHERE LoadPercentage IS NOT NULL");
                float total = 0;
                var count = 0;
                foreach (var o in searcher.Get())
                {
                    total += Convert.ToSingle(o["LoadPercentage"]);
                    count++;
                }
                return count > 0 ? total / count : 0f;
            }
            else
            {
                // Linux: leitura de /proc/stat com intervalo de 500ms
                var (idle1, total1) = ReadProcStat();
                Thread.Sleep(500);
                var (idle2, total2) = ReadProcStat();
                var dIdle = idle2 - idle1;
                var dTotal = total2 - total1;
                return dTotal > 0 ? (float)((dTotal - dIdle) * 100.0 / dTotal) : 0f;
            }
        }
        catch
        {
            return 0f;
        }
    }

    private static (ulong idle, ulong total) ReadProcStat()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ")) ?? "";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
        if (parts.Length < 4)
            return (0, 0);
        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
        var total = parts.Aggregate(0ul, (a, b) => a + b);
        return (idle, total);
    }

    private static string? ReadFirstLine(string path, string prefix)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(prefix))
                    return line[(prefix.Length + 1)..].Trim();
            }
        }
        catch { }
        return null;
    }

    private static (double totalGb, double usedGb, double percent) GetMemoryInfo()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var o in searcher.Get())
                {
                    var totalKb = Convert.ToDouble(o["TotalVisibleMemorySize"]);
                    var freeKb = Convert.ToDouble(o["FreePhysicalMemory"]);
                    var usedKb = totalKb - freeKb;
                    var totalGb = totalKb / 1048576.0;
                    var usedGb = usedKb / 1048576.0;
                    return (totalGb, usedGb, totalKb > 0 ? usedKb / totalKb * 100.0 : 0.0);
                }
            }
            else
            {
                var memInfo = File.ReadAllLines("/proc/meminfo");
                var totalKb = ParseMemInfo(memInfo, "MemTotal");
                var availableKb = ParseMemInfo(memInfo, "MemAvailable");
                if (totalKb > 0)
                {
                    var usedKb = totalKb - availableKb;
                    return (totalKb / 1048576.0, usedKb / 1048576.0, usedKb / (double)totalKb * 100.0);
                }
            }
        }
        catch { }
        return (0, 0, 0);
    }

    private static double ParseMemInfo(string[] lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(key + ":")) ?? "";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && double.TryParse(parts[1], out var v) ? v : 0;
    }

    private static string GetOsVersion()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, Version FROM Win32_OperatingSystem");
                foreach (var o in searcher.Get())
                    return $"{o["Caption"]} {o["Version"]}".Trim();
            }
            else
            {
                var osRelease = "/etc/os-release";
                if (File.Exists(osRelease))
                {
                    var lines = File.ReadAllLines(osRelease);
                    var pretty = lines.FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                    if (pretty != null)
                        return pretty.Split('=', 2)[1].Trim('"');
                }
            }
        }
        catch { }
        return Environment.OSVersion.ToString();
    }

    private static List<DiskInfo> GetDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady)
                        continue;
                    var total = drive.TotalSize / 1073741824.0;
                    var available = drive.AvailableFreeSpace / 1073741824.0;
                    var used = total - available;
                    disks.Add(new DiskInfo
                    {
                        Mount = drive.Name,
                        TotalGb = Math.Round(total, 2),
                        UsedGb = Math.Round(used, 2),
                        AvailableGb = Math.Round(available, 2),
                        UsagePercent = total > 0 ? used / total * 100.0 : 0.0,
                    });
                }
            }
            else
            {
                var df = RunProcess("df", "-B1");
                foreach (var line in df.Split('\n').Skip(1))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 6)
                        continue;
                    if (!double.TryParse(parts[1], out var totalBytes) || totalBytes <= 0)
                        continue;
                    var usedBytes = double.Parse(parts[2]);
                    var availableBytes = double.Parse(parts[3]);
                    disks.Add(new DiskInfo
                    {
                        Mount = parts[5],
                        TotalGb = Math.Round(totalBytes / 1073741824.0, 2),
                        UsedGb = Math.Round(usedBytes / 1073741824.0, 2),
                        AvailableGb = Math.Round(availableBytes / 1073741824.0, 2),
                        UsagePercent = usedBytes / totalBytes * 100.0,
                    });
                }
            }
        }
        catch { }
        return disks;
    }

    private static List<ProcessInfo> GetTopProcesses()
    {
        var list = new List<ProcessInfo>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    list.Add(new ProcessInfo
                    {
                        Pid = (uint)p.Id,
                        Name = p.ProcessName,
                        CpuPercent = 0f,
                        MemoryMb = p.WorkingSet64 / 1048576.0,
                    });
                }
                catch { }
            }
            list.Sort((a, b) => b.MemoryMb.CompareTo(a.MemoryMb));
            return list.Take(20).ToList();
        }
        catch
        {
            return list;
        }
    }

    private static List<ServiceInfo> GetServices()
    {
        var services = new List<ServiceInfo>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName, State FROM Win32_Service");
                foreach (var o in searcher.Get())
                {
                    services.Add(new ServiceInfo
                    {
                        Name = o["Name"]?.ToString() ?? "",
                        DisplayName = o["DisplayName"]?.ToString() ?? "",
                        Status = o["State"]?.ToString() ?? "",
                    });
                }
            }
            else
            {
                var output = RunProcess("systemctl", "--no-pager", "--no-legend", "list-units", "--type=service", "--state=running");
                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        services.Add(new ServiceInfo
                        {
                            Name = parts[0],
                            DisplayName = parts[0],
                            Status = "Running",
                        });
                    }
                }
            }
        }
        catch { }
        return services;
    }

    private static List<Models.EventLogEntry> GetEvents()
    {
        var events = new List<Models.EventLogEntry>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var since = DateTime.Now.AddHours(-1);
                var log = new EventLog("System");
                var entries = log.Entries.Cast<System.Diagnostics.EventLogEntry>()
                    .Where(e => e.TimeGenerated >= since)
                    .OrderByDescending(e => e.TimeGenerated)
                    .Take(15);
                foreach (var e in entries)
                {
                    events.Add(new Models.EventLogEntry
                    {
                        Time = e.TimeGenerated.ToString("yyyy-MM-dd HH:mm:ss"),
                        Level = e.EntryType.ToString(),
                        Source = e.Source,
                        Message = e.Message.Length > 200 ? e.Message[..200] : e.Message,
                    });
                }
            }
        }
        catch { }
        return events;
    }

    private static List<NetworkInfo> GetNetwork()
    {
        var list = new List<NetworkInfo>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, BytesReceivedPerSec, BytesSentPerSec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");
                foreach (var o in searcher.Get())
                {
                    list.Add(new NetworkInfo
                    {
                        Interface = o["Name"]?.ToString() ?? "",
                        ReceivedBytes = Convert.ToUInt64(o["BytesReceivedPerSec"] ?? 0),
                        TransmittedBytes = Convert.ToUInt64(o["BytesSentPerSec"] ?? 0),
                    });
                }
            }
            else
            {
                var netDev = File.ReadAllLines("/proc/net/dev");
                foreach (var line in netDev.Skip(2))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length != 2)
                        continue;
                    var iface = parts[0].Trim();
                    var nums = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (nums.Length >= 9 && ulong.TryParse(nums[0], out var rx) && ulong.TryParse(nums[8], out var tx))
                    {
                        list.Add(new NetworkInfo { Interface = iface, ReceivedBytes = rx, TransmittedBytes = tx });
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static string RunProcess(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null)
                return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return stdout;
        }
        catch
        {
            return "";
        }
    }
}