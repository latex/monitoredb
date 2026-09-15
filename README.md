# MonitoreDB

Monitoramento centralizado de servidores Windows (IIS e SQL Server).

- **Servidor** — recebe os snapshots, mantém o histórico, serve um dashboard web e expõe
  as métricas no formato Prometheus.
- **Cliente (agente)** — instalado em cada host monitorado; coleta CPU, memória, disco,
  uptime, IIS e SQL Server, e envia para o servidor.

Implementação atual: **.NET 10** (`monitoredb.dotnet/`). A pasta `server/` e `agent/`
(Rust) é a implementação **legada**, mantida como referência.

```
   Host monitorado (Windows)                 Servidor (Linux/Windows/Docker)
 ┌───────────────────────────┐             ┌──────────────────────────────┐
 │  Cliente MonitoreDB       │  HTTP POST  │  MonitoreDB Server (:3000)   │
 │  (Monitoredb.WorkerAgent) │ ──────────► │  /api/ingest                 │
 │  coleta: Windows/IIS/SQL  │  Bearer     │  Dashboard + /api/metrics    │
 └───────────────────────────┘             └──────────────────────────────┘
```

---

## Requisitos

| Componente | Requisitos |
|------------|-----------|
| Servidor via Docker | Docker Engine 24+ e Docker Compose v2 |
| Servidor nativo Linux | systemd + root/sudo |
| Servidor/Cliente via .NET | .NET SDK 10 (build) ou runtime ASP.NET Core 10 |
| Cliente (Windows) | Windows 10/Server 2016+; .NET Runtime 10 (ou self-contained) |

---

# Como subir a aplicação (servidor)

Escolha **uma** das três formas abaixo. A porta padrão é **3000**.

## Opção A — Docker (recomendado)

O `docker-compose.yml` sobe o servidor, um SQL Server 2022 e um agente de exemplo.

```bash
# 1. Clone o repositório
git clone https://github.com/latex/monitoredb.git
cd monitoredb

# 2. Crie o arquivo de configuração
cp .env.example .env

# 3. Edite o .env e defina a senha do SQL Server (obrigatório)
#    MSSQL_SA_PASSWORD=uma-senha-forte
#    MONITOREDB_TOKEN=seu-token-de-autenticacao   (recomendado)

# 4. Suba a stack
docker compose up -d

# 5. Acompanhe a subida
docker compose logs -f
```

Acesse o dashboard em **http://localhost:3000**.

```bash
docker compose ps          # status
docker compose logs -f server
docker compose down        # para (mantém os dados)
docker compose down -v     # para e apaga os dados
```

## Opção B — Instalação nativa em Linux (systemd)

Instala o servidor como serviço, com usuário dedicado e runtime .NET automático.

```bash
git clone https://github.com/latex/monitoredb.git
cd monitoredb

# Instala (gera um token automaticamente)
sudo ./scripts/install-server-linux.sh

# Exemplos de customização
sudo ./scripts/install-server-linux.sh --port 8080 --token "meu-token"
sudo ./scripts/install-server-linux.sh --self-contained   # não exige .NET no host
sudo ./scripts/install-server-linux.sh --open-firewall    # libera a porta no ufw/firewalld
```

O instalador publica a aplicação, cria o usuário `monitoredb`, grava a configuração em
`/etc/monitoredb/server.env`, registra o serviço `monitoredb` e faz o health check.

```bash
systemctl status monitoredb      # status
journalctl -u monitoredb -f      # logs
systemctl restart monitoredb     # reiniciar

# Remover
sudo ./scripts/uninstall-server-linux.sh --purge
```

> Use `sudo ./scripts/install-server-linux.sh --help` para ver todas as opções
> (`--install-dir`, `--data-dir`, `--conf-dir`, `--user`, `--service`, `--source`, ...).

## Opção C — Desenvolvimento local

```bash
git clone https://github.com/latex/monitoredb.git
cd monitoredb

dotnet run --project monitoredb.dotnet/Monitoredb.ApiServer
```

Escuta em `http://0.0.0.0:3000`. Para trocar a porta:

```bash
PORT=8080 dotnet run --project monitoredb.dotnet/Monitoredb.ApiServer
```

### Configuração do servidor

| Variável | Padrão | Descrição |
|----------|--------|-----------|
| `PORT` | `3000` | Porta HTTP |
| `MONITOREDB_TOKEN` | vazio | Token de autenticação. **Vazio = API aberta** |
| `MONITOREDB_DATA_FILE` | `data/state.json` | Arquivo de persistência do estado |

> O dashboard é servido de `public/`. A pasta precisa existir no diretório da aplicação,
> caso contrário o servidor não inicia.

---

