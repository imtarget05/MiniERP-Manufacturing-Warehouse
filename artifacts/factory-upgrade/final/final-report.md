# Factory ERP & Warehouse Traceability Platform — Final Upgrade Report

## Upgrade Result
PASS

## Baseline
- Existing tests before change: 37 passed, 0 failed (ErpContractTests baseline)
- Existing tests after change: 37 passed, 0 failed (all original contract tests pass without regression)
- New tests and feature coverage: 127 additional unit/contract/integration cases beyond the 37-test baseline.
- Full final suite: **164 passed, 0 failed, 0 skipped** (`dotnet test tests/MiniERP.Api.Tests/MiniERP.Api.Tests.csproj`).

## Implemented Features
- **Lot / Batch Inventory Management**: Full lifecycle management of inventory lots (`INVENTORY_LOT`, `LOT_STOCK`), shelf-life, expiration dates, and lot status gating (`ACTIVE`, `HOLD`, `CONSUMED`, `EXPIRED`, `BLOCKED`).
- **Warehouse Location & Bin Hierarchy**: Multi-tier location structure per warehouse (`BIN`, `STAGING`, `RECEIVING`, `SHIPPING`, `LINE`) via `WAREHOUSE_LOCATION`.
- **Scan & Keyboard-Wedge Barcode Resolution**: Deterministic scan engine parsing `LOT:`, `ITEM:`, `LOC:`, and `PO:` barcodes through `TraceabilityLogic.ResolveScan` and `/api/barcodes/resolve/{code}`.
- **Idempotent Receiving & Movement**: Single-transaction lot receipts and internal/cross-warehouse relocations with duplicate-scan replay protection via `REQUEST_IDEMPOTENCY` and `ERP_TRACEABILITY`.
- **Traceable Manufacturing & FEFO/FIFO Allocation**: Automated recommendation of lots sorted by FEFO (earliest expiry) then FIFO; atomic lot issue and consumable accounting.
- **Traceable Completion & Output Genealogy**: Atomic completion creating finished-goods lot, recording exact genealogy links (`PRODUCTION_LOT_CONSUMPTION` and `PRODUCTION_LOT_OUTPUT`), and issuing FG barcodes.
- **Bi-Directional Genealogy Tree**: Backward trace (FG lot $\rightarrow$ PO $\rightarrow$ Consumed raw material lots $\rightarrow$ Supplier PO) and Forward trace (Raw material lot $\rightarrow$ Affected POs $\rightarrow$ Finished goods lots) via `/api/trace/{lotCode}`.
- **Printable Labels & Reprint Auditing**: Deterministic HTML card and raw ZPL II code preview for thermal printers; reprint operations write auditable records to `LABEL_PRINT_JOB` with zero inventory side effects.
- **Credential Hardening & RBAC**: PBKDF2 password hashing (210,000 iterations); compact signed HS256 JWT bearer token issuance; role authorization middleware enforcing least-privilege matrix (`ERP_ADMIN`, `WAREHOUSE_OPERATOR`, `PRODUCTION_OPERATOR`, `ERP_SUPPORT`, `VIEWER`).
- **Append-Only Security & Mutation Audit Trail**: Correlation ID tracing (`X-Correlation-ID`) across HTTP requests, database audit logs (`APP_AUDIT_EVENT`), and integration payloads.
- **System Operability & Health**: Liveness (`/api/health`), readiness probe checking database connectivity, table count >= 31, and all 3 packages valid (`/api/health/ready`), and diagnostic inspection (`/api/health/details`).
- **Disaster Recovery & Data Safety**: Automated backup (`scripts/backup-db.sh`), safety-locked restore (`scripts/restore-db.sh`), and comprehensive schema verification (`scripts/verify-backup.sh`).
- **Enterprise IT Helpdesk Integration**: Asynchronous fail-soft incident outbox (`HELPDESK_DELIVERY`) with credential sanitization, SHA-256 payload conflict detection, idempotent retry, and graceful degradation during Helpdesk outages.

