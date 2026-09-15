# MonitoreDB

Monitoramento centralizado de servidores Windows: um **servidor** recebe snapshots de
**agentes** instalados nos hosts monitorados e expõe uma API REST, um dashboard web e
métricas no formato Prometheus.

Coleta disponível nos agentes:

- **Windows** — CPU, memória, disco e uptime.
- **IIS** — status de sites e app pools (somente Windows).
- **SQL Server** — sessões, conexões e uso de recursos.

O projeto possui duas implementações:

- **.NET 10** (`monitoredb.dotnet/`) — implementação **atual** (servidor + agente).
- **Rust** (`server/`, `agent/`) — implementação **legada**, mantida como referência.

## Estrutura do repositório

```
monitoredb/
├── monitoredb.dotnet/            # Implementação .NET (atual)
│   ├── Core/
│   │   └── Monitoredb.CommonCollectors/   # Modelos e coletores (Windows/IIS/SQL)
│   ├── Monitoredb.ApiServer/              # Servidor HTTP + dashboard + Prometheus
│   ├── Monitoredb.WorkerAgent/            # Agente de coleta
│   ├── Monitoredb.Tests/                  # Testes xUnit
│   └── monitoredb.sln.slnx                # Solution
├── scripts/                      # Instaladores Linux e utilitários
│   ├── install-server-linux.sh   # Instalador do servidor (systemd)
│   ├── uninstall-server-linux.sh # Desinstalador do servidor
│   ├── install-service.ps1       # Registra o agente como serviço do Windows
│   └── iis-load.sh / .ps1        # Gerador de carga HTTP (para testes)
├── public/                       # Frontend (dashboard) — index.html
├── Dockerfile.server.dotnet      # Imagem do servidor .NET
├── Dockerfile.agent.dotnet       # Imagem do agente .NET
├── docker-compose.yml            # Servidor + SQL Server + agente
├── Makefile                      # Atalhos de build/execução
├── .env.example                  # Modelo de configuração
└── VERSION
```

## Requisitos

| Uso | Requisitos |
|-----|-----------|
| Docker (recomendado) | Docker Engine 24+ e Docker Compose v2 |
| Execução local (.NET) | .NET SDK 10 |
| Instalação em servidor Linux | systemd, root/sudo |
| Implementação legada (Rust) | Rust 1.88+ |

---

## 1. Subir com Docker (recomendado)

O `docker-compose.yml` sobe três serviços: `server` (API), `sqlserver` (SQL Server 2022)
e `agent` (agente de coleta).

```bash
# 1. Crie o arquivo de configuração
cp .env.example .env

# 2. Edite .env e defina OBRIGATORIAMENTE a senha do SQL Server
#    MSSQL_SA_PASSWORD=uma-senha-forte
#    (opcional) MONITOREDB_TOKEN=seu-token-de-autenticacao

# 3. Suba a stack
docker compose up -d

# 4. Acompanhe os logs
docker compose logs -f
```

Acesse o dashboard em **http://localhost:3000**.

Comandos úteis:

```bash
docker compose ps          # status dos serviços
docker compose logs -f server
docker compose down        # derruba (mantém volumes)
docker compose down -v     # derruba e apaga os dados
```

### Build manual das imagens

```bash
# Servidor
docker build -f Dockerfile.server.dotnet -t monitoredb-server:latest .

# Agente
docker build -f Dockerfile.agent.dotnet -t monitoredb-agent:latest .
```

---

## 2. Subir localmente (.NET)

### Servidor

```bash
dotnet run --project monitoredb.dotnet/Monitoredb.ApiServer
```

O servidor escuta em `http://0.0.0.0:3000` por padrão. Configure pela variável `PORT`:

```bash
PORT=8080 dotnet run --project monitoredb.dotnet/Monitoredb.ApiServer
```

Variáveis de ambiente do servidor:

| Variável | Padrão | Descrição |
|----------|--------|-----------|
| `PORT` | `3000` | Porta HTTP |
| `MONITOREDB_TOKEN` | vazio | Token de autenticação. **Vazio = API aberta** |
| `MONITOREDB_DATA_FILE` | `data/state.json` | Caminho do arquivo de persistência do estado |

> O dashboard é servido de `public/`. A pasta precisa existir no diretório da aplicação,
> caso contrário o servidor não inicia.

### Agente

```bash
dotnet run --project monitoredb.dotnet/Monitoredb.WorkerAgent -- \
  --server http://localhost:3000 \
  --agent-id meu-host \
  --interval 30
```

Coleta única (sem loop), útil para testes:

```bash
dotnet run --project monitoredb.dotnet/Monitoredb.WorkerAgent -- --server http://localhost:3000 --once
```

Flags disponíveis:

