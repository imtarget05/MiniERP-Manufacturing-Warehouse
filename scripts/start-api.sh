#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/start-api.sh
# PURPOSE: build and run the ASP.NET Core API (Swagger UI at the root URL).
# USAGE:   bash scripts/start-api.sh              foreground, http://localhost:5000
#          API_PORT=5080 bash scripts/start-api.sh
#          bash scripts/start-api.sh --stop       stop a background instance
#          bash scripts/start-api.sh --status
#          RUN_MODE=bg  bash scripts/start-api.sh start in background (logs in artifacts/api.log)
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

PID_FILE="${PID_FILE:-/tmp/minierp-api.pid}"
LOG_FILE="$REPO_ROOT/artifacts/api.log"

stop_instance() {
  if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
    kill "$(cat "$PID_FILE")" 2>/dev/null
    ok "stopped API (pid $(cat "$PID_FILE"))"
    rm -f "$PID_FILE"
  else
    pkill -f 'MiniERP.Api.dll' 2>/dev/null && ok "stopped matching API process" || warn "no running instance found"
  fi
}

case "${1:-run}" in
  --stop|stop) stop_instance; exit 0 ;;
  --status|status)
    if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
      ok "API running (pid $(cat "$PID_FILE")) on $BASE_URL"
    else
      warn "API is not running"
    fi
    exit 0 ;;
esac

find_dotnet || die ".NET SDK 8 was not found. Install it, or extract it to ~/.dotnet:\n  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0"
stop_instance >/dev/null 2>&1 || true
mkdir -p "$ARTIFACT_DIR"

rule
printf '%b\n' "${C_B} Building & starting the Mini ERP API${C_0}"
rule
log "connection string target: $APP_USER@$PDB (see src/appsettings.json)"

cd "$REPO_ROOT/src" || exit 1
dotnet build -c Release --nologo 2>&1 | grep -E 'error|warning|Build succeeded|Warning\(s\)|Error\(s\)' | sed 's/^/       /'
DLL="bin/Release/net8.0/MiniERP.Api.dll"
[ -f "$DLL" ] || die "build did not produce $DLL"

export ASPNETCORE_URLS="http://localhost:$API_PORT"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
if [ "$ASPNETCORE_ENVIRONMENT" != "Development" ] && [ -z "${JWT_SIGNING_KEY:-}" ]; then
  die "JWT_SIGNING_KEY is required when ASPNETCORE_ENVIRONMENT=$ASPNETCORE_ENVIRONMENT"
fi

if [ "${RUN_MODE:-fg}" = "bg" ]; then
  nohup dotnet "$DLL" > "$LOG_FILE" 2>&1 &
  echo $! > "$PID_FILE"
  ok "API started in background (pid $(cat "$PID_FILE")), logs: artifacts/api.log"
  wait_for_api "${API_TIMEOUT:-90}"
  printf '%b\n' "  Swagger UI : $BASE_URL/  (root)"
  printf '%b\n' "  OpenAPI doc: $BASE_URL/swagger/v1/swagger.json"
  printf '%b\n' "  Stop with  : bash scripts/start-api.sh --stop"
  rule
else
  printf '%b\n' "  Swagger UI will be served at ${C_B}$BASE_URL/${C_0}  (Ctrl+C to stop)"
  rule
  exec dotnet "$DLL"
fi
