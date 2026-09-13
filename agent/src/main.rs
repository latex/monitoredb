mod cli;
mod collectors;
mod logger;

use clap::Parser;
use chrono::Utc;
use std::time::Duration;
use tokio::time::interval;
use tracing::{error, info};

#[tokio::main]
async fn main() {
    let _ = dotenvy::dotenv();
    logger::init();
    let cli = cli::Cli::parse();

    let agent_id = cli.agent_id.clone().unwrap_or_else(|| {
        whoami::fallible::hostname().unwrap_or_else(|_| "unknown".into())
    });
    let server_url = cli.server.trim_end_matches('/').to_string();
    let ingest_url = format!("{}/api/ingest", server_url);
    let token = cli.token.filter(|t| !t.trim().is_empty());

    let sql_host = cli.sql_host;
    let sql_port = cli.sql_port.unwrap_or(1433);
    let sql_user = cli.sql_user;
    let sql_pass = cli.sql_pass;

    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(30))
        .build()
        .expect("falha ao criar client HTTP");

    info!("Agente {} iniciado. Enviando para {}", agent_id, ingest_url);

    if cli.once {
        collect_and_send(&client, &agent_id, &ingest_url, token.as_deref(), sql_host.as_deref(), sql_port, &sql_user, &sql_pass).await;
        return;
    }

    let mut tick = interval(Duration::from_secs(cli.interval));
    loop {
        tick.tick().await;
        collect_and_send(&client, &agent_id, &ingest_url, token.as_deref(), sql_host.as_deref(), sql_port, &sql_user, &sql_pass).await;
    }
}

async fn collect_and_send(client: &reqwest::Client, agent_id: &str, ingest_url: &str, token: Option<&str>, sql_host: Option<&str>, sql_port: u16, sql_user: &Option<String>, sql_pass: &Option<String>) {
    let hostname = whoami::fallible::hostname().unwrap_or_else(|_| "unknown".into());

    info!("Coletando métricas Windows...");
    let windows = tokio::task::spawn_blocking(|| {
        collectors::windows::collect()
    }).await;

    let sql_server = if let Some(host) = sql_host {
        info!("Coletando métricas SQL Server de {}:{}...", host, sql_port);
        let h = host.to_string();
        let u = sql_user.clone();
        let p = sql_pass.clone();
        Some(tokio::task::spawn_blocking(move || {
            collectors::sqlserver::collect(&h, sql_port, &u, &p)
        }).await)
    } else {
        None
    };

    let iis = if cfg!(target_os = "windows") {
        Some(tokio::task::spawn_blocking(|| {
            collectors::iis::collect()
        }).await)
    } else {
        None
    };

    let windows_ok = match windows {
        Ok(w) => Some(w),
        Err(e) => { error!("Erro coleta Windows: {}", e); None }
    };

    let iis_ok = match iis {
        Some(Ok(i)) => Some(i),
        Some(Err(e)) => { error!("Erro coleta IIS: {}", e); None }
        None => None,
    };

    let sql_ok = match sql_server {
        Some(Ok(Some(s))) => Some(s),
        Some(Ok(None)) => { info!("SQL Server não disponível"); None }
        Some(Err(e)) => { error!("Erro coleta SQL Server: {}", e); None }
        None => None,
    };

    let payload = serde_json::json!({
        "agent_id": agent_id,
        "hostname": hostname,
        "timestamp": Utc::now(),
        "windows": windows_ok,
        "iis": iis_ok,
        "sql_server": sql_ok,
    });

    info!("Enviando dados para {}...", ingest_url);
    let mut request = client.post(ingest_url).json(&payload);
    if let Some(t) = token {
        request = request.bearer_auth(t);
    }
    match request.send().await {
        Ok(resp) if resp.status().is_success() => info!("Dados enviados com sucesso"),
        Ok(resp) => error!("Servidor retornou {}", resp.status()),
        Err(e) => error!("Erro ao enviar: {}", e),
    }
}