## Database Changes
- `sql/07_traceability_schema.sql` (NEW):
  - Added columns `TRACE_MODE`, `SHELF_LIFE_DAYS`, `LABEL_TEMPLATE` to `ITEM`.
  - Added columns `PASSWORD_HASH`, `PASSWORD_SALT`, `PASSWORD_ITERATIONS`, `LAST_LOGIN_AT` to `APP_USER`.
  - Created 10 traceability/security tables: `WAREHOUSE_LOCATION`, `INVENTORY_LOT`, `LOT_STOCK`, `TRACEABILITY_EVENT`, `PRODUCTION_LOT_CONSUMPTION`, `PRODUCTION_LOT_OUTPUT`, `BARCODE_IDENTIFIER`, `LABEL_PRINT_JOB`, `REQUEST_IDEMPOTENCY`, `APP_AUDIT_EVENT`.
- `sql/08_traceability_plsql.sql` (NEW):
  - Created package `ERP_TRACEABILITY` (specification and package body, 1000+ lines) providing transactional procedures: `ensure_location`, `receive_lot_stock`, `putaway_lot`, `move_lot_stock`, `set_lot_hold`, `allocate_lots_preview`, `issue_lot_to_production`, `complete_production_traceable`, `create_label_job`.
- `sql/09_traceability_seed.sql` (NEW):
  - Seeded demo locations (`RCV-01`, `A-01-01`, `A-01-02`, `FG-01-01`, `LINE-01`), marked raw materials as `TRACE_MODE = 'LOT'`, created demo PO `PO_PUR_LOT_01`, and registered location barcodes.
- `sql/10_helpdesk_schema.sql` (NEW):
  - Created outbox table `HELPDESK_DELIVERY` with index `IX_HD_STATUS`.
- `sql/03_seed.sql` (MODIFIED):
  - Upgraded users (`admin`, `tan.mai`, `planner01`, `warehouse01`, `viewer01`) with PBKDF2 password hashes; marked legacy `PASSWORD` column as `MIGRATED`.

## API Changes
- `POST /api/auth/login`: Authenticates against PBKDF2 hash, issues HS256 Bearer JWT.
- `GET /api/auth/me`: Returns current authenticated principal identity and roles.
- `GET /api/health/ready`: Checks DB connectivity, >= 31 tables, and all 3 package statuses.
- `GET /api/health/details`: Diagnostics with package versions (restricted to `ERP_ADMIN`/`ERP_SUPPORT`).
- `GET /api/warehouse/{wh}/locations` & `POST .../locations`: Location management.
- `GET /api/warehouse/lots/{lot}` & `GET .../lots/{lot}/stock`: Lot master and location stock lookup.
- `POST /api/warehouse/receipts/{po}/receive`: Lot-based PO receipt with idempotency key.
- `POST /api/warehouse/putaway` & `POST /api/warehouse/move`: Internal and cross-warehouse movements.
- `POST /api/warehouse/lots/{lot}/hold` & `POST .../release-hold`: Quality hold management.
- `GET /api/manufacturing/production-order/{po}/allocate-lots`: FEFO lot recommendation.
- `POST /api/manufacturing/production-order/{po}/issue-lots`: Traceable lot issue.
- `POST /api/manufacturing/production-order/{po}/complete-traceable`: Traceable completion and FG lot generation.
- `GET /api/trace/{lotCode}`: Bi-directional tree traversal (`backward`, `forward`, `both`).
- `POST /api/labels`, `GET /api/labels`, `GET /api/labels/{id}`, `GET .../render`: Label job creation, listing, and HTML/ZPL rendering.
- `GET /api/barcodes/resolve/{code}`: Scanner resolution endpoint.
- `POST /api/integration/helpdesk/incidents`: Forwards ERP incidents to Helpdesk outbox.
- `GET /api/integration/helpdesk/deliveries/{ref}`: Outbox delivery inspection.

