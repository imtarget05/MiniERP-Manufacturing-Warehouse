-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE: sql/06_automation_plsql.sql
-- TARGET: Oracle Database 19c / 21c / 23c
-- DESCRIPTION: Package ERP_AUTOMATION - the business-process automation layer
--              (workflows W1..W7). Depends on sql/01_schema.sql,
--              sql/05_automation_schema.sql and sql/02_plsql.sql.
--              Loaded by scripts/run-sql.sh; both packages must be VALID.
--
-- DESIGN GUARANTEES:
--   (a) ERP_OPERATIONS stays the only owner of physical STOCK mutations.
--       The automation layer drives it through soft reservations
--       (STOCK_RESERVATION). The single exception is adjust_stock, which
--       consumes an APPROVED approval token and still writes the signed
--       INVENTORY_TRANSACTION row, so the ledger invariant
--         STOCK.QTY = SUM(INVENTORY_TRANSACTION.QTY)
--       keeps holding (PO001 assertions A3/C5 depend on it).
--   (b) Every workflow writes exactly one ERP_AUTOMATION_RUN audit row
--       through the autonomous write_run helper: a FAILED run survives the
--       ROLLBACK of the workflow transaction (same rationale as ERROR_LOG).
--   (c) Diagnosis text is deterministic and rule-based. If an AI assistant
--       is ever layered on top (Phase 7, optional) it is ADVISORY ONLY and
--       may never perform DML itself.
--   (d) New business error codes (docs/04-plsql-spec.md section 4):
--         -20010 ERR_APPROVAL_REQUIRED  approval token missing/not approved
--         -20011 ERR_AUTOMATION_STATE   workflow state transition illegal
--         -20012 ERR_INVALID_INPUT      bad automation parameter
-- ============================================================================

