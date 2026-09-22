-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE: sql/04_incident_scenarios.sql
-- TARGET: Oracle Database 19c / 21c / 23c
-- DESCRIPTION: Real-world ERP Support & Troubleshooting Scenario
-- SCENARIO: Production order PO001 fails to complete due to raw material shortage.
--           IT ERP team investigates, traces ERROR_LOG, performs fix, and verifies.
-- ============================================================================

SET SERVEROUTPUT ON;
-- ----------------------------------------------------------------------------
-- STEP 0: Reset the scenario so this script is IDEMPOTENT (safe to re-run).
--         Only rows belonging to the PO001 demo are touched; the deliberately
--         short MAT_RUBBER_01 balance (40) is restored so the shortage
--         incident is reproduced on every run.
-- ----------------------------------------------------------------------------
DECLARE
  PROCEDURE safe_dml(p_stmt IN VARCHAR2) IS
  BEGIN
    EXECUTE IMMEDIATE p_stmt;
  EXCEPTION
    WHEN OTHERS THEN
      NULL; -- object/row may not exist on the very first run
  END safe_dml;
BEGIN
  safe_dml('DELETE FROM CHANGE_REQUEST WHERE CR_NO = ''CR-2026-0901''');
  safe_dml('DELETE FROM ERROR_LOG WHERE REF_NO IN (''PO001'', ''PO_PUR_901'')');
  safe_dml('DELETE FROM INVENTORY_TRANSACTION WHERE REF_NO IN (''PO001'', ''PO_PUR_901'')');
  safe_dml('DELETE FROM PURCHASE_ORDER WHERE PO_NO = ''PO_PUR_901''');
  safe_dml('DELETE FROM PRODUCTION_ORDER WHERE PO_NO = ''PO001''');
  safe_dml('DELETE FROM STOCK WHERE ITEM_ID = (SELECT ID FROM ITEM WHERE CODE = ''FG_RUNNER_PRO_42'')');

  UPDATE STOCK
     SET QTY = 40, UPDATED_AT = SYSDATE
   WHERE ITEM_ID = (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01')
     AND WAREHOUSE_ID = (SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW');
  IF SQL%ROWCOUNT = 0 THEN
    INSERT INTO STOCK (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
    VALUES ((SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW'),
            (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01'), 40, SYSDATE);
  END IF;
  COMMIT;
  DBMS_OUTPUT.PUT_LINE('>>> Step 0 SUCCESS: scenario reset, MAT_RUBBER_01 @ WH_RAW = 40');
END;
/


PROMPT ============================================================================
PROMPT SCENARIO STEP 1: Production Planner creates Production Order PO001 for 50 units
PROMPT ============================================================================

BEGIN
  ERP_OPERATIONS.create_production_order(
    p_po_no   => 'PO001',
    p_fg_code => 'FG_RUNNER_PRO_42',
    p_qty     => 50,
    p_wh_code => 'WH_RAW',
    p_user    => 'planner01'
  );
  DBMS_OUTPUT.PUT_LINE('>>> Step 1 SUCCESS: Created PO001 for 50 pairs of FG_RUNNER_PRO_42');
END;
/

PROMPT ============================================================================
PROMPT SCENARIO STEP 2: Workshop operator tries to complete PO001 -> EXPECT FAILURE!
PROMPT (MAT_RUBBER_01 stock is 40, but 50 are required according to BOM V1.0)
PROMPT ============================================================================

BEGIN
  ERP_OPERATIONS.complete_production_order(
    p_po_no => 'PO001',
    p_user  => 'workshop_lead'
  );
EXCEPTION
  WHEN OTHERS THEN
    DBMS_OUTPUT.PUT_LINE('>>> Step 2 EXPECTED ERROR CAUGHT (SQLCODE=' || SQLCODE || '): ' || SQLERRM);
    DBMS_OUTPUT.PUT_LINE('[ASSERT] S2_PO001_raises_ORA_minus_20007 -> ' ||
                         CASE WHEN SQLCODE = -20007 THEN 'PASS' ELSE 'FAIL' END);
END;
/

PROMPT ============================================================================
PROMPT SCENARIO STEP 3: IT ERP Support Specialist investigates the issue
PROMPT ============================================================================

-- Query the autonomous error log
SELECT ID, ERR_CODE, MESSAGE, PROC_NAME, REF_NO, CREATED_AT
FROM ERROR_LOG
WHERE REF_NO = 'PO001'
ORDER BY ID DESC;

-- Run diagnostic query comparing BOM requirements vs actual stock
SELECT 
  it.CODE AS MAT_CODE,
  it.NAME AS MAT_NAME,
  bd.QTY_REQUIRED * po.QTY_PLANNED AS REQUIRED_QTY,
  NVL(st.QTY, 0) AS CURRENT_STOCK,
  CASE 
    WHEN NVL(st.QTY, 0) < (bd.QTY_REQUIRED * po.QTY_PLANNED) THEN 'DEFICIT / SHORTAGE'
    ELSE 'SUFFICIENT'
  END AS STATUS
FROM PRODUCTION_ORDER po
JOIN BOM bm ON po.FG_ITEM_ID = bm.FG_ITEM_ID AND bm.STATUS = 'ACTIVE'
JOIN BOM_DETAIL bd ON bm.ID = bd.BOM_ID
JOIN ITEM it ON bd.MAT_ITEM_ID = it.ID
LEFT JOIN STOCK st ON st.WAREHOUSE_ID = po.WAREHOUSE_ID AND st.ITEM_ID = it.ID
WHERE po.PO_NO = 'PO001';

PROMPT ============================================================================
PROMPT SCENARIO STEP 4: Create Change Request (CR) to document root cause and fix
PROMPT ============================================================================

INSERT INTO CHANGE_REQUEST (
  CR_NO, TITLE, REQ_TYPE, REF_NO, ROOT_CAUSE, FIX_ACTION, STATUS, REQUESTER
) VALUES (
  'CR-2026-0901',
  'Lệnh PO001 lỗi không thể complete do thiếu đế giày MAT_RUBBER_01',
  'INCIDENT',
  'PO001',
  'Tồn kho khả dụng chỉ còn 40 cặp, trong khi lệnh sản xuất yêu cầu 50 cặp. Nhà cung ứng đã giao lô hàng mới nhưng thủ kho chưa kịp nhập phiếu Purchase Order.',
  'Phối hợp với thủ kho thực hiện nhập kho lô hàng PO_PUR_901 (100 cặp MAT_RUBBER_01) qua procedure receive_purchase_order, sau đó chạy lại lệnh hoàn thành PO001.',
  'IN_PROGRESS',
  'tan.mai'
);
COMMIT;

PROMPT ============================================================================
PROMPT SCENARIO STEP 5: Execute Fix — Receive Purchase Order PO_PUR_901 for 100 units
PROMPT ============================================================================

-- Create and receive the pending purchase order
INSERT INTO PURCHASE_ORDER (PO_NO, ITEM_ID, QTY, WAREHOUSE_ID, STATUS)
VALUES (
  'PO_PUR_901',
  (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01'),
  100,
  (SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW'),
  'CREATED'
);
COMMIT;

BEGIN
  ERP_OPERATIONS.receive_purchase_order(
    p_po_no => 'PO_PUR_901',
    p_user  => 'warehouse01'
  );
  DBMS_OUTPUT.PUT_LINE('>>> Step 5 SUCCESS: Received 100 units of MAT_RUBBER_01 into WH_RAW');
END;
/

PROMPT ============================================================================
PROMPT SCENARIO STEP 6: Re-run complete_production_order — SHOULD SUCCEED!
PROMPT ============================================================================

BEGIN
  ERP_OPERATIONS.complete_production_order(
    p_po_no => 'PO001',
    p_user  => 'workshop_lead'
  );
  DBMS_OUTPUT.PUT_LINE('>>> Step 6 SUCCESS: PO001 completed successfully!');
END;
/

PROMPT ============================================================================
PROMPT SCENARIO STEP 7: Close Change Request & Verify Final State
PROMPT ============================================================================

UPDATE CHANGE_REQUEST
SET STATUS = 'RESOLVED', CLOSED_AT = SYSDATE
WHERE CR_NO = 'CR-2026-0901';
COMMIT;

-- Verify PO status
SELECT PO_NO, QTY_PLANNED, QTY_DONE, STATUS, CREATED_AT, COMPLETED_AT
FROM PRODUCTION_ORDER
WHERE PO_NO = 'PO001';

-- Verify stock of Finished Goods (FG_RUNNER_PRO_42)
SELECT w.CODE AS WH, i.CODE AS ITEM, s.QTY, s.UPDATED_AT
FROM STOCK s
JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
JOIN ITEM i ON s.ITEM_ID = i.ID
WHERE i.CODE = 'FG_RUNNER_PRO_42';

-- Verify audit trail
SELECT TXN_TYPE, QTY, BALANCE_AFTER, REF_NO, CREATED_BY, CREATED_AT
FROM INVENTORY_TRANSACTION
WHERE REF_NO IN ('PO001', 'PO_PUR_901')
ORDER BY ID ASC;

PROMPT ============================================================================
PROMPT SCENARIO ASSERTIONS (parsed by scripts/test-incident.sh)
PROMPT ============================================================================

DECLARE
  v_short_log_cnt NUMBER := 0;
  v_ora20007_cnt  NUMBER := 0;
  v_po_status     PRODUCTION_ORDER.STATUS%TYPE;
  v_po_done       PRODUCTION_ORDER.QTY_DONE%TYPE;
  v_fg_qty        NUMBER := 0;
  v_rubber_qty    NUMBER := 0;
  v_consume_cnt   NUMBER := 0;
  v_output_cnt    NUMBER := 0;
  v_recv_cnt      NUMBER := 0;
  v_cr_status     CHANGE_REQUEST.STATUS%TYPE;
  v_ledger_breaks NUMBER := 0;
  v_fail          NUMBER := 0;

  PROCEDURE check_it(p_name IN VARCHAR2, p_ok IN BOOLEAN) IS
  BEGIN
    DBMS_OUTPUT.PUT_LINE('[ASSERT] ' || p_name || ' -> ' ||
                         CASE WHEN p_ok THEN 'PASS' ELSE 'FAIL' END);
    IF NOT p_ok THEN
      v_fail := v_fail + 1;
    END IF;
  END check_it;
BEGIN
  SELECT COUNT(*) INTO v_short_log_cnt
    FROM ERROR_LOG WHERE REF_NO = 'PO001' AND ERR_CODE = 'ERR_MATERIAL_SHORTAGE';

  SELECT COUNT(*) INTO v_ora20007_cnt
    FROM ERROR_LOG WHERE REF_NO = 'PO001' AND ERR_CODE = '-20007';

  SELECT STATUS, QTY_DONE INTO v_po_status, v_po_done
    FROM PRODUCTION_ORDER WHERE PO_NO = 'PO001';

  SELECT NVL(SUM(QTY), 0) INTO v_fg_qty
    FROM STOCK WHERE ITEM_ID = (SELECT ID FROM ITEM WHERE CODE = 'FG_RUNNER_PRO_42');

  SELECT NVL(SUM(QTY), 0) INTO v_rubber_qty
    FROM STOCK
   WHERE ITEM_ID = (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01')
     AND WAREHOUSE_ID = (SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW');

  SELECT COUNT(*) INTO v_consume_cnt
    FROM INVENTORY_TRANSACTION WHERE REF_NO = 'PO001' AND TXN_TYPE = 'MFG_CONSUME';

  SELECT COUNT(*) INTO v_output_cnt
    FROM INVENTORY_TRANSACTION WHERE REF_NO = 'PO001' AND TXN_TYPE = 'MFG_OUTPUT';

  SELECT COUNT(*) INTO v_recv_cnt
    FROM INVENTORY_TRANSACTION WHERE REF_NO = 'PO_PUR_901' AND TXN_TYPE = 'STOCK_IN';

  SELECT STATUS INTO v_cr_status FROM CHANGE_REQUEST WHERE CR_NO = 'CR-2026-0901';

  DBMS_OUTPUT.PUT_LINE('[EVIDENCE] ERR_LOG_SHORTAGE=' || v_short_log_cnt ||
                       ' ERR_LOG_ORA20007=' || v_ora20007_cnt ||
                       ' PO_STATUS=' || v_po_status ||
                       ' QTY_DONE=' || v_po_done ||
                       ' FG_STOCK=' || v_fg_qty ||
                       ' RUBBER_STOCK=' || v_rubber_qty ||
                       ' CONSUME_TXN=' || v_consume_cnt ||
                       ' OUTPUT_TXN=' || v_output_cnt);

  -- A1/A2: the aborted attempt still left an autonomous error trail ...
  check_it('A1_autonomous_error_log_written_for_PO001', v_short_log_cnt >= 1);
  check_it('A2_ora_minus_20007_recorded_in_error_log', v_ora20007_cnt >= 1);
  -- ... while every business change of that attempt was rolled back (ACID):
  --     the invariant "STOCK.QTY == SUM(INVENTORY_TRANSACTION.QTY)" must still hold
  --     for every stock line, which is impossible if the aborted run had committed.
  SELECT COUNT(*) INTO v_ledger_breaks FROM (
    SELECT s.WAREHOUSE_ID, s.ITEM_ID, s.QTY,
           NVL((SELECT SUM(t.QTY) FROM INVENTORY_TRANSACTION t
                 WHERE t.WAREHOUSE_ID = s.WAREHOUSE_ID AND t.ITEM_ID = s.ITEM_ID), 0) AS LEDGER
      FROM STOCK s
  ) WHERE QTY <> LEDGER;
  check_it('A3_stock_ledger_reconciles_after_failed_attempt', v_ledger_breaks = 0);
  -- B: the purchase-order fix restored material availability (40 + 100 = 140).
  check_it('B_purchase_order_received_into_wh_raw', v_recv_cnt >= 1);
  -- C: the successful re-run completed the order and produced 50 FG pairs.
  check_it('C1_PO001_status_is_COMPLETED', v_po_status = 'COMPLETED');
  check_it('C2_PO001_qty_done_is_50', v_po_done = 50);
  check_it('C3_finished_goods_stock_is_50', v_fg_qty = 50);
  check_it('C4_rubber_balance_after_consumption_is_90', v_rubber_qty = 90);
  check_it('C5_audit_trail_5_consume_1_output', v_consume_cnt = 5 AND v_output_cnt = 1);
  -- D: the change request lifecycle was closed.
  check_it('D_change_request_resolved', v_cr_status = 'RESOLVED');

  IF v_fail = 0 THEN
    DBMS_OUTPUT.PUT_LINE('[RESULT] INCIDENT_SCENARIO = ALL_ASSERTIONS_PASSED');
  ELSE
    DBMS_OUTPUT.PUT_LINE('[RESULT] INCIDENT_SCENARIO = ' || v_fail || '_ASSERTIONS_FAILED');
  END IF;
END;
/
