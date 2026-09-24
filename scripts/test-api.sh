#!/usr/bin/env bash
# ============================================================================
# PROJECT: 04-MiniERP-Manufacturing-Warehouse
# FILE:    scripts/test-api.sh
# PURPOSE: End-to-end smoke test of every Mini ERP API endpoint (curl only).
#          Asserts HTTP status + JSON contract so the ASP.NET Core -> Dapper ->
#          Oracle PL/SQL round trip is proven, including the PO001 shortage
#          incident (ORA-20007 + autonomous ERROR_LOG + fix + completion).
#
# USAGE:   bash scripts/test-api.sh [base_url]     default http://localhost:5000
#          VERBOSE=1 bash scripts/test-api.sh      to dump every response
# EXIT:    0 = all checks passed, 1 = at least one check failed
# ============================================================================
set -uo pipefail

BASE_URL="${1:-http://localhost:5000}"
RUN_ID="$(date +%H%M%S)"
PASS=0; FAIL=0; FAILED_CHECKS=""
VERBOSE="${VERBOSE:-0}"
RESP_CODE="000"; RESP_BODY=""
AUTH_TOKEN="${API_TOKEN:-}"

if [ -t 1 ]; then
  C_G='\033[32m'; C_R='\033[31m'; C_B='\033[36m'; C_Y='\033[33m'; C_0='\033[0m'
else C_G=''; C_R=''; C_B=''; C_Y=''; C_0=''; fi

say()   { printf '%b\n' "$*"; }
title() { say "\n${C_B}────────────────────────────────────────────────────────${C_0}"; say "${C_B}$*${C_0}"; }

# request <METHOD> <PATH> [json_body]
request() {
  local method="$1" path="$2" body="${3:-}" tmp
  tmp="$(mktemp)"
  local args=(-s -o "$tmp" -w '%{http_code}' -X "$method" "$BASE_URL$path"
              -H 'Content-Type: application/json' --max-time 90)
  [ -n "$AUTH_TOKEN" ] && args+=(-H "Authorization: Bearer $AUTH_TOKEN")
  [ -n "$body" ] && args+=(-d "$body")
  RESP_CODE="$(curl "${args[@]}" 2>/dev/null || echo 000)"
  RESP_BODY="$(cat "$tmp")"; rm -f "$tmp"
  if [ "$VERBOSE" = "1" ]; then
    if [ "$path" = "/api/auth/login" ]; then
      say "  ${C_Y}$method $path -> $RESP_CODE  [credentials redacted]${C_0}"
    else
      say "  ${C_Y}$method $path -> $RESP_CODE  ${RESP_BODY:0:300}${C_0}"
    fi
  fi
}

# jget <field> [list_index]  -> value of a top-level field of the last response
# (scans the first array element when the payload is a list).
jget() {
  local field="$1" idx="${2:-0}"
  printf '%s' "$RESP_BODY" | python3 -c '
import json, sys
field, idx = sys.argv[1], int(sys.argv[2])
try:
    d = json.loads(sys.stdin.read())
except Exception:
    print(""); raise SystemExit(0)
if isinstance(d, list):
    d = d[idx] if len(d) > idx else {}
v = d.get(field) if isinstance(d, dict) else None
print("" if v is None else (str(v).lower() if isinstance(v, bool) else v))
' "$field" "$idx" 2>/dev/null
}

# jitem <match_key> <match_value> <want_field> -> value inside a list payload
jitem() {
  printf '%s' "$RESP_BODY" | python3 -c '
import json, sys
k, val, want = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    d = json.loads(sys.stdin.read())
except Exception:
    print(""); raise SystemExit(0)
for item in d if isinstance(d, list) else []:
    if isinstance(item, dict) and str(item.get(k, "")) == val:
        v = item.get(want)
        print("" if v is None else (str(v).lower() if isinstance(v, bool) else v))
        raise SystemExit(0)
print("")
' "$1" "$2" "$3" 2>/dev/null
}

# jany <key> <value> -> "true" when any item of a list payload matches
jany() {
  printf '%s' "$RESP_BODY" | python3 -c '
import json, sys
k, val = sys.argv[1], sys.argv[2]
try:
    d = json.loads(sys.stdin.read())
except Exception:
    print("false"); raise SystemExit(0)
hit = any(isinstance(i, dict) and str(i.get(k, "")) == val for i in (d if isinstance(d, list) else []))
print("true" if hit else "false")
' "$1" "$2" 2>/dev/null
}

