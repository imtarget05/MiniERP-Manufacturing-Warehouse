# Phase 0 — Repository map (verified 2026-09-24)

## Endpoints (src/Program.cs, ~31 routes)
- GET /api/health
- GET /api/warehouse | GET /api/stock/{warehouseCode} | POST /api/stock/in | POST /api/stock/out
- POST /api/manufacturing/bom/line | POST /api/manufacturing/production-order
  GET /api/manufacturing/production-order/{poNo}
  POST /api/manufacturing/production-order/{poNo}/complete | .../cancel
- procurement purchase-order create/receive (see Program.cs section 3)
- GET /api/support/errors | POST /api/support/change-requests | GET /api/support/change-requests/{crNo}
- automation: material-check | reserve | release | complete (reserved)
  replenishment evaluate/sweep/alerts | stale-orders detect/list
  reports generate/list | incidents collect/list | approvals request/decide/list | stock/adjust

## Schema (sql/)
- 01_schema.sql: 13 core tables (WAREHOUSE..CHANGE_REQUEST)
- 05_automation_schema.sql: 7 automation tables + PO status widening + ITEM replenishment cols
- 02_plsql.sql: package ERP_OPERATIONS (stock/bom/PO/PO-receive)
- 06_automation_plsql.sql: package ERP_AUTOMATION (W1..W7 + approval gate)
- 03_seed.sql: footwear plant seed (WH_RAW/WH_FG/WH_WIP, 5 RAW + 2 FG, BOM V1.0, opening STOCK+txn, roles/users)
- 04_incident_scenarios.sql: PO001 shortage demo (optional)

## Service layer (src/)
- src/Services/ErpDbService.cs (namespace MiniERP.Api.Services) — EXTEND THIS, do not duplicate
- src/Services/ErpErrorMapper.cs — error codes -20001..-20012
- src/ErpApiPresenter.cs — HTTP status mapping (404/400/403/500/409)
- src/Models/ErpModels.cs + ErpAdditionalDtos.cs — EXTEND, do not expose Oracle rows directly
- src/Program.cs — endpoint composition only (keep thin)

## Tests (tests/MiniERP.Api.Tests/)
- ErpContractTests.cs: 37 unit tests (mapper + presenter, no DB)
- ApiIntegrationTests.cs: 10 integration tests (require Oracle)
- AutomationIntegrationTests.cs: 9 integration tests (require Oracle)
- TestStockFixture.cs: shared-schema stock reset helper
- AssemblyInfo.cs: DisableTestParallelization = true

## Selected files to extend (Phase 1+)
- sql/07_traceability_schema.sql (NEW) + sql/08_traceability_plsql.sql (NEW, package ERP_TRACEABILITY)
- scripts/run-sql.sh (add 07/08 to load order)
- src/Models/ErpTraceabilityDtos.cs (NEW)
- src/Services/ErpDbService.Traceability.cs (NEW partial) — same class ErpDbService
- src/Services/TraceabilitySupportServices.cs (NEW: barcode/label/idempotency helpers, pure logic)
- tests/.../TraceabilityContractTests.cs (NEW unit tests, no DB)
