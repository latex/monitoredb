#!/usr/bin/env bash
# ============================================================================
# MonitoreDB - Instalador do Servidor (Linux / systemd)
# ============================================================================
# Instala o Monitoredb.ApiServer (.NET) como serviço systemd, com usuário
# dedicado, diretório de dados persistente e configuração por env file.
#
# Uso:
#   sudo ./install-server-linux.sh [opções]
#
# Opções:
#   -p, --port N            Porta HTTP (default: 3000)
#   -t, --token TOKEN       Token de autenticação (default: $MONITOREDB_TOKEN ou aleatório)
#       --no-token          Não configurar token (API aberta)
#   -d, --install-dir DIR   Diretório da aplicação (default: /opt/monitoredb)
#       --data-dir DIR      Diretório de dados/estado  (default: /var/lib/monitoredb)
#       --conf-dir DIR      Diretório de configuração  (default: /etc/monitoredb)
#   -u, --user USER         Usuário de sistema      (default: monitoredb)
#   -s, --service NAME      Nome do serviço systemd (default: monitoredb)
#       --source DIR        Usar binários já publicados em DIR (senão publica)
#       --self-contained    Publicar self-contained (não requer .NET no host)
#       --rid RID           Runtime identifier (default: linux-x64 ou linux-arm64)
#       --no-start          Instala sem iniciar o serviço
#       --open-firewall     Abre a porta no ufw/firewalld, se disponível
#   -h, --help              Mostra esta ajuda
#
# Exemplos:
#   sudo ./install-server-linux.sh --token "$(openssl rand -hex 16)"
#   sudo ./install-server-linux.sh -p 8080 --self-contained
#   sudo ./install-server-linux.sh --source ./publish
# ============================================================================
set -Eeuo pipefail

# ------------------------------- defaults -----------------------------------
APP_NAME="monitoredb"
INSTALL_DIR="/opt/monitoredb"
DATA_DIR="/var/lib/monitoredb"
CONF_DIR="/etc/monitoredb"
SERVICE="monitoredb"
SVC_USER="monitoredb"
PORT="3000"
TOKEN="${MONITOREDB_TOKEN:-}"
USE_TOKEN=1
SOURCE_DIR=""
SELF_CONTAINED=0
RID=""
DO_START=1
OPEN_FIREWALL=0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/monitoredb.dotnet/Monitoredb.ApiServer/Monitoredb.ApiServer.csproj"

# ------------------------------- helpers ------------------------------------
log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m  ok\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m  !!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merro:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
MonitoreDB - Instalador do Servidor (Linux / systemd)

Uso:
  sudo ./install-server-linux.sh [opções]

Opções:
  -p, --port N            Porta HTTP (default: 3000)
  -t, --token TOKEN       Token de autenticação (default: $MONITOREDB_TOKEN ou aleatório)
      --no-token          Não configurar token (API aberta)
  -d, --install-dir DIR   Diretório da aplicação (default: /opt/monitoredb)
      --data-dir DIR      Diretório de dados/estado  (default: /var/lib/monitoredb)
      --conf-dir DIR      Diretório de configuração  (default: /etc/monitoredb)
  -u, --user USER         Usuário de sistema      (default: monitoredb)
  -s, --service NAME      Nome do serviço systemd (default: monitoredb)
      --source DIR        Usar binários já publicados em DIR (senão publica)
      --self-contained    Publicar self-contained (não requer .NET no host)
      --rid RID           Runtime identifier (default: linux-x64 ou linux-arm64)
      --no-start          Instala sem iniciar o serviço
      --open-firewall     Abre a porta no ufw/firewalld, se disponível
  -h, --help              Mostra esta ajuda

Exemplos:
  sudo ./install-server-linux.sh --token "$(openssl rand -hex 16)"
  sudo ./install-server-linux.sh -p 8080 --self-contained
  sudo ./install-server-linux.sh --source ./publish
EOF
  exit 0
}

require_root() {
  [[ "${EUID}" -eq 0 ]] || die "execute com privilégios de root (sudo)."
}

need_cmd() { command -v "$1" >/dev/null 2>&1; }

