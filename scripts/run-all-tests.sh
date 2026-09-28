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
#
# QA GATE: stages 3, 6 and 7 call test-incident.sh / test-api.sh /
#          test-traceability.sh, all of which WRITE. scripts/qa-gate.sh refuses
#          them unless the target is named and the operator has declared it a
#          disposable, because test-api.sh used to default to
#          http://localhost:5000 - which in a shared workspace is a real system.
#          This runner creates and owns its own database (stage 1/2), so it
#          asserts the disposable acknowledgement for its own target - see
#          MINIERP_QA_DISPOSABLE below. It never sets MINIERP_QA_LIVE_SMOKE.
#
# TEST DSN: stage 5 runs `dotnet test`, and the test host is fail-closed -
#          tests/MiniERP.Api.Tests/TestOracleDsnGuard.cs refuses to build unless
#          the RUN grants a data source through the environment, because
#          src/appsettings.json points at a real Oracle listener and the suite
#          writes a row on every POST. So this runner exports
#          ConnectionStrings__OracleDb itself (export_test_dsn) naming the
#          database it just created. It never uses the ambient opt-in
#          MINIERP_TEST_ALLOW_AMBIENT_DB, which is exactly the path that would
#          let a test run inherit appsettings.json's target. This is also the
#          DSN CI job "2. Full Acceptance Pipeline" runs dotnet test with: the
#          workflow does not carry a password, the runner derives one from the
#          container it started. See .github/workflows/ci.yml.
# ============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Operator-supplied targets, captured before lib.sh substitutes its defaults, so
# the gate can tell "the caller said so" from "lib.sh guessed".
QA_OWNED_BASE_URL="${BASE_URL:-}"
QA_OWNED_DB_CONTAINER="${DB_CONTAINER:-}"
QA_OWNED_APP_USER="${APP_USER:-}"

source "$HERE/lib.sh"
# shellcheck source=scripts/qa-gate.sh
source "$HERE/qa-gate.sh"

# This runner starts its own Oracle (stage 1) and its own API (stage 6), so it
# is asserting ownership of a target it created, not borrowing someone else's.
# Declaring it here is what makes stages 3/6/7 runnable; deleting these three
# lines makes those stages REFUSE (exit 78) instead of guessing a target.
export MINIERP_QA_DISPOSABLE=1
# Identity, not just an acknowledgement: scripts/qa-gate.sh REFUSES
# MINIERP_QA_DISPOSABLE=1 when DB_CONTAINER is minierp-oracle, when the volume
# is 04-minierp-manufacturing-warehouse_oracle_data, or when the compose project
# is 04-minierp-manufacturing-warehouse - the names docker-compose.yml uses by
# default, which are the same ones the shared production stack uses. So this
# runner pins a QA identity, and start-db.sh brings the stack up under it via
# docker-compose.qa.yml. The API port may stay 5000: the gate accepts a
# production-shaped port when the database identity is QA-marked and
# MINIERP_QA_INSTANCE is set.
export MINIERP_QA_INSTANCE="${MINIERP_QA_INSTANCE:-minierp-qa}"
export QA_CONTAINER_NAME="${QA_CONTAINER_NAME:-minierp-qa-oracle}"
export QA_DB_PROJECT="$MINIERP_QA_INSTANCE"
export QA_DB_VOLUME="${MINIERP_QA_INSTANCE}_oracle_data"
export DB_CONTAINER="$QA_CONTAINER_NAME"
# The declaration above is EARNED only when this runner starts the database
# itself (stage 1). With SKIP_DOCKER=1 the container is inherited from whoever
# started it - which in a shared workspace may be a real system named
# minierp-oracle. Say so out loud, because the gate cannot tell the difference:
# a self-declared acknowledgement is not evidence.
if [ "${SKIP_DOCKER:-0}" = "1" ]; then
  printf '%b\n' "${C_Y}WARN SKIP_DOCKER=1: this run did NOT create $DB_CONTAINER; it is asserting a disposable over a database it inherited.${C_0}"
  printf '%b\n' "     Confirm it is a throwaway before stages 3/6/7 write to it." >&2
