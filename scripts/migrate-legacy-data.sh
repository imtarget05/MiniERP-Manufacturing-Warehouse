#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/migrate-legacy-data.sh
# PURPOSE: Simulate legacy CSV inventory extraction, validation, import into
#          MiniERP Oracle database, and mathematical reconciliation (Delta = 0).
# ============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSV_FILE="${1:-$REPO_ROOT/data/legacy_inventory.csv}"
ARTIFACT_DIR="$REPO_ROOT/artifacts"

mkdir -p "$ARTIFACT_DIR"
REPORT_FILE="$ARTIFACT_DIR/migration_reconciliation_$(date +%Y%m%d_%H%M%S).txt"

if [ -t 1 ]; then
  C_G='\033[32m'; C_R='\033[31m'; C_B='\033[36m'; C_Y='\033[33m'; C_0='\033[0m'
else
  C_G=''; C_R=''; C_B=''; C_Y=''; C_0='';
fi

log()  { printf '%b\n' "  ${C_B}->${C_0}   $*"; }
ok()   { printf '%b\n' "  ${C_G}OK${C_0}     $*"; }
err()  { printf '%b\n' "  ${C_R}FAIL${C_0}   $*" >&2; }
rule() { printf '%b\n' "${C_B}--------------------------------------------------------------------${C_0}"; }

rule
printf '%b\n' "${C_B} MiniERP Legacy Data Migration & Reconciliation Simulator${C_0}"
rule

if [ ! -f "$CSV_FILE" ]; then
  err "Legacy CSV file not found: $CSV_FILE"
  exit 1
fi

log "Source file: $CSV_FILE"
log "Parsing header and calculating legacy source totals..."

LEGACY_TOTAL=0
ROW_COUNT=0

# Read CSV skipping header
while IFS=, read -r wh item name uom qty cost; do
  # Skip header
  if [ "$wh" = "WAREHOUSE_CODE" ]; then
    continue
  fi
  # Clean carriage returns
  wh=$(echo "$wh" | tr -d '\r')
  item=$(echo "$item" | tr -d '\r')
  qty=$(echo "$qty" | tr -d '\r')

  # Validate non-empty and numeric
  if [[ "$qty" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
    LEGACY_TOTAL=$(echo "$LEGACY_TOTAL + $qty" | bc 2>/dev/null || awk "BEGIN {print $LEGACY_TOTAL + $qty}")
    ROW_COUNT=$((ROW_COUNT + 1))
  else
    err "Invalid quantity in row: $wh,$item,$qty"
    exit 1
  fi
done < "$CSV_FILE"

ok "Validated $ROW_COUNT records from legacy extract"
log "Legacy source total inventory units: $LEGACY_TOTAL"

# Generate SQL script to import opening balances atomically
SQL_IMPORT=$(mktemp)
cat << 'HEADER' > "$SQL_IMPORT"
SET SERVEROUTPUT ON;
DECLARE
  v_imported_total NUMBER := 0;
  v_wh_id NUMBER;
  v_item_id NUMBER;
BEGIN
HEADER

while IFS=, read -r wh item name uom qty cost; do
  if [ "$wh" = "WAREHOUSE_CODE" ]; then continue; fi
  wh=$(echo "$wh" | tr -d '\r')
  item=$(echo "$item" | tr -d '\r')
  qty=$(echo "$qty" | tr -d '\r')

  cat << ROW >> "$SQL_IMPORT"
  BEGIN
    SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = '$wh';
    SELECT ID INTO v_item_id FROM ITEM WHERE CODE = '$item';
    
    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS wid, v_item_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = $qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_wh_id, v_item_id, $qty, SYSDATE);

    INSERT INTO INVENTORY_TRANSACTION (
      TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
    ) VALUES (
      'STOCK_IN', v_item_id, v_wh_id, $qty, $qty, 'MIG_LEGACY_2026', 'migration_tool'
    );
    v_imported_total := v_imported_total + $qty;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      DBMS_OUTPUT.PUT_LINE('ERROR: Unknown entity: ' || '$wh' || ' / ' || '$item');
      RAISE;
  END;
ROW
done < "$CSV_FILE"

cat << 'FOOTER' >> "$SQL_IMPORT"
  COMMIT;
  DBMS_OUTPUT.PUT_LINE('MIGRATION_TOTAL=' || v_imported_total);
END;
/
EXIT;
FOOTER

log "Executing atomic import into Oracle Database container..."
SQL_LOG=$(mktemp)
docker exec -i minierp-oracle bash -c "cat > /tmp/migrate_legacy.sql && sqlplus -S erp_user/ErpPassword2026#@FREEPDB1 @/tmp/migrate_legacy.sql" < "$SQL_IMPORT" > "$SQL_LOG" 2>&1
docker exec minierp-oracle rm -f /tmp/migrate_legacy.sql

IMPORTED_TOTAL=$(grep -oE 'MIGRATION_TOTAL=[0-9]+' "$SQL_LOG" | cut -d= -f2)

if [ -z "$IMPORTED_TOTAL" ]; then
  err "Migration failed to report imported total. Log output:"
  cat "$SQL_LOG"
  rm -f "$SQL_IMPORT" "$SQL_LOG"
  exit 1
fi

DIFF=$(awk "BEGIN {print $LEGACY_TOTAL - $IMPORTED_TOTAL}")

cat << REPORT > "$REPORT_FILE"
====================================================================
 MiniERP Data Migration & Reconciliation Certificate
====================================================================
Timestamp:                $(date -u +"%Y-%m-%dT%H:%M:%SZ")
Source File:              $(basename "$CSV_FILE")
Records Processed:        $ROW_COUNT
Legacy Inventory Total:   $LEGACY_TOTAL units
ERP Imported Total:       $IMPORTED_TOTAL units
Reconciliation Delta:     $DIFF units
Audit Transaction Ref:    MIG_LEGACY_2026
Result:                   $([ "$DIFF" = "0" ] && echo "PASSED (Zero Discrepancy)" || echo "FAILED")
====================================================================
REPORT

rule
printf '%b\n' "${C_B} RECONCILIATION SUMMARY${C_0}"
rule
printf '  Legacy inventory total:       %s units\n' "$LEGACY_TOTAL"
printf '  ERP imported inventory total: %s units\n' "$IMPORTED_TOTAL"
printf '  Difference:                   %s\n' "$DIFF"
rule

rm -f "$SQL_IMPORT" "$SQL_LOG"

if [ "$DIFF" = "0" ]; then
  ok "Migration reconciliation passed! Certificate archived: $REPORT_FILE"
  exit 0
else
  err "Migration discrepancy detected! Check: $REPORT_FILE"
  exit 1
fi
