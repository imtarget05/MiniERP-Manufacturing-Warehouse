#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/test-traceability.sh
# PURPOSE: Real Oracle/API acceptance for receive -> barcode -> label ->
#          put-away -> cross-warehouse move -> FEFO issue -> FG output ->
#          backward/forward trace -> reconciliation.
# USAGE:   MINIERP_QA_DISPOSABLE=1 DB_CONTAINER=<disposable> APP_USER=<user> \
#            bash scripts/test-traceability.sh <base_url>
#          ... --gate-check             print the authorised target, contact nothing
# REQUIRES: fresh SQL load (bash scripts/run-sql.sh), running Oracle + API.
#
#          No default base URL. scripts/lib.sh still defaults $BASE_URL to
#          http://localhost:5000, so the operator's value is captured BEFORE
#          lib.sh runs - otherwise "unset" and "explicitly set to localhost:5000"
#          are indistinguishable and the gate cannot refuse the second one.
#          The run writes to both ends (it calls run_sql and the API), so
#          scripts/qa-gate.sh is asked to authorise the database too.
# EXIT:    0 = every check passed, 1 = at least one failed,
#          78 = refused by scripts/qa-gate.sh (nothing was contacted)
# ============================================================================
set -uo pipefail
QA_HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Operator-supplied only, captured BEFORE lib.sh can invent a default: after
# sourcing it, DB_CONTAINER is always "minierp-oracle" and BASE_URL is always
# "http://localhost:5000", so the gate would be unable to tell an explicit
# value from an inherited one.
BASE_URL_IN="${BASE_URL:-}"
DB_CONTAINER_IN="${DB_CONTAINER:-}"
APP_USER_IN="${APP_USER:-}"
QA_DB_VOLUME_IN="${QA_DB_VOLUME:-}"
QA_DB_PROJECT_IN="${QA_DB_PROJECT:-}"

source "$QA_HERE/lib.sh"
# shellcheck source=scripts/qa-gate.sh
source "$QA_HERE/qa-gate.sh"
# shellcheck source=scripts/portable-tmp.sh
source "$QA_HERE/portable-tmp.sh"

# One scratch directory per run, so every .sql file below keeps its suffix
# without asking mktemp for a non-trailing X run (BSD/macOS resolves such a
# template to a literal, colliding path - see scripts/portable-tmp.sh). Any
# literal file an older broken run left behind is swept first, so a stale file
# can never be picked up as a snapshot or block this run.
TRACE_TMPDIR="$(portable_tmpdir trace-recon)" \
  || die "could not create a scratch directory for this traceability run"
portable_tmp_sweep_legacy >&2
trap 'rm -rf "$TRACE_TMPDIR"' EXIT

qa_gate_parse_args "test-traceability.sh" "$@"
qa_gate "test-traceability.sh" "$QA_BASE_URL" 1 1 "$QA_GATE_MODE"

BASE_URL="$QA_BASE_URL"
BASE_URL_DISPLAY="$(qa_gate_sanitize "$BASE_URL")"

RUN_ID="E2E$(date +%H%M%S)${RANDOM}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-Admin@123}"
EVIDENCE_DIR="$REPO_ROOT/artifacts/factory-upgrade/final/evidence"
PASS=0
FAIL=0
FAILED_CHECKS=""
RESP_CODE="000"
RESP_BODY=""
TOKEN=""

PASS_ACTIVE="RM-ACTIVE-$RUN_ID"
PASS_BLOCKED="RM-BLOCKED-$RUN_ID"
PASS_EXPIRED="RM-EXPIRED-$RUN_ID"
FG_LOT="FG-$RUN_ID"
PO_NO="PUR-$RUN_ID"
MFG_NO="MO-$RUN_ID"

mkdir -p "$EVIDENCE_DIR"
rule
printf '%b\n' "${C_B}Traceability E2E acceptance${C_0}  run=$RUN_ID"
printf '%b\n' "API=$BASE_URL_DISPLAY  evidence=$EVIDENCE_DIR"
rule