# ------------------------------- arg parsing --------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|--port)          PORT="${2:?}"; shift 2 ;;
    -t|--token)         TOKEN="${2:?}"; shift 2 ;;
    --no-token)         USE_TOKEN=0; shift ;;
    -d|--install-dir)   INSTALL_DIR="${2:?}"; shift 2 ;;
    --data-dir)         DATA_DIR="${2:?}"; shift 2 ;;
    --conf-dir)         CONF_DIR="${2:?}"; shift 2 ;;
    -u|--user)          SVC_USER="${2:?}"; shift 2 ;;
    -s|--service)       SERVICE="${2:?}"; shift 2 ;;
    --source)           SOURCE_DIR="${2:?}"; shift 2 ;;
    --self-contained)   SELF_CONTAINED=1; shift ;;
    --rid)              RID="${2:?}"; shift 2 ;;
    --no-start)         DO_START=0; shift ;;
    --open-firewall)    OPEN_FIREWALL=1; shift ;;
    -h|--help)          usage ;;
    *) die "argumento desconhecido: $1 (use --help)" ;;
  esac
done

require_root

[[ "$PORT" =~ ^[0-9]+$ ]] || die "porta inválida: $PORT"
[[ "$SERVICE" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "nome de serviço inválido: $SERVICE"
[[ "$SVC_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || die "nome de usuário inválido: $SVC_USER"

SYSTEMD_DIR="/etc/systemd/system"
UNIT_FILE="$SYSTEMD_DIR/$SERVICE.service"
ENV_FILE="$CONF_DIR/server.env"

# ------------------------------- arquitetura ---------------------------------
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    x86_64|amd64)  RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    armv7l)        RID="linux-arm" ;;
    *) die "arquitetura não suportada: $(uname -m). Informe --rid." ;;
  esac
fi

# ------------------------------- systemd check -------------------------------
need_cmd systemctl || die "systemd não encontrado (systemctl ausente)."

# ------------------------------- publish -------------------------------------
log "Preparando binários do servidor ($RID)"

STAGE="$(mktemp -d)"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

publish_app() {
  if [[ -n "$SOURCE_DIR" ]]; then
    [[ -d "$SOURCE_DIR" ]] || die "diretório --source não existe: $SOURCE_DIR"
    log "Usando binários de $SOURCE_DIR"
    cp -a "$SOURCE_DIR/." "$STAGE/"
    return
  fi

  # Reaproveita um publish existente, se houver.
  local existing
  existing="$(find "$REPO_ROOT/monitoredb.dotnet/Monitoredb.ApiServer/bin" \
    -type d -path "*Release/net10.0/$RID/publish" 2>/dev/null | head -n1 || true)"
  if [[ -n "$existing" ]]; then
    log "Reaproveitando publish existente: $existing"
    cp -a "$existing/." "$STAGE/"
    return
  fi

  [[ -f "$PROJECT" ]] || die "projeto não encontrado: $PROJECT (use --source)."
  need_cmd dotnet || die ".NET SDK não encontrado. Instale o SDK ou use --source com binários publicados."

  log "Publicando $PROJECT (self-contained=$SELF_CONTAINED)"
  local sc="false"; [[ "$SELF_CONTAINED" -eq 1 ]] && sc="true"
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained "$sc" \
    -p:SatelliteResourceLanguages=en \
    -p:DebugType=None \
    -o "$STAGE" >/dev/null
}

publish_app
[[ -f "$STAGE/Monitoredb.ApiServer.dll" ]] || die "publish não contém Monitoredb.ApiServer.dll."

# ------------------------------- frontend ------------------------------------
# O publish do .NET não inclui a pasta public/, então copiamos explicitamente.
copy_public() {
  if [[ -d "$INSTALL_DIR/public" && -f "$INSTALL_DIR/public/index.html" ]]; then
    return
  fi
  local candidates=(
    "$REPO_ROOT/public"
    "$SOURCE_DIR/public"
    "$REPO_ROOT/monitoredb.dotnet/Monitoredb.ApiServer/public"
  )
  local p
  for p in "${candidates[@]}"; do
    if [[ -n "$p" && -f "$p/index.html" ]]; then
      install -d -m 0755 "$INSTALL_DIR/public"
      cp -a "$p/." "$INSTALL_DIR/public/"
      ok "frontend copiado de $p"
      return
    fi
  done
  warn "public/index.html não encontrado: o dashboard não será servido."
}

# ------------------------------- .NET runtime --------------------------------
DOTNET_BIN=""

find_system_dotnet() {
  local candidates=(/usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet "$(command -v dotnet 2>/dev/null || true)")
  local c
  for c in "${candidates[@]}"; do
    [[ -n "$c" && -x "$c" ]] || continue
    # Ignora instalações de usuário (mise/asdf em /home) que o serviço não enxerga.
    [[ "$c" == /home/* ]] && continue
    if "$c" --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 10\.'; then
      DOTNET_BIN="$c"
      return 0
    fi
  done
  return 1
}

install_dotnet_runtime() {
  log "Instalando runtime ASP.NET Core 10 em /usr/share/dotnet"
  need_cmd curl || need_cmd wget || die "curl ou wget é necessário para baixar o runtime."
  local tmp; tmp="$(mktemp -d)"
  if need_cmd curl; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp/dotnet-install.sh"
  else
    wget -qO "$tmp/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
  fi
  bash "$tmp/dotnet-install.sh" --channel 10.0 --runtime aspnetcore \
    --install-dir /usr/share/dotnet --version latest
  rm -rf "$tmp"
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
  DOTNET_BIN="/usr/share/dotnet/dotnet"
}

# ------------------------------- usuário -------------------------------------
if ! id -u "$SVC_USER" >/dev/null 2>&1; then
  log "Criando usuário de sistema: $SVC_USER"
  if need_cmd useradd; then
    useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SVC_USER" 2>/dev/null \
      || useradd --system --home-dir "$DATA_DIR" --shell /sbin/nologin "$SVC_USER"
  else
    die "useradd não encontrado."
  fi
fi
SVC_GROUP="$(id -gn "$SVC_USER")"

# ------------------------------- diretórios ----------------------------------
log "Criando diretórios"
install -d -m 0755 "$INSTALL_DIR"
install -d -m 0755 "$CONF_DIR"
install -d -m 0750 -o "$SVC_USER" -g "$SVC_GROUP" "$DATA_DIR"
install -d -m 0750 -o "$SVC_USER" -g "$SVC_GROUP" "$DATA_DIR/data"
install -d -m 0755 "$INSTALL_DIR/public"

# Para de um serviço em execução antes de substituir os binários.
if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
  log "Parando serviço em execução"
  systemctl stop "$SERVICE"
fi

log "Instalando arquivos em $INSTALL_DIR"
cp -a "$STAGE/." "$INSTALL_DIR/"
copy_public
chown -R root:root "$INSTALL_DIR"
chmod 0755 "$INSTALL_DIR"

# ------------------------------- runtime resolve -----------------------------
if [[ "$SELF_CONTAINED" -eq 0 ]]; then
  if ! find_system_dotnet; then
    install_dotnet_runtime
  fi
else
  # Confirma que o apphost self-contained existe.
  [[ -x "$INSTALL_DIR/Monitoredb.ApiServer" ]] || die "apphost self-contained não encontrado no publish."
fi

# ------------------------------- token ---------------------------------------
if [[ "$USE_TOKEN" -eq 1 && -z "$TOKEN" ]]; then
  if need_cmd openssl; then
    TOKEN="$(openssl rand -hex 24)"
  else
    TOKEN="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  fi
  warn "Nenhum token informado; gerado automaticamente (veja $ENV_FILE)."
fi

# Escapa valor para o env file do systemd.
env_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

# ------------------------------- env file ------------------------------------
log "Escrevendo configuração: $ENV_FILE"
{
  printf '# MonitoreDB server - gerado por install-server-linux.sh\n'
  printf 'ASPNETCORE_ENVIRONMENT=Production\n'
  printf 'DOTNET_ENVIRONMENT=Production\n'
  printf 'PORT=%s\n' "$PORT"
  printf 'MONITOREDB_DATA_FILE=%s\n' "$DATA_DIR/data/state.json"
  if [[ "$USE_TOKEN" -eq 1 ]]; then
    printf 'MONITOREDB_TOKEN="%s"\n' "$(env_escape "$TOKEN")"
  else
    printf '# MONITOREDB_TOKEN vazio = API aberta (sem autenticação)\n'
  fi
} > "$ENV_FILE"
chmod 0640 "$ENV_FILE"
chown root:"$SVC_GROUP" "$ENV_FILE"

# ------------------------------- unit file -----------------------------------
if [[ "$SELF_CONTAINED" -eq 1 ]]; then
  EXEC_START="$INSTALL_DIR/Monitoredb.ApiServer"
else
  EXEC_START="$DOTNET_BIN $INSTALL_DIR/Monitoredb.ApiServer.dll"
fi

log "Criando serviço systemd: $SERVICE"
cat > "$UNIT_FILE" <<EOF
[Unit]
Description=MonitoreDB API Server
Documentation=https://github.com/invent/monitoredb
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_GROUP
WorkingDirectory=$INSTALL_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$EXEC_START
Restart=on-failure
RestartSec=3
TimeoutStopSec=15
KillSignal=SIGINT

# Endurecimento
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
ReadWritePaths=$DATA_DIR
ReadOnlyPaths=$INSTALL_DIR

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null

# ------------------------------- firewall ------------------------------------
if [[ "$OPEN_FIREWALL" -eq 1 ]]; then
  if need_cmd ufw && ufw status 2>/dev/null | grep -q "Status: active"; then
    log "Liberando porta $PORT no ufw"
    ufw allow "$PORT/tcp" >/dev/null && ok "ufw: $PORT/tcp liberado"
  elif need_cmd firewall-cmd && firewall-cmd --state >/dev/null 2>&1; then
    log "Liberando porta $PORT no firewalld"
    firewall-cmd --permanent --add-port="$PORT/tcp" >/dev/null
    firewall-cmd --reload >/dev/null && ok "firewalld: $PORT/tcp liberado"
  else
    warn "ufw/firewalld não encontrados ou inativos; porta não liberada."
  fi
fi

# ------------------------------- start & health ------------------------------
HOST="http://127.0.0.1:$PORT"

if [[ "$DO_START" -eq 1 ]]; then
  log "Iniciando serviço"
  systemctl restart "$SERVICE"

  if need_cmd curl || need_cmd wget; then
    log "Aguardando health check em $HOST/api/health"
    ok_health=0
    for _ in $(seq 1 30); do
      if need_cmd curl; then
        code="$(curl -fsS -o /dev/null -w '%{http_code}' "$HOST/api/health" 2>/dev/null || true)"
      else
        code="$(wget -qO /dev/null --server-response "$HOST/api/health" 2>&1 | awk '/HTTP\//{print $2; exit}' || true)"
      fi
      if [[ "$code" == "200" ]]; then ok_health=1; break; fi
      sleep 1
    done
    if [[ "$ok_health" -eq 1 ]]; then
      ok "servidor saudável (HTTP 200)"
    else
      warn "health check não retornou 200; verifique: journalctl -u $SERVICE -n 50"
    fi
  fi
fi

# ------------------------------- resumo --------------------------------------
echo
echo "============================================================"
echo " MonitoreDB Server instalado"
echo "============================================================"
echo "  Serviço    : $SERVICE"
echo "  App        : $INSTALL_DIR"
echo "  Dados      : $DATA_DIR/data/state.json"
echo "  Config     : $ENV_FILE"
echo "  Endereço   : $HOST"
echo "  Dashboard  : $HOST/"
echo "  Runtime    : $([[ "$SELF_CONTAINED" -eq 1 ]] && echo 'self-contained' || echo "$DOTNET_BIN")"
if [[ "$USE_TOKEN" -eq 1 ]]; then
  echo "  Token      : ${TOKEN}"
fi
echo
echo "  Status : systemctl status $SERVICE"
echo "  Logs   : journalctl -u $SERVICE -f"
echo "  Parar  : systemctl stop $SERVICE"
echo "  Remover: sudo $SCRIPT_DIR/uninstall-server-linux.sh --service $SERVICE"
echo "============================================================"