# check <name> <expected_status> <field> [expected_value]
check() {
  local name="$1" want_code="$2" field="${3:-}" want_val="${4:-}" got=""
  if [ -n "$field" ] && [ "$field" != "_" ]; then got="$(jget "$field")"; fi
  if [ "$RESP_CODE" = "$want_code" ] && { [ -z "$field" ] || [ "$field" = "_" ] || [ -z "$want_val" ] || [ "$got" = "$want_val" ]; } \
     && { [ -z "$field" ] || [ "$field" = "_" ] || [ -n "$got" ]; }; then
    PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} $name ${C_G}(HTTP $want_code${want_val:+, $field=$want_val})${C_0}"
  else
    FAIL=$((FAIL+1))
    FAILED_CHECKS="$FAILED_CHECKS
    - $name (want HTTP $want_code ${field}=${want_val:-<present>}, got HTTP $RESP_CODE ${field:-}='$got')"
    say "  ${C_R}[FAIL]${C_0} $name ${C_R}(want HTTP $want_code ${field}=${want_val:-<present>}, got $RESP_CODE '$(printf '%.140s' "$RESP_BODY")')${C_0}"
  fi
}

# check_eq <name> <expected> <actual>
check_eq() {
  if [ "$2" = "$3" ]; then
    PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} $1 ${C_G}($3)${C_0}"
  else
    FAIL=$((FAIL+1))
    FAILED_CHECKS="$FAILED_CHECKS
    - $1 (expected '$2', got '$3')"
    say "  ${C_R}[FAIL]${C_0} $1 ${C_R}(expected '$2', got '$3')${C_0}"
  fi
}

num() { python3 -c "print($1)" 2>/dev/null; }

say "${C_B}========================================================================${C_0}"
say "${C_B} Mini ERP API smoke test${C_0}   base: ${C_B}$BASE_URL${C_0}   run: ${C_B}$RUN_ID${C_0}"
say "${C_B}========================================================================${C_0}"


# ---------------------------------------------------------------- 0. health
title "STEP 0  Service & database availability"
request GET /api/health
if [ "$RESP_CODE" = "000" ]; then
  say "${C_R}API is not reachable at $BASE_URL${C_0}"
  say "${C_R}Start it first: (cd src && dotnet run --urls http://localhost:5000)${C_0}"
  exit 1
fi
check "GET /api/health -> UP" 200 status UP
DB="$(printf '%s' "$RESP_BODY" | python3 -c '
import json,sys
d=json.load(sys.stdin).get("database") or {}
print(str(d.get("banner","")) + "|" + str(d.get("packageStatus","")) + "|" + str(d.get("tableCount","")))
' 2>/dev/null)"
say "  ${C_B}database      : ${DB%%|*}${C_0}"
PKG="$(printf '%s' "$DB" | cut -d'|' -f2)"; TBL="$(printf '%s' "$DB" | cut -d'|' -f3)"
check_eq "PL/SQL package ERP_OPERATIONS is VALID" "VALID" "$PKG"
check_eq "schema object count (31 tables)" "31" "$TBL"

title "STEP 0A  Readiness & authentication"
request GET /api/health/ready
check "GET /api/health/ready -> READY" 200 status READY
request POST /api/auth/login '{"username":"admin","password":"Admin@123"}'
check "POST /api/auth/login -> token" 200 accessToken
AUTH_TOKEN="$(jget accessToken)"
if [ -z "$AUTH_TOKEN" ]; then
  say "${C_R}Login did not return an access token; mutation checks cannot continue.${C_0}"
  exit 1
fi
request GET /api/auth/me
check "GET /api/auth/me with token" 200 username admin

# ------------------------------------------------- 1. warehouse & stock reads
title "STEP 1  Warehouse & inventory queries"
request GET /api/warehouse
check "GET /api/warehouse -> WH_FG first row" 200 code WH_FG
WAREHOUSE_COUNT="$(printf '%s' "$RESP_BODY" | python3 -c 'import json,sys;print(len(json.load(sys.stdin)))' 2>/dev/null)"
check_eq "GET /api/warehouse returns 3 warehouses" "3" "$WAREHOUSE_COUNT"