pass() { PASS=$((PASS + 1)); printf '%b\n' "  ${C_G}[PASS]${C_0} $*"; }
fail() { FAIL=$((FAIL + 1)); FAILED_CHECKS="$FAILED_CHECKS
    - $*"; printf '%b\n' "  ${C_R}[FAIL]${C_0} $*" >&2; }
check_eq() { [ "$2" = "$3" ] && pass "$1 ($3)" || fail "$1: expected '$2', got '$3'"; }
check_json() {
  local name="$1" expr="$2"
  if printf '%s' "$RESP_BODY" | python3 -c "import json,sys; d=json.load(sys.stdin); assert $expr" 2>/dev/null; then
    pass "$name"
  else
    fail "$name: $expr; body=${RESP_BODY:0:240}"
  fi
}
json_value() {
  local path="$1"
  printf '%s' "$RESP_BODY" | python3 -c 'import json,sys
p=sys.argv[1].split(".")
d=json.load(sys.stdin)
for k in p:
 d=d[int(k)] if isinstance(d,list) else d[k]
print("" if d is None else d)' "$path" 2>/dev/null
}
request() {
  local method="$1" path="$2" body="${3:-}" tmp
  tmp="$(mktemp)"
  local args=(-sS -o "$tmp" -w '%{http_code}' --max-time 30
    -X "$method" "$BASE_URL$path" -H 'Content-Type: application/json')
  [ -n "$TOKEN" ] && args+=(-H "Authorization: Bearer $TOKEN")
  [ -n "$body" ] && args+=(-d "$body")
  RESP_CODE="$(curl "${args[@]}" 2>/dev/null || echo 000)"
  RESP_BODY="$(cat "$tmp")"; rm -f "$tmp"
}
expect() {
  local name="$1" code="$2"
  [ "$RESP_CODE" = "$code" ] && pass "$name -> HTTP $code" || fail "$name: expected HTTP $code, got $RESP_CODE; body=${RESP_BODY:0:240}"
}
sql_eval() {
  local sql="$1" keep="${2:-}" out value
  out="$(mktemp)"
  { printf '%s\n' "SET HEADING OFF FEEDBACK OFF PAGESIZE 0 LINESIZE 240" "$sql" "EXIT;"; } > /tmp/trace_${$}.sql
  run_sql "/tmp/trace_${$}.sql" "$out" || { rm -f "$out" /tmp/trace_${$}.sql; return 1; }
  value="$(grep -oE '[A-Z_]+=[-0-9.]+' "$out" | tail -1 | cut -d= -f2)"
  if [ "$keep" = "KEEP" ]; then cp "$out" "$EVIDENCE_DIR/traceability-sql-verification.txt"; fi
  rm -f "$out" /tmp/trace_${$}.sql
  printf '%s' "$value"
}
save_response() { printf '%s' "$RESP_BODY" > "$EVIDENCE_DIR/$1"; }
now_iso() { python3 -c 'import datetime,sys; print((datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(days=int(sys.argv[1]))).isoformat().replace("+00:00","Z"))' "$1"; }
MFG_DATE="$(now_iso -30)"
ACTIVE_EXPIRY="$(now_iso 30)"
BLOCKED_EXPIRY="$(now_iso 60)"
EXPIRED_EXPIRY="$(now_iso -30)"

# ------------------------------------------------------------------ 0. auth
rule; printf '%b\n' "${C_B}0. Authentication & readiness${C_0}"; rule
TOKEN=""
request POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$ADMIN_PASSWORD\"}"
expect "admin login" 200
TOKEN="$(json_value accessToken)"
[ -n "$TOKEN" ] && pass "admin bearer token issued" || fail "admin bearer token missing"
request GET /api/health/ready
expect "database readiness" 200
check_json "readiness status is READY" "d['status']=='READY'"

# B-04: the reconciliation invariant at the end of this run must be scoped to
# what this run is responsible for. ERP_TRACEABILITY moves STOCK and LOT_STOCK
# together, so the run's own invariant is (a) the per-pair DELTA of the two
# ledgers agrees, and (b) each lot this run created reconciles absolutely
# against what the run received / consumed. A global "every LOT-tracked item
# reconciles" check is not a property of this run: ERP_OPERATIONS
# (receive_purchase_order, create_stock_in, MFG_CONSUME) never writes
# LOT_STOCK, so a drift created by test-incident.sh / test-api.sh / the xUnit
# suite used to decide this run's verdict (60/61 in the plan order).
# Snapshot the run's own (warehouse, item) pairs before anything mutates them.
RECON_SQL="SELECT 'SNAP=' || W.CODE || ':' || I.CODE || ':' ||
  NVL((SELECT TO_CHAR(S.QTY) FROM STOCK S
        WHERE S.WAREHOUSE_ID=W.ID AND S.ITEM_ID=I.ID), 'absent') || ':' ||
  NVL((SELECT TO_CHAR(SUM(LS.QTY_ON_HAND)) FROM LOT_STOCK LS
        JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID
       WHERE LS.WAREHOUSE_ID=W.ID AND L.ITEM_ID=I.ID), '0')
  FROM WAREHOUSE W CROSS JOIN ITEM I
 WHERE W.CODE IN ('WH_RAW','WH_WIP','WH_FG')
   AND I.CODE IN ('MAT_RUBBER_01','FG_RUNNER_PRO_42')
 ORDER BY W.CODE, I.CODE;"
RECON_PAIRS=6   # 3 warehouses (WH_RAW/WH_WIP/WH_FG) x 2 items (rubber, finished good)
# Raw output, not a scalar: sql_eval would collapse these rows to one value and
# the comparison would silently compare nothing.
recon_snapshot() {
  local out="$1" file
  file="$(portable_tmp_path "$TRACE_TMPDIR" "recon-$RUN_ID-$RANDOM.sql")"
  { printf '%s\n' "SET HEADING OFF FEEDBACK OFF PAGESIZE 0 LINESIZE 240" "$RECON_SQL" "EXIT;"; } > "$file"
  if ! run_sql "$file" "$out"; then
    fail "reconciliation snapshot could not be read (see $out)"
  fi
  rm -f "$file"
}
recon_delta() {  # recon_delta <before snapshot> <after snapshot> -> MISMATCH=<n>
  python3 - "$1" "$2" <<'PYEOF'
import sys

def parse(text):
    out = {}
    for line in text.splitlines():
        if not line.startswith("SNAP="):
            continue
        parts = line[len("SNAP="):].strip().split(":")
        if len(parts) < 4:
            continue
        wh, item, stock, lot = parts[0], parts[1], parts[2], ":".join(parts[3:])
        out[(wh, item)] = (0.0 if stock == "absent" else float(stock), float(lot))
    return out

before, after = parse(open(sys.argv[1]).read()), parse(open(sys.argv[2]).read())
if not before or not after:
    print("MISMATCH=unreadable")
    print("MISMATCH_PAIR=empty snapshot (before=%d pairs, after=%d pairs)" % (len(before), len(after)))
    sys.exit(0)
bad = []
for key in sorted(set(before) | set(after)):
    b_stock, b_lot = before.get(key, (0.0, 0.0))
    a_stock, a_lot = after.get(key, (0.0, 0.0))
    d_stock, d_lot = a_stock - b_stock, a_lot - b_lot
    if abs(d_stock - d_lot) > 0.0001:
        bad.append("%s/%s stock_delta=%+g lot_delta=%+g" % (key[0], key[1], d_stock, d_lot))
print("MISMATCH=" + str(len(bad)))
for line in bad:
    print("MISMATCH_PAIR=" + line)
PYEOF
}
RECON_BEFORE_FILE="$(mktemp /tmp/trace-recon-before-XXXX)"
recon_snapshot "$RECON_BEFORE_FILE"
RECON_BEFORE_PAIRS="$(grep -c "^SNAP=" "$RECON_BEFORE_FILE" || true)"
check_eq "reconciliation snapshot covers the run's scoped stock/lot pairs" "$RECON_PAIRS" "$RECON_BEFORE_PAIRS"

# ------------------------------------------------- 1. purchase + receive lots
rule; printf '%b\n' "${C_B}1. Purchase order & lot receiving${C_0}"; rule
request POST /api/procurement/purchase-order "{\"purchaseOrderNo\":\"$PO_NO\",\"itemCode\":\"MAT_RUBBER_01\",\"quantity\":30,\"warehouseCode\":\"WH_RAW\"}"
expect "create traceability purchase order" 200
save_response "01-purchase-order.json"

receive_lot() {
  local lot="$1" qty="$2" expiry="$3" key="$4" file="$5"
  request POST "/api/warehouse/receipts/$PO_NO/receive" "{\"itemCode\":\"MAT_RUBBER_01\",\"qty\":$qty,\"lotCode\":\"$lot\",\"supplierLotNo\":\"SUP-$RUN_ID\",\"mfgDate\":\"$MFG_DATE\",\"expiryDate\":\"$expiry\",\"receivingLocationCode\":\"RCV-01\",\"idempotencyKey\":\"$key\"}"
  expect "receive $lot" 200
  save_response "$file"
}
receive_lot "$PASS_ACTIVE" 10 "$ACTIVE_EXPIRY" "recv-active-$RUN_ID" "02-receive-active.json"
check_json "active lot receive is not replay" "d['replayed'] is False"
receive_lot "$PASS_ACTIVE" 10 "$ACTIVE_EXPIRY" "recv-active-$RUN_ID" "03-receive-active-replay.json"
check_json "duplicate receive reports replay" "d['replayed'] is True"
LOT_QTY="$(sql_eval "SELECT 'LOT_QTY=' || SUM(QTY_ON_HAND) FROM LOT_STOCK LS JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID WHERE L.LOT_CODE='$PASS_ACTIVE';")"
check_eq "duplicate receive did not duplicate lot stock" "10" "$LOT_QTY"

receive_lot "$PASS_BLOCKED" 5 "$BLOCKED_EXPIRY" "recv-blocked-$RUN_ID" "04-receive-blocked.json"
request POST "/api/warehouse/lots/$PASS_BLOCKED/hold"
expect "set blocked lot on HOLD" 200
check_json "blocked lot status is HOLD" "d['status']=='HOLD'"
receive_lot "$PASS_EXPIRED" 2 "$EXPIRED_EXPIRY" "recv-expired-$RUN_ID" "05-receive-expired.json"

# Same key + different payload must be conflict, not replay.
request POST "/api/warehouse/receipts/$PO_NO/receive" "{\"itemCode\":\"MAT_RUBBER_01\",\"qty\":9,\"lotCode\":\"$PASS_ACTIVE\",\"supplierLotNo\":\"SUP-$RUN_ID\",\"mfgDate\":\"$MFG_DATE\",\"expiryDate\":\"$ACTIVE_EXPIRY\",\"receivingLocationCode\":\"RCV-01\",\"idempotencyKey\":\"recv-active-$RUN_ID\"}"
expect "idempotency key reused with different payload" 409
check_json "payload conflict contract" "d['businessCode']=='ERR_IDEMPOTENCY_CONFLICT'"

# ------------------------------------------------------- 2. barcode + labels
rule; printf '%b\n' "${C_B}2. Barcode resolution & label reprint${C_0}"; rule
request GET "/api/barcodes/resolve/LOT:$PASS_ACTIVE"
expect "resolve lot barcode" 200
check_json "barcode resolves expected lot" "d['entityKey']=='$PASS_ACTIVE' and d['status']=='ACTIVE'"
save_response "06-barcode-resolution.json"

STOCK_BEFORE="$(sql_eval "SELECT 'STOCK=' || QTY FROM STOCK S JOIN ITEM I ON I.ID=S.ITEM_ID JOIN WAREHOUSE W ON W.ID=S.WAREHOUSE_ID WHERE W.CODE='WH_RAW' AND I.CODE='MAT_RUBBER_01';")"
for format in HTML ZPL HTML; do
  request POST /api/labels "{\"entityType\":\"LOT\",\"entityKey\":\"$PASS_ACTIVE\",\"labelType\":\"RAW_MATERIAL\",\"copies\":1,\"format\":\"$format\"}"
  expect "create/reprint label $format" 200
  save_response "07-label-$format-$(date +%s%N).json"
done
STOCK_AFTER="$(sql_eval "SELECT 'STOCK=' || QTY FROM STOCK S JOIN ITEM I ON I.ID=S.ITEM_ID JOIN WAREHOUSE W ON W.ID=S.WAREHOUSE_ID WHERE W.CODE='WH_RAW' AND I.CODE='MAT_RUBBER_01';")"
check_eq "label reprints did not change aggregate stock" "$STOCK_BEFORE" "$STOCK_AFTER"
LABEL_JOBS="$(sql_eval "SELECT 'LABELS=' || COUNT(*) FROM LABEL_PRINT_JOB WHERE ENTITY_TYPE='LOT' AND ENTITY_KEY='$PASS_ACTIVE';")"
check_eq "three print/reprint jobs audited" "3" "$LABEL_JOBS"

# ------------------------------------------------- 3. put-away and movement
rule; printf '%b\n' "${C_B}3. Put-away & cross-warehouse movement${C_0}"; rule
request POST /api/warehouse/putaway "{\"lotCode\":\"$PASS_ACTIVE\",\"fromLocationCode\":\"RCV-01\",\"toLocationCode\":\"A-01-01\",\"qty\":10,\"idempotencyKey\":\"putaway-$RUN_ID\"}"
expect "put-away lot to bin" 200
check_json "put-away is not replay" "d['replayed'] is False"
request POST /api/warehouse/putaway "{\"lotCode\":\"$PASS_ACTIVE\",\"fromLocationCode\":\"RCV-01\",\"toLocationCode\":\"A-01-01\",\"qty\":10,\"idempotencyKey\":\"putaway-$RUN_ID\"}"
expect "duplicate put-away" 200
check_json "duplicate put-away reports replay" "d['replayed'] is True"

TOTAL_BEFORE="$(sql_eval "SELECT 'TOTAL=' || SUM(QTY_ON_HAND) FROM LOT_STOCK LS JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID WHERE L.LOT_CODE='$PASS_ACTIVE';")"
request POST /api/warehouse/move "{\"lotCode\":\"$PASS_ACTIVE\",\"fromWarehouseCode\":\"WH_RAW\",\"fromLocationCode\":\"A-01-01\",\"toWarehouseCode\":\"WH_WIP\",\"toLocationCode\":\"LINE-01\",\"qty\":2,\"idempotencyKey\":\"move-$RUN_ID\"}"
expect "move 2 units across warehouse" 200
save_response "08-cross-warehouse-move.json"
request POST /api/warehouse/move "{\"lotCode\":\"$PASS_ACTIVE\",\"fromWarehouseCode\":\"WH_RAW\",\"fromLocationCode\":\"A-01-01\",\"toWarehouseCode\":\"WH_WIP\",\"toLocationCode\":\"LINE-01\",\"qty\":2,\"idempotencyKey\":\"move-$RUN_ID\"}"
expect "duplicate cross-warehouse move" 200
check_json "duplicate move reports replay" "d['replayed'] is True"
TOTAL_AFTER="$(sql_eval "SELECT 'TOTAL=' || SUM(QTY_ON_HAND) FROM LOT_STOCK LS JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID WHERE L.LOT_CODE='$PASS_ACTIVE';")"
check_eq "move preserved total lot quantity" "$TOTAL_BEFORE" "$TOTAL_AFTER"
WIP_QTY="$(sql_eval "SELECT 'WIP=' || SUM(LS.QTY_ON_HAND) FROM LOT_STOCK LS JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID JOIN WAREHOUSE W ON W.ID=LS.WAREHOUSE_ID WHERE L.LOT_CODE='$PASS_ACTIVE' AND W.CODE='WH_WIP';")"
check_eq "cross-warehouse destination has 2 units" "2" "$WIP_QTY"
request POST /api/warehouse/move "{\"lotCode\":\"$PASS_ACTIVE\",\"fromWarehouseCode\":\"WH_WIP\",\"fromLocationCode\":\"LINE-01\",\"toWarehouseCode\":\"WH_RAW\",\"toLocationCode\":\"A-01-01\",\"qty\":2,\"idempotencyKey\":\"move-back-$RUN_ID\"}"
expect "move units back to input warehouse" 200

# ------------------------------------------------ 4. FEFO and issue lots
rule; printf '%b\n' "${C_B}4. FEFO allocation & exact lot issue${C_0}"; rule
request POST /api/manufacturing/production-order "{\"productionOrderNo\":\"$MFG_NO\",\"finishedGoodCode\":\"FG_RUNNER_PRO_42\",\"plannedQuantity\":1,\"warehouseCode\":\"WH_RAW\"}"
expect "create traceable production order" 200
save_response "09-production-order.json"
request GET "/api/manufacturing/production-order/$MFG_NO/allocate-lots"
expect "FEFO allocation preview" 200
save_response "10-fefo-allocation.json"
check_json "allocation is complete" "all(not x['isShort'] for x in d['lines'])"
check_json "FEFO chooses earliest active expiry" "next(x['suggestedLots'][0]['lotCode'] for x in d['lines'] if x['itemCode']=='MAT_RUBBER_01')=='$PASS_ACTIVE'"
check_json "held and expired lots are excluded" "all(s['lotCode'] not in ('$PASS_BLOCKED','$PASS_EXPIRED') for x in d['lines'] for s in x.get('suggestedLots',[]))"

ISSUE_BEFORE="$(sql_eval "SELECT 'ISSUE=' || COUNT(*) FROM PRODUCTION_LOT_CONSUMPTION C JOIN PRODUCTION_ORDER O ON O.ID=C.PRODUCTION_ORDER_ID WHERE O.PO_NO='$MFG_NO';")"
request POST "/api/manufacturing/production-order/$MFG_NO/issue-lots" "{\"idempotencyKey\":\"issue-$RUN_ID\"}"
expect "issue exact material lots" 200
check_json "issue is not replay" "d['replayed'] is False"
save_response "11-issue-lots.json"
request POST "/api/manufacturing/production-order/$MFG_NO/issue-lots" "{\"idempotencyKey\":\"issue-$RUN_ID\"}"
expect "duplicate issue" 200
check_json "duplicate issue reports replay" "d['replayed'] is True"
ISSUE_AFTER="$(sql_eval "SELECT 'ISSUE=' || COUNT(*) FROM PRODUCTION_LOT_CONSUMPTION C JOIN PRODUCTION_ORDER O ON O.ID=C.PRODUCTION_ORDER_ID WHERE O.PO_NO='$MFG_NO';")"
check_eq "no genealogy rows before issue" "0" "$ISSUE_BEFORE"
check_eq "all five BOM material lots consumed" "5" "$ISSUE_AFTER"
CONSUMED_BLOCKED="$(sql_eval "SELECT 'BLOCKED=' || COUNT(*) FROM PRODUCTION_LOT_CONSUMPTION C JOIN PRODUCTION_ORDER O ON O.ID=C.PRODUCTION_ORDER_ID JOIN INVENTORY_LOT L ON L.LOT_ID=C.INPUT_LOT_ID WHERE O.PO_NO='$MFG_NO' AND L.LOT_CODE='$PASS_BLOCKED';")"
CONSUMED_EXPIRED="$(sql_eval "SELECT 'EXPIRED=' || COUNT(*) FROM PRODUCTION_LOT_CONSUMPTION C JOIN PRODUCTION_ORDER O ON O.ID=C.PRODUCTION_ORDER_ID JOIN INVENTORY_LOT L ON L.LOT_ID=C.INPUT_LOT_ID WHERE O.PO_NO='$MFG_NO' AND L.LOT_CODE='$PASS_EXPIRED';")"
check_eq "held lot was not consumed" "0" "$CONSUMED_BLOCKED"
check_eq "expired lot was not consumed" "0" "$CONSUMED_EXPIRED"

# ----------------------------------------------------- 5. FG output & trace
rule; printf '%b\n' "${C_B}5. FG output, labels & genealogy${C_0}"; rule
request POST "/api/manufacturing/production-order/$MFG_NO/complete-traceable" "{\"fgLot\":\"$FG_LOT\",\"qty\":1,\"locationCode\":\"FG-01-01\",\"outputWarehouseCode\":\"WH_FG\",\"tolerance\":0,\"idempotencyKey\":\"complete-$RUN_ID\"}"
expect "traceable completion creates FG lot" 200
check_json "completion is not replay" "d['replayed'] is False and d['outputWarehouseCode']=='WH_FG'"
check_json "completion returns barcode and label link" "d['barcode']=='LOT:$FG_LOT' and '/api/labels' in d['labelEndpoint']"
save_response "12-complete-traceable.json"
request POST "/api/manufacturing/production-order/$MFG_NO/complete-traceable" "{\"fgLot\":\"$FG_LOT\",\"qty\":1,\"locationCode\":\"FG-01-01\",\"outputWarehouseCode\":\"WH_FG\",\"tolerance\":0,\"idempotencyKey\":\"complete-$RUN_ID\"}"
expect "duplicate traceable completion" 200
check_json "duplicate completion reports replay" "d['replayed'] is True"
request GET "/api/manufacturing/production-order/$MFG_NO"
check_json "production order is COMPLETED" "d['status']=='COMPLETED' and float(d['doneQuantity'])==1"

request GET "/api/trace/$FG_LOT?direction=backward"
expect "backward trace from FG lot" 200
check_json "backward trace links production order" "any(x['kind']=='PRODUCTION_ORDER' and x['key']=='$MFG_NO' for x in d['trace']['children'])"
check_json "backward trace includes exact source lot" "'$PASS_ACTIVE' in json.dumps(d)"
save_response "13-backward-trace.json"
request GET "/api/trace/$PASS_ACTIVE?direction=forward"
expect "forward trace from raw lot" 200
check_json "forward trace links production order and FG lot" "'$MFG_NO' in json.dumps(d) and '$FG_LOT' in json.dumps(d)"
save_response "14-forward-trace.json"
request POST /api/labels "{\"entityType\":\"LOT\",\"entityKey\":\"$FG_LOT\",\"labelType\":\"FINISHED_GOOD\",\"copies\":1,\"format\":\"ZPL\"}"
expect "print finished-goods label" 200
save_response "15-finished-goods-label.json"

# -------------------------------------------------------- 6. reconciliation
rule; printf '%b\n' "${C_B}6. Accounting / lot reconciliation & audit${C_0}"; rule
# B-04 self-test: TRACEABILITY_RECON_FAULT injects an IN-SCOPE drift after the
# run finished moving stock, and the reconciliation below must fail. Without it
# the scoped check could be vacuously green. Off by default.
#   stock_drift -> the per-pair delta check must fail (STOCK moved, LOT_STOCK did not)
#   lot_drift   -> the per-lot absolute check must fail (LOT_STOCK corrupted)
fault_apply() {
  local name="$1" statement="$2"
  local file out
  file="$(portable_tmp_path "$TRACE_TMPDIR" "fault-$name.sql")"
  out="$(portable_tmp_path "$TRACE_TMPDIR" "fault-$name.log")"
  { printf '%s\n' "SET HEADING OFF FEEDBACK OFF PAGESIZE 0 LINESIZE 240" "$statement" "EXIT;"; } > "$file"
  if run_sql "$file" "$out"; then
    cp "$out" "$EVIDENCE_DIR/traceability-fault-$name.log"
    printf '%b\n' "${C_R}[FAULT INJECTION] $name applied${C_0}" >&2
  else
    cp "$out" "$EVIDENCE_DIR/traceability-fault-$name.log"
    fail "fault injection '$name' could not be applied (see traceability-fault-$name.log)"
  fi
  rm -f "$file" "$out"
}
case "${TRACEABILITY_RECON_FAULT:-}" in
  stock_drift)
    printf '%b\n' "${C_R}[FAULT INJECTION] stock_drift: adding 1 to WH_RAW/MAT_RUBBER_01 STOCK only${C_0}" >&2
    fault_apply "stock_drift" "UPDATE STOCK S SET S.QTY = S.QTY + 1, S.UPDATED_AT = SYSDATE
     WHERE S.ITEM_ID = (SELECT I.ID FROM ITEM I WHERE I.CODE='MAT_RUBBER_01')
       AND S.WAREHOUSE_ID = (SELECT W.ID FROM WAREHOUSE W WHERE W.CODE='WH_RAW');
    COMMIT;"
    ;;
  lot_drift)
    printf '%b\n' "${C_R}[FAULT INJECTION] lot_drift: subtracting 1 from the run lot in LOT_STOCK${C_0}" >&2
    # CK_LS_RSV_LE keeps QTY_ON_HAND >= QTY_RESERVED, so only a free row can drift.
    fault_apply "lot_drift" "UPDATE LOT_STOCK LS SET LS.QTY_ON_HAND = LS.QTY_ON_HAND - 1
     WHERE LS.LOT_ID = (SELECT L.LOT_ID FROM INVENTORY_LOT L WHERE L.LOT_CODE='$PASS_ACTIVE')
       AND LS.WAREHOUSE_ID = (SELECT W.ID FROM WAREHOUSE W WHERE W.CODE='WH_RAW')
       AND NVL(LS.QTY_RESERVED, 0) = 0 AND LS.QTY_ON_HAND > 0;
    COMMIT;"
    ;;
  "") : ;;
  *) fail "unknown TRACEABILITY_RECON_FAULT='$TRACEABILITY_RECON_FAULT' (expected stock_drift or lot_drift)" ;;
