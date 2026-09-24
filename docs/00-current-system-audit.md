# PHASE 0 — Current System Audit & Requirement Gap Matrix
**Project:** MiniERP Manufacturing & Warehouse  
**Target Platform:** ASP.NET Core 8 Web API, Oracle Database 23c Free (PL/SQL), Angular / Web Dashboard  
**Date:** September 2026  
**Auditor:** AI Systems Architect & Software Engineer (Portfolio Upgrade Taskforce)

---

## 1. Executive Summary

This repository audit inspects the current state of **MiniERP Manufacturing & Warehouse** before executing the multi-phase ERP Implementation Portfolio Upgrade.

The project currently represents an advanced, functional **Manufacturing Execution & Warehouse Management (MES/WMS)** prototype built specifically for factory environments (such as footwear and apparel assembly). It incorporates high-integrity database-level business logic in **Oracle PL/SQL**, an **ASP.NET Core 8 REST API** using Dapper and Minimal APIs, automated tests via **xUnit**, and an interactive web dashboard.

Rather than rebuilding from scratch, the system already possesses strong transactional foundations (idempotent barcode scanning, FEFO/FIFO lot allocation, bidirectional genealogy, PBKDF2 password hashing, and fail-soft helpdesk integration). The upgrade path consists of formalizing enterprise business processes, enhancing approvals, standardizing documentation (Business Analysis, Project Management, UAT, Disaster Recovery, Architecture ADRs), containerizing the API & UI, polishing CI/CD, and delivering a clean recruiter demonstration.

---

## 2. Technical Stack Audit