## UI Changes
- `dashboard/index.html` & `dashboard/app.js`:
  - Added **Nhận hàng theo Lot** card (PO receipt, lot registration, idempotency key).
  - Added **Quét & Chuyển vị trí** card with keyboard-wedge barcode auto-lookup on Enter and focus return.
  - Added **In nhãn lot (label workflow)** card with HTML live preview and ZPL II code preview.
  - Added **Phân bổ FEFO** card under Production tab showing material shortages and recommended lot allocations.
  - Added **Cây phả hệ (Genealogy Tree)** card with interactive backward/forward tree rendering.

## Security Changes
- Password storage transitioned from plaintext to PBKDF2-SHA256 with 210,000 iterations and 16-byte cryptographically secure random salt.
- Zero-dependency compact HS256 Bearer token handler (`MiniErpBearerHandler`) with signature verification and expiration validation.
- Centralized `MutationAuthorizationMiddleware` enforcing role policies on all mutation endpoints:
  - Unauthenticated requests return `401 Unauthorized` (`AUTH_REQUIRED`).
  - Unauthorized roles return `403 Forbidden` (`AUTH_FORBIDDEN`).
- Sensitive data sanitizer in `HelpdeskSecurity.Sanitize` scrubbing passwords, tokens, connection strings, and raw SQL queries before transmission.
- Centralized `CorrelationIdMiddleware` standardizing `X-Correlation-ID`.

## Backup/Restore Verification
- `scripts/backup-db.sh`: Non-interactive backup creating timestamped directory, Oracle Data Pump dump, schema snapshot, and `metadata.json` without leaking passwords.
- `scripts/restore-db.sh`: Safety-locked recovery requiring explicit `--confirm` / `CONFIRM_RESTORE=yes`.
- `scripts/verify-backup.sh`: Deep schema validation verifying table count >= 31, all 3 packages `VALID`, and core master data integrity.
- Runbook and RPO/RTO metrics published in `docs/07-backup-recovery-dr.md`.

## Helpdesk Integration Verification
- Implemented `HelpdeskIntegrationService` and `HELPDESK_DELIVERY` outbox.
- Automated tests in `HelpdeskIntegrationTests.cs` (11 tests) verifying:
  - Credential and token sanitization.
  - Idempotency via `X-Integration-Key` and `Idempotency-Key` headers.
  - Duplicate replay detection without redundant network calls.
  - Payload conflict detection (409).
  - Fail-soft offline handling (retries and returns `DELIVERY_UNAVAILABLE` without crashing or aborting ERP transactions).