esac
RECON_AFTER_FILE="$(mktemp /tmp/trace-recon-after-XXXX)"
recon_snapshot "$RECON_AFTER_FILE"
RECON_AFTER_PAIRS="$(grep -c "^SNAP=" "$RECON_AFTER_FILE" || true)"
check_eq "reconciliation snapshot is still readable after the run" "$RECON_PAIRS" "$RECON_AFTER_PAIRS"
RECON_RESULT="$(recon_delta "$RECON_BEFORE_FILE" "$RECON_AFTER_FILE")"
MISMATCH="$(printf '%s\n' "$RECON_RESULT" | sed -n 's/^MISMATCH=//p')"
check_eq "run's stock and lot movements reconcile (WH_RAW/WH_WIP rubber, WH_FG finished good)" \
  "0" "$MISMATCH"
printf '%s\n' "$RECON_RESULT" | grep '^MISMATCH_PAIR=' | while read -r _ detail; do
  fail "lot reconciliation drift in scope: ${detail#MISMATCH_PAIR=}"
done

# Absolute reconciliation for the lots this run created: received quantity minus
# what the run consumed. A pre-existing drift on some other lot is not this
# run's business, but a corruption inside this run's own lots still fails.
LOT_SCOPE_MISMATCH=0
for spec in "$PASS_ACTIVE:10" "$PASS_BLOCKED:5" "$PASS_EXPIRED:2"; do
  lot_code="${spec%%:*}"; received="${spec##*:}"
  # Consumed by the run's own production order. PRODUCTION_LOT_OUTPUT holds the
  # FG side (QTY_PRODUCED against OUTPUT_LOT_ID), so it is not part of a raw
  # lot's balance.
  consumed="$(sql_eval "SELECT 'CONSUMED=' || COALESCE((SELECT SUM(C.QTY_CONSUMED)
      FROM PRODUCTION_LOT_CONSUMPTION C JOIN INVENTORY_LOT L ON L.LOT_ID=C.INPUT_LOT_ID
     WHERE L.LOT_CODE='$lot_code'), 0);")"
  consumed="${consumed#CONSUMED=}"
  case "$consumed" in
    ''|*[!0-9.-]*) consumed=0 ;;
  esac
  held="$(sql_eval "SELECT 'HELD=' || COALESCE(SUM(QTY_ON_HAND),0) FROM LOT_STOCK LS
      JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID WHERE L.LOT_CODE='$lot_code';")"
  held="${held#HELD=}"
  expected="$(python3 -c "print($received - $consumed)")"
  if [ "$held" = "$expected" ]; then
    pass "run lot $lot_code reconciles: received $received - consumed $consumed = held $held"
  else
    fail "run lot $lot_code does not reconcile: received $received - consumed $consumed = $expected, lot stock says $held"
    LOT_SCOPE_MISMATCH=$((LOT_SCOPE_MISMATCH + 1))
  fi