| Flag | Descrição | Padrão |
|------|-----------|--------|
| `--server URL` | URL do servidor | `http://localhost:3000` |
| `--agent-id ID` | Identificador do agente | hostname da máquina |
| `--interval SEG` | Intervalo entre coletas | `30` |
| `--token TOKEN` | Token Bearer de ingestão | vazio |
| `--sql-host HOST` | Host do SQL Server (habilita coleta SQL) | vazio |
| `--sql-port PORT` | Porta do SQL Server | `1433` |
| `--sql-user USER` | Usuário do SQL Server | `sa` |
| `--sql-pass SENHA` / `--sql-password SENHA` | Senha do SQL Server | vazio |
| `--once` | Coleta uma vez e encerra | — |

**Precedência de configuração:** flags CLI > `appsettings.json` (`Monitoredb:*`) >
variáveis de ambiente `MONITOREDB_*`.

Variáveis equivalentes: `MONITOREDB_AGENT_SERVER`, `MONITOREDB_AGENT_ID`,
`MONITOREDB_AGENT_INTERVAL`, `MONITOREDB_AGENT_TOKEN`, `MONITOREDB_SQL_HOST`,
`MONITOREDB_SQL_PORT`, `MONITOREDB_SQL_USERNAME`, `MONITOREDB_SQL_PASSWORD`.

---

## 3. Instalar o servidor em Linux (systemd)

O instalador publica o servidor, cria usuário de sistema dedicado, configura
`/etc/monitoredb/server.env` e registra o serviço systemd.

```bash
# Instalação com token gerado automaticamente
sudo ./scripts/install-server-linux.sh

# Opções comuns
sudo ./scripts/install-server-linux.sh --port 8080 --token "meu-token"
sudo ./scripts/install-server-linux.sh --no-token          # API aberta
sudo ./scripts/install-server-linux.sh --self-contained    # não exige .NET no host
sudo ./scripts/install-server-linux.sh --source ./publish  # usa binários já publicados
```

Principais opções: `--install-dir` (`/opt/monitoredb`), `--data-dir`
(`/var/lib/monitoredb`), `--conf-dir` (`/etc/monitoredb`), `--user` (`monitoredb`),
`--service` (`monitoredb`), `--rid` (`linux-x64`/`linux-arm64`), `--no-start`,
`--open-firewall`. Use `--help` para a lista completa.

Operação do serviço:

```bash
systemctl status monitoredb
journalctl -u monitoredb -f
systemctl restart monitoredb
```

Desinstalação:

```bash
sudo ./scripts/uninstall-server-linux.sh            # remove o serviço
sudo ./scripts/uninstall-server-linux.sh --purge    # remove serviço, dados, config e usuário
```

---

## 4. Instalar o agente no Windows (serviço)

Publique o agente e registre-o como serviço:

```powershell
dotnet publish monitoredb.dotnet/Monitoredb.WorkerAgent -c Release -r win-x64 --self-contained false -o C:\Monitoredb

# Registra o serviço "MonitoredbAgent" (requer administrador)
.\scripts\install-service.ps1 -Start
```

A configuração vem do `appsettings.json` no diretório do executável (seção `Monitoredb`),
ou de flags/variáveis `MONITOREDB_*`.

Para provisionar o IIS no host monitorado, use `install-iis.ps1`.

---

## 5. Endpoints da API

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

Autenticação: quando `MONITOREDB_TOKEN` está definido, as rotas marcadas exigem o header
`Authorization: Bearer <token>`. As rotas de leitura permanecem abertas.

Exemplo:

```bash
# Health check
curl http://localhost:3000/api/health

# Registrar agente (com token)
curl -X POST http://localhost:3000/api/agents \
  -H "Authorization: Bearer $MONITOREDB_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"agent_id":"web01","hostname":"web01.local"}'
```

Dashboard: **http://localhost:3000/**
Métricas Prometheus: **http://localhost:3000/api/metrics**

---

## 6. Build e testes

```bash
# Build da solution
dotnet build monitoredb.dotnet/monitoredb.sln.slnx

# Testes
dotnet test monitoredb.dotnet/monitoredb.sln.slnx

# Publicar o servidor
make publish-server
```

Alvos do `Makefile`:

| Alvo | Ação |
|------|------|
| `make publish-server` | Publica o servidor .NET em `publish/server` |
| `make install-server-linux` | Executa o instalador Linux |
| `make uninstall-server-linux` | Executa o desinstalador Linux |
| `make build-server` / `build-agent` | Build Rust (legado) |
| `make dev-server` / `dev-agent` | Executa Rust (legado) |
| `make up` / `down` / `logs` | Docker Compose |

---

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
- **Agente sem dados de IIS** — a coleta de IIS só ocorre em Windows.
- **Agente sem dados de SQL Server** — informe `--sql-host` e credenciais válidas.
- **Health check não retorna 200 no instalador** — verifique
  `journalctl -u monitoredb -n 50`.
