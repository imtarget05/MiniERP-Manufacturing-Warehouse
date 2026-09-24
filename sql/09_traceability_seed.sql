-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE: sql/09_traceability_seed.sql
-- TARGET: Oracle Database 19c / 21c / 23c
-- DESCRIPTION: Phase 2 demo seed (runs AFTER 03_seed.sql): receiving bins,
--              LOT trace mode for raw materials, and a lot-tracked purchase
--              order used by the receive -> putaway -> move demo.
--              Idempotent (MERGE / guarded INSERTs) => rerunnable.
-- ============================================================================

-- Demo locations (RCV receiving + BIN storage per warehouse).
BEGIN
  ERP_TRACEABILITY.ensure_location('WH_RAW', 'RCV-01', 'Receiving dock RAW', 'RECEIVING', 'seed');
  ERP_TRACEABILITY.ensure_location('WH_RAW', 'A-01-01', 'Rack A level 1 RAW', 'BIN', 'seed');
  ERP_TRACEABILITY.ensure_location('WH_RAW', 'A-01-02', 'Rack A level 2 RAW', 'BIN', 'seed');
  ERP_TRACEABILITY.ensure_location('WH_FG', 'RCV-01', 'Receiving dock FG', 'RECEIVING', 'seed');
  ERP_TRACEABILITY.ensure_location('WH_FG', 'FG-01-01', 'FG rack 1', 'BIN', 'seed');
  ERP_TRACEABILITY.ensure_location('WH_WIP', 'LINE-01', 'Production line 1', 'LINE', 'seed');
END;
/

-- Raw materials become LOT-tracked; finished goods stay NONE until Phase 3.
UPDATE ITEM SET TRACE_MODE = 'LOT', SHELF_LIFE_DAYS = 365
 WHERE CODE IN ('MAT_RUBBER_01','MAT_MESH_01','MAT_THREAD_01','MAT_GLUE_01','MAT_BOX_01')
   AND TRACE_MODE = 'NONE';

-- Represent every LOT-tracked opening balance in LOT_STOCK. STOCK remains
-- the accounting truth; the opening lot is the physical traceability truth.
-- MERGE inserts only missing rows, so rerunning this seed never overwrites
-- operational quantities from a receiving / issue / production workflow.
DECLARE
  v_wh_id  NUMBER;
  v_loc_id NUMBER;
  v_lot_id NUMBER;
  v_code   VARCHAR2(60);
BEGIN
  SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = 'WH_RAW';
  SELECT ID INTO v_loc_id FROM WAREHOUSE_LOCATION
   WHERE WAREHOUSE_ID = v_wh_id AND LOCATION_CODE = 'A-01-01';

  FOR r IN (
    SELECT i.ID AS ITEM_ID, i.CODE AS ITEM_CODE, s.QTY
      FROM STOCK s
      JOIN ITEM i ON i.ID = s.ITEM_ID
     WHERE s.WAREHOUSE_ID = v_wh_id
       AND i.TRACE_MODE = 'LOT'
       AND s.QTY > 0
  ) LOOP
    v_code := 'OPEN-' || r.ITEM_CODE;
    BEGIN
      SELECT LOT_ID INTO v_lot_id FROM INVENTORY_LOT
       WHERE LOT_CODE = v_code FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        INSERT INTO INVENTORY_LOT
          (LOT_CODE, ITEM_ID, SOURCE_TYPE, SOURCE_REF_NO,
           MFG_DATE, EXPIRY_DATE, STATUS, CREATED_BY)
        VALUES
          (v_code, r.ITEM_ID, 'OPENING', 'INIT_BALANCE',
           SYSDATE - 30, SYSDATE + 365, 'ACTIVE', 'seed')
        RETURNING LOT_ID INTO v_lot_id;
    END;

    MERGE INTO LOT_STOCK d
    USING (SELECT v_lot_id AS LOT_ID, v_wh_id AS WAREHOUSE_ID,
                  v_loc_id AS LOCATION_ID FROM DUAL) s
       ON (d.LOT_ID = s.LOT_ID
           AND d.WAREHOUSE_ID = s.WAREHOUSE_ID
           AND d.LOCATION_ID = s.LOCATION_ID)
    WHEN NOT MATCHED THEN
      INSERT (LOT_ID, WAREHOUSE_ID, LOCATION_ID,
              QTY_ON_HAND, QTY_RESERVED, UPDATED_AT)
      VALUES (v_lot_id, v_wh_id, v_loc_id, r.QTY, 0, SYSDATE);
  END LOOP;
END;
/

-- A lot-tracked PO used by the Phase 2 demo (100 rubber soles to WH_RAW).
MERGE INTO PURCHASE_ORDER d
USING (SELECT 'PO_PUR_LOT_01' AS po FROM DUAL) s
ON (d.PO_NO = s.po)
WHEN NOT MATCHED THEN
  INSERT (PO_NO, ITEM_ID, QTY, WAREHOUSE_ID, STATUS)
  VALUES ('PO_PUR_LOT_01',
          (SELECT ID FROM ITEM WHERE CODE = 'MAT_RUBBER_01'),
          100,
          (SELECT ID FROM WAREHOUSE WHERE CODE = 'WH_RAW'),
          'CREATED');

-- Barcode identities for the demo locations (scanner flow).
MERGE INTO BARCODE_IDENTIFIER d
USING (SELECT 'LOC:WH_RAW/RCV-01' AS c, 'LOCATION' AS t, 'WH_RAW/RCV-01' AS k FROM DUAL) s
ON (d.CODE_VALUE = s.c)
WHEN NOT MATCHED THEN
  INSERT (CODE_VALUE, CODE_TYPE, ENTITY_TYPE, ENTITY_KEY)
  VALUES (s.c, 'QR', s.t, s.k);

COMMIT;
