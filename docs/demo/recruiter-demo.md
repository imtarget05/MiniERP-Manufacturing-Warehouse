# Recruiter & Technical Interview Live Demo Script
**Project:** MiniERP Manufacturing & Warehouse  
**Target Duration:** 5–10 Minutes  
**Audience:** Hiring Managers, Lead Technical Interviewers, ERP Recruiters  
**Author:** Mai Nguyễn Bình Tân  

---

## 1. Demo Narrative & Setup (30 Seconds)

> *"Hello, today I want to demonstrate MiniERP, a portfolio implementation of a factory Manufacturing Execution (MES) and Warehouse Management (WMS) system. It simulates the core operational lifecycle of a footwear manufacturing plant built with ASP.NET Core 8, Oracle Database 23c PL/SQL, and an interactive operations dashboard."*

### Prerequisites Setup (Terminal):
```bash
# 1. Start Oracle Database container
docker compose up -d oracle-db && bash scripts/start-db.sh

# 2. Seed database master data & schema
bash scripts/run-sql.sh

# 3. Start Web API (port 5000)
bash scripts/start-api.sh &

# 4. Open dashboard in browser
cd dashboard && python3 -m http.server 8080 &
# Open: http://localhost:8080

# 5. JSON helpers used by every command below - run this first in the SAME shell.
#    `jq` is optional: when it is missing the helper falls back to python3, which
#    ships with macOS, so the rehearsal needs no extra install.
pp() {  # pretty-print a JSON response
  if command -v jq >/dev/null 2>&1; then jq .; else python3 -m json.tool; fi
}
pick_item() {  # usage: curl -s .../api/stock/WH_RAW | pick_item MAT_RUBBER_01
  if command -v jq >/dev/null 2>&1; then
    jq ".[] | select(.itemCode==\"$1\")"
  else
    ITEM_CODE="$1" python3 -c '
import json, os, sys
rows = json.load(sys.stdin)
rows = rows if isinstance(rows, list) else [rows]
for r in rows:
    if r.get("itemCode") == os.environ["ITEM_CODE"]:
        print(json.dumps(r, indent=2))'
  fi
}
```

---

## 2. Live Demo Script (Step-by-Step)

> **Rehearsal note:** run the commands in one shell so `$TOKEN_*`, `pp` and
> `pick_item` stay in scope. Steps are repeatable except order creation: before a
> second rehearsal restore the baseline with `bash scripts/run-sql.sh` (or switch
> to a fresh `PO_NO`/`RUN` suffix as done in Step 6).

### Step 1: Login as Warehouse Staff & Receive Raw Material (1 Minute)
> *"Let's begin at the truck unloading dock. A supplier delivers 100 pairs of vulcanized rubber soles."*
```bash
# 1. Login as warehouse staff
TOKEN_WH=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"warehouse01","password":"Warehouse@123"}' | grep -oE '"accessToken":"[^"]+' | cut -d'"' -f4)

# 2. Receive 100 units of MAT_RUBBER_01 with lot tracking & idempotency key
curl -s -X POST http://localhost:5000/api/warehouse/receipts/PO_PUR_LOT_01/receive \
  -H "Authorization: Bearer $TOKEN_WH" \
  -H "Content-Type: application/json" \
  -d '{
    "itemCode": "MAT_RUBBER_01",
    "qty": 100,
    "lotCode": "RM-RUB-2026-01",
    "receivingLocationCode": "RCV-01",
    "idempotencyKey": "recv-demo-001"
  }' | pp
```
- **Key Talking Point:** Show that the API captures lot code, expiration date, destination bin location, and validates idempotency to prevent duplicate scanner clicks.

### Step 2: Verify Real-Time Inventory (30 Seconds)
```bash
# Check warehouse stock: inventory increased by 100
curl -s http://localhost:5000/api/stock/WH_RAW | pick_item MAT_RUBBER_01
```