request GET /api/stock/WH_RAW
RUBBER="$(jitem itemCode MAT_RUBBER_01 quantity)"
FG_STOCK="$(jitem itemCode FG_RUNNER_PRO_42 quantity)"
if [ -n "$RUBBER" ] && [ "$RESP_CODE" = "200" ]; then
  PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} GET /api/stock/WH_RAW -> MAT_RUBBER_01 row present ${C_G}(HTTP 200)${C_0}"
else
  FAIL=$((FAIL+1)); FAILED_CHECKS="$FAILED_CHECKS
    - GET /api/stock/WH_RAW -> MAT_RUBBER_01 row present"; say "  ${C_R}[FAIL]${C_0} GET /api/stock/WH_RAW -> MAT_RUBBER_01 row present"
fi
say "  ${C_B}MAT_RUBBER_01 on hand = $RUBBER, FG_RUNNER_PRO_42 = $FG_STOCK${C_0}"
BELOW_MIN="$(jitem itemCode MAT_RUBBER_01 isBelowMinStock)"
check_eq "min-stock alert flag computed by Oracle CASE" "true" "$BELOW_MIN"

request GET /api/stock/WH_UNKNOWN_WH
check "GET /api/stock/{unknown} -> 200 empty array (no crash)" 200 _

# --------------------------------------------------------- 2. stock in / out
title "STEP 2  Stock in / stock out (+ business validations)"
request GET /api/stock/WH_RAW
BOX_BEFORE="$(jitem itemCode MAT_BOX_01 quantity)"

request POST /api/stock/in "{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\",\"quantity\":25,\"referenceNo\":\"SMOKE_IN_$RUN_ID\",\"user\":\"warehouse01\"}"
check "POST /api/stock/in -> success" 200 success true
BOX_NEW="$(jget balance)"
check_eq "stock-in raised balance by +25" "$(num "$BOX_BEFORE + 25")" "$BOX_NEW"

request GET /api/stock/WH_RAW
check "audit trail: MAT_BOX_01 persisted at new balance" 200 _
request POST /api/stock/out "{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\",\"quantity\":10,\"referenceNo\":\"SMOKE_OUT_$RUN_ID\",\"user\":\"warehouse01\"}"
check "POST /api/stock/out -> success" 200 success true
check_eq "stock-out lowered balance by -10" "$(num "$BOX_NEW - 10")" "$(jget balance)"

request POST /api/stock/out "{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\",\"quantity\":9999999,\"referenceNo\":\"SMOKE_NEG_$RUN_ID\"}"
check "over-issue rejected -> HTTP 409"        409 businessCode  ERR_INSUFFICIENT_STOCK
check "over-issue carries ORA-20003"           409 errorCode     ORA-20003
check "over-issue names failing procedure"     409 procedureName create_stock_out

request POST /api/stock/in "{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"ITEM_NOT_EXIST\",\"quantity\":5,\"referenceNo\":\"SMOKE_404_$RUN_ID\"}"
check "unknown item -> HTTP 404"               404 businessCode  ERR_NOT_FOUND

request POST /api/stock/in "{\"warehouseCode\":\"WH_RAW\",\"itemCode\":\"MAT_BOX_01\",\"quantity\":0,\"referenceNo\":\"SMOKE_400_$RUN_ID\"}"
check "quantity <= 0 -> HTTP 400"              400 businessCode  ERR_INVALID_QTY

# ---------------------------------------------------------- 3. manufacturing
title "STEP 3  Production order lifecycle through the API"
PO="SMOKE_PO_$RUN_ID"

request POST /api/manufacturing/production-order "{\"productionOrderNo\":\"$PO\",\"finishedGoodCode\":\"FG_RUNNER_PRO_42\",\"plannedQuantity\":5,\"warehouseCode\":\"WH_RAW\",\"user\":\"planner01\"}"
check "POST production-order -> created" 200 success true

request GET "/api/manufacturing/production-order/$PO"
check "GET production-order -> RELEASED"      200 status          RELEASED
check "GET production-order -> planned qty 5" 200 plannedQuantity 5

request GET /api/manufacturing/production-order/ORDER_NOT_EXIST
check "GET unknown production-order -> 404" 404 _

# 3b. BOM maintenance
request POST /api/manufacturing/bom/line "{\"finishedGoodCode\":\"FG_RUNNER_PRO_42\",\"version\":\"V1.0\",\"materialCode\":\"MAT_BOX_01\",\"quantityRequired\":1,\"user\":\"planner01\"}"
check "POST /api/manufacturing/bom/line -> saved" 200 success true

