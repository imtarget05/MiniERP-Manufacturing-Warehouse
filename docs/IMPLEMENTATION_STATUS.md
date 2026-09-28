# MiniERP Implementation & Upgrade Status Tracker

This document tracks the progress of the MiniERP Manufacturing & Warehouse ERP implementation case study across all 18 phases according to the upgrade specification.

> **Status Legend:**  
> ⬜ Not Started &nbsp;|&nbsp; 🟡 In Progress &nbsp;|&nbsp; 🟢 Completed &nbsp;|&nbsp; 🔴 Blocked  
> *Note: A phase may only become 🟢 when code works, tests pass, documentation exists, and verifiable evidence is linked.*

---

## Progress Overview Matrix

| Phase | Description | Priority | Status | Code | Tests | Docs | Evidence / Artifacts |
|:---:|---|:---:|:---:|:---:|:---:|:---:|---|
| **0** | **Repository Audit & Gap Matrix** | P0 | 🟢 | N/A | N/A | 🟢 | [`docs/00-current-system-audit.md`](00-current-system-audit.md) |
| **1** | **Business Process Design & Core ERP** | P0 | 🟢 | 🟢 | 🟢 | 🟢 | [`ApiIntegrationTests.cs`](../tests/MiniERP.Api.Tests/ApiIntegrationTests.cs) (Phase 1 Acceptance Test PASSED) |
| **2** | **Workflow & Enterprise Approvals** | P0 | 🟢 | 🟢 | 🟢 | 🟢 | `ERP_AUTOMATION` package & CR-001 Approval Gate; RBAC 401/403 verified |
| **3** | **Auditability & Enterprise Security** | P0 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/security/security-design.md`](security/security-design.md), PBKDF2 210k, Sonar secret cleanup |
| **4** | **Business Analysis Documentation** | P1 | 🟢 | N/A | N/A | 🟢 | [`docs/business-analysis/`](business-analysis) (01 Context through 07 RTM) |
| **5** | **ERP Implementation Simulation** | P2 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/project-management/`](project-management) (Charter, Scope, Risks, CR-001) |
| **6** | **Automated Testing & Quality Gates** | P0 | 🟢 | 🟢 | 🟢 | 🟢 | 302 tests passed, 0 failures, [`docs/testing/test-strategy.md`](testing/test-strategy.md) |
| **7** | **User Acceptance Testing (UAT)** | P1 | 🟢 | N/A | 🟢 | 🟢 | [`docs/uat/`](uat) (Plan, Test Cases, Results, Sign-off Certificate) |
| **8** | **Database Backup & Disaster Recovery**| P1 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/operations/backup-restore.md`](operations/backup-restore.md), `verify-backup.sh` pass |
| **9** | **Application Observability & Health** | P1 | 🟢 | 🟢 | 🟢 | 🟢 | `/api/health`, `/health/ready`, `ERROR_LOG`, [`docs/operations/monitoring.md`](operations/monitoring.md) |
| **10**| **Docker & Reproducible Deployment** | P1 | 🟢 | 🟢 | 🟢 | 🟢 | [`src/Dockerfile`](../src/Dockerfile), [`dashboard/Dockerfile`](../dashboard/Dockerfile), [`docker-compose.yml`](../docker-compose.yml) |
| **11**| **CI/CD Pipeline Automation** | P1 | 🟢 | 🟢 | 🟢 | 🟢 | [`.github/workflows/ci.yml`](../.github/workflows/ci.yml), [`docs/devops/ci-cd.md`](devops/ci-cd.md) |
| **12**| **Release, Migration & Go-Live** | P2 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/go-live/`](go-live), [`scripts/migrate-legacy-data.sh`](../scripts/migrate-legacy-data.sh) ($Delta = 0$) |
| **13**| **Post-Go-Live Support & Incident RCA**| P2 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/support/`](support) (INC-001 through INC-005 Runbooks) |
| **14**| **Reporting & Management Dashboard** | P2 | 🟢 | 🟢 | N/A | 🟢 | [`dashboard/`](../dashboard), [`docs/reporting/kpi-dashboard-guide.md`](reporting/kpi-dashboard-guide.md) |
| **15**| **Architecture Documentation & ADRs** | P2 | 🟢 | 🟢 | N/A | 🟢 | [`docs/architecture/`](architecture) & ADR-001 through ADR-005 |
| **16**| **Portfolio README Transformation** | P3 | 🟢 | N/A | N/A | 🟢 | [`README.md`](../README.md) (20 sections, Mermaid diagrams) |
| **17**| **Recruiter 5-Minute Demo Scenario** | P3 | 🟢 | 🟢 | 🟢 | 🟢 | [`docs/demo/recruiter-demo.md`](demo/recruiter-demo.md) |
| **18**| **Portfolio Honesty & Ethical Defense**| P3 | 🟢 | N/A | N/A | 🟢 | Transparent portfolio framing without unsubstantiated claims |

---

## Detailed Phase Completion Log

### Phase 0 — Repository Audit
- **Status:** 🟢 Completed
- **Completion Date:** 2026-09-24
- **Deliverables:** [`docs/00-current-system-audit.md`](00-current-system-audit.md), [`docs/IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md)

