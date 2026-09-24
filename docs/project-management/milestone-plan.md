# Project Milestone Schedule & Delivery Roadmap
**Project:** MiniERP Manufacturing & Warehouse  
**Document Code:** PM-MLS-004  

---

## 1. Project Implementation Milestones

| Milestone ID | Milestone Description | Target Window | Deliverables / Acceptance Gates | Status |
|:---:|---|:---:|---|:---:|
| **M0** | **Project Initiation & Repository Audit** | Week 1 | Completed audit (`00-current-system-audit.md`), progress tracker (`IMPLEMENTATION_STATUS.md`). | 🟢 Completed |
| **M1** | **Core ERP Engine & Database Setup** | Weeks 2–3 | 31 Oracle tables, 3 valid PL/SQL packages, basic stock in/out, BOM calculation. | 🟢 Completed |
| **M2** | **Traceability & FEFO Engine** | Weeks 4–5 | Lot tracking, FEFO allocation, Zebra ZPL label service, bidirectional genealogy API. | 🟢 Completed |
| **M3** | **Enterprise Governance & Security** | Weeks 6–7 | PBKDF2 210k password hashing, JWT Bearer tokens, two-person approval gate, audit logs. | 🟢 Completed |
| **M4** | **Automated Quality & Test Hardening** | Weeks 8–9 | 164+ automated xUnit tests passing, 0 failures, CI pipeline green. | 🟢 Completed |
| **M5** | **Business Analysis & Lifecycle Docs** | Weeks 10–11 | Complete BA suite (01-07), PM logs, UAT test suite, DR runbooks. | 🟢 Completed |
| **M6** | **Disaster Recovery Drill & Sign-off** | Week 12 | Execution of full `backup-db.sh` and `restore-db.sh` drill; verifier validation. | 🟢 Completed |
| **M7** | **Legacy Data Cutover & Go-Live** | Week 13 | Legacy CSV data migration, checksum reconciliation, cutover checklist execution. | 🟢 Completed |
| **M8** | **Post-Go-Live Support & Handover** | Week 14 | Operational incident runbook (INC-001 - INC-005), recruiter demo, final portfolio signoff. | 🟢 Completed |

---

## 2. Critical Path Analysis

The critical path for the implementation runs through:
$$\text{Schema DDL} \longrightarrow \text{PL/SQL Packages} \longrightarrow \text{Web API Services} \longrightarrow \text{FEFO & Lot Traceability} \longrightarrow \text{Automated Test Gates} \longrightarrow \text{Data Migration} \longrightarrow \text{Go-Live}$$

A delay in database package compilation directly impacts API endpoints and automated tests. Adhering to strict CI verification (`scripts/run-all-tests.sh`) prevents regressions along the critical path.
