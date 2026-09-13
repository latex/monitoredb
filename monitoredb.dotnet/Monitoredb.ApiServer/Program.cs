using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Monitoredb.ApiServer;
using Monitoredb.CommonCollectors.Models;

var builder = WebApplication.CreateBuilder(args);

// Token de autenticação (MONITOREDB_TOKEN)
var token = builder.Configuration["MONITOREDB_TOKEN"];
var dataFile = builder.Configuration["MONITOREDB_DATA_FILE"];
var state = new AppState(token, dataFile);
builder.Services.AddSingleton(state);

// JSON snake_case (compatível com o dashboard e o agente Rust)
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// CORS permissivo (como o Rust original)
builder.Services.AddCors(c => c.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var port = builder.Configuration["PORT"] ?? "3000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// Persistência periódica (30s)
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    while (await timer.WaitForNextTickAsync())
        state.SaveIfDirty();
});

app.UseCors();

// ---- API ----

// POST /api/ingest — recebe snapshot do agente
app.MapPost("/api/ingest", (HttpContext ctx, AgentSnapshot snapshot) =>
{
    if (!state.IsAuthorized(ctx.Request.Headers.Authorization))
        return Results.Json(new { status = "error", message = "nao autorizado" }, statusCode: 401);
    state.Ingest(snapshot);
    return Results.Json(new { status = "ok" });
});

// GET /api/agents — lista agentes
app.MapGet("/api/agents", () =>
{
    var agents = state.AllAgents().Select(a => new
    {
        agent_id = a.AgentId,
        hostname = a.Hostname,
        timestamp = a.Timestamp,
        has_windows = a.Windows is not null,
        has_iis = a.Iis is not null,
        has_sql_server = a.SqlServer is not null,
    }).ToList();
    return Results.Json(new { agents, count = agents.Count });
});

// POST /api/agents — registra agente
app.MapPost("/api/agents", (HttpContext ctx, JsonElement body) =>
{
    if (!state.IsAuthorized(ctx.Request.Headers.Authorization))
        return Results.Json(new { status = "error", message = "nao autorizado" }, statusCode: 401);
    var id = body.TryGetProperty("agent_id", out var v) ? v.GetString()?.Trim() ?? "" : "";
    var hostname = body.TryGetProperty("hostname", out var h) ? h.GetString()?.Trim() ?? "" : "";
    if (id.Length == 0)
        return Results.Json(new { status = "error", message = "agent_id é obrigatório" }, statusCode: 400);
    state.Register(id, hostname);
    return Results.Json(new { status = "ok" });
});

// GET /api/agent/{id} — snapshot do agente
app.MapGet("/api/agent/{id}", (string id) =>
{
    var agent = state.GetAgent(id);
    return agent is null
        ? Results.Json(new { error = "agent not found" }, statusCode: 404)
        : Results.Json(agent, AppState.JsonOpts);
});

// DELETE /api/agent/{id} — remove agente
app.MapDelete("/api/agent/{id}", (HttpContext ctx, string id) =>
{
    if (!state.IsAuthorized(ctx.Request.Headers.Authorization))
        return Results.Json(new { status = "error", message = "nao autorizado" }, statusCode: 401);
    return state.Remove(id)
        ? Results.Json(new { status = "ok" })
        : Results.Json(new { status = "error", message = "agente não encontrado" }, statusCode: 404);
});

// GET /api/agent/{id}/history — histórico do agente
app.MapGet("/api/agent/{id}/history", (string id) =>
{
    var samples = state.History(id);
    return Results.Json(new { agent_id = id, samples }, AppState.JsonOpts);
});

// GET /api/latest — snapshot mais recente
app.MapGet("/api/latest", () =>
{
    var latest = state.Latest();
    return latest is null
        ? Results.Json(new { error = "no data" }, statusCode: 404)
        : Results.Json(latest, AppState.JsonOpts);
});

// GET /api/metrics — formato Prometheus
app.MapGet("/api/metrics", () => Results.Text(
    PrometheusFormatter.Format(state.AllAgents()),
    "text/plain; charset=utf-8"));

// GET /api/health — health check
app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

// ---- Frontend (public/) ----
var publicPath = Path.Combine(app.Environment.ContentRootPath, "public");
if (!Directory.Exists(publicPath))
    publicPath = Path.Combine(Directory.GetCurrentDirectory(), "public");
app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = ["index.html"] });
app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publicPath) });

// Fallback para SPA
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publicPath),
});

Console.WriteLine($"[server] MonitoreDB iniciado em http://0.0.0.0:{port}");
Console.WriteLine(state.Token is null
    ? "[server] Sem token configurado: API aberta (defina MONITOREDB_TOKEN)"
    : "[server] Autenticação por token ativada (ingest e escrita)");

app.Run();

// Persistência final
state.Save();

/// <summary>Ponto de entrada exposto para testes de integração (WebApplicationFactory).</summary>
public partial class Program { }