| Dimension | Current Implementation | Target / Upgrade State | Notes & Observations |
|---|---|---|---|
| **Backend** | ASP.NET Core 8 Minimal API (C#) | ASP.NET Core 8 Web API | 57 route registrations in `Program.cs`, Dapper micro-ORM, custom middleware. Healthy & structured. |
| **Database** | Oracle Database 23c Free (`gvenzl/oracle-free:slim`) | Oracle 19c/21c/23c Free | 31 tables, 3 valid PL/SQL packages (`ERP_OPERATIONS`, `ERP_AUTOMATION`, `ERP_TRACEABILITY`). High performance. |
| **Authentication** | Bearer Token (JWT-like HMAC-SHA256) + PBKDF2 (210,000 rounds) | JWT / Role-Based Access Control | Secure password derivation with individual salts. In-memory refresh token store. Role-based policies in place. |
| **Frontend** | Vanilla HTML5 / CSS3 / ES6 JavaScript (`dashboard/`) | Modern Web Dashboard (Vanilla JS + Angular portfolio alignment) | Responsive 5-tab UI with live KPI metrics, stock visualization, lot receiving, barcode scanner, and genealogy tree. |
| **Containerization**| `docker-compose.yml` for Oracle Free DB | Full Docker Compose (DB + Backend + Frontend) | Database containerized; backend and frontend Dockerfiles are currently missing. |
| **Testing** | xUnit (`tests/MiniERP.Api.Tests`) | xUnit Unit + Integration + Contract | 164 tests covering contracts, RBAC, traceability, and Oracle workflows. |
| **CI/CD** | GitHub Actions (`ci.yml`, `sonarcloud.yml`) | Multi-stage GitHub Actions | Unit and integration jobs configured; SonarCloud workflow contains a fallback token requiring remediation. |
| **Documentation** | Technical specs in `docs/` & `README.md` | Comprehensive 18-Phase ERP Lifecycle Suite | Existing docs cover schema, PL/SQL, API, and DR; missing BA, PM, UAT, and ADR packs. |

---

## 3. Architecture & Structural Analysis

### 3.1 Backend Architecture (`src/`)
- **API Style:** ASP.NET Core 8 Minimal APIs concentrated in `src/Program.cs` (1,278 lines) organized into functional sections (Authentication, Health, Warehouse Core, Manufacturing Core, Procurement, Automation, Traceability, Labels, Helpdesk Integration).
- **Service Layer (`src/Services/`):**
  - `ErpDbService.cs`: Core database repository executing Oracle stored procedures and SQL queries via Dapper.
  - `ErpDbService.Traceability.cs`: Extension handling locations, lots, putaway, moves, FEFO allocation, and genealogy traversal.
  - `ErpDbService.Helpdesk.cs`: Extension managing outbox deliveries to the external IT Helpdesk system.
  - `TokenService.cs` & `RefreshTokenStore.cs`: Token issuance, validation, and refresh lifecycle.
  - `PasswordHashService.cs`: RFC 2898 PBKDF2 implementation with SHA-256 and 210,000 iterations.
  - `LabelRenderService.cs`: Generation of HTML preview labels and Zebra ZPL II barcode streams.
  - `HelpdeskIntegrationService.cs`: Resilient HTTP client for asynchronous incident synchronization.
  - `ErpErrorMapper.cs`: Translates Oracle exceptions (`ORA-20001` through `ORA-20025`) into structured business error codes and RFC 7807 problem details.
- **Middleware Pipeline:**
  - `CorrelationIdMiddleware`: Propagates `X-Correlation-Id` across requests, logs, and database audit entries.
  - `AuditMiddleware`: Intercepts mutating requests and records audit events.
  - `MutationAuthorizationMiddleware`: Validates user role credentials for sensitive mutation endpoints.

### 3.2 Database Schema & PL/SQL (`sql/`)
The database contains **31 tables** divided across four architectural layers:
1. **Core ERP Base (13 tables):**
   - `WAREHOUSE`: Storage locations (`WH_RAW`, `WH_WIP`, `WH_FG`).
   - `ITEM`: Master catalogue with `ITEM_TYPE` (`RAW` vs `FG`), `UOM`, and `MIN_STOCK`.
   - `STOCK`: Real-time on-hand inventory per warehouse and item.
   - `BOM` & `BOM_DETAIL`: Multi-level bill of materials specification.
   - `PRODUCTION_ORDER`: Work orders with status lifecycle (`CREATED`, `RELEASED`, `MATERIAL_CHECK`, `WAITING_MATERIAL`, `READY`, `IN_PROGRESS`, `COMPLETED`, `CANCELLED`).
   - `PURCHASE_ORDER`: Raw material procurement from suppliers.
   - `INVENTORY_TRANSACTION`: Immutable record of every stock movement.
   - `APP_USER`, `ERP_ROLE`, `USER_ROLE`: User identities and role memberships.
   - `ERROR_LOG`: Autonomous transaction error capture surviving rollbacks.
   - `CHANGE_REQUEST`: Formal change control records.
2. **Business Automation Extension (7 tables):**
   - `STOCK_RESERVATION`: Soft-holds on material for released production orders.
   - `REPLENISH_ALERT`: Automatic notifications for stock below reorder points.
   - `ERP_AUTOMATION_RUN`: Execution journal for batch jobs and automated sweeps.
   - `PO_STATE_HISTORY`: Granular state audit trail for production orders.
   - `SUPPORT_INCIDENT`: Production and warehouse incident tracking.
   - `APPROVAL_REQUEST`: Formal approval gate for sensitive adjustments.
   - `AUTOMATION_REPORT`: Pre-aggregated scheduled reporting data.
3. **Traceability, Location & Security (10 tables):**
   - `WAREHOUSE_LOCATION`: Warehouse bins, racks, receiving docks, and staging areas.
   - `INVENTORY_LOT`: Lot tracking with supplier lot numbers, manufacture dates, and expiration dates.
   - `LOT_STOCK`: On-hand balance by lot and bin location.
   - `BARCODE_IDENTIFIER`: Scannable GS1/QR/Code128 identifiers mapped to entities.
   - `REQUEST_IDEMPOTENCY`: Prevents duplicate submissions from barcode scanner retries.
   - `LABEL_PRINT_JOB`: Audit log and reprint history for Zebra/ZPL barcode labels.
   - `TRACEABILITY_EVENT`: Chronological event trail of lot state changes.
   - `PRODUCTION_LOT_CONSUMPTION`: Exact lot quantities consumed by each production order.
   - `PRODUCTION_LOT_OUTPUT`: Finished good lots generated by production orders.
   - `APP_AUDIT_EVENT`: System-wide security and user activity audit trail.
4. **Integration Outbox (1 table):**
   - `HELPDESK_DELIVERY`: Idempotent outbox for outward event dispatching to IT Helpdesk.

### 3.3 Frontend Web Application (`dashboard/`)
- Pure client-side application implemented in HTML5, modern CSS with CSS variables, and Vanilla ES6 JavaScript.
- Modules:
  - **Overview (`tab-overview`):** Warehouse count, stock health against safety levels, open POs, system health dots, and real-time inventory table.
  - **Stock & Warehouses (`tab-stock`):** Stock levels, quick stock in/out forms, and warehouse breakdown.
  - **Manufacturing & BOM (`tab-mfg`):** BOM breakdown for `FG_RUNNER_PRO_42`, production order progress, and completion action buttons.
  - **Receiving & Lots (`tab-lots`):** Lot receiving with idempotency keys, FEFO allocation review, location transfers, label preview, and interactive genealogy tree traversal.
  - **Incident PO001 (`tab-incident`):** Material shortage simulation, autonomous error log review, procurement fix, and successful order closeout.
- *Gap identified against user plan:* The prompt references Angular as the target frontend framework. The existing implementation is clean, production-grade Vanilla JavaScript. We will maintain the existing dashboard while preparing the architectural roadmap and Angular-ready component specifications.

### 3.4 Automated Test Suite (`tests/MiniERP.Api.Tests/`)
- Contains **164 automated test cases** utilizing xUnit, FluentAssertions, and ASP.NET Core `WebApplicationFactory<Program>`.
- Test Suites:
  - `ApiIntegrationTests.cs`: End-to-end API workflows with Oracle connection (Health, Warehouses, Stock movements, BOM, PO complete).
  - `AutomationIntegrationTests.cs`: Production order lifecycle, material check, soft reservation, replenishment alert sweep, stale order detection.
  - `HelpdeskIntegrationTests.cs`: Outbox retry, payload sanitization, and fail-soft behavior.
  - `Phase2RbacTests.cs`: PBKDF2 verification, JWT token issuance, refresh token flow, role-based endpoint authorization.
  - `SecurityContractTests.cs`: Audit middleware validation, tamper-evident logging, and role enforcement.
  - `TraceabilityContractTests.cs`, `TraceabilityPhase2Tests.cs`, `TraceabilityPhase3Tests.cs`, `TraceabilityPhase4Tests.cs`: Unit validation of FEFO algorithms, barcode formatting, and genealogy trees.

---

## 4. Features Status Matrix

| Module / Feature | Working | Partial | Missing | Current Status & Details |
|---|:---:|:---:|:---:|---|
| **User Authentication (PBKDF2)** | ✅ | | | Fully implemented. 210,000 iteration PBKDF2 with SHA-256, secure salt generation, and legacy hash fallback. |
| **Token Lifecycle (Bearer / Refresh)** | ✅ | | | JWT-style Bearer token issuance (`TokenService`), in-memory refresh token store (`RefreshTokenStore`), revocation, and renewal. |
| **Role-Based Authorization (RBAC)** | ✅ | | | Backend policies (`WarehouseMutation`, `ProductionMutation`, etc.) enforced via middleware and route metadata. |
| **Master Data (Warehouse, Item, BOM)**| ✅ | | | Full relational schema for Warehouses, Raw Materials, Finished Goods, and multi-line BOMs with versioning. |
| **Supplier Master Data** | | 🟡 | | Handled implicitly via `PURCHASE_ORDER.SUPPLIER_NAME` text field; lacks dedicated `SUPPLIER` table. |
| **Stock In / Stock Out (Basic)** | ✅ | | | Atomic inventory movement calling `ERP_OPERATIONS.create_stock_in` and `create_stock_out`. |
| **Traceable Goods Receipt (Lots)** | ✅ | | | Lot-level receipt with manufacture date, expiry date, bin location, and idempotency key. |
| **FEFO / FIFO Material Allocation** | ✅ | | | Automated allocation in `ERP_TRACEABILITY.allocate_lots_fefo` sorting by earliest expiry date. |
| **Production Order Lifecycle** | ✅ | | | 8-stage lifecycle (`CREATED` $\rightarrow$ `RELEASED` $\rightarrow$ `MATERIAL_CHECK` $\rightarrow$ `READY` $\rightarrow$ `IN_PROGRESS` $\rightarrow$ `COMPLETED`). |
| **Automated Material Consumption** | ✅ | | | Atomic consumption of required raw materials and generation of finished goods inside single Oracle transaction. |
| **Bidirectional Genealogy** | ✅ | | | Backward trace (FG $\rightarrow$ PO $\rightarrow$ Raw Lots $\rightarrow$ PO) and Forward trace (Raw Lot $\rightarrow$ POs $\rightarrow$ FG Lots). |
| **Approval Gate (Enterprise Control)** | ✅ | | | `APPROVAL_REQUEST` table, multi-user approval logic in `ERP_AUTOMATION.adjust_stock_with_approval`. |
| **Barcode & Label Printing** | ✅ | | | Barcode resolution (`/api/barcodes/resolve`), Zebra ZPL II generation, and HTML printable labels. |
| **Autonomous Error Logging** | ✅ | | | Autonomous transaction logging into `ERROR_LOG` so failures persist even when outer business transaction rolls back. |
| **Audit Logging** | ✅ | | | `APP_AUDIT_EVENT` records actor, action, target entity, timestamp, correlation ID, and JSON payload. |
| **Health & Readiness Endpoints** | ✅ | | | `/api/health`, `/api/health/ready`, `/api/health/details` verifying Oracle connectivity and package validity. |
| **Backup & Restore Tooling** | ✅ | | | Standalone scripts `backup-db.sh`, `restore-db.sh`, and `verify-backup.sh` utilizing Oracle Data Pump (`expdp`/`impdp`). |
| **Containerization (Full Stack)** | | 🟡 | | Oracle DB containerized in `docker-compose.yml`. Missing Dockerfiles for ASP.NET Core API and Web UI. |
| **CI/CD Pipeline** | | 🟡 | | `.github/workflows/ci.yml` builds and runs tests. SonarCloud workflow contains a fallback token to remove. |
| **Business Analysis Documentation** | | | ❌ | `docs/business-analysis/` (01 to 07) is missing. |
| **Project Management Documentation** | | | ❌ | `docs/project-management/` (Charter, Scope, Risk, CR-001) is missing. |
| **Formal UAT Documentation** | | | ❌ | `docs/uat/` (Plan, Test Cases, Results, Sign-off) is missing. |
| **Disaster Recovery Formal Report** | | 🟡 | | Technical runbook exists in `docs/07-backup-recovery-dr.md`; requires formal drill evidence report. |
| **Go-Live & Migration Plan** | | | ❌ | `docs/go-live/` (Deployment, Cutover, CSV Migration Script) is missing. |
| **Support Incident Runbooks** | | 🟡 | | `docs/05-erp-support-runbook.md` exists; requires standardization to INC-001 through INC-005 format. |
| **Architecture Documentation & ADRs** | | 🟡 | | ERD and API specs exist; requires C4/Mermaid diagrams and ADRs 001-005. |
| **Portfolio README Structure** | | 🟡 | | Existing README is comprehensive but needs alignment with the 20-section portfolio structure. |
| **Recruiter 5-Minute Demo Script** | | 🟡 | | Demo snippets exist; needs dedicated end-to-end `docs/demo/recruiter-demo.md`. |

---

## 5. Technical Debt & Security Observations

1. **Security / Secret Hygiene:**
   - In `.github/workflows/sonarcloud.yml` (line 47 & 70), there is a fallback token string: `d82888019c866c9fd0925275ef73b64b217187a3`. This must be removed so that SonarCloud is either driven purely by secret injection or gracefully skipped if unconfigured.
   - Database credentials in `docker-compose.yml` and `appsettings.json` use standard local defaults (`ErpPassword2026#`). Production configurations should rely on environment overrides (`ORACLE_PWD`, `APP_USER_PWD`).
2. **Containerization Completeness:**
   - The repository currently runs the backend API via host `dotnet` and UI via static server or Python HTTP server. Adding a multi-stage `Dockerfile` for the ASP.NET Core API and an Nginx container for the Dashboard will enable a single `docker compose up` command.
3. **Database Concurrency & Test Hygiene:**
   - In integration testing, `TestStockFixture` resets stock rows via API calls. If an orphaned test process holds locks on Oracle rows, concurrent DDL/DML migrations can block. Test runners must clean up connections promptly.
4. **Documentation Packaging:**
   - While technical runbooks exist for developers, the repository lacks the complete business analysis, project management, and enterprise governance artifacts necessary to demonstrate an end-to-end ERP implementation lifecycle.

---

## 6. Detailed Gap Matrix & Action Plan

| Upgrade Phase | Requirement Description | Current State | Gap / Needed Action | Priority |
|---|---|---|---|:---:|
| **Phase 0** | Repository Audit & Status Tracker | Partially done in thoughts | Commit `docs/00-current-system-audit.md` and `docs/IMPLEMENTATION_STATUS.md`. | **P0** |
| **Phase 1** | Business Process Design & Core ERP | Fully working in DB & API | Verify 100 RM $\rightarrow$ 30 consume $\rightarrow$ 70 balance $\rightarrow$ 10 FG acceptance criteria test. | **P0** |
| **Phase 2** | Workflow & Enterprise Approvals | Working in `ERP_AUTOMATION` | Seed dedicated `MANAGER` role and formalize `DRAFT -> SUBMITTED -> APPROVED -> EXECUTED`. | **P0** |
| **Phase 3** | Auditability & Enterprise Security | Working in API & DB | Remove SonarCloud hardcoded token fallback, create `docs/security/security-design.md`. | **P0** |
| **Phase 4** | Business Analysis Documentation | Missing | Author `docs/business-analysis/` (Context, As-Is, To-Be, FRs, NFRs, Use Cases, RTM). | **P1** |
| **Phase 5** | ERP Implementation Simulation | Missing | Author `docs/project-management/` (Charter, Scope, Risks, Change Request CR-001). | **P2** |
| **Phase 6** | Automated Testing | 164 tests exist, seed sync needed | Reseed database and ensure all 164 unit and integration tests pass cleanly (0 failures). | **P0** |
| **Phase 7** | UAT Simulation & Acceptance | Missing | Author `docs/uat/` (Plan, Test Cases UAT-INV-001+, Results, Sign-off). | **P1** |
| **Phase 8** | Database Backup & Disaster Recovery | Scripts exist, report needed | Document drill results and operational procedures in `docs/operations/backup-restore.md`. | **P1** |
| **Phase 9** | Observability & Monitoring | `/health` & logs exist | Create `docs/operations/monitoring.md` documenting metrics, health checks, error triage. | **P1** |
| **Phase 10** | Docker & Deployment | Oracle DB only | Add `Dockerfile` for API, `Dockerfile` for UI, and unified `docker-compose.yml`. | **P1** |
| **Phase 11** | CI/CD Pipeline Upgrade | Existing CI runs tests | Add Docker build verification, clean up Sonar, create `docs/devops/ci-cd.md`. | **P1** |
| **Phase 12** | Release, Migration & Go-Live | Missing | Create `docs/go-live/` with legacy CSV import script, cutover checklist, rollback plan. | **P2** |
| **Phase 13** | Post-Go-Live Support & Incident Runbooks| Runbook exists | Author `docs/support/` covering INC-001 through INC-005 in structured RCA format. | **P2** |
| **Phase 14** | Reporting & Management Dashboard | Working in Vanilla JS UI | Enhance reporting views and document KPIs in dashboard. | **P2** |
| **Phase 15** | Architecture Documentation & ADRs | Schema & API docs exist | Author `docs/architecture/` with Mermaid C4 diagrams and ADR-001 through ADR-005. | **P2** |
| **Phase 16** | Portfolio README Rewrite | Existing README comprehensive | Align with 20-section portfolio structure, honesty disclaimers, and architecture diagrams. | **P3** |
| **Phase 17** | Recruiter Demo Scenario | Instructions exist in README | Author step-by-step 5-10 minute recruiter guide `docs/demo/recruiter-demo.md`. | **P3** |
| **Phase 18** | Portfolio Honesty & Integrity | Honest tone maintained | Verify all documentation reflects simulated/portfolio context without fictitious claims. | **P3** |

---

## 7. Next Steps & Minimum Changes for Phase 1

1. Initialize `docs/IMPLEMENTATION_STATUS.md` with all phases tracked.
2. Confirm Phase 1 core ERP flow verification: execute and record the business acceptance scenario (Receipt 100 RM $\rightarrow$ create PO $\rightarrow$ consume 30 $\rightarrow$ stock = 70 $\rightarrow$ produce 10 FG $\rightarrow$ FG stock = 10).
3. Proceed strictly by priority tiers: **Priority P0** (Phases 0, 1, 2, 3, 6) followed by **Priority P1** (Phases 4, 7, 8, 9, 10, 11).
