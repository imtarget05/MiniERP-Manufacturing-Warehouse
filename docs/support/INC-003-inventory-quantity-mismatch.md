# Incident Runbook: INC-003 — Inventory Quantity Mismatch
**Project:** MiniERP Manufacturing & Warehouse  
**Incident Code:** INC-003  
**Severity:** P1 (Critical)  
**Target Component:** Inventory Engine / `STOCK` vs `INVENTORY_TRANSACTION`  

---

## 1. Symptoms & Incident Report
- **Reported By:** Internal Auditor during bi-weekly physical cycle count.
- **Symptom:** Physical count of `MAT_RUBBER_01` in `WH_RAW` is 340 pairs, while spreadsheet or report shows 350 pairs (Variance: -10 pairs).
- **Impact:** Misstated inventory value and potential line stoppage if uncorrected.

---

## 2. Investigation Protocol & Diagnostic Steps

1. **Query Current Snapshot in `STOCK` Table:**
   ```sql
   SELECT w.CODE AS WAREHOUSE, i.CODE AS ITEM, s.QTY, s.UPDATED_AT
     FROM STOCK s
     JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
     JOIN ITEM i ON s.ITEM_ID = i.ID
    WHERE w.CODE = 'WH_RAW' AND i.CODE = 'MAT_RUBBER_01';
   ```
2. **Reconstruct Transactional History from `INVENTORY_TRANSACTION`:**
   ```sql
   SELECT ID, TXN_TYPE, QTY, BALANCE_AFTER, REF_NO, CREATED_BY, CREATED_AT
     FROM INVENTORY_TRANSACTION
    WHERE WAREHOUSE_ID = (SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW')
      AND ITEM_ID = (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01')
    ORDER BY CREATED_AT ASC, ID ASC;
   ```
3. **Audit Mathematical Continuity:**
   $$\text{Running Balance}_{n} = \text{Running Balance}_{n-1} + Q_n$$
   *Finding:* At 14:15, a production order `PO001` completed, consuming 10 soles with `TXN_TYPE = 'MFG_CONSUME'`, but the floor report was printed prior to `PO001` completion.

---

## 3. Root Cause Analysis (RCA)
No data corruption occurred in the database. The discrepancy was caused by an asynchronous reporting delay: the floor supervisor executed physical cycle counting at 14:10, while `PO001` was completed on the ERP system at 14:15. Physical goods were staged for line stitching but had not physically cleared the dock aisle.

---

## 4. Resolution Procedure

1. **Reconciliation Confirmation:**
   - Confirm that the physical shoes on the stitching line contain the missing 10 pairs of soles.
   - Total physical inventory (Warehouse Floor + WIP Line) = $340 + 10 = 350$, exactly matching the ERP transaction ledger.
2. **Issue Resolution Certificate:**
   - Document the timing offset in the cycle count audit sheet and close the ticket with status `RESOLVED - TIMING_VARIANCE`.

---

## 5. Prevention & System Hardening
- Implement WIP staging warehouse (`WH_WIP`) location tracking so issued materials immediately move out of `WH_RAW` into a visible line-side location (`WH_WIP`).
- Timestamp all printed cycle count sheets with database server time.