# Como instalar o cliente (agente)

O cliente roda **no servidor monitorado** (Windows) e envia os dados para o servidor
MonitoreDB. O procedimento abaixo é feito no host monitorado, com **PowerShell como
Administrador**.

## 1. Gerar o executável do cliente

No host monitorado, dentro do repositório clonado:

```powershell
# Self-contained (NÃO exige .NET instalado no host) — recomendado
dotnet publish monitoredb.dotnet/Monitoredb.WorkerAgent -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o C:\Monitoredb

# OU framework-dependent (exige .NET Runtime 10 instalado)
dotnet publish monitoredb.dotnet/Monitoredb.WorkerAgent -c Release -r win-x64 `
  --self-contained false -o C:\Monitoredb
```

> Se você não tiver o SDK .NET no host, gere o publish em outra máquina com
> `dotnet publish ... -r win-x64` e copie a pasta `C:\Monitoredb` resultante.

## 2. Configurar o cliente

Edite `C:\Monitoredb\appsettings.json` apontando para o seu servidor:

```json
{
  "Monitoredb": {
    "Server": "http://SEU-SERVIDOR:3000",
    "AgentId": "web01",
    "Interval": 30,
    "Token": "SEU_TOKEN_DO_SERVIDOR",
    "SqlHost": "localhost",
    "SqlPort": 1433,
    "SqlUser": "sa",
    "SqlPassword": "SUA_SENHA_SQL"
  }
}
```

- `Server` — URL do servidor MonitoreDB.
- `AgentId` — identificador do host (vazio = hostname da máquina).
- `Token` — deve ser igual ao `MONITOREDB_TOKEN` do servidor.
- `SqlHost` / `SqlUser` / `SqlPassword` — preencha para coletar SQL Server
  (deixe `SqlHost` vazio para desabilitar).
- A coleta de **IIS** é automática quando o host é Windows.

**Precedência de configuração:** flags de linha de comando > `appsettings.json`
(`Monitoredb:*`) > variáveis de ambiente `MONITOREDB_*`.

## 3. Registrar como serviço do Windows

O script `scripts/install-service.ps1` registra o cliente como serviço com inicialização
automática e recuperação em caso de falha.

```powershell
# Copie o instalador para a pasta do cliente
Copy-Item scripts\install-service.ps1 C:\Monitoredb\

# Registre e inicie o serviço (Administrador)
cd C:\Monitoredb
.\scripts\install-service.ps1 -Start
```

O serviço é criado com o nome **`MonitoredbAgent`** usando o `appsettings.json` da pasta
`C:\Monitoredb`. Para instalar em outro diretório:

```powershell
.\install-service.ps1 -InstallDir "C:\Program Files\Monitoredb" -Start
```

## 4. Verificar

```powershell
Get-Service MonitoredbAgent
Get-EventLog -LogName Application -Source MonitoredbAgent -Newest 20
```

No servidor, confira o dashboard (**http://SEU-SERVIDOR:3000**) — o host deve aparecer na
lista de agentes em até `Interval` segundos.

### Teste rápido (sem instalar como serviço)

```powershell
C:\Monitoredb\Monitoredb.WorkerAgent.exe --server http://SEU-SERVIDOR:3000 --once
```

## Opção: baixar o cliente pelo dashboard

O dashboard possui o botão **"Download Agente Windows"**, que baixa
`/download/monitoredb-agent.exe`. Para habilitá-lo no servidor, publique o cliente como
arquivo único e coloque-o em `public/download/monitoredb-agent.exe`:

```powershell
dotnet publish monitoredb.dotnet/Monitoredb.WorkerAgent -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o C:\temp\agent

# No servidor, copie para a pasta servida
mkdir public\download
Copy-Item C:\temp\agent\Monitoredb.WorkerAgent.exe public\download\monitoredb-agent.exe
```

## Opção: cliente em container (Linux)

O agente também roda em container (coleta sistema operacional e SQL Server):

```bash
docker build -f Dockerfile.agent.dotnet -t monitoredb-agent:latest .
docker run --rm monitoredb-agent:latest \
  --server http://SEU-SERVIDOR:3000 --agent-id container-01 --once
```

### Flags do cliente

| Flag | Descrição | Padrão |
|------|-----------|--------|
| `--server URL` | URL do servidor | `http://localhost:3000` |
| `--agent-id ID` | Identificador do agente | hostname da máquina |
| `--interval SEG` | Intervalo entre coletas | `30` |
| `--token TOKEN` | Token Bearer de ingestão | vazio |
| `--sql-host HOST` | Host do SQL Server (habilita coleta) | vazio |
| `--sql-port PORT` | Porta do SQL Server | `1433` |
| `--sql-user USER` | Usuário do SQL Server | `sa` |
| `--sql-pass SENHA` | Senha do SQL Server | vazio |
| `--once` | Coleta uma vez e encerra | — |

