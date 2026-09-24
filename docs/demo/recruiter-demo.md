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
```

---

## 2. Live Demo Script (Step-by-Step)

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
  }' | jq .
```
- **Key Talking Point:** Show that the API captures lot code, expiration date, destination bin location, and validates idempotency to prevent duplicate scanner clicks.

### Step 2: Verify Real-Time Inventory (30 Seconds)
```bash
# Check warehouse stock: inventory increased by 100
curl -s http://localhost:5000/api/stock/WH_RAW | jq '.[] | select(.itemCode=="MAT_RUBBER_01")'
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
  }' | jq .

# 3. Perform pre-flight material check (BOM explosion)
curl -s -X POST http://localhost:5000/api/automation/production-order/PO_DEMO_01/material-check \
  -H "Authorization: Bearer $TOKEN_PLAN" | jq .

# 4. Release order (Soft-reserves raw materials)
curl -s -X POST http://localhost:5000/api/automation/production-order/PO_DEMO_01/release \
  -H "Authorization: Bearer $TOKEN_PLAN" | jq .
```
- **Key Talking Point:** Point out how the system soft-reserves stock to avoid line starvation.

### Step 4: Execute Assembly & Atomic Completion (1.5 Minutes)
> *"On the shop floor, the assembly line stitches the shoes and completes the order."*
```bash
# 1. Complete order: consumes raw materials according to BOM & outputs finished shoes
curl -s -X POST http://localhost:5000/api/manufacturing/production-order/PO_DEMO_01/complete?user=planner01 \
  -H "Authorization: Bearer $TOKEN_PLAN" | jq .

# 2. Verify raw material stock decreased by exactly 30 units
curl -s http://localhost:5000/api/stock/WH_RAW | jq '.[] | select(.itemCode=="MAT_RUBBER_01")'

# 3. Verify finished goods increased in WH_FG
curl -s http://localhost:5000/api/stock/WH_FG | jq '.[] | select(.itemCode=="FG_RUNNER_PRO_42")'
```
- **Key Talking Point:** Emphasize that raw material deduction and finished good increment occurred inside a single atomic Oracle transaction. Retrying returns 409 Conflict.

### Step 5: Login as Manager & Review Two-Person Approval (1 Minute)
> *"For high-impact inventory adjustments, the system enforces a strict two-person rule."*
```bash
# 1. Warehouse clerk submits adjustment request
curl -s -X POST http://localhost:5000/api/automation/approvals \
  -H "Authorization: Bearer $TOKEN_WH" \
  -H "Content-Type: application/json" \
  -d '{
    "approvalType": "STOCK_ADJUST",
    "referenceNo": "CYCLE_COUNT_01",
    "targetTable": "STOCK",
    "notes": "Cycle count discrepancy -10"
  }' | jq .

# 2. Manager logs in and approves the adjustment
TOKEN_ADMIN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"Admin@123"}' | grep -oE '"accessToken":"[^"]+' | cut -d'"' -f4)

curl -s -X POST http://localhost:5000/api/automation/approvals/APP-001/decision \
  -H "Authorization: Bearer $TOKEN_ADMIN" \
  -H "Content-Type: application/json" \
  -d '{"decision":"APPROVED","comment":"Verified by Plant Manager"}' | jq .
```

### Step 6: Visual Dashboard, Audit Trail & Backward Genealogy (1.5 Minutes)
1. Open browser at `http://localhost:8080`.
2. Show **Overview Tab**: Live KPI metrics (Active Warehouses, Safety Stock Health, Open Orders).
3. Show **Lots & Receiving Tab**: Enter finished good lot identifier and click **"Xem Cây Phả hệ (Backward Trace)"**. Show the complete interactive hierarchy:
   `Finished Good Lot` $\rightarrow$ `PO_DEMO_01` $\rightarrow$ `Raw Material Lots` $\rightarrow$ `Inbound Purchase Order`.
4. Demonstrate **Audit Trail**: Show immutable records in `APP_AUDIT_EVENT`.

### Step 7: Automated Tests, CI/CD & Disaster Recovery (1 Minute)
```bash
# 1. Show automated test suite passing cleanly (164 tests, 0 failures)
dotnet test tests/MiniERP.Api.Tests

# 2. Highlight automated backup verification
bash scripts/verify-backup.sh
```
- Conclude by pointing to the comprehensive documentation suite (`docs/`).
