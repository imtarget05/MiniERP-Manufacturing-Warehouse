#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/verify-backup.sh
# PURPOSE: Validates the integrity of an Oracle schema restore or active database:
#          - Verifies all 31 required tables exist (core, automation, traceability, Helpdesk)
#          - Verifies 3 packages (ERP_OPERATIONS, ERP_AUTOMATION, ERP_TRACEABILITY) are VALID
#          - Verifies key warehouse/item data and a successful INVENTORY_LOT query
#          - Verifies Helpdesk outbox table
# USAGE:   bash scripts/verify-backup.sh
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

rule
printf '%b\n' "${C_B} MiniERP Database & Backup Verification${C_0}"
rule

db_probe || die "Oracle database is not reachable at $APP_USER@$PDB - run: bash scripts/start-db.sh"

chk_sql="$(mktemp)"
chk_log="$(mktemp)"

cat << 'EOF' > "$chk_sql"
SET LINESIZE 200 PAGESIZE 0 FEEDBACK OFF HEADING OFF

-- 1. Table Counts & Crucial Tables
SELECT 'TABLE_COUNT=' || COUNT(*) FROM user_tables;
SELECT 'HAS_WH=' || COUNT(*) FROM user_tables WHERE table_name = 'WAREHOUSE';
SELECT 'HAS_ITEM=' || COUNT(*) FROM user_tables WHERE table_name = 'ITEM';
SELECT 'HAS_STOCK=' || COUNT(*) FROM user_tables WHERE table_name = 'STOCK';
SELECT 'HAS_LOT=' || COUNT(*) FROM user_tables WHERE table_name = 'INVENTORY_LOT';
SELECT 'HAS_LOT_STOCK=' || COUNT(*) FROM user_tables WHERE table_name = 'LOT_STOCK';
SELECT 'HAS_TRACE_EVENT=' || COUNT(*) FROM user_tables WHERE table_name = 'TRACEABILITY_EVENT';
SELECT 'HAS_LABEL_JOB=' || COUNT(*) FROM user_tables WHERE table_name = 'LABEL_PRINT_JOB';
SELECT 'HAS_AUDIT=' || COUNT(*) FROM user_tables WHERE table_name = 'APP_AUDIT_EVENT';
SELECT 'HAS_HELPDESK=' || COUNT(*) FROM user_tables WHERE table_name = 'HELPDESK_DELIVERY';

-- 2. Packages Valid
SELECT 'PKG_OPS_VALID=' || COUNT(*) FROM user_objects WHERE object_name = 'ERP_OPERATIONS' AND object_type = 'PACKAGE BODY' AND status = 'VALID';
SELECT 'PKG_AUT_VALID=' || COUNT(*) FROM user_objects WHERE object_name = 'ERP_AUTOMATION' AND object_type = 'PACKAGE BODY' AND status = 'VALID';
SELECT 'PKG_TR_VALID=' || COUNT(*) FROM user_objects WHERE object_name = 'ERP_TRACEABILITY' AND object_type = 'PACKAGE BODY' AND status = 'VALID';

-- 3. Master Data & Traceability Seed Integrity
SELECT 'WH_RAW_EXISTS=' || COUNT(*) FROM warehouse WHERE code = 'WH_RAW';
SELECT 'ITEM_RUBBER_EXISTS=' || COUNT(*) FROM item WHERE code = 'MAT_RUBBER_01';
SELECT 'LOC_RCV_EXISTS=' || COUNT(*) FROM warehouse_location WHERE location_code = 'RCV-01';
SELECT 'LOT_QUERY_OK=' || CASE WHEN COUNT(*) >= 0 THEN 1 ELSE 0 END FROM inventory_lot;

EXIT;
EOF

log "Running database integrity checks..."
run_sql "$chk_sql" "$chk_log" || die "Database verification SQL failed"
rm -f "$chk_sql"

val_for() { grep -oE "$1=[0-9]+" "$chk_log" | tail -1 | cut -d= -f2 || echo 0; }

