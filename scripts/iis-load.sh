#!/usr/bin/env bash
# Gerador de carga HTTP contínuo (Linux/macOS).
#
# Mantém conexões ativas para que servidores web (IIS w3wp, nginx, etc.)
# exponham métricas de app pool / requisições durante a coleta do MonitoreDB.
#
# Uso:
#   ./iis-load.sh [URLS...] [--duration N] [--interval MS] [--concurrency N] [--timeout N] [--log FILE]
#
# Exemplos:
#   ./iis-load.sh                                   # localhost/ e localhost:8080/, infinito, 4 workers
#   ./iis-load.sh http://localhost:8080/ --duration 300
#   URLS="http://localhost/ http://localhost:8080/" CONCURRENCY=8 ./iis-load.sh
set -uo pipefail

DURATION=${DURATION:-0}          # segundos; 0 = infinito
INTERVAL_MS=${INTERVAL_MS:-200}
CONCURRENCY=${CONCURRENCY:-4}
TIMEOUT=${TIMEOUT:-5}
LOG_FILE=${LOG_FILE:-}
URLS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --duration)    DURATION="$2"; shift 2 ;;
    --interval)    INTERVAL_MS="$2"; shift 2 ;;
    --concurrency) CONCURRENCY="$2"; shift 2 ;;
    --timeout)     TIMEOUT="$2"; shift 2 ;;
    --log)         LOG_FILE="$2"; shift 2 ;;
    -h|--help)     sed -n '2,14p' "$0"; exit 0 ;;
    http://*|https://*) URLS+=("$1"); shift ;;
    *) echo "Argumento desconhecido: $1" >&2; exit 1 ;;
  esac
done

if [[ ${#URLS[@]} -eq 0 ]]; then
  # Permite sobrescrever via env URLS (separado por espaço).
  read -r -a URLS <<< "${URLS:-http://localhost/ http://localhost:8080/}"
fi

command -v curl >/dev/null 2>&1 || { echo "curl nao encontrado" >&2; exit 1; }

tmp=$(mktemp -d)
trap 'stop=1; wait; rm -rf "$tmp"' EXIT
stop=0
start_ts=$(date +%s)
end_ts=0
[[ "$DURATION" -gt 0 ]] && end_ts=$((start_ts + DURATION))

worker() {
  local id=$1 i=0
  while [[ $stop -eq 0 ]]; do
    if [[ $end_ts -gt 0 && $(date +%s) -ge $end_ts ]]; then break; fi
    local url="${URLS[$(( (id + i) % ${#URLS[@]} ))]}"
    i=$((i + 1))
    local out
    if out=$(curl -s -o /dev/null -m "$TIMEOUT" -w '%{http_code} %{time_total} %{size_download}' "$url" 2>/dev/null); then
      echo "$out" >> "$tmp/w${id}.ok"
    else
      echo "fail" >> "$tmp/w${id}.fail"
    fi
    [[ "$INTERVAL_MS" -gt 0 ]] && sleep "$(awk "BEGIN{printf \"%.3f\", $INTERVAL_MS/1000}")"
  done
}

echo "Gerando carga: ${URLS[*]}"
echo "concorrencia=$CONCURRENCY intervalo=${INTERVAL_MS}ms duracao=$([[ $DURATION -gt 0 ]] && echo "${DURATION}s" || echo "infinita (Ctrl+C p/ parar)")"

for ((w=0; w<CONCURRENCY; w++)); do worker "$w" & done

# Monitor de progresso
while [[ $stop -eq 0 ]]; do
  sleep 5
  ok=$(cat "$tmp"/*.ok 2>/dev/null | wc -l)
  fail=$(cat "$tmp"/*.fail 2>/dev/null | wc -l)
  msg="ok=$ok fail=$fail"
  echo "[$(date +%H:%M:%S)] $msg"
  [[ -n "$LOG_FILE" ]] && echo "$(date -Is) $msg" >> "$LOG_FILE"
  [[ $end_ts -gt 0 && $(date +%s) -ge $end_ts ]] && break
done

stop=1
wait

ok=$(cat "$tmp"/*.ok 2>/dev/null | wc -l)
fail=$(cat "$tmp"/*.fail 2>/dev/null | wc -l)
avg=$(awk '{s+=$2; n++} END{if(n)printf "%.1f", (s/n)*1000; else print 0}' "$tmp"/*.ok 2>/dev/null)
max=$(awk 'BEGIN{m=0}{if($2>m)m=$2}END{printf "%.1f", m*1000}' "$tmp"/*.ok 2>/dev/null)
bytes=$(awk '{s+=$3}END{printf "%d", s}' "$tmp"/*.ok 2>/dev/null)

echo ""
echo "===== RESUMO ====="
echo "requisicoes : $((ok + fail))"
echo "  ok        : $ok"
echo "  falhas    : $fail"
echo "bytes       : ${bytes:-0}"
echo "lat.media   : ${avg:-0} ms"
echo "lat.max     : ${max:-0} ms"
[[ -n "$LOG_FILE" ]] && echo "RESUMO total=$((ok+fail)) ok=$ok fail=$fail avg_ms=${avg:-0}" >> "$LOG_FILE"