fi
# An operator-supplied DB_CONTAINER wins, but it then has to BE a QA identity
# or the gate will refuse stages 3/6/7 - which is the intended behaviour.
if [ -n "$QA_OWNED_DB_CONTAINER" ]; then DB_CONTAINER="$QA_OWNED_DB_CONTAINER"; fi
export DB_CONTAINER
APP_USER="${QA_OWNED_APP_USER:-$APP_USER}";              export APP_USER
BASE_URL="${QA_OWNED_BASE_URL:-$BASE_URL}";              export BASE_URL

# Grants the .NET test host its data source, explicitly, for stage 5.
#
# Why the runner has to do this: TestOracleDsnGuard is fail-closed, so a
# `dotnet test` with no ConnectionStrings__OracleDb in the environment dies in
# fixture construction (it would otherwise fall back to src/appsettings.json).
# The value is built from the same APP_USER / APP_USER_PWD / PDB that stage 1
# handed to the container, and the host port is ASKED OF the container rather
# than hardcoded, so a remapped port cannot silently point the integration
# tests somewhere else.
#
# Three deliberate properties:
#   1. it always overwrites an inherited DSN - this runner created the database,
#      so the tests may only ever see this run's throwaway;
#   2. it clears MINIERP_TEST_ALLOW_AMBIENT_DB, so the grant below is the only
#      thing standing between the suite and a connection;
#   3. it prints the DSN shape with the password omitted - enough for a reviewer
#      to see the target, not enough to leak the credential into CI logs.
export_test_dsn() {
  local host port mapping dsn
  host="${TEST_DSN_HOST:-127.0.0.1}"
  port="${TEST_DSN_PORT:-}"
  if [ -z "$port" ]; then
    # PDB_PORT is the CONTAINER port Oracle listens on, which
    # docker-compose.yml publishes as 1521:1521. The host port it is published
    # on is read back from the container below, so changing the mapping needs no
    # edit here - only changing the listener port does.
    mapping="$(docker port "$DB_CONTAINER" "${PDB_PORT:-1521}/tcp" 2>/dev/null | head -1)"
    port="${mapping##*:}"
    if [ -z "$port" ] || [ "$port" = "$mapping" ] || [ -z "$mapping" ]; then
      err "cannot resolve the published Oracle port for '$DB_CONTAINER' (docker port: ${mapping:-<none>}); refusing to guess a data source"
      return 1
    fi
  fi
  dsn="User Id=${APP_USER};Password=${APP_USER_PWD};Data Source=${host}:${port}/${PDB};Pooling=true;Min Pool Size=1;"
  if [ -n "${ConnectionStrings__OracleDb:-}" ] && [ "${ConnectionStrings__OracleDb}" != "$dsn" ]; then
    warn "replacing an inherited ConnectionStrings__OracleDb; stage 5 only ever runs against the disposable this runner created"
  fi
  export ConnectionStrings__OracleDb="$dsn"
  unset MINIERP_TEST_ALLOW_AMBIENT_DB
  ok "test data source granted explicitly: User Id=$APP_USER;Data Source=$host:$port/$PDB (password redacted; container $DB_CONTAINER)"
  return 0
}

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
# test-incident.sh needs no base URL: it is authorised on the database target,
# which this runner owns (see the QA GATE note in the header).
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
  # Fail closed: no granted DSN, no test run. Without this the guarded fixtures
  # abort (correctly) with a wall of identical refusals that reads like a broken
  # test suite instead of a missing variable.
  export_test_dsn || return 1
  cd "$REPO_ROOT/tests/MiniERP.Api.Tests" || return 1
  local out; out="$(mktemp)"
  dotnet test --nologo -c Release > "$out" 2>&1
  local rc=$?
  grep -E 'Passed!|Failed!|error|\[FAIL\]|Passed:|Failed:' "$out" | sed 's/^/       /' | tail -30
  archive_artifact "dotnet-test" "$out" 'Passed:|Failed:|\[FAIL\]'
  rm -f "$out"
  return $rc
}
stage "5/9 dotnet test (unit + integration, explicit disposable DSN)" run_dotnet_test || true

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
