-- ============================================================================
-- PROJECT: 04-MiniERP-Manufacturing-Warehouse
-- FILE:    sql/00_reset_schema.sql
-- PURPOSE: drop every object owned by the schema user so that run-sql.sh can be
--          re-run against a *clean* schema. Only executed when RESET=1:
--            RESET=1 bash scripts/run-sql.sh
--          Stop the API first (docker compose stop api): a live session holding
--          DML locks makes DROP fail with ORA-00054 and leaves a half schema.
-- NOTE:    dependents are dropped first (triggers, views, packages, sequences,
--          then tables); objects that already vanished are reported as "skip"
--          and are not treated as an error.
-- ============================================================================
SET DEFINE OFF
SET SERVEROUTPUT ON SIZE UNLIMITED
DECLARE
  CURSOR c IS
    SELECT object_type, object_name FROM user_objects
     WHERE object_type IN ('TRIGGER','SYNONYM','VIEW','PACKAGE BODY','PACKAGE','PROCEDURE','FUNCTION','SEQUENCE','TABLE')
       AND object_name NOT LIKE 'BIN$'
     ORDER BY CASE object_type
                WHEN 'TRIGGER' THEN 1 WHEN 'SYNONYM' THEN 2 WHEN 'VIEW' THEN 3
                WHEN 'PACKAGE BODY' THEN 4 WHEN 'PACKAGE' THEN 5
                WHEN 'PROCEDURE' THEN 6 WHEN 'FUNCTION' THEN 7
                WHEN 'SEQUENCE' THEN 8 ELSE 9 END;
  v_sql  VARCHAR2(500);
  v_done NUMBER := 0;
  v_skip NUMBER := 0;
BEGIN
  FOR r IN c LOOP
    IF r.object_type = 'TABLE' THEN
      v_sql := 'DROP TABLE "' || r.object_name || '" CASCADE CONSTRAINTS PURGE';
    ELSE
      v_sql := 'DROP ' || r.object_type || ' "' || r.object_name || '"';
    END IF;
    BEGIN
      EXECUTE IMMEDIATE v_sql;
      v_done := v_done + 1;
    EXCEPTION WHEN OTHERS THEN
      v_skip := v_skip + 1;
      DBMS_OUTPUT.PUT_LINE('skip ' || r.object_type || ' ' || r.object_name || ': ' || SQLERRM);
    END;
  END LOOP;
  DBMS_OUTPUT.PUT_LINE('DROPPED=' || v_done || ' SKIPPED=' || v_skip);
  DBMS_OUTPUT.PUT_LINE('ERP_SCHEMA_RESET_DONE');
END;
/
SELECT 'TABLES_LEFT=' || COUNT(*) FROM user_tables;