### Phase 1 — Business Process Design & Core ERP
- **Status:** 🟢 Completed
- **Deliverables:** Verified core flow (Receive 100 RM $\rightarrow$ create PO $\rightarrow$ consume 30 $\rightarrow$ stock balance = 70 $\rightarrow$ produce 10 FG $\rightarrow$ FG stock = 10) in `tests/MiniERP.Api.Tests/ApiIntegrationTests.cs`.

### Phase 2 — Business Workflow & Enterprise Approvals
- **Status:** 🟢 Completed
- **Deliverables:** Two-person approval gating in `ERP_AUTOMATION` package, role verification, CR-001 change control.

### Phase 3 — Auditability & Enterprise Security
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/security/security-design.md`](security/security-design.md), SonarCloud hardcoded token cleanup in `.github/workflows/sonarcloud.yml`.

### Phase 4 — Business Analysis Documentation
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/business-analysis/01-business-context.md`](business-analysis/01-business-context.md) through [`07-requirement-traceability-matrix.md`](business-analysis/07-requirement-traceability-matrix.md).

### Phase 5 — ERP Implementation Simulation
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/project-management/`](project-management) (Charter, Scope, Stakeholders, Milestones, Risks, Issues, Change Request CR-001).

### Phase 6 — Automated Testing & Quality Gates
- **Status:** 🟢 Completed
- **Deliverables:** 302 unit, contract, RBAC-security and integration tests passing cleanly (247 run without a database, 55 `Category=Integration` need Oracle); [`docs/testing/test-strategy.md`](testing/test-strategy.md).

### Phase 7 — User Acceptance Testing (UAT)
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/uat/`](uat) (UAT Plan, Test Cases UAT-INV-001+, Results, Sign-off Certificate).

### Phase 8 — Database Backup & Disaster Recovery
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/operations/backup-restore.md`](operations/backup-restore.md); staging drill verified.

### Phase 9 — Observability & Monitoring
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/operations/monitoring.md`](operations/monitoring.md); `/api/health`, `/health/ready`, `ERROR_LOG` active.

### Phase 10 — Docker & Deployment
- **Status:** 🟢 Completed
- **Deliverables:** [`src/Dockerfile`](../src/Dockerfile), [`dashboard/Dockerfile`](../dashboard/Dockerfile), multi-service [`docker-compose.yml`](../docker-compose.yml).

### Phase 11 — CI/CD Pipeline
- **Status:** 🟢 Completed
- **Deliverables:** [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) (Build, Test, Acceptance, Docker buildx); [`docs/devops/ci-cd.md`](devops/ci-cd.md).

### Phase 12 — Release, Migration & Go-Live
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/go-live/`](go-live); [`scripts/migrate-legacy-data.sh`](../scripts/migrate-legacy-data.sh) verified ($Delta = 0$).

### Phase 13 — Post-Go-Live Support
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/support/`](support) (Runbooks INC-001 through INC-005, RCA guide).

### Phase 14 — Reporting & Management Dashboard
- **Status:** 🟢 Completed
- **Deliverables:** Operational Dashboard UI & [`docs/reporting/kpi-dashboard-guide.md`](reporting/kpi-dashboard-guide.md).

### Phase 15 — Architecture Documentation & ADRs
- **Status:** 🟢 Completed
- **Deliverables:** [`docs/architecture/`](architecture) (System Context, Container, Backend, DB, Auth, Deployment) & ADR-001 through ADR-005.

### Phase 16 — Portfolio README Transformation
- **Status:** 🟢 Completed
- **Deliverables:** Complete 20-section [`README.md`](../README.md) with Mermaid diagrams.

### Phase 17 — Recruiter Demo Scenario
- **Status:** 🟢 Completed
- **Deliverables:** Scripted 5–10 minute live walkthrough [`docs/demo/recruiter-demo.md`](demo/recruiter-demo.md).

### Phase 18 — Portfolio Honesty
- **Status:** 🟢 Completed
- **Deliverables:** Explicit portfolio simulation wording throughout documentation suite.

