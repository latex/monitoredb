use serde::{Deserialize, Serialize};
use std::process::Command;
use tracing::warn;

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
pub struct SqlServerDatabase { pub name: String, pub state: String, pub size_mb: f64, pub recovery_model: String }
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerConnection { pub session_id: i32, pub hostname: String, pub program_name: String, pub login_name: String, #[serde(alias = "database_name")] pub database: String, pub status: String, pub cpu_time_ms: i64 }
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerQuery { pub session_id: i32, #[serde(alias = "database_name")] pub database: String, #[serde(alias = "user_name")] pub user: String, pub status: String, pub wait_type: String, pub cpu_time_ms: i64, pub total_elapsed_ms: i64, pub query_text: String }
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerBackup { #[serde(alias = "database_name")] pub database: String, pub backup_type: String, pub last_backup: Option<String>, pub size_mb: f64 }
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerJob { pub name: String, pub last_run: Option<String>, pub last_run_status: String, pub enabled: bool }
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SqlServerPerformance { pub buffer_cache_hit_ratio: f64, pub page_life_expectancy: i64, pub batch_requests_per_sec: i64, pub deadlocks_per_sec: i64, pub user_connections: i32 }

pub fn collect(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Option<SqlServerSnapshot> {
    let version = query_sql(host, port, username, password, "SET NOCOUNT ON; SELECT @@VERSION");
    if version.is_empty() {
        warn!("sqlcmd não disponível ou SQL Server {}:{} inacessível", host, port);
        return None;
    }

    let edition = query_sql(host, port, username, password, "SET NOCOUNT ON; SELECT SERVERPROPERTY('Edition')");
    let uptime = query_sql(host, port, username, password,
        "SET NOCOUNT ON; SELECT CAST(DATEDIFF(MINUTE, sqlserver_start_time, GETDATE()) AS FLOAT) / 60 FROM sys.dm_os_sys_info"
    ).trim().parse::<f64>().unwrap_or(0.0);

    Some(SqlServerSnapshot {
        server_name: host.to_string(),
        version: version.trim().to_string(),
        edition: edition.trim().to_string(),
        uptime_hours: uptime,
        databases: collect_databases(host, port, username, password),
        connections: collect_connections(host, port, username, password),
        running_queries: collect_queries(host, port, username, password),
        backups: collect_backups(host, port, username, password),
        jobs: collect_jobs(host, port, username, password),
        performance: collect_performance(host, port, username, password),
    })
}

fn build_args(host: &str, port: u16, username: &Option<String>, password: &Option<String>, query: &str) -> Vec<String> {
    let mut args = vec!["-S".into(), format!("{},{}", host, port), "-Q".into(), query.into(), "-h".into(), "-1".into(), "-W".into(), "-C".into()];
    match (username, password) {
        (Some(u), Some(p)) => { args.extend(["-U".into(), u.clone(), "-P".into(), p.clone()]); }
        _ => { args.push("-E".into()); }
    }
    args
}

fn clean_output(raw: &str) -> String {
    raw.lines()
        .map(str::trim_end)
        .filter(|l| {
            let t = l.trim();
            !t.is_empty() && !(t.starts_with('(') && t.ends_with("affected)"))
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn query_sql(host: &str, port: u16, username: &Option<String>, password: &Option<String>, query: &str) -> String {
    let args = build_args(host, port, username, password, query);
    match Command::new("sqlcmd").args(&args).output() {
        Ok(o) if o.status.success() => clean_output(&String::from_utf8_lossy(&o.stdout)),
        Ok(o) => { warn!("sqlcmd stderr: {}", String::from_utf8_lossy(&o.stderr)); String::new() }
        Err(e) => { warn!("sqlcmd não encontrado: {}", e); String::new() }
    }
}

fn query_json<T: serde::de::DeserializeOwned>(host: &str, port: u16, username: &Option<String>, password: &Option<String>, query: &str) -> Vec<T> {
    let json = format!("SET NOCOUNT ON;\n{}\nFOR JSON PATH", query);
    let result = query_sql(host, port, username, password, &json);
    if result.is_empty() { return vec![]; }
    let cleaned: String = result.lines().filter(|l| !l.starts_with("---")).collect::<Vec<_>>().join("");
    let trimmed = cleaned.trim();
    if trimmed.is_empty() || trimmed.eq_ignore_ascii_case("null") { return vec![]; }
    serde_json::from_str(&cleaned).unwrap_or_else(|e| {
        warn!("Falha ao desserializar {} : {}", std::any::type_name::<T>(), e);
        serde_json::from_str(&format!("[{}]", cleaned)).unwrap_or_else(|e| {
            warn!("Falha ao desserializar {} como array: {} | payload: {}", std::any::type_name::<T>(), e, trimmed.chars().take(200).collect::<String>());
            Vec::new()
        })
    })
}

fn collect_databases(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Vec<SqlServerDatabase> {
    query_json(host, port, username, password, r#"
SELECT d.name, d.state_desc AS state,
CAST(SUM(CAST(mf.size AS BIGINT))*8.0/1024.0 AS DECIMAL(18,2)) AS size_mb,
d.recovery_model_desc AS recovery_model
FROM sys.databases d LEFT JOIN sys.master_files mf ON d.database_id=mf.database_id
WHERE d.database_id>4 GROUP BY d.name,d.state_desc,d.recovery_model_desc ORDER BY d.name"#)
}

fn collect_connections(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Vec<SqlServerConnection> {
    query_json(host, port, username, password, r#"
SELECT s.session_id, ISNULL(s.host_name,'') AS hostname, ISNULL(s.program_name,'') AS program_name,
ISNULL(s.login_name,'') AS login_name, ISNULL(DB_NAME(s.database_id),'') AS database_name,
s.status, s.cpu_time AS cpu_time_ms
FROM sys.dm_exec_sessions s WHERE s.is_user_process=1 AND s.session_id <> @@SPID ORDER BY s.cpu_time DESC"#)
}

fn collect_queries(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Vec<SqlServerQuery> {
    query_json(host, port, username, password, r#"
SELECT r.session_id, ISNULL(DB_NAME(r.database_id),'') AS database_name,
ISNULL(s.login_name,'') AS user_name, r.status, ISNULL(r.wait_type,'') AS wait_type,
r.cpu_time AS cpu_time_ms, r.total_elapsed_time AS total_elapsed_ms,
ISNULL((SELECT TOP 1 SUBSTRING(text,(r.statement_start_offset/2)+1,
CASE WHEN r.statement_end_offset=-1 THEN 4000 ELSE (r.statement_end_offset-r.statement_start_offset)/2 END)
FROM sys.dm_exec_sql_text(r.sql_handle)),'') AS query_text
FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON r.session_id=s.session_id
WHERE s.is_user_process=1 AND r.session_id <> @@SPID ORDER BY r.cpu_time DESC"#)
}

fn collect_backups(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Vec<SqlServerBackup> {
    query_json(host, port, username, password, r#"
SELECT d.name AS database_name,
CASE bs.type WHEN 'D' THEN 'FULL' WHEN 'I' THEN 'DIFF' WHEN 'L' THEN 'LOG' ELSE 'OTHER' END AS backup_type,
ISNULL(CONVERT(VARCHAR,MAX(bs.backup_finish_date),120),'') AS last_backup,
ISNULL(CAST(MAX(bs.backup_size)/1024.0/1024.0 AS DECIMAL(18,2)),0) AS size_mb
FROM sys.databases d LEFT JOIN msdb.dbo.backupset bs ON d.name=bs.database_name
WHERE d.database_id>4 GROUP BY d.name,bs.type ORDER BY d.name"#)
}

fn collect_jobs(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Vec<SqlServerJob> {
    query_json(host, port, username, password, r#"
SELECT j.name, ISNULL(CONVERT(VARCHAR,MAX(jh.run_date),120),'') AS last_run,
ISNULL(CASE jh.run_status WHEN 1 THEN 'Succeeded' WHEN 0 THEN 'Failed' WHEN 2 THEN 'Retry' WHEN 3 THEN 'Cancelled' ELSE 'Unknown' END,'Never') AS last_run_status,
j.enabled FROM msdb.dbo.sysjobs j
LEFT JOIN msdb.dbo.sysjobhistory jh ON j.job_id=jh.job_id
AND jh.instance_id=(SELECT MAX(instance_id) FROM msdb.dbo.sysjobhistory WHERE job_id=j.job_id)
GROUP BY j.name,j.enabled,jh.run_status ORDER BY j.name"#)
}

fn collect_performance(host: &str, port: u16, username: &Option<String>, password: &Option<String>) -> Option<SqlServerPerformance> {
    let query = r#"
SET NOCOUNT ON;
SELECT
(SELECT TOP 1 CAST(cntr_value AS FLOAT) FROM sys.dm_os_performance_counters WHERE counter_name='Buffer cache hit ratio' AND object_name LIKE'%Buffer Manager%')
/NULLIF((SELECT TOP 1 CAST(cntr_value AS FLOAT) FROM sys.dm_os_performance_counters WHERE counter_name='Buffer cache hit ratio base' AND object_name LIKE'%Buffer Manager%'),0)*100 AS buffer_cache_hit_ratio,
(SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Page life expectancy' AND object_name LIKE'%Buffer Manager%') AS page_life_expectancy,
(SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Batch Requests/sec' AND object_name LIKE'%SQL Statistics%') AS batch_requests_per_sec,
(SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Number of Deadlocks/sec' AND object_name LIKE'%Locks%') AS deadlocks_per_sec,
(SELECT TOP 1 cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='User Connections' AND object_name LIKE'%General Statistics%') AS user_connections
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER"#;
    let result = query_sql(host, port, username, password, query);
    serde_json::from_str(&result).ok()
}
