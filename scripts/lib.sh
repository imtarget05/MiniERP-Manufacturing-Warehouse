#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/lib.sh  (library - source it, never execute it directly)
# PURPOSE: shared configuration + helpers used by every other script.
#          Override any value through the environment, e.g.
#            APP_USER=poc_user BASE_URL=http://localhost:5080 bash scripts/test-api.sh
# ============================================================================

DB_CONTAINER="${DB_CONTAINER:-minierp-oracle}"
APP_USER="${APP_USER:-erp_user}"
APP_USER_PWD="${APP_USER_PWD:-ErpPassword2026#}"
PDB="${PDB:-FREEPDB1}"
BASE_URL="${BASE_URL:-http://localhost:5000}"
API_PORT="${API_PORT:-5000}"
SQLPLUS="${SQLPLUS:-}"       # optional: path to a host sqlplus (skips docker exec)
ARTIFACT_KEEP="${ARTIFACT_KEEP:-20}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SQL_DIR="$REPO_ROOT/sql"
ARTIFACT_DIR="$REPO_ROOT/artifacts"

if [ -t 1 ]; then
  C_G='\033[32m'; C_R='\033[31m'; C_B='\033[36m'; C_Y='\033[33m'; C_0='\033[0m'
else C_G=''; C_R=''; C_B=''; C_Y=''; C_0=''; fi

log()  { printf '%b\n' "  ${C_B}->${C_0}   $*"; }
ok()   { printf '%b\n' "  ${C_G}OK${C_0}     $*"; }
warn() { printf '%b\n' "  ${C_Y}WARN${C_0}   $*"; }
err()  { printf '%b\n' "  ${C_R}FAIL${C_0}   $*" >&2; }
die()  { err "$*"; exit 1; }
rule() { printf '%b\n' "${C_B}--------------------------------------------------------------------${C_0}"; }

need_cmd() { command -v "$1" >/dev/null 2>&1 || die "the '$1' command is required but was not found"; }

# --------------------------------------------------------------- .NET SDK
# Resolves dotnet from PATH or from the per-user install used by this repo.
find_dotnet() {
  command -v dotnet >/dev/null 2>&1 && return 0
  local candidate
  for candidate in "$HOME/.dotnet" /usr/local/share/dotnet /usr/lib/dotnet /opt/dotnet; do
    if [ -x "$candidate/dotnet" ]; then
      DOTNET_ROOT="$candidate"
      PATH="$candidate:$PATH"
      export DOTNET_ROOT PATH
      log "using the .NET SDK installed in $candidate"
      return 0
    fi
  done
  return 1
}

# ------------------------------------------------------------------ sqlplus
# The sqlplus shipped with the Oracle Free image does not understand
# "WHEN SQLERROR", so failures are detected by grepping the log instead.
_sqlplus_master() {
  printf '%s\n' \
    'SET ECHO OFF FEEDBACK 1 HEADING ON PAGESIZE 100 LINESIZE 200 TRIMSPOOL ON LONG 10000' \
    'SET SERVEROUTPUT ON SIZE UNLIMITED' \
    "@${1:-/tmp/run.sql}" \
    'EXIT;'
}

# run_sql <host_file.sql> <logfile>
run_sql() {
  local host_file="$1" log_file="$2"
  # The container-side script name must be shell/sqlplus friendly: sqlplus
  # appends ".sql" when no extension is given and cannot open names containing
  # extra dots (mktemp files such as /tmp/tmp.Ab12Cd), so a safe flat name is
  # derived from the source file instead of reused verbatim.
  local safe name mname
  safe="$(basename "$host_file" | tr -c 'A-Za-z0-9' '_' | cut -c1-40)"
  name="erp_${safe}_$$_$(date +%s).sql"
  mname="m_${safe}_$$_$(date +%s).sql"
  [ -f "$host_file" ] || die "SQL file not found: $host_file"

  if [ -n "$SQLPLUS" ]; then                      # optional host-side sqlplus
    _sqlplus_master "$host_file" > /tmp/erp_master_local.sql
    "$SQLPLUS" -S "$APP_USER/$APP_USER_PWD@$PDB" @/tmp/erp_master_local.sql > "$log_file" 2>&1
    return $?
  fi

  need_cmd docker
  docker inspect "$DB_CONTAINER" >/dev/null 2>&1 \
    || die "container '$DB_CONTAINER' is not up - run: docker compose up -d"
  # The script is streamed through stdin rather than `docker cp`: cp preserves
  # the host permissions (mktemp files are 0600) and the oracle user inside the
  # container would then be unable to read it.
  docker exec -i "$DB_CONTAINER" bash -c "cat > /tmp/$name && chmod 0644 /tmp/$name" \
    < "$host_file" >/dev/null || die "could not stage /tmp/$name inside the container"
  _sqlplus_master "/tmp/$name" \
    | docker exec -i "$DB_CONTAINER" bash -c "cat > /tmp/$mname && chmod 0644 /tmp/$mname" >/dev/null \
    || die "could not stage /tmp/$mname inside the container"
  docker exec -i -e NLS_LANG=AMERICAN_AMERICA.AL32UTF8 -e "CONN=$APP_USER/$APP_USER_PWD@$PDB" \
    "$DB_CONTAINER" bash -c "sqlplus -S \"\$CONN\" @/tmp/$mname" < /dev/null > "$log_file" 2>&1
  local rc=$?
  docker exec "$DB_CONTAINER" rm -f "/tmp/$name" "/tmp/$mname" >/dev/null 2>&1 || true
  return $rc
}


