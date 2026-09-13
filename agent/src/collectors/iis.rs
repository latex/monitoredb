use serde::Serialize;
use std::collections::HashMap;
use std::process::Command;
use sysinfo::{ProcessesToUpdate, System};

#[derive(Clone, Debug, Serialize)]
pub struct IisSnapshot {
    pub active_requests: Vec<IisRequest>,
    pub app_pools: Vec<IisAppPool>,
    pub server_name: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct IisRequest {
    pub url: String,
    pub verb: String,
    pub client_ip: String,
    pub stage: String,
    pub module: String,
    pub time_ms: Option<i64>,
    pub site_id: Option<i32>,
    pub app_pool: String,
    pub pid: Option<u32>,
    pub cpu_percent: Option<f32>,
    pub memory_mb: Option<f64>,
}

#[derive(Clone, Debug, Serialize)]
pub struct IisAppPool {
    pub name: String,
    pub pid: u32,
    pub cpu_percent: f32,
    pub memory_mb: f64,
    pub uptime_minutes: Option<f64>,
    pub state: String,
    pub active_requests: Option<usize>,
}

pub fn collect() -> IisSnapshot {
    let server_name = System::host_name().unwrap_or_else(|| "unknown".into());
    let (w3wp_map, pool_states) = get_w3wp_info();
    let (cpu_map, mem_map, uptime_map, w3wp_pids) = get_process_maps();
    let requests_raw = get_active_requests();
    let counter_pools = get_counter_pools();

    let active_requests: Vec<IisRequest> = requests_raw.iter().map(|req| {
        let pid = req.get("WP.NAME")
            .and_then(|v| v.parse::<u32>().ok());

        let cpu = pid.and_then(|p| cpu_map.get(&p).copied());
        let mem = pid.and_then(|p| mem_map.get(&p).copied());

        IisRequest {
            url: req.get("Url").cloned().unwrap_or_default(),
            verb: req.get("Verb").cloned().unwrap_or_default(),
            client_ip: req.get("ClientIp").cloned().unwrap_or_default(),
            stage: req.get("Stage").cloned().unwrap_or_default(),
            module: req.get("ModuleName").cloned().unwrap_or_default(),
            time_ms: req.get("Time").and_then(|v| v.parse::<i64>().ok()),
            site_id: req.get("SITE.ID").and_then(|v| v.parse::<i32>().ok()),
            app_pool: req.get("APPPOOL.NAME").cloned().unwrap_or_default(),
            pid,
            cpu_percent: cpu,
            memory_mb: mem,
        }
    }).collect();

    let new_pool = |pid: u32, name: String| IisAppPool {
        name,
        pid,
        cpu_percent: cpu_map.get(&pid).copied().unwrap_or(0.0),
        memory_mb: mem_map.get(&pid).copied().unwrap_or(0.0),
        uptime_minutes: uptime_map.get(&pid).copied(),
        state: "Running".into(),
        active_requests: None,
    };

    let mut pools: HashMap<u32, IisAppPool> = HashMap::new();

    for pid in &w3wp_pids {
        pools.entry(*pid).or_insert_with(|| new_pool(*pid, format!("w3wp-{}", pid)));
    }

    for (pid, name) in &w3wp_map {
        let entry = pools.entry(*pid).or_insert_with(|| new_pool(*pid, name.clone()));
        entry.name = name.clone();
        if let Some(st) = pool_states.get(name.as_str()) {
            entry.state = st.clone();
        }
    }

    for (name, pid, count) in &counter_pools {
        let entry = pools.entry(*pid).or_insert_with(|| new_pool(*pid, name.clone()));
        if entry.name.starts_with("w3wp-") {
            entry.name = name.clone();
        }
        entry.active_requests = Some(*count);
    }

    let mut app_pools: Vec<IisAppPool> = pools.into_values().collect();
    app_pools.sort_by(|a, b| a.name.cmp(&b.name).then(a.pid.cmp(&b.pid)));

    IisSnapshot { active_requests, app_pools, server_name }
}

fn get_w3wp_info() -> (HashMap<u32, String>, HashMap<String, String>) {
    let script = r#"
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
"#;

    let output = match Command::new("powershell")
        .args(["-NoProfile", "-Command", script])
        .output()
    {
        Ok(o) if o.status.success() => o,
        _ => return (HashMap::new(), HashMap::new()),
    };

    let text = String::from_utf8_lossy(&output.stdout);
    let v: serde_json::Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(_) => return (HashMap::new(), HashMap::new()),
    };

