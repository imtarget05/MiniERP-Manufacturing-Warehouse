#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/run-all-tests.sh
# PURPOSE: one-shot acceptance run. Executes the full DoD checklist:
#            1  start Oracle container + wait until it is usable
#            2  load schema / package / seed data
#            3  reproduce & verify the PO001 shortage incident (PL/SQL level)
#            4  dotnet restore + build (Release)
#            5  dotnet test  (unit + integration against the live database)
#            6  start the API and run the endpoint smoke test (curl level)
#            7  run the real traceability E2E workflow
#            8  create a fresh backup and verify the active schema (non-destructive)
#            9  export artifacts/swagger.json as the acceptance evidence
# USAGE:   bash scripts/run-all-tests.sh
#          SKIP_DOCKER=1 bash scripts/run-all-tests.sh   (container already up)
#          KEEP_API=1    bash scripts/run-all-tests.sh   (leave the API running)
# EXIT:    0 only when every stage passed
# ============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/lib.sh"

STAGES=""
FAILED_STAGES=""
stage() {  # stage <name> <command...>
  local name="$1"; shift
  rule
  printf '%b\n' "${C_B}>>> $name${C_0}"
  rule
  if "$@"; then
    STAGES="$STAGES\n  OK    $name"
    return 0
  fi
  STAGES="$STAGES\n  FAIL  $name"
  FAILED_STAGES="$FAILED_STAGES\n  - $name"
  warn "stage failed: $name"
  return 1
}

