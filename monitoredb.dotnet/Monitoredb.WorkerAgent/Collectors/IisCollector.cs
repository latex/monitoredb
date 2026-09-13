using System.Diagnostics;
using System.Text.Json;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.CommonCollectors.Collectors;

/// <summary>Coleta métricas do IIS via PowerShell + appcmd (Windows only).</summary>
public static class IisCollector
{
    public static IisSnapshot? Collect()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var serverName = Environment.MachineName;
            var (w3wpMap, poolStates) = GetW3wpInfo();
            var (cpuMap, memMap, uptimeMap, w3wpPids) = GetProcessMaps();
            var requestsRaw = GetActiveRequests();
            var counterPools = GetCounterPools();

            var activeRequests = requestsRaw.Select(req =>
            {
                uint? pid = req.TryGetValue("WP.NAME", out var wp) && uint.TryParse(wp, out var p) ? p : null;
                return new IisRequest
                {
                    Url = req.GetValueOrDefault("Url") ?? "",
                    Verb = req.GetValueOrDefault("Verb") ?? "",
                    ClientIp = req.GetValueOrDefault("ClientIp") ?? "",
                    Stage = req.GetValueOrDefault("Stage") ?? "",
                    Module = req.GetValueOrDefault("ModuleName") ?? "",
                    TimeMs = req.TryGetValue("Time", out var t) && long.TryParse(t, out var tm) ? tm : null,
                    SiteId = req.TryGetValue("SITE.ID", out var sid) && int.TryParse(sid, out var s) ? s : null,
                    AppPool = req.GetValueOrDefault("APPPOOL.NAME") ?? "",
                    Pid = pid,
                    CpuPercent = pid.HasValue && cpuMap.TryGetValue(pid.Value, out var c) ? c : null,
                    MemoryMb = pid.HasValue && memMap.TryGetValue(pid.Value, out var m) ? m : null,
                };
            }).ToList();

            IisAppPool NewPool(uint pid, string name) => new()
            {
                Name = name,
                Pid = pid,
                CpuPercent = cpuMap.GetValueOrDefault(pid),
                MemoryMb = memMap.GetValueOrDefault(pid),
                UptimeMinutes = uptimeMap.GetValueOrDefault(pid),
                State = "Running",
                ActiveRequests = null,
            };

            var pools = new Dictionary<uint, IisAppPool>();
            foreach (var pid in w3wpPids)
                pools.TryAdd(pid, NewPool(pid, $"w3wp-{pid}"));

            foreach (var (pid, name) in w3wpMap)
            {
                var entry = pools.TryGetValue(pid, out var e) ? e : NewPool(pid, name);
                entry = entry with { Name = name };
                if (poolStates.TryGetValue(name, out var st))
                    entry = entry with { State = st };
                pools[pid] = entry;
            }

            foreach (var (name, pid, count) in counterPools)
            {
                var entry = pools.TryGetValue(pid, out var e) ? e : NewPool(pid, name);
                if (entry.Name.StartsWith("w3wp-"))
                    entry = entry with { Name = name };
                entry = entry with { ActiveRequests = count };
                pools[pid] = entry;
            }

