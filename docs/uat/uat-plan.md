# User Acceptance Testing (UAT) Plan
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** UAT-PLN-001  
**Test Window:** 2026-09-20 to 2026-09-24  
**Target Environment:** UAT Staging Environment (Docker Oracle Database Free + ASP.NET Core 8 Web API + Web Dashboard)  

---

## 1. Objectives of UAT

The User Acceptance Testing (UAT) phase validates that MiniERP operates in strict alignment with manufacturing shop floor requirements, plant safety controls, and warehouse logistics operations at Tân Vĩnh Footwear & Apparel.

Specific acceptance objectives:
1. Verify that warehouse operators can receive raw materials, capture lot numbers and expiration dates, and print barcode labels without data corruption.
2. Verify that production planners can check BOM material availability and release orders with confidence.
3. Verify that floor operators can execute production orders, consume raw materials, and output finished goods with exact inventory reconciliation.
4. Verify that two-person approval gates prevent unauthorized stock modifications.
5. Verify that quality inspectors can perform rapid backward and forward genealogy tracing.

---

## 2. Test Environment & Data Setup

- **Database:** Oracle Database 23c Free container running on port 1521, initialized via `scripts/run-sql.sh`.
- **API Server:** ASP.NET Core 8 Web API running on `http://localhost:5000`.
- **Dashboard UI:** Web operations dashboard running on `http://localhost:8080`.
- **Master Seed Baseline:**
  - Warehouses: `WH_RAW`, `WH_WIP`, `WH_FG`.
  - Items: 5 Raw Materials (`MAT_RUBBER_01`, `MAT_MESH_01`, `MAT_THREAD_01`, `MAT_GLUE_01`, `MAT_BOX_01`), 2 Finished Goods (`FG_RUNNER_PRO_42`, `FG_SNEAKER_LITE_40`).
  - Active BOM: `FG_RUNNER_PRO_42` Version V1.0.

---

## 3. Test Execution Schedule & Participants

| Session | Focus Area | Date | Primary Testers | Approver |
|---|---|---|---|---|
| **Session 1** | Inbound Receiving, Lot Capture & Label Generation | Day 1 | Lead Warehouseman (`warehouse01`) | Warehouse Manager |
| **Session 2** | Production Planning, BOM Explosion & Shortage Alert | Day 2 | Production Planner (`planner01`) | Production Superintendent |
| **Session 3** | FEFO Consumption, Assembly Completion & FG Output | Day 3 | Floor Operators (`production01`) | Production Superintendent |
| **Session 4** | Approval Gate & Multi-Tier Authorization Security | Day 4 | Internal Auditor (`auditor01`), Manager | Plant Director |
| **Session 5** | Backward Quality Recall & Disaster Recovery Drill | Day 5 | QA Compliance Lead, IT Specialist | IT Director |

---

## 4. Entry & Exit Criteria

### Entry Criteria:
- All 164 unit and integration tests passing in CI/CD pipeline.
- Database packages `ERP_OPERATIONS`, `ERP_AUTOMATION`, and `ERP_TRACEABILITY` compiled with `VALID` status.
- Test user accounts seeded with correct PBKDF2 password credentials.

### Exit Criteria:
- 100% of critical (P0) UAT test scenarios executed with `PASSED` status.
- Zero open Critical or High severity defects.
- Formal UAT Sign-off executed by Warehouse Manager, Production Superintendent, and IT Director.