---

## Release Verification Record — v1.1 (2026-09-25) — SUPERSEDED by v1.2

> **Superseded, kept as an audit trail.** These numbers were genuinely measured on
> 2026-09-25, but the 2026-09-26 wave supersedes them (302 / 247 / 55). Read the
> current figures in the v1.2 record below; do not quote this table as current.

Every number below was *measured* at the time, not carried over from an earlier phase.

| Check | Command | Result |
|---|---|---|
| Full suite (Oracle integration included) | `dotnet test tests/MiniERP.Api.Tests` | `Passed: 166, Failed: 0, Skipped: 0, Total: 166, Duration: 40 s` |
| DB-free contract suite | `dotnet test tests/MiniERP.Api.Tests --filter "Category!=Integration"` | `Passed: 139, Failed: 0, Total: 139` (⇒ 27 cases need Oracle) |
| Schema load from clean state | `RESET=1 bash scripts/run-sql.sh` | `schema reset complete (DROPPED=38, TABLES_LEFT=0)` → `31 tables`, 3 packages (spec + body) `VALID`, 0 invalid objects |
| Full stack | `docker compose up -d` | `minierp-oracle` healthy + `minierp-api:5000` + `minierp-ui:8080` |
| Demo rehearsal | `docs/demo/recruiter-demo.md` Steps 1–6, scripted curl run | **19/19 assertions PASS** (receive → BOM check → reserve → atomic complete + `409` retry → two-person approval → FEFO issue → `complete-traceable` → backward trace) |
| Endpoint inventory | `grep -c 'MapGet\|MapPost\|MapPut\|MapDelete' src/Program.cs` | **62** routes (README badge/architecture diagram state 62) |
| CI | GitHub Actions | recorded in the commit that closes the plan below |

**Defects found and fixed during this verification (all in scope of "make what is documented actually true"):**

1. `GET /api/automation/approvals` (and the alert/run/report reads) mapped Oracle columns into **positional records** with implicit column order and no `AS` aliases. As soon as a row had a real `DECIDED_AT`, Dapper's positional-constructor mapping of that nullable `DATE` failed and the endpoint answered 500 — the two-person demo could never be shown. Fixed by aliasing every selected column and switching the affected rows to mutable classes that convert instead of type-comparing; pinned by two new integration tests (decide → read-back, status filter → read-back). All four reads now also return `200` live (`approvals`, `runs`, `reports`, `replenishment/alerts`).
2. `docs/demo/recruiter-demo.md` drifted from the API: approval payload used `approvalType`/`STOCK_ADJUST` (real contract: `action`/`INVENTORY_ADJUST`), the decision URL referenced a non-existent `APP-001`, the finished-good check queried `WH_FG` while `complete_production_order` books the FG to `PRODUCTION_ORDER.WAREHOUSE_ID`, step 3 called `/release` while describing a soft-reserve (real endpoint: `/reserve`), and every command required `jq`, which is not installed by default. All corrected; the genealogy finale now drives the real `allocate-lots → issue-lots → complete-traceable → /api/trace` chain.
3. `RESET=1 bash scripts/run-sql.sh` was documented in the script header but never implemented — running it silently re-ran non-idempotent DDL (and, with the API up, failed with `ORA-00054`/`ORA-00955` and left a half-schema). Now implemented as `sql/00_reset_schema.sql` with a guard that refuses to drop while the API answers on `$BASE_URL`.
4. Stale test counts (`164` / `164+` / `25 integration`) corrected to the measured `166` / `166+` / `27` across README and docs. (Those figures were themselves superseded on 2026-09-26 — see the v1.2 record below.)

---

## Release Verification Record — v1.2 (2026-09-26, W9 fresh re-verification)

Every number below comes from a **run executed by the 2026-09-26 QA wave** and is
reproducible from the command in the second column. No archived `artifacts/**` record
was used as evidence. **Two rows are qualified:** the Docker build row is a
**2026-09-25 round-tag** result carried forward (it is the only row here not re-run on
2026-09-26), and the cross-project/browser rows are W8 runs.