    let mut pid_map = HashMap::new();
    if let Some(workers) = v["workers"].as_array() {
        for w in workers {
            let pid = w["pid"].as_i64().unwrap_or(0) as u32;
            let pool = w["pool"].as_str().unwrap_or("").to_string();
            if pid > 0 && !pool.is_empty() {
                pid_map.insert(pid, pool);
            }
        }
    }

    let mut state_map = HashMap::new();
    if let Some(pools) = v["pools"].as_object() {
        for (name, state) in pools {
            state_map.insert(name.clone(), state.as_str().unwrap_or("Unknown").to_string());
        }
    }

    (pid_map, state_map)
}

fn get_counter_pools() -> Vec<(String, u32, usize)> {
    let script = r#"
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
"#;

    let output = match Command::new("powershell")
        .args(["-NoProfile", "-Command", script])
        .output()
    {
        Ok(o) if o.status.success() => o,
        _ => return vec![],
    };

    let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if text.is_empty() || text == "null" {
        return vec![];
    }

    let vals: Vec<serde_json::Value> = serde_json::from_str(&text)
        .or_else(|_| serde_json::from_str::<serde_json::Value>(&text).map(|v| vec![v]))
        .unwrap_or_default();

    vals.iter().filter_map(|v| {
        let name = v["pool"].as_str()?.to_string();
        let pid = v["pid"].as_u64()? as u32;
        let active = v["active"].as_u64().unwrap_or(0) as usize;
        if name.is_empty() || pid == 0 { None } else { Some((name, pid, active)) }
    }).collect()
}

fn get_process_maps() -> (
    HashMap<u32, f32>,
    HashMap<u32, f64>,
    HashMap<u32, f64>,
    Vec<u32>,
) {
    let mut cpu_map = HashMap::new();
    let mut mem_map = HashMap::new();
    let mut uptime_map = HashMap::new();
    let mut w3wp_pids = Vec::new();

    let mut sys = System::new_all();
    sys.refresh_processes(ProcessesToUpdate::All, true);
    sys.refresh_cpu_usage();
    std::thread::sleep(std::time::Duration::from_millis(500));
    sys.refresh_processes(ProcessesToUpdate::All, true);
    sys.refresh_cpu_usage();

    for (pid, proc) in sys.processes() {
        let p = pid.as_u32();
        cpu_map.insert(p, proc.cpu_usage());
        mem_map.insert(p, proc.memory() as f64 / 1048576.0);
        let run = proc.run_time();
        uptime_map.insert(p, run as f64 / 60.0);
        if proc.name().to_string_lossy().eq_ignore_ascii_case("w3wp.exe") {
            w3wp_pids.push(p);
        }
    }

    (cpu_map, mem_map, uptime_map, w3wp_pids)
}

fn get_active_requests() -> Vec<HashMap<String, String>> {
    let ps_script = r#"
$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
if (-not (Test-Path $appcmd)) { exit }
$xmlText = (& $appcmd list requests /xml 2>&1) -join "`n"
if ([string]::IsNullOrWhiteSpace($xmlText)) { exit }
try { [xml]$xml = $xmlText } catch { exit }
if ($null -eq $xml.appcmd -or $null -eq $xml.appcmd.REQUEST) { exit }
$xml.appcmd.REQUEST | Select-Object Url,Verb,ClientIp,Stage,ModuleName,Time,"SITE.ID","APPPOOL.NAME","WP.NAME" |
ConvertTo-Json -Compress
"#;

    let output = match Command::new("powershell")
        .args(["-NoProfile", "-Command", ps_script])
        .output()
    {
        Ok(o) if o.status.success() => o,
        _ => return vec![],
    };

    let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if text.is_empty() || text == "null" { return vec![]; }

    match serde_json::from_str::<Vec<serde_json::Value>>(&text) {
        Ok(items) => items.iter().map(|v| {
            let mut map = HashMap::new();
            if let Some(obj) = v.as_object() {
                for (k, val) in obj {
                    map.insert(k.clone(), val.as_str().unwrap_or("").to_string());
                }
            }
            map
        }).collect(),
        Err(_) => {
            if let Ok(single) = serde_json::from_str::<serde_json::Value>(&text) {
                if let Some(obj) = single.as_object() {
                    let mut map = HashMap::new();
                    for (k, val) in obj {
                        map.insert(k.clone(), val.as_str().unwrap_or("").to_string());
                    }
                    return vec![map];
                }
            }
            vec![]
        }
    }
}