CREATE OR REPLACE PACKAGE ERP_AUTOMATION AS

  -- W1: evaluate material availability of a production order.
  --     RELEASED -> MATERIAL_CHECK -> READY | WAITING_MATERIAL.
  --     Short materials raise/refresh REPLENISH_ALERT rows. Shortage is an
  --     OUTCOME, not an error: p_status carries READY/WAITING_MATERIAL.
  PROCEDURE check_material_availability (
    p_po_no  IN  VARCHAR2,
    p_user   IN  VARCHAR2 DEFAULT 'system',
    p_status OUT VARCHAR2
  );

  -- W2: soft-reserve every BOM line of the order (idempotent per PO).
  --     Raises ORA-20007 when available stock cannot cover the plan.
  PROCEDURE reserve_materials (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

  -- W2 (undo): free the ACTIVE reservations and return the PO to RELEASED
  --             so it can be re-checked, re-reserved or cancelled.
  PROCEDURE release_reservations (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

  -- W3: replenishment rule for one (item, warehouse) line.
  --     Opens the alert below REORDER_POINT, closes it when recovered.
  --     p_suggested returns the suggested order quantity (API contract).
  PROCEDURE evaluate_replenishment (
    p_item_code  IN  VARCHAR2,
    p_wh_code    IN  VARCHAR2,
    p_trigger    IN  VARCHAR2 DEFAULT 'LOW_STOCK',
    p_user       IN  VARCHAR2 DEFAULT 'system',
    p_suggested  OUT NUMBER
  );

  -- W3 (batch): sweep every RAW material; p_alerts = OPEN alerts afterwards.
  PROCEDURE sweep_replenishment (
    p_user   IN  VARCHAR2 DEFAULT 'system',
    p_alerts OUT NUMBER
  );

  -- W4: transactional completion wrapper. Marks the PO IN_PROGRESS, flips
  --     ACTIVE reservations to CONSUMED, then delegates the physical stock
  --     movement to ERP_OPERATIONS.complete_production_order inside ONE
  --     transaction: any ORA-200xx rolls everything back (reservations
  --     included) while the FAILED audit row survives autonomously.
  --     Falls back to the plain balance check when the PO was never
  --     reserved (legacy path - 0 active reservation rows).
  PROCEDURE complete_reserved_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  );

  -- W5: stale production-order detection. A non-terminal PO whose last
  --     state change is older than p_days is stale; p_stale_count returns
  --     how many were found (run status = MANUAL_REVIEW when count > 0).
  PROCEDURE detect_stale_orders (
    p_days        IN  NUMBER DEFAULT 7,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_stale_count OUT NUMBER
  );

  -- W6: persist a scheduled operational report as a JSON snapshot.
  --     PAYLOAD_JSON is capped at 4000 chars (VARCHAR2(4000) column).
  PROCEDURE generate_report (
    p_report_type IN  VARCHAR2,
    p_period_from IN  DATE DEFAULT NULL,
    p_period_to   IN  DATE DEFAULT NULL,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_report_id   OUT NUMBER
  );

  -- W7: support incident context collector. Snapshots ERROR_LOG rows, PO
  --     state and open replenishment alerts of a reference into
  --     SUPPORT_INCIDENT.CONTEXT_JSON plus a rule-based DIAGNOSIS text.
  PROCEDURE collect_incident_context (
    p_ref_no      IN  VARCHAR2,
    p_error_code  IN  VARCHAR2 DEFAULT 'ERR_MATERIAL_SHORTAGE',
    p_title       IN  VARCHAR2 DEFAULT NULL,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_incident_id OUT NUMBER
  );

  -- Approval gate: human override for risky automation actions.
  PROCEDURE request_approval (
    p_approval_no IN VARCHAR2,
    p_action      IN VARCHAR2,
    p_ref_no      IN VARCHAR2,
    p_payload     IN VARCHAR2,
    p_requester   IN VARCHAR2 DEFAULT 'system'
  );

  PROCEDURE decide_approval (
    p_approval_no IN VARCHAR2,
    p_decision    IN VARCHAR2,   -- APPROVED | REJECTED
    p_approver    IN VARCHAR2 DEFAULT 'system'
  );

  -- Inventory adjustment executed ONLY against an APPROVED, not-yet-EXECUTED
  -- approval token of action INVENTORY_ADJUST. Writes a signed ADJUSTMENT
  -- ledger row (real delta + balance-after) => ledger invariant preserved.
  PROCEDURE adjust_stock (
    p_wh_code     IN VARCHAR2,
    p_item_code   IN VARCHAR2,
    p_qty_delta   IN NUMBER,
    p_approval_no IN VARCHAR2,
    p_user        IN VARCHAR2 DEFAULT 'system'
  );

END ERP_AUTOMATION;
/

CREATE OR REPLACE PACKAGE BODY ERP_AUTOMATION AS

  -----------------------------------------------------------------------------
  -- write_run: autonomous audit writer (guarantee b). One row per workflow
  -- run; a FAILED row survives the ROLLBACK of the caller's transaction.
  -----------------------------------------------------------------------------
  PROCEDURE write_run (
    p_workflow_name IN VARCHAR2,
    p_trigger_type  IN VARCHAR2,
    p_trigger_id    IN VARCHAR2,
    p_status        IN VARCHAR2,
    p_started_at    IN DATE,
    p_result_summary IN VARCHAR2,
    p_error_code    IN VARCHAR2,
    p_error_message IN VARCHAR2,
    p_run_by        IN VARCHAR2
  ) IS
    PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    INSERT INTO ERP_AUTOMATION_RUN
      (WORKFLOW_NAME, TRIGGER_TYPE, TRIGGER_ID, STATUS, STARTED_AT,
       FINISHED_AT, RETRY_COUNT, ERROR_CODE, ERROR_MESSAGE, RESULT_SUMMARY, RUN_BY)
    VALUES
      (p_workflow_name, p_trigger_type, p_trigger_id, p_status, p_started_at,
       SYSDATE, 0, p_error_code,
       SUBSTR(p_error_message, 1, 2000), SUBSTR(p_result_summary, 1, 2000),
       p_run_by);
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;  -- the audit writer must never take the caller down
  END write_run;

  -----------------------------------------------------------------------------
  -- f_available: STOCK.QTY minus ACTIVE holds of *other* orders.
  -- p_exclude_po NULL => subtract every hold (plant-wide availability).
  -----------------------------------------------------------------------------
  FUNCTION f_available (p_warehouse_id IN NUMBER, p_item_id IN NUMBER,
                        p_exclude_po   IN VARCHAR2 DEFAULT NULL) RETURN NUMBER IS
    v_qty  NUMBER := 0;
    v_held NUMBER := 0;
  BEGIN
    BEGIN
      SELECT QTY INTO v_qty
        FROM STOCK
       WHERE WAREHOUSE_ID = p_warehouse_id AND ITEM_ID = p_item_id
       FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        v_qty := 0;
    END;

    SELECT NVL(SUM(QTY), 0) INTO v_held
      FROM STOCK_RESERVATION
     WHERE WAREHOUSE_ID = p_warehouse_id
       AND ITEM_ID      = p_item_id
       AND STATUS       = 'ACTIVE'
       AND (p_exclude_po IS NULL OR PO_NO <> p_exclude_po);

    RETURN v_qty - v_held;
  END f_available;

  -----------------------------------------------------------------------------
  -- upsert_alert: keep exactly one OPEN alert per (item, warehouse).
  -- Reopens/refreshes while available < reorder point, closes on recovery.
  -----------------------------------------------------------------------------
  PROCEDURE upsert_alert (
    p_item_id       IN NUMBER,
    p_warehouse_id  IN NUMBER,
    p_qty_available IN NUMBER,
    p_qty_suggested IN NUMBER,
    p_trigger_type  IN VARCHAR2,
    p_ref_no        IN VARCHAR2,
    p_triggered_by   IN VARCHAR2
  ) IS
    v_open_cnt NUMBER;
    v_item     ITEM%ROWTYPE;
  BEGIN
    BEGIN
      SELECT * INTO v_item FROM ITEM WHERE ID = p_item_id;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RETURN; -- item vanished mid-flight: nothing to alert about
    END;

    SELECT COUNT(*) INTO v_open_cnt
      FROM REPLENISH_ALERT
     WHERE ITEM_ID = p_item_id
       AND WAREHOUSE_ID = p_warehouse_id
       AND STATUS = 'OPEN';

    IF p_qty_available < v_item.REORDER_POINT THEN
      IF v_open_cnt = 0 THEN
        INSERT INTO REPLENISH_ALERT
          (ITEM_ID, WAREHOUSE_ID, QTY_AVAILABLE, QTY_SUGGESTED,
           TRIGGER_TYPE, REF_NO, STATUS, TRIGGERED_BY)
        VALUES
          (p_item_id, p_warehouse_id, p_qty_available, p_qty_suggested,
           p_trigger_type, p_ref_no, 'OPEN', p_triggered_by);
      ELSE
        UPDATE REPLENISH_ALERT
           SET QTY_AVAILABLE = p_qty_available,
               QTY_SUGGESTED = p_qty_suggested,
               TRIGGER_TYPE  = p_trigger_type,
               REF_NO        = p_ref_no,
               TRIGGERED_BY  = p_triggered_by
         WHERE ITEM_ID = p_item_id
           AND WAREHOUSE_ID = p_warehouse_id
           AND STATUS = 'OPEN';
      END IF;
    ELSIF v_open_cnt > 0 THEN
      UPDATE REPLENISH_ALERT
         SET STATUS = 'CLOSED',
             CLOSED_AT = SYSDATE,
             QTY_AVAILABLE = p_qty_available,
             QTY_SUGGESTED = 0
       WHERE ITEM_ID = p_item_id
         AND WAREHOUSE_ID = p_warehouse_id
         AND STATUS = 'OPEN';
    END IF;
  END upsert_alert;

  -----------------------------------------------------------------------------
  -- W1: check_material_availability
  --     Locks the PO, explodes its ACTIVE BOM against plant availability
  --     (stock minus other orders' holds), moves the PO to MATERIAL_CHECK
  --     then READY or WAITING_MATERIAL, and raises/refreshes alerts for
  --     every short material. Shortage is reported via p_status, not raised.
  -----------------------------------------------------------------------------
  PROCEDURE check_material_availability (
    p_po_no  IN  VARCHAR2,
    p_user   IN  VARCHAR2 DEFAULT 'system',
    p_status OUT VARCHAR2
  ) IS
    v_started   DATE := SYSDATE;
    v_po_id     NUMBER;
    v_fg_id     NUMBER;
    v_wh_id     NUMBER;
    v_planned   NUMBER;
    v_status    VARCHAR2(20);
    v_bom_id    NUMBER;
    v_short_cnt NUMBER := 0;
    v_checked   NUMBER := 0;
    v_detail    VARCHAR2(2000) := '';
  BEGIN
    SELECT ID, FG_ITEM_ID, QTY_PLANNED, STATUS, WAREHOUSE_ID
      INTO v_po_id, v_fg_id, v_planned, v_status, v_wh_id
      FROM PRODUCTION_ORDER
     WHERE PO_NO = p_po_no
       FOR UPDATE;

    IF v_status IN ('COMPLETED', 'CANCELLED') THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Cannot check materials of order ' || p_po_no || ' in state ' || v_status || '.');
    END IF;

    BEGIN
      SELECT ID INTO v_bom_id
        FROM BOM
       WHERE FG_ITEM_ID = v_fg_id AND STATUS = 'ACTIVE' AND ROWNUM = 1;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        ERP_OPERATIONS.log_error('ERR_NO_ACTIVE_BOM',
          'No active BOM for FG item ' || v_fg_id, 'check_material_availability',
          p_po_no, p_user);
        RAISE_APPLICATION_ERROR(-20006, 'No active BOM found for product.');
    END;

    UPDATE PRODUCTION_ORDER SET STATUS = 'MATERIAL_CHECK' WHERE ID = v_po_id;

    FOR line IN (
      SELECT bd.MAT_ITEM_ID,
             it.CODE AS MAT_CODE,
             bd.QTY_REQUIRED,
             it.AVG_DAILY_USAGE,
             it.LEAD_TIME_DAYS,
             it.SAFETY_STOCK
        FROM BOM_DETAIL bd
        JOIN ITEM it ON bd.MAT_ITEM_ID = it.ID
       WHERE bd.BOM_ID = v_bom_id
    ) LOOP
      DECLARE
        v_need      NUMBER := line.QTY_REQUIRED * v_planned;
        v_avail     NUMBER;
        v_suggested NUMBER;
      BEGIN
        v_avail := f_available(v_wh_id, line.MAT_ITEM_ID, p_po_no);
        v_checked := v_checked + 1;

        IF v_avail < v_need THEN
          v_short_cnt := v_short_cnt + 1;
          v_detail := v_detail || line.MAT_CODE || ': need ' || v_need ||
                      ', available ' || v_avail || '; ';
          v_suggested := GREATEST(0,
            line.AVG_DAILY_USAGE * line.LEAD_TIME_DAYS
            + line.SAFETY_STOCK - v_avail);
          upsert_alert(line.MAT_ITEM_ID, v_wh_id, v_avail, v_suggested,
                       'PO_SHORTAGE', p_po_no, p_user);
        ELSE
          -- recovery path: close any OPEN alert this material still has
          upsert_alert(line.MAT_ITEM_ID, v_wh_id,
                       f_available(v_wh_id, line.MAT_ITEM_ID, NULL), 0,
                       'LOW_STOCK', NULL, p_user);
        END IF;
      END;
    END LOOP;

    IF v_short_cnt > 0 THEN
      p_status := 'WAITING_MATERIAL';
    ELSE
      p_status := 'READY';
    END IF;

    UPDATE PRODUCTION_ORDER SET STATUS = p_status WHERE ID = v_po_id;
    COMMIT;

    write_run('MATERIAL_CHECK', 'MANUAL', p_po_no, 'SUCCESS', v_started,
      'po=' || p_po_no || ' lines=' || v_checked || ' short=' || v_short_cnt ||
      ' status=' || p_status || CASE WHEN v_detail IS NOT NULL
                    THEN ' | ' || v_detail END,
      NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('MATERIAL_CHECK', 'MANUAL', p_po_no, 'FAILED', v_started,
        'po=' || p_po_no, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END check_material_availability;

  -----------------------------------------------------------------------------
  -- W2: reserve_materials - idempotent soft reservation of every BOM line.
  --     Availability uses stock minus OTHER orders' holds; the PO's own
  --     ACTIVE rows are reused (REPLACE) so a retry never double-books.
  --     Any shortage => ORA-20007 (same code as complete_production_order).
  -----------------------------------------------------------------------------
  PROCEDURE reserve_materials (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started   DATE := SYSDATE;
    v_po_id     NUMBER;
    v_fg_id     NUMBER;
    v_wh_id     NUMBER;
    v_planned   NUMBER;
    v_status    VARCHAR2(20);
    v_bom_id    NUMBER;
    v_short_msg VARCHAR2(2000) := '';
    v_reserved  NUMBER := 0;
  BEGIN
    SELECT ID, FG_ITEM_ID, QTY_PLANNED, STATUS, WAREHOUSE_ID
      INTO v_po_id, v_fg_id, v_planned, v_status, v_wh_id
      FROM PRODUCTION_ORDER
     WHERE PO_NO = p_po_no
       FOR UPDATE;

    IF v_status IN ('COMPLETED', 'CANCELLED') THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Cannot reserve materials of order ' || p_po_no || ' in state ' || v_status || '.');
    END IF;

    BEGIN
      SELECT ID INTO v_bom_id
        FROM BOM
       WHERE FG_ITEM_ID = v_fg_id AND STATUS = 'ACTIVE' AND ROWNUM = 1;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20006, 'No active BOM found for product.');
    END;

    FOR line IN (
      SELECT bd.MAT_ITEM_ID, it.CODE AS MAT_CODE, bd.QTY_REQUIRED
        FROM BOM_DETAIL bd
        JOIN ITEM it ON bd.MAT_ITEM_ID = it.ID
       WHERE bd.BOM_ID = v_bom_id
    ) LOOP
      DECLARE
        v_need  NUMBER := line.QTY_REQUIRED * v_planned;
        v_avail NUMBER;
      BEGIN
        -- exclude the PO's own ACTIVE rows: they are exactly what we (re)write
        v_avail := f_available(v_wh_id, line.MAT_ITEM_ID, p_po_no);

        IF v_avail < v_need THEN
          v_short_msg := v_short_msg || line.MAT_CODE || ' requires ' || v_need ||
                         ', available ' || v_avail || '; ';
        ELSE
          MERGE INTO STOCK_RESERVATION r
          USING (SELECT p_po_no AS PO_NO, line.MAT_ITEM_ID AS ITEM_ID,
                        v_wh_id AS WH_ID, v_need AS QTY FROM DUAL) s
             ON (r.PO_NO = s.PO_NO AND r.ITEM_ID = s.ITEM_ID
                 AND r.WAREHOUSE_ID = s.WH_ID AND r.STATUS = 'ACTIVE')
          WHEN MATCHED THEN
            UPDATE SET r.QTY = s.QTY, r.UPDATED_AT = SYSDATE
          WHEN NOT MATCHED THEN
            INSERT (PO_NO, ITEM_ID, WAREHOUSE_ID, QTY, STATUS)
            VALUES (s.PO_NO, s.ITEM_ID, s.WH_ID, s.QTY, 'ACTIVE');
          v_reserved := v_reserved + 1;
        END IF;
      END;
    END LOOP;

    -- NOTE: '' is NULL in Oracle, so only an IS NOT NULL test is meaningful.
    IF v_short_msg IS NOT NULL THEN
      ERP_OPERATIONS.log_error('ERR_MATERIAL_SHORTAGE', v_short_msg,
        'reserve_materials', p_po_no, p_user);
      RAISE_APPLICATION_ERROR(-20007,
        'Cannot reserve PO ' || p_po_no || ' due to material shortage: ' || v_short_msg);
    END IF;

    IF v_status IN ('RELEASED', 'MATERIAL_CHECK', 'WAITING_MATERIAL') THEN
      UPDATE PRODUCTION_ORDER SET STATUS = 'READY' WHERE ID = v_po_id;
    END IF;
    COMMIT;

    write_run('MATERIAL_RESERVE', 'MANUAL', p_po_no, 'SUCCESS', v_started,
      'po=' || p_po_no || ' lines_reserved=' || v_reserved, NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('MATERIAL_RESERVE', 'MANUAL', p_po_no, 'FAILED', v_started,
        'po=' || p_po_no, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END reserve_materials;

  -----------------------------------------------------------------------------
  -- W2 undo: release_reservations - free ACTIVE holds, PO back to RELEASED
  --          (terminal orders are refused with -20011).
  -----------------------------------------------------------------------------
  PROCEDURE release_reservations (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started   DATE := SYSDATE;
    v_po_id     NUMBER;
    v_status    VARCHAR2(20);
    v_released  NUMBER := 0;
  BEGIN
    SELECT ID, STATUS INTO v_po_id, v_status
      FROM PRODUCTION_ORDER
     WHERE PO_NO = p_po_no
       FOR UPDATE;

    IF v_status IN ('COMPLETED', 'CANCELLED') THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Cannot release reservations of order ' || p_po_no || ' in state ' || v_status || '.');
    END IF;

    UPDATE STOCK_RESERVATION
       SET STATUS = 'RELEASED', UPDATED_AT = SYSDATE
     WHERE PO_NO = p_po_no AND STATUS = 'ACTIVE';
    v_released := SQL%ROWCOUNT;

    UPDATE PRODUCTION_ORDER SET STATUS = 'RELEASED' WHERE ID = v_po_id;
    COMMIT;

    write_run('MATERIAL_RELEASE', 'MANUAL', p_po_no, 'SUCCESS', v_started,
      'po=' || p_po_no || ' released=' || v_released, NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('MATERIAL_RELEASE', 'MANUAL', p_po_no, 'FAILED', v_started,
        'po=' || p_po_no, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END release_reservations;

  -----------------------------------------------------------------------------
  -- W3: evaluate_replenishment - one (item, warehouse) line.
  --     suggested = ADU * lead_days + safety - available; the alert opens
  --     below REORDER_POINT and closes on recovery (see upsert_alert).
  -----------------------------------------------------------------------------
  PROCEDURE evaluate_replenishment (
    p_item_code  IN  VARCHAR2,
    p_wh_code    IN  VARCHAR2,
    p_trigger    IN  VARCHAR2 DEFAULT 'LOW_STOCK',
    p_user       IN  VARCHAR2 DEFAULT 'system',
    p_suggested  OUT NUMBER
  ) IS
    v_started  DATE := SYSDATE;
    v_item_id  NUMBER;
    v_wh_id    NUMBER;
    v_avail    NUMBER;
    v_item     ITEM%ROWTYPE;
  BEGIN
    IF p_trigger NOT IN ('LOW_STOCK', 'PO_SHORTAGE', 'MANUAL', 'SWEEP') THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Unknown replenishment trigger type: ' || NVL(p_trigger, '<null>'));
    END IF;

    BEGIN
      SELECT ID INTO v_item_id FROM ITEM WHERE CODE = p_item_code;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002, 'Item code ' || p_item_code || ' not found.');
    END;

    BEGIN
      SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = p_wh_code;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002, 'Warehouse code ' || p_wh_code || ' not found.');
    END;

    BEGIN
      SELECT * INTO v_item FROM ITEM WHERE ID = v_item_id;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002, 'Item code ' || p_item_code || ' not found.');
    END;
    v_avail := f_available(v_wh_id, v_item_id, NULL);  -- plant-wide holds count

    p_suggested := GREATEST(0,
      v_item.AVG_DAILY_USAGE * v_item.LEAD_TIME_DAYS
      + v_item.SAFETY_STOCK - v_avail);

    upsert_alert(v_item_id, v_wh_id, v_avail, p_suggested,
                 p_trigger, p_item_code || '@' || p_wh_code, p_user);
    COMMIT;

    write_run('REPLENISHMENT_EVALUATE', 'MANUAL',
      p_item_code || '@' || p_wh_code, 'SUCCESS', v_started,
      'item=' || p_item_code || ' wh=' || p_wh_code ||
      ' available=' || v_avail || ' suggested=' || p_suggested ||
      ' reorder_point=' || v_item.REORDER_POINT,
      NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('REPLENISHMENT_EVALUATE', 'MANUAL',
        p_item_code || '@' || p_wh_code, 'FAILED', v_started,
        'item=' || p_item_code, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END evaluate_replenishment;

  -----------------------------------------------------------------------------
  -- W3 batch: sweep_replenishment - evaluate every RAW material stocked in
  --           the plant; p_alerts returns the OPEN alert count afterwards.
  -----------------------------------------------------------------------------
  PROCEDURE sweep_replenishment (
    p_user   IN  VARCHAR2 DEFAULT 'system',
    p_alerts OUT NUMBER
  ) IS
    v_started DATE := SYSDATE;
    v_lines   NUMBER := 0;
    v_opens   NUMBER := 0;
  BEGIN
    FOR r IN (
      SELECT DISTINCT i.ID AS ITEM_ID, s.WAREHOUSE_ID AS WH_ID, i.CODE AS ITEM_CODE
        FROM STOCK s
        JOIN ITEM i ON s.ITEM_ID = i.ID
       WHERE i.ITEM_TYPE = 'RAW'
    ) LOOP
      DECLARE
        v_item      ITEM%ROWTYPE;
        v_avail     NUMBER;
        v_suggested NUMBER;
      BEGIN
        BEGIN
          SELECT * INTO v_item FROM ITEM WHERE ID = r.ITEM_ID;
        EXCEPTION
          WHEN NO_DATA_FOUND THEN
            CONTINUE; -- item vanished mid-sweep; skip this stock line
        END;
        v_avail := f_available(r.WH_ID, r.ITEM_ID, NULL);
        v_suggested := GREATEST(0,
          v_item.AVG_DAILY_USAGE * v_item.LEAD_TIME_DAYS
          + v_item.SAFETY_STOCK - v_avail);
        upsert_alert(r.ITEM_ID, r.WH_ID, v_avail, v_suggested,
                     'SWEEP', r.ITEM_CODE || '@' || p_user, p_user);
        v_lines := v_lines + 1;
      END;
    END LOOP;

    SELECT COUNT(*) INTO v_opens FROM REPLENISH_ALERT WHERE STATUS = 'OPEN';
    p_alerts := v_opens;
    COMMIT;

    write_run('REPLENISHMENT_SWEEP', 'SCHEDULED', NULL, 'SUCCESS', v_started,
      'lines=' || v_lines || ' open_alerts=' || v_opens, NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('REPLENISHMENT_SWEEP', 'SCHEDULED', NULL, 'FAILED', v_started,
        NULL, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END sweep_replenishment;

  -----------------------------------------------------------------------------
  -- W4: complete_reserved_order - transactional completion wrapper.
  --     IN_PROGRESS + CONSUMED flips and the physical movement delegated to
  --     ERP_OPERATIONS.complete_production_order inside ONE transaction: on
  --     rolls back (reservations stay ACTIVE) while the FAILED audit row and
  --     the ERROR_LOG row (both autonomous) survive.
  --     A PO never reserved simply skips the flip (legacy fallback).
  -----------------------------------------------------------------------------
  PROCEDURE complete_reserved_order (
    p_po_no IN VARCHAR2,
    p_user  IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started  DATE := SYSDATE;
    v_po_id    NUMBER;
    v_status   VARCHAR2(20);
    v_flipped  NUMBER := 0;
  BEGIN
    SELECT ID, STATUS INTO v_po_id, v_status
      FROM PRODUCTION_ORDER
     WHERE PO_NO = p_po_no
       FOR UPDATE;

    IF v_status IN ('COMPLETED', 'CANCELLED') THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Cannot complete order ' || p_po_no || ' in state ' || v_status || '.');
    END IF;

    UPDATE PRODUCTION_ORDER SET STATUS = 'IN_PROGRESS' WHERE ID = v_po_id;

    UPDATE STOCK_RESERVATION
       SET STATUS = 'CONSUMED', UPDATED_AT = SYSDATE
     WHERE PO_NO = p_po_no AND STATUS = 'ACTIVE';
    v_flipped := SQL%ROWCOUNT;

    -- Physical stock movement: commits this transaction on success and rolls
    -- everything above back on shortage (its own handler logs ERROR_LOG).
    ERP_OPERATIONS.complete_production_order(p_po_no, p_user);

    write_run('PRODUCTION_COMPLETE', 'MANUAL', p_po_no, 'SUCCESS', v_started,
      'po=' || p_po_no || ' reservations_consumed=' || v_flipped ||
      ' (single transaction with stock movement)', NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('PRODUCTION_COMPLETE', 'MANUAL', p_po_no, 'FAILED', v_started,
        'po=' || p_po_no || ' (all changes rolled back)',
        TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END complete_reserved_order;

  -----------------------------------------------------------------------------
  -- W5: detect_stale_orders - non-terminal PO with no state change for
  --     p_days days. Uses PO_STATE_HISTORY (trigger-maintained) with a
  --     CREATED_AT fallback. Stale findings => run status MANUAL_REVIEW.
  -----------------------------------------------------------------------------
  PROCEDURE detect_stale_orders (
    p_days        IN  NUMBER DEFAULT 7,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_stale_count OUT NUMBER
  ) IS
    v_started DATE := SYSDATE;
    v_days    NUMBER := NVL(p_days, 7);
    v_cnt     NUMBER := 0;
    v_sample  VARCHAR2(500);
    v_status  VARCHAR2(20);
  BEGIN
    IF v_days < 0 THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Stale threshold must be >= 0 days, got ' || v_days || '.');
    END IF;

    SELECT COUNT(*), MAX(PO_NO) INTO v_cnt, v_sample
      FROM (
        SELECT po.PO_NO
          FROM PRODUCTION_ORDER po
         WHERE po.STATUS NOT IN ('COMPLETED', 'CANCELLED')
           AND NVL((SELECT MAX(h.CHANGED_AT) FROM PO_STATE_HISTORY h
                     WHERE h.PO_NO = po.PO_NO), po.CREATED_AT)
               < SYSDATE - v_days
      );

    p_stale_count := v_cnt;
    v_status := CASE WHEN v_cnt > 0 THEN 'MANUAL_REVIEW' ELSE 'SUCCESS' END;
    COMMIT;

    write_run('STALE_PO_DETECTION', 'SCHEDULED', 'days=' || v_days, v_status,
      v_started, 'threshold_days=' || v_days || ' stale_orders=' || v_cnt ||
      CASE WHEN v_sample IS NOT NULL THEN ' sample=' || v_sample END,
      NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('STALE_PO_DETECTION', 'SCHEDULED', 'days=' || v_days, 'FAILED',
        v_started, NULL, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END detect_stale_orders;

  -----------------------------------------------------------------------------
  -- W6: generate_report - persist one operational report as a JSON array
  --     snapshot. LINE_COUNT feeds the API contract; the payload is capped
  --     at 4000 chars by the column itself (SUBSTR guard added here so the
  --     INSERT can never raise ORA-12899 on oversized aggregates).
  -----------------------------------------------------------------------------
  PROCEDURE generate_report (
    p_report_type IN  VARCHAR2,
    p_period_from IN  DATE DEFAULT NULL,
    p_period_to   IN  DATE DEFAULT NULL,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_report_id   OUT NUMBER
  ) IS
    v_started  DATE := SYSDATE;
    v_from     DATE := p_period_from;
    v_to       DATE := NVL(p_period_to, SYSDATE);
    v_lines    NUMBER := 0;
    v_payload  VARCHAR2(4000);
  BEGIN
    IF p_report_type NOT IN ('INVENTORY_SNAPSHOT', 'LOW_STOCK', 'PO_BY_STATUS',
                             'COMPLETED_PRODUCTION', 'STOCK_MOVEMENTS',
                             'FAILED_OPERATIONS') THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Unknown report type: ' || NVL(p_report_type, '<null>'));
    END IF;
    IF v_from IS NULL THEN
      v_from := v_to - 30;  -- default window: last 30 days
    END IF;
    IF v_from > v_to THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Report period_from must be <= period_to.');
    END IF;

    IF p_report_type = 'INVENTORY_SNAPSHOT' THEN
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'warehouse' VALUE w.CODE,
                 'item'      VALUE i.CODE,
                 'itemType'  VALUE i.ITEM_TYPE,
                 'uom'       VALUE i.UOM,
                 'qty'       VALUE s.QTY,
                 'minStock'  VALUE i.MIN_STOCK,
                 'reorderPoint' VALUE i.REORDER_POINT
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM STOCK s
        JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
        JOIN ITEM i ON s.ITEM_ID = i.ID;

    ELSIF p_report_type = 'LOW_STOCK' THEN
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'warehouse' VALUE w.CODE,
                 'item'      VALUE i.CODE,
                 'qty'       VALUE s.QTY,
                 'minStock'  VALUE i.MIN_STOCK,
                 'belowMin'  VALUE
                   CASE WHEN s.QTY < i.MIN_STOCK THEN 'Y' ELSE 'N' END,
                 'reorderPoint' VALUE i.REORDER_POINT
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM STOCK s
        JOIN WAREHOUSE w ON s.WAREHOUSE_ID = w.ID
        JOIN ITEM i ON s.ITEM_ID = i.ID
       WHERE s.QTY < i.MIN_STOCK OR s.QTY < i.REORDER_POINT;

    ELSIF p_report_type = 'PO_BY_STATUS' THEN
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'status' VALUE STATUS,
                 'orders' VALUE CNT,
                 'plannedQty' VALUE TOTAL_PLANNED
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM (
          SELECT STATUS, COUNT(*) AS CNT, SUM(QTY_PLANNED) AS TOTAL_PLANNED
            FROM PRODUCTION_ORDER
           GROUP BY STATUS
        );

    ELSIF p_report_type = 'COMPLETED_PRODUCTION' THEN
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'poNo'      VALUE PO_NO,
                 'qtyDone'   VALUE QTY_DONE,
                 'completedAt' VALUE TO_CHAR(COMPLETED_AT, 'YYYY-MM-DD HH24:MI')
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM PRODUCTION_ORDER
       WHERE STATUS = 'COMPLETED'
         AND COMPLETED_AT BETWEEN v_from AND v_to;

    ELSIF p_report_type = 'STOCK_MOVEMENTS' THEN
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'txnType' VALUE TXN_TYPE,
                 'item'    VALUE i.CODE,
                 'qty'     VALUE t.QTY,
                 'balanceAfter' VALUE t.BALANCE_AFTER,
                 'refNo'   VALUE t.REF_NO,
                 'at'      VALUE TO_CHAR(t.CREATED_AT, 'YYYY-MM-DD HH24:MI')
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM INVENTORY_TRANSACTION t
        JOIN ITEM i ON t.ITEM_ID = i.ID
       WHERE t.CREATED_AT BETWEEN v_from AND v_to;

    ELSE  -- FAILED_OPERATIONS
      SELECT COUNT(*),
             SUBSTR(NVL(JSON_ARRAYAGG(
               JSON_OBJECT(
                 'errCode' VALUE ERR_CODE,
                 'message' VALUE SUBSTR(MESSAGE, 1, 200),
                 'proc'    VALUE PROC_NAME,
                 'refNo'   VALUE REF_NO,
                 'at'      VALUE TO_CHAR(CREATED_AT, 'YYYY-MM-DD HH24:MI')
               ) RETURNING CLOB), '[]'), 1, 4000)
        INTO v_lines, v_payload
        FROM ERROR_LOG
       WHERE CREATED_AT BETWEEN v_from AND v_to;
    END IF;

    INSERT INTO AUTOMATION_REPORT
      (REPORT_TYPE, PERIOD_FROM, PERIOD_TO, LINE_COUNT, PAYLOAD_JSON, GENERATED_BY)
    VALUES
      (p_report_type, v_from, v_to, v_lines, v_payload, p_user)
    RETURNING ID INTO p_report_id;
    COMMIT;

    write_run('REPORT_GENERATION', 'SCHEDULED', p_report_type, 'SUCCESS',
      v_started, 'report_id=' || p_report_id || ' type=' || p_report_type ||
      ' lines=' || v_lines, NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('REPORT_GENERATION', 'SCHEDULED', p_report_type, 'FAILED',
        v_started, NULL, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END generate_report;

  -----------------------------------------------------------------------------
  -- W7: collect_incident_context - snapshot everything a support engineer
  --     needs for one reference: recent ERROR_LOG rows, PO state, open
  --     replenishment alerts, and a deterministic rule-based DIAGNOSIS
  --     (guarantee c: diagnosis is advisory text, never a DML driver).
  -----------------------------------------------------------------------------
  PROCEDURE collect_incident_context (
    p_ref_no      IN  VARCHAR2,
    p_error_code  IN  VARCHAR2 DEFAULT 'ERR_MATERIAL_SHORTAGE',
    p_title       IN  VARCHAR2 DEFAULT NULL,
    p_user        IN  VARCHAR2 DEFAULT 'system',
    p_incident_id OUT NUMBER
  ) IS
    v_started  DATE := SYSDATE;
    v_errors   NUMBER := 0;
    v_alerts   NUMBER := 0;
    v_po_state VARCHAR2(20);
    v_context  VARCHAR2(4000);
    v_diagnosis VARCHAR2(1000);
  BEGIN
    -- '' is NULL in Oracle: IS NULL already catches empty input, LENGTH guard
    -- additionally rejects whitespace-only references.
    IF p_ref_no IS NULL OR LENGTH(TRIM(p_ref_no)) = 0 THEN
      RAISE_APPLICATION_ERROR(-20012, 'Incident reference must not be empty.');
    END IF;

    SELECT COUNT(*) INTO v_errors
      FROM ERROR_LOG WHERE REF_NO = p_ref_no;

    SELECT COUNT(*) INTO v_alerts
      FROM REPLENISH_ALERT
     WHERE REF_NO = p_ref_no AND STATUS = 'OPEN';

    BEGIN
      SELECT STATUS INTO v_po_state
        FROM PRODUCTION_ORDER WHERE PO_NO = p_ref_no;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        v_po_state := NULL;
    END;

    -- JSON context built entirely in SQL: log codes, counts, PO state.
    SELECT SUBSTR(
             JSON_OBJECT(
               'refNo'       VALUE p_ref_no,
               'errorCode'   VALUE p_error_code,
               'errorLogRows' VALUE v_errors,
               'poStatus'    VALUE v_po_state,
               'openAlerts'  VALUE v_alerts,
               'capturedAt'  VALUE TO_CHAR(SYSDATE, 'YYYY-MM-DD HH24:MI:SS'),
               'capturedBy'  VALUE p_user,
               'errorCodes'  VALUE (
                 SELECT NVL(JSON_ARRAYAGG(err_code ORDER BY ID DESC
                            RETURNING CLOB), '[]')
                   FROM (SELECT ERR_CODE, ID FROM ERROR_LOG
                          WHERE REF_NO = p_ref_no AND ROWNUM <= 20)
               ) RETURNING CLOB
             ), 1, 4000)
      INTO v_context
      FROM DUAL;

    -- Deterministic rule-based diagnosis (advisory only).
    IF p_error_code = 'ERR_MATERIAL_SHORTAGE' THEN
      v_diagnosis := 'Likely material shortage: check REPLENISH_ALERT rows for ' ||
        'this reference, verify STOCK vs BOM requirement, then receive the ' ||
        'pending purchase order or free competing reservations (release_reservations).';
    ELSIF p_error_code = 'ERR_INSUFFICIENT_STOCK' THEN
      v_diagnosis := 'Stock balance below the requested movement: reconcile ' ||
        'STOCK with INVENTORY_TRANSACTION and run a physical count before retrying.';
    ELSIF p_error_code = 'ERR_NO_ACTIVE_BOM' THEN
      v_diagnosis := 'No ACTIVE BOM for the finished good: ask Engineering ' ||
        'to release a BOM version (save_bom_line) before planning production.';
    ELSE
      v_diagnosis := 'General incident for reference ' || p_ref_no ||
        ': review the attached ERROR_LOG snapshot and the ERP support runbook.';
    END IF;

    INSERT INTO SUPPORT_INCIDENT
      (ERROR_CODE, REF_NO, TITLE, CONTEXT_JSON, DIAGNOSIS, STATUS, CREATED_BY)
    VALUES
      (p_error_code, p_ref_no,
       SUBSTR(NVL(p_title, p_error_code || ' on ' || p_ref_no), 1, 200),
       v_context, v_diagnosis, 'OPEN', p_user)
    RETURNING ID INTO p_incident_id;
    COMMIT;

    write_run('INCIDENT_CONTEXT', 'EVENT', p_ref_no, 'SUCCESS', v_started,
      'incident_id=' || p_incident_id || ' ref=' || p_ref_no ||
      ' error_rows=' || v_errors || ' open_alerts=' || v_alerts,
      NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('INCIDENT_CONTEXT', 'EVENT', p_ref_no, 'FAILED', v_started,
        'ref=' || p_ref_no, TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END collect_incident_context;

  -----------------------------------------------------------------------------
  -- Approval gate: request_approval - register a PENDING approval token.
  -----------------------------------------------------------------------------
  PROCEDURE request_approval (
    p_approval_no IN VARCHAR2,
    p_action      IN VARCHAR2,
    p_ref_no      IN VARCHAR2,
    p_payload     IN VARCHAR2,
    p_requester   IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started DATE := SYSDATE;
    v_dup     NUMBER;
  BEGIN
    IF p_approval_no IS NULL OR LENGTH(TRIM(p_approval_no)) = 0 THEN
      RAISE_APPLICATION_ERROR(-20012, 'Approval number must not be empty.');
    END IF;
    IF p_action NOT IN ('INVENTORY_ADJUST', 'COMPLETE_OVERRIDE',
                        'BOM_FORCE_SAVE', 'STOCK_OVERRIDE') THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Unknown approval action: ' || NVL(p_action, '<null>'));
    END IF;

    SELECT COUNT(*) INTO v_dup FROM APPROVAL_REQUEST WHERE APPROVAL_NO = p_approval_no;
    IF v_dup > 0 THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Approval ' || p_approval_no || ' already exists.');
    END IF;

    INSERT INTO APPROVAL_REQUEST
      (APPROVAL_NO, ACTION, REF_NO, PAYLOAD, STATUS, REQUESTER)
    VALUES
      (p_approval_no, p_action, p_ref_no, p_payload, 'PENDING', p_requester);
    COMMIT;

    write_run('APPROVAL_REQUEST', 'MANUAL', p_approval_no, 'SUCCESS', v_started,
      'approval=' || p_approval_no || ' action=' || p_action ||
      ' ref=' || p_ref_no, NULL, NULL, p_requester);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('APPROVAL_REQUEST', 'MANUAL', p_approval_no, 'FAILED', v_started,
        'approval=' || p_approval_no, TO_CHAR(SQLCODE), SQLERRM, p_requester);
      RAISE;
  END request_approval;

  -----------------------------------------------------------------------------
  -- decide_approval - PENDING -> APPROVED | REJECTED (one-shot).
  -----------------------------------------------------------------------------
  PROCEDURE decide_approval (
    p_approval_no IN VARCHAR2,
    p_decision    IN VARCHAR2,
    p_approver    IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started DATE := SYSDATE;
    v_status  VARCHAR2(20);
  BEGIN
    IF p_decision NOT IN ('APPROVED', 'REJECTED') THEN
      RAISE_APPLICATION_ERROR(-20012,
        'Approval decision must be APPROVED or REJECTED, got: ' ||
        NVL(p_decision, '<null>'));
    END IF;

    BEGIN
      SELECT STATUS INTO v_status
        FROM APPROVAL_REQUEST
       WHERE APPROVAL_NO = p_approval_no
       FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002,
          'Approval ' || p_approval_no || ' not found.');
    END;

    IF v_status <> 'PENDING' THEN
      RAISE_APPLICATION_ERROR(-20011,
        'Approval ' || p_approval_no || ' is ' || v_status ||
        ', only PENDING can be decided.');
    END IF;

    UPDATE APPROVAL_REQUEST
       SET STATUS = p_decision, APPROVER = p_approver, DECIDED_AT = SYSDATE
     WHERE APPROVAL_NO = p_approval_no;
    COMMIT;

    write_run('APPROVAL_DECISION', 'MANUAL', p_approval_no, 'SUCCESS', v_started,
      'approval=' || p_approval_no || ' decision=' || p_decision ||
      ' approver=' || p_approver, NULL, NULL, p_approver);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('APPROVAL_DECISION', 'MANUAL', p_approval_no, 'FAILED', v_started,
        'approval=' || p_approval_no, TO_CHAR(SQLCODE), SQLERRM, p_approver);
      RAISE;
  END decide_approval;

  -----------------------------------------------------------------------------
  -- adjust_stock - approval-gated inventory override. The only automation
  --     path allowed to touch STOCK directly, and only with an APPROVED
  --     (not yet EXECUTED) INVENTORY_ADJUST token. Writes the signed
  --     ADJUSTMENT ledger row in the same transaction, so the invariant
  --     STOCK.QTY = SUM(INVENTORY_TRANSACTION.QTY) still holds (guarantee a).
  -----------------------------------------------------------------------------
  PROCEDURE adjust_stock (
    p_wh_code     IN VARCHAR2,
    p_item_code   IN VARCHAR2,
    p_qty_delta   IN NUMBER,
    p_approval_no IN VARCHAR2,
    p_user        IN VARCHAR2 DEFAULT 'system'
  ) IS
    v_started   DATE := SYSDATE;
    v_wh_id     NUMBER;
    v_item_id   NUMBER;
    v_apv_status VARCHAR2(20);
    v_apv_action VARCHAR2(40);
    v_new_bal   NUMBER;
  BEGIN
    IF p_qty_delta = 0 OR p_qty_delta IS NULL THEN
      RAISE_APPLICATION_ERROR(-20012, 'Adjustment delta must be non-zero.');
    END IF;

    BEGIN
      SELECT ID INTO v_wh_id FROM WAREHOUSE WHERE CODE = p_wh_code;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002, 'Warehouse code ' || p_wh_code || ' not found.');
    END;
    BEGIN
      SELECT ID INTO v_item_id FROM ITEM WHERE CODE = p_item_code;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20002, 'Item code ' || p_item_code || ' not found.');
    END;

    BEGIN
      SELECT STATUS, ACTION INTO v_apv_status, v_apv_action
        FROM APPROVAL_REQUEST
       WHERE APPROVAL_NO = p_approval_no
       FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        RAISE_APPLICATION_ERROR(-20010,
          'Approval token ' || NVL(p_approval_no, '<null>') || ' not found.');
    END;

    IF v_apv_action <> 'INVENTORY_ADJUST' THEN
      RAISE_APPLICATION_ERROR(-20010,
        'Approval ' || p_approval_no || ' is for action ' || v_apv_action ||
        ', not INVENTORY_ADJUST.');
    END IF;
    IF v_apv_status <> 'APPROVED' THEN
      RAISE_APPLICATION_ERROR(-20010,
        'Approval ' || p_approval_no || ' must be APPROVED, current status is ' ||
        v_apv_status || '.');
    END IF;

    -- Lock/create the stock line, apply the delta, refuse negative balance.
    BEGIN
      SELECT QTY INTO v_new_bal
        FROM STOCK
       WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id
       FOR UPDATE;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        v_new_bal := 0;
    END;

    IF v_new_bal + p_qty_delta < 0 THEN
      RAISE_APPLICATION_ERROR(-20003,
        'Adjustment would drive ' || p_item_code || ' negative: balance ' ||
        v_new_bal || ', delta ' || p_qty_delta || '.');
    END IF;

    MERGE INTO STOCK s
    USING (SELECT v_wh_id AS WH_ID, v_item_id AS ITEM_ID,
                  p_qty_delta AS DELTA FROM DUAL) d
       ON (s.WAREHOUSE_ID = d.WH_ID AND s.ITEM_ID = d.ITEM_ID)
    WHEN MATCHED THEN
      UPDATE SET s.QTY = s.QTY + d.DELTA, s.UPDATED_AT = SYSDATE
    WHEN NOT MATCHED THEN
      INSERT (WAREHOUSE_ID, ITEM_ID, QTY, UPDATED_AT)
      VALUES (d.WH_ID, d.ITEM_ID, d.DELTA, SYSDATE);

    SELECT QTY INTO v_new_bal
      FROM STOCK WHERE WAREHOUSE_ID = v_wh_id AND ITEM_ID = v_item_id;

    -- Real signed delta + balance-after => ledger invariant preserved.
    INSERT INTO INVENTORY_TRANSACTION
      (TXN_TYPE, ITEM_ID, WAREHOUSE_ID, QTY, BALANCE_AFTER, REF_NO, CREATED_BY)
    VALUES
      ('ADJUSTMENT', v_item_id, v_wh_id, p_qty_delta, v_new_bal,
       p_approval_no, p_user);

    -- One-shot consumption of the approval token.
    UPDATE APPROVAL_REQUEST
       SET STATUS = 'EXECUTED', DECIDED_AT = NVL(DECIDED_AT, SYSDATE)
     WHERE APPROVAL_NO = p_approval_no;

    COMMIT;

    write_run('INVENTORY_ADJUST', 'MANUAL', p_approval_no, 'SUCCESS', v_started,
      'item=' || p_item_code || ' wh=' || p_wh_code ||
      ' delta=' || p_qty_delta || ' new_balance=' || v_new_bal,
      NULL, NULL, p_user);
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK;
      write_run('INVENTORY_ADJUST', 'MANUAL', p_approval_no, 'FAILED', v_started,
        'item=' || p_item_code || ' delta=' || p_qty_delta,
        TO_CHAR(SQLCODE), SQLERRM, p_user);
      RAISE;
  END adjust_stock;

END ERP_AUTOMATION;
/

-- ============================================================================
-- end of sql/06_automation_plsql.sql
-- ============================================================================