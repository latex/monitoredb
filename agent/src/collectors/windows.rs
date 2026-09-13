use serde::Serialize;
use sysinfo::{Disks, Networks, System};

#[derive(Clone, Debug, Serialize)]
pub struct WindowsSnapshot {
    pub cpu_percent: f32,
    pub core_count: usize,
    pub cpu_name: String,
    pub memory_total_gb: f64,
    pub memory_used_gb: f64,
    pub memory_percent: f64,
    pub uptime_seconds: u64,
    pub os_version: String,
    pub disks: Vec<DiskInfo>,
    pub top_processes: Vec<ProcessInfo>,
    pub services: Vec<ServiceInfo>,
    pub events: Vec<EventLogEntry>,
    pub network: Vec<NetworkInfo>,
}

#[derive(Clone, Debug, Serialize)]
pub struct DiskInfo { pub mount: String, pub total_gb: f64, pub used_gb: f64, pub available_gb: f64, pub usage_percent: f64 }

#[derive(Clone, Debug, Serialize)]
pub struct ProcessInfo { pub pid: u32, pub name: String, pub cpu_percent: f32, pub memory_mb: f64 }

#[derive(Clone, Debug, Serialize)]
pub struct ServiceInfo { pub name: String, pub display_name: String, pub status: String }

#[derive(Clone, Debug, Serialize)]
pub struct EventLogEntry { pub time: String, pub level: String, pub source: String, pub message: String }

#[derive(Clone, Debug, Serialize)]
pub struct NetworkInfo { pub interface: String, pub received_bytes: u64, pub transmitted_bytes: u64 }

pub fn collect() -> WindowsSnapshot {
    let mut sys = System::new_all();
    sys.refresh_all();

    let _hostname = System::host_name().unwrap_or_default();
    let os_version = System::long_os_version().unwrap_or_default();
    let uptime_seconds = System::uptime();

    let cpus = sys.cpus();
    let cpu_percent = sys.global_cpu_usage();
    let core_count = cpus.len();
    let cpu_name = cpus.first().map(|c| c.brand().to_string()).unwrap_or_default();

    let memory_total_gb = sys.total_memory() as f64 / 1073741824.0;
    let memory_used_gb = sys.used_memory() as f64 / 1073741824.0;
    let memory_percent = if sys.total_memory() > 0 {
        (sys.used_memory() as f64 / sys.total_memory() as f64) * 100.0
    } else { 0.0 };

    let disks = Disks::new_with_refreshed_list().iter().map(|d| {
        let total = d.total_space() as f64 / 1073741824.0;
        let available = d.available_space() as f64 / 1073741824.0;
        let used = total - available;
        DiskInfo {
            mount: d.mount_point().to_string_lossy().to_string(),
            total_gb: (total * 100.0).round() / 100.0,
            used_gb: (used * 100.0).round() / 100.0,
            available_gb: (available * 100.0).round() / 100.0,
            usage_percent: if total > 0.0 { (used / total) * 100.0 } else { 0.0 },
        }
    }).collect();

    let mut processes: Vec<ProcessInfo> = sys.processes().iter().map(|(pid, p)| ProcessInfo {
        pid: pid.as_u32(),
        name: p.name().to_string_lossy().to_string(),
        cpu_percent: p.cpu_usage(),
        memory_mb: p.memory() as f64 / 1048576.0,
    }).collect();
    processes.sort_by(|a, b| b.cpu_percent.partial_cmp(&a.cpu_percent).unwrap_or(std::cmp::Ordering::Equal));
    processes.truncate(20);

    let network = Networks::new_with_refreshed_list().iter().map(|(name, data)| NetworkInfo {
        interface: name.clone(),
        received_bytes: data.total_received(),
        transmitted_bytes: data.total_transmitted(),
    }).collect();

    let services = collect_services();
    let events = collect_events();

    WindowsSnapshot {
        cpu_percent, core_count, cpu_name,
        memory_total_gb, memory_used_gb, memory_percent,
        uptime_seconds, os_version,
        disks, top_processes: processes, services, events, network,
    }
}

fn collect_services() -> Vec<ServiceInfo> {
    let output = std::process::Command::new("powershell")
        .args(["-NoProfile", "-Command",
            "Get-Service | Select-Object Name,DisplayName,Status | ConvertTo-Json -Compress"])
        .output();
    let output = match output { Ok(o) if o.status.success() => o, _ => return vec![] };
    let text = String::from_utf8_lossy(&output.stdout);
    parse_json_array_or_single(&text)
        .into_iter()
        .map(|v| ServiceInfo {
            name: v["Name"].as_str().unwrap_or("").into(),
            display_name: v["DisplayName"].as_str().unwrap_or("").into(),
            status: v["Status"].as_str().unwrap_or("").into(),
        })
        .collect()
}

fn collect_events() -> Vec<EventLogEntry> {
    let script = r#"Get-WinEvent -FilterHashtable @{LogName='System';StartTime=(Get-Date).AddHours(-1)} -MaxEvents 15 | Select-Object TimeCreated,LevelDisplayName,ProviderName,Message | ConvertTo-Json -Compress"#;
    let output = std::process::Command::new("powershell")
        .args(["-NoProfile", "-Command", script]).output();
    let output = match output { Ok(o) if o.status.success() => o, _ => return vec![] };
    let text = String::from_utf8_lossy(&output.stdout);
    parse_json_array_or_single(&text)
        .into_iter()
        .map(|v| EventLogEntry {
            time: v["TimeCreated"].as_str().unwrap_or("").into(),
            level: v["LevelDisplayName"].as_str().unwrap_or("").into(),
            source: v["ProviderName"].as_str().unwrap_or("").into(),
            message: v["Message"].as_str().unwrap_or("").chars().take(200).collect(),
        })
        .collect()
}

fn parse_json_array_or_single(text: &str) -> Vec<serde_json::Value> {
    let t = text.trim();
    if t.is_empty() { return vec![]; }
    if let Ok(v) = serde_json::from_str::<Vec<serde_json::Value>>(t) { return v; }
    if let Ok(v) = serde_json::from_str::<serde_json::Value>(t) {
        return vec![v];
    }
    vec![]
}
