# Phase 1 — Traceability schema foundation (additive, backward compatible)

Status: PASS (Oracle-backed verification completed on 2026-09-24).

## SQL
- `sql/07_traceability_schema.sql` (NEW, 408 lines, rerunnable existence-guarded):
  ITEM.TRACE_MODE/SHELF_LIFE_DAYS/LABEL_TEMPLATE (+ CK_ITEM_TRACE_MODE),
  WAREHOUSE_LOCATION (+ UQ wh,loc),
  INVENTORY_LOT (+ CK_LOT_DATES, one code = one item),
  LOT_STOCK (+ UQ lot,wh,loc; CK reserved<=onhand),
  TRACEABILITY_EVENT (+ UQ idempotency),
  PRODUCTION_LOT_CONSUMPTION + PRODUCTION_LOT_OUTPUT (genealogy bridge),
  BARCODE_IDENTIFIER, LABEL_PRINT_JOB, REQUEST_IDEMPOTENCY,
  APP_USER.PASSWORD_HASH/SALT/ITERATIONS/LAST_LOGIN_AT, APP_AUDIT_EVENT.
- `sql/08_traceability_plsql.sql` (NEW):
  package ERP_TRACEABILITY v1.0.0-phase1 with package_version + ensure_location
  (idempotent MERGE, LOCATION_TYPE validated, uses existing -20002/-20012 codes
  and ERP_OPERATIONS.log_error for autonomous error logging).
- `scripts/run-sql.sh`: load order extended to
  01 -> 05 -> 07 -> 02 -> 06 -> 08 -> 03 (-> 04); verify block now requires
  3 packages VALID and >= 31 tables (13 core + 7 automation + 10 traceability/security + 1 Helpdesk outbox).

## C# (unit-testable, no DB required)
- `src/Models/ErpTraceabilityDtos.cs`: location/lot/receive/move/barcode/label/trace DTOs.
- `src/Services/TraceabilityLogic.cs`: ResolveScan / HashRequest / OrderForIssue (FEFO+FIFO) / IsIssuableLotStatus.
- `src/Services/PasswordHashService.cs`: PBKDF2-SHA256 hash/verify (210k iterations).
- `src/Services/LabelRenderService.cs`: deterministic HTML + ZPL label preview.

## Tests
- `tests/MiniERP.Api.Tests/TraceabilityContractTests.cs` (31 new cases):
  scan mapping (10) + invalid (4), idempotency hash, FEFO/FIFO order,
  issuable gating (7), location-type validation (4), HTML/ZPL render (3), PBKDF2 (2).
- Suite at the time of this phase: 68 database-free tests passed; final full suite is 164/164 after all phases.
- Oracle-backed traceability E2E is recorded in `artifacts/factory-upgrade/final/evidence/traceability-e2e.log` (61/61 checks).

## Compatibility
- No existing table/route/type renamed or removed. Old tests untouched.
- Legacy APP_USER.PASSWORD column untouched (hash columns additive).
