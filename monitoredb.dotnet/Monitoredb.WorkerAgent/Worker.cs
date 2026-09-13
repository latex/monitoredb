using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Monitoredb.CommonCollectors.Collectors;
using Monitoredb.CommonCollectors.Models;

namespace Monitoredb.WorkerAgent;

public sealed class AgentOptions
{
    public string Server { get; init; } = "http://localhost:3000";
    public string? AgentId { get; init; }
    public int IntervalSeconds { get; init; } = 30;
    public string? Token { get; init; }
    public string? SqlHost { get; init; }
    public int SqlPort { get; init; } = 1433;
    public string? SqlUser { get; init; }
    public string? SqlPassword { get; init; }
    public bool Once { get; init; }
}

public sealed class Worker(ILogger<Worker> logger, AgentOptions options) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agentId = options.AgentId ?? Environment.MachineName;
        var serverUrl = options.Server.TrimEnd('/');
        var ingestUrl = $"{serverUrl}/api/ingest";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(options.Token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        logger.LogInformation("Agente {AgentId} iniciado. Enviando para {Url}", agentId, ingestUrl);

        do
        {
            await CollectAndSend(client, agentId, ingestUrl, stoppingToken);
            if (options.Once)
                break;
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }

    private async Task CollectAndSend(HttpClient client, string agentId, string ingestUrl, CancellationToken ct)
    {
        var hostname = Environment.MachineName;

        logger.LogInformation("Coletando métricas Windows...");
        var windows = await Task.Run(WindowsCollector.Collect, ct);

        SqlServerSnapshot? sqlServer = null;
        if (!string.IsNullOrEmpty(options.SqlHost))
        {
            logger.LogInformation("Coletando métricas SQL Server de {Host}:{Port}...", options.SqlHost, options.SqlPort);
            sqlServer = await Task.Run(() =>
                SqlServerCollector.Collect(options.SqlHost!, options.SqlPort, options.SqlUser, options.SqlPassword), ct);
            if (sqlServer is null)
                logger.LogInformation("SQL Server não disponível");
        }

        IisSnapshot? iis = null;
        if (OperatingSystem.IsWindows())
        {
            logger.LogInformation("Coletando métricas IIS...");
            iis = await Task.Run(IisCollector.Collect, ct);
        }

        var payload = new AgentSnapshot
        {
            AgentId = agentId,
            Hostname = hostname,
            Timestamp = DateTime.UtcNow,
            Windows = windows,
            Iis = iis,
            SqlServer = sqlServer,
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        logger.LogInformation("Enviando dados para {Url}...", ingestUrl);
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(ingestUrl, content, ct);
            if (resp.IsSuccessStatusCode)
                logger.LogInformation("Dados enviados com sucesso");
            else
                logger.LogError("Servidor retornou {Status}", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao enviar dados");
        }
    }
}