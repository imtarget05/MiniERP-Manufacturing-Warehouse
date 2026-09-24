#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/run-sql.sh
# PURPOSE: execute the SQL scripts in the required order:
#            sql/01_schema.sql             -> DDL, 13 core tables + indexes
#            sql/05_automation_schema.sql  -> automation tables (7) + widened PO states
#            sql/07_traceability_schema.sql -> lot/location/genealogy/label/idempotency/audit tables
#            sql/02_plsql.sql              -> package ERP_OPERATIONS (must compile VALID)
#            sql/06_automation_plsql.sql   -> package ERP_AUTOMATION (must compile VALID)
#            sql/08_traceability_plsql.sql -> package ERP_TRACEABILITY (must compile VALID)
#            sql/03_seed.sql               -> master data of the footwear plant
#          Optional:  --with-incident      also runs sql/04_incident_scenarios.sql
# USAGE:   bash scripts/run-sql.sh [--with-incident]
#          RESET=1 bash scripts/run-sql.sh        (drop & recreate the schema)
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

WITH_INCIDENT=0
for arg in "$@"; do
  case "$arg" in
    --with-incident) WITH_INCIDENT=1 ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option: $arg (try --with-incident)" ;;
  esac
done

rule
printf '%b\n' "${C_B} Loading the Mini ERP database${C_0}"
rule

db_probe || die "cannot log in as $APP_USER@$PDB - run: bash scripts/start-db.sh"

FILES=(01_schema.sql 05_automation_schema.sql 07_traceability_schema.sql 02_plsql.sql 06_automation_plsql.sql 08_traceability_plsql.sql 03_seed.sql 09_traceability_seed.sql 10_helpdesk_schema.sql 11_rbac_seed.sql)
[ "$WITH_INCIDENT" = "1" ] && FILES+=(04_incident_scenarios.sql)

mkdir -p "$ARTIFACT_DIR"
FAILED=0
for f in "${FILES[@]}"; do
  log "executing sql/$f"
  tmp="$(mktemp)"
  if ! run_sql "$SQL_DIR/$f" "$tmp"; then
    err "sqlplus exited with an error while running $f"
    tail -25 "$tmp" | sed 's/^/       /'
    FAILED=1
  fi
  if [ "$f" = "04_incident_scenarios.sql" ]; then
    # ORA-20007 is deliberately raised and handled inside this script.
    if grep -q 'ORA-20007' "$tmp"; then
      ok "expected business error ORA-20007 raised (material shortage demo)"
    fi
    if grep -q 'ALL_ASSERTIONS_PASSED' "$tmp"; then
      ok "incident scenario: every assertion passed"
    else
      err "incident scenario did not report ALL_ASSERTIONS_PASSED"
      FAILED=1
    fi
  elif sql_unexpected_error "$tmp"; then
    err "sql/$f produced unexpected errors:"
    grep -E '^ORA-|^SP2-' "$tmp" | sort -u | head -10 | sed 's/^/       /'
    FAILED=1
  else
    ok "sql/$f executed"
  fi
  archive_artifact "sql-${f%.sql}" "$tmp" '^ORA-'
  rm -f "$tmp"
done

# ------------------------------------------------------------ compile status
log "verifying that ERP_OPERATIONS + ERP_AUTOMATION + ERP_TRACEABILITY compiled VALID and all tables exist"
verify="$(mktemp)"
verify_log="$(mktemp)"
# Note: USER_PROCEDURES lists package subprograms under OBJECT_NAME, there is no
# PACKAGE_NAME column in that data-dictionary view.
printf '%s\n' \
  "SELECT 'ERP_SCHEMA_TABLES=' || COUNT(*) FROM user_tables;" \
  "SELECT 'ERP_INVALID_OBJECTS=' || COUNT(*) FROM user_objects WHERE status <> 'VALID';" \
  "SELECT 'ERP_PKG_OPS_VALID=' || COUNT(*) FROM user_objects" \
  " WHERE object_name = 'ERP_OPERATIONS' AND status = 'VALID';" \
  "SELECT 'ERP_PKG_AUT_VALID=' || COUNT(*) FROM user_objects" \
  " WHERE object_name = 'ERP_AUTOMATION' AND status = 'VALID';" \
  "SELECT 'ERP_PKG_TR_VALID=' || COUNT(*) FROM user_objects" \
  " WHERE object_name = 'ERP_TRACEABILITY' AND status = 'VALID';" \
  "SELECT 'ERP_HELPDESK_TABLE=' || COUNT(*) FROM user_tables WHERE table_name = 'HELPDESK_DELIVERY';" \
  "SELECT 'ERP_PROCEDURES=' || COUNT(*) FROM user_procedures" \
  " WHERE object_name IN ('ERP_OPERATIONS', 'ERP_AUTOMATION', 'ERP_TRACEABILITY');" \
  "EXIT;" > "$verify"
run_sql "$verify" "$verify_log" || true
TBL="$(grep -oE 'ERP_SCHEMA_TABLES=[0-9]+'  "$verify_log" | tail -1 | cut -d= -f2)"
INV="$(grep -oE 'ERP_INVALID_OBJECTS=[0-9]+' "$verify_log" | tail -1 | cut -d= -f2)"
PKG1="$(grep -oE 'ERP_PKG_OPS_VALID=[0-9]+'  "$verify_log" | tail -1 | cut -d= -f2)"
PKG2="$(grep -oE 'ERP_PKG_AUT_VALID=[0-9]+'  "$verify_log" | tail -1 | cut -d= -f2)"
PKG3="$(grep -oE 'ERP_PKG_TR_VALID=[0-9]+'   "$verify_log" | tail -1 | cut -d= -f2)"
PRC="$(grep -oE 'ERP_PROCEDURES=[0-9]+'      "$verify_log" | tail -1 | cut -d= -f2)"
rm -f "$verify" "$verify_log"

[ -n "$TBL" ]  || TBL=0
[ -n "$INV" ]  || INV=0
[ -n "$PKG1" ] || PKG1=0
[ -n "$PKG2" ] || PKG2=0
[ -n "$PKG3" ] || PKG3=0

if [ "$INV" != "0" ]; then
  err "there are $INV INVALID object(s) in the schema - a package did not compile"
  FAILED=1
elif [ "$PKG1" -lt 2 ] || [ "$PKG2" -lt 2 ] || [ "$PKG3" -lt 2 ]; then
  err "packages incomplete (ERP_OPERATIONS: $PKG1, ERP_AUTOMATION: $PKG2, ERP_TRACEABILITY: $PKG3 - need 2 + 2 + 2)"
  FAILED=1
else
  ok "packages ERP_OPERATIONS + ERP_AUTOMATION + ERP_TRACEABILITY (spec + body) are VALID"
fi

if [ "$TBL" -ge 31 ]; then
  ok "schema contains $TBL tables (13 core + 7 automation + 10 traceability/security + 1 Helpdesk), $PRC package subprograms exposed"
else
  err "only $TBL tables found - expected 31 (13 core + 7 automation + 10 traceability/security + 1 Helpdesk)"; FAILED=1
fi

rule
if [ "$FAILED" = "1" ]; then
  printf '%b\n' "${C_R} SQL load FAILED - inspect artifacts/sql-*.log${C_0}"
  exit 1
fi
printf '%b\n' "${C_G} Database loaded successfully.${C_0}"
printf '%b\n' "  Next: bash scripts/test-incident.sh   (reproducible PO001 incident)"
rule
