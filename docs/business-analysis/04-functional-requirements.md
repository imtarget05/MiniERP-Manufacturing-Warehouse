# Functional Requirements Specification (FRS)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** BA-FRS-004  
**Target Enterprise:** Tân Vĩnh Footwear & Apparel Manufacturing Co., Ltd.  

---

## FR-001: Goods Receipt with Lot and Expiry Capture
- **Description:** Allows warehouse staff to record inbound deliveries of raw materials against a valid Purchase Order, capturing supplier lot code, expiry date, and initial receiving location.
- **Actor:** Warehouse Staff (`WAREHOUSE`), Admin (`ADMIN`)
- **Precondition:** Purchase Order exists in `PURCHASE_ORDER` with status allowing receipt; receiving bin location exists in `WAREHOUSE_LOCATION`.
- **Main Flow:**
  1. Actor submits receipt request containing PO number, item code, quantity, lot code, expiry date, bin location, and idempotency key.
  2. System validates that item belongs to PO and received quantity does not exceed PO pending balance.
  3. System creates record in `INVENTORY_LOT` with status `ACTIVE`.
  4. System increments `LOT_STOCK` and aggregate `STOCK`.
  5. System records `INVENTORY_TRANSACTION` with `TXN_TYPE = 'STOCK_IN'`.
- **Alternative Flow:**
  - *2a. Replayed Idempotency Key:* System detects duplicate submission with identical payload; returns previous receipt response with `200 OK` without duplicating stock.
  - *2b. Exceeded PO Quantity:* System rejects transaction with `409 Conflict` (`ORA-20014`).
- **Postcondition:** Inventory balances are incremented, lot is active for FEFO allocation, and transaction trail is recorded.
- **Acceptance Criteria:** Given PO `PO_PUR_901` for 100 units `MAT_RUBBER_01`, receiving 100 units creates lot `RM001`, increases warehouse stock by 100, and logs `STOCK_IN` transaction.

---

## FR-002: Production Order Pre-Flight Material Validation & Release
- **Description:** Validates raw material availability according to BOM specifications before allowing a production order to be released to the manufacturing floor.
- **Actor:** Production Planner (`PLANNER`), Admin (`ADMIN`)
- **Precondition:** Production Order exists in `CREATED` status; active BOM exists for the target Finished Good.
- **Main Flow:**
  1. Actor triggers material check for target production order (`/api/automation/production-order/{poNo}/material-check`).
  2. System explodes BOM for planned quantity.
  3. System verifies available unreserved physical stock for every required material.
  4. If all materials are sufficient, status transitions to `READY`.
  5. Actor releases order (`/api/automation/production-order/{poNo}/release`); system creates soft reservations in `STOCK_RESERVATION` and updates status to `RELEASED`.
- **Alternative Flow:**
  - *3a. Material Shortage:* Available stock is less than required. System updates PO status to `WAITING_MATERIAL`, logs incident in `SUPPORT_INCIDENT`, generates low-stock alert in `REPLENISH_ALERT`, and records autonomous error in `ERROR_LOG`.
- **Postcondition:** Production order is in `RELEASED` status with materials reserved, preventing competing orders from claiming the same inventory.
- **Acceptance Criteria:** PO with planned quantity requiring 50 units of rubber when only 40 are in stock transitions to `WAITING_MATERIAL` and returns `409 Conflict`.

---

## FR-003: Atomic Production Order Completion & Finished Goods Receipt
- **Description:** Consumes required raw materials, increments finished goods inventory, and marks the production order as completed within a single atomic ACID transaction.
- **Actor:** Production Operator (`PRODUCTION`), Planner (`PLANNER`), Admin (`ADMIN`)
- **Precondition:** Production Order is in `RELEASED` or `READY` status; required raw materials are physically present.
- **Main Flow:**
  1. Actor submits order completion request (`POST /api/manufacturing/production-order/{poNo}/complete`).
  2. System verifies active status and re-validates stock.
  3. System deducts BOM raw materials from `STOCK`, writing `MFG_CONSUME` transaction records.
  4. System adds planned quantity to Finished Goods `STOCK`, writing `MFG_OUTPUT` transaction record.
  5. System updates `PRODUCTION_ORDER` status to `COMPLETED`, records `COMPLETED_AT = SYSDATE`, and marks reservations as `CONSUMED`.
  6. Transaction commits atomically.
