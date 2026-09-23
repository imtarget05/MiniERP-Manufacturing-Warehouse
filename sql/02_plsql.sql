-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE: sql/02_plsql.sql
-- TARGET: Oracle Database 19c / 21c / 23c
-- DESCRIPTION: PL/SQL Package ERP_OPERATIONS (Business logic, transactions, 
--              BOM explosion, stock reservation, error logging)
-- ============================================================================

CREATE OR REPLACE PACKAGE ERP_OPERATIONS AS

  -- Function to check current stock availability
  FUNCTION get_current_stock (
    p_warehouse_id IN NUMBER,
    p_item_id      IN NUMBER
  ) RETURN NUMBER;

  -- Autonomous procedure to record operational errors without rolling back with transaction
  PROCEDURE log_error (
    p_err_code IN VARCHAR2,
    p_message  IN VARCHAR2,
    p_proc     IN VARCHAR2,
    p_ref      IN VARCHAR2,
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- Warehouse operations
  PROCEDURE create_stock_in (
    p_wh_code   IN VARCHAR2,
    p_item_code IN VARCHAR2,
    p_qty       IN NUMBER,
    p_ref_no    IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  );

  PROCEDURE create_stock_out (
    p_wh_code   IN VARCHAR2,
    p_item_code IN VARCHAR2,
    p_qty       IN NUMBER,
    p_ref_no    IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  );

  -- BOM Management
  PROCEDURE save_bom_line (
    p_fg_code   IN VARCHAR2,
    p_version   IN VARCHAR2,
    p_mat_code  IN VARCHAR2,
    p_qty_req   IN NUMBER,
    p_user      IN VARCHAR2 DEFAULT 'system'
  );

  -- Manufacturing operations
  PROCEDURE create_production_order (
    p_po_no     IN VARCHAR2,
    p_fg_code   IN VARCHAR2,
    p_qty       IN NUMBER,
    p_wh_code   IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  );

  -- Complete production order (Explodes BOM, validates inventory, consumes RAW, outputs FG)
  PROCEDURE complete_production_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

  -- Cancel production order
  PROCEDURE cancel_production_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

  -- Purchase receiving
  PROCEDURE receive_purchase_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

END ERP_OPERATIONS;
/

CREATE OR REPLACE PACKAGE BODY ERP_OPERATIONS AS

  -----------------------------------------------------------------------------
  -- FUNCTION: get_current_stock
  -----------------------------------------------------------------------------
  FUNCTION get_current_stock (
    p_warehouse_id IN NUMBER,
    p_item_id      IN NUMBER
  ) RETURN NUMBER IS
    v_qty NUMBER := 0;
  BEGIN
    SELECT NVL(QTY, 0) INTO v_qty
    FROM STOCK
    WHERE WAREHOUSE_ID = p_warehouse_id AND ITEM_ID = p_item_id;
    RETURN v_qty;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RETURN 0;
  END get_current_stock;

  -----------------------------------------------------------------------------
  -- PROCEDURE: log_error (Autonomous Transaction)
  -----------------------------------------------------------------------------
  PROCEDURE log_error (
    p_err_code IN VARCHAR2,
    p_message  IN VARCHAR2,
    p_proc     IN VARCHAR2,
    p_ref      IN VARCHAR2,
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    INSERT INTO ERROR_LOG (ERR_CODE, MESSAGE, PROC_NAME, REF_NO, CREATED_BY, CREATED_AT)
    VALUES (p_err_code, SUBSTR(p_message, 1, 1000), p_proc, p_ref, p_user, SYSDATE);
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
  END log_error;

  -----------------------------------------------------------------------------
  -- PROCEDURE: create_stock_in
  -----------------------------------------------------------------------------
  PROCEDURE create_stock_in (
    p_wh_code   IN VARCHAR2,
    p_item_code IN VARCHAR2,
    p_qty       IN NUMBER,
    p_ref_no    IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_wh_id   NUMBER;
    v_item_id NUMBER;
    v_new_qty NUMBER;
  BEGIN
    IF p_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Quantity for stock-in must be greater than 0.');
    END IF;

    SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = p_wh_code AND IS_ACTIVE = 1;
    SELECT ID INTO v_item_id FROM ITEM WHERE CODE = p_item_code;

    -- Upsert stock
    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS wid, v_item_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + p_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_wh_id, v_item_id, p_qty, SYSDATE);

    -- Retrieve balance after
    SELECT QTY INTO v_new_qty FROM STOCK WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id;

    -- Audit trail
    INSERT INTO INVENTORY_TRANSACTION (
      TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
    ) VALUES (
      'STOCK_IN', v_item_id, v_wh_id, p_qty, v_new_qty, p_ref_no, p_user
    );

    COMMIT;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      ROLLBACK;
      log_error('ERR_NOT_FOUND', 'Warehouse or Item not found: ' || p_wh_code || ' / ' || p_item_code, 'create_stock_in', p_ref_no, p_user);
      RAISE_APPLICATION_ERROR(-20002, 'Warehouse or Item code not found.');
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'create_stock_in', p_ref_no, p_user);
      RAISE;
  END create_stock_in;

  -----------------------------------------------------------------------------
  -- PROCEDURE: create_stock_out
  -----------------------------------------------------------------------------
  PROCEDURE create_stock_out (
    p_wh_code   IN VARCHAR2,
    p_item_code IN VARCHAR2,
    p_qty       IN NUMBER,
    p_ref_no    IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_wh_id       NUMBER;
    v_item_id     NUMBER;
    v_current_qty NUMBER;
    v_new_qty     NUMBER;
  BEGIN
    IF p_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Quantity for stock-out must be greater than 0.');
    END IF;

    SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = p_wh_code;
    SELECT ID INTO v_item_id FROM ITEM WHERE CODE = p_item_code;

    -- Check availability with row lock
    BEGIN
      SELECT QTY INTO v_current_qty
      FROM STOCK
      WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id
      FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        v_current_qty := 0;
    END;

    IF v_current_qty < p_qty THEN
      log_error('ERR_INSUFFICIENT_STOCK', 
        'Stock-out failed. Available: ' || v_current_qty || ', Requested: ' || p_qty, 
        'create_stock_out', p_ref_no, p_user);
      RAISE_APPLICATION_ERROR(-20003, 'Insufficient stock in warehouse: available ' || v_current_qty || ', requested ' || p_qty);
    END IF;

    UPDATE STOCK
    SET QTY = QTY - p_qty, UPDATED_AT = SYSDATE
    WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id;

    v_new_qty := v_current_qty - p_qty;

    INSERT INTO INVENTORY_TRANSACTION (
      TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
    ) VALUES (
      'STOCK_OUT', v_item_id, v_wh_id, -p_qty, v_new_qty, p_ref_no, p_user
    );

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'create_stock_out', p_ref_no, p_user);
      RAISE;
  END create_stock_out;

  -----------------------------------------------------------------------------
  -- PROCEDURE: save_bom_line
  -----------------------------------------------------------------------------
  PROCEDURE save_bom_line (
    p_fg_code   IN VARCHAR2,
    p_version   IN VARCHAR2,
    p_mat_code  IN VARCHAR2,
    p_qty_req   IN NUMBER,
    p_user      IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_fg_id  NUMBER;
    v_mat_id NUMBER;
    v_bom_id NUMBER;
  BEGIN
    SELECT ID INTO v_fg_id FROM ITEM WHERE CODE = p_fg_code AND ITEM_TYPE = 'FG';
    SELECT ID INTO v_mat_id FROM ITEM WHERE CODE = p_mat_code AND ITEM_TYPE = 'RAW';

    -- Ensure BOM header exists
    BEGIN
      SELECT ID INTO v_bom_id FROM BOM WHERE FG_ITEM_ID = v_fg_id AND VERSION = p_version;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        INSERT INTO BOM (FG_ITEM_ID, VERSION, DESCRIPTION, STATUS)
        VALUES (v_fg_id, p_version, 'Standard BOM for ' || p_fg_code, 'ACTIVE')
        RETURNING ID INTO v_bom_id;
    END;

    -- Upsert BOM detail line
    MERGE INTO BOM_DETAIL bd
    USING (SELECT v_bom_id AS bid, v_mat_id AS mid FROM DUAL) d
    ON (bd.BOM_ID = d.bid AND bd.MAT_ITEM_ID = d.mid)
    WHEN MATCHED THEN
      UPDATE SET bd.QTY_REQUIRED = p_qty_req
    WHEN NOT MATCHED THEN
      INSERT (BOM_ID, MAT_ITEM_ID, QTY_REQUIRED)
      VALUES (v_bom_id, v_mat_id, p_qty_req);

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'save_bom_line', p_fg_code || '-' || p_version, p_user);
      RAISE;
  END save_bom_line;

  -----------------------------------------------------------------------------
  -- PROCEDURE: create_production_order
  -----------------------------------------------------------------------------
  PROCEDURE create_production_order (
    p_po_no     IN VARCHAR2,
    p_fg_code   IN VARCHAR2,
    p_qty       IN NUMBER,
    p_wh_code   IN VARCHAR2,
    p_user      IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_fg_id NUMBER;
    v_wh_id NUMBER;
  BEGIN
    SELECT ID INTO v_fg_id FROM ITEM WHERE CODE = p_fg_code AND ITEM_TYPE = 'FG';
    SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = p_wh_code;

    INSERT INTO PRODUCTION_ORDER (PO_NO, FG_ITEM_ID, QTY_PLANNED, STATUS, WAREHOUSE_ID)
    VALUES (p_po_no, v_fg_id, p_qty, 'RELEASED', v_wh_id);

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'create_production_order', p_po_no, p_user);
      RAISE;
  END create_production_order;

  -----------------------------------------------------------------------------
  -- PROCEDURE: complete_production_order
  -- Core ERP logic: checks all BOM components, deducts RAW, adds FG, logs all
  -----------------------------------------------------------------------------
  PROCEDURE complete_production_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_po_id       NUMBER;
    v_fg_id       NUMBER;
    v_planned_qty NUMBER;
    v_status      VARCHAR2(20);
    v_wh_id       NUMBER;
    v_bom_id      NUMBER;
    v_shortage    BOOLEAN := FALSE;
    v_short_msg   VARCHAR2(1000) := '';
    v_cur_stock   NUMBER;
    v_new_bal     NUMBER;

    -- Cursor to iterate over BOM details for the Finished Good
    CURSOR c_bom_lines(b_id NUMBER) IS
      SELECT bd.MAT_ITEM_ID, it.CODE AS MAT_CODE, bd.QTY_REQUIRED
      FROM BOM_DETAIL bd
      JOIN ITEM it ON bd.MAT_ITEM_ID = it.ID
      WHERE bd.BOM_ID = b_id;

  BEGIN
    -- 1. Lock and check Production Order
    SELECT ID, FG_ITEM_ID, QTY_PLANNED, STATUS, WAREHOUSE_ID
    INTO v_po_id, v_fg_id, v_planned_qty, v_status, v_wh_id
    FROM PRODUCTION_ORDER
    WHERE PO_NO = p_po_no
    FOR UPDATE;

    IF v_status = 'COMPLETED' THEN
      RAISE_APPLICATION_ERROR(-20004, 'Production Order ' || p_po_no || ' is already completed.');
    ELSIF v_status = 'CANCELLED' THEN
      RAISE_APPLICATION_ERROR(-20005, 'Production Order ' || p_po_no || ' is cancelled.');
    END IF;

    -- 2. Find Active BOM
    BEGIN
      SELECT ID INTO v_bom_id
      FROM BOM
      WHERE FG_ITEM_ID = v_fg_id AND STATUS = 'ACTIVE' AND ROWNUM = 1;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        log_error('ERR_NO_ACTIVE_BOM', 'No active BOM found for FG item ID ' || v_fg_id, 'complete_production_order', p_po_no, p_user);
        RAISE_APPLICATION_ERROR(-20006, 'No active BOM found for product.');
    END;

    -- 3. Pre-flight check: verify all raw materials have sufficient inventory
    FOR line IN c_bom_lines(v_bom_id) LOOP
      DECLARE
        v_needed NUMBER := line.QTY_REQUIRED * v_planned_qty;
      BEGIN
        SELECT NVL(QTY, 0) INTO v_cur_stock
        FROM STOCK
        WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = line.MAT_ITEM_ID;
      EXCEPTION
        WHEN NO_DATA_FOUND THEN
          v_cur_stock := 0;
      END;

      IF v_cur_stock < (line.QTY_REQUIRED * v_planned_qty) THEN
        v_shortage := TRUE;
        v_short_msg := v_short_msg || 'Material ' || line.MAT_CODE || 
                       ' requires ' || (line.QTY_REQUIRED * v_planned_qty) || 
                       ', available ' || v_cur_stock || '; ';
      END IF;
    END LOOP;

    -- If shortage detected, log to ERROR_LOG and abort transaction
    IF v_shortage THEN
      log_error('ERR_MATERIAL_SHORTAGE', v_short_msg, 'complete_production_order', p_po_no, p_user);
      RAISE_APPLICATION_ERROR(-20007, 'Cannot complete PO ' || p_po_no || ' due to material shortage: ' || v_short_msg);
    END IF;

    -- 4. Consume materials (Stock out of RAW materials)
    FOR line IN c_bom_lines(v_bom_id) LOOP
      DECLARE
        v_consume_qty NUMBER := line.QTY_REQUIRED * v_planned_qty;
      BEGIN
        UPDATE STOCK
        SET QTY = QTY - v_consume_qty, UPDATED_AT = SYSDATE
        WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = line.MAT_ITEM_ID;

        SELECT QTY INTO v_new_bal
        FROM STOCK
        WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = line.MAT_ITEM_ID;

        INSERT INTO INVENTORY_TRANSACTION (
          TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
        ) VALUES (
          'MFG_CONSUME', line.MAT_ITEM_ID, v_wh_id, -v_consume_qty, v_new_bal, p_po_no, p_user
        );
      END;
    END LOOP;

    -- 5. Output Finished Goods (Stock in of FG)
    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS wid, v_fg_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + v_planned_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_wh_id, v_fg_id, v_planned_qty, SYSDATE);

    SELECT QTY INTO v_new_bal FROM STOCK WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_fg_id;

    INSERT INTO INVENTORY_TRANSACTION (
      TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
    ) VALUES (
      'MFG_OUTPUT', v_fg_id, v_wh_id, v_planned_qty, v_new_bal, p_po_no, p_user
    );

    -- 6. Update PO status
    UPDATE PRODUCTION_ORDER
    SET STATUS = 'COMPLETED', QTY_DONE = v_planned_qty, COMPLETED_AT = SYSDATE
    WHERE ID = v_po_id;

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'complete_production_order', p_po_no, p_user);
      RAISE;
  END complete_production_order;

  -----------------------------------------------------------------------------
  -- PROCEDURE: cancel_production_order
  -----------------------------------------------------------------------------
  PROCEDURE cancel_production_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
  BEGIN
    UPDATE PRODUCTION_ORDER
    SET STATUS = 'CANCELLED'
    WHERE PO_NO = p_po_no AND STATUS IN ('CREATED', 'RELEASED');

    IF SQL%ROWCOUNT = 0 THEN
      RAISE_APPLICATION_ERROR(-20008, 'PO not found or already completed/cancelled.');
    END IF;

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'cancel_production_order', p_po_no, p_user);
      RAISE;
  END cancel_production_order;

  -----------------------------------------------------------------------------
  -- PROCEDURE: receive_purchase_order
  -----------------------------------------------------------------------------
  PROCEDURE receive_purchase_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_item_id NUMBER;
    v_qty     NUMBER;
    v_wh_id   NUMBER;
    v_status  VARCHAR2(20);
    v_new_bal NUMBER;
  BEGIN
    SELECT ITEM_ID, QTY, WAREHOUSE_ID, STATUS
    INTO v_item_id, v_qty, v_wh_id, v_status
    FROM PURCHASE_ORDER
    WHERE PO_NO = p_po_no
    FOR UPDATE;

    IF v_status != 'CREATED' THEN
      RAISE_APPLICATION_ERROR(-20009, 'Purchase Order ' || p_po_no || ' cannot be received (status: ' || v_status || ').');
    END IF;

    -- Upsert stock
    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS wid, v_item_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + v_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_wh_id, v_item_id, v_qty, SYSDATE);

    SELECT QTY INTO v_new_bal FROM STOCK WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id;

    INSERT INTO INVENTORY_TRANSACTION (
      TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY
    ) VALUES (
      'STOCK_IN', v_item_id, v_wh_id, v_qty, v_new_bal, p_po_no, p_user
    );

    UPDATE PURCHASE_ORDER
    SET STATUS = 'RECEIVED', RECEIVED_AT = SYSDATE
    WHERE PO_NO = p_po_no;

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      log_error(SQLCODE, SQLERRM, 'receive_purchase_order', p_po_no, p_user);
      RAISE;
  END receive_purchase_order;

END ERP_OPERATIONS;
/
