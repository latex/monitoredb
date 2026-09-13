using Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// 1. Serviço de Métricas (Prometheus) - Configuração Global e Centralizada
builder.Services.AddSingleton<Registry>(); // Instancia o registro global de métricas
// Adiciona um Hosted Service que será responsável por rodar o coletor em background
builder.Services.AddHostedService<MetricCollectionWorker>(); 

var app = builder.Build();


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();


// 2. Endpoint de Métricas: expõe os dados coletados no formato Prometheus
app.MapGet("/metrics", context => {
    var registry = context.RequestServices.GetRequiredService<Registry>();
    // Retorna o conteúdo das métricas formatado em texto puro Prometheus
    return Results.Content(registry.Metrics, "text/plain; version=0.0.4; charset=utf-8"); 
});

// 3. Endpoint de Saúde (Health Check): Verificação básica se o serviço está rodando
app.MapGet("/health", () => Results.Ok("Service operational and ready for metrics collection"));


app.Run();