- **Alternative Flow:**
  - *2a. Intermediate Material Theft or Defect:* Physical stock dropped below required quantity prior to completion. Transaction rolls back completely; zero FG is produced; incident logged to `ERROR_LOG`.
- **Postcondition:** Raw materials are depleted, finished goods stock increases by planned quantity, order is marked `COMPLETED`.
- **Acceptance Criteria:** Completing order for 10 pairs of shoes reduces rubber by 10 units and increases finished shoe stock by 10 units. Retrying completion returns `409 Conflict` (idempotent state guard).

---

## FR-004: FEFO / FIFO Material Allocation & Lot Traceability
- **Description:** Automatically suggests and allocates material lots for a production order based on First-Expiry-First-Out (FEFO) rules.
- **Actor:** Warehouse Operator (`WAREHOUSE`), Production Operator (`PRODUCTION`)
- **Precondition:** Production Order exists; multiple lots of required raw materials exist in `INVENTORY_LOT` with valid expiry dates.
- **Main Flow:**
  1. Actor queries lot allocation for order (`GET /api/manufacturing/production-order/{poNo}/allocate-lots`).
  2. System retrieves active unreserved lots for each BOM item, ordered by `EXPIRY_DATE ASC`, followed by `CREATED_AT ASC`.
  3. System assigns quantity requirement sequentially across candidate lots until order requirement is satisfied.
  4. Actor confirms issue (`POST /api/manufacturing/production-order/{poNo}/issue-lots`).
  5. System records lot consumption in `PRODUCTION_LOT_CONSUMPTION` and writes `TRACEABILITY_EVENT`.
- **Alternative Flow:**
  - *2a. Expired or Held Lots:* Lots in `HOLD`, `EXPIRED`, or `BLOCKED` status are automatically filtered out and excluded from allocation.
- **Postcondition:** Materials are issued in compliance with FEFO guidelines; full genealogy link is created.
- **Acceptance Criteria:** Given Lot A (expires in 10 days) and Lot B (expires in 30 days), system allocates Lot A first.

---

## FR-005: Two-Person Approval Gate for Inventory Adjustments
- **Description:** Requires multi-role approval before any manual stock adjustment or write-off can modify warehouse inventory balances.
- **Actor:** Warehouse Staff (Requester), Plant Manager / Admin (Approver)
- **Precondition:** Item and warehouse exist; discrepancy observed during physical cycle count.
- **Main Flow:**
  1. Warehouse Staff submits adjustment proposal (`POST /api/automation/approvals` or `/api/automation/stock/adjust`).
  2. System records `APPROVAL_REQUEST` in `PENDING` status. Ledger stock is untouched.
  3. Plant Manager reviews pending requests (`GET /api/automation/approvals`).
  4. Plant Manager approves request (`POST /api/automation/approvals/{id}/decision` with `DECISION = 'APPROVED'`).
  5. System updates physical `STOCK`, logs `ADJUSTMENT` record in `INVENTORY_TRANSACTION`, and writes audit record in `APP_AUDIT_EVENT`.
- **Alternative Flow:**
  - *4a. Manager Rejection:* Manager rejects request with reason notes. Status becomes `REJECTED`; stock remains unchanged.
  - *1b. Unauthorized Direct Modification:* Regular warehouse staff attempting direct adjustment receives `403 Forbidden`.
- **Postcondition:** Stock balance reflects approved adjustment; full dual-authorization audit trail is preserved.
- **Acceptance Criteria:** Unapproved adjustment request leaves stock balance at original value. Approval by manager applies delta and records approver user ID.