            var appPools = pools.Values
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Pid)
                .ToList();

            return new IisSnapshot
            {
                ActiveRequests = activeRequests,
                AppPools = appPools,
                ServerName = serverName,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[iis] falha na coleta: {ex.Message}");
            return null;
        }
    }

    private static (Dictionary<uint, string> pids, Dictionary<string, string> states) GetW3wpInfo()
    {
        var script = """
            $appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
            $stateMap = @{}
            $xml = & $appcmd list apppools /xml 2>$null
            if ($xml) {
              [xml]$am = ($xml -join "`n")
              if ($null -ne $am.appcmd.APPPOOL) {
                foreach ($p in @($am.appcmd.APPPOOL)) {
                  $stateMap[$p.name] = $p.@state
                }
              }
            }
            $w3wp = Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'"
            $result = @{ pools = $stateMap; workers = @() }
            $w3wp | %{
                $pool = ""
                if ($_.CommandLine -match '-ap\s+"([^"]+)"') { $pool = $Matches[1] }
                if ($pool -ne "" -and -not $stateMap.ContainsKey($pool)) { $stateMap[$pool] = "Running" }
                $result.workers += @{ pid = $_.ProcessId; pool = $pool }
            }
            $result.pools = $stateMap
            $result | ConvertTo-Json -Compress
            """;

        var output = RunPowerShell(script);
        if (string.IsNullOrWhiteSpace(output))
            return (new(), new());

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var pidMap = new Dictionary<uint, string>();
            if (root.TryGetProperty("workers", out var workers) && workers.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in workers.EnumerateArray())
                {
                    var pid = w.TryGetProperty("pid", out var p) && p.TryGetUInt32(out var pv) ? pv : 0u;
                    var pool = w.TryGetProperty("pool", out var pl) ? pl.GetString() ?? "" : "";
                    if (pid > 0 && pool.Length > 0)
                        pidMap[pid] = pool;
                }
            }
            var stateMap = new Dictionary<string, string>();
            if (root.TryGetProperty("pools", out var pools) && pools.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pools.EnumerateObject())
                    stateMap[prop.Name] = prop.Value.GetString() ?? "Unknown";
            }
            return (pidMap, stateMap);
        }
        catch
        {
            return (new(), new());
        }
    }

    private static List<(string pool, uint pid, int active)> GetCounterPools()
    {
        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $items = @(Get-CimInstance Win32_PerfFormattedData_W3SVC_W3WP | Where-Object { $_.Name -and $_.Name -ne '_Total' })
            if ($items.Count -eq 0) { exit }
            @($items | ForEach-Object {
              $n = [string]$_.Name
              $idx = $n.LastIndexOf('_')
              if ($idx -gt 0) {
                $p = 0
                if ([int]::TryParse($n.Substring($idx + 1), [ref]$p)) {
                  [pscustomobject]@{ pool = $n.Substring(0, $idx); pid = $p; active = [int]$_.ActiveRequests }
                }
              }
            }) | ConvertTo-Json -Compress
            """;

        var output = RunPowerShell(script).Trim();
        if (string.IsNullOrWhiteSpace(output) || output == "null")
            return [];

        try
        {
            var list = new List<(string, uint, int)>();
            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var v in items)
            {
                var name = v.TryGetProperty("pool", out var n) ? n.GetString() ?? "" : "";
                var pid = v.TryGetProperty("pid", out var p) && p.TryGetUInt32(out var pv) ? pv : 0u;
                var active = v.TryGetProperty("active", out var a) && a.TryGetInt32(out var av) ? av : 0;
                if (name.Length > 0 && pid > 0)
                    list.Add((name, pid, active));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    private static (Dictionary<uint, float> cpu, Dictionary<uint, double> mem, Dictionary<uint, double> uptime, List<uint> w3wpPids) GetProcessMaps()
    {
        var cpuMap = new Dictionary<uint, float>();
        var memMap = new Dictionary<uint, double>();
        var uptimeMap = new Dictionary<uint, double>();
        var w3wpPids = new List<uint>();

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var pid = (uint)p.Id;
                    cpuMap[pid] = 0f;
                    memMap[pid] = p.WorkingSet64 / 1048576.0;
                    uptimeMap[pid] = (DateTime.UtcNow - p.StartTime.ToUniversalTime()).TotalMinutes;
                    if (p.ProcessName.Equals("w3wp", StringComparison.OrdinalIgnoreCase))
                        w3wpPids.Add(pid);
                }
                catch { }
            }
        }
        catch { }
        return (cpuMap, memMap, uptimeMap, w3wpPids);
    }

    private static List<Dictionary<string, string>> GetActiveRequests()
    {
        var script = """
            $appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
            if (-not (Test-Path $appcmd)) { exit }
            $xmlText = (& $appcmd list requests /xml 2>&1) -join "`n"
            if ([string]::IsNullOrWhiteSpace($xmlText)) { exit }
            try { [xml]$xml = $xmlText } catch { exit }
            if ($null -eq $xml.appcmd -or $null -eq $xml.appcmd.REQUEST) { exit }
            $xml.appcmd.REQUEST | Select-Object Url,Verb,ClientIp,Stage,ModuleName,Time,"SITE.ID","APPPOOL.NAME","WP.NAME" |
            ConvertTo-Json -Compress
            """;

        var output = RunPowerShell(script).Trim();
        if (string.IsNullOrWhiteSpace(output) || output == "null")
            return [];

        try
        {
            var result = new List<Dictionary<string, string>>();
            using var doc = JsonDocument.Parse(output);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var item in items)
            {
                var map = new Dictionary<string, string>();
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in item.EnumerateObject())
                        map[prop.Name] = prop.Value.GetString() ?? "";
                }
                result.Add(map);
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null)
                return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            return stdout;
        }
        catch
        {
            return "";
        }
    }
}