TBL_COUNT="$(val_for TABLE_COUNT)"
HAS_WH="$(val_for HAS_WH)"
HAS_ITEM="$(val_for HAS_ITEM)"
HAS_STOCK="$(val_for HAS_STOCK)"
HAS_LOT="$(val_for HAS_LOT)"
HAS_LOT_STOCK="$(val_for HAS_LOT_STOCK)"
HAS_TRACE_EVENT="$(val_for HAS_TRACE_EVENT)"
HAS_LABEL_JOB="$(val_for HAS_LABEL_JOB)"
HAS_AUDIT="$(val_for HAS_AUDIT)"
HAS_HELPDESK="$(val_for HAS_HELPDESK)"
LOT_QUERY_OK="$(val_for LOT_QUERY_OK)"
PKG_OPS="$(val_for PKG_OPS_VALID)"
PKG_AUT="$(val_for PKG_AUT_VALID)"
PKG_TR="$(val_for PKG_TR_VALID)"
WH_RAW="$(val_for WH_RAW_EXISTS)"
ITEM_RUBBER="$(val_for ITEM_RUBBER_EXISTS)"
LOC_RCV="$(val_for LOC_RCV_EXISTS)"

rm -f "$chk_log"

FAILURES=0

log "Verifying schema tables (expected >= 31, actual: $TBL_COUNT)..."
if [ "$TBL_COUNT" -ge 31 ]; then
  ok "Schema table count passed ($TBL_COUNT >= 31)"
else
  err "Insufficient table count: $TBL_COUNT (expected >= 31)"
  FAILURES=$((FAILURES + 1))
fi

log "Verifying core and traceability tables existence..."
for pair in "WAREHOUSE:$HAS_WH" "ITEM:$HAS_ITEM" "STOCK:$HAS_STOCK" \
            "INVENTORY_LOT:$HAS_LOT" "LOT_STOCK:$HAS_LOT_STOCK" \
            "TRACEABILITY_EVENT:$HAS_TRACE_EVENT" "LABEL_PRINT_JOB:$HAS_LABEL_JOB" \
            "APP_AUDIT_EVENT:$HAS_AUDIT" "HELPDESK_DELIVERY:$HAS_HELPDESK"; do
  name="${pair%%:*}"
  has="${pair##*:}"
  if [ "$has" -gt 0 ]; then
    ok "Table $name exists"
  else
    err "Required table $name is missing"
    FAILURES=$((FAILURES + 1))
  fi
done

log "Verifying PL/SQL package bodies are VALID..."
for pkg in "ERP_OPERATIONS:$PKG_OPS" "ERP_AUTOMATION:$PKG_AUT" "ERP_TRACEABILITY:$PKG_TR"; do
  name="${pkg%%:*}"
  valid="${pkg##*:}"
  if [ "$valid" -gt 0 ]; then
    ok "Package body $name is VALID"
  else
    err "Package body $name is NOT valid"
    FAILURES=$((FAILURES + 1))
  fi
done

log "Verifying seeded master data..."
[ "$WH_RAW" -gt 0 ] && ok "Warehouse WH_RAW exists" || { err "WH_RAW missing"; FAILURES=$((FAILURES + 1)); }
[ "$ITEM_RUBBER" -gt 0 ] && ok "Item MAT_RUBBER_01 exists" || { err "MAT_RUBBER_01 missing"; FAILURES=$((FAILURES + 1)); }
[ "$LOC_RCV" -gt 0 ] && ok "Location RCV-01 exists" || { err "Location RCV-01 missing"; FAILURES=$((FAILURES + 1)); }
[ "$LOT_QUERY_OK" -gt 0 ] && ok "INVENTORY_LOT traceability query succeeds" || { err "INVENTORY_LOT query failed"; FAILURES=$((FAILURES + 1)); }

if [ "$FAILURES" -eq 0 ]; then
  rule
  ok "ALL VERIFICATION CHECKS PASSED: Database schema and backup are valid."
  rule
  exit 0
else
  rule
  err "VERIFICATION FAILED: Encountered $FAILURES issues."
  rule
  exit 1
fi