cleanup() {
  if [ "${KEEP_API:-0}" != "1" ]; then
    bash "$HERE/start-api.sh" --stop >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

START=$(date +%s)
printf '%b\n' "${C_B}############################################################################${C_0}"
printf '%b\n' "${C_B}#  Mini ERP - full acceptance run                                           #${C_0}"
printf '%b\n' "${C_B}#  repo : $REPO_ROOT${C_0}"
printf '%b\n' "${C_B}############################################################################${C_0}"

# ---------------------------------------------------------------- 1 database
if [ "${SKIP_DOCKER:-0}" != "1" ]; then
  stage "1/9 start Oracle container (docker compose up -d)" bash "$HERE/start-db.sh" || true
else
  log "1/9 docker start skipped (SKIP_DOCKER=1)"
  wait_for_db "${DB_TIMEOUT:-120}" || true
fi

# --------------------------------------------------------------------- 2 SQL
stage "2/9 load schema + package + seed data" bash "$HERE/run-sql.sh" || true

# ----------------------------------------------------------------- 3 incident
stage "3/9 verify the PO001 incident scenario (PL/SQL)" bash "$HERE/test-incident.sh" || true

# -------------------------------------------------------------- 4 build .NET
run_build() {
  find_dotnet || { err ".NET SDK 8 not found"; return 1; }
  cd "$REPO_ROOT/src" || return 1
  local out; out="$(mktemp)"
  dotnet restore --nologo > "$out" 2>&1
  # quality gate: compiler warnings must never survive into the acceptance build.
  # NuGetAudit is off so that an offline/vulnerability-advisory warning cannot
  # turn a green build red on another machine.
  dotnet build -c Release --nologo \
    -p:TreatWarningsAsErrors=true -p:NuGetAudit=false >> "$out" 2>&1
  local rc=$?
  grep -E 'error|warning|Build succeeded|Warning\(s\)|Error\(s\)' "$out" | sed 's/^/       /'
  archive_artifact "build" "$out" 'warning|error'
  rm -f "$out"
  [ $rc -eq 0 ] && ok "Release build succeeded with 0 warnings / 0 errors" \
                || err "build failed (warnings are treated as errors)"
  return $rc
}
stage "4/9 dotnet restore + build (Release, warnings as errors)" run_build || true

# ------------------------------------------------------------------ 5 dotnet test
run_dotnet_test() {
  find_dotnet || { err ".NET SDK 8 not found"; return 1; }
  cd "$REPO_ROOT/tests/MiniERP.Api.Tests" || return 1
  local out; out="$(mktemp)"
  dotnet test --nologo -c Release > "$out" 2>&1
  local rc=$?
  grep -E 'Passed!|Failed!|error|\[FAIL\]|Passed:|Failed:' "$out" | sed 's/^/       /' | tail -30
  archive_artifact "dotnet-test" "$out" 'Passed:|Failed:|\[FAIL\]'
  rm -f "$out"
  return $rc
}
stage "5/9 dotnet test (unit + integration)" run_dotnet_test || true

# -------------------------------------------------------- 6 API smoke test
run_api_smoke() {
  RUN_MODE=bg bash "$HERE/start-api.sh" || { err "API did not start"; return 1; }
  bash "$HERE/test-api.sh" "$BASE_URL"
  local rc=$?
  archive_artifact "api-server" "$REPO_ROOT/artifacts/api.log" 'Exception|error'
  return $rc
}
stage "6/9 API endpoint smoke test (curl, authenticated routes)" run_api_smoke || true

# --------------------------------------------------------- 7 traceability E2E
run_traceability_e2e() {
  bash "$HERE/run-sql.sh" || return 1
  RUN_MODE=bg bash "$HERE/start-api.sh" || return 1
  bash "$HERE/test-traceability.sh" "$BASE_URL"
}
stage "7/9 real traceability E2E (receive -> trace -> genealogy -> reconcile)" run_traceability_e2e || true

# ------------------------------------------------------- 8 backup + verify
run_restore_safety_lock_check() {
  local log combined
  log="$(mktemp)"
  combined="$(mktemp)"

  # Layer 1: the destructive flag must be explicit.
  if env -u ALLOW_DESTRUCTIVE_RESTORE -u CONFIRM_RESTORE \
      bash "$HERE/restore-db.sh" "$REPO_ROOT" >"$log" 2>&1; then
    err "restore unexpectedly succeeded without the destructive flag"
    rm -f "$log" "$combined"
    return 1
  fi
  if ! grep -q "SAFETY LOCK.*ALLOW_DESTRUCTIVE_RESTORE" "$log"; then
    err "restore did not reject the missing destructive flag"
    cat "$log" >&2
    rm -f "$log" "$combined"
    return 1
  fi
  ok "restore safety lock rejected a missing ALLOW_DESTRUCTIVE_RESTORE flag"

  # Layer 2: confirmation is independently required even after the allow flag.
  if env -u CONFIRM_RESTORE ALLOW_DESTRUCTIVE_RESTORE=true \
      bash "$HERE/restore-db.sh" "$REPO_ROOT" >"$log" 2>&1; then
    err "restore unexpectedly succeeded without confirmation"
    rm -f "$log" "$combined"
    return 1
  fi
  if ! grep -q "SAFETY LOCK.*CONFIRM_RESTORE" "$log"; then
    err "restore did not reject the missing confirmation flag"
    cat "$log" >&2
    rm -f "$log" "$combined"
    return 1
  fi
  ok "restore safety lock rejected a missing CONFIRM_RESTORE flag"

  cat "$log" > "$combined"
  archive_artifact "restore-safety-lock" "$combined" 'SAFETY LOCK'
  rm -f "$log" "$combined"
}

run_backup_verify() {
  local backup_dir
  backup_dir="$(mktemp -d "${TMPDIR:-/tmp}/minierp-acceptance-backup.XXXXXX")"
  bash "$HERE/backup-db.sh" "$backup_dir" || return 1
  bash "$HERE/verify-backup.sh" || return 1
  run_restore_safety_lock_check || return 1
  ok "Fresh backup, schema verification and restore safety checks passed: $backup_dir"
}
stage "8/9 fresh backup + non-destructive verification + restore safety locks" run_backup_verify || true

# ----------------------------------------------------------------- 9 swagger
stage "9/9 export swagger.json evidence" bash "$HERE/export-swagger.sh" || true

# ------------------------------------------------------------------- summary
END=$(date +%s)
rule
printf '%b\n' "${C_B} ACCEPTANCE SUMMARY (${C_0}$((END - START))s${C_B})${C_0}"
rule
printf '%b\n' "$STAGES"
rule
if [ -n "$FAILED_STAGES" ]; then
  printf '%b\n' "${C_R} RESULT: ACCEPTANCE FAILED${C_0}"
  printf '%b\n' "  failed stages:$FAILED_STAGES"
  printf '%b\n' "  logs: artifacts/*.log"
  exit 1
fi
printf '%b\n' "${C_G} RESULT: ALL 9 STAGES PASSED - the system is verified end to end${C_0}"
printf '%b\n' "  evidence: artifacts/  (sql-*, incident-*, build-*, dotnet-test-*, api-*, swagger.json)"
rule
