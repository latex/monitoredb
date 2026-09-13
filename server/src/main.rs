mod api;
mod cli;
mod logger;
mod state;

use crate::state::{AppState, SharedState};
use clap::Parser;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::RwLock;
use tokio::time::interval;
use tracing::info;
use axum::Router;
use tower_http::cors::CorsLayer;
use tower_http::services::ServeDir;

#[tokio::main]
async fn main() {
    let _ = dotenvy::dotenv();
    logger::init();
    let cli = cli::Cli::parse();

    let token = cli.token.filter(|t| !t.trim().is_empty());
    let state: SharedState = Arc::new(RwLock::new(AppState::new(token.clone())));
    let addr = format!("{}:{}", cli.bind, cli.port);

    let api_routes = api::routes::router(state.clone());

    let public_path = std::path::Path::new("../public");
    let app = Router::new()
        .nest("/api", api_routes)
        .fallback_service(ServeDir::new(
            if public_path.exists() { "../public" } else { "public" }
        ))
        .layer(CorsLayer::permissive());

    let saver = state.clone();
    tokio::spawn(async move {
        let mut tick = interval(Duration::from_secs(30));
        tick.tick().await;
        loop {
            tick.tick().await;
            state::save_if_dirty(&saver).await;
        }
    });

    info!("Servidor MonitoreDB iniciado em http://{}", addr);
    if token.is_some() {
        info!("Autenticação por token ativada (ingest e escrita)");
    } else {
        info!("Sem token configurado: API aberta (defina MONITOREDB_TOKEN)");
    }

    let listener = tokio::net::TcpListener::bind(&addr)
        .await
        .expect("Falha ao bind");

    axum::serve(listener, app)
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
        .expect("Falha ao iniciar servidor");

    state::save(&state).await;
    info!("Estado salvo. Encerrando.");
}