### Step 3: Login as Production Planner & Schedule Order (1.5 Minutes)
> *"Next, the planning department schedules production of running shoes."*
```bash
# 1. Login as planner
TOKEN_PLAN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"planner01","password":"Planner@123"}' | grep -oE '"accessToken":"[^"]+' | cut -d'"' -f4)

# 2. Create Production Order PO_DEMO_01 for 30 pairs of FG_RUNNER_PRO_42
curl -s -X POST http://localhost:5000/api/manufacturing/production-order \
  -H "Authorization: Bearer $TOKEN_PLAN" \
  -H "Content-Type: application/json" \
  -d '{
    "productionOrderNo": "PO_DEMO_01",
    "finishedGoodCode": "FG_RUNNER_PRO_42",
    "plannedQuantity": 30,
    "warehouseCode": "WH_RAW",
    "user": "planner01"
  }' | pp

# 3. Perform pre-flight material check (BOM explosion)
curl -s -X POST http://localhost:5000/api/automation/production-order/PO_DEMO_01/material-check \
  -H "Authorization: Bearer $TOKEN_PLAN" | pp

# 4. Soft-reserve every BOM line (idempotent; ORA-20007 if stock is short)
curl -s -X POST http://localhost:5000/api/automation/production-order/PO_DEMO_01/reserve \
  -H "Authorization: Bearer $TOKEN_PLAN" | pp
```
- **Key Talking Point:** Point out how the system soft-reserves stock to avoid line starvation.

### Step 4: Execute Assembly & Atomic Completion (1.5 Minutes)
> *"On the shop floor, the assembly line stitches the shoes and completes the order."*
```bash
# 1. Complete order: flips the reservations to CONSUMED and moves stock atomically
curl -s -X POST http://localhost:5000/api/automation/production-order/PO_DEMO_01/complete \
  -H "Authorization: Bearer $TOKEN_PLAN" | pp

# 2. Verify raw material stock decreased by exactly 30 units (BOM = 1 pair/pair)
curl -s http://localhost:5000/api/stock/WH_RAW | pick_item MAT_RUBBER_01

# 3. Verify finished goods landed in the ORDER'S warehouse (WH_RAW, the value
#    passed at creation): ERP_OPERATIONS.complete_production_order books the FG
#    output to PRODUCTION_ORDER.WAREHOUSE_ID - there is no separate FG move.
curl -s http://localhost:5000/api/stock/WH_RAW | pick_item FG_RUNNER_PRO_42

# 4. Re-run the completion: the state machine answers 409 (nothing double-counted)
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  http://localhost:5000/api/automation/production-order/PO_DEMO_01/complete \
  -H "Authorization: Bearer $TOKEN_PLAN"
```
- **Key Talking Point:** Emphasize that raw material deduction and finished good increment occurred inside a single atomic Oracle transaction. Retrying returns 409 Conflict.

### Step 5: Login as Manager & Review Two-Person Approval (1 Minute)
> *"For high-impact inventory adjustments, the system enforces a strict two-person rule."*
```bash
# 1. Warehouse clerk PROPOSES the adjustment (an operator may only propose)
APP_NO="APP_DEMO_$(date +%H%M%S)"
curl -s -X POST http://localhost:5000/api/automation/approvals \
  -H "Authorization: Bearer $TOKEN_WH" \
  -H "Content-Type: application/json" \
  -d "{
    \"approvalNo\": \"$APP_NO\",
    \"action\": \"INVENTORY_ADJUST\",
    \"referenceNo\": \"CYCLE_COUNT_01\",
    \"payload\": \"Cycle count discrepancy -10 MAT_RUBBER_01 in WH_RAW\"
  }" | pp

# 2. Manager logs in and DECIDES - a different person in a different role
TOKEN_ADMIN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Admin@123"}' | grep -oE '"accessToken":"[^"]+' | cut -d'"' -f4)

curl -s -X POST "http://localhost:5000/api/automation/approvals/$APP_NO/decision" \
  -H "Authorization: Bearer $TOKEN_ADMIN" \
  -H "Content-Type: application/json" \
  -d '{"decision":"APPROVED"}' | pp

# 3. The decided request is in the queue with decidedAt populated
curl -s "http://localhost:5000/api/automation/approvals?status=APPROVED&take=5" \
  -H "Authorization: Bearer $TOKEN_ADMIN" | pp
```
- **Key Talking Point:** The two-person rule (CR-001) is a policy, not a convention: `warehouse01` carries `OperationsMutation` and can only create a `PENDING` proposal, while `admin` carries `SupportMutation` and is the only one allowed to POST `/decision`. Ledger stock is still untouched - it moves only when `POST /api/automation/stock/adjust` consumes an approved token.

