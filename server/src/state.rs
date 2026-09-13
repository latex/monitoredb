use std::collections::{HashMap, VecDeque};
use std::sync::Arc;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use tokio::sync::RwLock;
use tracing::{error, info, warn};

pub type SharedState = Arc<RwLock<AppState>>;

const DATA_DIR: &str = "data";
const DATA_FILE: &str = "data/state.json";
const MAX_HISTORY: usize = 2880;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct AgentSnapshot {
    pub agent_id: String,
    pub hostname: String,
    pub timestamp: DateTime<Utc>,
    pub windows: Option<WindowsSnapshot>,
    pub iis: Option<IisSnapshot>,
    pub sql_server: Option<SqlServerSnapshot>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
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

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DiskInfo {
    pub mount: String,
    pub total_gb: f64,
    pub used_gb: f64,
    pub available_gb: f64,
    pub usage_percent: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ProcessInfo {
    pub pid: u32,
    pub name: String,
    pub cpu_percent: f32,
    pub memory_mb: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ServiceInfo {
    pub name: String,
    pub display_name: String,
    pub status: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct EventLogEntry {
    pub time: String,
    pub level: String,
    pub source: String,
    pub message: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct NetworkInfo {
    pub interface: String,
    pub received_bytes: u64,
    pub transmitted_bytes: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerSnapshot {
    pub server_name: String,
    pub version: String,
    pub edition: String,
    pub uptime_hours: f64,
    pub databases: Vec<SqlServerDatabase>,
    pub connections: Vec<SqlServerConnection>,
    pub running_queries: Vec<SqlServerQuery>,
    pub backups: Vec<SqlServerBackup>,
    pub jobs: Vec<SqlServerJob>,
    pub performance: Option<SqlServerPerformance>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerDatabase {
    pub name: String,
    pub state: String,
    pub size_mb: f64,
    pub recovery_model: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerConnection {
    pub session_id: i32,
    pub hostname: String,
    pub program_name: String,
    pub login_name: String,
    pub database: String,
    pub status: String,
    pub cpu_time_ms: i64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerQuery {
    pub session_id: i32,
    pub database: String,
    pub user: String,
    pub status: String,
    pub wait_type: String,
    pub cpu_time_ms: i64,
    pub total_elapsed_ms: i64,
    pub query_text: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerBackup {
    pub database: String,
    pub backup_type: String,
    pub last_backup: Option<String>,
    pub size_mb: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerJob {
    pub name: String,
    pub last_run: Option<String>,
    pub last_run_status: String,
    pub enabled: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerPerformance {
    pub buffer_cache_hit_ratio: f64,
    pub page_life_expectancy: i64,
    pub batch_requests_per_sec: i64,
    pub deadlocks_per_sec: i64,
    pub user_connections: i32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct IisSnapshot {
    pub active_requests: Vec<IisRequest>,
    pub app_pools: Vec<IisAppPool>,
    pub server_name: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
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

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct IisAppPool {
    pub name: String,
    pub pid: u32,
    pub cpu_percent: f32,
    pub memory_mb: f64,
    pub uptime_minutes: Option<f64>,
    pub state: String,
    #[serde(default)]
    pub active_requests: Option<usize>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct HistorySample {
    pub timestamp: DateTime<Utc>,
    pub cpu_percent: Option<f32>,
    pub memory_percent: Option<f64>,
    pub disk_avg_percent: Option<f64>,
    pub iis_active_requests: Option<usize>,
    pub sql_user_connections: Option<i32>,
    pub sql_buffer_cache_hit_ratio: Option<f64>,
    pub sql_page_life_expectancy: Option<i64>,
    pub sql_deadlocks_per_sec: Option<i64>,
    pub sql_batch_requests_per_sec: Option<i64>,
}

impl HistorySample {
    fn from_snapshot(s: &AgentSnapshot) -> Self {
        let perf = s.sql_server.as_ref().and_then(|q| q.performance.as_ref());
        Self {
            timestamp: s.timestamp,
            cpu_percent: s.windows.as_ref().map(|w| w.cpu_percent),
            memory_percent: s.windows.as_ref().map(|w| w.memory_percent),
            disk_avg_percent: s.windows.as_ref().map(|w| {
                if w.disks.is_empty() {
                    0.0
                } else {
                    w.disks.iter().map(|d| d.usage_percent).sum::<f64>() / w.disks.len() as f64
                }
            }),
            iis_active_requests: s.iis.as_ref().map(|i| i.active_requests.len()),
            sql_user_connections: perf.map(|p| p.user_connections),
            sql_buffer_cache_hit_ratio: perf.map(|p| p.buffer_cache_hit_ratio),
            sql_page_life_expectancy: perf.map(|p| p.page_life_expectancy),
            sql_deadlocks_per_sec: perf.map(|p| p.deadlocks_per_sec),
            sql_batch_requests_per_sec: perf.map(|p| p.batch_requests_per_sec),
        }
    }
}

#[derive(Serialize, Deserialize)]
struct PersistedState {
    agents: Vec<AgentSnapshot>,
    history: HashMap<String, VecDeque<HistorySample>>,
}

#[derive(Clone, Debug)]
pub struct AppState {
    pub agents: HashMap<String, AgentSnapshot>,
    pub history: HashMap<String, VecDeque<HistorySample>>,
    pub token: Option<String>,
    dirty: bool,
}

impl AppState {
    pub fn new(token: Option<String>) -> Self {
        let mut agents = HashMap::new();
        let mut history = HashMap::new();
        match std::fs::read_to_string(DATA_FILE) {
            Ok(text) => match serde_json::from_str::<PersistedState>(&text) {
                Ok(p) => {
                    for a in p.agents {
                        agents.insert(a.agent_id.clone(), a);
                    }
                    history = p.history;
                    info!("Estado restaurado de {} ({} agentes)", DATA_FILE, agents.len());
                }
                Err(e) => warn!("Falha ao carregar {}: {}", DATA_FILE, e),
            },
            Err(_) => {}
        }
        Self { agents, history, token, dirty: false }
    }

    pub fn ingest(&mut self, snapshot: AgentSnapshot) {
        let id = snapshot.agent_id.clone();
        let h = self.history.entry(id.clone()).or_default();
        h.push_back(HistorySample::from_snapshot(&snapshot));
        while h.len() > MAX_HISTORY {
            h.pop_front();
        }
        self.agents.insert(id, snapshot);
        self.dirty = true;
    }

    pub fn register(&mut self, agent_id: String, hostname: String) {
        self.agents.entry(agent_id.clone()).or_insert_with(|| AgentSnapshot {
            agent_id: agent_id.clone(),
            hostname,
            timestamp: Utc::now(),
            windows: None,
            iis: None,
            sql_server: None,
        });
        self.dirty = true;
    }

    pub fn remove(&mut self, agent_id: &str) -> Option<AgentSnapshot> {
        self.history.remove(agent_id);
        self.dirty = true;
        self.agents.remove(agent_id)
    }

    pub fn history(&self, agent_id: &str) -> Option<&VecDeque<HistorySample>> {
        self.history.get(agent_id)
    }

    pub fn latest(&self) -> Option<&AgentSnapshot> {
        self.agents.values().max_by_key(|a| a.timestamp)
    }

    pub fn all_agents(&self) -> Vec<&AgentSnapshot> {
        let mut v: Vec<_> = self.agents.values().collect();
        v.sort_by_key(|a| a.hostname.clone());
        v
    }

    fn persisted(&self) -> PersistedState {
        PersistedState {
            agents: self.agents.values().cloned().collect(),
            history: self.history.clone(),
        }
    }

    pub fn to_prometheus(&self) -> String {
        let mut lines = Vec::new();
        {
            let mut fam = |name: &str, help: &str| {
                lines.push(format!("# HELP {} {}", name, help));
                lines.push(format!("# TYPE {} gauge", name));
            };
            fam("monitoredb_up", "1 if agent has collected data");
            fam("monitoredb_agent_last_seen_timestamp", "Unix timestamp of last agent snapshot");
            fam("monitoredb_cpu_percent", "CPU usage percent");
            fam("monitoredb_memory_percent", "Memory usage percent");
            fam("monitoredb_disk_usage_percent", "Disk usage percent per mount");
            fam("monitoredb_disk_available_gb", "Disk available GB per mount");
            fam("monitoredb_service_status", "Windows service running (1) or not (0)");
            fam("monitoredb_iis_active_requests", "Active IIS requests");
            fam("monitoredb_iis_app_pool_cpu", "IIS app pool CPU percent");
            fam("monitoredb_iis_app_pool_memory_mb", "IIS app pool memory MB");
            fam("monitoredb_iis_app_pool_active_requests", "Active requests per IIS app pool");
            fam("monitoredb_sqlserver_connections", "SQL Server user connections");
            fam("monitoredb_sqlserver_buffer_cache_hit_ratio", "SQL Server buffer cache hit ratio percent");
            fam("monitoredb_sqlserver_page_life_expectancy", "SQL Server page life expectancy seconds");
            fam("monitoredb_database_size_mb", "SQL Server database size MB");
            fam("monitoredb_job_status", "SQL Agent job last run succeeded (1) or not (0)");
        }

        for a in self.agents.values() {
            let lbl = format!("agent=\"{}\",hostname=\"{}\"", escape_label(&a.agent_id), escape_label(&a.hostname));
            let has_data = a.windows.is_some() || a.iis.is_some() || a.sql_server.is_some();
            lines.push(format!("monitoredb_up{{{}}} {}", lbl, i32::from(has_data)));
            lines.push(format!("monitoredb_agent_last_seen_timestamp{{{}}} {}", lbl, a.timestamp.timestamp()));

            if let Some(w) = &a.windows {
                lines.push(format!("monitoredb_cpu_percent{{{}}} {:.2}", lbl, w.cpu_percent));
                lines.push(format!("monitoredb_memory_percent{{{}}} {:.2}", lbl, w.memory_percent));
                for disk in &w.disks {
                    let m = escape_label(&disk.mount.replace(' ', "_").replace(':', ""));
                    lines.push(format!("monitoredb_disk_usage_percent{{{},mount=\"{}\"}} {:.2}", lbl, m, disk.usage_percent));
                    lines.push(format!("monitoredb_disk_available_gb{{{},mount=\"{}\"}} {:.2}", lbl, m, disk.available_gb));
                }
                for svc in &w.services {
                    let n = escape_label(&svc.name.replace(' ', "_"));
                    lines.push(format!("monitoredb_service_status{{{},name=\"{}\"}} {}", lbl, n, i32::from(svc.status == "Running")));
                }
            }

            if let Some(i) = &a.iis {
                lines.push(format!("monitoredb_iis_active_requests{{{}}} {}", lbl, i.active_requests.len()));
                for pool in &i.app_pools {
                    let n = escape_label(&pool.name.replace(' ', "_"));
                    lines.push(format!("monitoredb_iis_app_pool_cpu{{{},pool=\"{}\"}} {:.2}", lbl, n, pool.cpu_percent));
                    lines.push(format!("monitoredb_iis_app_pool_memory_mb{{{},pool=\"{}\"}} {:.2}", lbl, n, pool.memory_mb));
                    if let Some(c) = pool.active_requests {
                        lines.push(format!("monitoredb_iis_app_pool_active_requests{{{},pool=\"{}\"}} {}", lbl, n, c));
                    }
                }
            }

            if let Some(s) = &a.sql_server {
                if let Some(p) = &s.performance {
                    lines.push(format!("monitoredb_sqlserver_connections{{{}}} {}", lbl, p.user_connections));
                    lines.push(format!("monitoredb_sqlserver_buffer_cache_hit_ratio{{{}}} {:.2}", lbl, p.buffer_cache_hit_ratio));
                    lines.push(format!("monitoredb_sqlserver_page_life_expectancy{{{}}} {}", lbl, p.page_life_expectancy));
                }
                for db in &s.databases {
                    let n = escape_label(&db.name.replace(' ', "_"));
                    lines.push(format!("monitoredb_database_size_mb{{{},database=\"{}\"}} {:.2}", lbl, n, db.size_mb));
                }
                for j in &s.jobs {
                    let n = escape_label(&j.name.replace(' ', "_"));
                    lines.push(format!("monitoredb_job_status{{{},name=\"{}\"}} {}", lbl, n, i32::from(j.last_run_status == "Succeeded")));
                }
            }
        }

        lines.push(String::new());
        lines.join("\n")
    }
}

fn escape_label(v: &str) -> String {
    v.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n")
}

pub async fn save_if_dirty(state: &SharedState) {
    let data = {
        let guard = state.read().await;
        if !guard.dirty {
            return;
        }
        guard.persisted()
    };
    if write_state(data).await {
        state.write().await.dirty = false;
    }
}

pub async fn save(state: &SharedState) {
    let data = { state.read().await.persisted() };
    write_state(data).await;
}

async fn write_state(data: PersistedState) -> bool {
    let json = match serde_json::to_string(&data) {
        Ok(j) => j,
        Err(e) => {
            error!("Falha ao serializar estado: {}", e);
            return false;
        }
    };
    let _ = std::fs::create_dir_all(DATA_DIR);
    match tokio::task::spawn_blocking(move || std::fs::write(DATA_FILE, json)).await {
        Ok(Ok(())) => true,
        Ok(Err(e)) => {
            error!("Falha ao salvar {}: {}", DATA_FILE, e);
            false
        }
        Err(e) => {
            error!("Falha na task de persistência: {}", e);
            false
        }
    }
}
