use axum::extract::State;
use axum::http::{header, HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{delete, get, post};
use axum::{Json, Router};
use crate::state::{AgentSnapshot, AppState, SharedState};

pub fn router(state: SharedState) -> Router {
    Router::new()
        .route("/ingest", post(ingest))
        .route("/agents", get(list_agents))
        .route("/agents", post(register_agent))
        .route("/agent/{id}", get(get_agent))
        .route("/agent/{id}", delete(delete_agent))
        .route("/agent/{id}/history", get(get_history))
        .route("/latest", get(get_latest))
        .route("/metrics", get(get_metrics))
        .route("/health", get(get_health))
        .with_state(state)
}

fn is_authorized(state: &AppState, headers: &HeaderMap) -> bool {
    let Some(token) = state.token.as_deref() else { return true; };
    headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .map(|v| v == format!("Bearer {}", token))
        .unwrap_or(false)
}

fn unauthorized() -> Response {
    (
        StatusCode::UNAUTHORIZED,
        Json(serde_json::json!({"status": "error", "message": "nao autorizado"})),
    )
        .into_response()
}

async fn ingest(
    State(state): State<SharedState>,
    headers: HeaderMap,
    Json(snapshot): Json<AgentSnapshot>,
) -> Response {
    if !is_authorized(&*state.read().await, &headers) {
        return unauthorized();
    }
    let mut guard = state.write().await;
    guard.ingest(snapshot);
    (StatusCode::OK, Json(serde_json::json!({"status": "ok"}))).into_response()
}

async fn list_agents(State(state): State<SharedState>) -> Json<serde_json::Value> {
    let guard = state.read().await;
    let agents: Vec<serde_json::Value> = guard.all_agents().iter().map(|a| {
        serde_json::json!({
            "agent_id": a.agent_id,
            "hostname": a.hostname,
            "timestamp": a.timestamp,
            "has_windows": a.windows.is_some(),
            "has_iis": a.iis.is_some(),
            "has_sql_server": a.sql_server.is_some(),
        })
    }).collect();
    Json(serde_json::json!({"agents": agents, "count": agents.len()}))
}

async fn get_agent(
    State(state): State<SharedState>,
    axum::extract::Path(id): axum::extract::Path<String>,
) -> Json<serde_json::Value> {
    let guard = state.read().await;
    match guard.agents.get(&id) {
        Some(snapshot) => Json(serde_json::to_value(snapshot).unwrap_or_default()),
        None => Json(serde_json::json!({"error": "agent not found"})),
    }
}

async fn get_history(
    State(state): State<SharedState>,
    axum::extract::Path(id): axum::extract::Path<String>,
) -> Json<serde_json::Value> {
    let guard = state.read().await;
    match guard.history(&id) {
        Some(samples) => Json(serde_json::json!({"agent_id": id, "samples": samples})),
        None => Json(serde_json::json!({"agent_id": id, "samples": []})),
    }
}

async fn register_agent(
    State(state): State<SharedState>,
    headers: HeaderMap,
    Json(body): Json<serde_json::Value>,
) -> Response {
    if !is_authorized(&*state.read().await, &headers) {
        return unauthorized();
    }
    let id = body.get("agent_id").and_then(|v| v.as_str()).unwrap_or("").trim().to_string();
    let hostname = body.get("hostname").and_then(|v| v.as_str()).unwrap_or("").trim().to_string();
    if id.is_empty() {
        return (
            StatusCode::BAD_REQUEST,
            Json(serde_json::json!({"status": "error", "message": "agent_id é obrigatório"})),
        )
            .into_response();
    }
    let name = if hostname.is_empty() { id.clone() } else { hostname };
    let mut guard = state.write().await;
    guard.register(id, name);
    (StatusCode::OK, Json(serde_json::json!({"status": "ok"}))).into_response()
}

async fn delete_agent(
    State(state): State<SharedState>,
    headers: HeaderMap,
    axum::extract::Path(id): axum::extract::Path<String>,
) -> Response {
    if !is_authorized(&*state.read().await, &headers) {
        return unauthorized();
    }
    let mut guard = state.write().await;
    match guard.remove(&id) {
        Some(_) => (StatusCode::OK, Json(serde_json::json!({"status": "ok"}))).into_response(),
        None => (
            StatusCode::NOT_FOUND,
            Json(serde_json::json!({"status": "error", "message": "agente não encontrado"})),
        )
            .into_response(),
    }
}

async fn get_latest(State(state): State<SharedState>) -> Json<serde_json::Value> {
    let guard = state.read().await;
    match guard.latest() {
        Some(snapshot) => Json(serde_json::to_value(snapshot).unwrap_or_default()),
        None => Json(serde_json::json!({"error": "no data"})),
    }
}

async fn get_metrics(State(state): State<SharedState>) -> impl IntoResponse {
    let guard = state.read().await;
    let metrics = guard.to_prometheus();
    (StatusCode::OK, [("Content-Type", "text/plain; charset=utf-8")], metrics)
}

async fn get_health() -> Json<serde_json::Value> {
    Json(serde_json::json!({"status": "ok"}))
}
