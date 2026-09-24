-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE: sql/08_traceability_plsql.sql
-- TARGET: Oracle Database 19c / 21c / 23c
-- DESCRIPTION: Package ERP_TRACEABILITY - Phase 1 skeleton. Provides the
--              package entry point plus idempotent location provisioning.
--              Phase 2 extends this file with receive/putaway/move/hold and
--              Phase 3 with allocate/issue/complete-traceable. Existing
--              ERP_OPERATIONS stays the only owner of aggregate STOCK writes
--              until the Phase 2/3 procedures below are fully verified.
--              Loaded by scripts/run-sql.sh; spec + body must compile VALID.
-- ============================================================================

CREATE OR REPLACE PACKAGE ERP_TRACEABILITY AS

  -- Package version for health/diagnostics probes.
  FUNCTION package_version RETURN VARCHAR2;

  -- Idempotent location provisioning: creates the bin when missing,
  -- reactivates it when present but inactive. Validates LOCATION_TYPE.
  PROCEDURE ensure_location (
    p_wh_code  IN VARCHAR2,
    p_loc_code IN VARCHAR2,
    p_loc_name IN VARCHAR2 DEFAULT NULL,
    p_loc_type IN VARCHAR2 DEFAULT 'BIN',
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- Idempotency probe: 1 = key already processed with the same hash
  -- (caller treats it as a replay, no-op); 0 = new key. Raises ORA-20013
  -- when the same key carries a different payload hash.
  FUNCTION idem_replay (
    p_key  IN VARCHAR2,
    p_hash IN VARCHAR2
  ) RETURN NUMBER;

  -- Receiving with lot capture (Phase 2). Validates the purchase-order line
  -- (PO must exist, item must match, qty must not exceed the PO qty) without
  -- changing the legacy PO status flow owned by receive_purchase_order.
  PROCEDURE receive_lot_stock (
    p_po_no        IN VARCHAR2,
    p_item_code    IN VARCHAR2,
    p_qty          IN NUMBER,
    p_lot_code     IN VARCHAR2,
    p_supplier_lot IN VARCHAR2 DEFAULT NULL,
    p_mfg          IN DATE DEFAULT NULL,
    p_expiry       IN DATE DEFAULT NULL,
    p_loc_code     IN VARCHAR2 DEFAULT NULL,
    p_idem_key     IN VARCHAR2 DEFAULT NULL,
    p_req_hash     IN VARCHAR2 DEFAULT NULL,
    p_user         IN VARCHAR2 DEFAULT 'system'
  );

  -- Put-away: relocate lot qty between locations of ONE warehouse.
  -- The warehouse total is unchanged; only the location split moves.
  PROCEDURE putaway_lot (
    p_lot_code IN VARCHAR2,
    p_from_loc IN VARCHAR2,
    p_to_loc   IN VARCHAR2,
    p_qty      IN NUMBER,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- Move: relocate lot qty across warehouses (both aggregates updated).
  PROCEDURE move_lot_stock (
    p_lot_code IN VARCHAR2,
    p_from_wh  IN VARCHAR2,
    p_from_loc IN VARCHAR2,
    p_to_wh    IN VARCHAR2,
    p_to_loc   IN VARCHAR2,
    p_qty      IN NUMBER,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- Hold / release / block a lot. Only ACTIVE lots may be issued;
  -- CONSUMED and EXPIRED lots can no longer change state.
  PROCEDURE set_lot_hold (
    p_lot_code IN VARCHAR2,
    p_status   IN VARCHAR2,
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- FEFO/FIFO allocation preview for a production order: for every BOM line
  -- of a LOT-tracked material, walk issuable lots (ACTIVE, not expired,
  -- earliest expiry first, then earliest created) and report the split.
  -- Read-only: no reservation, no stock change.
  PROCEDURE allocate_lots_preview (
    p_po_no IN VARCHAR2,
    p_cur   OUT SYS_REFCURSOR
  );

  -- Issue exact material lots to a production order (consumes lot stock,
  -- mirrors aggregate STOCK, writes MFG_CONSUME txns + ISSUE events +
  -- PRODUCTION_LOT_CONSUMPTION genealogy rows, one tx).
  PROCEDURE issue_lot_to_production (
    p_po_no    IN VARCHAR2,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  );

  -- Traceable completion: requires every LOT-tracked BOM line fully issued
  -- (within the tolerance below), creates/validates the FG lot, writes the
  -- FG LOT_STOCK + MFG_OUTPUT txn + PRODUCTION_OUTPUT event +
  -- PRODUCTION_LOT_OUTPUT link, consumes the NON-lot BOM lines from STOCK
  -- itself and flips the order to COMPLETED — all in one transaction.
  -- P_TOLERANCE: allowed shortfall fraction (0 = exact, 0.02 = 2%).
  -- P_OUTPUT_WH: optional output warehouse; defaults to the production order's
  -- warehouse. When omitted, P_LOC_CODE must identify a location in that warehouse.
  PROCEDURE complete_production_traceable (
    p_po_no     IN VARCHAR2,
    p_fg_lot    IN VARCHAR2 DEFAULT NULL,
    p_qty       IN NUMBER DEFAULT NULL,
    p_loc_code  IN VARCHAR2 DEFAULT NULL,
    p_output_wh IN VARCHAR2 DEFAULT NULL,
    p_tolerance IN NUMBER DEFAULT 0,
    p_idem_key  IN VARCHAR2 DEFAULT NULL,
    p_req_hash  IN VARCHAR2 DEFAULT NULL,
    p_user      IN VARCHAR2 DEFAULT 'system'
  );

  -- Phase 4: label print job. Reprint-safe by design — inserts one
  -- LABEL_PRINT_JOB audit row per print and NEVER touches INVENTORY_LOT or
  -- STOCK, so reprinting a label can never create a lot or move inventory.
  PROCEDURE create_label_job (
    p_entity_type IN VARCHAR2,          -- LOT | ITEM | LOCATION | PRODUCTION_ORDER
    p_entity_key  IN VARCHAR2,
    p_label_type  IN VARCHAR2,          -- RAW_MATERIAL | FINISHED_GOOD | LOCATION
    p_copies      IN NUMBER   DEFAULT 1,
    p_format      IN VARCHAR2 DEFAULT 'HTML',  -- HTML | ZPL
    p_user        IN VARCHAR2 DEFAULT 'system',
    p_job_id      OUT NUMBER
  );

END ERP_TRACEABILITY;
/

CREATE OR REPLACE PACKAGE BODY ERP_TRACEABILITY AS

  -- Private helpers (not in the spec): idempotency guard + marker.
  PROCEDURE check_idem (
    p_key  IN VARCHAR2,
    p_hash IN VARCHAR2,
    p_op   IN VARCHAR2
  );
  PROCEDURE mark_idem (
    p_key   IN VARCHAR2,
    p_op    IN VARCHAR2,
    p_hash  IN VARCHAR2,
    p_code  IN NUMBER DEFAULT 200,
    p_body  IN VARCHAR2 DEFAULT '{"success":true}'
  );

  FUNCTION package_version RETURN VARCHAR2 IS
  BEGIN
    RETURN '1.3.0-phase4';
  END package_version;

  -- Idempotency guard shared by every stock-affecting traceability write.
  -- Same (key, hash): raises ORA-20014 DUPLICATE_REPLAY (caller no-ops).
  -- Same key + other hash: raises ORA-20013 conflict.
  PROCEDURE check_idem (
    p_key  IN VARCHAR2,
    p_hash IN VARCHAR2,
    p_op   IN VARCHAR2
  ) IS
    v_hash VARCHAR2(128);
  BEGIN
    IF p_key IS NULL THEN
      RAISE_APPLICATION_ERROR(-20012, 'Idempotency key is required for ' || p_op || '.');
    END IF;
    IF p_hash IS NULL THEN
      RAISE_APPLICATION_ERROR(-20012, 'Request hash is required for ' || p_op || '.');
    END IF;
    BEGIN
      SELECT REQUEST_HASH INTO v_hash FROM REQUEST_IDEMPOTENCY
       WHERE IDEMPOTENCY_KEY = p_key FOR UPDATE;
      IF v_hash != p_hash THEN
        RAISE_APPLICATION_ERROR(-20013,
          'Idempotency conflict for ' || p_op || ': key reused with different payload.');
      END IF;
      RAISE_APPLICATION_ERROR(-20014, 'DUPLICATE_REPLAY');
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        NULL; -- first attempt with this key: proceed
    END;
  END check_idem;

  PROCEDURE mark_idem (
    p_key   IN VARCHAR2,
    p_op    IN VARCHAR2,
    p_hash  IN VARCHAR2,
    p_code  IN NUMBER DEFAULT 200,
    p_body  IN VARCHAR2 DEFAULT '{"success":true}'
  ) IS
  BEGIN
    INSERT INTO REQUEST_IDEMPOTENCY
      (IDEMPOTENCY_KEY, OPERATION, REQUEST_HASH, RESPONSE_CODE, RESPONSE_BODY)
    VALUES (p_key, p_op, p_hash, p_code, p_body);
  END mark_idem;

  FUNCTION idem_replay (
    p_key  IN VARCHAR2,
    p_hash IN VARCHAR2
  ) RETURN NUMBER IS
    v_hash VARCHAR2(128);
  BEGIN
    SELECT REQUEST_HASH INTO v_hash FROM REQUEST_IDEMPOTENCY
     WHERE IDEMPOTENCY_KEY = p_key;
    IF v_hash = p_hash THEN
      RETURN 1;
    END IF;
    RAISE_APPLICATION_ERROR(-20013, 'Idempotency conflict: key reused with different payload.');
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RETURN 0;
  END idem_replay;


  PROCEDURE ensure_location (
    p_wh_code  IN VARCHAR2,
    p_loc_code IN VARCHAR2,
    p_loc_name IN VARCHAR2 DEFAULT NULL,
    p_loc_type IN VARCHAR2 DEFAULT 'BIN',
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_wh_id  NUMBER;
    v_type   VARCHAR2(15);
  BEGIN
    IF p_wh_code IS NULL OR p_loc_code IS NULL THEN
      RAISE_APPLICATION_ERROR(-20012, 'Warehouse code and location code are required.');
    END IF;

    v_type := UPPER(TRIM(p_loc_type));
    IF v_type NOT IN ('BIN','STAGING','RECEIVING','SHIPPING','LINE') THEN
      RAISE_APPLICATION_ERROR(-20012, 'Invalid LOCATION_TYPE: ' || p_loc_type);
    END IF;

    SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = UPPER(TRIM(p_wh_code));
    IF SQL%NOTFOUND THEN
      RAISE_APPLICATION_ERROR(-20002, 'Warehouse not found: ' || p_wh_code);
    END IF;

    MERGE INTO WAREHOUSE_LOCATION d
    USING (SELECT v_wh_id AS wid, UPPER(TRIM(p_loc_code)) AS lcode FROM DUAL) s
    ON (d.WAREHOUSE_ID = s.wid AND d.LOCATION_CODE = s.lcode)
    WHEN MATCHED THEN
      UPDATE SET d.LOCATION_NAME = NVL(p_loc_name, d.LOCATION_NAME),
                 d.LOCATION_TYPE = v_type,
                 d.IS_ACTIVE = 1
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, LOCATION_CODE, LOCATION_NAME, LOCATION_TYPE, IS_ACTIVE)
      VALUES (v_wh_id, UPPER(TRIM(p_loc_code)), p_loc_name, v_type, 1);

    COMMIT;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      ROLLBACK;
      ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'ensure_location', p_loc_code, p_user);
      RAISE_APPLICATION_ERROR(-20002, 'Warehouse not found: ' || p_wh_code);
    WHEN OTHERS THEN
      ROLLBACK;
      ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'ensure_location', p_loc_code, p_user);
      RAISE;
  END ensure_location;

  -- ==== Phase 2: receive/putaway/move/hold ====

  -- Receiving: lot capture validated against the PO line, then
  -- lot stock + aggregate STOCK + INVENTORY_TRANSACTION + TRACE event
  -- written in ONE transaction. A PO line may arrive in several lots;
  -- the sum of received lots must never exceed the PO qty.
  PROCEDURE receive_lot_stock (
    p_po_no        IN VARCHAR2,
    p_item_code    IN VARCHAR2,
    p_qty          IN NUMBER,
    p_lot_code     IN VARCHAR2,
    p_supplier_lot IN VARCHAR2 DEFAULT NULL,
    p_mfg          IN DATE DEFAULT NULL,
    p_expiry       IN DATE DEFAULT NULL,
    p_loc_code     IN VARCHAR2 DEFAULT NULL,
    p_idem_key     IN VARCHAR2 DEFAULT NULL,
    p_req_hash     IN VARCHAR2 DEFAULT NULL,
    p_user         IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_item_id  NUMBER;
    v_wh_id    NUMBER;
    v_loc_id   NUMBER;
    v_po_qty   NUMBER;
    v_po_item  NUMBER;
    v_got      NUMBER;
    v_lot_id   NUMBER;
    v_lot_item NUMBER;
    v_new_bal  NUMBER;
    v_ev_id    NUMBER;
  BEGIN
    IF p_qty IS NULL OR p_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Receive quantity must be > 0.');
    END IF;
    IF p_lot_code IS NULL THEN
      RAISE_APPLICATION_ERROR(-20012, 'Lot code is required.');
    END IF;
    IF p_mfg IS NOT NULL AND p_expiry IS NOT NULL AND p_expiry < p_mfg THEN
      RAISE_APPLICATION_ERROR(-20012, 'Expiry must be on/after MFG date.');
    END IF;

    check_idem(p_idem_key, p_req_hash, 'RECEIVE_LOT');

    SELECT ID INTO v_item_id FROM ITEM WHERE CODE = p_item_code;

    BEGIN
      SELECT LOT_ID, ITEM_ID INTO v_lot_id, v_lot_item FROM INVENTORY_LOT
       WHERE LOT_CODE = UPPER(TRIM(p_lot_code)) FOR UPDATE;
      IF v_lot_item != v_item_id THEN
        RAISE_APPLICATION_ERROR(-20012,
          'Lot ' || p_lot_code || ' belongs to another item.');
      END IF;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        INSERT INTO INVENTORY_LOT
          (LOT_CODE, ITEM_ID, SUPPLIER_LOT_NO, SOURCE_TYPE, SOURCE_REF_NO,
           MFG_DATE, EXPIRY_DATE, STATUS, CREATED_BY)
        VALUES (UPPER(TRIM(p_lot_code)), v_item_id, p_supplier_lot,
                'PURCHASE', p_po_no, p_mfg, p_expiry, 'ACTIVE', p_user)
        RETURNING LOT_ID INTO v_lot_id;
    END;

    SELECT ITEM_ID, QTY, WAREHOUSE_ID INTO v_po_item, v_po_qty, v_wh_id
      FROM PURCHASE_ORDER WHERE PO_NO = p_po_no FOR UPDATE;
    IF v_po_item != v_item_id THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Item ' || p_item_code || ' mismatches PO ' || p_po_no || '.');
    END IF;
    SELECT NVL(SUM(s.QTY_ON_HAND), 0) INTO v_got FROM LOT_STOCK s
      JOIN INVENTORY_LOT l ON s.LOT_ID = l.LOT_ID
     WHERE l.SOURCE_REF_NO = p_po_no AND l.ITEM_ID = v_item_id;
    IF v_got + p_qty > v_po_qty THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Receive exceeds PO ' || p_po_no || ' (ordered ' || v_po_qty ||
        ', got ' || v_got || ').');
    END IF;

    IF p_loc_code IS NULL THEN
      SELECT ID INTO v_loc_id FROM (
        SELECT ID FROM WAREHOUSE_LOCATION
         WHERE WAREHOUSE_ID = v_wh_id AND IS_ACTIVE = 1
         ORDER BY CASE LOCATION_TYPE WHEN 'RECEIVING' THEN 0 ELSE 1 END,
                  LOCATION_CODE
      ) WHERE ROWNUM = 1;
    ELSE
      SELECT ID INTO v_loc_id FROM WAREHOUSE_LOCATION
       WHERE WAREHOUSE_ID = v_wh_id
         AND LOCATION_CODE = UPPER(TRIM(p_loc_code)) AND IS_ACTIVE = 1
       FOR UPDATE;
    END IF;

    MERGE INTO LOT_STOCK d
    USING (SELECT v_lot_id AS lid, v_wh_id AS wid, v_loc_id AS loc FROM DUAL) s
    ON (d.LOT_ID = s.lid AND d.WAREHOUSE_ID = s.wid AND d.LOCATION_ID = s.loc)
    WHEN MATCHED THEN
      UPDATE SET d.QTY_ON_HAND = d.QTY_ON_HAND + p_qty, d.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (LOT_ID, WAREHOUSE_ID, LOCATION_ID, QTY_ON_HAND, QTY_RESERVED, UPDATED_AT)
      VALUES (v_lot_id, v_wh_id, v_loc_id, p_qty, 0, SYSDATE);

    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS wid, v_item_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + p_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_wh_id, v_item_id, p_qty, SYSDATE);

    SELECT QTY INTO v_new_bal FROM STOCK
     WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id;

    INSERT INTO INVENTORY_TRANSACTION
      (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
    VALUES ('STOCK_IN', v_item_id, v_wh_id, p_qty, v_new_bal, p_po_no, p_user);

    INSERT INTO TRACEABILITY_EVENT
      (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, TO_LOCATION_ID,
       QTY, REF_TYPE, REF_NO, ACTOR, IDEMPOTENCY_KEY)
    VALUES ('RECEIVE', v_item_id, v_lot_id, v_wh_id, v_loc_id,
            p_qty, 'PURCHASE_ORDER', p_po_no, p_user, p_idem_key)
    RETURNING EVENT_ID INTO v_ev_id;

    BEGIN
      INSERT INTO BARCODE_IDENTIFIER (CODE_VALUE, CODE_TYPE, ENTITY_TYPE, ENTITY_KEY)
      VALUES ('LOT:' || UPPER(TRIM(p_lot_code)), 'CODE128', 'LOT', UPPER(TRIM(p_lot_code)));
    EXCEPTION
      WHEN DUP_VAL_ON_INDEX THEN NULL;
    END;

    mark_idem(p_idem_key, 'RECEIVE_LOT', p_req_hash, 200,
      '{"lot":"' || UPPER(TRIM(p_lot_code)) || '","qty":' || p_qty || '}');

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE = -20014 THEN
        RAISE;
      END IF;
      IF SQLCODE NOT IN (-20001, -20002, -20012, -20013) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'receive_lot_stock', p_po_no, p_user);
      END IF;
      RAISE;
  END receive_lot_stock;

  -- Put-away inside ONE warehouse: only the location split changes.
  PROCEDURE putaway_lot (
    p_lot_code IN VARCHAR2,
    p_from_loc IN VARCHAR2,
    p_to_loc   IN VARCHAR2,
    p_qty      IN NUMBER,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_lot_id  NUMBER;
    v_item_id NUMBER;
    v_wh_id   NUMBER;
    v_from_id NUMBER;
    v_to_id   NUMBER;
    v_have    NUMBER;
    v_ev_id   NUMBER;
  BEGIN
    IF p_qty IS NULL OR p_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Put-away qty must be > 0.');
    END IF;

    check_idem(p_idem_key, p_req_hash, 'PUTAWAY');

    SELECT LOT_ID, ITEM_ID INTO v_lot_id, v_item_id FROM INVENTORY_LOT
     WHERE LOT_CODE = UPPER(TRIM(p_lot_code)) FOR UPDATE;

    -- Resolve the owning warehouse through the FROM location so a lot
    -- spread over several warehouses still put-aways deterministically.
    SELECT l.WAREHOUSE_ID, l.ID INTO v_wh_id, v_from_id FROM WAREHOUSE_LOCATION l
      JOIN LOT_STOCK s ON s.LOCATION_ID = l.ID AND s.LOT_ID = v_lot_id
     WHERE l.LOCATION_CODE = UPPER(TRIM(p_from_loc)) AND l.IS_ACTIVE = 1
       AND ROWNUM = 1;
    SELECT ID INTO v_to_id FROM WAREHOUSE_LOCATION
     WHERE WAREHOUSE_ID = v_wh_id
       AND LOCATION_CODE = UPPER(TRIM(p_to_loc)) AND IS_ACTIVE = 1;
    IF v_from_id = v_to_id THEN
      RAISE_APPLICATION_ERROR(-20012, 'Source and destination must differ.');
    END IF;

    SELECT QTY_ON_HAND INTO v_have FROM LOT_STOCK
     WHERE LOT_ID = v_lot_id AND WAREHOUSE_ID = v_wh_id AND LOCATION_ID = v_from_id
     FOR UPDATE;
    IF v_have < p_qty THEN
      RAISE_APPLICATION_ERROR(-20003,
        'Insufficient lot qty at ' || p_from_loc || ' (have ' || v_have || ').');
    END IF;

    UPDATE LOT_STOCK SET QTY_ON_HAND = QTY_ON_HAND - p_qty, UPDATED_AT = SYSDATE
     WHERE LOT_ID = v_lot_id AND WAREHOUSE_ID = v_wh_id AND LOCATION_ID = v_from_id;

    MERGE INTO LOT_STOCK d
    USING (SELECT v_lot_id AS lid, v_wh_id AS wid, v_to_id AS loc FROM DUAL) s
    ON (d.LOT_ID = s.lid AND d.WAREHOUSE_ID = s.wid AND d.LOCATION_ID = s.loc)
    WHEN MATCHED THEN
      UPDATE SET d.QTY_ON_HAND = d.QTY_ON_HAND + p_qty, d.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (LOT_ID, WAREHOUSE_ID, LOCATION_ID, QTY_ON_HAND, QTY_RESERVED, UPDATED_AT)
      VALUES (v_lot_id, v_wh_id, v_to_id, p_qty, 0, SYSDATE);

    INSERT INTO TRACEABILITY_EVENT
      (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, FROM_LOCATION_ID, TO_LOCATION_ID,
       QTY, REF_TYPE, REF_NO, ACTOR, IDEMPOTENCY_KEY)
    VALUES ('PUTAWAY', v_item_id, v_lot_id, v_wh_id, v_from_id, v_to_id,
            p_qty, 'PUTAWAY', p_lot_code, p_user, p_idem_key)
    RETURNING EVENT_ID INTO v_ev_id;

    mark_idem(p_idem_key, 'PUTAWAY', p_req_hash);
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE = -20014 THEN
        RAISE;
      END IF;
      IF SQLCODE NOT IN (-20001, -20002, -20003, -20012, -20013) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'putaway_lot', p_lot_code, p_user);
      END IF;
      RAISE;
  END putaway_lot;

  -- Move across warehouses: both aggregates updated, total preserved.
  PROCEDURE move_lot_stock (
    p_lot_code IN VARCHAR2,
    p_from_wh  IN VARCHAR2,
    p_from_loc IN VARCHAR2,
    p_to_wh    IN VARCHAR2,
    p_to_loc   IN VARCHAR2,
    p_qty      IN NUMBER,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_lot_id   NUMBER;
    v_item_id  NUMBER;
    v_from_wid NUMBER;
    v_to_wid   NUMBER;
    v_from_id  NUMBER;
    v_to_id    NUMBER;
    v_have     NUMBER;
    v_bal      NUMBER;
    v_ev_id    NUMBER;
  BEGIN
    IF p_qty IS NULL OR p_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Move qty must be > 0.');
    END IF;

    check_idem(p_idem_key, p_req_hash, 'MOVE_LOT');

    SELECT LOT_ID, ITEM_ID INTO v_lot_id, v_item_id FROM INVENTORY_LOT
     WHERE LOT_CODE = UPPER(TRIM(p_lot_code)) FOR UPDATE;
    SELECT ID INTO v_from_wid FROM WAREHOUSE WHERE CODE = UPPER(TRIM(p_from_wh));
    SELECT ID INTO v_to_wid FROM WAREHOUSE WHERE CODE = UPPER(TRIM(p_to_wh));
    SELECT ID INTO v_from_id FROM WAREHOUSE_LOCATION
     WHERE WAREHOUSE_ID = v_from_wid
       AND LOCATION_CODE = UPPER(TRIM(p_from_loc)) AND IS_ACTIVE = 1;
    SELECT ID INTO v_to_id FROM WAREHOUSE_LOCATION
     WHERE WAREHOUSE_ID = v_to_wid
       AND LOCATION_CODE = UPPER(TRIM(p_to_loc)) AND IS_ACTIVE = 1;

    SELECT QTY_ON_HAND INTO v_have FROM LOT_STOCK
     WHERE LOT_ID = v_lot_id AND WAREHOUSE_ID = v_from_wid AND LOCATION_ID = v_from_id
     FOR UPDATE;
    IF v_have < p_qty THEN
      RAISE_APPLICATION_ERROR(-20003,
        'Insufficient lot qty at ' || p_from_wh || '/' || p_from_loc ||
        ' (have ' || v_have || ').');
    END IF;

    UPDATE LOT_STOCK SET QTY_ON_HAND = QTY_ON_HAND - p_qty, UPDATED_AT = SYSDATE
     WHERE LOT_ID = v_lot_id AND WAREHOUSE_ID = v_from_wid AND LOCATION_ID = v_from_id;

    MERGE INTO LOT_STOCK d
    USING (SELECT v_lot_id AS lid, v_to_wid AS wid, v_to_id AS loc FROM DUAL) s
    ON (d.LOT_ID = s.lid AND d.WAREHOUSE_ID = s.wid AND d.LOCATION_ID = s.loc)
    WHEN MATCHED THEN
      UPDATE SET d.QTY_ON_HAND = d.QTY_ON_HAND + p_qty, d.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (LOT_ID, WAREHOUSE_ID, LOCATION_ID, QTY_ON_HAND, QTY_RESERVED, UPDATED_AT)
      VALUES (v_lot_id, v_to_wid, v_to_id, p_qty, 0, SYSDATE);

    UPDATE STOCK SET QTY = QTY - p_qty, UPDATED_AT = SYSDATE
     WHERE WAREHOUSE_ID = v_from_wid AND ITEM_ID = v_item_id;
    MERGE INTO STOCK s
    USING (SELECT v_to_wid AS wid, v_item_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + p_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_to_wid, v_item_id, p_qty, SYSDATE);

    SELECT QTY INTO v_bal FROM STOCK
     WHERE WAREHOUSE_ID = v_from_wid AND ITEM_ID = v_item_id;
    INSERT INTO INVENTORY_TRANSACTION
      (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
    VALUES ('STOCK_OUT', v_item_id, v_from_wid, -p_qty, v_bal, p_lot_code, p_user);
    SELECT QTY INTO v_bal FROM STOCK
     WHERE WAREHOUSE_ID = v_to_wid AND ITEM_ID = v_item_id;
    INSERT INTO INVENTORY_TRANSACTION
      (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
    VALUES ('STOCK_IN', v_item_id, v_to_wid, p_qty, v_bal, p_lot_code, p_user);

    INSERT INTO TRACEABILITY_EVENT
      (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, FROM_LOCATION_ID, TO_LOCATION_ID,
       QTY, REF_TYPE, REF_NO, ACTOR, IDEMPOTENCY_KEY)
    VALUES ('MOVE', v_item_id, v_lot_id, v_to_wid, v_from_id, v_to_id,
            p_qty, 'MOVE', p_lot_code, p_user, p_idem_key)
    RETURNING EVENT_ID INTO v_ev_id;

    mark_idem(p_idem_key, 'MOVE_LOT', p_req_hash);
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE = -20014 THEN
        RAISE;
      END IF;
      IF SQLCODE NOT IN (-20001, -20002, -20003, -20012, -20013) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'move_lot_stock', p_lot_code, p_user);
      END IF;
      RAISE;
  END move_lot_stock;

  PROCEDURE set_lot_hold (
    p_lot_code IN VARCHAR2,
    p_status   IN VARCHAR2,
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_lot_id NUMBER;
    v_item   NUMBER;
    v_wh     NUMBER;
    v_cur    VARCHAR2(10);
    v_new    VARCHAR2(10);
    v_ev     NUMBER;
  BEGIN
    v_new := UPPER(TRIM(p_status));
    IF v_new NOT IN ('ACTIVE','HOLD','BLOCKED') THEN
      RAISE_APPLICATION_ERROR(-20012, 'Hold status must be ACTIVE, HOLD or BLOCKED.');
    END IF;
    SELECT LOT_ID, ITEM_ID, STATUS INTO v_lot_id, v_item, v_cur FROM INVENTORY_LOT
     WHERE LOT_CODE = UPPER(TRIM(p_lot_code)) FOR UPDATE;
    IF v_cur IN ('CONSUMED','EXPIRED') THEN
      RAISE_APPLICATION_ERROR(-20011, 'Lot ' || p_lot_code || ' is ' || v_cur || '.');
    END IF;
    UPDATE INVENTORY_LOT SET STATUS = v_new WHERE LOT_ID = v_lot_id;
    SELECT WAREHOUSE_ID INTO v_wh FROM LOT_STOCK WHERE LOT_ID = v_lot_id AND ROWNUM = 1;
    INSERT INTO TRACEABILITY_EVENT
      (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, QTY, REF_TYPE, REF_NO, ACTOR)
    VALUES (CASE WHEN v_new = 'ACTIVE' THEN 'RELEASE_HOLD' ELSE 'HOLD' END,
            v_item, v_lot_id, v_wh, 0, 'LOT_HOLD', p_lot_code, p_user)
    RETURNING EVENT_ID INTO v_ev;
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'set_lot_hold', p_lot_code, p_user);
      RAISE;
  END set_lot_hold;

  -- Manufacturing genealogy (Phase 3): FEFO/FIFO preview, exact issue,
  -- traceable completion. See the spec block above for the contract.
  PROCEDURE allocate_lots_preview (
    p_po_no IN VARCHAR2,
    p_cur   OUT SYS_REFCURSOR
  ) IS
    v_po_id NUMBER;
  BEGIN
    SELECT ID INTO v_po_id FROM PRODUCTION_ORDER WHERE PO_NO = p_po_no;
    OPEN p_cur FOR
      SELECT m.CODE AS ITEM_CODE, m.TRACE_MODE,
             (SELECT NVL(SUM(s.QTY_ON_HAND - s.QTY_RESERVED), 0)
                FROM LOT_STOCK s JOIN INVENTORY_LOT l ON s.LOT_ID = l.LOT_ID
               WHERE l.ITEM_ID = m.ID AND l.STATUS = 'ACTIVE'
                 AND (l.EXPIRY_DATE IS NULL OR l.EXPIRY_DATE >= TRUNC(SYSDATE))
             ) AS QTY_AVAILABLE_ISSUABLE
        FROM BOM b
        JOIN BOM_DETAIL d ON d.BOM_ID = b.ID
        JOIN ITEM m ON m.ID = d.MAT_ITEM_ID
        JOIN PRODUCTION_ORDER o ON o.FG_ITEM_ID = b.FG_ITEM_ID
       WHERE o.PO_NO = p_po_no AND b.STATUS = 'ACTIVE'
       ORDER BY m.CODE;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RAISE_APPLICATION_ERROR(-20002, 'Production order not found: ' || p_po_no);
  END allocate_lots_preview;

  PROCEDURE issue_lot_to_production (
    p_po_no    IN VARCHAR2,
    p_idem_key IN VARCHAR2 DEFAULT NULL,
    p_req_hash IN VARCHAR2 DEFAULT NULL,
    p_user     IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_po_id    NUMBER;
    v_wh_id    NUMBER;
    v_fg_id    NUMBER;
    v_planned  NUMBER;
    v_item_id  NUMBER;
    v_item     VARCHAR2(30);
    v_need     NUMBER;
    v_have     NUMBER;
    v_take     NUMBER;
    v_ev       NUMBER;
    v_bal      NUMBER;
  BEGIN
    check_idem(p_idem_key, p_req_hash, 'ISSUE_LOT');

    SELECT ID, WAREHOUSE_ID, FG_ITEM_ID, QTY_PLANNED
      INTO v_po_id, v_wh_id, v_fg_id, v_planned
      FROM PRODUCTION_ORDER WHERE PO_NO = p_po_no FOR UPDATE;
    IF v_planned IS NULL OR v_planned <= 0 THEN
      RAISE_APPLICATION_ERROR(-20012, 'Planned quantity must be > 0.');
    END IF;

    FOR b IN (SELECT d.MAT_ITEM_ID AS item_id, i.CODE AS code,
                     i.TRACE_MODE AS trace_mode,
                     d.QTY_REQUIRED * v_planned AS need
                FROM BOM bo JOIN BOM_DETAIL d ON d.BOM_ID = bo.ID
                JOIN ITEM i ON i.ID = d.MAT_ITEM_ID
               WHERE bo.FG_ITEM_ID = v_fg_id AND bo.STATUS = 'ACTIVE'
               ORDER BY i.CODE)
    LOOP
      IF b.trace_mode = 'LOT' THEN
        v_need := b.need;
        FOR lot IN (SELECT l.LOT_ID AS id, l.LOT_CODE AS code,
                           (s.QTY_ON_HAND - s.QTY_RESERVED) AS avail,
                           s.WAREHOUSE_ID AS wid, s.LOCATION_ID AS loc
                      FROM LOT_STOCK s JOIN INVENTORY_LOT l ON s.LOT_ID = l.LOT_ID
                     WHERE l.ITEM_ID = b.item_id AND l.STATUS = 'ACTIVE'
                       AND s.WAREHOUSE_ID = v_wh_id
                       AND (l.EXPIRY_DATE IS NULL OR l.EXPIRY_DATE >= TRUNC(SYSDATE))
                       AND (s.QTY_ON_HAND - s.QTY_RESERVED) > 0
                     ORDER BY CASE WHEN l.EXPIRY_DATE IS NULL THEN 1 ELSE 0 END,
                              l.EXPIRY_DATE, l.CREATED_AT, l.LOT_CODE
                       FOR UPDATE OF s.QTY_ON_HAND)
        LOOP
          EXIT WHEN v_need <= 0;
          v_take := LEAST(lot.avail, v_need);
          UPDATE LOT_STOCK SET QTY_ON_HAND = QTY_ON_HAND - v_take, UPDATED_AT = SYSDATE
           WHERE LOT_ID = lot.id AND WAREHOUSE_ID = lot.wid AND LOCATION_ID = lot.loc;
          UPDATE STOCK SET QTY = QTY - v_take, UPDATED_AT = SYSDATE
           WHERE WAREHOUSE_ID = lot.wid AND ITEM_ID = b.item_id;
          SELECT QTY INTO v_bal FROM STOCK
           WHERE WAREHOUSE_ID = lot.wid AND ITEM_ID = b.item_id;
          INSERT INTO INVENTORY_TRANSACTION
            (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
          VALUES ('MFG_CONSUME', b.item_id, lot.wid, -v_take, v_bal, p_po_no, p_user);
          INSERT INTO TRACEABILITY_EVENT
            (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, FROM_LOCATION_ID,
             QTY, REF_TYPE, REF_NO, ACTOR, IDEMPOTENCY_KEY)
          VALUES ('ISSUE_TO_PRODUCTION', b.item_id, lot.id, lot.wid, lot.loc,
                  v_take, 'PRODUCTION_ORDER', p_po_no, p_user,
                  SUBSTR(p_idem_key || ':' || b.code || ':' || lot.code, 1, 80))
          RETURNING EVENT_ID INTO v_ev;
          INSERT INTO PRODUCTION_LOT_CONSUMPTION
            (PRODUCTION_ORDER_ID, ITEM_ID, INPUT_LOT_ID, QTY_CONSUMED, EVENT_ID)
          VALUES (v_po_id, b.item_id, lot.id, v_take, v_ev);
          v_need := v_need - v_take;
        END LOOP;
        IF v_need > 0 THEN
          RAISE_APPLICATION_ERROR(-20007,
            'Lot-tracked material ' || b.code || ' short by ' || v_need ||
            ' for order ' || p_po_no || '.');
        END IF;
      END IF;
    END LOOP;

    mark_idem(p_idem_key, 'ISSUE_LOT', p_req_hash);
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE = -20014 THEN
        RAISE;
      END IF;
      IF SQLCODE NOT IN (-20001, -20002, -20007, -20012, -20013) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'issue_lot_to_production', p_po_no, p_user);
      END IF;
      RAISE;
  END issue_lot_to_production;

  PROCEDURE complete_production_traceable (
    p_po_no     IN VARCHAR2,
    p_fg_lot    IN VARCHAR2 DEFAULT NULL,
    p_qty       IN NUMBER DEFAULT NULL,
    p_loc_code  IN VARCHAR2 DEFAULT NULL,
    p_output_wh IN VARCHAR2 DEFAULT NULL,
    p_tolerance IN NUMBER DEFAULT 0,
    p_idem_key  IN VARCHAR2 DEFAULT NULL,
    p_req_hash  IN VARCHAR2 DEFAULT NULL,
    p_user      IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_po_id       NUMBER;
    v_in_wh_id    NUMBER;
    v_out_wh_id   NUMBER;
    v_fg_id       NUMBER;
    v_planned NUMBER;
    v_status  VARCHAR2(20);
    v_qty     NUMBER;
    v_lot     VARCHAR2(60);
    v_lot_id  NUMBER;
    v_lot_item NUMBER;
    v_loc_id  NUMBER;
    v_ev      NUMBER;
    v_bal     NUMBER;
    v_tol     NUMBER;
  BEGIN
    check_idem(p_idem_key, p_req_hash, 'COMPLETE_TRACEABLE');

    IF p_tolerance IS NULL OR p_tolerance < 0 OR p_tolerance > 1 THEN
      RAISE_APPLICATION_ERROR(-20012, 'Tolerance must be between 0 and 1.');
    END IF;
    v_tol := NVL(p_tolerance, 0);

    SELECT ID, WAREHOUSE_ID, FG_ITEM_ID, QTY_PLANNED, STATUS
      INTO v_po_id, v_in_wh_id, v_fg_id, v_planned, v_status
      FROM PRODUCTION_ORDER WHERE PO_NO = p_po_no FOR UPDATE;
    IF v_status = 'COMPLETED' THEN
      RAISE_APPLICATION_ERROR(-20004,
        'Production Order ' || p_po_no || ' is already completed.');
    ELSIF v_status = 'CANCELLED' THEN
      RAISE_APPLICATION_ERROR(-20005,
        'Production Order ' || p_po_no || ' is cancelled.');
    END IF;

    IF p_output_wh IS NULL THEN
      v_out_wh_id := v_in_wh_id;
    ELSE
      SELECT ID INTO v_out_wh_id FROM WAREHOUSE
       WHERE CODE = UPPER(TRIM(p_output_wh));
    END IF;


    v_qty := NVL(p_qty, v_planned);
    IF v_qty <= 0 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Output quantity must be > 0.');
    END IF;

    -- Every LOT-tracked BOM line must be fully issued (within tolerance).
    FOR b IN (SELECT d.MAT_ITEM_ID AS item_id, i.CODE AS code,
                     d.QTY_REQUIRED * v_planned AS need,
                     NVL((SELECT SUM(c.QTY_CONSUMED)
                            FROM PRODUCTION_LOT_CONSUMPTION c
                           WHERE c.PRODUCTION_ORDER_ID = v_po_id
                             AND c.ITEM_ID = d.MAT_ITEM_ID), 0) AS got
                FROM BOM bo JOIN BOM_DETAIL d ON d.BOM_ID = bo.ID
                JOIN ITEM i ON i.ID = d.MAT_ITEM_ID
               WHERE bo.FG_ITEM_ID = v_fg_id AND bo.STATUS = 'ACTIVE'
                 AND i.TRACE_MODE = 'LOT')
    LOOP
      IF b.got + (b.need * v_tol) < b.need THEN
        RAISE_APPLICATION_ERROR(-20007,
          'Material ' || b.code || ' not fully issued for ' || p_po_no ||
          ' (need ' || b.need || ', issued ' || b.got || ').');
      END IF;
    END LOOP;

    -- FG lot: caller-supplied or deterministic PO-derived code.
    v_lot := UPPER(TRIM(NVL(p_fg_lot, 'FG-' || p_po_no || '-001')));
    BEGIN
      SELECT LOT_ID, ITEM_ID INTO v_lot_id, v_lot_item FROM INVENTORY_LOT
       WHERE LOT_CODE = v_lot FOR UPDATE;
      IF v_lot_item != v_fg_id THEN
        RAISE_APPLICATION_ERROR(-20012,
          'Finished-goods lot ' || v_lot || ' belongs to another item.');
      END IF;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        INSERT INTO INVENTORY_LOT
          (LOT_CODE, ITEM_ID, SOURCE_TYPE, SOURCE_REF_NO, STATUS, CREATED_BY)
        VALUES (v_lot, v_fg_id, 'PRODUCTION', p_po_no, 'ACTIVE', p_user)
        RETURNING LOT_ID INTO v_lot_id;
    END;

    IF p_loc_code IS NULL THEN
      SELECT ID INTO v_loc_id FROM (
        SELECT ID FROM WAREHOUSE_LOCATION
         WHERE WAREHOUSE_ID = v_out_wh_id AND IS_ACTIVE = 1
         ORDER BY CASE LOCATION_TYPE WHEN 'BIN' THEN 0 ELSE 1 END, LOCATION_CODE
      ) WHERE ROWNUM = 1;
    ELSE
      SELECT ID INTO v_loc_id FROM WAREHOUSE_LOCATION
       WHERE WAREHOUSE_ID = v_out_wh_id
         AND LOCATION_CODE = UPPER(TRIM(p_loc_code)) AND IS_ACTIVE = 1
       FOR UPDATE;
    END IF;

    -- FG lot stock + event + genealogy link (aggregate close below).
    MERGE INTO LOT_STOCK d
    USING (SELECT v_lot_id AS lid, v_out_wh_id AS wid, v_loc_id AS loc FROM DUAL) s
    ON (d.LOT_ID = s.lid AND d.WAREHOUSE_ID = s.wid AND d.LOCATION_ID = s.loc)
    WHEN MATCHED THEN
      UPDATE SET d.QTY_ON_HAND = d.QTY_ON_HAND + v_qty, d.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (LOT_ID, WAREHOUSE_ID, LOCATION_ID, QTY_ON_HAND, QTY_RESERVED, UPDATED_AT)
      VALUES (v_lot_id, v_out_wh_id, v_loc_id, v_qty, 0, SYSDATE);

    MERGE INTO STOCK s
    USING (SELECT v_out_wh_id AS wid, v_fg_id AS iid FROM DUAL) d
    ON (s.WAREHOUSE_ID = d.wid AND s.ITEM_ID = d.iid)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + v_qty, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (v_out_wh_id, v_fg_id, v_qty, SYSDATE);

    SELECT QTY INTO v_bal FROM STOCK
     WHERE WAREHOUSE_ID = v_out_wh_id AND ITEM_ID = v_fg_id;
    INSERT INTO INVENTORY_TRANSACTION
      (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
    VALUES ('MFG_OUTPUT', v_fg_id, v_out_wh_id, v_qty, v_bal, p_po_no, p_user);
    INSERT INTO TRACEABILITY_EVENT
      (EVENT_TYPE, ITEM_ID, LOT_ID, WAREHOUSE_ID, TO_LOCATION_ID,
       QTY, REF_TYPE, REF_NO, ACTOR, IDEMPOTENCY_KEY)
    VALUES ('PRODUCTION_OUTPUT', v_fg_id, v_lot_id, v_out_wh_id, v_loc_id,
            v_qty, 'PRODUCTION_ORDER', p_po_no, p_user, p_idem_key)
    RETURNING EVENT_ID INTO v_ev;
    INSERT INTO PRODUCTION_LOT_OUTPUT
      (PRODUCTION_ORDER_ID, FG_ITEM_ID, OUTPUT_LOT_ID, QTY_PRODUCED, EVENT_ID)
    VALUES (v_po_id, v_fg_id, v_lot_id, v_qty, v_ev);
    BEGIN
      INSERT INTO BARCODE_IDENTIFIER (CODE_VALUE, CODE_TYPE, ENTITY_TYPE, ENTITY_KEY)
      VALUES ('LOT:' || v_lot, 'CODE128', 'LOT', v_lot);
    EXCEPTION
      WHEN DUP_VAL_ON_INDEX THEN NULL;
    END;

    -- Close the order here: lot-tracked lines were already consumed from
    -- STOCK by issue_lot_to_production, so delegating to the legacy
    -- complete_production_order would double-issue them. Only NON-lot lines
    -- still draw from the aggregate, then the PO status flips — one tx.
    FOR b IN (SELECT d.MAT_ITEM_ID AS item_id, i.CODE AS code,
                     i.TRACE_MODE AS trace_mode,
                     d.QTY_REQUIRED * v_planned AS need
                FROM BOM bo JOIN BOM_DETAIL d ON d.BOM_ID = bo.ID
                JOIN ITEM i ON i.ID = d.MAT_ITEM_ID
               WHERE bo.FG_ITEM_ID = v_fg_id AND bo.STATUS = 'ACTIVE')
    LOOP
      IF b.trace_mode <> 'LOT' THEN
        BEGIN
          SELECT QTY INTO v_bal FROM STOCK
           WHERE WAREHOUSE_ID = v_in_wh_id AND ITEM_ID = b.item_id;
        EXCEPTION
          WHEN NO_DATA_FOUND THEN v_bal := 0;
        END;
        IF v_bal < b.need THEN
          RAISE_APPLICATION_ERROR(-20007,
            'Material ' || b.code || ' requires ' || b.need ||
            ', available ' || v_bal || '; cannot complete ' || p_po_no || '.');
        END IF;
        UPDATE STOCK SET QTY = QTY - b.need, UPDATED_AT = SYSDATE
         WHERE WAREHOUSE_ID = v_in_wh_id AND ITEM_ID = b.item_id;
        SELECT QTY INTO v_bal FROM STOCK
         WHERE WAREHOUSE_ID = v_in_wh_id AND ITEM_ID = b.item_id;
        INSERT INTO INVENTORY_TRANSACTION
          (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
        VALUES ('MFG_CONSUME', b.item_id, v_in_wh_id, -b.need, v_bal, p_po_no, p_user);
      END IF;
    END LOOP;

    UPDATE PRODUCTION_ORDER
       SET STATUS = 'COMPLETED', QTY_DONE = v_qty, COMPLETED_AT = SYSDATE
     WHERE ID = v_po_id;

    mark_idem(p_idem_key, 'COMPLETE_TRACEABLE', p_req_hash, 200,
      '{"fgLot":"' || v_lot || '","qty":' || v_qty || '}');
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE = -20014 THEN
        RAISE;
      END IF;
      IF SQLCODE NOT IN (-20001, -20002, -20003, -20004, -20005,
                         -20006, -20007, -20012, -20013) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'complete_production_traceable',
                                 p_po_no, p_user);
      END IF;
      RAISE;
  END complete_production_traceable;

  -----------------------------------------------------------------------------
  -- PROCEDURE: create_label_job (Phase 4 label workflow)
  -----------------------------------------------------------------------------
  PROCEDURE create_label_job (
    p_entity_type IN VARCHAR2,
    p_entity_key  IN VARCHAR2,
    p_label_type  IN VARCHAR2,
    p_copies      IN NUMBER   DEFAULT 1,
    p_format      IN VARCHAR2 DEFAULT 'HTML',
    p_user        IN VARCHAR2 DEFAULT 'system',
    p_job_id      OUT NUMBER
  ) IS
    v_et  VARCHAR2(20) := UPPER(TRIM(p_entity_type));
    v_ek  VARCHAR2(80) := UPPER(TRIM(p_entity_key));
    v_lt  VARCHAR2(20) := UPPER(TRIM(p_label_type));
    v_fmt VARCHAR2(10) := UPPER(TRIM(p_format));
    v_n   NUMBER;
  BEGIN
    IF p_copies IS NULL OR p_copies < 1 THEN
      RAISE_APPLICATION_ERROR(-20001, 'Copies must be >= 1.');
    END IF;
    IF v_fmt NOT IN ('HTML', 'ZPL') THEN
      RAISE_APPLICATION_ERROR(-20012, 'Invalid label format: ' || p_format);
    END IF;
    IF v_et NOT IN ('LOT', 'ITEM', 'LOCATION', 'PRODUCTION_ORDER') THEN
      RAISE_APPLICATION_ERROR(-20012, 'Invalid label entity type: ' || p_entity_type);
    END IF;
    IF v_lt NOT IN ('RAW_MATERIAL', 'FINISHED_GOOD', 'LOCATION') THEN
      RAISE_APPLICATION_ERROR(-20012, 'Invalid label type: ' || p_label_type);
    END IF;

    -- The entity must exist (a historical lot can still be reprinted).
    IF v_et = 'LOT' THEN
      SELECT COUNT(*) INTO v_n FROM INVENTORY_LOT WHERE LOT_CODE = v_ek;
    ELSIF v_et = 'ITEM' THEN
      SELECT COUNT(*) INTO v_n FROM ITEM WHERE CODE = v_ek;
    ELSIF v_et = 'LOCATION' THEN
      SELECT COUNT(*) INTO v_n FROM WAREHOUSE_LOCATION WHERE LOCATION_CODE = v_ek;
    ELSE
      SELECT COUNT(*) INTO v_n FROM PRODUCTION_ORDER WHERE PO_NO = v_ek;
    END IF;
    IF v_n = 0 THEN
      RAISE_APPLICATION_ERROR(-20002,
        'Label entity not found: ' || v_et || ' ' || v_ek);
    END IF;

    -- Reprint gate: one audit row per print, zero lot/stock side effects.
    INSERT INTO LABEL_PRINT_JOB
      (LABEL_TYPE, ENTITY_TYPE, ENTITY_KEY, COPIES, FORMAT,
       STATUS, PRINTED_BY, PRINTED_AT)
    VALUES
      (v_lt, v_et, v_ek, p_copies, v_fmt,
       'PRINTED', p_user, SYSDATE)
    RETURNING PRINT_JOB_ID INTO p_job_id;
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      IF SQLCODE NOT IN (-20001, -20002, -20012) THEN
        ERP_OPERATIONS.log_error(SQLCODE, SQLERRM, 'create_label_job',
                                 v_ek, p_user);
      END IF;
      RAISE;
  END create_label_job;

END ERP_TRACEABILITY;
/