done
FG_QTY="$(sql_eval "SELECT 'FG=' || SUM(QTY_ON_HAND) FROM LOT_STOCK LS JOIN INVENTORY_LOT L ON L.LOT_ID=LS.LOT_ID JOIN WAREHOUSE W ON W.ID=LS.WAREHOUSE_ID WHERE L.LOT_CODE='$FG_LOT' AND W.CODE='WH_FG';")"
check_eq "FG lot is stored in WH_FG" "1" "$FG_QTY"
AUDIT_COUNT="$(sql_eval "SELECT 'AUDIT=' || COUNT(*) FROM APP_AUDIT_EVENT WHERE ACTOR='admin' AND CORRELATION_ID IS NOT NULL;")"
if [ "${AUDIT_COUNT:-0}" -ge 1 ] 2>/dev/null; then
  pass "sensitive operations produced audit events ($AUDIT_COUNT)"
else
  fail "no admin audit events with correlation id"
fi

TOKEN=""
if [ "$FAIL" -eq 0 ]; then
  printf '%s\n' "${C_G}TRACEABILITY E2E PASSED: $PASS checks${C_0}"
  printf '%s\n' "runId=$RUN_ID po=$PO_NO mo=$MFG_NO activeLot=$PASS_ACTIVE fgLot=$FG_LOT" \
    > "$EVIDENCE_DIR/traceability-run-summary.txt"
  exit 0
fi
printf '%b\n' "${C_R}TRACEABILITY E2E FAILED: $FAIL of $((PASS + FAIL)) checks${C_0}" >&2
printf '%b\n' "$FAILED_CHECKS" >&2
exit 1

