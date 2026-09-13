use clap::Parser;

#[derive(Parser, Debug, Clone)]
#[command(name = "monitoredb-agent", version, about = "MonitoreDB - Agente de coleta")]
pub struct Cli {
    #[arg(short, long, env = "MONITOREDB_AGENT_SERVER", default_value = "http://localhost:3000", help = "URL do servidor")]
    pub server: String,

    #[arg(short, long, env = "MONITOREDB_AGENT_ID", help = "Identificador do agente (padrão: hostname)")]
    pub agent_id: Option<String>,

    #[arg(short, long, env = "MONITOREDB_AGENT_INTERVAL", default_value = "30", help = "Intervalo de coleta em segundos")]
    pub interval: u64,

    #[arg(long, env = "MONITOREDB_AGENT_TOKEN", help = "Token de autenticação do servidor")]
    pub token: Option<String>,

    #[arg(long, env = "MONITOREDB_SQL_HOST", help = "SQL Server host")]
    pub sql_host: Option<String>,

    #[arg(long, env = "MONITOREDB_SQL_PORT", default_value = "1433", help = "SQL Server port")]
    pub sql_port: Option<u16>,

    #[arg(long, env = "MONITOREDB_SQL_USERNAME", help = "SQL Server username")]
    pub sql_user: Option<String>,

    #[arg(long, env = "MONITOREDB_SQL_PASSWORD", help = "SQL Server password")]
    pub sql_pass: Option<String>,

    #[arg(long, help = "Coletar uma vez e sair")]
    pub once: bool,
}
