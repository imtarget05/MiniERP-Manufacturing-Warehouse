# User Acceptance Test Cases Specification
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** UAT-CAS-002  

---

## 1. Test Cases Matrix

### UAT-INV-001: Goods Receipt with Lot Number & Expiry Date
- **Module:** Warehouse Logistics
- **Actor:** Warehouse Staff (`warehouse01`)
- **Precondition:** Purchase Order `PO_PUR_901` exists for 100 units of `MAT_RUBBER_01`.
- **Test Steps:**
  1. Open Dashboard and login as `warehouse01` / `Warehouse@123`.
  2. Navigate to "Nhận hàng & Lots" tab.
  3. Enter PO `PO_PUR_901`, Item `MAT_RUBBER_01`, Quantity `100`, Lot Code `RM001-DEMO-001`, Expiry Date `2027-12-31`, Location `RCV-01`.
  4. Click "Nhận hàng".
  5. Check Inventory tab for `WH_RAW`.
- **Expected Result:** Lot `RM001-DEMO-001` is registered; stock increases by 100; transaction `STOCK_IN` appears in audit logs.
- **Status:** 🟢 PASSED

---

### UAT-INV-002: Direct Goods Issue & Insufficient Quantity Validation
- **Module:** Warehouse Logistics
- **Actor:** Warehouse Staff (`warehouse01`)
- **Precondition:** `WH_RAW` contains 100 units of `MAT_RUBBER_01`.
- **Test Steps:**
  1. Login as `warehouse01`.
  2. Navigate to "Kho & Tồn kho" tab.
  3. Submit Goods Issue (`/api/stock/out`) for 1,000 units of `MAT_RUBBER_01` (exceeding stock).
- **Expected Result:** System rejects transaction with `409 Conflict` and code `ERR_INSUFFICIENT_STOCK`. Ledger balance remains completely unchanged.
- **Status:** 🟢 PASSED

---

### UAT-INV-003: Bin Location Transfer (Putaway)
- **Module:** Warehouse Logistics
- **Actor:** Warehouse Staff (`warehouse01`)
- **Precondition:** Lot `RM001-DEMO-001` exists in dock bin `RCV-01` with 100 units.
- **Test Steps:**
  1. Navigate to "Nhận hàng & Lots" tab $\rightarrow$ "Chuyển vị trí kho (Move / Putaway)".
  2. Select Lot `RM001-DEMO-001`, Source Location `RCV-01`, Destination Location `BIN-01-01`, Quantity `50`.
  3. Click "Thực hiện di chuyển".
  4. Query lot stock breakdown by location.
- **Expected Result:** Location `RCV-01` has 50 units, `BIN-01-01` has 50 units. Overall warehouse stock remains 100 units.
- **Status:** 🟢 PASSED

---

### UAT-MFG-001: Production Order Creation & Shortage Detection
- **Module:** Manufacturing Execution
- **Actor:** Production Planner (`planner01`)
- **Precondition:** Rubber sole stock in `WH_RAW` is 40 units. BOM for `FG_RUNNER_PRO_42` requires 1 sole per pair.
- **Test Steps:**
  1. Navigate to "Sản xuất & BOM" tab.
  2. Create Production Order `PO_TEST_SHORT` for 50 pairs of `FG_RUNNER_PRO_42`.
  3. Click "Kiểm tra NVL" (Material Availability Check).
- **Expected Result:** System flags 10 units shortage on `MAT_RUBBER_01`; PO status updates to `WAITING_MATERIAL`; alert is logged in `REPLENISH_ALERT` and `ERROR_LOG`.
- **Status:** 🟢 PASSED

---

### UAT-MFG-002: Production Order Atomic Completion & Finished Goods Output
- **Module:** Manufacturing Execution
- **Actor:** Production Planner (`planner01`), Admin (`admin`)
- **Precondition:** 100 units of rubber received in `WH_RAW`. Stock is sufficient.
- **Test Steps:**
  1. Create Production Order for 30 pairs of `FG_RUNNER_PRO_42`.
  2. Click "Phát hành lệnh" (Release Order). Status becomes `RELEASED`.
  3. Click "Hoàn thành lệnh sản xuất" (Complete Order).
  4. Check stock for raw materials and finished goods.
- **Expected Result:** Order status changes to `COMPLETED`; exactly 30 soles, 15m mesh, 3 spools thread, 6kg glue, and 30 boxes are consumed; exactly 30 pairs of `FG_RUNNER_PRO_42` appear in finished goods stock.
- **Status:** 🟢 PASSED

---

### UAT-SEC-001: Multi-Role Authorization & 403 Forbidden Enforcement
- **Module:** Enterprise Security & RBAC
- **Actor:** Warehouse Operator (`warehouse01`), Auditor (`auditor01`)
- **Test Steps:**
  1. Login as `warehouse01` (Warehouse Operator role).
  2. Attempt to invoke administrative user creation endpoint `POST /api/admin/users`.
  3. Login as `auditor01` (Auditor / Viewer role).
  4. Attempt to mutate inventory by calling `POST /api/stock/in`.
- **Expected Result:** Both requests are blocked at the server API level with `403 Forbidden`. No state changes occur.
- **Status:** 🟢 PASSED

---

### UAT-APP-001: Two-Person Approval Gate for Inventory Adjustment
- **Module:** Enterprise Governance
- **Actor:** Warehouse Staff (Requester), Plant Manager / Admin (Approver)
- **Test Steps:**
  1. Warehouse Staff submits adjustment proposal (`POST /api/automation/approvals`) for -20 units of `MAT_RUBBER_01`.
  2. Verify stock balance in `WH_RAW`. Balance must NOT change.
  3. Login as `admin` (holding `MANAGER`/`ADMIN` role).
  4. Review pending approval list and submit approval decision `APPROVED`.
  5. Check stock balance in `WH_RAW`.
- **Expected Result:** Stock balance is reduced by 20 only after managerial approval decision is posted. Full audit event is logged.
- **Status:** 🟢 PASSED

---

### UAT-REP-001: Operational Dashboard & Traceability Report
- **Module:** Management Reporting
- **Actor:** Plant Superintendent / Auditor
- **Test Steps:**
  1. Access Dashboard "Tổng quan" tab.
  2. Verify KPI cards: Active Warehouses (3), Safety Stock Health, Open Orders, System Error Count.
  3. Access "Nhận hàng & Lots" tab and enter Finished Good lot identifier.
  4. Click "Xem Cây Phả hệ (Backward Trace)".
- **Expected Result:** Dashboard shows accurate real-time values; genealogy tree renders complete hierarchy from finished shoe down to original raw material lots and supplier PO numbers.
- **Status:** 🟢 PASSED