### Step 6: Lot Genealogy & Backward Trace (1.5 Minutes)
> *"Traceability only pays off when the finished good can be traced back to the exact material lot it was built from."*

```bash
# 1. Create a lot-tracked order (qty 1 keeps the FEFO issue instant)
RUN="DEMO$(date +%H%M%S)"
FG_LOT="FG-$RUN"
curl -s -X POST http://localhost:5000/api/manufacturing/production-order \
  -H "Authorization: Bearer $TOKEN_ADMIN" \
  -H "Content-Type: application/json" \
  -d "{
    \"productionOrderNo\": \"MO-$RUN\",
    \"finishedGoodCode\": \"FG_RUNNER_PRO_42\",
    \"plannedQuantity\": 1,
    \"warehouseCode\": \"WH_RAW\"
  }" | pp

# 2. FEFO allocation preview (earliest ACTIVE expiry wins; held/expired excluded)
curl -s -H "Authorization: Bearer $TOKEN_ADMIN" \
  "http://localhost:5000/api/manufacturing/production-order/MO-$RUN/allocate-lots" | pp

# 3. Issue the material lots -> PRODUCTION_LOT_CONSUMPTION rows = the genealogy
curl -s -X POST "http://localhost:5000/api/manufacturing/production-order/MO-$RUN/issue-lots" \
  -H "Authorization: Bearer $TOKEN_ADMIN" -H "Content-Type: application/json" \
  -d "{\"idempotencyKey\":\"demo-issue-$RUN\"}" | pp

# 4. Complete traceably -> creates the FG lot in WH_FG and links it to MO-$RUN
#    (idempotent: a replay answers {"replayed": true} instead of duplicating stock)
curl -s -X POST "http://localhost:5000/api/manufacturing/production-order/MO-$RUN/complete-traceable" \
  -H "Authorization: Bearer $TOKEN_ADMIN" -H "Content-Type: application/json" \
  -d "{\"fgLot\":\"$FG_LOT\",\"qty\":1,\"locationCode\":\"FG-01-01\",\"outputWarehouseCode\":\"WH_FG\",\"tolerance\":0,\"idempotencyKey\":\"demo-complete-$RUN\"}" | pp

# 5. Backward trace from the finished-good lot
curl -s -H "Authorization: Bearer $TOKEN_ADMIN" \
  "http://localhost:5000/api/trace/$FG_LOT?direction=backward" | pp
```
1. Open browser at `http://localhost:8080`.
2. Show **Overview Tab**: Live KPI metrics (Active Warehouses, Safety Stock Health, Open Orders).
3. Show **Lots & Receiving Tab**: enter the `$FG_LOT` printed above and click **"Xem Cây Phả hệ (Backward Trace)"**. Show the complete interactive hierarchy:
   `FG-DEMO...` $\rightarrow$ `MO-DEMO...` $\rightarrow$ `Raw Material Lots` $\rightarrow$ `Inbound Purchase Order`.
4. Demonstrate **Audit Trail**: Show immutable records in `APP_AUDIT_EVENT`.
- **Key Talking Point:** the same order can be completed through the accounting path (Step 4) or the lot path (above); the lot path proves FEFO excludes held/expired lots, keeps `STOCK` and `LOT_STOCK` reconciled, and is replay-safe.

### Step 7: Automated Tests, CI/CD & Disaster Recovery (1 Minute)
```bash
# 1. Full suite against Oracle (166 tests, 0 failures)
dotnet test tests/MiniERP.Api.Tests

# 2. DB-free contract suite - runs with no container at all (139 tests)
dotnet test tests/MiniERP.Api.Tests --filter "Category!=Integration"

# 3. Traceability E2E acceptance (61 assertions) + backup verification
bash scripts/test-traceability.sh
bash scripts/verify-backup.sh
```
# 2. Highlight automated backup verification
bash scripts/verify-backup.sh
```
- Conclude by pointing to the comprehensive documentation suite (`docs/`).