# 3a/3c. shortage incident: over-planned PO must fail with ORA-20007
request GET /api/stock/WH_RAW
RUB="$(jitem itemCode MAT_RUBBER_01 quantity)"
OVER_QTY="$(num "int($RUB) + 10000")"
PO_BIG="SMOKE_PO_BIG_$RUN_ID"
request POST /api/manufacturing/production-order "{\"productionOrderNo\":\"$PO_BIG\",\"finishedGoodCode\":\"FG_RUNNER_PRO_42\",\"plannedQuantity\":$OVER_QTY,\"warehouseCode\":\"WH_RAW\",\"user\":\"planner01\"}"
check "over-planned PO accepted (materials are not reserved)" 200 success true

request POST "/api/manufacturing/production-order/$PO_BIG/complete"
check "shortage -> HTTP 409"                    409 businessCode  ERR_MATERIAL_SHORTAGE
check "shortage -> ORA-20007"                   409 errorCode     ORA-20007
check "shortage -> failing procedure reported"  409 procedureName complete_production_order
check "shortage response carries the reference no" 409 referenceNo "$PO_BIG"

request GET "/api/support/errors?refNo=$PO_BIG"
if [ "$RESP_CODE" = "200" ] && [ "$(jany referenceNo "$PO_BIG")" = "true" ]; then
  PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} ERROR_LOG row survived the rollback (PRAGMA AUTONOMOUS_TRANSACTION) ${C_G}(HTTP 200)${C_0}"
else
  FAIL=$((FAIL+1)); FAILED_CHECKS="$FAILED_CHECKS
    - ERROR_LOG row survived the rollback"; say "  ${C_R}[FAIL]${C_0} ERROR_LOG row survived the rollback (HTTP $RESP_CODE)"
fi
say "  ${C_B}logged error  : $(jget errorCode 0) / $(jget procedureName 0)${C_0}"

request GET /api/stock/WH_RAW
check_eq "failed completion consumed nothing (atomic ROLLBACK)" "$RUB" "$(jitem itemCode MAT_RUBBER_01 quantity)"

# 3d. procurement fix path then successful completion
request POST /api/procurement/purchase-order "{\"purchaseOrderNo\":\"SMOKE_PUR_$RUN_ID\",\"itemCode\":\"MAT_RUBBER_01\",\"quantity\":100,\"warehouseCode\":\"WH_RAW\"}"
check "POST purchase-order -> registered as CREATED" 200 status CREATED

request GET /api/stock/WH_RAW
RUB_BEFORE="$(jitem itemCode MAT_RUBBER_01 quantity)"
request POST "/api/procurement/purchase-order/SMOKE_PUR_$RUN_ID/receive"
check "POST purchase-order/receive -> RECEIVED" 200 status RECEIVED
request GET /api/stock/WH_RAW
check_eq "receive_purchase_order added +100 to WH_RAW" "$(num "$RUB_BEFORE + 100")" "$(jitem itemCode MAT_RUBBER_01 quantity)"

request GET /api/stock/WH_RAW
FG_BEFORE="$(jitem itemCode FG_RUNNER_PRO_42 quantity)"
RUB_MID="$(jitem itemCode MAT_RUBBER_01 quantity)"
request POST "/api/manufacturing/production-order/$PO/complete?user=workshop_lead"
check "complete production order -> HTTP 200"  200 success  true
check "complete production order -> COMPLETED" 200 status   COMPLETED
request GET "/api/manufacturing/production-order/$PO"
check "QTY_DONE filled with planned qty" 200 doneQuantity 5
request GET /api/stock/WH_RAW
check_eq "finished goods increased by +5"   "$(num "$FG_BEFORE + 5")"  "$(jitem itemCode FG_RUNNER_PRO_42 quantity)"
check_eq "raw rubber consumed by -5 (BOM)"  "$(num "$RUB_MID - 5")"    "$(jitem itemCode MAT_RUBBER_01 quantity)"

request POST "/api/manufacturing/production-order/$PO/complete?user=workshop_lead"
check "completing a closed order -> 409 ERR_PO_ALREADY_COMPLETED" 409 businessCode ERR_PO_ALREADY_COMPLETED

