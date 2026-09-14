using Microsoft.Extensions.Hosting.WindowsServices;
using Monitoredb.WorkerAgent;

// O agente roda de 3 formas com a mesma build:
//   - Console/SSH .......... dotnet Monitoredb.WorkerAgent.dll [--flags]
//   - Servico do Windows ... registrado via sc.exe (UseWindowsService)
//   - Container ............ docker compose
//
// Precedencia de configuracao:
//   flags CLI  >  appsettings.json (Monitoredb:*)  >  variaveis MONITOREDB_*
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Servico roda com CWD = C:\Windows\System32; garante que appsettings.json
    // seja lido do diretorio da aplicacao.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(o => o.ServiceName = "MonitoredbAgent");

var options = CliParser.Parse(args, builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
