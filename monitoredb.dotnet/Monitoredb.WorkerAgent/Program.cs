using Monitoredb.WorkerAgent;

// Parse de argumentos de linha de comando (compatível com o agente Rust)
// --server http://host:3000 | --agent-id nome | --interval 30 | --token xxx
// --sql-host host | --sql-port 1433 | --sql-user sa | --sql-pass senha | --once
var cliArgs = CliParser.Parse(args);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(cliArgs);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();