| Check | Command | Fresh result |
|---|---|---|
| Full suite (Oracle integration included) | `dotnet test tests/MiniERP.Api.Tests -c Release -p:TreatWarningsAsErrors=true -p:NuGetAudit=false` against a **disposable** Oracle 23c instance | `Failed: 0, Passed: 302, Skipped: 0, Total: 302, Duration: 29 s` |
| Split of the 302 | trait census over `tests/MiniERP.Api.Tests/*.cs` | **247** `Category!=Integration` (no database needed) + **55** `Category=Integration` (`ApiIntegrationTests` 11 + `AutomationIntegrationTests` 35 + `SecurityContractTests` 2 + `Phase2RbacTests` 7) = 302 |
| Integration share really needs Oracle | the 55 integration tests carry `[Trait("Category","Integration")]` at class level (`ApiIntegrationTests`, `AutomationIntegrationTests`) or method level (`SecurityContractTests` x2, `Phase2RbacTests` x7) | 302 − 55 = 247, consistent with the DB-free filter |
| Endpoint inventory | `grep -cE 'Map(Get\|Post\|Put\|Delete)\(' src/Program.cs` | **62** routes (26 `MapGet` + 36 `MapPost`) — unchanged |
| Cross-project E2E MiniERP → Helpdesk | W8 disposable-Oracle chain | **13/13**, 0 skipped (host and container runs agree) |
| Helpdesk browser E2E (Playwright) | W8 UI journey run | **19/19** |
| Helpdesk baseline gate | W8 `test/` suite run | **18/18**, exit 0 |
| Helpdesk offline regression | `env -u … npm test` | **269/269**, 0 fail, 0 skipped |
| Helpdesk Flask isolation suite | `python3 -m unittest` (isolated `DATA_FILE`) | **79/79** OK |
| Helpdesk PowerShell static gate | `[Parser]::ParseFile` in container | **8** `.ps1` files, **0** parse errors |
| Helpdesk REST smoke | `npm run test:api` | **68/68** |
| Docker builds ⚠️ *(2026-09-25, round tag — **không phải** số fresh của wave này; xem ghi chú bên dưới)* | `docker build` (api, dashboard, portal test target) | **3/3** exit 0 |

**What changed against the v1.1 record (2026-09-25):** the automated suite grew
from 166 to 302 cases as the 2026-09-26 QA waves added dashboard-asset
contract, traceability-documentation, presenter/error-mapper and
automation/RBAC coverage. The Oracle integration share moved from 27 to 55 and
the DB-free share from 139 to 247. The schema (`31 tables`, 3 `VALID` PL/SQL
packages), the 62-route surface and the demo rehearsal did not change.

**Still open / not re-verified by this wave:** the remote CI and SonarCloud
rows of the v1.1 table above were recorded at commit `ca2a02d` and have **not**
been re-executed here; they remain claims about a historical pipeline state. The
Docker `3/3` build result comes from the 2026-09-25 build wave using round tags;
a final rebuild is scheduled to re-confirm image reproducibility.


## Final Completion Status (2026-09-24) — HISTORICAL SNAPSHOT

> **Historical record, not the current state.** Every number in this section was
> measured on 2026-09-24 and is kept verbatim as an audit trail. The counts below
> — `164 passed`, `139 unit + 25 integration`, `53/53`, `61/61`, `20/20`,
> `62 endpoints, 164 tests` — are **superseded**. The current verified figures are
> in *Release Verification Record — v1.2 (2026-09-26)* above: **302 tests**
> (247 without a database + 55 integration). Do not quote this section as
> current evidence; the same superseded counts also appear, by design, under
> `artifacts/factory-upgrade/**`, which is an immutable build record.

The project had been verified end-to-end from local-only to fully complete:

| Stage | Status | Evidence |
|-------|--------|----------|
| **Remote CI** | 🟢 | CI/CD Pipeline #17 (commit `ca2a02d`) — 3 jobs green: Build & Unit (50s), Full Acceptance (3m15s), Container Build (45s) |
| **SonarCloud** | 🟢 | SonarCloud Analysis #9 (commit `ca2a02d`) — gate step green, no token required |
| **SQL Load** | 🟢 | `run-sql.sh` → 31 tables, 0 invalid objects, 3 PL/SQL packages VALID |
| **Full Test Suite** | 🟢 | `dotnet test` → **164 passed, 0 failed, 0 skipped** (139 unit + 25 integration) |
| **Docker Compose** | 🟢 | `docker compose up` → 3/3 containers healthy (oracle-db, api, ui) |
| **API Smoke** | 🟢 | `test-api.sh` → 53/53 checks passed |
| **Traceability E2E** | 🟢 | `test-traceability.sh` → 61/61 checks passed |
| **Demo Rehearsal** | 🟢 | `recruiter-demo.md` → 20/20 API calls HTTP 200, dashboard UI live |
| **Release** | 🟢 | Tag `v1.1` created, README badges corrected (62 endpoints, 164 tests) |

**Verified commit:** `ca2a02d` (HEAD of `origin/main`)
**Working tree:** Clean after commit of CI fix + demo/README corrections.