Variáveis equivalentes: `MONITOREDB_AGENT_SERVER`, `MONITOREDB_AGENT_ID`,
`MONITOREDB_AGENT_INTERVAL`, `MONITOREDB_AGENT_TOKEN`, `MONITOREDB_SQL_HOST`,
`MONITOREDB_SQL_PORT`, `MONITOREDB_SQL_USERNAME`, `MONITOREDB_SQL_PASSWORD`.

---

## Referência da API

Base: `http://<host>:3000`

| Método | Rota | Auth | Descrição |
|--------|------|:----:|-----------|
| `POST` | `/api/ingest` | Sim | Recebe o snapshot de um agente |
| `GET` | `/api/agents` | Não | Lista agentes registrados |
| `POST` | `/api/agents` | Sim | Registra um agente |
| `GET` | `/api/agent/{id}` | Não | Snapshot atual de um agente |
| `DELETE` | `/api/agent/{id}` | Sim | Remove um agente |
| `GET` | `/api/agent/{id}/history` | Não | Histórico de amostras do agente |
| `GET` | `/api/latest` | Não | Snapshot mais recente |
| `GET` | `/api/metrics` | Não | Métricas em formato Prometheus |
| `GET` | `/api/health` | Não | Health check (`{"status":"ok"}`) |

```bash
curl http://localhost:3000/api/health

curl -X POST http://localhost:3000/api/agents \
  -H "Authorization: Bearer $MONITOREDB_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"web01","hostname":"web01.local"}'
```

- Dashboard: **http://localhost:3000/**
- Métricas Prometheus: **http://localhost:3000/api/metrics**

## Build e testes

```bash
dotnet build monitoredb.dotnet/monitoredb.sln.slnx
dotnet test  monitoredb.dotnet/monitoredb.sln.slnx
make publish-server     # publica o servidor em publish/server
```

| Alvo do Makefile | Ação |
|------------------|------|
| `make publish-server` | Publica o servidor .NET em `publish/server` |
| `make install-server-linux` | Executa o instalador Linux |
| `make uninstall-server-linux` | Executa o desinstalador Linux |
| `make build-server` / `build-agent` | Build Rust (legado) |
| `make up` / `down` / `logs` | Docker Compose |

## Estrutura do repositório

```
monitoredb/
├── monitoredb.dotnet/                     # Implementação .NET (atual)
│   ├── Core/Monitoredb.CommonCollectors/  # Modelos e coletores (Windows/IIS/SQL)
│   ├── Monitoredb.ApiServer/              # Servidor HTTP + dashboard + Prometheus
│   ├── Monitoredb.WorkerAgent/            # Cliente (agente) de coleta
│   ├── Monitoredb.Tests/                  # Testes xUnit
│   └── monitoredb.sln.slnx
├── scripts/
│   ├── install-server-linux.sh            # Instalador do servidor (systemd)
│   ├── uninstall-server-linux.sh
│   ├── install-service.ps1                # Registra o cliente como serviço do Windows
│   └── iis-load.sh / .ps1                 # Gerador de carga HTTP (testes)
├── public/                                # Frontend (dashboard) — index.html
├── Dockerfile.server.dotnet / agent.dotnet
├── docker-compose.yml
└── .env.example
```

## Segurança

- **Defina sempre `MONITOREDB_TOKEN`** em produção. Sem token, as rotas de escrita e
  ingestão ficam abertas a qualquer origem.
- O arquivo `.env` contém segredos e está no `.gitignore` — nunca o versione.
- O instalador Linux grava `server.env` com permissão `0640` (root + grupo do serviço) e
  roda o serviço com endurecimento systemd (`ProtectSystem=strict`, `PrivateTmp`, etc.).

## Solução de problemas

- **Servidor não inicia / `DirectoryNotFoundException ... public/`** — crie ou copie a
  pasta `public/` (com `index.html`) para o diretório da aplicação.
- **`MSSQL_SA_PASSWORD` ausente no Docker** — defina a variável no `.env`; o Compose
  falha de propósito sem ela.
- **Cliente não aparece no dashboard** — confira `Server`, `Token` e se o serviço
  `MonitoredbAgent` está em execução; teste com `--once`.
- **Cliente sem dados de IIS** — a coleta de IIS só ocorre em Windows.
- **Cliente sem dados de SQL Server** — informe `SqlHost` e credenciais válidas.
- **Health check não retorna 200 no instalador Linux** — verifique
  `journalctl -u monitoredb -n 50`.
