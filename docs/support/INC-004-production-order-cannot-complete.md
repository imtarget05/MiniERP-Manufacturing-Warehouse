# Incident Runbook: INC-004 — Production Order Cannot Complete
**Project:** MiniERP Manufacturing & Warehouse  
**Incident Code:** INC-004  
**Severity:** P1 (Critical — Factory Line Blocker)  
**Target Component:** Manufacturing Execution / `ERP_OPERATIONS.complete_production_order`  

---

## 1. Symptoms & Incident Report
- **Reported By:** Assembly Line 1 Supervisor.
- **Symptom:** Submitting `/api/manufacturing/production-order/PO001/complete` fails with:
  `409 Conflict - {"success":false,"errorCode":"ORA-20007","businessCode":"ERR_MATERIAL_SHORTAGE"}`.
- **Impact:** Finished goods cannot be received into `WH_FG`; shipping containers cannot be loaded.

---

## 2. Investigation Protocol & Diagnostic Steps

1. **Query Autonomous Error Log (`/api/support/errors?refNo=PO001`):**
   ```bash
   curl -s http://localhost:5000/api/support/errors?refNo=PO001
   ```
   *Response:*
   ```json
   [
     {
       "errorCode": "ERR_MATERIAL_SHORTAGE",
       "message": "Cannot complete PO PO001 due to material shortage: MAT_RUBBER_01: need 50, have 40",
       "procedureName": "complete_production_order",
       "referenceNo": "PO001"
     }
   ]
   ```
2. **Inspect Current Stock vs BOM Requirement:**
   - Order `PO001` specifies planned quantity = 50 pairs of `FG_RUNNER_PRO_42`.
   - BOM requires 1 pair of `MAT_RUBBER_01` per shoe (Total requirement: 50).
   - `STOCK` in `WH_RAW` for `MAT_RUBBER_01` has only 40 units available. Shortage = 10 units.

---

## 3. Root Cause Analysis (RCA)
The production order was created and released prior to verifying physical raw material availability. Inbound shipment `PO_PUR_901` containing 100 replacement soles arrived at the truck dock at 08:30 but had not been received in the system by the warehouse crew.

---

## 4. Resolution Procedure

1. **Expedite Goods Receipt of Inbound Purchase Order:**
   ```bash
   # Warehouse staff executes goods receipt for inbound PO:
   curl -X POST http://localhost:5000/api/procurement/purchase-order/PO_PUR_901/receive \
     -H "Authorization: Bearer $WAREHOUSE_TOKEN"
   # Stock of MAT_RUBBER_01 increases from 40 to 140.
   ```
2. **Re-attempt Production Order Completion:**
   ```bash
   curl -X POST http://localhost:5000/api/manufacturing/production-order/PO001/complete \
     -H "Authorization: Bearer $PLANNER_TOKEN"
   # Returns: 200 OK - {"success":true,"status":"COMPLETED"}
   ```
3. **Verify Stock Deductions & Finished Goods Addition:**
   - `MAT_RUBBER_01`: Decreased from 140 to 90.
   - `FG_RUNNER_PRO_42`: Increased by 50 pairs in `WH_FG`.

---

## 5. Prevention & System Hardening
- Mandate pre-flight BOM check via `/api/automation/production-order/{poNo}/material-check` before issuing physical order tickets to the line.
- Enable automatic purchase requisitions when stock approaches reorder points (`REORDER_POINT`).