## Modified / Added Files
- `sql/07_traceability_schema.sql` — Traceability, location, genealogy, label, audit, and user credential schema.
- `sql/08_traceability_plsql.sql` — Package `ERP_TRACEABILITY` implementation.
- `sql/09_traceability_seed.sql` — Seed data for locations, lot items, demo PO, and barcodes.
- `sql/10_helpdesk_schema.sql` — `HELPDESK_DELIVERY` outbox schema and index.
- `sql/03_seed.sql` — PBKDF2 user password hash seeds.
- `src/Program.cs` — Authentication, RBAC, correlation, traceability, label, and helpdesk endpoints.
- `src/Services/ErpDbService.cs` — Added health diagnostics and user authentication query methods.
- `src/Services/ErpDbService.Traceability.cs` — Traceability database access methods.
- `src/Services/ErpDbService.Helpdesk.cs` — Outbox persistence methods.
- `src/Services/PasswordHashService.cs` — PBKDF2 hashing and verification.
- `src/Services/TokenService.cs` — HS256 JWT bearer token issuance, validation, and authentication handler.
- `src/Services/AuthPolicies.cs` — System policy names and role constants.
- `src/Services/MutationAuthorization.cs` — Mutation route authorization policy mapper and middleware.
- `src/Services/CorrelationIdMiddleware.cs` — Correlation ID extractor and sanitization middleware.
- `src/Services/AuditMiddleware.cs` — Asynchronous mutation audit logger.
- `src/Services/LabelRenderService.cs` — HTML and ZPL II deterministic label rendering.
- `src/Services/TraceabilityLogic.cs` — Scan resolution, request hashing, and FEFO sorting logic.
- `src/Services/HelpdeskOptions.cs` — Configuration options and security sanitization.
- `src/Services/HelpdeskIntegrationService.cs` — Asynchronous fail-soft Helpdesk client.
- `src/Models/ErpAuthDtos.cs` — Authentication and health DTOs.
- `src/Models/ErpTraceabilityDtos.cs` — Location, lot, label, and trace DTOs.
- `src/Models/ErpHelpdeskDtos.cs` — Helpdesk request, delivery, and outbox DTOs.
- `dashboard/index.html` & `dashboard/app.js` — UI forms for receiving, moving, labels, FEFO, and genealogy.
- `scripts/run-sql.sh` — Added `10_helpdesk_schema.sql` and verified table count >= 31.
- `scripts/backup-db.sh` — Backup automation script.
- `scripts/restore-db.sh` — Restore automation script.
- `scripts/verify-backup.sh` — Post-restore verification script.
- `docs/07-backup-recovery-dr.md` — Backup & DR runbook.
- `README.md` — Updated badges, architecture, capabilities, and demo scenarios.
- `tests/MiniERP.Api.Tests/TraceabilityContractTests.cs` — Contract unit tests for Phase 1.
- `tests/MiniERP.Api.Tests/TraceabilityPhase2Tests.cs` — Warehouse operations unit tests.
- `tests/MiniERP.Api.Tests/TraceabilityPhase3Tests.cs` — Manufacturing genealogy unit tests.
- `tests/MiniERP.Api.Tests/TraceabilityPhase4Tests.cs` — Label workflow unit tests.
- `tests/MiniERP.Api.Tests/SecurityContractTests.cs` — Authentication, PBKDF2, and RBAC tests.
- `tests/MiniERP.Api.Tests/HelpdeskIntegrationTests.cs` — Helpdesk integration and fail-soft tests.
- `artifacts/factory-upgrade/` — Reports for all implementation phases (0 through 8).

## Evidence
- `artifacts/factory-upgrade/baseline/baseline.txt` — Baseline test execution and environment state.
- `artifacts/factory-upgrade/phase-1-schema/phase-1-report.md` — Phase 1 schema report.
- `artifacts/factory-upgrade/phase-2-warehouse/phase-2-report.md` — Phase 2 warehouse and movement report.
- `artifacts/factory-upgrade/phase-3-genealogy/phase-3-report.md` — Phase 3 genealogy and FEFO report.
- `artifacts/factory-upgrade/phase-4-labels/phase-4-report.md` — Phase 4 label workflow and reprint report.
- `artifacts/factory-upgrade/phase-5-security/phase-5-report.md` — Phase 5 PBKDF2, JWT, and RBAC report.
- `artifacts/factory-upgrade/phase-6-dr/phase-6-report.md` — Phase 6 monitoring and disaster recovery report.
- `artifacts/factory-upgrade/phase-7-integration/phase-7-report.md` — Phase 7 Helpdesk integration report.
- `artifacts/factory-upgrade/phase-8-final/phase-8-report.md` — Phase 8 final end-to-end acceptance report (9-stage pipeline, all PASS).
- `artifacts/factory-upgrade/final/final-report.md` — Complete master upgrade report.

## Known Limitations / Stretch Goals
- **Serial Number Tracking**: Traceability is implemented at the lot/batch level (`TRACE_MODE = 'LOT'`), which is optimal for bulk factory operations (footwear components, soles, fabrics). Item-level serial tracking is architected via `TRACE_MODE` enum but deferred to future phases.
- **Direct TCP/LPR Label Printing**: Labels are generated as standard ZPL II code and responsive HTML previews. Physical direct-socket transmission (port 9100) to Zebra printers can be attached directly to the ZPL output stream.