# sql_unexpected_error <logfile> [codes_to_ignore]
# Business errors raised by ERP_OPERATIONS are *expected* inside the incident
# script, so they are not treated as failures. Default ignore list covers the
# documented codes of docs/04-plsql-spec.md section 4.
sql_unexpected_error() {
  local log="$1" ignore="${2:-2000[1-9]|2001[0-2]}"
  grep -E '^ORA-' "$log" 2>/dev/null | grep -vE "^ORA-(01403|$ignore)" | grep -q . && return 0
  grep -qE '^SP2-[0-9]{4}' "$log" 2>/dev/null && return 0
  return 1
}

# ---------------------------------------------------------------- connectivity
# db_probe  -> success when the schema user can open a session
db_probe() {
  local probe_log=/tmp/erp_probe.log name mname
  if [ -n "$SQLPLUS" ]; then
    printf '%s\n' 'SELECT 1 AS ERP_PROBE_OK FROM dual;' 'EXIT;' > /tmp/erp_probe.sql
    "$SQLPLUS" -S "$APP_USER/$APP_USER_PWD@$PDB" @/tmp/erp_probe.sql > "$probe_log" 2>&1
  else
    name="probe_$$_$(date +%s).sql"; mname="probem_$$_$(date +%s).sql"
    printf '%s\n' 'SELECT 1 AS ERP_PROBE_OK FROM dual;' 'EXIT;' \
      | docker exec -i "$DB_CONTAINER" bash -c "cat > /tmp/$name && chmod 0644 /tmp/$name" \
        >/dev/null 2>&1 || return 1
    _sqlplus_master "/tmp/$name" \
      | docker exec -i "$DB_CONTAINER" bash -c "cat > /tmp/$mname && chmod 0644 /tmp/$mname" >/dev/null 2>&1
    docker exec -i -e "CONN=$APP_USER/$APP_USER_PWD@$PDB" "$DB_CONTAINER" \
      bash -c "sqlplus -S \"\$CONN\" @/tmp/$mname" < /dev/null > "$probe_log" 2>&1
    docker exec "$DB_CONTAINER" rm -f "/tmp/$name" "/tmp/$mname" >/dev/null 2>&1 || true
  fi
  grep -q 'ERP_PROBE_OK' "$probe_log"
}

# wait_for_db [timeout_seconds]
wait_for_db() {
  local timeout="${1:-360}" waited=0 health='unknown'
  log "waiting for Oracle to accept logins as '$APP_USER' (max ${timeout}s)"
  while [ "$waited" -lt "$timeout" ]; do
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}no-healthcheck{{end}}' \
              "$DB_CONTAINER" 2>/dev/null || echo 'container-missing')"
    if db_probe; then
      ok "database is ready ($APP_USER@$PDB, container health: $health)"
      return 0
    fi
    sleep 5; waited=$((waited + 5)); printf '.'
  done
  printf '\n'
  die "database was not ready within ${timeout}s (health: $health; probe: $(tr '\n' ' ' < /tmp/erp_probe.log 2>/dev/null | tail -c 180))"
}

# wait_for_api [timeout_seconds]  -> polls /api/health until it answers 200
wait_for_api() {
  local timeout="${1:-120}" waited=0 code
  log "waiting for the API on $BASE_URL (max ${timeout}s)"
  while [ "$waited" -lt "$timeout" ]; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE_URL/api/health" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ]; then
      ok "API is up on $BASE_URL"
      return 0
    fi
    sleep 2; waited=$((waited + 2)); printf '.'
  done
  printf '\n'
  die "the API did not become ready within ${timeout}s"
}

# ------------------------------------------------------------------- artifacts
# archive_artifact <label> <logfile> [grep-pattern]
archive_artifact() {
  local label="$1" log="$2" pattern="${3:-}"
  mkdir -p "$ARTIFACT_DIR" || return 0
  local out; out="$ARTIFACT_DIR/$label-$(date +%Y%m%d-%H%M%S).log"
  cp "$log" "$out" 2>/dev/null || return 0
  ls -1t "$ARTIFACT_DIR/$label"-*.log 2>/dev/null | tail -n "+$((ARTIFACT_KEEP + 1))" | xargs rm -f 2>/dev/null
  ok "evidence archived: artifacts/$(basename "$out")"
  [ -n "$pattern" ] && grep -hE "$pattern" "$out" | head -30
  return 0
}
