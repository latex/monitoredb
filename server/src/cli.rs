use clap::Parser;

#[derive(Parser, Debug, Clone)]
#[command(name = "monitoredb-server", version, about = "MonitoreDB - Servidor central")]
pub struct Cli {
    #[arg(long, env = "MONITOREDB_SERVER_PORT", default_value = "3000", help = "Porta do servidor")]
    pub port: u16,

    #[arg(long, env = "MONITOREDB_SERVER_BIND", default_value = "0.0.0.0", help = "Endereço de bind")]
    pub bind: String,

    #[arg(long, env = "MONITOREDB_TOKEN", help = "Token de autenticação (ingest e escrita; vazio = sem auth)")]
    pub token: Option<String>,
}
