# Use Case Specifications (UC)
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** BA-UC-006  
**Target Enterprise:** Tân Vĩnh Footwear & Apparel Manufacturing Co., Ltd.  

---

## UC-01: Receive Raw Materials by Lot & Bin Location
- **Primary Actor:** Warehouse Operator (`warehouse01`)
- **Supporting Systems:** Rugged Barcode Handheld Scanner, Zebra Label Printer
- **Goal:** Ingest physical supplier shipment into the warehouse ledger with lot number and expiry dates.
- **Trigger:** Truck arrives at `WH_RAW` unloading dock with supplier goods delivery.
- **Preconditions:** Supplier Purchase Order exists in `PURCHASE_ORDER`.
- **Main Success Scenario:**
  1. Operator logs into handheld scanner; application acquires JWT Bearer token.
  2. Operator scans PO barcode `PO_PUR_901`.
  3. Operator scans carton barcode, capturing Supplier Lot `LOT-RUBBER-2026A` and expiration date `2027-12-31`.
  4. Operator specifies destination dock bin `RCV-01` and quantity `100 PAIR`.
  5. Operator triggers submission with unique scan GUID (`idempotencyKey`).
  6. Backend invokes `ERP_TRACEABILITY.receive_lot_stock`.
  7. Database increments `STOCK` and `LOT_STOCK`, generates `STOCK_IN` transaction, and logs audit record.
  8. Operator uses printer service (`POST /api/labels`) to print Zebra ZPL II bin label for the pallet.
- **Extensions:**
  - *5a. Handheld Network Drop:* Scanner reconnects and resends identical GUID. Backend recognizes existing key in `REQUEST_IDEMPOTENCY` and confirms previous success without duplicating inventory.
  - *6a. Invalid PO / Item Mismatch:* Backend returns `404 Not Found` or `409 Conflict`; handheld sounds error tone.

---

## UC-02: Production Order Planning, Pre-Flight Check & Floor Release
- **Primary Actor:** Production Planner (`planner01`)
- **Goal:** Schedule factory assembly order for 50 pairs of shoes, verifying raw material availability before dispatching work tickets to the stitching line.
- **Trigger:** Sales department confirms new export shipment schedule.
- **Preconditions:** Active BOM exists for `FG_RUNNER_PRO_42`.
- **Main Success Scenario:**
  1. Planner accesses MiniERP Web Dashboard $\rightarrow$ "Sản xuất & BOM" tab.
  2. Planner creates Production Order `PO_EXPORT_001` for 50 pairs of `FG_RUNNER_PRO_42` assigned to `WH_RAW`.
  3. Order is created in `CREATED` status.
  4. Planner clicks "Kiểm tra NVL" (Trigger `/api/automation/production-order/PO_EXPORT_001/material-check`).
  5. System evaluates BOM: 50 soles, 25m mesh, 5 spools thread, 10kg glue, 50 boxes.
  6. System checks current unreserved stock. All lines have sufficient balance. Order transitions to `READY`.
  7. Planner clicks "Phát hành lệnh" (`/api/automation/production-order/PO_EXPORT_001/release`).
  8. System records soft holds in `STOCK_RESERVATION` and updates PO status to `RELEASED`.
- **Extensions:**
  - *6a. Material Shortage Detected:* System discovers only 40 soles in stock (need 50). Status updates to `WAITING_MATERIAL`. Automated alert generated in `REPLENISH_ALERT`. IT Support / Procurement notified. Order cannot be released until new stock arrives.

---

## UC-03: FEFO Material Issue & Order Completion
- **Primary Actor:** Line Operator (`planner01` / `admin`), Warehouseman (`warehouse01`)
- **Goal:** Issue raw materials according to earliest expiration dates, manufacture shoes, and deposit finished goods into `WH_FG`.
- **Preconditions:** Production order is `RELEASED`.
- **Main Success Scenario:**
  1. Operator queries suggested FEFO allocation (`GET /api/manufacturing/production-order/{poNo}/allocate-lots`).
  2. System suggests lots sorted by expiration date (`EXPIRY_DATE ASC`).
  3. Operator verifies and issues lots (`POST /api/manufacturing/production-order/{poNo}/issue-lots`).
  4. Materials are physically transferred to assembly line; system updates `PRODUCTION_LOT_CONSUMPTION`.
  5. Line completes stitching and sole pressing.
  6. Supervisor triggers completion (`POST /api/manufacturing/production-order/{poNo}/complete-traceable`).
  7. System consumes raw material stock, creates Finished Goods lot `FG-PO_EXPORT_001-01` in `WH_FG`, writes `MFG_CONSUME` and `MFG_OUTPUT` transactions, updates PO to `COMPLETED`, and links full genealogy.
- **Extensions:**
  - *3a. Duplicate Completion Attempt:* Supervisor clicks "Hoàn thành" a second time. System rejects duplicate action with `409 Conflict` (`ORA-20004`), preserving inventory integrity.

---

## UC-04: Quality Recall & Backward Genealogy Trace
- **Primary Actor:** Plant Quality Assurance Auditor (`auditor01` / `admin`)
- **Goal:** Trace defective finished goods batch back to its component materials and supplier purchase orders.
- **Trigger:** Export client reports adhesive failure on carton batch `FG-PO001-001`.
- **Main Success Scenario:**
  1. Auditor opens Dashboard $\rightarrow$ "Nhận hàng & Lots" $\rightarrow$ "Truy vết Cây Phả hệ".
  2. Auditor enters lot identifier `FG-PO001-001` and selects `direction=backward`.
  3. API queries `ERP_TRACEABILITY.get_lot_genealogy_backward`.
  4. System renders interactive tree:
     ```text
     FG-PO001-001 (Finished Goods)
       └── Production Order: PO001
             ├── Consumed: RM-RUBBER-001 (Qty: 50) <-- PO_PUR_901 (Supplier A)
             └── Consumed: RM-GLUE-002 (Qty: 10)   <-- PO_PUR_842 (Supplier B)
     ```
  5. Auditor identifies supplier lot `RM-GLUE-002` as the root cause.
  6. Auditor executes Forward Trace on `RM-GLUE-002` to discover all *other* production orders that used the same adhesive batch.
  7. Auditor issues quarantine hold (`POST /api/warehouse/lots/RM-GLUE-002/hold`).