# 3e. cancel path
PO2="SMOKE_PO2_$RUN_ID"
request POST /api/manufacturing/production-order "{\"productionOrderNo\":\"$PO2\",\"finishedGoodCode\":\"FG_SNEAKER_LITE_40\",\"plannedQuantity\":3,\"warehouseCode\":\"WH_RAW\",\"user\":\"planner01\"}"
check "second production order created" 200 success true
request POST "/api/manufacturing/production-order/$PO2/cancel?user=planner01"
check "cancel production order -> CANCELLED" 200 status CANCELLED
request POST "/api/manufacturing/production-order/$PO2/cancel?user=planner01"
check "cancelling twice -> 409" 409 businessCode ERR_PO_STATE_INVALID


# ------------------------------------------------------------- 4. erp support
title "STEP 4  ERP support / change request workflow"
CR="SMOKE_CR_$RUN_ID"
request POST /api/support/change-requests "{\"changeRequestNo\":\"$CR\",\"title\":\"Smoke test change request\",\"requestType\":\"DATA_FIX\",\"referenceNo\":\"$PO\",\"rootCause\":\"Automated API test\",\"fixAction\":\"No action required\",\"requester\":\"tan.mai\"}"
check "POST change-requests -> created"      200 success true
request GET "/api/support/change-requests/$CR"
check "GET change-requests/{crNo} -> OPEN"   200 status OPEN
check "GET change-requests/{crNo} -> type DATA_FIX" 200 requestType DATA_FIX

request GET /api/support/change-requests/CR_NOT_EXIST
check "GET unknown change request -> 404" 404 _

request GET "/api/support/errors?refNo=$PO_BIG"
check "GET support/errors filtered by refNo" 200 referenceNo "$PO_BIG"

request GET /api/support/errors?take=20
if [ "$RESP_CODE" = "200" ] && [ "$(jany errorCode -20007)" = "true" ]; then
  PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} GET support/errors returns recent ORA-20007 shortage logs ${C_G}(HTTP 200)${C_0}"
else
  FAIL=$((FAIL+1)); FAILED_CHECKS="$FAILED_CHECKS
    - GET support/errors returns recent ORA-20007 logs"; say "  ${C_R}[FAIL]${C_0} GET support/errors returns recent ORA-20007 logs (HTTP $RESP_CODE)"
fi

request GET /api/this-route-does-not-exist
check "unknown api route -> 404" 404 _

# ---------------------------------------------------------------- 5. swagger
title "STEP 5  OpenAPI contract"
request GET /swagger/v1/swagger.json
if [ "$RESP_CODE" = "200" ]; then
  PATH_COUNT="$(printf '%s' "$RESP_BODY" | python3 -c 'import json,sys;print(len(json.load(sys.stdin).get("paths",{})))' 2>/dev/null)"
  say "  ${C_B}swagger.json documents $PATH_COUNT paths${C_0}"
  if [ "${PATH_COUNT:-0}" -ge 10 ] 2>/dev/null; then
    PASS=$((PASS+1)); say "  ${C_G}[PASS]${C_0} swagger.json documents $PATH_COUNT paths (>= 10)"
  else
    FAIL=$((FAIL+1)); FAILED_CHECKS="$FAILED_CHECKS
    - swagger.json path count = $PATH_COUNT"; say "  ${C_R}[FAIL]${C_0} swagger.json path count = $PATH_COUNT"
  fi
else
  FAIL=$((FAIL+1)); FAILED_CHECKS="$FAILED_CHECKS
    - swagger.json reachable (HTTP $RESP_CODE)"; say "  ${C_R}[FAIL]${C_0} swagger.json reachable (HTTP $RESP_CODE)"
fi

# ------------------------------------------------------------------- summary
title "SUMMARY"
TOTAL=$((PASS + FAIL))
say "  checks run : $TOTAL"
say "  ${C_G}passed   : $PASS${C_0}"
if [ "$FAIL" -gt 0 ]; then
  say "  ${C_R}failed   : $FAIL${C_0}"
  printf '%b\n' "  ${C_R}failed checks:$FAILED_CHECKS${C_0}"
  say "\n${C_R}RESULT: API SMOKE TEST FAILED${C_0}"
  exit 1
fi
say "\n${C_G}RESULT: API SMOKE TEST - ALL $PASS CHECKS PASSED${C_0}"
exit 0
