#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/test-incident.sh
# PURPOSE: run sql/04_incident_scenarios.sql and prove the ERP support story:
#            1. complete_production_order(PO001) fails with ORA-20007
#            2. the failure is still visible in ERROR_LOG (autonomous tx)
#            3. receive_purchase_order(PO_PUR_901) fixes the shortage
#            4. PO001 completes and becomes COMPLETED, FG stock +50
#          The script is idempotent: the SQL file resets its own rows.
# USAGE:   bash scripts/test-incident.sh
# EXIT:    0 when every assertion in the SQL script reported PASS
# ============================================================================
set -uo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

rule
printf '%b\n' "${C_B} ERP incident scenario: production order PO001${C_0}"
rule

db_probe || die "cannot log in as $APP_USER@$PDB - run: bash scripts/start-db.sh"

LOG="$(mktemp)"
log "executing sql/04_incident_scenarios.sql"
run_sql "$SQL_DIR/04_incident_scenarios.sql" "$LOG" || true

# --------------------------------------------------------------- assertions
PASS_CNT=0; FAIL_CNT=0
report() {
  local name="$1" ok="$2"
  if [ "$ok" = "1" ]; then
    PASS_CNT=$((PASS_CNT+1)); printf '%b\n' "  ${C_G}[PASS]${C_0} $name"
  else
    FAIL_CNT=$((FAIL_CNT+1)); printf '%b\n' "  ${C_R}[FAIL]${C_0} $name"
  fi
}

has() { grep -qE "$1" "$LOG"; }

printf '%b\n' "${C_B}-- expected business error${C_0}"
has 'ORA-20007' && report "complete_production_order raised ORA-20007 (material shortage)" 1 \
  || report "complete_production_order raised ORA-20007 (material shortage)" 0
has 'S2_PO001_raises_ORA_minus_20007 -> PASS' \
  && report "SQLCODE captured inside the PL/SQL handler equals -20007" 1 \
  || report "SQLCODE captured inside the PL/SQL handler equals -20007" 0

printf '%b\n' "${C_B}-- autonomous transaction (PRAGMA AUTONOMOUS_TRANSACTION)${C_0}"
has 'A1_autonomous_error_log_written_for_PO001 -> PASS' \
  && report "ERROR_LOG row survived the ROLLBACK of the failed transaction" 1 \
  || report "ERROR_LOG row survived the ROLLBACK of the failed transaction" 0
has 'A2_ora_minus_20007_recorded_in_error_log -> PASS' \
  && report "the ORA-20007 code itself was persisted in ERROR_LOG" 1 \
  || report "the ORA-20007 code itself was persisted in ERROR_LOG" 0
has 'A3_stock_ledger_reconciles_after_failed_attempt -> PASS' \
  && report "STOCK balances still reconcile with INVENTORY_TRANSACTION (atomicity)" 1 \
  || report "STOCK balances still reconcile with INVENTORY_TRANSACTION (atomicity)" 0

printf '%b\n' "${C_B}-- remediation through the standard ERP procedure${C_0}"
has 'Step 5 SUCCESS' && report "receive_purchase_order(PO_PUR_901) received 100 pairs" 1 \
  || report "receive_purchase_order(PO_PUR_901) received 100 pairs" 0
has 'B_purchase_order_received_into_wh_raw -> PASS' \
  && report "MAT_RUBBER_01 availability raised to 140 in WH_RAW" 1 \
  || report "MAT_RUBBER_01 availability raised to 140 in WH_RAW" 0

printf '%b\n' "${C_B}-- successful re-run${C_0}"
has 'Step 6 SUCCESS' && report "complete_production_order(PO001) succeeded on retry" 1 \
  || report "complete_production_order(PO001) succeeded on retry" 0
has 'C1_PO001_status_is_COMPLETED -> PASS' && report "PO001 STATUS = COMPLETED" 1 \
  || report "PO001 STATUS = COMPLETED" 0
has 'C2_PO001_qty_done_is_50 -> PASS'   && report "PO001 QTY_DONE = 50" 1 \
  || report "PO001 QTY_DONE = 50" 0
has 'C3_finished_goods_stock_is_50 -> PASS' && report "finished goods stock increased by 50" 1 \
  || report "finished goods stock increased by 50" 0
has 'C4_rubber_balance_after_consumption_is_90 -> PASS' && report "50 pairs of soles consumed (140 -> 90)" 1 \
  || report "50 pairs of soles consumed (140 -> 90)" 0
has 'C5_audit_trail_5_consume_1_output -> PASS' \
  && report "audit trail: 5 MFG_CONSUME + 1 MFG_OUTPUT rows" 1 \
  || report "audit trail: 5 MFG_CONSUME + 1 MFG_OUTPUT rows" 0
has 'D_change_request_resolved -> PASS' && report "change request CR-2026-0901 closed as RESOLVED" 1 \
  || report "change request CR-2026-0901 closed as RESOLVED" 0

printf '%b\n' "${C_B}-- evidence from the database${C_0}"
grep -E '^\[EVIDENCE\]' "$LOG" | sed 's/^/       /'
grep -E '^>>> Step' "$LOG" | sed 's/^/       /'

rule
printf '%b\n' "  assertions passed: ${C_G}$PASS_CNT${C_0}   failed: ${C_R}$FAIL_CNT${C_0}"
if [ "$FAIL_CNT" -gt 0 ]; then
  printf '%b\n' "${C_R} INCIDENT SCENARIO FAILED${C_0} - full output:"
  tail -40 "$LOG" | sed 's/^/       /'
  archive_artifact "incident" "$LOG"
  rm -f "$LOG"; exit 1
fi
grep -q 'ALL_ASSERTIONS_PASSED' "$LOG" \
  && printf '%b\n' "${C_G} RESULT: INCIDENT SCENARIO - ALL ASSERTIONS PASSED${C_0}" \
  || printf '%b\n' "${C_Y} RESULT: assertions passed (marker line not found)${C_0}"
archive_artifact "incident" "$LOG" '^\[ASSERT\]|^\[RESULT\]|^\[EVIDENCE\]'
rm -f "$LOG"
rule
