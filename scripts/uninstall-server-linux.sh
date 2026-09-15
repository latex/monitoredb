#!/usr/bin/env bash
# ============================================================================
# MonitoreDB - Desinstalador do Servidor (Linux / systemd)
# ============================================================================
# Remove o serviço systemd e (opcionalmente) arquivos, config e usuário.
#
# Uso:
#   sudo ./uninstall-server-linux.sh [opções]
#
# Opções:
#   -s, --service NAME      Nome do serviço (default: monitoredb)
#   -d, --install-dir DIR   Diretório da aplicação (default: /opt/monitoredb)
#       --data-dir DIR      Diretório de dados (default: /var/lib/monitoredb)
#       --conf-dir DIR      Diretório de configuração (default: /etc/monitoredb)
#   -u, --user USER         Usuário de sistema (default: monitoredb)
#       --purge             Remove também dados, config e o usuário
#       --keep-user         Com --purge, mantém o usuário
#   -h, --help              Mostra esta ajuda
# ============================================================================
set -Eeuo pipefail

SERVICE="monitoredb"
INSTALL_DIR="/opt/monitoredb"
DATA_DIR="/var/lib/monitoredb"
CONF_DIR="/etc/monitoredb"
SVC_USER="monitoredb"
PURGE=0
KEEP_USER=0

log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m  ok\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m  !!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merro:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
MonitoreDB - Desinstalador do Servidor (Linux / systemd)

Uso:
  sudo ./uninstall-server-linux.sh [opções]

Opções:
  -s, --service NAME      Nome do serviço (default: monitoredb)
  -d, --install-dir DIR   Diretório da aplicação (default: /opt/monitoredb)
      --data-dir DIR      Diretório de dados (default: /var/lib/monitoredb)
      --conf-dir DIR      Diretório de configuração (default: /etc/monitoredb)
  -u, --user USER         Usuário de sistema (default: monitoredb)
      --purge             Remove também dados, config e o usuário
      --keep-user         Com --purge, mantém o usuário
  -h, --help              Mostra esta ajuda
EOF
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -s|--service)     SERVICE="${2:?}"; shift 2 ;;
    -d|--install-dir) INSTALL_DIR="${2:?}"; shift 2 ;;
    --data-dir)       DATA_DIR="${2:?}"; shift 2 ;;
    --conf-dir)       CONF_DIR="${2:?}"; shift 2 ;;
    -u|--user)        SVC_USER="${2:?}"; shift 2 ;;
    --purge)          PURGE=1; shift ;;
    --keep-user)      KEEP_USER=1; shift ;;
    -h|--help)        usage ;;
    *) die "argumento desconhecido: $1 (use --help)" ;;
  esac
done

[[ "${EUID}" -eq 0 ]] || die "execute com privilégios de root (sudo)."

SYSTEMD_DIR="/etc/systemd/system"
UNIT_FILE="$SYSTEMD_DIR/$SERVICE.service"

# ------------------------------- serviço -------------------------------------
if systemctl list-unit-files "$SERVICE.service" >/dev/null 2>&1; then
  log "Parando e desabilitando $SERVICE"
  systemctl stop "$SERVICE" 2>/dev/null || true
  systemctl disable "$SERVICE" 2>/dev/null || true
fi

if [[ -f "$UNIT_FILE" ]]; then
  log "Removendo unit: $UNIT_FILE"
  rm -f "$UNIT_FILE"
  systemctl daemon-reload
  systemctl reset-failed "$SERVICE" 2>/dev/null || true
  ok "serviço removido"
else
  warn "unit não encontrada: $UNIT_FILE"
fi

# ------------------------------- arquivos ------------------------------------
if [[ "$PURGE" -eq 1 ]]; then
  log "Removendo diretórios"
  [[ -d "$INSTALL_DIR" ]] && rm -rf "$INSTALL_DIR" && ok "removido $INSTALL_DIR"
  [[ -d "$CONF_DIR" ]] && rm -rf "$CONF_DIR" && ok "removido $CONF_DIR"
  [[ -d "$DATA_DIR" ]] && rm -rf "$DATA_DIR" && ok "removido $DATA_DIR"

  if [[ "$KEEP_USER" -eq 0 ]] && id -u "$SVC_USER" >/dev/null 2>&1; then
    log "Removendo usuário $SVC_USER"
    userdel "$SVC_USER" 2>/dev/null && ok "usuário removido" || warn "não foi possível remover o usuário"
  fi
else
  log "Binários mantidos. Para remover tudo, rode novamente com --purge."
fi

echo
echo "Desinstalação concluída."
